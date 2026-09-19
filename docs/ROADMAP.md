# Roadmap

## Phase 1 — Architecture & technical spec ✅
Deliverables: engine decision, layer diagram, folder structure, data schema,
control scheme. Acceptance: a new engineer can place a file without asking.

## Phase 2 — Core foundation & MVP ✅
1. `GridMap` (terrain, occupancy), footprint validation with every failure reason.
2. Camera rig: ground-anchored pan with inertia, anchored pinch zoom, rubber-band clamp.
3. Stockpile + `EconomySystem`: gather → deposit → return, node depletion + re-target.
4. Building placement: ghost preview, `BuildCommand.Validate`, construction with
   builder scaling, training queues with population gate.
5. Deterministic tick loop (`World.Step`), `ICommandSource`, replay recorder/player.
Verified: `dotnet test` — 20 tests incl. `Replay_ReproducesIdenticalHashes`
(60 s scripted match, every tick's hash identical on playback).
Not yet: pathfinding (units walk straight lines and slide along obstacles),
attack orders, cancel/refund, civ modifiers (`CivBaker`).

## Phase 3 — RTS combat & AI
1. Smart-tap / box selection, control groups.
2. Flow-field pathfinding + local avoidance (RVO-lite), formations.
3. Targeting, attack ranges, damage types, armor, projectiles.
4. Unit FSM: Idle · Move · Gather · Build · Attack · Flee · Dead.
5. Skirmish AI: build-order scripts + wave timer, 3 difficulties.
Acceptance: 200 units path across a 128×128 map at ≥55 fps on a
Snapdragon 7-class device.

## Phase 4 — Factions & mobile polish
1. 3 civilizations (see `Data/civs`), unique units, passive modifiers,
   Home-City shipments.
2. HUD: minimap, selection cards, build queue, shipment drawer, radial menu.
3. Safe-area, haptics, tutorial hints.
