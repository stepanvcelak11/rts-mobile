using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Web;

public enum TapMode : byte { Normal, AttackMove, Build, Rally }

/// <summary>
/// Browser twin of the Unity PlayerController: turns taps, long presses and box drags (already
/// converted to map coordinates by game.js) into simulation commands for the local player.
/// Reads the world, never writes it — every change goes through ICommandSource.
/// </summary>
public sealed class WebController
{
    private readonly IMatchSession _session;
    private readonly List<int> _selection = new();
    private int _buildIndex = -1;
    private TapMode _mode = TapMode.Normal;
    private FixVec2 _ghostPointer;
    private bool _ghostValid;
    private int _ghostX, _ghostY;

    public IReadOnlyList<int> Selection => _selection;
    public TapMode Mode => _mode;
    public int BuildIndex => _buildIndex;
    public bool GhostVisible => _mode == TapMode.Build;
    public int GhostX => _ghostX;
    public int GhostY => _ghostY;
    public bool GhostValid => _ghostValid;
    public string LastRejection { get; set; }
    public FixVec2 RadialPoint { get; private set; }

    private World W => _session.World;
    private int Me => _session.LocalPlayer;

    public WebController(IMatchSession session) { _session = session; }

    // ---- selection -----------------------------------------------------------------------

    public void Prune()
    {
        for (int i = _selection.Count - 1; i >= 0; i--)
            if (!W.IsAlive(_selection[i])) _selection.RemoveAt(i);
    }

    public bool IsSelected(int e) => _selection.Contains(e);

    public void Select(IEnumerable<int> entities)
    {
        _selection.Clear();
        _selection.AddRange(entities);
    }

    public void SubSelect(int defIndex)
    {
        var keep = new List<int>();
        foreach (int e in _selection)
            if (W.Identities.TryGet(e, out Identity id) && id.Kind == EntityKind.Unit && id.DefIndex == defIndex) keep.Add(e);
        Select(keep);
    }

    public int[] SelectedUnits(Func<int, bool> filter = null)
    {
        var list = new List<int>();
        foreach (int e in _selection)
            if (W.Identities.TryGet(e, out Identity id) && id.Kind == EntityKind.Unit && (filter == null || filter(e))) list.Add(e);
        return list.ToArray();
    }

    public int SelectedBuilding()
    {
        foreach (int e in _selection)
            if (W.Identities.TryGet(e, out Identity id) && id.Kind == EntityKind.Building) return e;
        return 0;
    }

    public int[] SelectedBuilders() => SelectedUnits(e => W.UnitDefOf(e).CanBuild);
    public int[] SelectedGatherers() => SelectedUnits(e => W.Cargos.Has(e));
    public int[] SelectedSoldiers() => SelectedUnits(e => W.UnitDefOf(e).CanAttack && !W.UnitDefOf(e).CanGather);

    public int CountIdleVillagers()
    {
        int n = 0;
        for (int i = 0; i < W.Behaviours.Count; i++)
        {
            int e = W.Behaviours.EntityAt(i);
            if (W.Identities.Get(e).Player == Me && W.Cargos.Has(e) && W.Behaviours.At(i).State == UnitState.Idle) n++;
        }
        return n;
    }

    /// <summary>Selects the next idle villager; returns its position (or false when none).</summary>
    public bool SelectNextIdleVillager(out FixVec2 at)
    {
        var idle = new List<int>();
        for (int i = 0; i < W.Behaviours.Count; i++)
        {
            int e = W.Behaviours.EntityAt(i);
            if (W.Identities.Get(e).Player == Me && W.Cargos.Has(e) && W.Behaviours.At(i).State == UnitState.Idle) idle.Add(e);
        }
        at = default;
        if (idle.Count == 0) return false;
        int current = _selection.Count == 1 ? idle.IndexOf(_selection[0]) : -1;
        int pick = idle[(current + 1) % idle.Count];
        Select(new[] { pick });
        at = W.Positions.Get(pick).Value;
        return true;
    }

