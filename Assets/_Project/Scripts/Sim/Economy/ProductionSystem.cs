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
            StepResearch(w);
            StepAgeUp(w);
            StepShipments(w);
            StepMarket(w);
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
                    w.OnBuildingCompleted(e, w.DefsOf(id.Player).Buildings[id.DefIndex], id.Player);
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
                BakedUnit unit = w.DefsOf(id.Player).Units[q.Get(0)];
                PlayerState ps = w.Players[id.Player];

                if (q.HeadRemaining > 0) { q.HeadRemaining--; continue; }

                // Ready: wait for population room, then spawn next to the building.
                if (ps.Population + unit.Population > ps.PopulationCap) continue;
                if (!w.Map.FindFreeCellAround(w.Footprints.Get(building), 4, out FixVec2 spawnAt)) continue;

                int nextTicks = q.Count > 1 ? w.DefsOf(id.Player).Units[q.Get(1)].TrainTicks : 0;
                q.Dequeue(nextTicks);
                int e = w.SpawnUnit(unit.Index, id.Player, spawnAt);
                w.Events.Add(new SimEvent(SimEventKind.UnitTrained, e, building));
                if (w.Rallies.TryGet(building, out Rally rally)) BehaviorSystem.OrderMove(w, e, rally.Point);
            }
        }

        private static void StepResearch(World w)
        {
            for (int i = w.Researches.Count - 1; i >= 0; i--)
            {
                ref Research r = ref w.Researches.At(i);
                if (r.Remaining > 0) { r.Remaining--; continue; }
                int building = w.Researches.EntityAt(i);
                int player = w.Identities.Get(building).Player;
                int tech = r.Tech;
                w.Researches.Remove(building);
                ApplyTech(w, player, tech);
                w.Events.Add(new SimEvent(SimEventKind.ResearchFinished, building, player, tech));
            }
        }

        /// <summary>Applies a tech's effects to the player's definitions and rescales living units whose max hp changed.</summary>
        public static void ApplyTech(World w, int player, int tech)
        {
            PlayerState ps = w.Players[player];
            ps.Researched[tech] = true;
            BakedTech t = w.Defs.Techs[tech];
            foreach (Data.ModifierDef m in t.Def.effects)
            {
                var touched = Modifiers.Apply(ps.Defs, m);
                foreach (int unitIndex in touched) RescaleUnits(w, player, unitIndex);
            }
        }

        /// <summary>Applies a single modifier (e.g. from a shipment) to a player and rescales affected units.</summary>
        public static void ApplyModifierToPlayer(World w, int player, Data.ModifierDef m)
        {
            var touched = Modifiers.Apply(w.Players[player].Defs, m);
            foreach (int unitIndex in touched) RescaleUnits(w, player, unitIndex);
        }

        private static void RescaleUnits(World w, int player, int unitIndex)
        {
            Fix64 newMax = w.DefsOf(player).Units[unitIndex].Hp;
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind != EntityKind.Unit || id.Player != player || id.DefIndex != unitIndex) continue;
                ref Health h = ref w.Healths.Get(w.Identities.EntityAt(i));
                if (h.MaxHp.IsZero || h.MaxHp == newMax) continue;
                h.Hp = h.Hp * newMax / h.MaxHp;
                h.MaxHp = newMax;
            }
        }

        /// <summary>Market prices drift back toward 100 gold per 100 resources, 1 point every 5 seconds.</summary>
        private static void StepMarket(World w)
        {
            if (w.Tick % 100 != 0) return;
            Fix64 baseline = Fix64.FromInt(100);
            foreach (PlayerState ps in w.Players)
                for (int r = 0; r < ps.MarketPrice.Length; r++)
                {
                    if (ps.MarketPrice[r] > baseline) ps.MarketPrice[r] = FixMath.Max(baseline, ps.MarketPrice[r] - Fix64.One);
                    else if (ps.MarketPrice[r] < baseline) ps.MarketPrice[r] = FixMath.Min(baseline, ps.MarketPrice[r] + Fix64.One);
                }
        }

        private static void StepAgeUp(World w)
        {
            for (int p = 0; p < w.Players.Length; p++)
            {
                PlayerState ps = w.Players[p];
                if (ps.AgeUpBuilding == 0) continue;
                if (ps.AgeUpRemaining > 0) { ps.AgeUpRemaining--; continue; }
                ps.Age++;
                ps.AgeUpBuilding = 0;
                w.Events.Add(new SimEvent(SimEventKind.AgeAdvanced, 0, p, ps.Age));
            }
        }

        /// <summary>Recomputes how many shipments each player can send from their XP.</summary>
        private static void StepShipments(World w)
        {
            if (w.Tick % 10 != 0) return;
            for (int p = 0; p < w.Players.Length; p++)
            {
                PlayerState ps = w.Players[p];
                int available = 0;
                Fix64 xp = ps.Xp;
                int n = ps.ShipmentsSent;
                while (available < 5 && xp >= ShipmentCost(w, ps, n + available))
                {
                    xp -= ShipmentCost(w, ps, n + available);
                    available++;
                }
                ps.ShipmentsAvailable = available;
            }
        }

        /// <summary>XP price of the n-th shipment (0-based): base × growth^n × civ multiplier.</summary>
        public static Fix64 ShipmentCost(World w, PlayerState ps, int n)
        {
            Data.HomeCityDef hc = ps.CivIndex >= 0 ? w.Defs.Data.Civs[ps.CivIndex].homeCity : new Data.HomeCityDef();
            Fix64 cost = Fix64.FromDecimal(hc.xpPerShipmentBase);
            Fix64 growth = Fix64.FromDecimal(hc.xpGrowth);
            for (int i = 0; i < n && i < 40; i++) cost *= growth;
            return cost * ps.Defs.ShipmentXpCostMultiplier;
        }
    }
}
