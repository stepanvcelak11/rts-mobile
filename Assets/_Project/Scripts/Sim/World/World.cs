using System;
using System.Collections.Generic;
using RTS.Data;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Systems;

namespace RTS.Sim.Model
{
    /// <summary>Settings a match is created with. Serialised into replays.</summary>
    public sealed class WorldConfig
    {
        public uint Seed = 1;
        public int PlayerCount = 2;
        public string MapId = "map.default";
        public string[] CivIds = { "civ.crown", "civ.crown" };
        public bool SpawnStartingUnits = true;
    }

    /// <summary>
    /// The whole deterministic game state plus the systems that advance it. The only way the
    /// state changes is <see cref="Step"/>. No UnityEngine, no floats, no wall clock.
    /// </summary>
    public sealed class World
    {
        public readonly BakedDefs Defs;
        public readonly WorldConfig Config;
        public readonly GridMap Map;
        public readonly DetRandom Rng;
        public readonly PlayerState[] Players;

        public int Tick { get; private set; }
        public ulong LastHash { get; private set; }

        // ---- component stores (dense, hashed in this order) ----
        public readonly ComponentStore<Identity> Identities = new ComponentStore<Identity>();
        public readonly ComponentStore<Position> Positions = new ComponentStore<Position>();
        public readonly ComponentStore<Footprint> Footprints = new ComponentStore<Footprint>();
        public readonly ComponentStore<Health> Healths = new ComponentStore<Health>();
        public readonly ComponentStore<Mover> Movers = new ComponentStore<Mover>();
        public readonly ComponentStore<UnitBehaviour> Behaviours = new ComponentStore<UnitBehaviour>();
        public readonly ComponentStore<Cargo> Cargos = new ComponentStore<Cargo>();
        public readonly ComponentStore<ResourceNode> Nodes = new ComponentStore<ResourceNode>();
        public readonly ComponentStore<Construction> Constructions = new ComponentStore<Construction>();
        public readonly ComponentStore<ProductionQueue> Queues = new ComponentStore<ProductionQueue>();

        /// <summary>Events raised during the last <see cref="Step"/>. Read-only for presentation.</summary>
        public readonly List<SimEvent> Events = new List<SimEvent>(64);

        private readonly List<ISystem> _systems = new List<ISystem>();
        private readonly List<int> _pendingDespawn = new List<int>();
        private int _nextEntity = 1;   // ids are never reused within a match

        public World(GameData data, WorldConfig config)
        {
            Defs = new BakedDefs(data);
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Rng = new DetRandom(config.Seed);

            MapDef mapDef = data.Maps.Count > 0 ? data.Maps[data.MapIndex(config.MapId)] : new MapDef();
            Map = GridMap.FromDef(mapDef);

            Players = new PlayerState[config.PlayerCount];
            for (int p = 0; p < Players.Length; p++)
            {
                string civId = p < config.CivIds.Length ? config.CivIds[p] : config.CivIds[0];
                int civ = data.Civs.Count > 0 ? data.CivIndex(civId) : -1;
                Players[p] = new PlayerState(p, civ, Defs.ResourceCount);
                Cost start = civ >= 0 ? data.Civs[civ].startingStockpile : data.Economy.startingStockpile;
                foreach (var kv in start)
                    if (data.TryResourceIndex(kv.Key, out int ri)) Players[p].Stockpile[ri] = Fix64.FromDecimal(kv.Value);
                Players[p].PopulationCap = Defs.PopulationCapBase;
            }

            // Fixed system order — see docs/01-ARCHITECTURE.md §3.2. Phase 2 subset.
            _systems.Add(new ProductionSystem());
            _systems.Add(new BehaviorSystem());
            _systems.Add(new MovementSystem());
            _systems.Add(new EconomySystem());

            SpawnMapContent(mapDef);
            LastHash = ComputeHash();
        }

        // ---- lifecycle ------------------------------------------------------------------

        /// <summary>Advances the world by exactly one tick, applying <paramref name="commands"/> first.</summary>
        public void Step(TickCommands commands)
        {
            Events.Clear();
            if (commands != null)
            {
                if (commands.Tick != Tick)
                    throw new InvalidOperationException($"Commands for tick {commands.Tick} applied at tick {Tick}");
                foreach (ICommand cmd in commands.Commands) cmd.Execute(this);
            }

            for (int i = 0; i < _systems.Count; i++) _systems[i].Step(this);

            FlushDespawns();
            Tick++;
            LastHash = ComputeHash();
        }

        public ulong ComputeHash()
        {
            Hasher h = Hasher.Create();
            h.Add(Tick);
            Rng.Hash(ref h);
            for (int p = 0; p < Players.Length; p++) Players[p].Hash(ref h);
            Map.Hash(ref h);
            Identities.Hash(ref h);
            Positions.Hash(ref h);
            Footprints.Hash(ref h);
            Healths.Hash(ref h);
            Movers.Hash(ref h);
            Behaviours.Hash(ref h);
            Cargos.Hash(ref h);
            Nodes.Hash(ref h);
            Constructions.Hash(ref h);
            Queues.Hash(ref h);
            h.Add(_nextEntity);
            return h.Value;
        }

