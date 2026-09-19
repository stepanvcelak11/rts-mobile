using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Tests
{
    public class EconomyAndReplayTests
    {
        [Test]
        public void Villagers_GatherDepositAndReturn()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int wood = w.Defs.Data.ResourceIndex("wood");
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            FixVec2 tcPos = w.Positions.Get(villagers[0]).Value;
            int tree = w.FindNearestNode(tcPos, wood, Fix64.FromInt(30));
            Assert.That(tree, Is.GreaterThan(0));
            Fix64 woodBefore = w.Players[0].Stockpile[wood];

            m.Source.Submit(new GatherCommand(0, new[] { villagers[0], villagers[1], villagers[2] }, tree));

            // 90 seconds: enough for several trips at 0.5 wood/s and 10 capacity.
            List<SimEvent> events = TestWorld.RunCollecting(m, 20 * 90);

            int deposits = 0;
            foreach (SimEvent ev in events) if (ev.Kind == SimEventKind.ResourceDeposited) deposits++;
            Assert.That(deposits, Is.GreaterThanOrEqualTo(3), "each villager should have deposited at least once");
            Assert.That(w.Players[0].Stockpile[wood], Is.GreaterThan(woodBefore + Fix64.FromInt(20)));

            // They are still working, not idle.
            foreach (int v in new[] { villagers[0], villagers[1], villagers[2] })
            {
                UnitState s = w.Behaviours.Get(v).State;
                Assert.That(s == UnitState.Gather || s == UnitState.ReturnCargo, "villager {0} is {1}", v, s);
            }
            // Untouched villagers stayed idle.
            Assert.AreEqual(UnitState.Idle, w.Behaviours.Get(villagers[3]).State);
        }

        [Test]
        public void Villagers_MoveToDepletedNodeReplacement()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int wood = w.Defs.Data.ResourceIndex("wood");
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            int tree = w.FindNearestNode(w.Positions.Get(villagers[0]).Value, wood, Fix64.FromInt(30));
            // Make that tree nearly empty so it depletes on the first trip.
            w.Nodes.Get(tree).Amount = Fix64.FromInt(3);

            m.Source.Submit(new GatherCommand(0, new[] { villagers[0] }, tree));
            List<SimEvent> events = TestWorld.RunCollecting(m, 20 * 60);

            Assert.IsTrue(TestWorld.Has(events, SimEventKind.NodeDepleted));
            Assert.IsFalse(w.IsAlive(tree));
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.ResourceDeposited));
            UnitState s = w.Behaviours.Get(villagers[0]).State;
            Assert.That(s == UnitState.Gather || s == UnitState.ReturnCargo, "should have found another tree, is {0}", s);
            Assert.That(w.Behaviours.Get(villagers[0]).TargetEntity, Is.Not.EqualTo(tree));
        }

        [Test]
        public void MoveCommand_ArrivesAndStops()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            FixVec2 start = w.Positions.Get(villagers[0]).Value;
            FixVec2 target = start + new FixVec2(Fix64.FromInt(-4), Fix64.FromInt(-3));   // 5 cells away, open ground

            m.Source.Submit(new MoveCommand(0, new[] { villagers[0] }, target));
            m.RunTicks(2);
            Assert.AreEqual(UnitState.Move, w.Behaviours.Get(villagers[0]).State);
            m.RunTicks(20 * 3);   // 4 cells/s → ~1.3 s needed
            Assert.AreEqual(UnitState.Idle, w.Behaviours.Get(villagers[0]).State);
            Assert.IsTrue(FixMath.WithinDistance(w.Positions.Get(villagers[0]).Value, target, SimConstants.ArriveRadius));
        }

        [Test]
        public void Replay_ReproducesIdenticalHashes()
        {
            var stream = new MemoryStream();
            var recorder = new ReplayRecorder(new LocalCommandSource(), stream, TestWorld.DefaultConfig(123));
            var live = new MatchRunner(TestWorld.Data, TestWorld.DefaultConfig(123), recorder);
            World w = live.World;
            var hashes = new List<ulong>();

            int wood = w.Defs.Data.ResourceIndex("wood");
            int food = w.Defs.Data.ResourceIndex("food");
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            int tree = w.FindNearestNode(w.Positions.Get(villagers[0]).Value, wood, Fix64.FromInt(30));
            int berries = w.FindNearestNode(w.Positions.Get(villagers[0]).Value, food, Fix64.FromInt(30));
            int tc = TestWorld.Find(w, (e, id) => id.Kind == EntityKind.Building && id.Player == 0);
            int house = w.Defs.Data.BuildingIndex("bld.house");

            // A scripted 60-second match with a mix of every command.
            for (int t = 0; t < 20 * 60; t++)
            {
                if (t == 5) live.Source.Submit(new GatherCommand(0, new[] { villagers[0], villagers[1] }, tree));
                if (t == 6) live.Source.Submit(new GatherCommand(0, new[] { villagers[2], villagers[3] }, berries));
                if (t == 10) live.Source.Submit(new BuildCommand(0, house, 6, 8, new[] { villagers[4], villagers[5] }));
                if (t == 12) live.Source.Submit(new TrainCommand(0, tc, w.Defs.Data.UnitIndex("unit.villager")));
                if (t == 400) live.Source.Submit(new MoveCommand(0, new[] { villagers[4] }, FixVec2.FromInts(20, 20)));
                if (t == 700) live.Source.Submit(new StopCommand(0, new[] { villagers[0] }));
                live.RunTicks(1);
                hashes.Add(w.LastHash);
            }
            recorder.Dispose();
            Assert.That(w.Players[0].Stockpile[wood], Is.GreaterThan(Fix64.FromInt(100)), "the scripted match should have gathered something");

            // Play the file back into a fresh world and compare every tick.
            var replay = new ReplayCommandSource(new MemoryStream(stream.ToArray()));
            Assert.AreEqual(123u, replay.Config.Seed);
            var again = new MatchRunner(TestWorld.Data, replay.Config, replay);
            for (int t = 0; t < hashes.Count; t++)
            {
                again.RunTicks(1);
                Assert.AreEqual(hashes[t], again.World.LastHash, "hash diverged at tick {0}", t);
                if (replay.TryGetHash(again.World.Tick, out ulong recorded))
                    Assert.AreEqual(recorded, again.World.LastHash, "checkpoint mismatch at tick {0}", again.World.Tick);
            }
            Assert.AreEqual(w.Players[0].Stockpile[wood], again.World.Players[0].Stockpile[wood]);
        }

        [Test]
        public void DifferentSeeds_StillDeterministicPerSeed()
        {
            ulong Run(uint seed)
            {
                MatchRunner m = TestWorld.NewMatch(seed);
                m.RunTicks(50);
                return m.World.LastHash;
            }
            Assert.AreEqual(Run(1), Run(1));
            Assert.AreEqual(Run(9), Run(9));
        }
    }
}
