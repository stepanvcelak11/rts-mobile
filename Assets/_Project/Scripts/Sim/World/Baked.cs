using System;
using System.Collections.Generic;
using RTS.Data;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    public enum DamageType : byte { Melee = 0, Ranged = 1, Siege = 2 }
    public enum Aggro : byte { Passive = 0, Defensive = 1, Aggressive = 2 }

    /// <summary>One attack of a unit or building, in fixed point and ticks.</summary>
    public sealed class BakedAttack
    {
        public string Id;
        public Fix64 Damage;
        public DamageType Type;
        public Fix64 Range;
        public Fix64 MinRange;
        public int CooldownTicks;
        public bool HasProjectile;
        public Fix64 ProjectileSpeed;         // cells per second
        public int[] MultiplierTags = Array.Empty<int>();
        public Fix64[] MultiplierValues = Array.Empty<Fix64>();

        public BakedAttack Clone()
        {
            var c = (BakedAttack)MemberwiseClone();
            c.MultiplierTags = (int[])MultiplierTags.Clone();
            c.MultiplierValues = (Fix64[])MultiplierValues.Clone();
            return c;
        }

        /// <summary>Product of every multiplier whose tag the target carries.</summary>
        public Fix64 MultiplierFor(int[] targetTags)
        {
            Fix64 m = Fix64.One;
            for (int i = 0; i < MultiplierTags.Length; i++)
                for (int j = 0; j < targetTags.Length; j++)
                    if (targetTags[j] == MultiplierTags[i]) { m *= MultiplierValues[i]; break; }
            return m;
        }
    }

    /// <summary>Unit definition converted to fixed point and ticks, once, at world creation.</summary>
    public sealed class BakedUnit
    {
        public int Index;
        public string Id;
        public UnitDef Def;
        public int Age;
        public Fix64 Hp;
        public Fix64 Speed;
        public Fix64 Radius;
        public Fix64 Los;
        public Fix64[] Armor = new Fix64[3];   // by DamageType
        public int TrainTicks;
        public int Population;
        public Fix64[] Cost;                   // per resource index
        public bool CanGather;
        public bool CanBuild;
        public Fix64 GatherRateMultiplier;
        public int[] Tags = Array.Empty<int>();
        public BakedAttack[] Attacks = Array.Empty<BakedAttack>();
        public Aggro Aggro;
        public Fix64 LeashRange;
        public Fix64 FleeHpPercent;
        public int[] TrainedAt = Array.Empty<int>();   // building indices

        public bool CanAttack => Attacks.Length > 0 && Attacks[0].Damage > Fix64.Zero;

        public BakedUnit Clone()
        {
            var c = (BakedUnit)MemberwiseClone();
            c.Armor = (Fix64[])Armor.Clone();
            c.Cost = (Fix64[])Cost.Clone();
            c.Tags = (int[])Tags.Clone();
            c.Attacks = new BakedAttack[Attacks.Length];
            for (int i = 0; i < Attacks.Length; i++) c.Attacks[i] = Attacks[i].Clone();
            return c;
        }
    }

    public sealed class BakedBuilding
    {
        public int Index;
        public string Id;
        public BuildingDef Def;
        public int Age;
        public Fix64 Hp;
        public Fix64 Los;
        public Fix64[] Armor = new Fix64[3];
        public int BuildTicks;
        public int W, H;
        public Fix64[] Cost;
        public bool[] DropOff;          // per resource index
        public int[] Trains;            // unit indices
        public int[] Researches;        // tech indices
        public int PopulationProvided;
        public int Limit;
        public byte TerrainMask;        // bit per TerrainType this building may stand on
        public int QueueSlots;
        public BakedAttack Attack;      // null when the building cannot shoot
        public int[] Tags = Array.Empty<int>();
        public int Garrison;

        public BakedBuilding Clone()
        {
            var c = (BakedBuilding)MemberwiseClone();
            c.Armor = (Fix64[])Armor.Clone();
            c.Cost = (Fix64[])Cost.Clone();
            c.Attack = Attack?.Clone();
            return c;
        }
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

    public sealed class BakedTech
    {
        public int Index;
        public string Id;
        public TechDef Def;
        public bool IsShipment;
        public int Age;
        public Fix64[] Cost;
        public int ResearchTicks;
        public int[] ResearchedAt = Array.Empty<int>();
        public int[] Prerequisites = Array.Empty<int>();
    }

    public sealed class BakedAge
    {
        public int Index;
        public string Id;
        public AgeDef Def;
        public Fix64[] Cost;
        public int ResearchTicks;
    }

    /// <summary>
    /// All definitions the simulation needs, in simulation-native types. The base set is baked
    /// from data once; each player then gets a clone with their civilization modifiers applied,
    /// and researched technologies keep mutating that clone during the match.
    /// </summary>
    public sealed class BakedDefs
    {
        public readonly GameData Data;
        public readonly int ResourceCount;
        public BakedUnit[] Units;
        public BakedBuilding[] Buildings;
        public readonly BakedNode[] Nodes;
        public readonly BakedTech[] Techs;
        public readonly BakedAge[] Ages;
        public readonly Fix64 CarryCapacity;
        public readonly Fix64 DepositRadius;
        public readonly Fix64 StockpileCap;
        public readonly int PopulationCapBase;
        public readonly int PopulationCapMax;

        /// <summary>Per-node gather rate multiplier (civ bonuses, techs).</summary>
        public Fix64[] GatherMultiplier;
        /// <summary>Unit index → unit index that this civ trains instead (identity when no replacement).</summary>
        public int[] Replacements;
        /// <summary>Multiplier on Home-City shipment XP costs.</summary>
        public Fix64 ShipmentXpCostMultiplier = Fix64.One;

        public readonly int TagBuilding;
        private readonly Dictionary<string, int> _tagIndex;

        public BakedDefs(GameData data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            ResourceCount = data.ResourceCount;
            CarryCapacity = Fix64.FromDecimal(data.Economy.carryCapacity);
            DepositRadius = Fix64.FromDecimal(data.Economy.depositRadius);
            StockpileCap = Fix64.FromDecimal(data.Economy.stockpileCap);
            PopulationCapBase = data.Economy.populationCapBase;
            PopulationCapMax = data.Economy.populationCapMax;

            // Tags: every tag mentioned anywhere gets a dense index; buildings always carry tag.building.
            _tagIndex = new Dictionary<string, int>();
            TagBuilding = Tag("tag.building");
            foreach (UnitDef u in data.Units)
            {
                foreach (string t in u.tags) Tag(t);
                foreach (AttackDef a in u.attacks) foreach (var kv in a.multipliers) Tag(kv.Key);
            }
            foreach (CivDef c in data.Civs) foreach (ModifierDef m in c.modifiers) if (m.target != null && m.target.StartsWith("tag.")) Tag(m.target);
            foreach (TechDef t in data.Techs) foreach (ModifierDef m in t.effects) if (m.target != null && m.target.StartsWith("tag.")) Tag(m.target);

            Ages = new BakedAge[data.Ages.Count];
            for (int i = 0; i < Ages.Length; i++)
            {
                AgeDef a = data.Ages[i];
                Ages[i] = new BakedAge { Index = i, Id = a.id, Def = a, Cost = BakeCost(data, a.cost), ResearchTicks = SimConstants.SecondsToTicks(a.researchSeconds) };
            }

            Techs = new BakedTech[data.Techs.Count];
            for (int i = 0; i < Techs.Length; i++)
            {
                TechDef t = data.Techs[i];
                var at = new List<int>();
                foreach (string b in t.researchedAt) if (data.TryBuildingIndex(b, out int bi)) at.Add(bi);
                var pre = new List<int>();
                foreach (string p in t.prerequisites) if (data.TryTechIndex(p, out int ti)) pre.Add(ti);
                Techs[i] = new BakedTech
                {
                    Index = i, Id = t.id, Def = t, IsShipment = t.kind == "shipment", Age = AgeIndexOf(data, t.age),
                    Cost = BakeCost(data, t.cost), ResearchTicks = SimConstants.SecondsToTicks(t.researchSeconds),
                    ResearchedAt = at.ToArray(), Prerequisites = pre.ToArray(),
                };
            }

            Units = new BakedUnit[data.Units.Count];
            for (int i = 0; i < Units.Length; i++)
            {
                UnitDef u = data.Units[i];
                var tags = new List<int>();
                foreach (string t in u.tags) tags.Add(Tag(t));
                var attacks = new BakedAttack[u.attacks.Count];
                for (int a = 0; a < attacks.Length; a++) attacks[a] = BakeAttack(u.attacks[a]);
                var trainedAt = new List<int>();
                foreach (string b in u.trainedAt) if (data.TryBuildingIndex(b, out int bi)) trainedAt.Add(bi);
                Units[i] = new BakedUnit
                {
                    Index = i,
                    Id = u.id,
                    Def = u,
                    Age = AgeIndexOf(data, u.age),
                    Hp = Fix64.FromDecimal(u.stats.hp),
                    Speed = Fix64.FromDecimal(u.stats.speed),
                    Radius = RadiusForSize(u.stats.sizeClass),
                    Los = Fix64.FromDecimal(u.stats.los),
                    Armor = BakeArmor(u.stats.armor),
                    TrainTicks = SimConstants.SecondsToTicks(u.trainSeconds),
                    Population = u.population,
                    Cost = BakeCost(data, u.cost),
                    CanGather = u.behaviour != null && u.behaviour.canGather,
                    CanBuild = u.behaviour != null && u.behaviour.canBuild,
                    GatherRateMultiplier = u.gather != null ? Fix64.FromDecimal(u.gather.rateMultiplier) : Fix64.One,
                    Tags = tags.ToArray(),
                    Attacks = attacks,
                    Aggro = ParseAggro(u.behaviour?.aggro),
                    LeashRange = Fix64.FromDecimal(u.behaviour?.leashRange ?? 8),
                    FleeHpPercent = Fix64.FromDecimal(u.behaviour?.fleeHpPercent ?? 0) / 100,
                    TrainedAt = trainedAt.ToArray(),
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
                var researches = new List<int>();
                foreach (string tid in b.researches)
                    if (data.TryTechIndex(tid, out int ti)) researches.Add(ti);
                byte mask = 0;
                foreach (string t in b.placement.terrain)
                    if (TerrainTypes.TryParse(t, out TerrainType tt)) mask |= (byte)(1 << (int)tt);

                Buildings[i] = new BakedBuilding
                {
                    Index = i,
                    Id = b.id,
                    Def = b,
                    Age = AgeIndexOf(data, b.age),
                    Hp = Fix64.FromDecimal(b.stats.hp),
                    Los = Fix64.FromDecimal(b.stats.los),
                    Armor = BakeArmor(b.stats.armor),
                    BuildTicks = Math.Max(1, SimConstants.SecondsToTicks(b.buildSeconds)),
                    W = b.FootprintW,
                    H = b.FootprintH,
                    Cost = BakeCost(data, b.cost),
                    DropOff = dropOff,
                    Trains = trains.ToArray(),
                    Researches = researches.ToArray(),
                    PopulationProvided = b.populationProvided,
                    Limit = b.limit,
                    TerrainMask = mask,
                    QueueSlots = b.queue != null ? Math.Min(b.queue.slots, ProductionQueue.Slots) : 0,
                    Attack = b.attack != null && b.attack.damage > 0 ? BakeAttack(b.attack) : null,
                    Tags = new[] { TagBuilding },
                    Garrison = b.garrison,
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

            GatherMultiplier = new Fix64[Nodes.Length];
            for (int i = 0; i < GatherMultiplier.Length; i++) GatherMultiplier[i] = Fix64.One;
            Replacements = new int[Units.Length];
            for (int i = 0; i < Replacements.Length; i++) Replacements[i] = i;
        }

        private BakedDefs(BakedDefs src)
        {
            Data = src.Data; ResourceCount = src.ResourceCount; Nodes = src.Nodes; Techs = src.Techs; Ages = src.Ages;
            CarryCapacity = src.CarryCapacity; DepositRadius = src.DepositRadius; StockpileCap = src.StockpileCap;
            PopulationCapBase = src.PopulationCapBase; PopulationCapMax = src.PopulationCapMax;
            TagBuilding = src.TagBuilding; _tagIndex = src._tagIndex;
            Units = new BakedUnit[src.Units.Length];
            for (int i = 0; i < Units.Length; i++) Units[i] = src.Units[i].Clone();
            Buildings = new BakedBuilding[src.Buildings.Length];
            for (int i = 0; i < Buildings.Length; i++) Buildings[i] = src.Buildings[i].Clone();
            GatherMultiplier = (Fix64[])src.GatherMultiplier.Clone();
            Replacements = (int[])src.Replacements.Clone();
            ShipmentXpCostMultiplier = src.ShipmentXpCostMultiplier;
        }

        /// <summary>Deep copy of the mutable parts (units, buildings, multipliers) for one player.</summary>
        public BakedDefs CloneForPlayer() => new BakedDefs(this);

        // ---- tags / lookups -----------------------------------------------------------------

        public int Tag(string name)
        {
            if (!_tagIndex.TryGetValue(name, out int i)) { i = _tagIndex.Count; _tagIndex.Add(name, i); }
            return i;
        }

        public bool TryTag(string name, out int index) => _tagIndex.TryGetValue(name, out index);

        public static bool HasTag(int[] tags, int tag)
        {
            for (int i = 0; i < tags.Length; i++) if (tags[i] == tag) return true;
            return false;
        }

        /// <summary>The unit this civilization actually trains for a requested unit (civ replacements).</summary>
        public int Replace(int unitIndex) => unitIndex >= 0 && unitIndex < Replacements.Length ? Replacements[unitIndex] : unitIndex;

        // ---- baking helpers ---------------------------------------------------------------------

        private BakedAttack BakeAttack(AttackDef a)
        {
            var tags = new List<int>();
            var values = new List<Fix64>();
            foreach (var kv in a.multipliers) { tags.Add(Tag(kv.Key)); values.Add(Fix64.FromDecimal(kv.Value)); }
            return new BakedAttack
            {
                Id = a.id,
                Damage = Fix64.FromDecimal(a.damage),
                Type = ParseDamage(a.type),
                Range = Fix64.FromDecimal(a.range),
                MinRange = Fix64.FromDecimal(a.minRange),
                CooldownTicks = Math.Max(1, SimConstants.SecondsToTicks(a.cooldownSeconds)),
                HasProjectile = !string.IsNullOrEmpty(a.projectile),
                ProjectileSpeed = Fix64.FromInt(string.IsNullOrEmpty(a.projectile) ? 0 : a.projectile.Contains("cannon") ? 12 : 24),
                MultiplierTags = tags.ToArray(),
                MultiplierValues = values.ToArray(),
            };
        }

        private static BakedAttack BakeAttack(BuildingAttackDef a) => new BakedAttack
        {
            Id = "tower",
            Damage = Fix64.FromDecimal(a.damage),
            Type = ParseDamage(a.type),
            Range = Fix64.FromDecimal(a.range),
            CooldownTicks = Math.Max(1, SimConstants.SecondsToTicks(a.cooldownSeconds)),
            HasProjectile = true,
            ProjectileSpeed = Fix64.FromInt(24),
        };

        private static Fix64[] BakeArmor(ArmorDef a) => new[]
        {
            Fix64.FromDecimal(a?.melee ?? 0), Fix64.FromDecimal(a?.ranged ?? 0), Fix64.FromDecimal(a?.siege ?? 0),
        };

        private static Fix64[] BakeCost(GameData data, Cost cost)
        {
            var arr = new Fix64[data.ResourceCount];
            if (cost == null) return arr;
            foreach (var kv in cost)
                if (data.TryResourceIndex(kv.Key, out int ri)) arr[ri] = Fix64.FromDecimal(kv.Value);
            return arr;
        }

        private static int AgeIndexOf(GameData data, string ageId)
        {
            for (int i = 0; i < data.Ages.Count; i++) if (data.Ages[i].id == ageId) return i;
            return 0;
        }

        private static DamageType ParseDamage(string s)
        {
            switch (s) { case "ranged": return DamageType.Ranged; case "siege": return DamageType.Siege; default: return DamageType.Melee; }
        }

        private static Aggro ParseAggro(string s)
        {
            switch (s) { case "passive": return Aggro.Passive; case "aggressive": return Aggro.Aggressive; default: return Aggro.Defensive; }
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