        // ---- entities -------------------------------------------------------------------

        public bool IsAlive(int entity) => Identities.Has(entity);

        public int SpawnUnit(int unitIndex, int player, FixVec2 pos)
        {
            BakedUnit u = Defs.Units[unitIndex];
            int e = _nextEntity++;
            Identities.Add(e, new Identity { Kind = EntityKind.Unit, DefIndex = unitIndex, Player = player });
            Positions.Add(e, new Position { Value = Map.ClampInside(pos), Radius = u.Radius, Facing = new FixVec2(Fix64.Zero, Fix64.One) });
            Healths.Add(e, new Health { Hp = u.Hp, MaxHp = u.Hp });
            Movers.Add(e, new Mover { Speed = u.Speed });
            Behaviours.Add(e, new UnitBehaviour { State = UnitState.Idle });
            if (u.CanGather) Cargos.Add(e, new Cargo { Resource = -1, Capacity = Defs.CarryCapacity });
            if (player >= 0) Players[player].Population += u.Population;
            Events.Add(new SimEvent(SimEventKind.Spawned, e));
            return e;
        }

        /// <summary>Places a building. With <paramref name="complete"/> = false it starts as a construction site.</summary>
        public int SpawnBuilding(int buildingIndex, int player, int x, int y, bool complete)
        {
            BakedBuilding b = Defs.Buildings[buildingIndex];
            int e = _nextEntity++;
            var fp = new Footprint { X = x, Y = y, W = b.W, H = b.H };
            Identities.Add(e, new Identity { Kind = EntityKind.Building, DefIndex = buildingIndex, Player = player });
            Footprints.Add(e, fp);
            Map.Occupy(fp, e);
            if (complete)
            {
                Healths.Add(e, new Health { Hp = b.Hp, MaxHp = b.Hp });
                OnBuildingCompleted(e, b, player);
            }
            else
            {
                // A site starts with 10% hp and grows with progress (ProductionSystem).
                Healths.Add(e, new Health { Hp = b.Hp / 10, MaxHp = b.Hp });
                Constructions.Add(e, new Construction { Progress = Fix64.Zero, TotalTicks = b.BuildTicks });
                Events.Add(new SimEvent(SimEventKind.ConstructionStarted, e));
            }
            Events.Add(new SimEvent(SimEventKind.Spawned, e));
            return e;
        }

        internal void OnBuildingCompleted(int entity, BakedBuilding b, int player)
        {
            if (b.QueueSlots > 0) Queues.Add(entity, new ProductionQueue());
            if (player >= 0 && b.PopulationProvided > 0)
                Players[player].PopulationCap = Math.Min(Players[player].PopulationCap + b.PopulationProvided, Defs.PopulationCapMax);
        }

        public int SpawnResourceNode(int nodeIndex, int x, int y, Fix64 amountOverride)
        {
            BakedNode n = Defs.Nodes[nodeIndex];
            int e = _nextEntity++;
            var fp = new Footprint { X = x, Y = y, W = n.W, H = n.H };
            Identities.Add(e, new Identity { Kind = EntityKind.ResourceNode, DefIndex = nodeIndex, Player = SimConstants.NeutralPlayer });
            Footprints.Add(e, fp);
            Map.Occupy(fp, e);
            Nodes.Add(e, new ResourceNode
            {
                Resource = n.Resource,
                Amount = amountOverride < Fix64.Zero ? n.Amount : amountOverride,
                RatePerTick = n.RatePerTick,
                Depletes = n.Depletes,
            });
            Events.Add(new SimEvent(SimEventKind.Spawned, e));
            return e;
        }

        /// <summary>Marks an entity for removal at the end of the current tick.</summary>
        public void Despawn(int entity)
        {
            if (!IsAlive(entity) || _pendingDespawn.Contains(entity)) return;
            _pendingDespawn.Add(entity);
        }

        private void FlushDespawns()
        {
            for (int i = 0; i < _pendingDespawn.Count; i++)
            {
                int e = _pendingDespawn[i];
                if (!Identities.TryGet(e, out Identity id)) continue;
                if (Footprints.TryGet(e, out Footprint fp)) Map.Release(fp, e);
                if (id.Kind == EntityKind.Unit && id.Player >= 0)
                    Players[id.Player].Population -= Defs.Units[id.DefIndex].Population;
                if (id.Kind == EntityKind.Building && id.Player >= 0 && !Constructions.Has(e))
                {
                    int provided = Defs.Buildings[id.DefIndex].PopulationProvided;
                    if (provided > 0) Players[id.Player].PopulationCap = Math.Max(Defs.PopulationCapBase, Players[id.Player].PopulationCap - provided);
                }
                Identities.Remove(e); Positions.Remove(e); Footprints.Remove(e); Healths.Remove(e);
                Movers.Remove(e); Behaviours.Remove(e); Cargos.Remove(e); Nodes.Remove(e);
                Constructions.Remove(e); Queues.Remove(e);
                Events.Add(new SimEvent(SimEventKind.Despawned, e));
            }
            _pendingDespawn.Clear();
        }

