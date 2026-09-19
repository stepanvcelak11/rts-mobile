using System.Collections.Generic;
using NUnit.Framework;
using RTS.Data;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Tests
{
    /// <summary>Generated maps, formations, stances, rally points, farms and the market.</summary>
    public class ContentTests
    {
        private static WorldConfig Gen(string type, uint seed, int size = 80) => new WorldConfig
        {
            Seed = seed, PlayerCount = 2, MapType = type, MapSize = size, CivIds = new[] { "civ.crown", "civ.sun" },
            Ai = new[] { AiDifficulty.None, AiDifficulty.None },
        };

        [Test]
        public void MapGenerator_IsDeterministic_FairAndConnected()
        {
            foreach (string type in MapGenerator.Types)
            {
                MapDef a = MapGenerator.Generate(type, 42, 80);
                MapDef b = MapGenerator.Generate(type, 42, 80);
                Assert.AreEqual(a.patches.Count, b.patches.Count, type);
                Assert.AreEqual(a.nodes.Count, b.nodes.Count, type);
                Assert.AreEqual(2, a.starts.Count);
                Assert.That(a.nodes.Count, Is.GreaterThan(80), type + " should have plenty of resources");

                var m = new MatchRunner(TestWorld.Data, Gen(type, 42), new LocalCommandSource());
                World w = m.World;
                for (int p = 0; p < 2; p++)
                {
                    int tc = w.FindBuilding(p, w.Defs.Data.BuildingIndex("bld.towncenter"));
                    Assert.That(tc, Is.GreaterThan(0), type + ": player " + p + " needs a town center");
                    FixVec2 home = w.Footprints.Get(tc).Center;
                    Assert.That(w.FindNearestNode(home, w.Defs.Data.ResourceIndex("food"), Fix64.FromInt(14)), Is.GreaterThan(0), type + ": food near start");
                    Assert.That(w.FindNearestNode(home, w.Defs.Data.ResourceIndex("wood"), Fix64.FromInt(16)), Is.GreaterThan(0), type + ": wood near start");
                    Assert.That(w.FindNearestNode(home, w.Defs.Data.ResourceIndex("gold"), Fix64.FromInt(14)), Is.GreaterThan(0), type + ": gold near start");
                    Assert.AreEqual(6, TestWorld.UnitsOf(w, p, "unit.villager").Length + (p == 1 ? -1 : 0), type + ": villagers spawned");
                }
                // The two starts are connected for walking units.
                int tcA = w.FindBuilding(0, w.Defs.Data.BuildingIndex("bld.towncenter"));
                int tcB = w.FindBuilding(1, w.Defs.Data.BuildingIndex("bld.towncenter"));
                FlowField f = w.Fields.ForEntity(w, tcB);
                Assert.IsNotNull(f);
                FixVec2 a0 = w.Positions.Get(TestWorld.UnitsOf(w, 0, "unit.villager")[0]).Value;
                Assert.IsTrue(f.IsReachable(a0.CellX, a0.CellY), type + ": starts must be connected");
                m.RunTicks(40);   // nothing explodes
            }
        }

        [Test]
        public void GroupMove_UsesFormationSlots()
        {
            var m = new MatchRunner(TestWorld.Data, Gen("greatPlains", 3), new LocalCommandSource());
            World w = m.World;
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            FixVec2 target = w.Positions.Get(v[0]).Value + new FixVec2(Fix64.FromInt(-6), Fix64.FromInt(-6));
            m.Source.Submit(new MoveCommand(0, v, target));
            m.RunTicks(2);
            var targets = new HashSet<string>();
            foreach (int u in v) targets.Add(w.Behaviours.Get(u).TargetPos.ToString());
            Assert.That(targets.Count, Is.GreaterThanOrEqualTo(v.Length - 2), "units get their own formation slots (a few may share a fallback cell)");
            m.RunTicks(20 * 8);
            foreach (int u in v)
            {
                Assert.AreEqual(UnitState.Idle, w.Behaviours.Get(u).State);
                Assert.IsTrue(FixMath.WithinDistance(w.Positions.Get(u).Value, target, Fix64.FromInt(3)));
            }
        }

        [Test]
        public void Stances_ChangeAutoEngagement()
        {
            var m = new MatchRunner(TestWorld.Data, Gen("greatPlains", 4), new LocalCommandSource());
            World w = m.World;
            int musk = w.Defs.Data.UnitIndex("unit.musketeer");
            int passive = w.SpawnUnit(musk, 0, FixVec2.CellCenter(40, 40));
            int normal = w.SpawnUnit(musk, 0, FixVec2.CellCenter(40, 30));
            int baitA = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.villager"), 1, FixVec2.CellCenter(46, 40));
            int baitB = w.SpawnUnit(w.Defs.Data.UnitIndex("unit.villager"), 1, FixVec2.CellCenter(46, 30));
            m.Source.Submit(new StanceCommand(0, new[] { passive }, Stance.Passive));
            m.RunTicks(20 * 4);
            Assert.AreEqual(UnitState.Idle, w.Behaviours.Get(passive).State, "passive units ignore enemies");
            Assert.AreEqual(UnitState.Attack, w.Behaviours.Get(normal).State, "default stance engages");
            Assert.AreEqual(Stance.Passive, w.Behaviours.Get(passive).Stance);
        }

        [Test]
        public void RallyPoint_SendsTrainedUnits()
        {
            var m = new MatchRunner(TestWorld.Data, Gen("greatPlains", 5), new LocalCommandSource());
            World w = m.World;
            int tc = w.FindBuilding(0, w.Defs.Data.BuildingIndex("bld.towncenter"));
            FixVec2 rally = w.Footprints.Get(tc).Center + new FixVec2(Fix64.FromInt(-9), Fix64.FromInt(-2));
            int before = TestWorld.UnitsOf(w, 0, "unit.villager").Length;
            m.Source.Submit(new RallyCommand(0, tc, rally));
            m.Source.Submit(new TrainCommand(0, tc, w.Defs.Data.UnitIndex("unit.villager")));
            m.RunTicks(25 * 20 + 3);
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            Assert.AreEqual(before + 1, villagers.Length);
            int newest = villagers[villagers.Length - 1];
            UnitState s = w.Behaviours.Get(newest).State;
            Assert.IsTrue(s == UnitState.Move || FixMath.WithinDistance(w.Positions.Get(newest).Value, rally, Fix64.FromInt(2)), "new unit heads to the rally point, was " + s);
        }

        [Test]
        public void Mill_IsAnInfiniteFoodSource()
        {
            var m = new MatchRunner(TestWorld.Data, Gen("greatPlains", 6), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int food = data.ResourceIndex("food");
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            FixVec2 home = w.Positions.Get(v[0]).Value;
            int mill = w.SpawnBuilding(data.BuildingIndex("bld.mill"), 0, home.CellX + 2, home.CellY - 5, complete: true);
            Assert.IsTrue(w.Nodes.Has(mill), "a completed mill doubles as a farm node");
            Assert.AreEqual(mill, w.FindNearestNode(home, food, Fix64.FromInt(10)));
            Fix64 before = w.Players[0].Stockpile[food];
            m.Source.Submit(new GatherCommand(0, new[] { v[0], v[1] }, mill));
            m.RunTicks(20 * 60);
            Assert.That(w.Players[0].Stockpile[food], Is.GreaterThan(before + Fix64.FromInt(30)));
            Assert.IsTrue(w.IsAlive(mill));
        }

        [Test]
        public void Market_BuysAndSellsWithDriftingPrices()
        {
            var m = new MatchRunner(TestWorld.Data, Gen("greatPlains", 7), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int wood = data.ResourceIndex("wood"), gold = data.ResourceIndex("gold");
            PlayerState ps = w.Players[0];
            Assert.AreEqual(CommandRejectReason.InvalidTarget, TradeCommand.Validate(w, 0, wood, true), "no market yet");
            ps.Age = 1;
            w.SpawnBuilding(data.BuildingIndex("bld.market"), 0, 30, 30, complete: true);
            ps.Stockpile[gold] = Fix64.FromInt(500);
            ps.Stockpile[wood] = Fix64.FromInt(300);

            m.Source.Submit(new TradeCommand(0, wood, buy: true));
            m.RunTicks(2);
            Assert.AreEqual(Fix64.FromInt(400), ps.Stockpile[wood]);
            Assert.AreEqual(Fix64.FromInt(400), ps.Stockpile[gold]);
            Assert.AreEqual(Fix64.FromInt(103), ps.MarketPrice[wood]);

            m.Source.Submit(new TradeCommand(0, wood, buy: false));
            m.RunTicks(2);
            Assert.AreEqual(Fix64.FromInt(300), ps.Stockpile[wood]);
            TestWorld.AssertFix(400m + 103m * 0.7m, ps.Stockpile[gold]);
            Assert.AreEqual(Fix64.FromInt(100), ps.MarketPrice[wood]);

            for (int i = 0; i < 5; i++) { m.Source.Submit(new TradeCommand(0, wood, buy: false)); m.RunTicks(2); }
            Assert.That(ps.MarketPrice[wood], Is.LessThan(Fix64.FromInt(100)));
            m.RunTicks(20 * 60);
            Assert.That(ps.MarketPrice[wood], Is.GreaterThan(Fix64.FromInt(90)), "prices drift back toward 100");
        }

        [Test]
        public void Treasures_AreGuardedAndPickedUp()
        {
            var m = new MatchRunner(TestWorld.Data, Gen("greatPlains", 8), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int wolves = w.CountUnits(SimConstants.WildPlayer);
            Assert.That(wolves, Is.GreaterThan(0), "generated maps have wolf guardians");
            Assert.IsTrue(World.AreEnemies(0, SimConstants.WildPlayer));
            Assert.IsFalse(World.AreEnemies(SimConstants.WildPlayer, SimConstants.WildPlayer));

            // Drop a treasure next to a villager and send it.
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            FixVec2 at = w.Positions.Get(v[0]).Value;
            int gold = data.ResourceIndex("gold");
            int t = w.SpawnResourceNode(data.NodeIndex("res.treasure_gold"), at.CellX + 2, at.CellY, Fix64.FromInt(200));
            Fix64 before = w.Players[0].Stockpile[gold];
            Fix64 xpBefore = w.Players[0].Xp;
            m.Source.Submit(new GatherCommand(0, new[] { v[0] }, t));
            m.RunTicks(20 * 6);
            Assert.IsFalse(w.IsAlive(t), "treasure is consumed");
            // At least the treasure; the villager may already be trickling gold from a nearby mine.
            Assert.That(w.Players[0].Stockpile[gold], Is.GreaterThanOrEqualTo(before + Fix64.FromInt(200)));
            Assert.That(w.Players[0].Xp, Is.GreaterThan(xpBefore));
        }

        [Test]
        public void AgeUp_AppliesTheChosenBonus()
        {
            var m = new MatchRunner(TestWorld.Data, Gen("greatPlains", 9), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int tc = w.FindBuilding(0, data.BuildingIndex("bld.towncenter"));
            w.Players[0].Stockpile[data.ResourceIndex("food")] = Fix64.FromInt(1000);
            int villagersBefore = TestWorld.UnitsOf(w, 0, "unit.villager").Length;
            m.Source.Submit(new AgeUpCommand(0, tc, 0));   // The Naturalist: +3 villagers
            m.RunTicks(60 * 20 + 5);
            Assert.AreEqual(1, w.Players[0].Age);
            Assert.AreEqual(villagersBefore + 3, TestWorld.UnitsOf(w, 0, "unit.villager").Length);
        }

        [Test]
        public void AiOnGeneratedMaps_StaysDeterministic()
        {
            ulong Run()
            {
                WorldConfig c = Gen("highlands", 11, 96);
                c.Ai = new[] { AiDifficulty.Hard, AiDifficulty.Normal };
                var r = new MatchRunner(TestWorld.Data, c, new LocalCommandSource());
                r.RunTicks(20 * 300);
                return r.World.LastHash;
            }
            Assert.AreEqual(Run(), Run());
        }
    }
}
