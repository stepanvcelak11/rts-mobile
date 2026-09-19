using System.Collections.Generic;
using RTS.Data;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// Rule-based skirmish opponent that lives inside the simulation: it only reads the world
    /// and queues ordinary commands, so it is deterministic and runs identically on every peer.
    /// Thinks once per second per AI player (staggered). Priorities: economy → housing →
    /// villagers → age → military buildings → army → techs/shipments → attack waves / defence.
    /// </summary>
    public sealed class AISystem : ISystem
    {
        private sealed class Profile
        {
            public int TargetVillagers, WaveSize, AgeUpVillagers, ReserveFood;
            public int WaveCooldownTicks;
            public bool ResearchTechs;
        }

        private static readonly Profile Easy = new Profile { TargetVillagers = 12, WaveSize = 6, AgeUpVillagers = 12, ReserveFood = 150, WaveCooldownTicks = 20 * 180, ResearchTechs = false };
        private static readonly Profile Normal = new Profile { TargetVillagers = 18, WaveSize = 10, AgeUpVillagers = 10, ReserveFood = 100, WaveCooldownTicks = 20 * 120, ResearchTechs = true };
        private static readonly Profile Hard = new Profile { TargetVillagers = 26, WaveSize = 14, AgeUpVillagers = 8, ReserveFood = 50, WaveCooldownTicks = 20 * 75, ResearchTechs = true };

        private readonly List<int> _idleVillagers = new List<int>();
        private readonly List<int> _army = new List<int>();
        private readonly int[] _gatherers = new int[8];

        public void Step(World w)
        {
            if (w.Winner != -1) return;
            for (int p = 0; p < w.Players.Length; p++)
            {
                PlayerState ps = w.Players[p];
                if (!ps.IsAi || !ps.Alive) continue;
                if ((w.Tick + p * 7) % 20 != 0) continue;
                Think(w, ps, ps.Ai == AiDifficulty.Easy ? Easy : ps.Ai == AiDifficulty.Hard ? Hard : Normal);
            }
        }

        private void Think(World w, PlayerState ps, Profile prof)
        {
            int p = ps.Index;
            GameData data = w.Defs.Data;
            BakedDefs defs = ps.Defs;
            if (!data.TryBuildingIndex("bld.towncenter", out int tcIndex)) return;
            int tc = w.FindBuilding(p, tcIndex);
            if (tc == 0) { DefendWithEverything(w, ps); return; }
            FixVec2 home = w.Footprints.Get(tc).Center;

            int villagerIndex = FindVillagerDef(defs);
            int villagers = 0, army = 0;
            _idleVillagers.Clear();
            _army.Clear();
            System.Array.Clear(_gatherers, 0, _gatherers.Length);
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind != EntityKind.Unit || id.Player != p) continue;
                int e = w.Identities.EntityAt(i);
                BakedUnit u = defs.Units[id.DefIndex];
                UnitBehaviour b = w.Behaviours.Get(e);
                if (u.CanGather)
                {
                    villagers++;
                    if (b.State == UnitState.Idle) _idleVillagers.Add(e);
                    else if ((b.State == UnitState.Gather || b.State == UnitState.ReturnCargo) && w.Nodes.TryGet(b.LastNode, out ResourceNode n) && n.Resource >= 0 && n.Resource < _gatherers.Length)
                        _gatherers[n.Resource]++;
                }
                else if (u.CanAttack)
                {
                    army++;
                    _army.Add(e);
                }
            }

            // 1. Put idle villagers to work following a target split that shifts with the age.
            AssignVillagers(w, ps, home, villagers);

            // 1b. Farms once the wild food near home is gone (or the economy is big).
            if (data.TryBuildingIndex("bld.mill", out int millIndex))
            {
                int mills = w.CountBuildings(p, millIndex, true);
                bool foodNearby = w.FindNearestNode(home, data.ResourceIndex("food"), Fix64.FromInt(28)) != 0;
                if ((!foodNearby && mills < 3) || (villagers >= 18 && mills < 1) || (villagers >= 24 && mills < 2))
                    TryBuild(w, ps, millIndex, home, 3, 9);
            }

            // 2. Housing.
            if (ps.PopulationCap - ps.Population <= 3 && ps.PopulationCap < w.Defs.PopulationCapMax
                && data.TryBuildingIndex("bld.house", out int house) && w.CountBuildings(p, house, true) - w.CountBuildings(p, house, false) == 0)
                TryBuild(w, ps, house, home, 3, 12);

            // 3. Villagers — but bank food for the age-up first, and once soldiers can be trained
            //    keep a food reserve so the army is not starved by the town center.
            bool savingForAge = ps.Age == 0 && villagers >= prof.AgeUpVillagers && ps.AgeUpBuilding == 0;
            int foodIdx = data.ResourceIndex("food");
            Fix64 ageFood = w.Defs.Ages.Length > 1 ? w.Defs.Ages[1].Cost[foodIdx] : Fix64.Zero;
            int militaryBuildings = CountMilitaryBuildings(w, p);
            bool armyNeeded = militaryBuildings > 0 && army < prof.WaveSize && villagers >= prof.AgeUpVillagers + 4;
            Fix64 villagerFood = villagerIndex >= 0 ? defs.Units[villagerIndex].Cost[foodIdx] : Fix64.Zero;
            bool foodOk = savingForAge ? ps.Stockpile[foodIdx] - villagerFood >= ageFood
                        : armyNeeded ? ps.Stockpile[foodIdx] - villagerFood >= Fix64.FromInt(300)
                        : true;
            if (villagerIndex >= 0 && villagers < prof.TargetVillagers && w.Queues.Get(tc).Count < 2 && foodOk)
                if (TrainCommand.Validate(w, p, tc, villagerIndex) == CommandRejectReason.None)
                    w.QueueAiCommand(new TrainCommand(p, tc, villagerIndex));

            // 4. Age up.
            if (ps.Age == 0 && villagers >= prof.AgeUpVillagers && AgeUpCommand.Validate(w, p, tc) == CommandRejectReason.None)
                w.QueueAiCommand(new AgeUpCommand(p, tc));
            else if (ps.Age == 1 && villagers >= prof.TargetVillagers - 2 && army >= prof.WaveSize && AgeUpCommand.Validate(w, p, tc) == CommandRejectReason.None)
                w.QueueAiCommand(new AgeUpCommand(p, tc));

            // 5. Military buildings (one of each, after the first age-up).
            if (ps.Age >= 1)
            {
                if (data.TryBuildingIndex("bld.barracks", out int barracks) && w.CountBuildings(p, barracks, true) == 0)
                    TryBuild(w, ps, barracks, home, 6, 14);
                else if (data.TryBuildingIndex("bld.stable", out int stable) && villagers >= 14 && w.CountBuildings(p, stable, true) == 0
                         && w.CountBuildings(p, barracks, false) > 0)
                    TryBuild(w, ps, stable, home, 6, 14);
                else if (data.TryBuildingIndex("bld.market", out int market) && villagers >= 14 && w.CountBuildings(p, market, true) == 0
                         && w.CountBuildings(p, barracks, false) > 0)
                    TryBuild(w, ps, market, home, 4, 10);
                else if (ps.Stockpile[data.ResourceIndex("wood")] >= Fix64.FromInt(500) && w.CountBuildings(p, barracks, true) < (prof.WaveSize >= 14 ? 3 : 2))
                    TryBuild(w, ps, barracks, home, 6, 16);   // spare wood → more production
                else if (data.TryBuildingIndex("bld.tower", out int tower) && ps.Stockpile[data.ResourceIndex("wood")] >= Fix64.FromInt(700) && w.CountBuildings(p, tower, true) < 2)
                    TryBuild(w, ps, tower, home, 8, 12);
            }

            // 6. Army from every completed military building.
            TrainArmy(w, ps, villagers, prof);

            // 7. Techs and shipments.
            if (prof.ResearchTechs) ResearchSomething(w, ps);
            SendShipment(w, ps, villagers, prof);

            // 8. Defence and attack waves.
            bool alerted = w.Tick - ps.AiAlertTick < 20 * 8;
            if (alerted)
            {
                var defenders = new List<int>();
                foreach (int e in _army)
                {
                    UnitBehaviour b = w.Behaviours.Get(e);
                    if (b.State == UnitState.Idle || b.State == UnitState.Move) defenders.Add(e);
                }
                if (defenders.Count > 0) w.QueueAiCommand(new AttackMoveCommand(p, defenders.ToArray(), ps.AiAlertPos));
            }
            else if (army >= prof.WaveSize && w.Tick - ps.AiLastWaveTick >= prof.WaveCooldownTicks)
            {
                int target = FindEnemyTarget(w, p, home);
                if (target != 0)
                {
                    ps.AiLastWaveTick = w.Tick;
                    ps.AiWaveTarget = target;
                    w.QueueAiCommand(new AttackMoveCommand(p, _army.ToArray(), w.TargetPoint(target)));
                }
            }
            else if (ps.AiWaveTarget != 0 && !w.Identities.Has(ps.AiWaveTarget))
            {
                // Wave target destroyed: pick the next one so the army keeps rolling.
                int target = FindEnemyTarget(w, p, home);
                ps.AiWaveTarget = target;
                var idle = new List<int>();
                foreach (int e in _army) if (w.Behaviours.Get(e).State == UnitState.Idle) idle.Add(e);
                if (target != 0 && idle.Count > 0) w.QueueAiCommand(new AttackMoveCommand(p, idle.ToArray(), w.TargetPoint(target)));
                else if (target == 0 && idle.Count > 0) w.QueueAiCommand(new MoveCommand(p, idle.ToArray(), home + new FixVec2(Fix64.Zero, Fix64.FromInt(-6))));
            }
            else
            {
                // Rally idle soldiers that wandered off (e.g. after a repelled attack).
                var far = new List<int>();
                foreach (int e in _army)
                {
                    UnitBehaviour b = w.Behaviours.Get(e);
                    if (b.State == UnitState.Idle && !FixMath.WithinDistance(w.Positions.Get(e).Value, home, Fix64.FromInt(16))) far.Add(e);
                }
                if (far.Count > 0 && w.Tick - ps.AiLastWaveTick > 20 * 60) w.QueueAiCommand(new MoveCommand(p, far.ToArray(), home + new FixVec2(Fix64.Zero, Fix64.FromInt(-6))));
            }
        }

        // ---- economy ----------------------------------------------------------------------

        private void AssignVillagers(World w, PlayerState ps, FixVec2 home, int villagers)
        {
            if (_idleVillagers.Count == 0) return;
            GameData data = w.Defs.Data;
            int food = data.ResourceIndex("food"), wood = data.ResourceIndex("wood"), gold = data.ResourceIndex("gold");
            // Desired split (percent) by age.
            int wantFood = ps.Age == 0 ? 60 : 50, wantWood = ps.Age == 0 ? 30 : 25, wantGold = ps.Age == 0 ? 10 : 25;
            int[] want = new int[w.Defs.ResourceCount];
            want[food] = wantFood; want[wood] = wantWood; want[gold] = wantGold;

            foreach (int v in _idleVillagers)
            {
                int total = System.Math.Max(1, villagers);
                int bestRes = -1; int bestDeficit = int.MinValue;
                for (int r = 0; r < want.Length; r++)
                {
                    int deficit = want[r] - _gatherers[r] * 100 / total;
                    if (deficit > bestDeficit) { bestDeficit = deficit; bestRes = r; }
                }
                // Try the most-needed resource first, then the others.
                bool sent = false;
                for (int attempt = 0; attempt < want.Length && !sent; attempt++)
                {
                    int r = (bestRes + attempt) % want.Length;
                    int node = w.FindNearestNode(home, r, Fix64.FromInt(40));
                    if (node == 0) continue;
                    w.QueueAiCommand(new GatherCommand(ps.Index, new[] { v }, node));
                    _gatherers[r]++;
                    sent = true;
                }
            }
        }

        private static int CountMilitaryBuildings(World w, int player)
        {
            int n = 0;
            for (int i = 0; i < w.Queues.Count; i++)
            {
                int e = w.Queues.EntityAt(i);
                Identity id = w.Identities.Get(e);
                if (id.Player != player) continue;
                BakedBuilding b = w.DefsOf(player).Buildings[id.DefIndex];
                bool trainsSoldiers = false;
                foreach (int t in b.Trains) if (!w.DefsOf(player).Units[t].CanGather) { trainsSoldiers = true; break; }
                if (trainsSoldiers) n++;
            }
            return n;
        }

        private static int FindVillagerDef(BakedDefs defs)
        {
            foreach (BakedUnit u in defs.Units) if (u.CanGather && u.CanBuild) return u.Index;
            return -1;
        }

        // ---- building placement ----------------------------------------------------------

        private void TryBuild(World w, PlayerState ps, int buildingIndex, FixVec2 home, int minDist, int maxDist)
        {
            BakedBuilding b = ps.Defs.Buildings[buildingIndex];
            if (b.Age > ps.Age || !ps.CanAfford(b.Cost)) return;
            if (!FindSpot(w, ps.Index, buildingIndex, home, minDist, maxDist, out int x, out int y)) return;
            // Builders: up to 2 idle villagers, else the nearest 2 gatherers (they will return to work afterwards? no — they idle; AI re-assigns them).
            var builders = new List<int>();
            foreach (int v in _idleVillagers) { builders.Add(v); if (builders.Count == 2) break; }
            if (builders.Count == 0) builders.AddRange(NearestVillagers(w, ps.Index, FixVec2.CellCenter(x, y), 2));
            if (builders.Count == 0) return;
            foreach (int v in builders) _idleVillagers.Remove(v);
            w.QueueAiCommand(new BuildCommand(ps.Index, buildingIndex, x, y, builders.ToArray()));
        }

        /// <summary>Spiral search for a valid spot around home, keeping a one-cell gap from other occupants.</summary>
        public static bool FindSpot(World w, int player, int buildingIndex, FixVec2 home, int minDist, int maxDist, out int x, out int y)
        {
            BakedBuilding b = w.DefsOf(player).Buildings[buildingIndex];
            int hx = home.CellX, hy = home.CellY;
            for (int ring = minDist; ring <= maxDist; ring++)
            {
                for (int dy = -ring; dy <= ring; dy += 2)
                    for (int dx = -ring; dx <= ring; dx += 2)
                    {
                        if (System.Math.Abs(dx) != ring && System.Math.Abs(dy) != ring) continue;
                        x = hx + dx - b.W / 2; y = hy + dy - b.H / 2;
                        if (w.Map.Validate(x - 1, y - 1, b.W + 2, b.H + 2, World.WalkableMask) != PlacementResult.Ok) continue;
                        if (BuildCommand.Validate(w, player, buildingIndex, x, y) == PlacementResult.Ok) return true;
                    }
            }
            x = y = 0;
            return false;
        }

        private static List<int> NearestVillagers(World w, int player, FixVec2 at, int count)
        {
            var list = new List<(Fix64 d, int e)>();
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind != EntityKind.Unit || id.Player != player) continue;
                int e = w.Identities.EntityAt(i);
                if (!w.DefsOf(player).Units[id.DefIndex].CanBuild) continue;
                UnitState s = w.Behaviours.Get(e).State;
                if (s == UnitState.Build || s == UnitState.Flee || s == UnitState.Dead) continue;
                list.Add((FixVec2.DistanceSq(w.Positions.Get(e).Value, at), e));
            }
            list.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.e.CompareTo(b.e));
            var result = new List<int>();
            for (int i = 0; i < list.Count && i < count; i++) result.Add(list[i].e);
            return result;
        }

        // ---- army -------------------------------------------------------------------------

        private static void TrainArmy(World w, PlayerState ps, int villagers, Profile prof)
        {
            int p = ps.Index;
            List<string> preferred = ps.CivIndex >= 0 ? w.Defs.Data.Civs[ps.CivIndex].ai.preferredComp : new List<string>();
            int food = w.Defs.Data.ResourceIndex("food");
            bool keepFoodForVillagers = villagers < prof.TargetVillagers;

            for (int i = 0; i < w.Queues.Count; i++)
            {
                int building = w.Queues.EntityAt(i);
                Identity id = w.Identities.Get(building);
                if (id.Player != p) continue;
                BakedBuilding b = ps.Defs.Buildings[id.DefIndex];
                if (w.Queues.At(i).Count >= 2) continue;

                int pick = -1;
                // Preferred composition first (round-robin by tick), then anything the building trains.
                for (int k = 0; k < preferred.Count && pick < 0; k++)
                {
                    int idx = (k + w.Tick / 20) % preferred.Count;
                    if (!w.Defs.Data.TryUnitIndex(preferred[idx], out int ui)) continue;
                    foreach (int t in b.Trains) if (t == ui || ps.Defs.Replace(t) == ui) { pick = t; break; }
                }
                if (pick < 0)
                    foreach (int t in b.Trains)
                        if (!ps.Defs.Units[ps.Defs.Replace(t)].CanGather) { pick = t; break; }
                if (pick < 0) continue;

                BakedUnit unit = ps.Defs.Units[ps.Defs.Replace(pick)];
                if (keepFoodForVillagers && ps.Stockpile[food] - unit.Cost[food] < Fix64.FromInt(prof.ReserveFood)) continue;
                if (TrainCommand.Validate(w, p, building, pick) == CommandRejectReason.None)
                    w.QueueAiCommand(new TrainCommand(p, building, pick));
            }
        }

        private static void ResearchSomething(World w, PlayerState ps)
        {
            int p = ps.Index;
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind != EntityKind.Building || id.Player != p) continue;
                int building = w.Identities.EntityAt(i);
                BakedBuilding b = ps.Defs.Buildings[id.DefIndex];
                foreach (int tech in b.Researches)
                    if (ResearchCommand.Validate(w, p, building, tech) == CommandRejectReason.None)
                    {
                        w.QueueAiCommand(new ResearchCommand(p, building, tech));
                        return;   // one per think
                    }
            }
        }

        private static void SendShipment(World w, PlayerState ps, int villagers, Profile prof)
        {
            if (ps.ShipmentsAvailable <= 0 || ps.CivIndex < 0) return;
            List<string> deck = w.Defs.Data.Civs[ps.CivIndex].homeCity.deck;
            int best = -1; int bestScore = int.MinValue;
            foreach (string id in deck)
            {
                if (!w.Defs.Data.TryTechIndex(id, out int tech)) continue;
                if (ShipmentCommand.Validate(w, ps.Index, tech) != CommandRejectReason.None) continue;
                TechDef t = w.Defs.Techs[tech].Def;
                int score = 0;
                int food = w.Defs.Data.ResourceIndex("food");
                foreach (SpawnDef s in t.spawns)
                    if (w.Defs.Data.TryUnitIndex(s.id, out int ui) && ps.Defs.Units[ui].CanGather) score += villagers < prof.TargetVillagers - 4 ? 30 : 2;
                    else score += w.Tick - ps.AiAlertTick < 20 * 30 ? 40 : 15;
                foreach (ModifierDef m in t.effects)
                {
                    if (m.target == "player" && m.stat == "stockpile.food") score += ps.Stockpile[food] < Fix64.FromInt(200) ? 45 : 15;
                    else if (m.target == "player") score += 12;
                    else score += 8;
                }
                if (score > bestScore) { bestScore = score; best = tech; }
            }
            if (best >= 0) w.QueueAiCommand(new ShipmentCommand(ps.Index, best));
        }

        // ---- targets ----------------------------------------------------------------------

        private static int FindEnemyTarget(World w, int player, FixVec2 from)
        {
            int best = 0; Fix64 bestD = Fix64.MaxValue;
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind != EntityKind.Building || !World.AreEnemies(player, id.Player)) continue;
                int e = w.Identities.EntityAt(i);
                Fix64 d = w.Footprints.Get(e).DistanceSqTo(from);
                // Prefer town centers slightly so waves go for the throat.
                if (w.DefsOf(id.Player).Buildings[id.DefIndex].Id == "bld.towncenter") d = d * Fix64.Ratio(3, 4);
                if (d < bestD || (d == bestD && e < best)) { bestD = d; best = e; }
            }
            return best;
        }

        private static void DefendWithEverything(World w, PlayerState ps)
        {
            // No town center: every soldier fights whatever is closest; villagers keep gathering.
            var soldiers = new List<int>();
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind != EntityKind.Unit || id.Player != ps.Index) continue;
                int e = w.Identities.EntityAt(i);
                if (w.DefsOf(ps.Index).Units[id.DefIndex].CanAttack && !w.DefsOf(ps.Index).Units[id.DefIndex].CanGather && w.Behaviours.Get(e).State == UnitState.Idle) soldiers.Add(e);
            }
            if (soldiers.Count == 0) return;
            int target = FindEnemyTarget(w, ps.Index, w.Positions.Get(soldiers[0]).Value);
            if (target != 0) w.QueueAiCommand(new AttackMoveCommand(ps.Index, soldiers.ToArray(), w.TargetPoint(target)));
        }
    }
}
