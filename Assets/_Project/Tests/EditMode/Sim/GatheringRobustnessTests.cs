using System.Collections.Generic;
using NUnit.Framework;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Tests
{
    /// <summary>
    /// The "villagers walk there but nothing comes in" class of bugs: resources must trickle in at the
    /// data rate, and a node walled in by other nodes (a tree deep in a forest) must still be worked.
    /// </summary>
    public class GatheringRobustnessTests
    {
        private static MatchRunner Generated(uint seed, string type = "twoRivers", int size = 96) =>
            new MatchRunner(TestWorld.Data, new WorldConfig
            {
                Seed = seed, PlayerCount = 2, MapType = type, MapSize = size, CivIds = new[] { "civ.compact", "civ.crown" },
                Ai = new[] { AiDifficulty.None, AiDifficulty.None }, SpawnStartingUnits = true,
            }, new LocalCommandSource());

        [Test]
        public void Gathering_TricklesAtTheDataRate()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int wood = w.Defs.Data.ResourceIndex("wood");
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            int tree = w.FindNearestNode(w.Positions.Get(v[0]).Value, wood, Fix64.FromInt(30));
            Fix64 before = w.Players[0].Stockpile[wood];
            m.Source.Submit(new GatherCommand(0, new[] { v[0] }, tree));
            m.RunTicks(20 * 60);
            // 0.5 wood/s minus a few seconds of walking; no cargo trips (AoE3 rule).
            Fix64 gained = w.Players[0].Stockpile[wood] - before;
            Assert.That(gained, Is.GreaterThan(Fix64.FromInt(24)), "one villager, one minute: " + gained);
            Assert.That(gained, Is.LessThan(Fix64.FromInt(35)), "rate must not exceed the data: " + gained);
            Assert.AreEqual(UnitState.Gather, w.Behaviours.Get(v[0]).State);
        }

        [Test]
        public void WalledInTree_IsStillWorked()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int wood = w.Defs.Data.ResourceIndex("wood");
            int treeDef = w.Defs.Data.NodeIndex("res.tree");
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            FixVec2 tc = w.Positions.Get(v[0]).Value;

            // Plant a solid 3×3 block of trees on free ground and aim at the one in the middle.
            int ox = -1, oy = -1;
            for (int dy = -8; dy <= 8 && ox < 0; dy++)
                for (int dx = 4; dx <= 12 && ox < 0; dx++)
                {
                    bool free = true;
                    for (int y = 0; y < 3 && free; y++) for (int x = 0; x < 3; x++) if (!w.Map.IsPassable(tc.CellX + dx + x, tc.CellY + dy + y)) { free = false; break; }
                    if (free) { ox = tc.CellX + dx; oy = tc.CellY + dy; }
                }
            Assert.That(ox, Is.GreaterThan(0), "free 3×3 patch near the town centre");
            int centre = 0;
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
            {
                int t = w.SpawnResourceNode(treeDef, ox + x, oy + y, Fix64.FromInt(300));
                if (x == 1 && y == 1) centre = t;
            }
            Assert.IsFalse(w.Map.FindFreeCellAround(w.Footprints.Get(centre), 1, out _), "centre tree must be walled in");

            Fix64 before = w.Players[0].Stockpile[wood];
            m.Source.Submit(new GatherCommand(0, new[] { v[1] }, centre));
            m.RunTicks(20 * 60);
            Assert.That(w.Players[0].Stockpile[wood] - before, Is.GreaterThan(Fix64.FromInt(15)), "walled-in tree: villager must work from the edge or switch to a reachable tree");
            Assert.AreEqual(UnitState.Gather, w.Behaviours.Get(v[1]).State);
        }

        [Test]
        public void CrowdedBush_EveryVillagerWorks()
        {
            MatchRunner m = Generated(7, "greatPlains", 128);
            World w = m.World;
            int food = w.Defs.Data.ResourceIndex("food");
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            int bush = 0;
            for (int i = 0; i < w.Nodes.Count && bush == 0; i++)
                if (w.Defs.Nodes[w.Nodes.At(i).Def].Id == "res.berries" && FixVec2.Distance(w.Footprints.Get(w.Nodes.EntityAt(i)).Center, w.Positions.Get(v[0]).Value) < Fix64.FromInt(25))
                    bush = w.Nodes.EntityAt(i);
            Assert.That(bush, Is.GreaterThan(0));
            Fix64 before = w.Players[0].Stockpile[food];
            m.Source.Submit(new GatherCommand(0, v, bush));
            m.RunTicks(20 * 90);
            // 6 villagers × 0.67/s × ~80 s ≈ 320; a crowd blocking one another would show as far less.
            Assert.That(w.Players[0].Stockpile[food] - before, Is.GreaterThan(Fix64.FromInt(200)));
            foreach (int e in v) Assert.AreEqual(UnitState.Gather, w.Behaviours.Get(e).State, "villager " + e);
        }

        [Test]
        public void LumberCampAura_SpeedsUpNearbyWork()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int wood = w.Defs.Data.ResourceIndex("wood");
            int[] v = TestWorld.UnitsOf(w, 0, "unit.villager");
            int tree = w.FindNearestNode(w.Positions.Get(v[0]).Value, wood, Fix64.FromInt(30));
            FixVec2 tp = w.Footprints.Get(tree).Center;
            int camp = w.SpawnBuilding(w.Defs.Data.BuildingIndex("bld.lumbercamp"), 0, tp.CellX + 3, tp.CellY + 3, complete: true);
            Assert.That(camp, Is.GreaterThan(0));
            Fix64 before = w.Players[0].Stockpile[wood];
            m.Source.Submit(new GatherCommand(0, new[] { v[0] }, tree));
            m.RunTicks(20 * 60);
            Fix64 gained = w.Players[0].Stockpile[wood] - before;
            Assert.That(gained, Is.GreaterThan(Fix64.FromInt(31)), "camp aura +25 %: " + gained);
        }
    }
}
