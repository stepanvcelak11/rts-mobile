# rts-mobile — mobile-first RTS (AoE3 mechanics × Supercell UX)

Real-time strategy for touch screens: three resources (Food, Wood, Gold),
base building, four Ages, asymmetric civilizations with Home-City shipments,
tactical real-time combat. Singleplayer skirmish + campaign; multiplayer-ready
(deterministic lockstep).

| Doc | What it covers |
|---|---|
| [docs/01-ARCHITECTURE.md](docs/01-ARCHITECTURE.md) | Engine choice, layers, tick model, determinism, folder structure |
| [docs/02-DATA-SCHEMA.md](docs/02-DATA-SCHEMA.md) | JSON schema for units, buildings, civs, techs, economy, maps |
| [docs/03-CONTROLS-CAMERA.md](docs/03-CONTROLS-CAMERA.md) | Touch gesture scheme, camera rig, HUD layout |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Phases 1–4 and their acceptance criteria |

## Engine
**Unity 6 LTS + URP, C#.** The simulation (`Assets/_Project/Scripts/Sim`) is a
plain .NET assembly with **no UnityEngine reference** — it also compiles as
`sim/RTS.Sim.csproj` for headless servers and fast `dotnet test` runs.

## Status
- [x] Phase 1 — architecture & technical spec
- [x] Phase 2 — deterministic sim core (Fix64, world, commands, replay), map grid +
      footprint validation, camera rig, gestures, economy loop (gather → deposit →
      return), construction & training, HUD, 20 NUnit tests
- [ ] Phase 3 — selection polish, flow-field pathfinding, combat, skirmish AI
- [ ] Phase 4 — 3 civilizations, full mobile HUD (minimap, cards, queues)

## Getting started

### Headless (no Unity needed)
```
dotnet build sim/RTS.Sim.csproj
dotnet test  sim/tests/RTS.Sim.Tests.csproj     # 20 tests, ~1 s
python tools/validate_data.py                   # schema + cross-reference check
python tools/check_deps.py                      # Sim/Data/Net stay engine-free
```

### Unity
1. Open the folder in Unity 6000.0 LTS (Unity Hub → Add). Packages restore from
   `Packages/manifest.json`; accept the prompt to enable the **Input System** backend.
2. Menu **RTS ▸ Create Skirmish Scene** — builds `Scenes/Skirmish.unity`, a URP asset,
   the camera rig, HUD panel settings and wires everything. Re-run any time.
3. Press Play.

Controls in the editor: left-drag pans, wheel zooms, click selects, click a tree /
berries / mine with villagers selected to gather, click the ground to move. On a
device: one-finger drag pans, pinch zooms, tap selects/acts (see docs/03).
Build: select villagers → bottom sheet → House / Mill / Barracks → tap the ground
(green ghost = valid). Select the Town Center → Villager to train.

Every match is recorded to `<persistentDataPath>/replays/*.rts`; the same file
replays to bit-identical `StateHash` values (see `Replay_ReproducesIdenticalHashes`).

## Layout
```
Assets/_Project/
  Resources/Data/     JSON source of truth (economy, ages, units, buildings, civs, techs, maps)
  Scripts/Sim         engine-free deterministic simulation   (RTS.Sim)
  Scripts/Data        definitions + JSON loader              (RTS.Data)
  Scripts/Net         ICommandSource, replay, MatchRunner    (RTS.Net)
  Scripts/Presentation views, interpolation, ground          (RTS.Presentation)
  Scripts/Input       CameraRig, GestureRecognizer, PlayerController
  Scripts/UI          HudController, SkirmishGlue (composition root)
  Scripts/Editor      scene builder, URP setup, data validator
  Tests/EditMode/Sim  NUnit tests (run in Unity Test Runner or via sim/tests)
sim/                  csproj files for dotnet build/test
tools/                validate_data.py, check_deps.py
```