        // ---- queries (O(n); Phase 3 adds a spatial hash) ----------------------------------

        /// <summary>Nearest node yielding <paramref name="resource"/> within radius, that still has stock. 0 if none.</summary>
        public int FindNearestNode(FixVec2 from, int resource, Fix64 radius, int exclude = 0)
        {
            int best = 0;
            Fix64 bestD = radius * radius;
            for (int i = 0; i < Nodes.Count; i++)
            {
                ref ResourceNode n = ref Nodes.At(i);
                int e = Nodes.EntityAt(i);
                if (e == exclude || n.Resource != resource || n.IsDepleted) continue;
                Fix64 d = Footprints.Get(e).DistanceSqTo(from);
                if (d < bestD || (d == bestD && e < best)) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>Nearest completed building of <paramref name="player"/> that accepts <paramref name="resource"/>. 0 if none.</summary>
        public int FindNearestDropOff(FixVec2 from, int player, int resource)
        {
            int best = 0;
            Fix64 bestD = Fix64.MaxValue;
            for (int i = 0; i < Identities.Count; i++)
            {
                ref Identity id = ref Identities.At(i);
                if (id.Kind != EntityKind.Building || id.Player != player) continue;
                int e = Identities.EntityAt(i);
                if (Constructions.Has(e)) continue;
                if (!Defs.Buildings[id.DefIndex].DropOff[resource]) continue;
                Fix64 d = Footprints.Get(e).DistanceSqTo(from);
                if (d < bestD || (d == bestD && e < best)) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>Entity under a world point: buildings/nodes by footprint, units by radius (+fat finger). 0 if none.</summary>
        public int PickAt(FixVec2 p, Fix64 unitPickRadius)
        {
            int occupant = Map.OccupantAt(p.CellX, p.CellY);
            int best = 0;
            Fix64 bestD = Fix64.MaxValue;
            for (int i = 0; i < Positions.Count; i++)
            {
                ref Position pos = ref Positions.At(i);
                Fix64 reach = pos.Radius + unitPickRadius;
                Fix64 d = FixVec2.DistanceSq(pos.Value, p);
                if (d <= reach * reach && d < bestD) { bestD = d; best = Positions.EntityAt(i); }
            }
            return best != 0 ? best : occupant;
        }

        public int CountBuildings(int player, int buildingIndex, bool includeSites)
        {
            int n = 0;
            for (int i = 0; i < Identities.Count; i++)
            {
                ref Identity id = ref Identities.At(i);
                if (id.Kind == EntityKind.Building && id.Player == player && id.DefIndex == buildingIndex)
                    if (includeSites || !Constructions.Has(Identities.EntityAt(i))) n++;
            }
            return n;
        }

        /// <summary>Distance-based check whether a unit stands next to a footprint (within reach).</summary>
        public bool IsAdjacent(int unit, int target, Fix64 reach)
        {
            if (!Positions.TryGet(unit, out Position pos) || !Footprints.TryGet(target, out Footprint fp)) return false;
            Fix64 r = reach + pos.Radius;
            return fp.DistanceSqTo(pos.Value) <= r * r;
        }

        // ---- map content ----------------------------------------------------------------

        /// <summary>grass | dirt | sand.</summary>
        public const byte WalkableMask = 0x07;

        private void SpawnMapContent(MapDef mapDef)
        {
            foreach (NodePlacementDef n in mapDef.nodes)
                if (Defs.Data.TryNodeIndex(n.id, out int ni))
                {
                    BakedNode bn = Defs.Nodes[ni];
                    if (Map.Validate(n.x, n.y, bn.W, bn.H, WalkableMask) == PlacementResult.Ok)
                        SpawnResourceNode(ni, n.x, n.y, n.amount < 0 ? Fix64.FromInt(-1) : Fix64.FromDecimal(n.amount));
                }

            if (!Config.SpawnStartingUnits) return;
            int tcIndex = Defs.Data.TryBuildingIndex("bld.towncenter", out int tc) ? tc : -1;
            for (int p = 0; p < Players.Length && p < mapDef.starts.Count; p++)
            {
                StartPositionDef s = mapDef.starts[p];
                if (tcIndex >= 0)
                {
                    BakedBuilding b = Defs.Buildings[tcIndex];
                    int x = s.x - b.W / 2, y = s.y - b.H / 2;
                    if (Map.Validate(x, y, b.W, b.H, b.TerrainMask) == PlacementResult.Ok)
                        SpawnBuilding(tcIndex, p, x, y, complete: true);
                }
                int civ = Players[p].CivIndex;
                if (civ < 0) continue;
                foreach (SpawnDef sd in Defs.Data.Civs[civ].startingUnits)
                {
                    if (!Defs.Data.TryUnitIndex(sd.id, out int ui)) continue;
                    for (int k = 0; k < sd.count; k++)
                    {
                        // Spread the starting units in a ring below the town center.
                        int ox = (k % 3) - 1, oy = -4 - (k / 3);
                        SpawnUnit(ui, p, FixVec2.CellCenter(s.x + ox * 2, s.y + oy));
                    }
                }
            }
        }
    }
}
