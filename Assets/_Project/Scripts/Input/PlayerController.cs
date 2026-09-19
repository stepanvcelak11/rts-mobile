using System;
using System.Collections.Generic;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;
using UnityEngine;

namespace RTS.Input
{
    /// <summary>What a tap on the ground should do while a mode is armed (attack-move, build).</summary>
    public enum TapMode : byte { Normal, AttackMove, Build }

    /// <summary>
    /// Resolves gestures into simulation commands for the local player: selection (tap, double
    /// tap type-select, long-press box), smart-tap context actions (move / attack / gather /
    /// repair), building placement and the attack-move mode. Reads the world, never writes it —
    /// every change goes through ICommandSource so replays and multiplayer see the same input.
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
        private TapMode _mode = TapMode.Normal;
        private Vector2 _lastPointer;
        private bool _bound;
        private Vector2 _boxStart, _boxEnd;
        private bool _boxActive;

        public IReadOnlyList<int> Selection => _selection;
        public bool InBuildMode => _mode == TapMode.Build;
        public TapMode Mode => _mode;
        public int BuildIndex => _buildIndex;
        public CameraRig Camera => cameraRig;
        public bool BoxActive => _boxActive;
        public Rect BoxScreenRect => RectFrom(_boxStart, _boxEnd);

        public event Action SelectionChanged;
        public event Action<CommandRejectReason> CommandRejected;
        /// <summary>Long press without drag: (screen position, ground position). The HUD shows the radial menu.</summary>
        public event Action<Vector2, Vector3> RadialRequested;
        public event Action ModeChanged;

        public void Bind(IMatchSession session, Material ghostOk, Material ghostBad)
        {
            _session = session;
            _ghost = BuildGhost.Create(transform, ghostOk, ghostBad);
            cameraRig.SetMapBounds(session.World.Map.Width, session.World.Map.Height);

            gestures.Tap += OnTap;
            gestures.LongPress += OnLongPress;
            gestures.LongPressDrag += OnBoxDrag;
            gestures.LongPressDragEnd += OnBoxEnd;
            gestures.PanBegin += _ => cameraRig.BeginPan();
            gestures.Pan += (from, to, dt) => cameraRig.Pan(from, to, dt);
            gestures.PanEnd += cameraRig.EndPan;
            gestures.Pinch += (ratio, center) => cameraRig.Zoom(ratio, center);
            gestures.Scroll += (steps, pos) => cameraRig.ZoomStep(steps, pos);
            _bound = true;

            CenterOnHome(0f);
        }

