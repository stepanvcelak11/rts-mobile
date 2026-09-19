# 03 — Touch controls, camera & HUD layout

Design rules (Supercell-grade): **one thumb can play**; every action ≤ 2
taps; nothing important under the fingers; tap targets ≥ 48 dp; the
world never "jumps" (all camera moves are eased, ≤ 250 ms).

## 1. Gesture table

| Gesture | Condition | Intent | Result |
|---|---|---|---|
| **Tap** (< 200 ms, < 12 dp move) | on own unit | `SelectAtScreen` | selects unit (replaces selection); tap again on same → all of that type on screen |
| Tap | on own building | `SelectAtScreen` | selects building, shows production panel |
| Tap | on enemy / resource, **with** selection | `ContextAction(target)` | smart action: attack / gather / repair / garrison |
| Tap | on ground, **with** selection | `ContextAction(ground)` | move (villagers) / attack-move (military; toggle in settings) |
| Tap | on empty ground, no selection | — | deselect |
| **Double tap** | on own unit | `SelectAllOfType` | selects all same type in view |
| **Long press** (350 ms, haptic tick) then drag | starts on ground | `BoxSelect(rect)` | rubber-band selection of own units |
| Long press (release without drag) | anywhere | `OpenRadial(pos)` | radial contextual menu (see §3) |
| **1-finger drag** | starts on ground, not long-pressed | `Pan` | camera pan, inertia, no sim intent |
| 1-finger drag | starts on a **selected** unit/building | `Pan` | still pan — dragging units is not a thing |
| **2-finger pinch** | — | `Zoom` | camera distance 12–40 m, anchored at pinch midpoint |
| 2-finger twist | ≥ 20° | `Rotate` (off by default) | snap to 45° steps; disabled for readability unless enabled in settings |
| **Minimap tap / drag** | — | `JumpCamera` | moves camera pivot; drag = scrub |
| Minimap tap **with** selection + hold | — | `ContextAction(mapPos)` | attack-move across the map |
| Selection card tap | — | `SubSelect(type)` | narrow selection to that type; drag card up = remove from selection |
| Selection card double tap | — | `CenterOn(type)` | camera to those units |

Disambiguation runs in `GestureRecognizer` as a small state machine:
`Idle → Touching → (Tap | LongPress | Dragging | Pinching)`. Thresholds:
`tapMaxMs=200`, `tapMaxMoveDp=12`, `longPressMs=350`, `pinchStartDp=20`.
Everything is measured in **dp** (density-independent), never pixels.

Command flow: gesture → `Intent` struct → `IntentResolver` (raycast to
ground plane, sim spatial query under finger, ownership check) →
`ICommand` → `ICommandSource.Submit`. The resolver picks the target with a
**fat finger** radius (24 dp) preferring: selected-type unit > enemy unit >
own unit > building > resource > ground.

## 2. Camera rig (`CameraRig.cs`, presentation only)

```
CameraRig (empty, at Pivot on ground plane y=0)
└─ Boom (rotation: pitch 50°, yaw 45°)
   └─ Camera (local pos (0,0,-distance), perspective FOV 30°)
```
- **Pan**: finger delta on screen → ray to ground plane at touch begin and
  now → pivot moves by world delta (so the ground sticks to the finger,
  unlike screen-space panning). Release → velocity carried, damping 6/s.
- **Zoom**: `distance = clamp(distance × pinchRatio⁻¹, 12, 40)`; the world
  point under the pinch midpoint stays under it (adjust pivot). Pitch
  eases from 45° (close) to 58° (far) for readability.
- **Clamp**: after every move, project the 4 frustum corners to the ground
  plane; if the visible rectangle leaves `mapBounds` inflated by 4 cells,
  move pivot back by the overshoot (soft: rubber-band with 0.4 factor while
  a finger is down, hard snap on release).
- **Follow / center**: `CenterOn(worldPos, 250 ms, easeOutCubic)`.
- Rendering: single main camera, minimap = RenderTexture from a top-down
  ortho camera updated at 4 Hz (or a painted texture from sim data —
  chosen in Phase 4 by measured cost).
- Safe area: HUD offsets from `Screen.safeArea`; the camera is unaffected.

## 3. Radial menu (long press)
6 wedges max, populated from the selection's capabilities:
`Move · Attack-move · Stop · Patrol · Gather/Build · Stance`. Villagers on
ground: `Build` opens the build ring (Age-filtered, ≤ 8 buildings; more via
"…"). Wedge size ≥ 64 dp; release on a wedge = execute, release in the
centre = cancel. All wedges have icons + 1-word labels.

## 4. HUD layout (portrait-first, landscape supported)

```
┌──────────────────────────────┐
│ [Food 512][Wood 300][Gold 90]│  top bar: resources, pop 34/50, age icon, ⚙
│ [Minimap]                    │  top-left, 96 dp; tap to expand (200 dp)
│                              │
│         3D world             │
│                              │
│ [Shipments ●3]      [Idle 👷 2]│  right-edge: shipment drawer badge; left: idle villager button
│┌────────────────────────────┐│
││ Selection cards  ▢ ▢ ▢ ▢   ││  bottom sheet, 88 dp, swipe up = details
││ Actions: [Train ×5][Tech]  ││  context actions for the selection
│└────────────────────────────┘│
└──────────────────────────────┘
```
- Bottom sheet is the only persistent UI; it collapses to 40 dp when
  nothing is selected.
- Build queue shows as a progress ring on the building card + ×N badge.
- Notifications ("Under attack!") = top toast with a **tap-to-jump** action
  and a ping on the minimap; never modal.
- Landscape: minimap bottom-left, sheet becomes a right rail (240 dp).

## 5. Input accessibility / settings
- Left-handed mirror (sheet actions and idle button swap sides).
- "Tap on ground = move" vs "attack-move" toggle for military.
- Camera rotation on/off; zoom limits for low-end devices (max distance
  30 m to cap draw calls).
- Haptics on: long-press start, selection, attack order.
