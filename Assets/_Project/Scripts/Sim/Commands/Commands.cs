using System.Collections.Generic;
using System.IO;
using RTS.Data;
using RTS.Sim.Core;
using RTS.Sim.Model;
using RTS.Sim.Systems;

namespace RTS.Sim.Commands
{
    public static class CommandType
    {
        public const byte Move = 1;
        public const byte Stop = 2;
        public const byte Gather = 3;
        public const byte Build = 4;
        public const byte Train = 5;
        public const byte Attack = 6;
        public const byte AttackMove = 7;
        public const byte AgeUp = 8;
        public const byte Research = 9;
        public const byte Shipment = 10;
        public const byte Cancel = 11;
        public const byte Repair = 12;
        public const byte Stance = 13;
        public const byte Rally = 14;
        public const byte Trade = 15;
    }

    /// <summary>Spreads a group order over a compact grid so units do not all fight for one point.</summary>
    public static class Formation
    {
        public static FixVec2[] Targets(World w, int[] units, FixVec2 center)
        {
            var result = new FixVec2[units.Length];
            int n = 0;
            foreach (int u in units) if (w.Positions.Has(u)) n++;
            if (n <= 1) { for (int i = 0; i < result.Length; i++) result[i] = center; return result; }

            int cols = 1;
            while (cols * cols < n) cols++;   // integer ceil(sqrt(n)), no floating point in the sim
            int rows = (n + cols - 1) / cols;
            Fix64 spacing = Fix64.FromDecimal(1.1m);
            // Order units by their position along the approach so the front line stays the front line.
            var order = new List<int>(units);
            order.Sort((a, b) =>
            {
                Fix64 da = w.Positions.Has(a) ? FixVec2.DistanceSq(w.Positions.Get(a).Value, center) : Fix64.MaxValue;
                Fix64 db = w.Positions.Has(b) ? FixVec2.DistanceSq(w.Positions.Get(b).Value, center) : Fix64.MaxValue;
                return da != db ? da.CompareTo(db) : a.CompareTo(b);
            });
            var slot = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++) slot[order[i]] = i;

