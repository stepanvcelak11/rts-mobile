using System.Collections.Generic;
using NUnit.Framework;
using RTS.Data;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;
using RTS.Sim.Systems;

namespace RTS.Tests
{
    public class CombatAndPathTests
    {
        private static WorldConfig NoAi(uint seed = 1)
        {
            WorldConfig c = TestWorld.DefaultConfig(seed);
            c.Ai = new[] { AiDifficulty.None, AiDifficulty.None };
            return c;
        }

        [Test]
        public void FlowField_RoutesAroundWall()
        {
            var map = new GridMap(20, 20);
            for (int wy = 0; wy < 16; wy++) map.Occupy(new Footprint { X = 10, Y = wy, W = 1, H = 1 }, 99);
            var goals = new List<int> { 5 * 20 + 15 };
            FlowField f = FlowField.Build(map, goals, 1);

            Assert.IsTrue(f.IsGoal(15, 5));
            Assert.IsTrue(f.IsReachable(5, 5));
            Assert.IsFalse(f.IsReachable(10, 5));
            // Walk the field from (5,5); it must reach the goal without touching the wall.
            int x = 5, y = 5, steps = 0;
            while (!f.IsGoal(x, y) && steps < 200)
            {
                Assert.IsTrue(f.TryNext(x, y, out FixVec2 next));
                x = next.CellX; y = next.CellY;
                Assert.IsTrue(map.IsPassable(x, y), "stepped into the wall at {0},{1}", x, y);
                steps++;
            }
            Assert.IsTrue(f.IsGoal(x, y));
            Assert.That(steps, Is.GreaterThan(15).And.LessThan(40));   // detour above the wall (y ≥ 16)
        }

        [Test]
        public void Units_WalkAroundObstaclesToTheirTarget()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            // Build a wall of trees between the villager and a point on the far side.
            int tree = w.Defs.Data.NodeIndex("res.tree");
            FixVec2 start = w.Positions.Get(v[0]).Value;
            int wx = start.CellX + 3;
            for (int y = start.CellY - 6; y <= start.CellY + 6; y++)
                if (w.Map.Validate(wx, y, 1, 1, World.WalkableMask) == PlacementResult.Ok) w.SpawnResourceNode(tree, wx, y, Fix64.FromInt(-1));
            FixVec2 target = FixVec2.CellCenter(wx + 4, start.CellY);

            m.Source.Submit(new MoveCommand(0, new[] { v[0] }, target));
            m.RunTicks(20 * 12);
            Assert.AreEqual(UnitState.Idle, w.Behaviours.Get(v[0]).State);
            Assert.IsTrue(FixMath.WithinDistance(w.Positions.Get(v[0]).Value, target, Fix64.FromInt(2)),
                "villager ended at {0}, wanted {1}", w.Positions.Get(v[0]).Value, target);
        }