        public void CenterOnHome(float seconds)
        {
            World w = _session.World;
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind == EntityKind.Building && id.Player == _session.LocalPlayer)
                {
                    Footprint fp = w.Footprints.Get(w.Identities.EntityAt(i));
                    cameraRig.CenterOn(new Vector3(fp.X + fp.W * 0.5f, 0f, fp.Y + fp.H * 0.5f), seconds);
                    return;
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

        /// <summary>Narrows the selection to units of one definition (selection-card tap).</summary>
        public void SubSelect(int defIndex)
        {
            World w = _session.World;
            var keep = new List<int>();
            foreach (int e in _selection)
                if (w.Identities.TryGet(e, out Identity id) && id.Kind == EntityKind.Unit && id.DefIndex == defIndex) keep.Add(e);
            Select(keep);
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

        public int[] SelectedBuilders() => SelectedUnits(e => _session.World.UnitDefOf(e).CanBuild);
        public int[] SelectedGatherers() => SelectedUnits(e => _session.World.Cargos.Has(e));
        public int[] SelectedSoldiers() => SelectedUnits(e => _session.World.UnitDefOf(e).CanAttack && !_session.World.UnitDefOf(e).CanGather);

        /// <summary>Selects own idle villagers one by one (idle-villager button) and returns the picked entity.</summary>
        public int SelectNextIdleVillager()
        {
            World w = _session.World;
            var idle = new List<int>();
            for (int i = 0; i < w.Behaviours.Count; i++)
            {
                int e = w.Behaviours.EntityAt(i);
                Identity id = w.Identities.Get(e);
                if (id.Player != _session.LocalPlayer || !w.Cargos.Has(e)) continue;
                if (w.Behaviours.At(i).State == UnitState.Idle) idle.Add(e);
            }
            if (idle.Count == 0) return 0;
            int current = _selection.Count == 1 ? idle.IndexOf(_selection[0]) : -1;
            int pick = idle[(current + 1) % idle.Count];
            Select(new[] { pick });
            cameraRig.CenterOn(ToWorld(w.Positions.Get(pick).Value), 0.25f);
            return pick;
        }

        public int CountIdleVillagers()
        {
            World w = _session.World;
            int n = 0;
            for (int i = 0; i < w.Behaviours.Count; i++)
            {
                int e = w.Behaviours.EntityAt(i);
                if (w.Identities.Get(e).Player == _session.LocalPlayer && w.Cargos.Has(e) && w.Behaviours.At(i).State == UnitState.Idle) n++;
            }
            return n;
        }

        // ---- gestures -------------------------------------------------------------------------

        private void OnTap(Vector2 screen)
        {
            _lastPointer = screen;
            if (!cameraRig.ScreenToGround(screen, out Vector3 ground)) return;
            World w = _session.World;
            int me = _session.LocalPlayer;
            FixVec2 p = ToSim(ground);

            if (_mode == TapMode.Build) { TryPlaceBuilding(ground); return; }
            if (_mode == TapMode.AttackMove)
            {
                int[] soldiers = SelectedSoldiers();
                if (soldiers.Length > 0) Submit(new AttackMoveCommand(me, soldiers, p));
                SetMode(TapMode.Normal);
                return;
            }

            int picked = w.PickAt(p, FatFingerWorld());
            if (picked != 0 && w.Identities.TryGet(picked, out Identity id))
            {
                if (id.Player == me)
                {
                    if (id.Kind == EntityKind.Building && w.Constructions.Has(picked) && SelectedBuilders().Length > 0)
                    {
                        Submit(new RepairCommand(me, SelectedBuilders(), picked));   // help build
                        return;
                    }
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

                if (World.AreEnemies(me, id.Player) && id.Kind != EntityKind.Projectile)
                {
                    int[] soldiers = SelectedSoldiers();
                    if (soldiers.Length > 0) { Submit(new AttackCommand(me, soldiers, picked)); return; }
                    // Villagers ordered onto an enemy just walk there.
                }
            }

            int[] units = SelectedUnits();
            if (units.Length > 0) Submit(new MoveCommand(me, units, p));
            else ClearSelection();
        }

        private void OnLongPress(Vector2 screen)
        {
            if (_mode != TapMode.Normal) return;
            if (!cameraRig.ScreenToGround(screen, out Vector3 ground)) return;
            _boxStart = _boxEnd = screen;
            RadialRequested?.Invoke(screen, ground);
        }

        private void OnBoxDrag(Vector2 start, Vector2 current)
        {
            if (_mode != TapMode.Normal) return;
            _boxActive = true;
            _boxStart = start;
            _boxEnd = current;
        }

        private void OnBoxEnd(Vector2 start, Vector2 end)
        {
            if (!_boxActive) return;
            _boxActive = false;
            Rect r = RectFrom(start, end);
            if (r.width < 8f && r.height < 8f) return;
            World w = _session.World;
            Camera cam = cameraRig.Camera;
            var hits = new List<int>();
            var soldiers = new List<int>();
            for (int i = 0; i < w.Movers.Count; i++)
            {
                int e = w.Movers.EntityAt(i);
                Identity id = w.Identities.Get(e);
                if (id.Player != _session.LocalPlayer) continue;
                Vector3 sp = cam.WorldToScreenPoint(ToWorld(w.Positions.Get(e).Value));
                if (sp.z < 0f || !r.Contains(new Vector2(sp.x, sp.y))) continue;
                hits.Add(e);
                if (!w.Cargos.Has(e)) soldiers.Add(e);
            }
            // Mixed drag: prefer soldiers so villagers are not dragged into battle.
            Select(soldiers.Count > 0 ? soldiers : hits);
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

        // ---- radial menu actions (called by the HUD with the long-press ground point) -----------

        public void RadialMove(Vector3 ground)
        {
            int[] units = SelectedUnits();
            if (units.Length > 0) Submit(new MoveCommand(_session.LocalPlayer, units, ToSim(ground)));
        }

        public void RadialAttackMove(Vector3 ground)
        {
            int[] soldiers = SelectedSoldiers();
            if (soldiers.Length > 0) Submit(new AttackMoveCommand(_session.LocalPlayer, soldiers, ToSim(ground)));
        }

        public void Stop()
        {
            int[] units = SelectedUnits();
            if (units.Length > 0) Submit(new StopCommand(_session.LocalPlayer, units));
        }

        // ---- modes -------------------------------------------------------------------------------

        public void SetMode(TapMode mode)
        {
            if (_mode == mode) return;
            if (_mode == TapMode.Build) { _buildIndex = -1; _ghost.Hide(); }
            _mode = mode;
            ModeChanged?.Invoke();
        }

        public void BeginBuild(int buildingIndex)
        {
            _buildIndex = buildingIndex;
            _mode = TapMode.Build;
            UpdateGhost();
            ModeChanged?.Invoke();
        }

        public void CancelBuild() => SetMode(TapMode.Normal);

        private void UpdateGhost()
        {
            Vector2 pointer = CurrentPointer();
            if (!cameraRig.ScreenToGround(pointer, out Vector3 ground)) { _ghost.Hide(); return; }
            BakedBuilding b = _session.World.DefsOf(_session.LocalPlayer).Buildings[_buildIndex];
            SimToOrigin(ground, b.W, b.H, out int x, out int y);
            PlacementResult r = BuildCommand.Validate(_session.World, _session.LocalPlayer, _buildIndex, x, y);
            _ghost.Show(x, y, b.W, b.H, r);
        }

        private void TryPlaceBuilding(Vector3 ground)
        {
            BakedBuilding b = _session.World.DefsOf(_session.LocalPlayer).Buildings[_buildIndex];
            SimToOrigin(ground, b.W, b.H, out int x, out int y);
            PlacementResult r = BuildCommand.Validate(_session.World, _session.LocalPlayer, _buildIndex, x, y);
            if (r != PlacementResult.Ok)
            {
                CommandRejected?.Invoke(r == PlacementResult.NotAffordable ? CommandRejectReason.NotAffordable
                                      : r == PlacementResult.LimitReached ? CommandRejectReason.LimitReached
                                      : r == PlacementResult.WrongAge ? CommandRejectReason.WrongAge
                                      : CommandRejectReason.BadPlacement);
                return;
            }
            Submit(new BuildCommand(_session.LocalPlayer, _buildIndex, x, y, SelectedBuilders()));
            SetMode(TapMode.Normal);
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

        private static Rect RectFrom(Vector2 a, Vector2 b) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        public static FixVec2 ToSim(Vector3 ground) =>
            new FixVec2(Fix64.FromDecimal((decimal)ground.x), Fix64.FromDecimal((decimal)ground.z));

        public static Vector3 ToWorld(FixVec2 p) => new Vector3(p.X.ToFloat(), 0f, p.Y.ToFloat());

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
