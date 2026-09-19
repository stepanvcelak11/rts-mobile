# rts-mobile — mobile-first RTS (AoE3 mechanics × Supercell UX)

Real-time strategy for touch screens: three resources (Food, Wood, Gold),
base building, four Ages, asymmetric civilizations with Home-City shipments,
tactical real-time combat. Singleplayer skirmish + campaign; multiplayer-ready
(deterministic lockstep).

**Play it in the browser: https://stepanvcelak11.github.io/rts-mobile/** (phone or desktop;
the same C# simulation compiled to WebAssembly, see `web/`).

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
      return), construction & training
- [x] Phase 3 — flow-field pathfinding + separation, combat (armor, multipliers,
      projectiles, turrets), unit FSM with attack/attack-move/flee, skirmish AI (3 levels)
- [x] Phase 4 — 3 civilizations with unique units and passives, ages, techs, Home-City
      shipments, full mobile HUD (minimap, cards, queues, radial menu, box select)
- [x] Phase 5 (web) — isometric renderer with procedural sprites, fog of war, generated maps
      (4 types × 4 sizes up to 160×160), 8 civilizations with unique units, age-up choices,
      guarded treasures, lumber/mining camps, farms, market, stances, formations, rally points,
      batch training, objectives, tooltips, day/night, sound, pause/speed, Expert AI
- 46 NUnit tests green, including AI-vs-AI determinism and replay playback

## Getting started

### Headless (no Unity needed)
```
dotnet build sim/RTS.Sim.csproj
dotnet test  sim/tests/RTS.Sim.Tests.csproj     # 37 tests, ~15 s
python tools/gen_data.py                        # regenerate balance JSON from one table
python tools/validate_data.py                   # schema + cross-reference check
python tools/check_deps.py                      # Sim/Data/Net stay engine-free
```

### Web build (what the link above runs)
```
dotnet publish web/RTS.Web/RTS.Web.csproj -c Release -o web-dist
python tools/smoke_web.py            # headless Playwright: boots, plays, checks for JS errors
python tools/play_test.py [URL]      # touch taps like a person; asserts food/wood/gold actually grow
```
`web/RTS.Web` hosts the simulation in Blazor WebAssembly; `wwwroot/game.js` (input + HUD),
`render.js` (isometric renderer, procedural sprites, fog) and `sfx.js` (WebAudio) are the client. Pushes to `main` run the tests and deploy to GitHub Pages
(`.github/workflows/deploy.yml`).

### Unity
1. Open the folder in Unity 6000.0 LTS (Unity Hub → Add). Packages restore from
   `Packages/manifest.json`; accept the prompt to enable the **Input System** backend.
2. Menu **RTS ▸ Create Skirmish Scene** — builds `Scenes/Skirmish.unity`, a URP asset,
   the camera rig, HUD panel settings and wires everything. Re-run any time.
3. Press Play.

Controls in the editor: left-drag pans, wheel zooms, click selects, click again on the
same unit to grab its type nearby, hold + drag for a box selection, hold still for the
radial menu (Move / Attack / Stop). Click a tree / berries / mine with villagers to
gather, an enemy with soldiers to attack, the ground to move. On a device: one-finger
drag pans, pinch zooms, tap/long-press as above (see docs/03).
Build: select villagers → bottom sheet → building → tap the ground (green ghost = valid).
Town Center: train villagers, research, Age up. Top bar: Shipments opens the Home City
deck once you have enough XP. The opponent is a Normal AI by default (GameBootstrap).

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
web/RTS.Web           browser build (Blazor WASM host + canvas renderer)
tools/                gen_data.py, validate_data.py, check_deps.py, smoke_web.py, play_test.py, serve_web.py
```
