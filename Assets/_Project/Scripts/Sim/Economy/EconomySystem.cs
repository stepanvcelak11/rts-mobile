using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// Gathering. As in Age of Empires III a villager in Gather state standing next to its node feeds
    /// the player stockpile directly every tick: no cargo, no walking home. Camps near the work site
    /// speed the work up. The Cargo component only tracks the resource being worked and the amount
    /// gathered since the last <see cref="SimEventKind.ResourceDeposited"/> event (used for floating
    /// "+5 wood" texts). Depositing remains for units that still carry something (treasure pickup).
    /// State transitions themselves belong to BehaviorSystem.
    /// </summary>
    public sealed class EconomySystem : ISystem
    {
        public void Step(World w)
        {
            for (int i = 0; i < w.Cargos.Count; i++)
            {
                int e = w.Cargos.EntityAt(i);
                ref Cargo cargo = ref w.Cargos.At(i);
                ref UnitBehaviour b = ref w.Behaviours.Get(e);

                if (b.State == UnitState.Gather) Gather(w, e, ref cargo, ref b);
                else if (b.State == UnitState.ReturnCargo) Deposit(w, e, ref cargo, ref b);
            }
        }

        private static void Gather(World w, int e, ref Cargo cargo, ref UnitBehaviour b)
        {
            if (cargo.IsFull) return;
            if (!w.Nodes.Has(b.TargetEntity)) return;
            if (!w.CanInteract(e, b.TargetEntity, SimConstants.InteractReach)) return;

            ref ResourceNode node = ref w.Nodes.Get(b.TargetEntity);
            if (node.IsDepleted || node.Resource < 0) return;

            if (w.Defs.Nodes[node.Def].Treasure)
            {
                // Treasure: the whole amount goes straight to the stockpile, plus experience.
                int owner = w.Identities.Get(e).Player;
                if (owner >= 0)
                {
                    PlayerState ps = w.Players[owner];
                    ps.Stockpile[node.Resource] = FixMath.Min(ps.Stockpile[node.Resource] + node.Amount, w.Defs.StockpileCap);
                    w.AddXp(owner, Fix64.FromInt(50));
                    w.Events.Add(new SimEvent(SimEventKind.ResourceDeposited, e, node.Resource, b.TargetEntity, node.Amount));
                    w.Events.Add(new SimEvent(SimEventKind.NodeDepleted, b.TargetEntity));
                }
                node.Amount = Fix64.Zero;
                w.Despawn(b.TargetEntity);
                return;
            }

            // Switching resource restarts the event counter.
            if (cargo.Resource != node.Resource)
            {
                cargo.Resource = node.Resource;
                cargo.Amount = Fix64.Zero;
                cargo.Accumulator = Fix64.Zero;
            }

            Identity id = w.Identities.Get(e);
            if (id.Player < 0) return;
            BakedDefs defs = w.DefsOf(id.Player);
            Fix64 rate = node.RatePerTick * defs.Units[id.DefIndex].GatherRateMultiplier * defs.GatherMultiplier[node.Def];
            if (NearOwnCamp(w, id.Player, w.Positions.Get(e).Value, node.Resource)) rate *= SimConstants.CampAuraBonus;
            Fix64 take = rate;
            if (node.Depletes) take = FixMath.Min(take, node.Amount);

            // Straight into the stockpile (AoE3 trickle); Home-City XP 1 per resource.
            PlayerState stock = w.Players[id.Player];
            stock.Stockpile[node.Resource] = FixMath.Min(stock.Stockpile[node.Resource] + take, w.Defs.StockpileCap);
            w.AddXp(id.Player, take);
            cargo.Accumulator += take;
            if (cargo.Accumulator >= SimConstants.TrickleEventEvery)
            {
                w.Events.Add(new SimEvent(SimEventKind.ResourceDeposited, e, node.Resource, b.TargetEntity, SimConstants.TrickleEventEvery));
                cargo.Accumulator -= SimConstants.TrickleEventEvery;
            }
            if (node.Depletes)
            {
                node.Amount -= take;
                if (node.Amount <= Fix64.Zero)
                {
                    w.Events.Add(new SimEvent(SimEventKind.NodeDepleted, b.TargetEntity));
                    w.Despawn(b.TargetEntity);
                }
            }
        }

        /// <summary>A completed own building that accepts <paramref name="resource"/> (other than the town centre) within the aura radius.</summary>
        private static bool NearOwnCamp(World w, int player, FixVec2 at, int resource)
        {
            BakedDefs defs = w.DefsOf(player);
            Fix64 r2 = SimConstants.CampAuraRadius * SimConstants.CampAuraRadius;
            for (int i = 0; i < w.Footprints.Count; i++)
            {
                int e = w.Footprints.EntityAt(i);
                if (!w.Identities.TryGet(e, out Identity id) || id.Kind != EntityKind.Building || id.Player != player) continue;
                if (w.Constructions.Has(e)) continue;
                BakedBuilding bd = defs.Buildings[id.DefIndex];
                if (bd.Id == "bld.towncenter" || resource >= bd.DropOff.Length || !bd.DropOff[resource]) continue;
                if (w.Footprints.At(i).DistanceSqTo(at) <= r2) return true;
            }
            return false;
        }

        private static void Deposit(World w, int e, ref Cargo cargo, ref UnitBehaviour b)
        {
            if (cargo.IsEmpty || cargo.Resource < 0) return;
            if (!w.Identities.TryGet(b.TargetEntity, out Identity target) || target.Kind != EntityKind.Building) return;
            if (w.Constructions.Has(b.TargetEntity)) return;
            if (!w.CanInteract(e, b.TargetEntity, w.Defs.DepositRadius)) return;

            int player = w.Identities.Get(e).Player;
            if (player < 0 || target.Player != player) return;
            if (!w.DefsOf(player).Buildings[target.DefIndex].DropOff[cargo.Resource]) return;

            PlayerState ps = w.Players[player];
            Fix64 amount = cargo.Amount;
            ps.Stockpile[cargo.Resource] = FixMath.Min(ps.Stockpile[cargo.Resource] + amount, w.Defs.StockpileCap);
            w.Events.Add(new SimEvent(SimEventKind.ResourceDeposited, e, cargo.Resource, b.TargetEntity, amount));
            w.AddXp(player, amount);   // Home-City XP: 1 per resource brought home

            cargo.Amount = Fix64.Zero;
            cargo.Accumulator = Fix64.Zero;
            // Keep cargo.Resource so BehaviorSystem knows which resource to look for next.
        }
    }
}
