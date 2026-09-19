using System.IO;
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
    }

    /// <summary>Move a group of units to a point (straight line in Phase 2).</summary>
    public sealed class MoveCommand : ICommand
    {
        public byte TypeId => CommandType.Move;
        public int Player { get; }
        public readonly int[] Units;
        public readonly FixVec2 Target;

        public MoveCommand(int player, int[] units, FixVec2 target) { Player = player; Units = units; Target = target; }

        public void Execute(World w)
        {
            foreach (int u in Units)
                if (CommandUtil.OwnsUnit(w, Player, u)) BehaviorSystem.OrderMove(w, u, Target);
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Player); CommandUtil.WriteInts(w, Units); w.Write(Target.X.Raw); w.Write(Target.Y.Raw);
        }

        public static MoveCommand Read(BinaryReader r) =>
            new MoveCommand(r.ReadInt32(), CommandUtil.ReadInts(r), new FixVec2(Fix64.FromRaw(r.ReadInt64()), Fix64.FromRaw(r.ReadInt64())));
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

    /// <summary>
    /// Place a construction site (pays the full cost up front, AoE-style) and send builders to it.
    /// Rejected when the footprint is invalid, the player cannot afford it, or the limit is reached.
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
            if (buildingIndex < 0 || buildingIndex >= w.Defs.Buildings.Length) return PlacementResult.OutOfBounds;
            BakedBuilding b = w.Defs.Buildings[buildingIndex];
            PlacementResult r = w.Map.Validate(x, y, b.W, b.H, b.TerrainMask);
            if (r != PlacementResult.Ok) return r;
            if (b.Limit > 0 && w.CountBuildings(player, buildingIndex, includeSites: true) >= b.Limit) return PlacementResult.LimitReached;
            if (!w.Players[player].CanAfford(b.Cost)) return PlacementResult.NotAffordable;
            return PlacementResult.Ok;
        }

        public void Execute(World w)
        {
            if (Player < 0 || Player >= w.Players.Length) return;
            PlacementResult r = Validate(w, Player, BuildingIndex, X, Y);
            if (r != PlacementResult.Ok)
            {
                CommandUtil.Reject(w, Player, r == PlacementResult.NotAffordable ? CommandRejectReason.NotAffordable
                                            : r == PlacementResult.LimitReached ? CommandRejectReason.LimitReached
                                            : CommandRejectReason.BadPlacement);
                return;
            }

            BakedBuilding b = w.Defs.Buildings[BuildingIndex];
            w.Players[Player].Pay(b.Cost);
            int site = w.SpawnBuilding(BuildingIndex, Player, X, Y, complete: false);
            foreach (int u in Builders)
                if (CommandUtil.OwnsUnit(w, Player, u) && w.Defs.Units[w.Identities.Get(u).DefIndex].CanBuild)
                    BehaviorSystem.OrderBuild(w, u, site);
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Player); w.Write(BuildingIndex); w.Write(X); w.Write(Y); CommandUtil.WriteInts(w, Builders);
        }

        public static BuildCommand Read(BinaryReader r) =>
            new BuildCommand(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), CommandUtil.ReadInts(r));
    }

    /// <summary>Queue a unit at a building. Pays on enqueue; refunds are a Phase 3 CancelCommand.</summary>
    public sealed class TrainCommand : ICommand
    {
        public byte TypeId => CommandType.Train;
        public int Player { get; }
        public readonly int Building;
        public readonly int UnitIndex;

        public TrainCommand(int player, int building, int unitIndex) { Player = player; Building = building; UnitIndex = unitIndex; }

        public void Execute(World w)
        {
            if (!CommandUtil.OwnsBuilding(w, Player, Building) || !w.Queues.Has(Building))
            { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }
            if (UnitIndex < 0 || UnitIndex >= w.Defs.Units.Length) { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }

            BakedBuilding b = w.Defs.Buildings[w.Identities.Get(Building).DefIndex];
            bool trainsHere = false;
            foreach (int ui in b.Trains) if (ui == UnitIndex) { trainsHere = true; break; }
            if (!trainsHere) { CommandUtil.Reject(w, Player, CommandRejectReason.InvalidTarget); return; }

            BakedUnit unit = w.Defs.Units[UnitIndex];
            PlayerState ps = w.Players[Player];
            ref ProductionQueue q = ref w.Queues.Get(Building);
            if (q.Count >= b.QueueSlots) { CommandUtil.Reject(w, Player, CommandRejectReason.QueueFull); return; }
            if (!ps.CanAfford(unit.Cost)) { CommandUtil.Reject(w, Player, CommandRejectReason.NotAffordable); return; }

            ps.Pay(unit.Cost);
            q.TryEnqueue(UnitIndex, unit.TrainTicks);
        }

        public void Write(BinaryWriter w) { w.Write(Player); w.Write(Building); w.Write(UnitIndex); }
        public static TrainCommand Read(BinaryReader r) => new TrainCommand(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
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
                default: throw new InvalidDataException("Unknown command type " + t);
            }
        }
    }
}
