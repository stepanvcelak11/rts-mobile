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
        public AiDifficulty[] Ai = { AiDifficulty.None, AiDifficulty.Normal };
        public bool SpawnStartingUnits = true;

        public AiDifficulty AiFor(int player) => Ai != null && player < Ai.Length ? Ai[player] : AiDifficulty.None;
    }

    /// <summary>
    /// The whole deterministic game state plus the systems that advance it. The only way the
    /// state changes is <see cref="Step"/>. No UnityEngine, no floats, no wall clock.
    /// </summary>
    public sealed class World
    {
        /// <summary>Base definitions (no civ modifiers). Use <see cref="DefsOf"/> for anything owned by a player.</summary>
        public readonly BakedDefs Defs;
        public readonly WorldConfig Config;
        public readonly GridMap Map;
        public readonly DetRandom Rng;
        public readonly PlayerState[] Players;
        public readonly FlowFieldCache Fields = new FlowFieldCache();
        public readonly UnitGrid Units;

        public int Tick { get; private set; }
        public ulong LastHash { get; private set; }
        /// <summary>-1 while the match runs, otherwise the winning player (or -2 for a draw).</summary>
        public int Winner { get; internal set; } = -1;

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
        public readonly ComponentStore<Turret> Turrets = new ComponentStore<Turret>();
        public readonly ComponentStore<Projectile> Projectiles = new ComponentStore<Projectile>();
        public readonly ComponentStore<Research> Researches = new ComponentStore<Research>();

        /// <summary>Events raised during the last <see cref="Step"/>. Read-only for presentation.</summary>
        public readonly List<SimEvent> Events = new List<SimEvent>(64);

        private readonly List<ISystem> _systems = new List<ISystem>();
        private readonly List<int> _pendingDespawn = new List<int>();
        private readonly List<ICommand> _aiCommands = new List<ICommand>();
        private int _nextEntity = 1;   // ids are never reused within a match

        public World(GameData data, WorldConfig config)
        {
            Defs = new BakedDefs(data);
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Rng = new DetRandom(config.Seed);

            MapDef mapDef = data.Maps.Count > 0 ? data.Maps[data.MapIndex(config.MapId)] : new MapDef();
            Map = GridMap.FromDef(mapDef);
            Units = new UnitGrid(Map.Width, Map.Height);

            Players = new PlayerState[config.PlayerCount];
            for (int p = 0; p < Players.Length; p++)
            {
                string civId = p < config.CivIds.Length ? config.CivIds[p] : config.CivIds[0];
                int civ = data.Civs.Count > 0 ? data.CivIndex(civId) : -1;
                var ps = new PlayerState(p, civ, Defs.ResourceCount, Defs.Techs.Length) { Defs = Defs.CloneForPlayer(), Ai = config.AiFor(p) };
                Cost start = civ >= 0 ? data.Civs[civ].startingStockpile : data.Economy.startingStockpile;
                foreach (var kv in start)
                    if (data.TryResourceIndex(kv.Key, out int ri)) ps.Stockpile[ri] = Fix64.FromDecimal(kv.Value);
                ps.PopulationCap = Defs.PopulationCapBase;
                if (civ >= 0) Modifiers.ApplyCiv(ps.Defs, data.Civs[civ]);
                Players[p] = ps;
            }

            // Fixed system order — see docs/01-ARCHITECTURE.md §3.2.
            _systems.Add(new AISystem());
            _systems.Add(new ProductionSystem());
            _systems.Add(new EconomySystem());
            _systems.Add(new BehaviorSystem());
            _systems.Add(new MovementSystem());
            _systems.Add(new CombatSystem());
            _systems.Add(new DeathSystem());
            _systems.Add(new VictorySystem());

            SpawnMapContent(mapDef);
            LastHash = ComputeHash();
        }

        /// <summary>Definitions as seen by a player (civ passives + researched techs). Neutral → base.</summary>
        public BakedDefs DefsOf(int player) => player >= 0 && player < Players.Length ? Players[player].Defs : Defs;

        public BakedUnit UnitDefOf(int entity)
        {
            Identity id = Identities.Get(entity);
            return DefsOf(id.Player).Units[id.DefIndex];
        }

        public BakedBuilding BuildingDefOf(int entity)
        {
            Identity id = Identities.Get(entity);
            return DefsOf(id.Player).Buildings[id.DefIndex];
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
            // AI commands queued during the previous tick run after the humans' commands.
            for (int i = 0; i < _aiCommands.Count; i++) _aiCommands[i].Execute(this);
            _aiCommands.Clear();

            Units.Rebuild(this);
            for (int i = 0; i < _systems.Count; i++) _systems[i].Step(this);

            FlushDespawns();
            Tick++;
            LastHash = ComputeHash();
        }

        /// <summary>Sim-side AI queues its decisions here; they execute at the start of the next tick.</summary>
        public void QueueAiCommand(ICommand command) => _aiCommands.Add(command);

        public ulong ComputeHash()
        {
            Hasher h = Hasher.Create();
            h.Add(Tick);
            h.Add(Winner);
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
            Turrets.Hash(ref h);
            Projectiles.Hash(ref h);
            Researches.Hash(ref h);
            h.Add(_nextEntity);
            return h.Value;
        }

        // ---- entities -------------------------------------------------------------------

        public bool IsAlive(int entity) => Identities.Has(entity);

        public int SpawnUnit(int unitIndex, int player, FixVec2 pos)
        {
            BakedUnit u = DefsOf(player).Units[unitIndex];
            int e = _nextEntity++;
            Identities.Add(e, new Identity { Kind = EntityKind.Unit, DefIndex = unitIndex, Player = player });
            Positions.Add(e, new Position { Value = Map.ClampInside(pos), Radius = u.Radius, Facing = new FixVec2(Fix64.Zero, Fix64.One) });
            Healths.Add(e, new Health { Hp = u.Hp, MaxHp = u.Hp });
            Movers.Add(e, new Mover { Speed = u.Speed });
            Behaviours.Add(e, new UnitBehaviour { State = UnitState.Idle, LeashOrigin = pos });
            if (u.CanGather) Cargos.Add(e, new Cargo { Resource = -1, Capacity = Defs.CarryCapacity });
            if (player >= 0) Players[player].Population += u.Population;
            Events.Add(new SimEvent(SimEventKind.Spawned, e));
            return e;
        }

        /// <summary>Places a building. With <paramref name="complete"/> = false it starts as a construction site.</summary>
        public int SpawnBuilding(int buildingIndex, int player, int x, int y, bool complete)
        {
            BakedBuilding b = DefsOf(player).Buildings[buildingIndex];
            int e = _nextEntity++;
            var fp = new Footprint { X = x, Y = y, W = b.W, H = b.H };
            Identities.Add(e, new Identity { Kind = EntityKind.Building, DefIndex = buildingIndex, Player = player });
            Footprints.Add(e, fp);
            Map.Occupy(fp, e);
            Fields.Invalidate();
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
            if (b.Attack != null) Turrets.Add(entity, new Turret());
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
            Fields.Invalidate();
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

        public int SpawnProjectile(int source, int sourcePlayer, EntityKind sourceKind, int sourceDef, int attackIndex, int target, FixVec2 from, Fix64 speed)
        {
            int e = _nextEntity++;
            FixVec2 to = TargetPoint(target);
            Fix64 dist = FixVec2.Distance(from, to);
            int ticks = Math.Max(1, (dist / FixMath.Max(speed, Fix64.One) * SimConstants.TickRate).CeilToInt());
            Identities.Add(e, new Identity { Kind = EntityKind.Projectile, DefIndex = attackIndex, Player = sourcePlayer });
            Positions.Add(e, new Position { Value = from, Radius = Fix64.Zero, Facing = (to - from).Normalized });
            Projectiles.Add(e, new Projectile
            {
                Source = source, SourcePlayer = sourcePlayer, SourceKind = sourceKind, SourceDef = sourceDef, AttackIndex = attackIndex,
                Target = target, TicksLeft = ticks, TotalTicks = ticks, Start = from,
            });
            Events.Add(new SimEvent(SimEventKind.Spawned, e));
            return e;
        }

        /// <summary>Centre of an entity for aiming: position for units, footprint centre for buildings.</summary>
        public FixVec2 TargetPoint(int entity)
        {
            if (Positions.TryGet(entity, out Position p)) return p.Value;
            if (Footprints.TryGet(entity, out Footprint fp)) return fp.Center;
            return FixVec2.Zero;
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
                if (Footprints.TryGet(e, out Footprint fp)) { Map.Release(fp, e); Fields.Invalidate(); }
                if (id.Kind == EntityKind.Unit && id.Player >= 0)
                    Players[id.Player].Population -= DefsOf(id.Player).Units[id.DefIndex].Population;
                if (id.Kind == EntityKind.Building && id.Player >= 0 && !Constructions.Has(e))
                {
                    int provided = DefsOf(id.Player).Buildings[id.DefIndex].PopulationProvided;
                    if (provided > 0) Players[id.Player].PopulationCap = Math.Max(Defs.PopulationCapBase, Players[id.Player].PopulationCap - provided);
                    if (Players[id.Player].AgeUpBuilding == e) { Players[id.Player].AgeUpBuilding = 0; Players[id.Player].AgeUpRemaining = 0; }
                }
                Identities.Remove(e); Positions.Remove(e); Footprints.Remove(e); Healths.Remove(e);
                Movers.Remove(e); Behaviours.Remove(e); Cargos.Remove(e); Nodes.Remove(e);
                Constructions.Remove(e); Queues.Remove(e); Turrets.Remove(e); Projectiles.Remove(e); Researches.Remove(e);
                Events.Add(new SimEvent(SimEventKind.Despawned, e));
            }
            _pendingDespawn.Clear();
        }

        // ---- queries ------------------------------------------------------------------------

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
                BakedBuilding b = DefsOf(player).Buildings[id.DefIndex];
                if (resource < 0 || resource >= b.DropOff.Length || !b.DropOff[resource]) continue;
                Fix64 d = Footprints.Get(e).DistanceSqTo(from);
                if (d < bestD || (d == bestD && e < best)) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>First completed building of a def for a player (e.g. the town center), 0 if none.</summary>
        public int FindBuilding(int player, int buildingIndex, bool allowSites = false)
        {
            for (int i = 0; i < Identities.Count; i++)
            {
                ref Identity id = ref Identities.At(i);
                if (id.Kind != EntityKind.Building || id.Player != player || id.DefIndex != buildingIndex) continue;
                int e = Identities.EntityAt(i);
                if (allowSites || !Constructions.Has(e)) return e;
            }
            return 0;
        }

        /// <summary>Entity under a world point: buildings/nodes by footprint, units by radius (+fat finger). 0 if none.</summary>
        public int PickAt(FixVec2 p, Fix64 unitPickRadius)
        {
            int occupant = Map.OccupantAt(p.CellX, p.CellY);
            int best = 0;
            Fix64 bestD = Fix64.MaxValue;
            for (int i = 0; i < Movers.Count; i++)
            {
                int e = Movers.EntityAt(i);
                ref Position pos = ref Positions.Get(e);
                Fix64 reach = pos.Radius + unitPickRadius;
                Fix64 d = FixVec2.DistanceSq(pos.Value, p);
                if (d <= reach * reach && d < bestD) { bestD = d; best = e; }
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

        public int CountUnits(int player, int unitIndex = -1)
        {
            int n = 0;
            for (int i = 0; i < Identities.Count; i++)
            {
                ref Identity id = ref Identities.At(i);
                if (id.Kind == EntityKind.Unit && id.Player == player && (unitIndex < 0 || id.DefIndex == unitIndex)) n++;
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

        /// <summary>Edge-to-edge distance between a unit and any entity (unit radius or footprint). MaxValue if unknown.</summary>
        public Fix64 EdgeDistance(int unit, int target)
        {
            Position a = Positions.Get(unit);
            if (Positions.TryGet(target, out Position b))
            {
                Fix64 d = FixVec2.Distance(a.Value, b.Value) - a.Radius - b.Radius;
                return d <= Fix64.Zero ? Fix64.Zero : d;
            }
            if (Footprints.TryGet(target, out Footprint fp))
            {
                Fix64 d = FixMath.Sqrt(fp.DistanceSqTo(a.Value)) - a.Radius;
                return d <= Fix64.Zero ? Fix64.Zero : d;
            }
            return Fix64.MaxValue;
        }

        public static bool AreEnemies(int player, int otherPlayer) => player >= 0 && otherPlayer >= 0 && player != otherPlayer;

        /// <summary>Awards Home-City experience to a player.</summary>
        public void AddXp(int player, Fix64 amount)
        {
            if (player < 0 || player >= Players.Length || amount <= Fix64.Zero) return;
            Players[player].Xp += amount;
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
                    BakedBuilding b = DefsOf(p).Buildings[tcIndex];
                    int x = s.x - b.W / 2, y = s.y - b.H / 2;
                    if (Map.Validate(x, y, b.W, b.H, b.TerrainMask) == PlacementResult.Ok)
                        SpawnBuilding(tcIndex, p, x, y, complete: true);
                }
                int civ = Players[p].CivIndex;
                if (civ < 0) continue;
                foreach (SpawnDef sd in Defs.Data.Civs[civ].startingUnits)
                {
                    if (!Defs.Data.TryUnitIndex(sd.id, out int ui)) continue;
                    ui = DefsOf(p).Replace(ui);
                    for (int k = 0; k < sd.count; k++)
                    {
                        // Spread the starting units in rows below the town center.
                        int ox = (k % 3) - 1, oy = -4 - (k / 3);
                        SpawnUnit(ui, p, FixVec2.CellCenter(s.x + ox * 2, s.y + oy));
                    }
                }
            }
        }
    }
}
