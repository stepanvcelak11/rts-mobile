using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>Construction progress and unit training queues.</summary>
    public sealed class ProductionSystem : ISystem
    {
        // Each extra builder adds 50% of a builder: time = buildSeconds / (1 + 0.5·(n−1)).
        private static readonly Fix64 ExtraBuilderFactor = Fix64.Half;

        public void Step(World w)
        {
            StepConstruction(w);
            StepQueues(w);
        }

        private static void StepConstruction(World w)
        {
            // Iterate by index but tolerate removals: finishing removes the component (swap-remove),
            // so we walk backwards.
            for (int i = w.Constructions.Count - 1; i >= 0; i--)
            {
                int e = w.Constructions.EntityAt(i);
                ref Construction c = ref w.Constructions.At(i);
                int builders = c.BuildersThisTick;
                c.BuildersThisTick = 0;
                if (builders <= 0) continue;

                Fix64 speed = Fix64.One + ExtraBuilderFactor * (builders - 1);
                c.Progress += speed / c.TotalTicks;

                ref Health hp = ref w.Healths.Get(e);
                Fix64 target = hp.MaxHp * FixMath.Clamp01(c.Progress);
                if (target > hp.Hp) hp.Hp = target;

                if (c.Progress >= Fix64.One)
                {
                    hp.Hp = hp.MaxHp;
                    Identity id = w.Identities.Get(e);
                    w.Constructions.Remove(e);
                    w.OnBuildingCompleted(e, w.Defs.Buildings[id.DefIndex], id.Player);
                    w.Events.Add(new SimEvent(SimEventKind.ConstructionFinished, e));
                }
            }
        }

        private static void StepQueues(World w)
        {
            for (int i = 0; i < w.Queues.Count; i++)
            {
                ref ProductionQueue q = ref w.Queues.At(i);
                if (q.Count == 0) continue;
                int building = w.Queues.EntityAt(i);
                Identity id = w.Identities.Get(building);
                BakedUnit unit = w.Defs.Units[q.Get(0)];
                PlayerState ps = w.Players[id.Player];

                if (q.HeadRemaining > 0) { q.HeadRemaining--; continue; }

                // Ready: wait for population room, then spawn next to the building.
                if (ps.Population + unit.Population > ps.PopulationCap) continue;
                if (!w.Map.FindFreeCellAround(w.Footprints.Get(building), 4, out FixVec2 spawnAt)) continue;

                int nextTicks = q.Count > 1 ? w.Defs.Units[q.Get(1)].TrainTicks : 0;
                q.Dequeue(nextTicks);
                int e = w.SpawnUnit(unit.Index, id.Player, spawnAt);
                w.Events.Add(new SimEvent(SimEventKind.UnitTrained, e, building));
            }
        }
    }
}
