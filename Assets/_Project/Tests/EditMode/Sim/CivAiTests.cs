using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Tests
{
    public class CivAiTests
    {
        private static WorldConfig Config(string civ0, string civ1, AiDifficulty ai0, AiDifficulty ai1, uint seed = 1) => new WorldConfig
        {
            Seed = seed, PlayerCount = 2, MapId = "map.default", CivIds = new[] { civ0, civ1 }, Ai = new[] { ai0, ai1 },
        };

        [Test]
        public void Civilizations_ApplyPassivesAndReplacements()
        {
            var m = new MatchRunner(TestWorld.Data, Config("civ.crown", "civ.compact", AiDifficulty.None, AiDifficulty.None), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int wood = data.ResourceIndex("wood");

            // Crown: infantry +10% hp, houses −20%, musketeer → redcoat.
            BakedDefs crown = w.DefsOf(0);
            TestWorld.AssertFix(165m, crown.Units[data.UnitIndex("unit.musketeer")].Hp);        // 150 × 1.1
            TestWorld.AssertFix(80m, crown.Buildings[data.BuildingIndex("bld.house")].Cost[wood]);
            Assert.AreEqual(data.UnitIndex("unit.redcoat"), crown.Replace(data.UnitIndex("unit.musketeer")));
            TestWorld.AssertFix(0.9m, crown.ShipmentXpCostMultiplier);

            // Compact: cavalry +10% damage, stables −25%, wood +10%, hussar → uhlan.
            BakedDefs compact = w.DefsOf(1);
            TestWorld.AssertFix(33m, compact.Units[data.UnitIndex("unit.hussar")].Attacks[0].Damage);   // 30 × 1.1
            TestWorld.AssertFix(150m, compact.Buildings[data.BuildingIndex("bld.stable")].Cost[wood]);
            TestWorld.AssertFix(1.1m, compact.GatherMultiplier[data.NodeIndex("res.tree")]);
            Assert.AreEqual(data.UnitIndex("unit.uhlan"), compact.Replace(data.UnitIndex("unit.hussar")));
            // The base definitions are untouched.
            Assert.AreEqual(Fix64.FromInt(150), w.Defs.Units[data.UnitIndex("unit.musketeer")].Hp);
            Assert.AreEqual(Fix64.FromInt(100), w.Defs.Buildings[data.BuildingIndex("bld.house")].Cost[wood]);
        }

        [Test]
        public void SunEmpire_StartsWithSevenFasterVillagers()
        {
            var m = new MatchRunner(TestWorld.Data, Config("civ.sun", "civ.crown", AiDifficulty.None, AiDifficulty.None), new LocalCommandSource());
            World w = m.World;
            Assert.AreEqual(7, TestWorld.UnitsOf(w, 0, "unit.villager").Length);
            TestWorld.AssertFix(4.6m, w.DefsOf(0).Units[w.Defs.Data.UnitIndex("unit.villager")].Speed);
            TestWorld.AssertFix(4.6m, w.Movers.Get(TestWorld.UnitsOf(w, 0, "unit.villager")[0]).Speed);
        }

        [Test]
        public void AgeUp_UnlocksBarracks_AndTrainingUsesReplacement()
        {
            var m = new MatchRunner(TestWorld.Data, Config("civ.crown", "civ.crown", AiDifficulty.None, AiDifficulty.None), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int tc = w.FindBuilding(0, data.BuildingIndex("bld.towncenter"));
            int barracks = data.BuildingIndex("bld.barracks");
            w.Players[0].Stockpile[data.ResourceIndex("food")] = Fix64.FromInt(2000);
            w.Players[0].Stockpile[data.ResourceIndex("wood")] = Fix64.FromInt(2000);
            w.Players[0].Stockpile[data.ResourceIndex("gold")] = Fix64.FromInt(2000);

            Assert.AreEqual(PlacementResult.WrongAge, BuildCommand.Validate(w, 0, barracks, 8, 20));
            m.Source.Submit(new AgeUpCommand(0, tc));
            List<SimEvent> events = TestWorld.RunCollecting(m, 60 * 20 + 5);
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.AgeAdvanced));
            Assert.AreEqual(1, w.Players[0].Age);
            Assert.AreEqual(Fix64.FromInt(1200), w.Players[0].Stockpile[data.ResourceIndex("food")]);
            Assert.AreEqual(PlacementResult.Ok, BuildCommand.Validate(w, 0, barracks, 8, 20));

            int site = w.SpawnBuilding(barracks, 0, 8, 20, complete: true);
            m.Source.Submit(new TrainCommand(0, site, data.UnitIndex("unit.musketeer")));
            m.RunTicks(30 * 20 + 5);
            Assert.AreEqual(1, TestWorld.UnitsOf(w, 0, "unit.redcoat").Length, "Crown trains redcoats in place of musketeers");
            Assert.AreEqual(0, TestWorld.UnitsOf(w, 0, "unit.musketeer").Length);
            TestWorld.AssertFix(187m, w.Healths.Get(TestWorld.UnitsOf(w, 0, "unit.redcoat")[0]).MaxHp);   // 170 × 1.1
        }

        [Test]
        public void Research_AppliesEffectsAndRescalesUnits()
        {
            var m = new MatchRunner(TestWorld.Data, Config("civ.crown", "civ.crown", AiDifficulty.None, AiDifficulty.None), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int tc = w.FindBuilding(0, data.BuildingIndex("bld.towncenter"));
            int dogs = data.TechIndex("tech.hunting_dogs");
            Fix64 before = w.DefsOf(0).GatherMultiplier[data.NodeIndex("res.hunt")];

            m.Source.Submit(new ResearchCommand(0, tc, dogs));
            m.RunTicks(2);
            Assert.IsTrue(w.Researches.Has(tc));
            Assert.AreEqual(Fix64.FromInt(100), w.Players[0].Stockpile[data.ResourceIndex("food")]);
            m.Source.Submit(new ResearchCommand(0, tc, dogs));   // already in progress → rejected, no charge
            List<SimEvent> events = TestWorld.RunCollecting(m, 30 * 20 + 5);
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.CommandRejected));
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.ResearchFinished));
            Assert.IsTrue(w.Players[0].Researched[dogs]);
            TestWorld.AssertFix((before * Fix64.FromDecimal(1.1m)).ToDecimal(), w.DefsOf(0).GatherMultiplier[data.NodeIndex("res.hunt")]);

            // Veteran infantry rescales a living musketeer's max hp.
            w.Players[0].Age = 2;
            w.Players[0].Stockpile[data.ResourceIndex("wood")] = Fix64.FromInt(1000);
            w.Players[0].Stockpile[data.ResourceIndex("gold")] = Fix64.FromInt(1000);
            int barracks = w.SpawnBuilding(data.BuildingIndex("bld.barracks"), 0, 8, 20, true);
            int redcoat = w.SpawnUnit(data.UnitIndex("unit.redcoat"), 0, FixVec2.FromInts(20, 20));
            Fix64 hpBefore = w.Healths.Get(redcoat).MaxHp;
            m.Source.Submit(new ResearchCommand(0, barracks, data.TechIndex("tech.veteran_infantry")));
            m.RunTicks(40 * 20 + 5);
            TestWorld.AssertFix((hpBefore * Fix64.FromDecimal(1.2m)).ToDecimal(), w.Healths.Get(redcoat).MaxHp);
            Assert.AreEqual(w.Healths.Get(redcoat).MaxHp, w.Healths.Get(redcoat).Hp);
        }

        [Test]
        public void Shipments_ArriveAtTownCenter()
        {
            var m = new MatchRunner(TestWorld.Data, Config("civ.crown", "civ.crown", AiDifficulty.None, AiDifficulty.None), new LocalCommandSource());
            World w = m.World;
            var data = w.Defs.Data;
            int villagersBefore = TestWorld.UnitsOf(w, 0, "unit.villager").Length;
            int ship = data.TechIndex("ship.3_villagers");

            Assert.AreEqual(CommandRejectReason.NotEnoughXp, ShipmentCommand.Validate(w, 0, ship));
            w.Players[0].Xp = Fix64.FromInt(300);   // Crown pays 270 for the first shipment
            m.RunTicks(11);                          // availability recomputed every 10 ticks
            Assert.AreEqual(1, w.Players[0].ShipmentsAvailable);
            m.Source.Submit(new ShipmentCommand(0, ship));
            List<SimEvent> events = TestWorld.RunCollecting(m, 3);
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.ShipmentArrived));
            Assert.AreEqual(villagersBefore + 3, TestWorld.UnitsOf(w, 0, "unit.villager").Length);
            TestWorld.AssertFix(30m, w.Players[0].Xp);
            Assert.AreEqual(1, w.Players[0].ShipmentsSent);
            // Age-gated shipment is refused in age I, and a resource crate adds to the stockpile once affordable.
            Assert.AreEqual(CommandRejectReason.WrongAge, ShipmentCommand.Validate(w, 0, data.TechIndex("ship.700_wood")));
        }

        [Test]
        public void SkirmishAi_BuildsEconomyAndArmy()
        {
            var m = new MatchRunner(TestWorld.Data, Config("civ.crown", "civ.compact", AiDifficulty.None, AiDifficulty.Hard, 5), new LocalCommandSource());
            World w = m.World;
            int villagersStart = TestWorld.UnitsOf(w, 1, "unit.villager").Length;
            int wood = w.Defs.Data.ResourceIndex("wood");

            m.RunTicks(20 * 240);   // 4 minutes
            int villagers = TestWorld.UnitsOf(w, 1, "unit.villager").Length;
            Assert.That(villagers, Is.GreaterThan(villagersStart + 3), "AI should train villagers");
            int gathering = 0;
            foreach (int v in TestWorld.UnitsOf(w, 1, "unit.villager"))
            {
                UnitState s = w.Behaviours.Get(v).State;
                if (s == UnitState.Gather || s == UnitState.ReturnCargo || s == UnitState.Build) gathering++;
            }
            Assert.That(gathering, Is.GreaterThanOrEqualTo(villagers - 2), "villagers should be working");

            m.RunTicks(20 * 240);   // 8 minutes total
            Assert.That(w.Players[1].Age, Is.GreaterThanOrEqualTo(1), "Hard AI should have aged up");
            Assert.That(w.CountBuildings(1, w.Defs.Data.BuildingIndex("bld.house"), true), Is.GreaterThan(0), "AI should build houses");
            int army = w.CountUnits(1) - TestWorld.UnitsOf(w, 1, "unit.villager").Length;
            Assert.That(army, Is.GreaterThan(0), "AI should have trained soldiers");
        }

        [Test]
        public void AiVsAi_IsDeterministic_AndSomebodyFights()
        {
            ulong Run(out int damagedEvents, out World world)
            {
                var runner = new MatchRunner(TestWorld.Data, Config("civ.sun", "civ.compact", AiDifficulty.Hard, AiDifficulty.Hard, 77), new LocalCommandSource());
                int damaged = 0;
                for (int t = 0; t < 20 * 600; t++)   // 10 minutes
                {
                    runner.RunTicks(1);
                    foreach (SimEvent ev in runner.World.Events) if (ev.Kind == SimEventKind.Damaged) damaged++;
                }
                damagedEvents = damaged;
                world = runner.World;
                return runner.World.LastHash;
            }
            ulong a = Run(out int dmgA, out World wa);
            ulong b = Run(out int dmgB, out World wb);
            Assert.AreEqual(a, b, "two identical AI matches must produce the same hash");
            Assert.AreEqual(dmgA, dmgB);
            Assert.That(dmgA, Is.GreaterThan(0), "in ten minutes the hard AIs should have clashed");
            Assert.That(wa.Players[0].Age + wa.Players[1].Age, Is.GreaterThan(0));
            TestContext.WriteLine($"10 min AI vs AI: hash {a:X16}, {dmgA} hits, ages {wa.Players[0].Age}/{wa.Players[1].Age}, " +
                                  $"units {wa.CountUnits(0)}/{wa.CountUnits(1)}, killed {wa.Players[0].UnitsKilled}/{wa.Players[1].UnitsKilled}, fields built {wa.Fields.TotalBuilds}");
        }

        [Test]
        public void Replay_WithAi_ReproducesHashes()
        {
            var stream = new MemoryStream();
            WorldConfig cfg = Config("civ.crown", "civ.sun", AiDifficulty.None, AiDifficulty.Normal, 9);
            var recorder = new ReplayRecorder(new LocalCommandSource(), stream, cfg);
            var live = new MatchRunner(TestWorld.Data, cfg, recorder);
            World w = live.World;
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            int wood = w.Defs.Data.ResourceIndex("wood");
            int tree = w.FindNearestNode(w.Positions.Get(villagers[0]).Value, wood, Fix64.FromInt(30));
            var hashes = new List<ulong>();
            for (int t = 0; t < 20 * 120; t++)
            {
                if (t == 3) live.Source.Submit(new GatherCommand(0, villagers, tree));
                if (t == 600) live.Source.Submit(new AttackMoveCommand(0, new[] { villagers[0] }, FixVec2.FromInts(40, 40)));
                live.RunTicks(1);
                hashes.Add(w.LastHash);
            }
            recorder.Dispose();

            var replay = new ReplayCommandSource(new MemoryStream(stream.ToArray()));
            Assert.AreEqual(AiDifficulty.Normal, replay.Config.Ai[1]);
            var again = new MatchRunner(TestWorld.Data, replay.Config, replay);
            for (int t = 0; t < hashes.Count; t++)
            {
                again.RunTicks(1);
                Assert.AreEqual(hashes[t], again.World.LastHash, "hash diverged at tick {0}", t);
            }
        }
    }
}