            for (int i = 0; i < units.Length; i++)
            {
                int k = slot[units[i]];
                int col = k % cols, row = k / cols;
                Fix64 ox = (Fix64.FromInt(col) - Fix64.FromInt(cols - 1) / 2) * spacing;
                Fix64 oy = (Fix64.FromInt(row) - Fix64.FromInt(rows - 1) / 2) * spacing;
                FixVec2 t = w.Map.ClampInside(center + new FixVec2(ox, oy));
                if (!w.Map.IsPassable(t))
                {
                    int cx = t.CellX, cy = t.CellY;
                    t = FlowFieldCache.FindNearestPassable(w.Map, ref cx, ref cy, 2) ? FixVec2.CellCenter(cx, cy) : center;
                }
                result[i] = t;
            }
            return result;
        }
    }

    /// <summary>Move a group of units to a point.</summary>
    public sealed class MoveCommand : ICommand
    {
        public byte TypeId => CommandType.Move;
        public int Player { get; }
        public readonly int[] Units;
        public readonly FixVec2 Target;

        public MoveCommand(int player, int[] units, FixVec2 target) { Player = player; Units = units; Target = target; }

        public void Execute(World w)
        {
            FixVec2[] targets = Formation.Targets(w, Units, Target);
            for (int i = 0; i < Units.Length; i++)
                if (CommandUtil.OwnsUnit(w, Player, Units[i])) BehaviorSystem.OrderMove(w, Units[i], targets[i]);
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Player); CommandUtil.WriteInts(w, Units); w.Write(Target.X.Raw); w.Write(Target.Y.Raw);
        }

        public static MoveCommand Read(BinaryReader r) =>
            new MoveCommand(r.ReadInt32(), CommandUtil.ReadInts(r), new FixVec2(Fix64.FromRaw(r.ReadInt64()), Fix64.FromRaw(r.ReadInt64())));
    }

    /// <summary>Walk to a point, fighting anything met on the way.</summary>
    public sealed class AttackMoveCommand : ICommand
    {
        public byte TypeId => CommandType.AttackMove;
        public int Player { get; }
        public readonly int[] Units;
        public readonly FixVec2 Target;

        public AttackMoveCommand(int player, int[] units, FixVec2 target) { Player = player; Units = units; Target = target; }

        public void Execute(World w)
        {
            FixVec2[] targets = Formation.Targets(w, Units, Target);
            for (int i = 0; i < Units.Length; i++)
            {
                int u = Units[i];
                if (!CommandUtil.OwnsUnit(w, Player, u)) continue;
                if (w.UnitDefOf(u).CanAttack && w.UnitDefOf(u).Aggro != Aggro.Passive) BehaviorSystem.OrderAttackMove(w, u, targets[i]);
                else BehaviorSystem.OrderMove(w, u, targets[i]);
            }
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Player); CommandUtil.WriteInts(w, Units); w.Write(Target.X.Raw); w.Write(Target.Y.Raw);
        }

        public static AttackMoveCommand Read(BinaryReader r) =>
            new AttackMoveCommand(r.ReadInt32(), CommandUtil.ReadInts(r), new FixVec2(Fix64.FromRaw(r.ReadInt64()), Fix64.FromRaw(r.ReadInt64())));
    }

    /// <summary>Attack a specific enemy unit or building.</summary>
    public sealed class AttackCommand : ICommand
    {
        public byte TypeId => CommandType.Attack;
        public int Player { get; }
        public readonly int[] Units;
        public readonly int Target;

        public AttackCommand(int player, int[] units, int target) { Player = player; Units = units; Target = target; }

        public void Execute(World w)
        {
            if (!w.Identities.TryGet(Target, out Identity t) || !World.AreEnemies(Player, t.Player) || t.Kind == EntityKind.ResourceNode)
            { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }
            foreach (int u in Units)
                if (CommandUtil.OwnsUnit(w, Player, u) && w.UnitDefOf(u).CanAttack) BehaviorSystem.OrderAttack(w, u, Target);
        }

        public void Write(BinaryWriter w) { w.Write(Player); CommandUtil.WriteInts(w, Units); w.Write(Target); }
        public static AttackCommand Read(BinaryReader r) => new AttackCommand(r.ReadInt32(), CommandUtil.ReadInts(r), r.ReadInt32());
    }

    public sealed class StopCommand : ICommand
    {
        public byte TypeId => CommandType.Stop;
        public int Player { get; }
        public readonly int[] Units;

        public StopCommand(int player, int[] units) { Player = player; Units = units; }

        public void Execute(World w)
        {
            foreach (int u in Units)
                if (CommandUtil.OwnsUnit(w, Player, u)) BehaviorSystem.OrderStop(w, u);
        }

        public void Write(BinaryWriter w) { w.Write(Player); CommandUtil.WriteInts(w, Units); }
        public static StopCommand Read(BinaryReader r) => new StopCommand(r.ReadInt32(), CommandUtil.ReadInts(r));
    }

    /// <summary>Send gatherers to a resource node.</summary>
    public sealed class GatherCommand : ICommand
    {
        public byte TypeId => CommandType.Gather;
        public int Player { get; }
        public readonly int[] Units;
        public readonly int Node;

        public GatherCommand(int player, int[] units, int node) { Player = player; Units = units; Node = node; }

        public void Execute(World w)
        {
            if (!w.Nodes.Has(Node)) { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }
            foreach (int u in Units)
                if (CommandUtil.OwnsUnit(w, Player, u) && w.Cargos.Has(u)) BehaviorSystem.OrderGather(w, u, Node);
        }

        public void Write(BinaryWriter w) { w.Write(Player); CommandUtil.WriteInts(w, Units); w.Write(Node); }
        public static GatherCommand Read(BinaryReader r) => new GatherCommand(r.ReadInt32(), CommandUtil.ReadInts(r), r.ReadInt32());
    }

    /// <summary>Send builders to an existing construction site of the same player.</summary>
    public sealed class RepairCommand : ICommand
    {
        public byte TypeId => CommandType.Repair;
        public int Player { get; }
        public readonly int[] Units;
        public readonly int Site;

        public RepairCommand(int player, int[] units, int site) { Player = player; Units = units; Site = site; }

        public void Execute(World w)
        {
            if (!CommandUtil.OwnsBuilding(w, Player, Site) || !w.Constructions.Has(Site))
            { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }
            foreach (int u in Units)
                if (CommandUtil.OwnsUnit(w, Player, u) && w.UnitDefOf(u).CanBuild) BehaviorSystem.OrderBuild(w, u, Site);
        }

        public void Write(BinaryWriter w) { w.Write(Player); CommandUtil.WriteInts(w, Units); w.Write(Site); }
        public static RepairCommand Read(BinaryReader r) => new RepairCommand(r.ReadInt32(), CommandUtil.ReadInts(r), r.ReadInt32());
    }

    /// <summary>
    /// Place a construction site (pays the full cost up front, AoE-style) and send builders to it.
    /// Rejected when the footprint is invalid, the age is too low, the player cannot afford it, or the limit is reached.
    /// </summary>
    public sealed class BuildCommand : ICommand
    {
        public byte TypeId => CommandType.Build;
        public int Player { get; }
        public readonly int BuildingIndex;
        public readonly int X, Y;
        public readonly int[] Builders;

        public BuildCommand(int player, int buildingIndex, int x, int y, int[] builders)
        {
            Player = player; BuildingIndex = buildingIndex; X = x; Y = y; Builders = builders;
        }

        /// <summary>Full validation without side effects — also used by the UI ghost preview.</summary>
        public static PlacementResult Validate(World w, int player, int buildingIndex, int x, int y)
        {
            if (player < 0 || player >= w.Players.Length) return PlacementResult.OutOfBounds;
            BakedDefs defs = w.DefsOf(player);
            if (buildingIndex < 0 || buildingIndex >= defs.Buildings.Length) return PlacementResult.OutOfBounds;
            BakedBuilding b = defs.Buildings[buildingIndex];
            if (b.Age > w.Players[player].Age) return PlacementResult.WrongAge;
            if (b.Limit > 0 && w.CountBuildings(player, buildingIndex, includeSites: true) >= b.Limit) return PlacementResult.LimitReached;
            PlacementResult r = w.Map.Validate(x, y, b.W, b.H, b.TerrainMask);
            if (r != PlacementResult.Ok) return r;
            if (!w.Players[player].CanAfford(b.Cost)) return PlacementResult.NotAffordable;
            return PlacementResult.Ok;
        }

        public void Execute(World w)
        {
            PlacementResult r = Validate(w, Player, BuildingIndex, X, Y);
            if (r != PlacementResult.Ok)
            {
                CommandUtil.Reject(w, Player, r == PlacementResult.NotAffordable ? CommandRejectReason.NotAffordable
                                            : r == PlacementResult.LimitReached ? CommandRejectReason.LimitReached
                                            : r == PlacementResult.WrongAge ? CommandRejectReason.WrongAge
                                            : CommandRejectReason.BadPlacement);
                return;
            }

            BakedBuilding b = w.DefsOf(Player).Buildings[BuildingIndex];
            w.Players[Player].Pay(b.Cost);
            int site = w.SpawnBuilding(BuildingIndex, Player, X, Y, complete: false);
            foreach (int u in Builders)
                if (CommandUtil.OwnsUnit(w, Player, u) && w.UnitDefOf(u).CanBuild)
                    BehaviorSystem.OrderBuild(w, u, site);
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Player); w.Write(BuildingIndex); w.Write(X); w.Write(Y); CommandUtil.WriteInts(w, Builders);
        }

        public static BuildCommand Read(BinaryReader r) =>
            new BuildCommand(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), CommandUtil.ReadInts(r));
    }

    /// <summary>Queue a unit at a building. Pays on enqueue. Civ replacements are applied here.</summary>
    public sealed class TrainCommand : ICommand
    {
        public byte TypeId => CommandType.Train;
        public int Player { get; }
        public readonly int Building;
        public readonly int UnitIndex;

        public TrainCommand(int player, int building, int unitIndex) { Player = player; Building = building; UnitIndex = unitIndex; }

        public static CommandRejectReason Validate(World w, int player, int building, int unitIndex)
        {
            if (!CommandUtil.OwnsBuilding(w, player, building) || !w.Queues.Has(building)) return CommandRejectReason.InvalidTarget;
            BakedDefs defs = w.DefsOf(player);
            if (unitIndex < 0 || unitIndex >= defs.Units.Length) return CommandRejectReason.InvalidTarget;
            BakedBuilding b = defs.Buildings[w.Identities.Get(building).DefIndex];
            bool trainsHere = false;
            foreach (int ui in b.Trains) if (ui == unitIndex) { trainsHere = true; break; }
            if (!trainsHere) return CommandRejectReason.InvalidTarget;
            BakedUnit unit = defs.Units[defs.Replace(unitIndex)];
            if (unit.Age > w.Players[player].Age) return CommandRejectReason.WrongAge;
            if (w.Queues.Get(building).Count >= b.QueueSlots) return CommandRejectReason.QueueFull;
            if (!w.Players[player].CanAfford(unit.Cost)) return CommandRejectReason.NotAffordable;
            return CommandRejectReason.None;
        }

        public void Execute(World w)
        {
            if (Player < 0 || Player >= w.Players.Length) return;
            CommandRejectReason reason = Validate(w, Player, Building, UnitIndex);
            if (reason != CommandRejectReason.None) { CommandUtil.Reject(w, Player, reason); return; }

            BakedDefs defs = w.DefsOf(Player);
            BakedUnit unit = defs.Units[defs.Replace(UnitIndex)];
            w.Players[Player].Pay(unit.Cost);
            w.Queues.Get(Building).TryEnqueue(unit.Index, unit.TrainTicks);
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Building); w.Write(UnitIndex); }
        public static TrainCommand Read(BinaryReader r) => new TrainCommand(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }

    /// <summary>Removes the last queued unit from a building and refunds it.</summary>
    public sealed class CancelCommand : ICommand
    {
        public byte TypeId => CommandType.Cancel;
        public int Player { get; }
        public readonly int Building;

        public CancelCommand(int player, int building) { Player = player; Building = building; }

        public void Execute(World w)
        {
            if (!CommandUtil.OwnsBuilding(w, Player, Building)) { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }
            if (w.Queues.Has(Building))
            {
                ref ProductionQueue q = ref w.Queues.Get(Building);
                if (q.Count > 0)
                {
                    int def = q.RemoveLast();
                    w.Players[Player].Refund(w.DefsOf(Player).Units[def].Cost, w.Defs.StockpileCap);
                    return;
                }
            }
            if (w.Researches.Has(Building))
            {
                int tech = w.Researches.Get(Building).Tech;
                w.Researches.Remove(Building);
                w.Players[Player].Refund(w.Defs.Techs[tech].Cost, w.Defs.StockpileCap);
                return;
            }
            if (w.Constructions.Has(Building))
            {
                // Cancel a construction site: refund and remove.
                w.Players[Player].Refund(w.BuildingDefOf(Building).Cost, w.Defs.StockpileCap);
                w.Despawn(Building);
            }
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Building); }
        public static CancelCommand Read(BinaryReader r) => new CancelCommand(r.ReadInt32(), r.ReadInt32());
    }

    /// <summary>Start advancing to the next age at a town center.</summary>
    public sealed class AgeUpCommand : ICommand
    {
        public byte TypeId => CommandType.AgeUp;
        public int Player { get; }
        public readonly int Building;
        public readonly int Choice;

        public AgeUpCommand(int player, int building, int choice = 0) { Player = player; Building = building; Choice = choice; }

        public static CommandRejectReason Validate(World w, int player, int building)
        {
            if (player < 0 || player >= w.Players.Length) return CommandRejectReason.InvalidTarget;
            PlayerState ps = w.Players[player];
            if (ps.AgeUpBuilding != 0) return CommandRejectReason.Busy;
            if (ps.Age + 1 >= w.Defs.Ages.Length) return CommandRejectReason.WrongAge;
            if (!CommandUtil.OwnsBuilding(w, player, building) || w.Constructions.Has(building)) return CommandRejectReason.InvalidTarget;
            BakedAge next = w.Defs.Ages[ps.Age + 1];
            if (!string.IsNullOrEmpty(next.Def.at) && w.BuildingDefOf(building).Id != next.Def.at) return CommandRejectReason.InvalidTarget;
            if (!ps.CanAfford(next.Cost)) return CommandRejectReason.NotAffordable;
            return CommandRejectReason.None;
        }

        public void Execute(World w)
        {
            CommandRejectReason reason = Validate(w, Player, Building);
            if (reason != CommandRejectReason.None) { CommandUtil.Reject(w, Player, reason); return; }
            PlayerState ps = w.Players[Player];
            BakedAge next = w.Defs.Ages[ps.Age + 1];
            ps.Pay(next.Cost);
            ps.AgeUpBuilding = Building;
            ps.AgeUpRemaining = next.ResearchTicks;
            ps.AgeUpChoice = Choice;
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Building); w.Write(Choice); }
        public static AgeUpCommand Read(BinaryReader r) => new AgeUpCommand(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }

    /// <summary>Research a technology at a building (one at a time per building).</summary>
    public sealed class ResearchCommand : ICommand
    {
        public byte TypeId => CommandType.Research;
        public int Player { get; }
        public readonly int Building;
        public readonly int Tech;

        public ResearchCommand(int player, int building, int tech) { Player = player; Building = building; Tech = tech; }

        public static CommandRejectReason Validate(World w, int player, int building, int tech)
        {
            if (player < 0 || player >= w.Players.Length) return CommandRejectReason.InvalidTarget;
            if (tech < 0 || tech >= w.Defs.Techs.Length) return CommandRejectReason.InvalidTarget;
            BakedTech t = w.Defs.Techs[tech];
            if (t.IsShipment) return CommandRejectReason.InvalidTarget;
            if (!CommandUtil.OwnsBuilding(w, player, building) || w.Constructions.Has(building)) return CommandRejectReason.InvalidTarget;
            bool here = false;
            foreach (int b in t.ResearchedAt) if (b == w.Identities.Get(building).DefIndex) { here = true; break; }
            if (!here) return CommandRejectReason.InvalidTarget;
            PlayerState ps = w.Players[player];
            if (ps.Researched[tech]) return CommandRejectReason.AlreadyResearched;
            if (w.Researches.Has(building)) return CommandRejectReason.Busy;
            for (int i = 0; i < w.Researches.Count; i++) if (w.Researches.At(i).Tech == tech && w.Identities.Get(w.Researches.EntityAt(i)).Player == player) return CommandRejectReason.Busy;
            if (t.Age > ps.Age) return CommandRejectReason.WrongAge;
            foreach (int pre in t.Prerequisites) if (!ps.Researched[pre]) return CommandRejectReason.WrongAge;
            if (!ps.CanAfford(t.Cost)) return CommandRejectReason.NotAffordable;
            return CommandRejectReason.None;
        }

        public void Execute(World w)
        {
            CommandRejectReason reason = Validate(w, Player, Building, Tech);
            if (reason != CommandRejectReason.None) { CommandUtil.Reject(w, Player, reason); return; }
            BakedTech t = w.Defs.Techs[Tech];
            w.Players[Player].Pay(t.Cost);
            w.Researches.Add(Building, new Research { Tech = Tech, Remaining = t.ResearchTicks });
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Building); w.Write(Tech); }
        public static ResearchCommand Read(BinaryReader r) => new ResearchCommand(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }

    /// <summary>Send a Home-City shipment: resources land in the stockpile, units appear at the town center.</summary>
    public sealed class ShipmentCommand : ICommand
    {
        public byte TypeId => CommandType.Shipment;
        public int Player { get; }
        public readonly int Tech;

        public ShipmentCommand(int player, int tech) { Player = player; Tech = tech; }

        public static CommandRejectReason Validate(World w, int player, int tech)
        {
            if (player < 0 || player >= w.Players.Length) return CommandRejectReason.InvalidTarget;
            if (tech < 0 || tech >= w.Defs.Techs.Length || !w.Defs.Techs[tech].IsShipment) return CommandRejectReason.InvalidTarget;
            PlayerState ps = w.Players[player];
            BakedTech t = w.Defs.Techs[tech];
            if (ps.CivIndex >= 0 && !w.Defs.Data.Civs[ps.CivIndex].homeCity.deck.Contains(t.Id)) return CommandRejectReason.InvalidTarget;
            if (t.Age > ps.Age) return CommandRejectReason.WrongAge;
            if (t.Def.oncePerGame && ps.Researched[tech]) return CommandRejectReason.AlreadyResearched;
            if (ps.ShipmentsAvailable <= 0 || ps.Xp < ProductionSystem.ShipmentCost(w, ps, ps.ShipmentsSent)) return CommandRejectReason.NotEnoughXp;
            int tcIndex = w.Defs.Data.TryBuildingIndex("bld.towncenter", out int tc) ? tc : -1;
            if (tcIndex < 0 || w.FindBuilding(player, tcIndex) == 0) return CommandRejectReason.InvalidTarget;
            return CommandRejectReason.None;
        }

        public void Execute(World w)
        {
            CommandRejectReason reason = Validate(w, Player, Tech);
            if (reason != CommandRejectReason.None) { CommandUtil.Reject(w, Player, reason); return; }
            PlayerState ps = w.Players[Player];
            BakedTech t = w.Defs.Techs[Tech];
            ps.Xp -= ProductionSystem.ShipmentCost(w, ps, ps.ShipmentsSent);
            ps.ShipmentsSent++;
            ps.ShipmentsAvailable = System.Math.Max(0, ps.ShipmentsAvailable - 1);
            if (t.Def.oncePerGame) ps.Researched[Tech] = true;

            foreach (ModifierDef m in t.Def.effects)
            {
                if (m.target == "player" && m.stat != null && m.stat.StartsWith("stockpile.") && w.Defs.Data.TryResourceIndex(m.stat.Substring(10), out int ri))
                    ps.Stockpile[ri] = FixMath.Min(ps.Stockpile[ri] + Fix64.FromDecimal(m.value), w.Defs.StockpileCap);
                else
                    ProductionSystem.ApplyModifierToPlayer(w, Player, m);
            }

            int tcIndex = w.Defs.Data.BuildingIndex("bld.towncenter");
            int tcEntity = w.FindBuilding(Player, tcIndex);
            Footprint fp = w.Footprints.Get(tcEntity);
            foreach (SpawnDef s in t.Def.spawns)
            {
                if (!w.Defs.Data.TryUnitIndex(s.id, out int ui)) continue;
                ui = ps.Defs.Replace(ui);
                for (int k = 0; k < s.count; k++)
                {
                    if (!w.Map.FindFreeCellAround(fp, 5, out FixVec2 at)) break;
                    w.SpawnUnit(ui, Player, at);
                }
            }
            w.Events.Add(new SimEvent(SimEventKind.ShipmentArrived, tcEntity, Player, Tech));
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Tech); }
        public static ShipmentCommand Read(BinaryReader r) => new ShipmentCommand(r.ReadInt32(), r.ReadInt32());
    }

    /// <summary>Sets how units react to enemies on their own.</summary>
    public sealed class StanceCommand : ICommand
    {
        public byte TypeId => CommandType.Stance;
        public int Player { get; }
        public readonly int[] Units;
        public readonly Stance Stance;

        public StanceCommand(int player, int[] units, Stance stance) { Player = player; Units = units; Stance = stance; }

        public void Execute(World w)
        {
            foreach (int u in Units)
                if (CommandUtil.OwnsUnit(w, Player, u)) w.Behaviours.Get(u).Stance = Stance;
        }

        public void Write(BinaryWriter w) { w.Write(Player); CommandUtil.WriteInts(w, Units); w.Write((byte)Stance); }
        public static StanceCommand Read(BinaryReader r) => new StanceCommand(r.ReadInt32(), CommandUtil.ReadInts(r), (Stance)r.ReadByte());
    }

    /// <summary>Sets (or clears) the rally point of a production building.</summary>
    public sealed class RallyCommand : ICommand
    {
        public byte TypeId => CommandType.Rally;
        public int Player { get; }
        public readonly int Building;
        public readonly FixVec2 Point;
        public readonly bool Clear;

        public RallyCommand(int player, int building, FixVec2 point, bool clear = false) { Player = player; Building = building; Point = point; Clear = clear; }

        public void Execute(World w)
        {
            if (!CommandUtil.OwnsBuilding(w, Player, Building) || !w.Queues.Has(Building)) { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }
            if (Clear) w.Rallies.Remove(Building);
            else w.Rallies.Set(Building, new Rally { Point = w.Map.ClampInside(Point) });
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Building); w.Write(Point.X.Raw); w.Write(Point.Y.Raw); w.Write(Clear); }
        public static RallyCommand Read(BinaryReader r) => new RallyCommand(r.ReadInt32(), r.ReadInt32(), new FixVec2(Fix64.FromRaw(r.ReadInt64()), Fix64.FromRaw(r.ReadInt64())), r.ReadBoolean());
    }

    /// <summary>Market trade: buy or sell 100 of a resource for gold at the current price (sell pays 70 %).</summary>
    public sealed class TradeCommand : ICommand
    {
        public const int Lot = 100;
        public static readonly Fix64 SellFactor = Fix64.Ratio(7, 10);
        public static readonly Fix64 PriceStep = Fix64.FromInt(3);

        public byte TypeId => CommandType.Trade;
        public int Player { get; }
        public readonly int Resource;
        public readonly bool Buy;

        public TradeCommand(int player, int resource, bool buy) { Player = player; Resource = resource; Buy = buy; }

        public static CommandRejectReason Validate(World w, int player, int resource, bool buy)
        {
            if (player < 0 || player >= w.Players.Length) return CommandRejectReason.InvalidTarget;
            int gold = w.Defs.Data.ResourceIndex("gold");
            if (resource < 0 || resource >= w.Defs.ResourceCount || resource == gold) return CommandRejectReason.InvalidTarget;
            if (!w.HasMarket(player)) return CommandRejectReason.InvalidTarget;
            PlayerState ps = w.Players[player];
            if (buy && ps.Stockpile[gold] < ps.MarketPrice[resource]) return CommandRejectReason.NotAffordable;
            if (!buy && ps.Stockpile[resource] < Fix64.FromInt(Lot)) return CommandRejectReason.NotAffordable;
            return CommandRejectReason.None;
        }

        public void Execute(World w)
        {
            CommandRejectReason why = Validate(w, Player, Resource, Buy);
            if (why != CommandRejectReason.None) { CommandUtil.Reject(w, Player, why); return; }
            PlayerState ps = w.Players[Player];
            int gold = w.Defs.Data.ResourceIndex("gold");
            if (Buy)
            {
                ps.Stockpile[gold] -= ps.MarketPrice[Resource];
                ps.Stockpile[Resource] = FixMath.Min(ps.Stockpile[Resource] + Fix64.FromInt(Lot), w.Defs.StockpileCap);
                ps.MarketPrice[Resource] += PriceStep;
            }
            else
            {
                ps.Stockpile[Resource] -= Fix64.FromInt(Lot);
                ps.Stockpile[gold] = FixMath.Min(ps.Stockpile[gold] + ps.MarketPrice[Resource] * SellFactor, w.Defs.StockpileCap);
                ps.MarketPrice[Resource] = FixMath.Max(Fix64.FromInt(30), ps.MarketPrice[Resource] - PriceStep);
            }
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Resource); w.Write(Buy); }
        public static TradeCommand Read(BinaryReader r) => new TradeCommand(r.ReadInt32(), r.ReadInt32(), r.ReadBoolean());
    }

    /// <summary>Binary (de)serialisation of commands for replays and the network.</summary>
    public static class CommandCodec
    {
        public static void Write(BinaryWriter w, ICommand c)
        {
            w.Write(c.TypeId);
            c.Write(w);
        }

        public static ICommand Read(BinaryReader r)
        {
            byte t = r.ReadByte();
            switch (t)
            {
                case CommandType.Move: return MoveCommand.Read(r);
                case CommandType.Stop: return StopCommand.Read(r);
                case CommandType.Gather: return GatherCommand.Read(r);
                case CommandType.Build: return BuildCommand.Read(r);
                case CommandType.Train: return TrainCommand.Read(r);
                case CommandType.Attack: return AttackCommand.Read(r);
                case CommandType.AttackMove: return AttackMoveCommand.Read(r);
                case CommandType.AgeUp: return AgeUpCommand.Read(r);
                case CommandType.Research: return ResearchCommand.Read(r);
                case CommandType.Shipment: return ShipmentCommand.Read(r);
                case CommandType.Cancel: return CancelCommand.Read(r);
                case CommandType.Repair: return RepairCommand.Read(r);
                case CommandType.Stance: return StanceCommand.Read(r);
                case CommandType.Rally: return RallyCommand.Read(r);
                case CommandType.Trade: return TradeCommand.Read(r);
                default: throw new InvalidDataException("Unknown command type " + t);
            }
        }
    }
}
