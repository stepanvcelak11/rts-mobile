using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// Gathering and depositing. A villager in Gather state standing next to its node fills its
    /// cargo at the node rate; one in ReturnCargo state next to a drop-off empties it into the
    /// player stockpile. State transitions themselves belong to BehaviorSystem.
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
            if (!w.IsAdjacent(e, b.TargetEntity, SimConstants.InteractReach)) return;

            ref ResourceNode node = ref w.Nodes.Get(b.TargetEntity);
            if (node.IsDepleted || node.Resource < 0) return;

            // Switching resource drops the old cargo (AoE rule).
            if (cargo.Resource != node.Resource)
            {
                cargo.Resource = node.Resource;
                cargo.Amount = Fix64.Zero;
                cargo.Accumulator = Fix64.Zero;
            }

            Identity id = w.Identities.Get(e);
            BakedDefs defs = w.DefsOf(id.Player);
            Fix64 rate = node.RatePerTick * defs.Units[id.DefIndex].GatherRateMultiplier * defs.GatherMultiplier[node.Def];
            Fix64 room = cargo.Capacity - cargo.Amount;
            Fix64 take = FixMath.Min(rate, room);
            if (node.Depletes) take = FixMath.Min(take, node.Amount);

            cargo.Amount += take;
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

        private static void Deposit(World w, int e, ref Cargo cargo, ref UnitBehaviour b)
        {
            if (cargo.IsEmpty || cargo.Resource < 0) return;
            if (!w.Identities.TryGet(b.TargetEntity, out Identity target) || target.Kind != EntityKind.Building) return;
            if (w.Constructions.Has(b.TargetEntity)) return;
            if (!w.IsAdjacent(e, b.TargetEntity, w.Defs.DepositRadius)) return;

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
