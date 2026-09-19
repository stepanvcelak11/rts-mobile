using NUnit.Framework;
using RTS.Data;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Tests
{
    public class MapAndPlacementTests
    {
        [Test]
        public void DataLoads_WithExpectedContent()
        {
            GameData d = TestWorld.Data;
            Assert.AreEqual(3, d.ResourceCount);
            Assert.That(d.Units.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(d.Buildings.Count, Is.GreaterThanOrEqualTo(5));
            Assert.AreEqual(1, d.Maps.Count);
            Assert.AreEqual(0.5m, d.NodeGatherRate("res.tree"));
            // JSON arrays must replace field initialisers (a Newtonsoft default appends to them).
            BuildingDef tc = d.Buildings[d.BuildingIndex("bld.towncenter")];
            Assert.AreEqual(2, tc.footprint.Count);
            Assert.AreEqual(6, tc.FootprintW);
            Assert.AreEqual(3, tc.dropOff.Count);
            Assert.AreEqual(3, tc.placement.terrain.Count);
            Assert.AreEqual(d.ResourceIndex("wood"), d.NodeResourceIndex("res.tree"));
        }

        [Test]
        public void Footprint_Validation_CoversEveryFailure()
        {
            var map = new GridMap(16, 16);
            var def = new MapDef { size = { [0] = 16, [1] = 16 } };
            def.patches.Add(new TerrainPatchDef { x = 0, y = 0, w = 16, h = 2, terrain = "water" });
            def.patches.Add(new TerrainPatchDef { x = 10, y = 10, w = 2, h = 2, terrain = "grass", elevation = 1 });
            map = GridMap.FromDef(def);

            Assert.AreEqual(PlacementResult.Ok, map.Validate(4, 4, 3, 3, World.WalkableMask));
            Assert.AreEqual(PlacementResult.OutOfBounds, map.Validate(14, 14, 3, 3, World.WalkableMask));
            Assert.AreEqual(PlacementResult.OutOfBounds, map.Validate(-1, 4, 2, 2, World.WalkableMask));
            Assert.AreEqual(PlacementResult.BadTerrain, map.Validate(2, 1, 2, 2, World.WalkableMask));
            Assert.AreEqual(PlacementResult.UnevenGround, map.Validate(9, 9, 3, 3, World.WalkableMask));

            map.Occupy(new Footprint { X = 5, Y = 5, W = 1, H = 1 }, 99);
            Assert.AreEqual(PlacementResult.Occupied, map.Validate(4, 4, 3, 3, World.WalkableMask));
            Assert.IsFalse(map.IsPassable(5, 5));
            map.Release(new Footprint { X = 5, Y = 5, W = 1, H = 1 }, 99);
            Assert.AreEqual(PlacementResult.Ok, map.Validate(4, 4, 3, 3, World.WalkableMask));
        }

        [Test]
        public void FindFreeCellAround_ReturnsNearestRingCell()
        {
            var map = new GridMap(10, 10);
            var fp = new Footprint { X = 4, Y = 4, W = 2, H = 2 };
            map.Occupy(fp, 1);
            Assert.IsTrue(map.FindFreeCellAround(fp, 2, out FixVec2 p));
            Assert.AreEqual(3, p.CellX);
            Assert.AreEqual(3, p.CellY);
        }

        [Test]
        public void World_SpawnsTownCentersAndVillagersFromMap()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            Assert.AreEqual(2, w.Players.Length);
            Assert.AreEqual(1, w.CountBuildings(0, w.Defs.Data.BuildingIndex("bld.towncenter"), true));
            int tcEntity = w.FindBuilding(0, w.Defs.Data.BuildingIndex("bld.towncenter"));
            Assert.AreEqual(6, w.Footprints.Get(tcEntity).W);
            Assert.IsFalse(w.Map.IsPassable(13, 13), "town center occupies its 6x6 footprint");
            Assert.AreEqual(1, w.CountBuildings(1, w.Defs.Data.BuildingIndex("bld.towncenter"), true));
            Assert.AreEqual(6, TestWorld.UnitsOf(w, 0, "unit.villager").Length);
            Assert.AreEqual(6, w.Players[0].Population);
            Assert.AreEqual(20, w.Players[0].PopulationCap);   // base 10 + town center 10
            Assert.That(w.Nodes.Count, Is.GreaterThan(50));
            Assert.AreEqual(Fix64.FromInt(200), w.Players[0].Stockpile[w.Defs.Data.ResourceIndex("food")]);
        }

        [Test]
        public void BuildCommand_PaysPlacesAndRejects()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int house = w.Defs.Data.BuildingIndex("bld.house");
            int wood = w.Defs.Data.ResourceIndex("wood");
            int[] villagers = TestWorld.UnitsOf(w, 0, "unit.villager");
            Fix64 woodBefore = w.Players[0].Stockpile[wood];
            Fix64 houseCost = w.DefsOf(0).Buildings[house].Cost[wood];   // civ passives may discount it

            // Valid placement near the town center.
            Assert.AreEqual(PlacementResult.Ok, BuildCommand.Validate(w, 0, house, 6, 8));
            m.Source.Submit(new BuildCommand(0, house, 6, 8, villagers));
            m.RunTicks(2);
            Assert.AreEqual(woodBefore - houseCost, w.Players[0].Stockpile[wood]);
            Assert.AreEqual(1, w.Constructions.Count);
            Assert.That(w.Map.OccupantAt(6, 8), Is.GreaterThan(0));

            // Same spot again → occupied → rejected, no charge.
            m.Source.Submit(new BuildCommand(0, house, 6, 8, villagers));
            var events = TestWorld.RunCollecting(m, 2);
            Assert.AreEqual(1, w.Constructions.Count);
            Assert.AreEqual(woodBefore - houseCost, w.Players[0].Stockpile[wood]);
            Assert.IsTrue(TestWorld.Has(events, SimEventKind.CommandRejected));

            // Cannot afford a town center (600 wood) → rejected.
            int tc = w.Defs.Data.BuildingIndex("bld.towncenter");
            Assert.AreEqual(PlacementResult.LimitReached, BuildCommand.Validate(w, 0, tc, 8, 20));
            int barracks = w.Defs.Data.BuildingIndex("bld.barracks");
            Assert.AreEqual(PlacementResult.WrongAge, BuildCommand.Validate(w, 0, barracks, 8, 20));
            int mill = w.Defs.Data.BuildingIndex("bld.mill");
            Assert.AreEqual(PlacementResult.NotAffordable, BuildCommand.Validate(w, 0, mill, 8, 20));
        }

        [Test]
        public void Construction_FinishesFasterWithMoreBuilders()
        {
            int TicksToFinish(int builderCount)
            {
                MatchRunner m = TestWorld.NewMatch();
                World w = m.World;
                int house = w.Defs.Data.BuildingIndex("bld.house");
                int[] all = TestWorld.UnitsOf(w, 0, "unit.villager");
                var builders = new int[builderCount];
                System.Array.Copy(all, builders, builderCount);
                m.Source.Submit(new BuildCommand(0, house, 12, 4, builders));
                for (int t = 0; t < 20 * 120; t++)
                {
                    m.RunTicks(1);
                    foreach (SimEvent ev in w.Events)
                        if (ev.Kind == SimEventKind.ConstructionFinished) return t;
                }
                return int.MaxValue;
            }

            int one = TicksToFinish(1);
            int three = TicksToFinish(3);
            Assert.That(one, Is.LessThan(20 * 60));
            Assert.That(three, Is.LessThan(one));
        }

        [Test]
        public void Training_QueuesPaysAndSpawns()
        {
            MatchRunner m = TestWorld.NewMatch();
            World w = m.World;
            int tc = TestWorld.Find(w, (e, id) => id.Kind == EntityKind.Building && id.Player == 0);
            int villager = w.Defs.Data.UnitIndex("unit.villager");
            int food = w.Defs.Data.ResourceIndex("food");
            int popBefore = w.Players[0].Population;

            m.Source.Submit(new TrainCommand(0, tc, villager));
            m.RunTicks(2);
            Assert.AreEqual(Fix64.FromInt(100), w.Players[0].Stockpile[food]);
            Assert.AreEqual(1, w.Queues.Get(tc).Count);

            // Second one is unaffordable (100 food left → affordable once more, then not).
            m.Source.Submit(new TrainCommand(0, tc, villager));
            m.Source.Submit(new TrainCommand(0, tc, villager));
            m.RunTicks(2);
            Assert.AreEqual(2, w.Queues.Get(tc).Count);
            Assert.AreEqual(Fix64.Zero, w.Players[0].Stockpile[food]);

            m.RunTicks(25 * 20 + 2);
            Assert.AreEqual(popBefore + 1, w.Players[0].Population);
            m.RunTicks(25 * 20 + 2);
            Assert.AreEqual(popBefore + 2, w.Players[0].Population);
            Assert.AreEqual(0, w.Queues.Get(tc).Count);
        }
    }
}