    // ---- gestures (map coordinates) -----------------------------------------------------------

    /// <summary>Returns what happened: 0 nothing, 1 move, 2 attack, 3 gather, 4 select, 5 build placed, 6 repair, 7 attack-move, 8 deselect, 9 rally.</summary>
    public int Tap(FixVec2 p, Fix64 pickRadius)
    {
        if (_mode == TapMode.Build) return TryPlaceBuilding(p) ? 5 : 0;
        if (_mode == TapMode.AttackMove)
        {
            int[] soldiers = SelectedSoldiers();
            if (soldiers.Length > 0) Submit(new AttackMoveCommand(Me, soldiers, p));
            SetMode(TapMode.Normal);
            return 7;
        }
        if (_mode == TapMode.Rally)
        {
            int b = SelectedBuilding();
            if (b != 0) Submit(new RallyCommand(Me, b, p));
            SetMode(TapMode.Normal);
            return 9;
        }

        int picked = W.PickAt(p, pickRadius);
        if (picked != 0 && W.Identities.TryGet(picked, out Identity id))
        {
            if (id.Player == Me)
            {
                if (id.Kind == EntityKind.Building && W.Constructions.Has(picked) && SelectedBuilders().Length > 0)
                { Submit(new RepairCommand(Me, SelectedBuilders(), picked)); return 6; }
                // A gathering point (mill) with gatherers selected is a gather order, not a selection.
                if (id.Kind == EntityKind.Building && W.Nodes.Has(picked) && SelectedGatherers().Length > 0)
                { Submit(new GatherCommand(Me, SelectedGatherers(), picked)); return 3; }
                if (id.Kind == EntityKind.Unit && _selection.Count == 1 && _selection[0] == picked) Select(SameTypeNearby(picked));
                else Select(new[] { picked });
                return 4;
            }
            if (id.Kind == EntityKind.ResourceNode)
            {
                int[] gatherers = SelectedGatherers();
                if (gatherers.Length > 0) { Submit(new GatherCommand(Me, gatherers, picked)); return 3; }
                _selection.Clear();
                return 8;
            }
            if (World.AreEnemies(Me, id.Player) && id.Kind != EntityKind.Projectile)
            {
                int[] soldiers = SelectedSoldiers();
                if (soldiers.Length > 0) { Submit(new AttackCommand(Me, soldiers, picked)); return 2; }
            }
        }

        int[] units = SelectedUnits();
        if (units.Length > 0) { Submit(new MoveCommand(Me, units, p)); return 1; }
        bool had = _selection.Count > 0;
        _selection.Clear();
        return had ? 8 : 0;
    }

    /// <summary>Box selection resolved in screen space by the renderer: keeps own units, prefers soldiers.</summary>
    public void SelectFromScreen(int[] ids)
    {
        var hits = new List<int>();
        var soldiers = new List<int>();
        foreach (int e in ids)
        {
            if (!W.Identities.TryGet(e, out Identity id) || id.Player != Me || id.Kind != EntityKind.Unit) continue;
            hits.Add(e);
            if (!W.Cargos.Has(e)) soldiers.Add(e);
        }
        if (hits.Count > 0) Select(soldiers.Count > 0 ? soldiers : hits);
    }

    public void SelectAll(bool soldiers)
    {
        var list = new List<int>();
        for (int i = 0; i < W.Behaviours.Count; i++)
        {
            int e = W.Behaviours.EntityAt(i);
            if (W.Identities.Get(e).Player != Me) continue;
            bool isVillager = W.Cargos.Has(e);
            if (soldiers ? (!isVillager && W.UnitDefOf(e).CanAttack) : isVillager) list.Add(e);
        }
        Select(list);
    }

    /// <summary>Long press: remembers the point for the radial menu. Returns true when a menu makes sense.</summary>
    public bool LongPress(FixVec2 p)
    {
        if (_mode != TapMode.Normal) return false;
        RadialPoint = p;
        return SelectedUnits().Length > 0;
    }

