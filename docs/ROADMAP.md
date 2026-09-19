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

## Phase 3 — RTS combat & AI ✅
1. Smart-tap, double-tap type select, long-press box selection, selection cards.
2. Flow-field pathfinding (integer Dijkstra, LRU cache, per-tick build budget) +
   separation with mover priority and stuck-arrival. No formations yet.
3. Targeting by tags, ranges (min/max), melee/ranged/siege damage types, armor,
   homing projectiles, building turrets, kill XP and statistics.
4. Unit FSM: Idle · Move · AttackMove · Attack · Flee · Gather · ReturnCargo · Build · Dead,
   with leash, resume and retaliation.
5. Skirmish AI (Easy/Normal/Hard) inside the simulation: villager split, housing,
   age-up saving, military buildings, army composition, techs, shipments, waves, defence.
Verified: 10-minute Hard-vs-Hard match is deterministic (same hash twice), 468 hits,
0.75 ms/tick with ~60 units on a laptop. Device benchmark still to run.

## Phase 4 — Factions & mobile polish ✅
1. 3 civilizations (`civs/*.json`): The Crown (redcoats, infantry hp, cheap houses/shipments),
   Iron Compact (uhlans, cavalry damage, cheap stables, wood), Sun Empire (jaguar warriors,
   fast villagers, food). Replacements + modifiers baked per player; techs mutate them live.
   Ages I–IV, 5 techs, 11 Home-City shipments with XP costs.
2. HUD: minimap (tap/drag to jump, camera outline, attack pings), selection cards,
   build ring, training with queue progress and cancel, research, age-up, shipment drawer,
   idle-villager button, radial menu on long press, selection box, under-attack toast,
   match-end panel.
3. Safe-area padding done; haptics and tutorial hints not yet.

## Next (Phase 5 candidates)
- Formations and group speed matching; hierarchical pathfinding for 128×128+ maps.
- Lockstep `LockstepCommandSource` over a relay; desync reports from hash checkpoints.
- Campaign scenario loader (map + scripted triggers), farms/market, garrison.
- Art: prefabs in `ViewCatalog`, animation contract, VFX pools, instanced health bars.
- Haptics, tutorial, settings (left-handed, rotation), device performance lab.
