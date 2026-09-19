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
/// The surface wwwroot/game.js talks to. Coordinates are map cells (1 cell = 1 m); the
/// camera lives entirely in JavaScript. Frame() returns a flat int array describing what to
/// draw, HudJson() returns the HUD model, and the remaining exports are player intents.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class GameApi
{
    private const int Scale = 64;                  // fixed-point → int scale for positions in the draw buffer
    private static GameData _data;
    private static WebSession _s;
    private static readonly List<int> _buf = new(4096);

    private static GameData Data => _data ??= JsonLoader.Load(new EmbeddedDataSource());

    [JSExport]
    public static string Civs()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < Data.Civs.Count; i++)
        {
            CivDef c = Data.Civs[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(c.id).Append("\",\"name\":\"").Append(Esc(c.name)).Append("\",\"tagline\":\"").Append(Esc(c.tagline)).Append("\",\"color\":\"").Append(c.color).Append("\"}");
        }
        return sb.Append(']').ToString();
    }

    [JSExport]
    public static void StartMatch(string civ, string enemyCiv, int difficulty, int seed)
    {
        var config = new WorldConfig
        {
            Seed = seed == 0 ? (uint)Environment.TickCount : (uint)seed,
            PlayerCount = 2,
            MapId = "map.default",
            CivIds = new[] { civ, enemyCiv },
            Ai = new[] { AiDifficulty.None, (AiDifficulty)Math.Clamp(difficulty, 1, 3) },
        };
        _s = new WebSession(Data, config, 0);
    }

    [JSExport]
    public static int MapWidth() => _s?.World.Map.Width ?? 0;

    [JSExport]
    public static int MapHeight() => _s?.World.Map.Height ?? 0;

    /// <summary>Terrain type per cell (row-major), for the ground layer drawn once.</summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static int[] Terrain()
    {
        GridMap m = _s.World.Map;
        var t = new int[m.Width * m.Height];
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
                t[y * m.Width + x] = (int)m.TerrainAt(x, y);
        return t;
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

    /// <summary>
    /// Advances the simulation by dt seconds and returns the draw list:
    /// header[12] = count, tick, winner, ghostVisible, ghostX, ghostY, ghostW, ghostH, ghostValid, effectCount, pingX, pingY
    /// then per entity 12 ints: kind, id, x, y, player, def, sizeA, sizeB, hp%, flags, facingDeg, state
    /// then per effect 3 ints: kind, x, y.  Positions/sizes are ×64.
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

        _buf.Clear();
        for (int i = 0; i < 12; i++) _buf.Add(0);

        int count = 0;
        // Footprint entities: buildings and nodes.
        for (int i = 0; i < w.Footprints.Count; i++)
        {
            int e = w.Footprints.EntityAt(i);
            Identity id = w.Identities.Get(e);
            Footprint fp = w.Footprints.At(i);
            int hp = w.Healths.TryGet(e, out Health h) && !h.MaxHp.IsZero ? (h.Hp * 100 / h.MaxHp).RoundToInt() : -1;
            int flags = (c.IsSelected(e) ? 1 : 0) | (w.Constructions.Has(e) ? 2 : 0);
            int state = 0;
            if (id.Kind == EntityKind.Building)
            {
                if (w.Constructions.TryGet(e, out Construction con)) state = (con.Progress * 100).RoundToInt();
                if (w.Queues.TryGet(e, out ProductionQueue q) && q.Count > 0) flags |= 16;
                if (w.Turrets.Has(e)) flags |= 32;
            }
            else
            {
                state = NodeKind(w.Defs.Nodes[id.DefIndex].Id);
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
                state = (int)w.Behaviours.Get(e).State;
                if (w.Cargos.TryGet(e, out Cargo cargo) && !cargo.IsEmpty) flags |= 512;
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
            BakedBuilding b = w.DefsOf(0).Buildings[c.BuildIndex];
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
        return _buf.ToArray();
    }

    private static void Push(int kind, int id, int x, int y, int player, int def, int a, int b, int hp, int flags, int facing, int state)
    {
        _buf.Add(kind); _buf.Add(id); _buf.Add(x); _buf.Add(y); _buf.Add(player); _buf.Add(def);
        _buf.Add(a); _buf.Add(b); _buf.Add(hp); _buf.Add(flags); _buf.Add(facing); _buf.Add(state);
    }

    private static int NodeKind(string id) => id switch { "res.tree" => 0, "res.berries" => 1, "res.hunt" => 2, "res.mine" => 3, _ => 4 };

    private static readonly Dictionary<string, int> _tagCache = new();
    private static int TagIndex(World w, string tag)
    {
        if (!_tagCache.TryGetValue(tag, out int i)) { i = w.Defs.TryTag(tag, out int t) ? t : -1; _tagCache[tag] = i; }
        return i;
    }

    // ---- input (map coordinates) ------------------------------------------------------------

    [JSExport]
    public static void Tap(double x, double y, double pickRadius) => _s?.Controller.Tap(V(x, y), Fix64.FromDecimal((decimal)Math.Clamp(pickRadius, 0.2, 3)));

    [JSExport]
    public static bool LongPress(double x, double y) => _s != null && _s.Controller.LongPress(V(x, y));

    [JSExport]
    public static void BoxSelect(double x0, double y0, double x1, double y1) => _s?.Controller.BoxSelect(V(x0, y0), V(x1, y1));

    [JSExport]
    public static void Pointer(double x, double y) => _s?.Controller.UpdateGhost(V(x, y));

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
            case "cancelmode": c.SetMode(TapMode.Normal); break;
            case "stop": c.Stop(); break;
            case "build": c.BeginBuild(arg); break;
            case "sub": c.SubSelect(arg); break;
            case "clear": c.Select(Array.Empty<int>()); break;
            case "train": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new TrainCommand(me, b, arg)); break; }
            case "research": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new ResearchCommand(me, b, arg)); break; }
            case "ageup": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new AgeUpCommand(me, b)); break; }
            case "cancel": { int b = c.SelectedBuilding(); if (b != 0) c.Submit(new CancelCommand(me, b)); break; }
            case "ship": c.Submit(new ShipmentCommand(me, arg)); break;
            case "idle":
                if (c.SelectNextIdleVillager(out FixVec2 at)) return "{\"x\":" + at.X.ToDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"y\":" + at.Y.ToDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                _s.Toasts.Add("No idle villagers");
                break;
        }
        return "{}";
    }

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
        var sb = new StringBuilder(2048);
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
        sb.Append(",\"stats\":{\"killed\":").Append(ps.UnitsKilled).Append(",\"lost\":").Append(ps.UnitsLost).Append(",\"razed\":").Append(ps.BuildingsRazed).Append(",\"ships\":").Append(ps.ShipmentsSent).Append('}');

        // toasts
        sb.Append(",\"toasts\":[");
        for (int i = 0; i < _s.Toasts.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(Esc(_s.Toasts[i])).Append('"'); }
        if (c.LastRejection != null) { if (_s.Toasts.Count > 0) sb.Append(','); sb.Append('"').Append(Esc(c.LastRejection)).Append('"'); c.LastRejection = null; }
        _s.Toasts.Clear();
        sb.Append(']');

        // selection
        string label;
        var cards = new List<(int def, string name, int count)>();
        var actions = new List<(string id, string label, bool enabled, string kind)>();
        if (c.Mode == TapMode.Build)
        {
            label = "Place " + defs.Buildings[c.BuildIndex].Def.name + " — tap the ground";
            actions.Add(("cancelmode", "Cancel", true, "danger"));
        }
        else if (c.Mode == TapMode.AttackMove)
        {
            label = "Attack-move — tap where to go";
            actions.Add(("cancelmode", "Cancel", true, "danger"));
        }
        else if (c.Selection.Count == 0)
        {
            label = "Tap a villager, then a tree or berries · long-press for orders";
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
                if (order.Count > 1) foreach (int d in order) cards.Add((d, defs.Units[d].Def.name, counts[d]));
                label = units.Length == 1 ? w.UnitDefOf(units[0]).Def.name : units.Length + " units";
                if (c.SelectedBuilders().Length > 0)
                    for (int i = 0; i < defs.Buildings.Length; i++)
                    {
                        BakedBuilding b = defs.Buildings[i];
                        if (b.Age > ps.Age) continue;
                        if (b.Limit > 0 && w.CountBuildings(me, i, true) >= b.Limit) continue;
                        actions.Add(("build:" + i, b.Def.name + "|" + CostText(w, b.Cost), ps.CanAfford(b.Cost), "build"));
                    }
                if (c.SelectedSoldiers().Length > 0) actions.Add(("attackmode", "Attack-move", true, "attack"));
                actions.Add(("stop", "Stop", true, "neutral"));
            }
        }
        sb.Append(",\"label\":\"").Append(Esc(label)).Append('"');
        sb.Append(",\"cards\":[");
        for (int i = 0; i < cards.Count; i++) { if (i > 0) sb.Append(','); sb.Append("{\"def\":").Append(cards[i].def).Append(",\"name\":\"").Append(Esc(cards[i].name)).Append("\",\"count\":").Append(cards[i].count).Append('}'); }
        sb.Append("],\"actions\":[");
        for (int i = 0; i < actions.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(actions[i].id).Append("\",\"label\":\"").Append(Esc(actions[i].label)).Append("\",\"enabled\":").Append(actions[i].enabled ? "true" : "false").Append(",\"kind\":\"").Append(actions[i].kind).Append("\"}");
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
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildingActions(World w, int me, BakedDefs defs, PlayerState ps, int building, List<(string, string, bool, string)> actions)
    {
        Identity id = w.Identities.Get(building);
        BakedBuilding b = defs.Buildings[id.DefIndex];
        string label = b.Def.name;
        if (w.Constructions.TryGet(building, out Construction con))
        {
            label += " · building " + (con.Progress * 100).FloorToInt() + "%";
            actions.Add(("cancel", "Cancel site", true, "danger"));
            return label;
        }
        if (w.Queues.TryGet(building, out ProductionQueue q))
        {
            if (q.Count > 0)
                label += " · " + defs.Units[q.Get(0)].Def.name + " " + (q.HeadRemaining + SimConstants.TickRate - 1) / SimConstants.TickRate + " s" + (q.Count > 1 ? " (+" + (q.Count - 1) + ")" : "");
            foreach (int ui in b.Trains)
            {
                BakedUnit u = defs.Units[defs.Replace(ui)];
                CommandRejectReason why = TrainCommand.Validate(w, me, building, ui);
                string note = why == CommandRejectReason.WrongAge ? " (Age " + (u.Age + 1) + ")" : "";
                actions.Add(("train:" + ui, u.Def.name + note + "|" + CostText(w, u.Cost), why == CommandRejectReason.None, "train"));
            }
            if (q.Count > 0) actions.Add(("cancel", "Cancel last", true, "danger"));
        }
        if (w.Researches.TryGet(building, out Research r))
            label += " · " + w.Defs.Techs[r.Tech].Def.name + " " + (r.Remaining + SimConstants.TickRate - 1) / SimConstants.TickRate + " s";
        foreach (int tech in b.Researches)
        {
            if (ps.Researched[tech]) continue;
            BakedTech t = w.Defs.Techs[tech];
            CommandRejectReason why = ResearchCommand.Validate(w, me, building, tech);
            string note = why == CommandRejectReason.WrongAge ? " (Age " + (t.Age + 1) + ")" : "";
            actions.Add(("research:" + tech, t.Def.name + note + "|" + CostText(w, t.Cost), why == CommandRejectReason.None, "research"));
        }
        if (ps.Age + 1 < w.Defs.Ages.Length && w.Defs.Ages[ps.Age + 1].Def.at == b.Id)
        {
            BakedAge next = w.Defs.Ages[ps.Age + 1];
            CommandRejectReason why = AgeUpCommand.Validate(w, me, building);
            actions.Add(("ageup", "Age up: " + next.Def.name + "|" + CostText(w, next.Cost), why == CommandRejectReason.None, "age"));
        }
        return label;
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
