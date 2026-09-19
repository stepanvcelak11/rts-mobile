using System;
using System.Collections.Generic;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;
using UnityEngine;

namespace RTS.Input
{
    /// <summary>
    /// Resolves gestures into simulation commands for the local player: selection, smart-tap
    /// context actions and building placement. Reads the world, never writes it — every change
    /// goes through ICommandSource so replays and multiplayer see exactly the same input.
    /// </summary>
    public sealed class PlayerController : MonoBehaviour
    {
        [SerializeField] private CameraRig cameraRig;
        [SerializeField] private GestureRecognizer gestures;
        [SerializeField] private float fatFingerDp = 24f;

        private IMatchSession _session;
        private BuildGhost _ghost;
        private readonly List<int> _selection = new List<int>();
        private int _buildIndex = -1;
        private Vector2 _lastPointer;
        private bool _bound;

        public IReadOnlyList<int> Selection => _selection;
        public bool InBuildMode => _buildIndex >= 0;
        public int BuildIndex => _buildIndex;
        public CameraRig Camera => cameraRig;

        public event Action SelectionChanged;
        public event Action<CommandRejectReason> CommandRejected;

        public void Bind(IMatchSession session, Material ghostOk, Material ghostBad)
        {
            _session = session;
            _ghost = BuildGhost.Create(transform, ghostOk, ghostBad);
            cameraRig.SetMapBounds(session.World.Map.Width, session.World.Map.Height);

            gestures.Tap += OnTap;
            gestures.PanBegin += _ => cameraRig.BeginPan();
            gestures.Pan += (from, to, dt) => cameraRig.Pan(from, to, dt);
            gestures.PanEnd += cameraRig.EndPan;
            gestures.Pinch += (ratio, center) => cameraRig.Zoom(ratio, center);
            gestures.Scroll += (steps, pos) => cameraRig.ZoomStep(steps, pos);
            _bound = true;

            // Start looking at our town center.
            World w = session.World;
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind == EntityKind.Building && id.Player == session.LocalPlayer)
                {
                    Footprint fp = w.Footprints.Get(w.Identities.EntityAt(i));
                    cameraRig.CenterOn(new Vector3(fp.X + fp.W * 0.5f, 0f, fp.Y + fp.H * 0.5f), 0f);
                    break;
                }
            }
        }

        private void Update()
        {
            if (!_bound) return;
            PruneSelection();
            if (InBuildMode) UpdateGhost();
        }

        // ---- selection ----------------------------------------------------------------------

        private void PruneSelection()
        {
            bool changed = false;
            for (int i = _selection.Count - 1; i >= 0; i--)
                if (!_session.World.IsAlive(_selection[i])) { _selection.RemoveAt(i); changed = true; }
            if (changed) SelectionChanged?.Invoke();
        }

        public void Select(IEnumerable<int> entities)
        {
            _selection.Clear();
            foreach (int e in entities) _selection.Add(e);
            SelectionChanged?.Invoke();
        }

        public void ClearSelection()
        {
            if (_selection.Count == 0) return;
            _selection.Clear();
            SelectionChanged?.Invoke();
        }

        public int[] SelectedUnits(Func<int, bool> filter = null)
        {
            var list = new List<int>();
            World w = _session.World;
            foreach (int e in _selection)
                if (w.Identities.TryGet(e, out Identity id) && id.Kind == EntityKind.Unit && (filter == null || filter(e))) list.Add(e);
            return list.ToArray();
        }

        public int SelectedBuilding()
        {
            World w = _session.World;
            foreach (int e in _selection)
                if (w.Identities.TryGet(e, out Identity id) && id.Kind == EntityKind.Building) return e;
            return 0;
        }

        public int[] SelectedBuilders() => SelectedUnits(e => _session.World.Defs.Units[_session.World.Identities.Get(e).DefIndex].CanBuild);
        public int[] SelectedGatherers() => SelectedUnits(e => _session.World.Cargos.Has(e));

        // ---- gestures -------------------------------------------------------------------------

        private void OnTap(Vector2 screen)
        {
            _lastPointer = screen;
            if (!cameraRig.ScreenToGround(screen, out Vector3 ground)) return;
            World w = _session.World;
            int me = _session.LocalPlayer;

            if (InBuildMode)
            {
                TryPlaceBuilding(ground);
                return;
            }

            FixVec2 p = SimToSimPoint(ground);
            int picked = w.PickAt(p, FatFingerWorld());

            if (picked != 0 && w.Identities.TryGet(picked, out Identity id))
            {
                if (id.Player == me)
                {
                    // Own unit / building: select. Tapping the sole selected unit again selects its type nearby.
                    if (id.Kind == EntityKind.Unit && _selection.Count == 1 && _selection[0] == picked)
                        Select(SameTypeNearby(picked));
                    else
                        Select(new[] { picked });
                    return;
                }

                if (id.Kind == EntityKind.ResourceNode)
                {
                    int[] gatherers = SelectedGatherers();
                    if (gatherers.Length > 0) { Submit(new GatherCommand(me, gatherers, picked)); return; }
                    ClearSelection();
                    return;
                }
                // Enemy entities: attack orders arrive in Phase 3. Fall through to a move.
            }

            int[] units = SelectedUnits();
            if (units.Length > 0) Submit(new MoveCommand(me, units, p));
            else ClearSelection();
        }

        private IEnumerable<int> SameTypeNearby(int unit)
        {
            World w = _session.World;
            Identity me = w.Identities.Get(unit);
            FixVec2 at = w.Positions.Get(unit).Value;
            Fix64 radius = Fix64.FromInt(15);
            for (int i = 0; i < w.Identities.Count; i++)
            {
                int e = w.Identities.EntityAt(i);
                Identity id = w.Identities.At(i);   // copy: iterators cannot hold ref locals
                if (id.Kind != EntityKind.Unit || id.Player != me.Player || id.DefIndex != me.DefIndex) continue;
                if (FixMath.WithinDistance(w.Positions.Get(e).Value, at, radius)) yield return e;
            }
        }

        // ---- building placement ----------------------------------------------------------------

        public void BeginBuild(int buildingIndex)
        {
            _buildIndex = buildingIndex;
            UpdateGhost();
        }

        public void CancelBuild()
        {
            _buildIndex = -1;
            _ghost.Hide();
        }

        private void UpdateGhost()
        {
            Vector2 pointer = CurrentPointer();
            if (!cameraRig.ScreenToGround(pointer, out Vector3 ground)) { _ghost.Hide(); return; }
            BakedBuilding b = _session.World.Defs.Buildings[_buildIndex];
            SimToOrigin(ground, b.W, b.H, out int x, out int y);
            PlacementResult r = BuildCommand.Validate(_session.World, _session.LocalPlayer, _buildIndex, x, y);
            _ghost.Show(x, y, b.W, b.H, r);
        }

        private void TryPlaceBuilding(Vector3 ground)
        {
            BakedBuilding b = _session.World.Defs.Buildings[_buildIndex];
            SimToOrigin(ground, b.W, b.H, out int x, out int y);
            PlacementResult r = BuildCommand.Validate(_session.World, _session.LocalPlayer, _buildIndex, x, y);
            if (r != PlacementResult.Ok)
            {
                CommandRejected?.Invoke(r == PlacementResult.NotAffordable ? CommandRejectReason.NotAffordable
                                      : r == PlacementResult.LimitReached ? CommandRejectReason.LimitReached
                                      : CommandRejectReason.BadPlacement);
                return;
            }
            Submit(new BuildCommand(_session.LocalPlayer, _buildIndex, x, y, SelectedBuilders()));
            CancelBuild();
        }

        // ---- helpers ----------------------------------------------------------------------------

        public void Submit(ICommand command) => _session.Source.Submit(command);

        private Vector2 CurrentPointer()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches.Count > 0)
                return UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches[0].screenPosition;
            if (mouse != null) return mouse.position.ReadValue();
            return _lastPointer;
        }

        private static FixVec2 SimToSimPoint(Vector3 ground) =>
            new FixVec2(Fix64.FromDecimal((decimal)ground.x), Fix64.FromDecimal((decimal)ground.z));

        private static void SimToOrigin(Vector3 ground, int w, int h, out int x, out int y)
        {
            x = Mathf.FloorToInt(ground.x - w * 0.5f + 0.5f);
            y = Mathf.FloorToInt(ground.z - h * 0.5f + 0.5f);
        }

        /// <summary>Fat-finger radius in world units: 24 dp projected at the current camera distance.</summary>
        private Fix64 FatFingerWorld()
        {
            float dpToPx = (Screen.dpi > 0f ? Screen.dpi : 160f) / 160f;
            float px = fatFingerDp * dpToPx;
            Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (cameraRig.ScreenToGround(c, out Vector3 a) && cameraRig.ScreenToGround(c + new Vector2(px, 0f), out Vector3 b))
                return Fix64.FromDecimal((decimal)Mathf.Clamp(Vector3.Distance(a, b), 0.3f, 2f));
            return Fix64.Half;
        }
    }
}
