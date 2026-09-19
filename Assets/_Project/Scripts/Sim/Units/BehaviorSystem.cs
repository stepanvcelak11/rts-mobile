using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// The unit finite-state machine. Decides *what* a unit is doing and where it wants to go;
    /// MovementSystem moves it, EconomySystem/ProductionSystem do the work once it is in place.
    /// Phase 2 states: Idle · Move · Gather · ReturnCargo · Build.
    /// </summary>
    public sealed class BehaviorSystem : ISystem
    {
        public void Step(World w)
        {
            for (int i = 0; i < w.Behaviours.Count; i++)
            {
                int e = w.Behaviours.EntityAt(i);
                ref UnitBehaviour b = ref w.Behaviours.At(i);
                ref Mover m = ref w.Movers.Get(e);

                switch (b.State)
                {
                    case UnitState.Idle:
                        m.Moving = false;
                        break;
                    case UnitState.Move:
                        TickMove(w, e, ref b, ref m);
                        break;
                    case UnitState.Gather:
                        TickGather(w, e, ref b, ref m);
                        break;
                    case UnitState.ReturnCargo:
                        TickReturn(w, e, ref b, ref m);
                        break;
                    case UnitState.Build:
                        TickBuild(w, e, ref b, ref m);
                        break;
                    default:
                        m.Moving = false;
                        break;
                }
            }
        }

        // ---- transitions --------------------------------------------------------------

        public static void SetState(World w, int entity, ref UnitBehaviour b, UnitState state)
        {
            if (b.State == state) return;
            b.State = state;
            w.Events.Add(new SimEvent(SimEventKind.StateChanged, entity, (int)state));
        }

        public static void OrderMove(World w, int entity, FixVec2 target)
        {
            ref UnitBehaviour b = ref w.Behaviours.Get(entity);
            b.TargetPos = w.Map.ClampInside(target);
            b.TargetEntity = 0;
            SetState(w, entity, ref b, UnitState.Move);
        }

        public static void OrderGather(World w, int entity, int node)
        {
            ref UnitBehaviour b = ref w.Behaviours.Get(entity);
            b.TargetEntity = node;
            b.LastNode = node;
            SetState(w, entity, ref b, UnitState.Gather);
        }

        public static void OrderBuild(World w, int entity, int site)
        {
            ref UnitBehaviour b = ref w.Behaviours.Get(entity);
            b.TargetEntity = site;
            SetState(w, entity, ref b, UnitState.Build);
        }

        public static void OrderStop(World w, int entity)
        {
            ref UnitBehaviour b = ref w.Behaviours.Get(entity);
            b.TargetEntity = 0;
            SetState(w, entity, ref b, UnitState.Idle);
            w.Movers.Get(entity).Moving = false;
        }

        // ---- per-state ticks ----------------------------------------------------------

        private static void TickMove(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            FixVec2 pos = w.Positions.Get(e).Value;
            if (FixMath.WithinDistance(pos, b.TargetPos, SimConstants.ArriveRadius))
            {
                m.Moving = false;
                SetState(w, e, ref b, UnitState.Idle);
                return;
            }
            m.Target = b.TargetPos;
            m.Moving = true;
        }

        private static void TickGather(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            if (!w.Cargos.Has(e)) { SetState(w, e, ref b, UnitState.Idle); return; }
            ref Cargo cargo = ref w.Cargos.Get(e);

            if (cargo.IsFull)
            {
                SetState(w, e, ref b, UnitState.ReturnCargo);
                b.TargetEntity = 0;
                return;
            }

            // Node gone or empty → look for another one of the same resource nearby.
            if (!w.Nodes.TryGet(b.TargetEntity, out ResourceNode node) || node.IsDepleted)
            {
                int resource = node.Resource;
                if (!w.Nodes.Has(b.TargetEntity) && cargo.Resource >= 0) resource = cargo.Resource;
                if (!w.Nodes.Has(b.TargetEntity) && cargo.Resource < 0 && w.Nodes.TryGet(b.LastNode, out ResourceNode last)) resource = last.Resource;
                int replacement = resource >= 0
                    ? w.FindNearestNode(w.Positions.Get(e).Value, resource, SimConstants.NodeSearchRadius, b.TargetEntity)
                    : 0;
                if (replacement == 0)
                {
                    // Bring home whatever we carry, then idle.
                    if (!cargo.IsEmpty) { SetState(w, e, ref b, UnitState.ReturnCargo); b.TargetEntity = 0; }
                    else { m.Moving = false; SetState(w, e, ref b, UnitState.Idle); }
                    return;
                }
                b.TargetEntity = replacement;
                b.LastNode = replacement;
            }

            if (w.IsAdjacent(e, b.TargetEntity, SimConstants.InteractReach))
            {
                m.Moving = false;          // EconomySystem gathers while we stand here
                FaceTowards(w, e, w.Footprints.Get(b.TargetEntity).Center);
            }
            else
            {
                m.Target = ApproachPoint(w, e, b.TargetEntity);
                m.Moving = true;
            }
        }

        private static void TickReturn(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            if (!w.Cargos.Has(e)) { SetState(w, e, ref b, UnitState.Idle); return; }
            ref Cargo cargo = ref w.Cargos.Get(e);
            int player = w.Identities.Get(e).Player;

            if (cargo.IsEmpty)
            {
                // Deposited (or nothing to carry): go back to the last node if it still has stock.
                if (w.Nodes.TryGet(b.LastNode, out ResourceNode last) && !last.IsDepleted)
                {
                    b.TargetEntity = b.LastNode;
                    SetState(w, e, ref b, UnitState.Gather);
                }
                else if (w.Nodes.Has(b.LastNode) || cargo.Resource >= 0)
                {
                    b.TargetEntity = b.LastNode;   // Gather will search for a replacement
                    SetState(w, e, ref b, UnitState.Gather);
                }
                else
                {
                    m.Moving = false;
                    SetState(w, e, ref b, UnitState.Idle);
                }
                return;
            }

            if (!w.Identities.Has(b.TargetEntity) || w.Constructions.Has(b.TargetEntity))
            {
                b.TargetEntity = w.FindNearestDropOff(w.Positions.Get(e).Value, player, cargo.Resource);
                if (b.TargetEntity == 0)
                {
                    m.Moving = false;
                    SetState(w, e, ref b, UnitState.Idle);
                    return;
                }
            }

            if (w.IsAdjacent(e, b.TargetEntity, w.Defs.DepositRadius))
            {
                m.Moving = false;          // EconomySystem deposits
            }
            else
            {
                m.Target = ApproachPoint(w, e, b.TargetEntity);
                m.Moving = true;
            }
        }

        private static void TickBuild(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            if (!w.Constructions.Has(b.TargetEntity))
            {
                // Finished or cancelled.
                m.Moving = false;
                b.TargetEntity = 0;
                SetState(w, e, ref b, UnitState.Idle);
                return;
            }

            if (w.IsAdjacent(e, b.TargetEntity, SimConstants.InteractReach))
            {
                m.Moving = false;
                w.Constructions.Get(b.TargetEntity).BuildersThisTick++;
                FaceTowards(w, e, w.Footprints.Get(b.TargetEntity).Center);
            }
            else
            {
                m.Target = ApproachPoint(w, e, b.TargetEntity);
                m.Moving = true;
            }
        }

        // ---- helpers ------------------------------------------------------------------

        /// <summary>Closest point on the target footprint edge, pushed out by the unit radius.</summary>
        private static FixVec2 ApproachPoint(World w, int unit, int target)
        {
            ref Position p = ref w.Positions.Get(unit);
            Footprint fp = w.Footprints.Get(target);
            Fix64 pad = p.Radius + Fix64.Ratio(1, 4);
            Fix64 minX = Fix64.FromInt(fp.X) - pad, maxX = Fix64.FromInt(fp.X + fp.W) + pad;
            Fix64 minY = Fix64.FromInt(fp.Y) - pad, maxY = Fix64.FromInt(fp.Y + fp.H) + pad;
            Fix64 x = FixMath.Clamp(p.Value.X, minX, maxX);
            Fix64 y = FixMath.Clamp(p.Value.Y, minY, maxY);
            // If we are inside the padded box (e.g. spawned overlapping), push to the nearest edge.
            if (x == p.Value.X && y == p.Value.Y)
            {
                Fix64 dl = x - minX, dr = maxX - x, db = y - minY, dt = maxY - y;
                Fix64 best = dl; x = minX; y = p.Value.Y;
                if (dr < best) { best = dr; x = maxX; y = p.Value.Y; }
                if (db < best) { best = db; x = p.Value.X; y = minY; }
                if (dt < best) { x = p.Value.X; y = maxY; }
            }
            return new FixVec2(x, y);
        }

        private static void FaceTowards(World w, int unit, FixVec2 point)
        {
            ref Position p = ref w.Positions.Get(unit);
            FixVec2 dir = (point - p.Value).Normalized;
            if (dir != FixVec2.Zero) p.Facing = dir;
        }
    }
}
