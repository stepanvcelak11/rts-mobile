using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using RTS.Data;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;
using RTS.Sim.Systems;

namespace RTS.Web;

/// <summary>
/// The surface wwwroot/game.js talks to. Coordinates are map cells (1 cell = 1 m); the camera
/// and all drawing live in JavaScript. Frame() advances the match and returns a flat int draw
/// list (filtered by fog of war), HudJson() returns the HUD model, the rest are player intents.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class GameApi
{
    private const int Scale = 64;                  // fixed-point → int scale for positions in the draw buffer
    private static GameData _data;
    private static WebSession _s;
    private static readonly List<int> _buf = new(8192);
    private static byte[] _fog = Array.Empty<byte>();   // 0 unexplored, 1 explored, 2 visible
    private static int _fogTick = -1;
    private static readonly Dictionary<string, int> _tagCache = new();

    private static GameData Data => _data ??= JsonLoader.Load(new EmbeddedDataSource());

    // ---- lobby -------------------------------------------------------------------------------

    [JSExport]
    public static string Civs()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < Data.Civs.Count; i++)
        {
            CivDef c = Data.Civs[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(c.id).Append("\",\"name\":\"").Append(Esc(c.name)).Append("\",\"tagline\":\"").Append(Esc(c.tagline))
              .Append("\",\"color\":\"").Append(c.color).Append("\",\"unique\":\"").Append(Esc(UniqueUnitName(c))).Append("\"}");
        }
        return sb.Append(']').ToString();
    }

    private static string UniqueUnitName(CivDef c)
    {
        foreach (string id in c.uniqueUnits) if (Data.TryUnitIndex(id, out int i)) return Data.Units[i].name;
        return "";
    }

    [JSExport]
    public static string MapTypes() => "[\"" + string.Join("\",\"", MapGenerator.Types) + "\"]";

    /// <summary>Unit and building definitions (index → id/name/tags) so the renderer can pick sprites.</summary>
    [JSExport]
    public static string Defs()
    {
        var sb = new StringBuilder("{\"units\":[");
        for (int i = 0; i < Data.Units.Count; i++)
        {
            UnitDef u = Data.Units[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(u.id).Append("\",\"name\":\"").Append(Esc(u.name)).Append("\",\"tags\":[\"").Append(string.Join("\",\"", u.tags)).Append("\"]}");
        }
        sb.Append("],\"buildings\":[");
        for (int i = 0; i < Data.Buildings.Count; i++)
        {
            BuildingDef b = Data.Buildings[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(b.id).Append("\",\"name\":\"").Append(Esc(b.name)).Append("\",\"w\":").Append(b.FootprintW).Append(",\"h\":").Append(b.FootprintH).Append('}');
        }
        sb.Append("],\"nodes\":[");
        for (int i = 0; i < Data.ResourceNodes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Data.ResourceNodes[i].id).Append('"');
        }
        return sb.Append("]}").ToString();
    }

    [JSExport]
    public static void StartMatch(string civ, string enemyCiv, int difficulty, int seed, string mapType, int mapSize)
    {
        var config = new WorldConfig
        {
            Seed = seed == 0 ? (uint)Environment.TickCount : (uint)seed,
            PlayerCount = 2,
            MapId = "map.default",
            MapType = string.IsNullOrEmpty(mapType) ? null : mapType,
            MapSize = mapSize <= 0 ? 80 : mapSize,
            CivIds = new[] { civ, enemyCiv },
            Ai = new[] { AiDifficulty.None, (AiDifficulty)Math.Clamp(difficulty, 1, 3) },
        };
        _s = new WebSession(Data, config, 0);
        _fog = new byte[_s.World.Map.Width * _s.World.Map.Height];
        _fogTick = -1;
        UpdateFog();
    }

    [JSExport]
    public static int MapWidth() => _s?.World.Map.Width ?? 0;

    [JSExport]
    public static int MapHeight() => _s?.World.Map.Height ?? 0;

    /// <summary>Terrain type | elevation &lt;&lt; 4 per cell (row-major), for the ground layer drawn once.</summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static int[] Terrain()
    {
        GridMap m = _s.World.Map;
        var t = new int[m.Width * m.Height];
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
            {
                ref Cell c = ref m.CellAt(x, y);
                t[y * m.Width + x] = (int)c.Terrain | (c.Elevation << 4);
            }
        return t;
    }

    /// <summary>Fog state per cell: 0 unexplored, 1 explored, 2 visible. Cheap to call every few frames.</summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static int[] Fog()
    {
        var f = new int[_fog.Length];
        for (int i = 0; i < f.Length; i++) f[i] = _fog[i];
        return f;
    }

    /// <summary>Home position of the local player (town center) for the initial camera.</summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static double[] Home()
    {
        World w = _s.World;
        int tc = w.FindBuilding(0, w.Defs.Data.BuildingIndex("bld.towncenter"));
        FixVec2 c = tc != 0 ? w.Footprints.Get(tc).Center : FixVec2.FromInts(w.Map.Width / 2, w.Map.Height / 2);
        return new[] { c.X.ToDouble(), c.Y.ToDouble() };
    }

    // ---- frame ---------------------------------------------------------------------------------

    /// <summary>
    /// Advances the simulation by dt seconds and returns the draw list:
    /// header[16] = count, tick, winner, ghostVisible, ghostX, ghostY, ghostW, ghostH, ghostValid, effectCount, pingX, pingY, fogChanged, rallyX, rallyY, reserved
    /// then per entity 12 ints: kind, id, x, y, player, def, sizeA, sizeB, hp%, flags, facingDeg, state
    /// then per effect 3 ints: kind, x, y.  Positions/sizes are ×64. Enemies hidden by fog are omitted.
    /// </summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static int[] Frame(double dt)
    {
        if (_s == null) return Array.Empty<int>();
        _s.Clock += dt;
        _s.Runner.Advance(Math.Min(dt, 0.25));
        World w = _s.World;
        WebController c = _s.Controller;
        Fix64 alpha = Fix64.FromDecimal((decimal)Math.Clamp(_s.Alpha, 0f, 1f));
        int me = _s.LocalPlayer;
        bool fogChanged = false;
        if (w.Tick - _fogTick >= 5) { UpdateFog(); fogChanged = true; }

        _buf.Clear();
        for (int i = 0; i < 16; i++) _buf.Add(0);

        int count = 0;
        // Footprint entities: buildings and nodes.
        for (int i = 0; i < w.Footprints.Count; i++)
        {
            int e = w.Footprints.EntityAt(i);
            Identity id = w.Identities.Get(e);
            Footprint fp = w.Footprints.At(i);
            if (id.Player != me && !FootprintExplored(fp)) continue;
            int hp = w.Healths.TryGet(e, out Health h) && !h.MaxHp.IsZero ? (h.Hp * 100 / h.MaxHp).RoundToInt() : -1;
            int flags = (c.IsSelected(e) ? 1 : 0) | (w.Constructions.Has(e) ? 2 : 0);
            int state = 0;
            if (id.Kind == EntityKind.Building)
            {
                if (w.Constructions.TryGet(e, out Construction con)) state = (con.Progress * 100).RoundToInt();
                if (w.Queues.TryGet(e, out ProductionQueue q) && q.Count > 0) flags |= 16;
                if (w.Turrets.Has(e)) flags |= 32;
                if (w.Researches.Has(e) || w.Players[id.Player].AgeUpBuilding == e) flags |= 1024;
                if (w.Nodes.Has(e)) flags |= 2048;   // mill with farm plots
                if (!FootprintVisible(fp)) flags |= 4096;   // remembered, not currently seen
            }
            else
            {
                state = NodeKind(w.Defs.Nodes[id.DefIndex].Id);
                if (w.Nodes.TryGet(e, out ResourceNode node) && node.Depletes && node.Amount < Fix64.FromInt(60)) flags |= 8192;   // nearly gone
            }
            Push((int)id.Kind, e, fp.X * Scale, fp.Y * Scale, id.Player, id.DefIndex, fp.W * Scale, fp.H * Scale, hp, flags, 0, state);
            count++;
        }
        // Units and projectiles.
        for (int i = 0; i < w.Positions.Count; i++)
        {
            int e = w.Positions.EntityAt(i);
            Identity id = w.Identities.Get(e);
            Position p = w.Positions.At(i);
            if (id.Player != me && !CellVisible(p.Value.CellX, p.Value.CellY)) continue;
            FixVec2 pos = _s.DrawPosition(e, p.Value, alpha);
            int hp = w.Healths.TryGet(e, out Health h) && !h.MaxHp.IsZero ? (h.Hp * 100 / h.MaxHp).RoundToInt() : -1;
            int flags = c.IsSelected(e) ? 1 : 0;
            int state = 0;
            if (id.Kind == EntityKind.Unit)
            {
                BakedUnit u = w.DefsOf(id.Player).Units[id.DefIndex];
                if (u.CanGather) flags |= 4;
                if (u.CanAttack && !u.CanGather) flags |= 8;
                if (BakedDefs.HasTag(u.Tags, TagIndex(w, "tag.cavalry"))) flags |= 64;
                if (BakedDefs.HasTag(u.Tags, TagIndex(w, "tag.artillery"))) flags |= 128;
                if (u.Attacks.Length > 0 && u.Attacks[0].HasProjectile) flags |= 256;
                UnitBehaviour b = w.Behaviours.Get(e);
                state = (int)b.State;
                if (w.Cargos.TryGet(e, out Cargo cargo) && !cargo.IsEmpty) flags |= 512 | (cargo.Resource << 14);
                if (w.Movers.Get(e).Moving) flags |= 16384 << 4;
                if (b.State == UnitState.Attack && u.Attacks.Length > 0 && b.Cooldown > u.Attacks[0].CooldownTicks - 4) flags |= 1 << 20;   // just fired
            }
            else if (id.Kind == EntityKind.Projectile)
            {
                Projectile pr = w.Projectiles.Get(e);
                state = pr.TotalTicks > 0 ? 100 - pr.TicksLeft * 100 / pr.TotalTicks : 100;   // flight progress %
                if (pr.SourceKind == EntityKind.Building || (pr.SourceKind == EntityKind.Unit && BakedDefs.HasTag(w.DefsOf(pr.SourcePlayer).Units[pr.SourceDef].Tags, TagIndex(w, "tag.artillery")))) flags |= 128;
            }
            int facing = (int)Math.Round(Math.Atan2(p.Facing.Y.ToDouble(), p.Facing.X.ToDouble()) * 180.0 / Math.PI);
            Push((int)id.Kind, e, (pos.X * Scale).RoundToInt(), (pos.Y * Scale).RoundToInt(), id.Player, id.DefIndex,
                 (p.Radius * Scale).RoundToInt(), 0, hp, flags, facing, state);
            count++;
        }

        _buf[0] = count;
        _buf[1] = w.Tick;
        _buf[2] = w.Winner;
        _buf[3] = c.GhostVisible ? 1 : 0;
        if (c.GhostVisible)
        {
            BakedBuilding b = w.DefsOf(me).Buildings[c.BuildIndex];
            _buf[4] = c.GhostX * Scale; _buf[5] = c.GhostY * Scale; _buf[6] = b.W * Scale; _buf[7] = b.H * Scale; _buf[8] = c.GhostValid ? 1 : 0;
        }
        _buf[9] = _s.Effects.Count;
        foreach (int[] fx in _s.Effects) { _buf.Add(fx[0]); _buf.Add(fx[1]); _buf.Add(fx[2]); }
        _s.Effects.Clear();
        if (_s.AttackPing.HasValue && _s.Clock < _s.AttackPingUntil)
        {
            _buf[10] = (_s.AttackPing.Value.X * Scale).RoundToInt();
            _buf[11] = (_s.AttackPing.Value.Y * Scale).RoundToInt();
        }
        else { _buf[10] = -1; _buf[11] = -1; }
        _buf[12] = fogChanged ? 1 : 0;
        int sel = c.SelectedBuilding();
        if (sel != 0 && w.Rallies.TryGet(sel, out Rally rally)) { _buf[13] = (rally.Point.X * Scale).RoundToInt(); _buf[14] = (rally.Point.Y * Scale).RoundToInt(); }
        else { _buf[13] = -1; _buf[14] = -1; }
        return _buf.ToArray();
    }

    private static void Push(int kind, int id, int x, int y, int player, int def, int a, int b, int hp, int flags, int facing, int state)
    {
        _buf.Add(kind); _buf.Add(id); _buf.Add(x); _buf.Add(y); _buf.Add(player); _buf.Add(def);
        _buf.Add(a); _buf.Add(b); _buf.Add(hp); _buf.Add(flags); _buf.Add(facing); _buf.Add(state);
    }

    private static int NodeKind(string id) => id switch { "res.tree" => 0, "res.berries" => 1, "res.hunt" => 2, "res.mine" => 3, _ => 4 };

    private static int TagIndex(World w, string tag)
    {
        if (!_tagCache.TryGetValue(tag, out int i)) { i = w.Defs.TryTag(tag, out int t) ? t : -1; _tagCache[tag] = i; }
        return i;
    }

    // ---- fog of war ---------------------------------------------------------------------------

    private static void UpdateFog()
    {
        World w = _s.World;
        _fogTick = w.Tick;
        int W = w.Map.Width, H = w.Map.Height, me = _s.LocalPlayer;
        for (int i = 0; i < _fog.Length; i++) if (_fog[i] == 2) _fog[i] = 1;
        for (int i = 0; i < w.Identities.Count; i++)
        {
            ref Identity id = ref w.Identities.At(i);
            if (id.Player != me) continue;
            int e = w.Identities.EntityAt(i);
            if (id.Kind == EntityKind.Unit)
            {
                Position p = w.Positions.Get(e);
                Reveal(p.Value.CellX, p.Value.CellY, w.DefsOf(me).Units[id.DefIndex].Los.CeilToInt(), W, H);
            }
            else if (id.Kind == EntityKind.Building)
            {
                Footprint fp = w.Footprints.Get(e);
                int r = w.DefsOf(me).Buildings[id.DefIndex].Los.CeilToInt();
                Reveal(fp.X + fp.W / 2, fp.Y + fp.H / 2, r + fp.W / 2, W, H);
            }
        }
    }

    private static void Reveal(int cx, int cy, int r, int W, int H)
    {
        int r2 = r * r;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x <= Math.Min(W - 1, cx + r); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2) _fog[y * W + x] = 2;
    }

    private static bool CellVisible(int x, int y)
    {
        int W = _s.World.Map.Width, H = _s.World.Map.Height;
        return x >= 0 && y >= 0 && x < W && y < H && _fog[y * W + x] == 2;
    }

    private static bool FootprintVisible(Footprint fp)
    {
        for (int y = fp.Y; y < fp.Y + fp.H; y++) for (int x = fp.X; x < fp.X + fp.W; x++) if (CellVisible(x, y)) return true;
        return false;
    }

    private static bool FootprintExplored(Footprint fp)
    {
        int W = _s.World.Map.Width, H = _s.World.Map.Height;
        for (int y = fp.Y; y < fp.Y + fp.H; y++)
            for (int x = fp.X; x < fp.X + fp.W; x++)
                if (x >= 0 && y >= 0 && x < W && y < H && _fog[y * W + x] != 0) return true;
        return false;
    }

    // ---- input (map coordinates) ------------------------------------------------------------

    /// <summary>Tap at a map point. Returns what happened: 0 nothing, 1 move, 2 attack, 3 gather, 4 select, 5 build placed, 6 repair, 7 attack-move, 8 deselect.</summary>
    [JSExport]
    public static int Tap(double x, double y, double pickRadius, int hitEntity) =>
        _s == null ? 0 : _s.Controller.Tap(V(x, y), Fix64.FromDecimal((decimal)Math.Clamp(pickRadius, 0.2, 3)), hitEntity);

    [JSExport]
    public static bool LongPress(double x, double y) => _s != null && _s.Controller.LongPress(V(x, y));

    [JSExport]
    public static void SelectEntities([JSMarshalAs<JSType.Array<JSType.Number>>] int[] ids) => _s?.Controller.SelectFromScreen(ids);

    [JSExport]
    public static void Pointer(double x, double y) => _s?.Controller.UpdateGhost(V(x, y));

    /// <summary>Radial / rally target at a map point.</summary>
    [JSExport]
    public static void PointAction(string name, double x, double y)
    {
        if (_s == null) return;
        WebController c = _s.Controller;
        int me = _s.LocalPlayer;
        FixVec2 p = V(x, y);
        switch (name)
        {
            case "move": { int[] u = c.SelectedUnits(); if (u.Length > 0) c.Submit(new MoveCommand(me, u, p)); break; }
            case "attackmove": { int[] u = c.SelectedSoldiers(); if (u.Length > 0) c.Submit(new AttackMoveCommand(me, u, p)); break; }
            case "rally": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new RallyCommand(me, b, p)); break; }
        }
    }

    /// <summary>Named HUD action. Returns a JSON reply for the few actions that need one (e.g. idle → position).</summary>
    [JSExport]
    public static string Action(string name, int arg)
    {
        if (_s == null) return "{}";
        World w = _s.World;
        WebController c = _s.Controller;
        int me = _s.LocalPlayer;
        switch (name)
        {
            case "move": c.RadialMove(); break;
            case "attackmove": c.RadialAttackMove(); break;
            case "attackmode": c.SetMode(TapMode.AttackMove); break;
            case "rallymode": c.SetMode(TapMode.Rally); break;
            case "cancelmode": c.SetMode(TapMode.Normal); break;
            case "stop": c.Stop(); break;
            case "build": c.BeginBuild(arg); break;
            case "sub": c.SubSelect(arg); break;
            case "clear": c.Select(Array.Empty<int>()); break;
            case "selectarmy": c.SelectAll(soldiers: true); break;
            case "selectvillagers": c.SelectAll(soldiers: false); break;
            case "stance": { int[] u = c.SelectedUnits(); if (u.Length > 0) c.Submit(new StanceCommand(me, u, (Stance)arg)); break; }
            case "train": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new TrainCommand(me, b, arg)); break; }
            case "research": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new ResearchCommand(me, b, arg)); break; }
            case "ageup": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new AgeUpCommand(me, b)); break; }
            case "cancel": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new CancelCommand(me, b)); break; }
            case "clearrally": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new RallyCommand(me, b, FixVec2.Zero, clear: true)); break; }
            case "buy": c.Submit(new TradeCommand(me, arg, buy: true)); break;
            case "sell": c.Submit(new TradeCommand(me, arg, buy: false)); break;
            case "ship": c.Submit(new ShipmentCommand(me, arg)); break;
            case "idle":
                if (c.SelectNextIdleVillager(out FixVec2 at)) return "{\"x\":" + Num(at.X) + ",\"y\":" + Num(at.Y) + "}";
                _s.Toasts.Add("No idle villagers");
                break;
            case "center":
                if (c.Selection.Count > 0 && w.IsAlive(c.Selection[0])) { FixVec2 tp = w.TargetPoint(c.Selection[0]); return "{\"x\":" + Num(tp.X) + ",\"y\":" + Num(tp.Y) + "}"; }
                break;
        }
        return "{}";
    }

    private static string Num(Fix64 v) => v.ToDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // ---- HUD model -------------------------------------------------------------------------------

    [JSExport]
    public static string HudJson()
    {
        if (_s == null) return "{}";
        World w = _s.World;
        WebController c = _s.Controller;
        int me = _s.LocalPlayer;
        PlayerState ps = w.Players[me];
        BakedDefs defs = w.DefsOf(me);
        var sb = new StringBuilder(4096);
        sb.Append('{');
        sb.Append("\"food\":").Append(ps.Stockpile[0].FloorToInt()).Append(",\"wood\":").Append(ps.Stockpile[1].FloorToInt()).Append(",\"gold\":").Append(ps.Stockpile[2].FloorToInt());
        sb.Append(",\"pop\":").Append(ps.Population).Append(",\"popCap\":").Append(ps.PopulationCap);
        sb.Append(",\"age\":").Append(ps.Age).Append(",\"ageName\":\"").Append(Esc(w.Defs.Ages[ps.Age].Def.name)).Append('"');
        sb.Append(",\"ageUp\":").Append(ps.AgeUpBuilding != 0 ? (ps.AgeUpRemaining / SimConstants.TickRate) : -1);
        sb.Append(",\"xp\":").Append(ps.Xp.FloorToInt()).Append(",\"shipCost\":").Append(ProductionSystem.ShipmentCost(w, ps, ps.ShipmentsSent).FloorToInt()).Append(",\"shipAvail\":").Append(ps.ShipmentsAvailable);
        sb.Append(",\"idle\":").Append(c.CountIdleVillagers());
        sb.Append(",\"mode\":").Append((int)c.Mode);
        sb.Append(",\"winner\":").Append(w.Winner);
        sb.Append(",\"minutes\":").Append(w.Tick / SimConstants.TickRate / 60);
        sb.Append(",\"seconds\":").Append(w.Tick / SimConstants.TickRate % 60);
        sb.Append(",\"villagers\":").Append(CountVillagers(w, me)).Append(",\"army\":").Append(w.CountUnits(me) - CountVillagers(w, me));
        sb.Append(",\"stats\":{\"killed\":").Append(ps.UnitsKilled).Append(",\"lost\":").Append(ps.UnitsLost).Append(",\"razed\":").Append(ps.BuildingsRazed).Append(",\"ships\":").Append(ps.ShipmentsSent)
          .Append(",\"enemyKilled\":").Append(w.Players[1 - me].UnitsKilled).Append('}');

        // toasts
        sb.Append(",\"toasts\":[");
        for (int i = 0; i < _s.Toasts.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(Esc(_s.Toasts[i])).Append('"'); }
        if (c.LastRejection != null) { if (_s.Toasts.Count > 0) sb.Append(','); sb.Append('"').Append(Esc(c.LastRejection)).Append('"'); c.LastRejection = null; }
        _s.Toasts.Clear();
        sb.Append(']');

        // selection
        string label;
        var cards = new List<(int def, string id, string name, int count)>();
        var actions = new List<(string id, string label, bool enabled, string kind, string icon, string tip)>();
        int stance = -1;
        if (c.Mode == TapMode.Build)
        {
            label = "Place " + defs.Buildings[c.BuildIndex].Def.name + " — tap the ground";
            actions.Add(("cancelmode", "Cancel", true, "danger", "x", ""));
        }
        else if (c.Mode == TapMode.AttackMove)
        {
            label = "Attack-move — tap where the army should push";
            actions.Add(("cancelmode", "Cancel", true, "danger", "x", ""));
        }
        else if (c.Mode == TapMode.Rally)
        {
            label = "Rally point — tap where new units should gather";
            actions.Add(("cancelmode", "Cancel", true, "danger", "x", ""));
        }
        else if (c.Selection.Count == 0)
        {
            label = "";
        }
        else
        {
            int building = c.SelectedBuilding();
            if (building != 0) label = BuildingActions(w, me, defs, ps, building, actions);
            else
            {
                int[] units = c.SelectedUnits();
                var counts = new Dictionary<int, int>();
                var order = new List<int>();
                foreach (int e in units) { int d = w.Identities.Get(e).DefIndex; if (!counts.ContainsKey(d)) { counts[d] = 0; order.Add(d); } counts[d]++; }
                if (order.Count > 1) foreach (int d in order) cards.Add((d, defs.Units[d].Id, defs.Units[d].Def.name, counts[d]));
                label = units.Length == 1 ? UnitLabel(w, units[0]) : units.Length + " units";
                if (units.Length == 1) stance = (int)w.Behaviours.Get(units[0]).Stance;
                else if (units.Length > 1) { stance = (int)w.Behaviours.Get(units[0]).Stance; foreach (int u in units) if ((int)w.Behaviours.Get(u).Stance != stance) { stance = -2; break; } }
                if (c.SelectedBuilders().Length > 0)
                    for (int i = 0; i < defs.Buildings.Length; i++)
                    {
                        BakedBuilding b = defs.Buildings[i];
                        if (b.Age > ps.Age) continue;
                        if (b.Limit > 0 && w.CountBuildings(me, i, true) >= b.Limit) continue;
                        actions.Add(("build:" + i, b.Def.name + "|" + CostText(w, b.Cost), ps.CanAfford(b.Cost), "build", "bld:" + b.Id, BuildingTip(b)));
                    }
                if (c.SelectedSoldiers().Length > 0)
                {
                    actions.Add(("attackmode", "Attack-move", true, "attack", "attack", "Walk to a point and fight everything on the way"));
                    actions.Add(("stance:" + (int)Stance.Aggressive, "Aggressive", stance != (int)Stance.Aggressive, "stance", "stance-a", "Chase any enemy in sight"));
                    actions.Add(("stance:" + (int)Stance.Defensive, "Defensive", stance != (int)Stance.Defensive && stance != (int)Stance.Default, "stance", "stance-d", "Fight back, return afterwards"));
                    actions.Add(("stance:" + (int)Stance.StandGround, "Stand ground", stance != (int)Stance.StandGround, "stance", "stance-s", "Never move; shoot what comes in range"));
                    actions.Add(("stance:" + (int)Stance.Passive, "Passive", stance != (int)Stance.Passive, "stance", "stance-p", "Ignore enemies"));
                }
                actions.Add(("stop", "Stop", true, "neutral", "stop", "Halt and go idle"));
            }
        }
        sb.Append(",\"label\":\"").Append(Esc(label)).Append('"');
        sb.Append(",\"stance\":").Append(stance);
        sb.Append(",\"cards\":[");
        for (int i = 0; i < cards.Count; i++) { if (i > 0) sb.Append(','); sb.Append("{\"def\":").Append(cards[i].def).Append(",\"id\":\"").Append(cards[i].id).Append("\",\"name\":\"").Append(Esc(cards[i].name)).Append("\",\"count\":").Append(cards[i].count).Append('}'); }
        sb.Append("],\"actions\":[");
        for (int i = 0; i < actions.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var a = actions[i];
            sb.Append("{\"id\":\"").Append(a.id).Append("\",\"label\":\"").Append(Esc(a.label)).Append("\",\"enabled\":").Append(a.enabled ? "true" : "false")
              .Append(",\"kind\":\"").Append(a.kind).Append("\",\"icon\":\"").Append(a.icon).Append("\",\"tip\":\"").Append(Esc(a.tip)).Append("\"}");
        }
        sb.Append(']');

        // Home City deck
        sb.Append(",\"deck\":[");
        if (ps.CivIndex >= 0)
        {
            bool first = true;
            foreach (string shipId in w.Defs.Data.Civs[ps.CivIndex].homeCity.deck)
            {
                if (!w.Defs.Data.TryTechIndex(shipId, out int tech)) continue;
                BakedTech t = w.Defs.Techs[tech];
                CommandRejectReason why = ShipmentCommand.Validate(w, me, tech);
                string note = why == CommandRejectReason.WrongAge ? "Age " + (t.Age + 1) : why == CommandRejectReason.AlreadyResearched ? "sent" : why == CommandRejectReason.NotEnoughXp ? "XP" : "";
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"tech\":").Append(tech).Append(",\"name\":\"").Append(Esc(t.Def.name)).Append("\",\"enabled\":").Append(why == CommandRejectReason.None ? "true" : "false").Append(",\"note\":\"").Append(note).Append("\"}");
            }
        }
        sb.Append(']');

        // Market
        sb.Append(",\"market\":");
        if (w.HasMarket(me))
        {
            sb.Append('[');
            int gold = w.Defs.Data.ResourceIndex("gold");
            bool first = true;
            for (int r = 0; r < w.Defs.ResourceCount; r++)
            {
                if (r == gold) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"res\":").Append(r).Append(",\"name\":\"").Append(w.Defs.Data.Economy.resources[r]).Append("\",\"buy\":").Append(ps.MarketPrice[r].RoundToInt())
                  .Append(",\"sell\":").Append((ps.MarketPrice[r] * TradeCommand.SellFactor).RoundToInt())
                  .Append(",\"canBuy\":").Append(TradeCommand.Validate(w, me, r, true) == CommandRejectReason.None ? "true" : "false")
                  .Append(",\"canSell\":").Append(TradeCommand.Validate(w, me, r, false) == CommandRejectReason.None ? "true" : "false").Append('}');
            }
            sb.Append(']');
        }
        else sb.Append("null");
        sb.Append('}');
        return sb.ToString();
    }

    private static int CountVillagers(World w, int player)
    {
        int n = 0;
        for (int i = 0; i < w.Cargos.Count; i++) if (w.Identities.Get(w.Cargos.EntityAt(i)).Player == player) n++;
        return n;
    }

    private static string UnitLabel(World w, int e)
    {
        BakedUnit u = w.UnitDefOf(e);
        Health h = w.Healths.Get(e);
        string s = u.Def.name + "  ·  " + h.Hp.RoundToInt() + "/" + h.MaxHp.RoundToInt() + " hp";
        UnitState st = w.Behaviours.Get(e).State;
        if (w.Cargos.TryGet(e, out Cargo cargo) && !cargo.IsEmpty) s += "  ·  carrying " + cargo.Amount.FloorToInt() + " " + w.Defs.Data.Economy.resources[cargo.Resource];
        s += "  ·  " + st.ToString().ToLowerInvariant();
        return s;
    }

    private static string UnitTip(World w, BakedUnit u)
    {
        var sb = new StringBuilder();
        sb.Append("HP ").Append(u.Hp.RoundToInt()).Append(" · speed ").Append(u.Speed.ToDecimal().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        if (u.CanAttack)
        {
            BakedAttack a = u.Attacks[0];
            sb.Append(" · ").Append(a.Type.ToString().ToLowerInvariant()).Append(' ').Append(a.Damage.RoundToInt()).Append(" dmg, range ").Append(a.Range.RoundToInt());
            var good = new List<string>();
            for (int i = 0; i < a.MultiplierTags.Length; i++)
                if (a.MultiplierValues[i] > Fix64.One) good.Add("×" + a.MultiplierValues[i].ToDecimal().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " vs " + TagName(w, a.MultiplierTags[i]));
            if (good.Count > 0) sb.Append(" · ").Append(string.Join(", ", good));
        }
        if (u.CanGather) sb.Append(" · gathers and builds");
        sb.Append(" · pop ").Append(u.Population);
        return sb.ToString();
    }

    private static string TagName(World w, int tag)
    {
        foreach (string t in new[] { "tag.infantry", "tag.cavalry", "tag.villager", "tag.artillery", "tag.building", "tag.ranged", "tag.gunpowder", "tag.melee" })
            if (w.Defs.TryTag(t, out int i) && i == tag) return t.Substring(4);
        return "?";
    }

    private static string BuildingTip(BakedBuilding b)
    {
        var parts = new List<string> { "HP " + b.Hp.RoundToInt(), b.W + "×" + b.H };
        if (b.PopulationProvided > 0) parts.Add("+" + b.PopulationProvided + " population");
        if (b.DropOff.Length > 0) { bool any = false; foreach (bool d in b.DropOff) any |= d; if (any) parts.Add("resource drop-off"); }
        if (b.Trains.Length > 0) parts.Add("trains units");
        if (b.Attack != null) parts.Add("shoots, range " + b.Attack.Range.RoundToInt());
        if (b.GatherNode >= 0) parts.Add("infinite farm plots (send villagers here)");
        if (b.IsMarket) parts.Add("trade resources for gold");
        if (b.Limit > 0) parts.Add("limit " + b.Limit);
        return string.Join(" · ", parts);
    }

    private static string BuildingActions(World w, int me, BakedDefs defs, PlayerState ps, int building, List<(string, string, bool, string, string, string)> actions)
    {
        Identity id = w.Identities.Get(building);
        BakedBuilding b = defs.Buildings[id.DefIndex];
        Health hp = w.Healths.Get(building);
        string label = b.Def.name + "  ·  " + hp.Hp.RoundToInt() + "/" + hp.MaxHp.RoundToInt() + " hp";
        if (w.Constructions.TryGet(building, out Construction con))
        {
            label = b.Def.name + " · building " + (con.Progress * 100).FloorToInt() + "%";
            actions.Add(("cancel", "Cancel site", true, "danger", "x", "Refunds the cost"));
            return label;
        }
        if (w.Queues.TryGet(building, out ProductionQueue q))
        {
            if (q.Count > 0)
                label += "  ·  " + defs.Units[q.Get(0)].Def.name + " " + (q.HeadRemaining + SimConstants.TickRate - 1) / SimConstants.TickRate + " s" + (q.Count > 1 ? " (+" + (q.Count - 1) + ")" : "");
            foreach (int ui in b.Trains)
            {
                BakedUnit u = defs.Units[defs.Replace(ui)];
                CommandRejectReason why = TrainCommand.Validate(w, me, building, ui);
                string note = why == CommandRejectReason.WrongAge ? " (Age " + (u.Age + 1) + ")" : "";
                actions.Add(("train:" + ui, u.Def.name + note + "|" + CostText(w, u.Cost), why == CommandRejectReason.None, "train", "unit:" + u.Id, UnitTip(w, u)));
            }
            if (q.Count > 0) actions.Add(("cancel", "Cancel last", true, "danger", "x", "Refunds the last queued unit"));
            actions.Add(("rallymode", w.Rallies.Has(building) ? "Move rally" : "Rally point", true, "neutral", "rally", "Where new units gather"));
            if (w.Rallies.Has(building)) actions.Add(("clearrally", "Clear rally", true, "neutral", "x", ""));
        }
        if (w.Researches.TryGet(building, out Research r))
            label += "  ·  " + w.Defs.Techs[r.Tech].Def.name + " " + (r.Remaining + SimConstants.TickRate - 1) / SimConstants.TickRate + " s";
        foreach (int tech in b.Researches)
        {
            if (ps.Researched[tech]) continue;
            BakedTech t = w.Defs.Techs[tech];
            CommandRejectReason why = ResearchCommand.Validate(w, me, building, tech);
            string note = why == CommandRejectReason.WrongAge ? " (Age " + (t.Age + 1) + ")" : "";
            actions.Add(("research:" + tech, t.Def.name + note + "|" + CostText(w, t.Cost), why == CommandRejectReason.None, "research", "tech", TechTip(t)));
        }
        if (ps.Age + 1 < w.Defs.Ages.Length && w.Defs.Ages[ps.Age + 1].Def.at == b.Id)
        {
            BakedAge next = w.Defs.Ages[ps.Age + 1];
            CommandRejectReason why = AgeUpCommand.Validate(w, me, building);
            actions.Add(("ageup", "Age up: " + next.Def.name + "|" + CostText(w, next.Cost), why == CommandRejectReason.None, "age", "age", "Unlocks new buildings, units and shipments"));
        }
        if (b.GatherNode >= 0) label += "  ·  farm: send villagers here for food";
        return label;
    }

    private static string TechTip(BakedTech t)
    {
        var parts = new List<string>();
        foreach (ModifierDef m in t.Def.effects)
        {
            string what = m.stat.Replace("attacks.*.damage", "damage").Replace("gather.res.", "").Replace("gather.", "");
            string who = m.target == "*" ? "" : m.target.Replace("tag.", "").Replace("bld.", "").Replace("unit.", "") + " ";
            string amount = m.op == "mul" ? "+" + ((m.value - 1) * 100).ToString("0") + "%" : m.op == "add" ? "+" + m.value : "= " + m.value;
            parts.Add(who + what + " " + amount);
        }
        return string.Join(", ", parts);
    }

    private static string CostText(World w, Fix64[] cost)
    {
        var parts = new List<string>();
        for (int i = 0; i < cost.Length; i++)
            if (!cost[i].IsZero) parts.Add(cost[i].FloorToInt() + " " + w.Defs.Data.Economy.resources[i]);
        return parts.Count == 0 ? "free" : string.Join(", ", parts);
    }

    private static FixVec2 V(double x, double y) => new(Fix64.FromDecimal((decimal)x), Fix64.FromDecimal((decimal)y));

    private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
}
