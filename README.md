# rts-mobile — mobile-first RTS (AoE3 mechanics × Supercell UX)

Real-time strategy for touch screens: three resources (Food, Wood, Gold),
base building, four Ages, asymmetric civilizations with Home-City shipments,
tactical real-time combat. Singleplayer skirmish + campaign; multiplayer-ready
(deterministic lockstep).

| Doc | What it covers |
|---|---|
| [docs/01-ARCHITECTURE.md](docs/01-ARCHITECTURE.md) | Engine choice, layers, tick model, determinism, folder structure |
| [docs/02-DATA-SCHEMA.md](docs/02-DATA-SCHEMA.md) | JSON schema for units, buildings, civs, techs, economy |
| [docs/03-CONTROLS-CAMERA.md](docs/03-CONTROLS-CAMERA.md) | Touch gesture scheme, camera rig, HUD layout |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Phases 1–4 and their acceptance criteria |

## Engine
**Unity 6 LTS + URP, C#.** The simulation (`Assets/_Project/Scripts/Sim`) is a
plain .NET assembly with **no UnityEngine reference** — it also compiles as
`sim/RTS.Sim.csproj` for headless servers and fast `dotnet test` runs.

## Status
- [x] Phase 1 — architecture & technical spec (this repo state)
- [ ] Phase 2 — map grid, camera, resources, building placement, worker loop
- [ ] Phase 3 — selection, pathfinding, combat, FSM, skirmish AI
- [ ] Phase 4 — 3 civilizations, mobile HUD
