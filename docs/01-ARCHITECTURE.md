# 01 — Architecture & technical spec

## 1. Engine decision: Unity 6 LTS + URP (C#)

| Criterion | Unity 6 + URP | Godot 4 (C#) |
|---|---|---|
| iOS/Android export maturity | Production-grade, years of RTS/Supercell-style titles shipped | Good on Android; C# on iOS still young (no hot reload, larger binaries) |
| Multi-unit performance | Burst + Jobs + Collections for flow fields / avoidance; GPU instancing & SRP Batcher out of the box | GDExtension needed for SIMD-class work; instancing via MultiMesh only |
| Mobile profiling | Profiler, Memory Profiler, Frame Debugger, Adaptive Performance (thermal throttling API) | Basic profiler, no thermal API |
| Stylized low-poly pipeline | Shader Graph, URP Toon/Decal, LOD, Addressables, huge low-poly asset ecosystem | Good, smaller ecosystem |
| Networking | Netcode/Transport packages, easy custom UDP | ENet built-in (fine) |
| Cost / licence | Runtime fee cancelled (2024); free under $200k revenue | Free, MIT |

**Decision: Unity.** The one place Godot wins (licence) does not outweigh
mobile maturity and Burst for the pathfinding budget. The **simulation is
engine-agnostic** anyway (section 3), so an engine swap later would only
touch Presentation/Input/UI.

Project settings baseline:
- Unity 6000.x LTS, URP, **IL2CPP**, ARM64 only, .NET Standard 2.1 API level.
- Input System package (`EnhancedTouch`), UI Toolkit for HUD, Addressables,
  Newtonsoft JSON (`com.unity.nuget.newtonsoft-json`), Burst, Collections.
- Target: 60 fps on Snapdragon 7-class / A14, 30 fps floor on 2020 mid-tier.

## 2. Layer diagram

```
┌───────────────────────────────────────────────────────────────────┐
│  UI (UI Toolkit)        Input (gestures, camera rig)               │
│      │ reads                  │ produces Intents                   │
│      ▼                        ▼                                    │
│  Presentation ◄──── snapshot ────  Sim  ◄──── ICommandSource ──── Net│
│  (views, interp,                 (pure C#,    Local / Lockstep /   │
│   anim, VFX, audio)              deterministic) Replay / AI        │
│                                     ▲                              │
│                                     │ definitions                  │
│                                   Data (JSON → POCO registry)      │
└───────────────────────────────────────────────────────────────────┘
```

Assembly definitions (asmdef) enforce the direction of dependencies:

| Assembly | May reference | Never references |
|---|---|---|
| `RTS.Data` | — | UnityEngine |
| `RTS.Sim` | `RTS.Data` | UnityEngine, `RTS.Presentation` |
| `RTS.Net` | `RTS.Sim` | Presentation |
| `RTS.Presentation` | `RTS.Sim`, `RTS.Data`, UnityEngine | Input, UI |
| `RTS.Input` | `RTS.Sim` (commands), UnityEngine | Presentation internals |
| `RTS.UI` | `RTS.Sim` (read), `RTS.Input` (intents), UnityEngine | — |
| `RTS.Editor` | everything | (editor only) |

A CI check (`tools/check_deps.py`) fails the build if `Sim` or `Data`
contain the string `using UnityEngine`.

## 3. Simulation (`RTS.Sim`)

### 3.1 Determinism contract
- **No floats.** All gameplay math uses `Fix64` (Q32.32 in a `long`) and
  `FixVec2`. Trig via lookup tables. Sqrt via integer Newton iteration.
- **No `System.Random`, no `DateTime`, no dictionary iteration order** inside
  the tick. `DetRandom` (xorshift128) is part of world state.
- **No engine callbacks.** The sim advances only through
  `World.Step(TickCommands)`.
- Every tick ends with `StateHash.Compute(world)` (FNV-1a over component
  arrays). Peers compare hashes every N ticks → desync detection.
- Entities are `int` ids; components live in `ComponentStore<T>` (struct
  arrays indexed by entity, dense + sparse). Iteration order = id order.

### 3.2 Tick model
- `TICK_RATE = 20` (50 ms). Presentation runs at display rate and
  interpolates between the last two sim snapshots (`alpha = accumulator/dt`).
- Commands issued at tick *T* execute at *T + inputDelay*
  (local: 1 tick, lockstep MP: 3–6 ticks, adaptive).
- `World.Step` runs systems in a **fixed order**:
  1. `CommandSystem` — applies this tick's commands (sorted by playerId, seq).
  2. `AISystem` — sim-side skirmish AI emits commands for *next* tick (so it is
     deterministic across peers; AI runs on every machine identically).
  3. `ProductionSystem` — build queues, research, construction progress.
  4. `EconomySystem` — gather/deposit, trickles, upkeep.
  5. `BehaviorSystem` — unit FSM transitions.
  6. `PathSystem` — flow-field requests, steering, local avoidance.
  7. `MovementSystem` — integrate positions, grid occupancy.
  8. `CombatSystem` — targeting, cooldowns, projectiles, damage.
  9. `DeathSystem` — cleanup, corpses, refund on cancel.
  10. `VictorySystem` — win/lose conditions.
  11. `HashSystem`.

### 3.3 Multiplayer readiness
`ICommandSource` is the only seam:

```csharp
public interface ICommandSource {
    // Returns commands scheduled for `tick`, or null if not yet available
    // (lockstep: wait for all peers). The game loop stalls on null.
    TickCommands Poll(int tick);
    void Submit(ICommand cmd);   // local intent → scheduled for tick + delay
}
```
Implementations: `LocalCommandSource` (SP), `ReplayCommandSource` (file),
`LockstepCommandSource` (relay server, later). Server-authoritative mode is
possible with the same sim: run `World` headless (`sim/RTS.Sim.csproj`) and
send snapshots — but lockstep is the default because bandwidth on mobile
is the scarcer resource.

### 3.4 Map & pathfinding
- `GridMap`: `width × height` cells, 1 cell = 1 m. Per cell: terrain type
  (bits), passable flag, occupancy (building id or 0), resource node id,
  elevation step (0–3) for cliffs.
- Buildings occupy rectangular `Footprint`s, snapped to the grid;
  validation = all cells passable ∧ unoccupied ∧ same elevation ∧ not
  within `minDistanceToEnemy` (for towers/forward bases).
- Pathfinding = **flow fields** per (destination cell, unit size class),
  cached in an LRU (64 fields). Groups sharing a destination share a field
  → 200 units cost the same as 1. Integration field via Dijkstra on the
  grid with cost = terrain multiplier. The field is computed **inside the
  tick** (integer Dijkstra, deterministic) with a per-tick budget of cells;
  a field is marked ready after a fixed number of ticks, never after a
  wall-clock time, so every peer sees it ready on the same tick. Local
  avoidance: separation + priority (moving vs idle) on fixed-point vectors;
  no RVO in v1.
- Distant navigation on big maps (later): hierarchical 16×16 sectors +
  portals, A* on the sector graph, flow fields only inside sectors.

### 3.5 Unit behaviour FSM
`IUnitState { Enter(ctx); Tick(ctx); Exit(ctx); }` with states
`Idle · Move · AttackMove · Gather · ReturnCargo · Build · Attack · Flee · Dead`.
Transitions are data-driven per unit class (`behaviour` block in the unit
JSON: `canGather`, `canBuild`, `aggro`, `leashRange`). State is a plain
`UnitBehaviour` component (enum + a few ints) — no per-unit objects, so the
FSM is allocation-free.

## 4. Data (`RTS.Data`)
- **JSON is the source of truth** (`Assets/_Project/Data/**.json`), loaded
  at boot into an immutable `GameData` registry (`Dictionary<string, UnitDef>`
  etc.). IDs are string keys (`"unit.musketeer"`), resolved to ints once at
  load for hot paths.
- Editor importer generates read-only ScriptableObject mirrors for
  inspector convenience and validates references (`tools/validate_data.py`
  does the same in CI without Unity).
- Civ modifiers are applied at load: `GameData.ForCiv(civId)` returns a
  baked, per-civ copy of definitions (so the sim never evaluates modifiers
  per tick).
- Schema: [02-DATA-SCHEMA.md](02-DATA-SCHEMA.md).

## 5. Presentation (`RTS.Presentation`)
- `WorldView` subscribes to `World.Events` (spawn, despawn, state change,
  damage, projectile) and keeps a `Dictionary<int, EntityView>`.
- `EntityView` = pooled prefab; position/rotation = lerp(prev, cur, alpha),
  Fix64 → float conversion happens **only here**.
- Animation via Animator with a 4-parameter contract (`Speed`, `Attack`,
  `Gather`, `Die`); VFX via a pooled `VfxSystem`; audio via `AudioSystem`
  with voice limits (max 8 simultaneous combat sounds).
- Selection outline and health bars are one instanced mesh each
  (`SelectionRenderer`, `HealthBarRenderer`) — no per-unit UI objects.
- Quality tiers (Settings/): `Low` (no shadows, 0.75 render scale, 30 fps
  cap) · `Mid` · `High`; chosen by `SystemInfo` heuristics + Adaptive
  Performance thermal state.

## 6. Input & camera (`RTS.Input`) — see 03-CONTROLS-CAMERA.md

Gestures → `Intent` (typed struct, e.g. `SelectAtScreen`, `BoxSelect`,
`ContextAction`) → `IntentResolver` (needs sim read access: what is under
the finger?) → `ICommand` submitted to `ICommandSource`. The camera rig is
pure presentation and never touches the sim.

## 7. Folder structure

```
rts-mobile/
├─ README.md
├─ docs/                          spec (this folder)
├─ sim/RTS.Sim.csproj             headless build of Assets/_Project/Scripts/Sim + Data
├─ tools/                         validate_data.py, check_deps.py, balance export
├─ Tests/
│  ├─ EditMode/                   Unity-side edit tests
│  └─ PlayMode/                   scene smoke tests
├─ Packages/manifest.json
├─ ProjectSettings/
└─ Assets/
   ├─ Plugins/                    third-party
   └─ _Project/                   everything we own (underscore = sorts first)
      ├─ Data/                    JSON source of truth
      │  ├─ economy.json  ages.json
      │  ├─ units/*.json  buildings/*.json  civs/*.json  techs/*.json
      ├─ Scenes/                  Boot · MainMenu · Skirmish · CampaignXX
      ├─ Scripts/
      │  ├─ Sim/        RTS.Sim.asmdef       (NO UnityEngine)
      │  │  ├─ Core/     Fix64, FixVec2, FixMath, DetRandom, StateHash, TickClock
      │  │  ├─ World/    World, Entity, ComponentStore, ISystem, Events
      │  │  ├─ Map/      GridMap, Cell, Footprint, FlowField, FlowFieldCache, Steering
      │  │  ├─ Commands/ ICommand, TickCommands, Move/Gather/Build/Train/Attack…
      │  │  ├─ Economy/  Stockpile, ResourceNode, EconomySystem, ProductionSystem
      │  │  ├─ Units/    UnitBehaviour, states/*, BehaviorSystem
      │  │  ├─ Combat/   Targeting, Damage, Projectiles, CombatSystem
      │  │  └─ AI/       SkirmishAI, BuildOrder, WaveScheduler
      │  ├─ Data/        RTS.Data.asmdef      Defs (POCO), JsonLoader, GameData, CivBaker
      │  ├─ Presentation/ RTS.Presentation.asmdef  WorldView, EntityView, pools, VFX, audio
      │  ├─ Input/       RTS.Input.asmdef     GestureRecognizer, Intents, IntentResolver, CameraRig
      │  ├─ UI/          RTS.UI.asmdef        HUD (UXML/USS), Minimap, SelectionPanel, BuildMenu
      │  ├─ Net/         RTS.Net.asmdef       ICommandSource impls, Replay, Lockstep
      │  └─ Editor/      importers, validators, map painter
      ├─ Art/  Prefabs/  Materials/  VFX/  Audio/
      ├─ UI/                       *.uxml, *.uss, sprites
      └─ Settings/                 URP assets, quality tiers, input actions
```

Naming: `PascalCase` types, one type per file, systems end in `System`,
components are `struct`s and live in `Components.cs` per domain. JSON ids
use `unit.` `bld.` `civ.` `tech.` `res.` prefixes.

## 8. Testing strategy
- `RTS.Sim.Tests` (NUnit, runs with `dotnet test sim/` in < 5 s):
  Fix64 math, footprint validation, flow field correctness, gather loop,
  **replay determinism** (run the same command log twice, compare hashes).
- PlayMode smoke: Skirmish scene boots, 60 s AI vs AI without exceptions.
- Device lab: automated 5-minute skirmish on 3 phones with frame-time log.
