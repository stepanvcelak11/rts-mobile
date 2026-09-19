# Roadmap

## Phase 1 — Architecture & technical spec ✅
Deliverables: engine decision, layer diagram, folder structure, data schema,
control scheme. Acceptance: a new engineer can place a file without asking.

## Phase 2 — Core foundation & MVP
1. `GridMap` (cells, terrain flags, passability), `Footprint` validation.
2. Camera rig: pan (inertia), pinch zoom, clamp to map bounds.
3. Resource stockpile + `GatherSystem` (gather → deposit → return).
4. Building placement: ghost preview, footprint validation, construction progress.
5. Deterministic tick loop with `CommandBuffer`, replay file.
Acceptance: place a Town Center, 3 villagers cycle Food/Wood/Gold, replay
of 5 minutes reproduces the same `StateHash` on a second run.

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