    public void BoxSelect(FixVec2 a, FixVec2 b)
    {
        Fix64 minX = FixMath.Min(a.X, b.X), maxX = FixMath.Max(a.X, b.X);
        Fix64 minY = FixMath.Min(a.Y, b.Y), maxY = FixMath.Max(a.Y, b.Y);
        var hits = new List<int>();
        var soldiers = new List<int>();
        for (int i = 0; i < W.Movers.Count; i++)
        {
            int e = W.Movers.EntityAt(i);
            if (W.Identities.Get(e).Player != Me) continue;
            FixVec2 p = W.Positions.Get(e).Value;
            if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY) continue;
            hits.Add(e);
            if (!W.Cargos.Has(e)) soldiers.Add(e);
        }
        if (hits.Count > 0) Select(soldiers.Count > 0 ? soldiers : hits);
    }

    private IEnumerable<int> SameTypeNearby(int unit)
    {
        Identity me = W.Identities.Get(unit);
        FixVec2 at = W.Positions.Get(unit).Value;
        Fix64 radius = Fix64.FromInt(15);
        var list = new List<int>();
        for (int i = 0; i < W.Identities.Count; i++)
        {
            int e = W.Identities.EntityAt(i);
            Identity id = W.Identities.At(i);
            if (id.Kind != EntityKind.Unit || id.Player != me.Player || id.DefIndex != me.DefIndex) continue;
            if (FixMath.WithinDistance(W.Positions.Get(e).Value, at, radius)) list.Add(e);
        }
        return list;
    }

    // ---- actions ------------------------------------------------------------------------------

    public void RadialMove() { int[] u = SelectedUnits(); if (u.Length > 0) Submit(new MoveCommand(Me, u, RadialPoint)); }
    public void RadialAttackMove() { int[] s = SelectedSoldiers(); if (s.Length > 0) Submit(new AttackMoveCommand(Me, s, RadialPoint)); }
    public void Stop() { int[] u = SelectedUnits(); if (u.Length > 0) Submit(new StopCommand(Me, u)); }

    public void SetMode(TapMode mode)
    {
        if (_mode == TapMode.Build && mode != TapMode.Build) _buildIndex = -1;
        _mode = mode;
    }

    public void BeginBuild(int buildingIndex)
    {
        _buildIndex = buildingIndex;
        _mode = TapMode.Build;
        UpdateGhost(_ghostPointer);
    }

    public void UpdateGhost(FixVec2 pointer)
    {
        _ghostPointer = pointer;
        if (_mode != TapMode.Build) return;
        BakedBuilding b = W.DefsOf(Me).Buildings[_buildIndex];
        Origin(pointer, b.W, b.H, out _ghostX, out _ghostY);
        _ghostValid = BuildCommand.Validate(W, Me, _buildIndex, _ghostX, _ghostY) == PlacementResult.Ok;
    }

    private bool TryPlaceBuilding(FixVec2 p)
    {
        BakedBuilding b = W.DefsOf(Me).Buildings[_buildIndex];
        Origin(p, b.W, b.H, out int x, out int y);
        PlacementResult r = BuildCommand.Validate(W, Me, _buildIndex, x, y);
        if (r != PlacementResult.Ok)
        {
            LastRejection = r == PlacementResult.NotAffordable ? "Not enough resources"
                          : r == PlacementResult.LimitReached ? "Limit reached"
                          : r == PlacementResult.WrongAge ? "Advance to the next Age first" : "Can't build there";
            return false;
        }
        Submit(new BuildCommand(Me, _buildIndex, x, y, SelectedBuilders()));
        SetMode(TapMode.Normal);
        return true;
    }

    public void Submit(ICommand command) => _session.Source.Submit(command);

    private static void Origin(FixVec2 p, int w, int h, out int x, out int y)
    {
        x = (p.X - Fix64.FromInt(w) / 2 + Fix64.Half).FloorToInt();
        y = (p.Y - Fix64.FromInt(h) / 2 + Fix64.Half).FloorToInt();
    }
}
