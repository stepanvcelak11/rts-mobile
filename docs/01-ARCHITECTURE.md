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

A CI check (`tools/check_deps.py`) fails the build if `Sim`, `Data` or `Net`
reference UnityEngine, or if `Sim` uses float/double outside lines marked
`presentation-only` (the Fix64 → float conversions used by views).

## 3. Simulation (`RTS.Sim`)

### 3.1 Determinism contract
- **No floats.** All gameplay math uses `Fix64` (Q32.32 in a `long`) and
  `FixVec2`. Trig via lookup tables. Sqrt via integer Newton iteration.
- **No `System.Random`, no `DateTime`, no dictionary iteration order** inside
  the tick. `DetRandom` (xorshift128) is part of world state.
- **No engine callbacks.** The sim advances only through
  `World.Step(TickCommands)`.
- Every tick ends with `World.ComputeHash()` (FNV-1a over players, map
  occupancy and every component store, in fixed order). Replays store a
  checkpoint every 20 ticks; peers will compare the same value.
- Entities are `int` ids; components live in `ComponentStore<T>` (struct
  arrays indexed by entity, dense + sparse). Iteration order = id order.

### 3.2 Tick model
- `TICK_RATE = 20` (50 ms). Presentation runs at display rate and
  interpolates between the last two sim snapshots (`alpha = accumulator/dt`).
- Commands issued at tick *T* execute at *T + inputDelay*
  (local: 1 tick, lockstep MP: 3–6 ticks, adaptive).
- `World.Step` runs in a **fixed order** (as built):
  1. Commands of this tick (sorted by player), then the AI commands queued last tick.
  2. `UnitGrid.Rebuild` — spatial buckets for the tick.
  3. `AISystem` — sim-side skirmish AI queues commands for the *next* tick (so it is
     deterministic across peers; AI runs on every machine identically).
  4. `ProductionSystem` — construction, training queues, research, age-up, shipment availability.
  5. `EconomySystem` — gather/deposit (+ Home-City XP).
  6. `BehaviorSystem` — unit FSM transitions and movement goals.
  7. `MovementSystem` — flow-field following, separation, collision, stuck-arrival.
  8. `CombatSystem` — cooldowns, firing, turrets, projectiles, damage, kills.
  9. `DeathSystem` — despawn dead units.
  10. `VictorySystem` — defeat/victory once per second.
  11. Despawn flush, tick++, `ComputeHash`.

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
`BehaviorSystem` switches on `UnitBehaviour.State` with states
`Idle · Move · AttackMove · Gather · ReturnCargo · Build · Attack · Flee · Dead`.
Transitions are data-driven per unit class (`behaviour` block in the unit
JSON: `canGather`, `canBuild`, `aggro`, `leashRange`). State is a plain
`UnitBehaviour` component (enum + a few ints) — no per-unit objects, so the
FSM is allocation-free.

## 4. Data (`RTS.Data`)
- **JSON is the source of truth** (`Assets/_Project/Resources/Data/**.json`), loaded
  at boot into an immutable `GameData` registry (`Dictionary<string, UnitDef>`
  etc.). IDs are string keys (`"unit.musketeer"`), resolved to ints once at
  load for hot paths.
- Editor importer generates read-only ScriptableObject mirrors for
  inspector convenience and validates references (`tools/validate_data.py`
  does the same in CI without Unity).
- Civ modifiers are applied at match start: every player gets
  `BakedDefs.CloneForPlayer()` with `Modifiers.ApplyCiv` on it; researched techs and
  shipments keep mutating that clone (`World.DefsOf(player)`), so the per-tick
  systems only ever read plain numbers.
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

## 7. Folder structure (as built in Phase 2)

```
rts-mobile/
├─ README.md
├─ docs/                          spec (this folder)
├─ sim/
│  ├─ RTS.Sim.csproj              headless build of Scripts/Data + Sim + Net (netstandard2.1)
│  └─ tests/RTS.Sim.Tests.csproj  NUnit runner over Assets/_Project/Tests/EditMode
├─ tools/                         validate_data.py, check_deps.py
├─ Packages/manifest.json
├─ ProjectSettings/
└─ Assets/
   ├─ Plugins/                    third-party
   └─ _Project/                   everything we own (underscore = sorts first)
      ├─ Resources/Data/          JSON source of truth (TextAssets on device)
      │  ├─ economy.json  ages.json
      │  ├─ units/*.json  buildings/*.json  civs/*.json  techs/*.json  maps/*.json
      ├─ Scenes/                  Skirmish.unity (generated by RTS ▸ Create Skirmish Scene)
      ├─ Scripts/
      │  ├─ Sim/        RTS.Sim.asmdef       (NO UnityEngine)      namespace RTS.Sim.*
      │  │  ├─ Core/     Fix64, FixVec2, FixMath, DetRandom, Hasher, SimConstants
      │  │  ├─ World/    World, ComponentStore, Components, Baked (per-player defs), Modifiers, PlayerState, SimEvent, ISystem
      │  │  ├─ Map/      GridMap, FlowField + FlowFieldCache, UnitGrid (spatial buckets)
      │  │  ├─ Commands/ ICommand, TickCommands, Move/Stop/Gather/Build/Train, CommandCodec
      │  │  ├─ Economy/  EconomySystem (gather/deposit), ProductionSystem (construction, queues)
      │  │  ├─ Units/    BehaviorSystem (FSM), MovementSystem
      │  │  ├─ Combat/   CombatSystem (damage, projectiles, turrets), DeathSystem, VictorySystem
      │  │  └─ AI/       AISystem (rule-based skirmish opponent, 3 difficulties)
      │  ├─ Data/        RTS.Data.asmdef      Defs (POCO, decimal), GameData, JsonLoader, IDataSource
      │  ├─ Net/         RTS.Net.asmdef       ICommandSource, LocalCommandSource, Replay*, MatchRunner, IMatchSession
      │  ├─ Presentation/ RTS.Presentation.asmdef  GameBootstrap, WorldView, EntityView, GroundView, ViewCatalog
      │  ├─ Input/       RTS.Input.asmdef     CameraRig, GestureRecognizer, PlayerController, BuildGhost
      │  ├─ UI/          RTS.UI.asmdef        HudController, SkirmishGlue (composition root)
      │  └─ Editor/      RTS.Editor.asmdef    SkirmishSceneBuilder (+URP setup), DataValidatorMenu
      ├─ Tests/EditMode/Sim       NUnit: Fix64, map/placement, economy, replay determinism
      ├─ Art/  Prefabs/  Materials/  VFX/  Audio/  UI/
      └─ Settings/                 generated: URP.asset, ViewCatalog, HudPanel, Ground.mat
```

Naming: `PascalCase` types, one type per file, systems end in `System`,
components are `struct`s and live in `Components.cs`. JSON ids use `unit.`
`bld.` `civ.` `tech.` `res.` `map.` prefixes. The `RTS.Sim.Model` namespace
holds the state (the class is `World`); `RTS.Sim.Systems` holds the systems.

## 8. Testing strategy
- `RTS.Sim.Tests` (NUnit, runs with `dotnet test sim/` in < 5 s):
  Fix64 math, footprint validation, flow field correctness, gather loop,
  **replay determinism** (run the same command log twice, compare hashes).
- PlayMode smoke: Skirmish scene boots, 60 s AI vs AI without exceptions.
- Device lab: automated 5-minute skirmish on 3 phones with frame-time log.
