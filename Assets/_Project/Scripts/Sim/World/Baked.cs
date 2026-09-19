using System;
using System.Collections.Generic;
using RTS.Data;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    /// <summary>Unit definition converted to fixed point and ticks, once, at world creation.</summary>
    public sealed class BakedUnit
    {
        public int Index;
        public string Id;
        public UnitDef Def;
        public Fix64 Hp;
        public Fix64 Speed;
        public Fix64 Radius;
        public int TrainTicks;
        public int Population;
        public Fix64[] Cost;            // per resource index
        public bool CanGather;
        public bool CanBuild;
        public Fix64 GatherRateMultiplier;
    }

    public sealed class BakedBuilding
    {
        public int Index;
        public string Id;
        public BuildingDef Def;
        public Fix64 Hp;
        public int BuildTicks;
        public int W, H;
        public Fix64[] Cost;
        public bool[] DropOff;          // per resource index
        public int[] Trains;            // unit indices
        public int PopulationProvided;
        public int Limit;
        public byte TerrainMask;        // bit per TerrainType this building may stand on
        public int QueueSlots;
    }

    public sealed class BakedNode
    {
        public int Index;
        public string Id;
        public ResourceNodeDef Def;
        public int Resource;            // resource index (-1 when not gatherable)
        public Fix64 Amount;
        public Fix64 RatePerTick;
        public int W, H;
        public bool Depletes;
    }

    /// <summary>All definitions the simulation needs, in simulation-native types.</summary>
    public sealed class BakedDefs
    {
        public readonly GameData Data;
        public readonly int ResourceCount;
        public readonly BakedUnit[] Units;
        public readonly BakedBuilding[] Buildings;
        public readonly BakedNode[] Nodes;
        public readonly Fix64 CarryCapacity;
        public readonly Fix64 DepositRadius;
        public readonly Fix64 StockpileCap;
        public readonly int PopulationCapBase;
        public readonly int PopulationCapMax;

        public BakedDefs(GameData data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            ResourceCount = data.ResourceCount;
            CarryCapacity = Fix64.FromDecimal(data.Economy.carryCapacity);
            DepositRadius = Fix64.FromDecimal(data.Economy.depositRadius);
            StockpileCap = Fix64.FromDecimal(data.Economy.stockpileCap);
            PopulationCapBase = data.Economy.populationCapBase;
            PopulationCapMax = data.Economy.populationCapMax;

            Units = new BakedUnit[data.Units.Count];
            for (int i = 0; i < Units.Length; i++)
            {
                UnitDef u = data.Units[i];
                Units[i] = new BakedUnit
                {
                    Index = i,
                    Id = u.id,
                    Def = u,
                    Hp = Fix64.FromDecimal(u.stats.hp),
                    Speed = Fix64.FromDecimal(u.stats.speed),
                    Radius = RadiusForSize(u.stats.sizeClass),
                    TrainTicks = SimConstants.SecondsToTicks(u.trainSeconds),
                    Population = u.population,
                    Cost = BakeCost(data, u.cost),
                    CanGather = u.behaviour != null && u.behaviour.canGather,
                    CanBuild = u.behaviour != null && u.behaviour.canBuild,
                    GatherRateMultiplier = u.gather != null ? Fix64.FromDecimal(u.gather.rateMultiplier) : Fix64.One,
                };
            }

            Buildings = new BakedBuilding[data.Buildings.Count];
            for (int i = 0; i < Buildings.Length; i++)
            {
                BuildingDef b = data.Buildings[i];
                var dropOff = new bool[ResourceCount];
                foreach (string r in b.dropOff)
                    if (data.TryResourceIndex(r, out int ri)) dropOff[ri] = true;
                var trains = new List<int>();
                foreach (string uid in b.trains)
                    if (data.TryUnitIndex(uid, out int ui)) trains.Add(ui);
                byte mask = 0;
                foreach (string t in b.placement.terrain)
                    if (TerrainTypes.TryParse(t, out TerrainType tt)) mask |= (byte)(1 << (int)tt);

                Buildings[i] = new BakedBuilding
                {
                    Index = i,
                    Id = b.id,
                    Def = b,
                    Hp = Fix64.FromDecimal(b.stats.hp),
                    BuildTicks = Math.Max(1, SimConstants.SecondsToTicks(b.buildSeconds)),
                    W = b.FootprintW,
                    H = b.FootprintH,
                    Cost = BakeCost(data, b.cost),
                    DropOff = dropOff,
                    Trains = trains.ToArray(),
                    PopulationProvided = b.populationProvided,
                    Limit = b.limit,
                    TerrainMask = mask,
                    QueueSlots = b.queue != null ? Math.Min(b.queue.slots, ProductionQueue.Slots) : 0,
                };
            }

            Nodes = new BakedNode[data.ResourceNodes.Count];
            for (int i = 0; i < Nodes.Length; i++)
            {
                ResourceNodeDef n = data.ResourceNodes[i];
                Nodes[i] = new BakedNode
                {
                    Index = i,
                    Id = n.id,
                    Def = n,
                    Resource = data.NodeResourceIndex(n.id),
                    Amount = Fix64.FromDecimal(n.amount),
                    RatePerTick = Fix64.FromDecimal(data.NodeGatherRate(n.id)) * SimConstants.TickSeconds,
                    W = n.FootprintW,
                    H = n.FootprintH,
                    Depletes = n.depletes,
                };
            }
        }

        private static Fix64[] BakeCost(GameData data, Cost cost)
        {
            var arr = new Fix64[data.ResourceCount];
            if (cost == null) return arr;
            foreach (var kv in cost)
                if (data.TryResourceIndex(kv.Key, out int ri)) arr[ri] = Fix64.FromDecimal(kv.Value);
            return arr;
        }

        private static Fix64 RadiusForSize(string sizeClass)
        {
            switch (sizeClass)
            {
                case "large": return Fix64.Ratio(3, 4);
                case "medium": return Fix64.Half;
                default: return Fix64.Ratio(3, 10);
            }
        }
    }
}
