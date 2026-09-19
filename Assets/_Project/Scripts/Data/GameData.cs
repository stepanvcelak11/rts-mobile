using System;
using System.Collections.Generic;

namespace RTS.Data
{
    /// <summary>
    /// Immutable registry of every definition, built once by <see cref="JsonLoader"/>.
    /// String ids are resolved to dense integer indices here so hot paths never touch strings.
    /// </summary>
    public sealed class GameData
    {
        public EconomyDef Economy { get; }
        public IReadOnlyList<AgeDef> Ages { get; }
        public IReadOnlyList<UnitDef> Units { get; }
        public IReadOnlyList<BuildingDef> Buildings { get; }
        public IReadOnlyList<CivDef> Civs { get; }
        public IReadOnlyList<TechDef> Techs { get; }
        public IReadOnlyList<ResourceNodeDef> ResourceNodes => Economy.resourceNodes;
        public IReadOnlyList<MapDef> Maps { get; }

        private readonly Dictionary<string, int> _unitIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _buildingIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _civIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _techIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _nodeIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _resourceIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _ageIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _mapIndex = new Dictionary<string, int>();

        public GameData(EconomyDef economy, List<AgeDef> ages, List<UnitDef> units, List<BuildingDef> buildings,
                        List<CivDef> civs, List<TechDef> techs, List<MapDef> maps)
        {
            Economy = economy ?? throw new ArgumentNullException(nameof(economy));
            Ages = ages; Units = units; Buildings = buildings; Civs = civs; Techs = techs; Maps = maps;

            Index(_resourceIndex, economy.resources, r => r, "resource");
            Index(_ageIndex, ages, a => a.id, "age");
            Index(_unitIndex, units, u => u.id, "unit");
            Index(_buildingIndex, buildings, b => b.id, "building");
            Index(_civIndex, civs, c => c.id, "civ");
            Index(_techIndex, techs, t => t.id, "tech");
            Index(_nodeIndex, economy.resourceNodes, n => n.id, "resource node");
            Index(_mapIndex, maps, m => m.id, "map");
        }

        private static void Index<T>(Dictionary<string, int> dict, IReadOnlyList<T> list, Func<T, string> key, string kind)
        {
            for (int i = 0; i < list.Count; i++)
            {
                string k = key(list[i]);
                if (string.IsNullOrEmpty(k)) throw new InvalidOperationException($"A {kind} definition has no id");
                if (dict.ContainsKey(k)) throw new InvalidOperationException($"Duplicate {kind} id '{k}'");
                dict.Add(k, i);
            }
        }

        public int ResourceCount => Economy.resources.Count;

        public int UnitIndex(string id) => Lookup(_unitIndex, id, "unit");
        public int BuildingIndex(string id) => Lookup(_buildingIndex, id, "building");
        public int CivIndex(string id) => Lookup(_civIndex, id, "civ");
        public int TechIndex(string id) => Lookup(_techIndex, id, "tech");
        public int NodeIndex(string id) => Lookup(_nodeIndex, id, "resource node");
        public int ResourceIndex(string id) => Lookup(_resourceIndex, id, "resource");
        public int AgeIndex(string id) => Lookup(_ageIndex, id, "age");
        public int MapIndex(string id) => Lookup(_mapIndex, id, "map");

        public bool TryUnitIndex(string id, out int index) => _unitIndex.TryGetValue(id ?? "", out index);
        public bool TryBuildingIndex(string id, out int index) => _buildingIndex.TryGetValue(id ?? "", out index);
        public bool TryResourceIndex(string id, out int index) => _resourceIndex.TryGetValue(id ?? "", out index);
        public bool TryNodeIndex(string id, out int index) => _nodeIndex.TryGetValue(id ?? "", out index);

        private static int Lookup(Dictionary<string, int> dict, string id, string kind)
        {
            if (id != null && dict.TryGetValue(id, out int i)) return i;
            throw new KeyNotFoundException($"Unknown {kind} id '{id}'");
        }

        /// <summary>
        /// Which resource a node yields, derived from economy.gatherRates (each node yields exactly one).
        /// Returns -1 when the node has no gather rate.
        /// </summary>
        public int NodeResourceIndex(string nodeId)
        {
            if (!Economy.gatherRates.TryGetValue(nodeId, out Cost rate)) return -1;
            foreach (var kv in rate)
                if (kv.Value > 0) return ResourceIndex(kv.Key);
            return -1;
        }

        public decimal NodeGatherRate(string nodeId)
        {
            if (!Economy.gatherRates.TryGetValue(nodeId, out Cost rate)) return 0;
            foreach (var kv in rate)
                if (kv.Value > 0) return kv.Value;
            return 0;
        }
    }
}