        [Test]
        public void Group_SpreadsOutInsteadOfStacking()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            FixVec2 target = w.Positions.Get(v[0]).Value + new FixVec2(Fix64.FromInt(-6), Fix64.FromInt(-4));
            m.Source.Submit(new MoveCommand(0, v, target));
            m.RunTicks(20 * 8);
            for (int i = 0; i < v.Length; i++)
            {
                Assert.AreEqual(UnitState.Idle, w.Behaviours.Get(v[i]).State, "villager {0} still moving", i);
                for (int j = i + 1; j < v.Length; j++)
                {
                    Fix64 d = FixVec2.Distance(w.Positions.Get(v[i]).Value, w.Positions.Get(v[j]).Value);
                    Assert.That(d.ToDecimal(), Is.GreaterThan(0.45m), "villagers {0} and {1} overlap", i, j);
                }
            }
        }

        [Test]
        public void Damage_UsesMultipliersAndArmor()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            int hussar = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.hussar"), 1, FixVec2.FromInts(30, 30));
            BakedUnit pike = w.DefsOf(0).Units[w.Defs.Data.UnitIndex("unit.pikeman")];
            Fix64 before = w.Healths.Get(hussar).Hp;
            CombatSystem.ApplyDamage(w, hussar, 0, 0, pike.Attacks[0]);
            // 8 × 5.0 (vs cavalry) × (1 − 0.2 melee armor) = 32
            TestWorld.AssertFix(32m, before - w.Healths.Get(hussar).Hp);

            int house = w.SpawnBuilding(w.Defs.Data.BuildingIndex("bld.house"), 1, 40, 40, true);
            before = w.Healths.Get(house).Hp;
            CombatSystem.ApplyDamage(w, house, 0, 0, pike.Attacks[0]);
            // 8 × 3.0 (vs building) × (1 − 0.6 melee armor) = 9.6
            TestWorld.AssertFix(9.6m, before - w.Healths.Get(house).Hp);
        }

        [Test]
        public void Musketeers_KillVillagerAndEarnXp()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            int musk = w.Defs.Data.UnitIndex("unit.musketeer");
            var squad = new List<int>();
            for (int i = 0; i < 4; i++) squad.Add(w.SpawnUnit(musk, 0, FixVec2.CellCenter(30 + i, 20)));
            int victim = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.villager"), 1, FixVec2.CellCenter(32, 26));
            Fix64 xpBefore = w.Players[0].Xp;

            m.Source.Submit(new AttackCommand(0, squad.ToArray(), victim));
            List<SimEvent> events = TestWorld.RunCollecting(m, 20 * 30);

            Assert.IsFalse(w.IsAlive(victim));
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.Died));
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.ProjectileHit), "musketeers fire projectiles");
            Assert.AreEqual(1, w.Players[0].UnitsKilled);
            Assert.AreEqual(1, w.Players[1].UnitsLost);
            Assert.That(w.Players[0].Xp, Is.GreaterThan(xpBefore));
            Assert.AreEqual(0, w.Projectiles.Count, "no projectiles linger after the fight");
            foreach (int s in squad) Assert.AreEqual(UnitState.Idle, w.Behaviours.Get(s).State);
        }

        [Test]
        public void Villager_FleesWhenHurt_AndTownCenterShootsBack()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            int tc = w.FindBuilding(0, w.Defs.Data.BuildingIndex("bld.towncenter"));
            Footprint fp = w.Footprints.Get(tc);
            // One enemy hussar rides in next to the villagers (inside town center range).
            int raider = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.hussar"), 1, FixVec2.CellCenter(fp.X + 3, fp.Y - 6));
            Fix64 raiderHp = w.Healths.Get(raider).Hp;

            List<SimEvent> events = TestWorld.RunCollecting(m, 20 * 20);

            Assert.That(w.Healths.Get(raider).Hp, Is.LessThan(raiderHp), "town center should have shot the raider");
            bool fled = false;
            foreach (int v in villagers)
                if (w.IsAlive(v) && w.Behaviours.Get(v).State == UnitState.Flee) fled = true;
            foreach (SimEvent ev in events) if (ev.Kind == SimEventKind.StateChanged && ev.A == (int)UnitState.Flee) fled = true;
            Assert.IsTrue(fled, "a hurt villager should run for the town center");
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.UnderAttack));
        }

        [Test]
        public void AttackMove_EngagesEnemiesOnTheWay_AndLeashHolds()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            int musk = w.Defs.Data.UnitIndex("unit.musketeer");
            var squad = new List<int>();
            for (int i = 0; i < 6; i++) squad.Add(w.SpawnUnit(musk, 0, FixVec2.CellCenter(26 + i, 18)));
            int enemyA = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.pikeman"), 1, FixVec2.CellCenter(30, 26));
            int enemyB = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.pikeman"), 1, FixVec2.CellCenter(31, 27));

            m.Source.Submit(new AttackMoveCommand(0, squad.ToArray(), FixVec2.CellCenter(30, 40)));
            m.RunTicks(20 * 40);
            Assert.IsFalse(w.IsAlive(enemyA));
            Assert.IsFalse(w.IsAlive(enemyB));
            // After the fight they resume the march and eventually arrive.
            bool anyArrived = false;
            foreach (int s in squad)
                if (FixMath.WithinDistance(w.Positions.Get(s).Value, FixVec2.CellCenter(30, 40), Fix64.FromInt(4))) anyArrived = true;
            Assert.IsTrue(anyArrived, "squad should continue to the attack-move destination");

            // Idle defensive unit auto-engages a nearby enemy but returns when it runs out of leash.
            int guard = w.SpawnUnit(musk, 0, FixVec2.CellCenter(10, 40));
            int bait = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.hussar"), 1, FixVec2.CellCenter(10, 50));
            m.RunTicks(20 * 3);
            Assert.AreEqual(UnitState.Attack, w.Behaviours.Get(guard).State);
        }

        [Test]
        public void Building_DiesAndFreesTheGround()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            int house = w.SpawnBuilding(w.Defs.Data.BuildingIndex("bld.house"), 1, 30, 30, true);
            int pikeDef = w.Defs.Data.UnitIndex("unit.pikeman");
            var squad = new List<int>();
            for (int i = 0; i < 6; i++) squad.Add(w.SpawnUnit(pikeDef, 0, FixVec2.CellCenter(26, 26 + i)));
            m.Source.Submit(new AttackCommand(0, squad.ToArray(), house));
            List<SimEvent> events = TestWorld.RunCollecting(m, 20 * 60);
            Assert.IsFalse(w.IsAlive(house), "house hp 800, 6 pikemen at 9.6/1.5 s should raze it in under a minute");
            Assert.IsTrue(w.Map.IsPassable(30, 30));
            Assert.AreEqual(1, w.Players[0].BuildingsRazed);
        }

        [Test]
        public void Victory_WhenEnemyHasNoBuildingsAndNoVillagers()
        {
            var m = new MatchRunner(TestWorld.Data, NoAi(), new LocalCommandSource());
            World w = m.World;
            foreach (int e in AllOf(w, 1)) w.Despawn(e);
            List<SimEvent> events = TestWorld.RunCollecting(m, 25);
            Assert.AreEqual(0, w.Winner);
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.MatchEnded));
            Assert.IsFalse(w.Players[1].Alive);
        }

        private static List<int> AllOf(World w, int player)
        {
            var list = new List<int>();
            for (int i = 0; i < w.Identities.Count; i++)
                if (w.Identities.At(i).Player == player) list.Add(w.Identities.EntityAt(i));
            return list;
        }
    }
}
