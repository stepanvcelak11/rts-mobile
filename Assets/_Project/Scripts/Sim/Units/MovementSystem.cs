using System.Collections.Generic;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// Moves units toward their Mover target: along a shared flow field when one exists (built
    /// on demand, budgeted per tick), straight-line otherwise. Then applies unit–unit separation
    /// so groups spread instead of stacking, and refuses to enter impassable cells.
    /// Arrival is reported through <see cref="Mover.Moving"/> turning false and
    /// <see cref="UnitBehaviour"/> reading the position next tick.
    /// </summary>
    public sealed class MovementSystem : ISystem
    {
        private static readonly Fix64 MaxPush = Fix64.Ratio(1, 5);        // cells per tick
        private static readonly Fix64 StuckFraction = Fix64.Ratio(1, 10);
        private const int StuckTicksToArrive = 12;
        private static readonly Fix64 StuckArriveRadius = Fix64.FromInt(3);

        private readonly List<int> _neighbours = new List<int>(32);
        private FixVec2[] _push = new FixVec2[256];

        public void Step(World w)
        {
            Fix64 dt = SimConstants.TickSeconds;

            // 1. Desired displacement per mover.
            for (int i = 0; i < w.Movers.Count; i++)
            {
                ref Mover m = ref w.Movers.At(i);
                m.Velocity = FixVec2.Zero;
                if (!m.Moving) { m.StuckTicks = 0; continue; }
                int e = w.Movers.EntityAt(i);
                ref Position p = ref w.Positions.Get(e);

                FixVec2 toGoal = m.Target - p.Value;
                Fix64 remaining = toGoal.Length;
                Fix64 maxStep = m.Speed * dt;

                FixVec2 dir;
                FlowField field = m.GoalEntity < 0 ? null                      // < 0 = straight line (chasing a unit)
                    : m.GoalEntity > 0 ? w.Fields.ForEntity(w, m.GoalEntity)
                    : w.Fields.ForPoint(w, m.Target);
                int cx = p.Value.CellX, cy = p.Value.CellY;
                if (field != null && field.IsGoal(cx, cy))
                {
                    // On a goal cell (point cell or the ring around a footprint): finish with a
                    // straight step to the exact approach point, then stop.
                    if (remaining <= SimConstants.ArriveRadius) { m.Moving = false; m.StuckTicks = 0; continue; }
                    dir = toGoal.Normalized;
                }
                else if (field != null && field.TryNext(cx, cy, out FixVec2 next))
                {
                    dir = (next - p.Value).Normalized;
                    // Blend toward the exact target when it is close to avoid zig-zag.
                    if (remaining < Fix64.Two) dir = (dir + toGoal.Normalized).Normalized;
                }
                else
                {
                    dir = toGoal.Normalized;   // unreachable / no field yet: straight line
                }

                if (dir == FixVec2.Zero) { m.Moving = false; continue; }
                Fix64 step = FixMath.Min(maxStep, remaining);
                m.Velocity = dir * step;
            }

            // 2. Separation from old positions (symmetric, deterministic order).
            EnsurePush(w.Movers.Count);
            for (int i = 0; i < w.Movers.Count; i++) _push[i] = FixVec2.Zero;
            for (int i = 0; i < w.Movers.Count; i++)
            {
                int e = w.Movers.EntityAt(i);
                ref Position p = ref w.Positions.Get(e);
                ref Mover m = ref w.Movers.At(i);
                _neighbours.Clear();
                w.Units.Query(p.Value, Fix64.Two, _neighbours);
                foreach (int o in _neighbours)
                {
                    if (o <= e) continue;   // each pair once
                    ref Position q = ref w.Positions.Get(o);
                    Fix64 minDist = p.Radius + q.Radius;
                    FixVec2 d = p.Value - q.Value;
                    Fix64 distSq = d.LengthSq;
                    if (distSq >= minDist * minDist) continue;
                    Fix64 dist = FixMath.Sqrt(distSq);
                    FixVec2 n = dist.IsZero ? DeterministicNudge(e, o) : d / dist;
                    Fix64 overlap = minDist - dist;
                    // Movers yield less than idle units so a marching column pushes bystanders aside.
                    bool iMoving = m.Moving, oMoving = w.Movers.Get(o).Moving;
                    Fix64 shareI = iMoving == oMoving ? Fix64.Half : iMoving ? Fix64.Ratio(1, 4) : Fix64.Ratio(3, 4);
                    FixVec2 pushI = n * (overlap * shareI);
                    FixVec2 pushO = -n * (overlap * (Fix64.One - shareI));
                    _push[i] += pushI;
                    _push[DenseIndex(w, o)] += pushO;
                }
            }

            // 3. Integrate with map collision.
            for (int i = 0; i < w.Movers.Count; i++)
            {
                ref Mover m = ref w.Movers.At(i);
                FixVec2 push = _push[i].ClampLength(MaxPush);
                FixVec2 delta = m.Velocity + push;
                if (delta == FixVec2.Zero) continue;
                int e = w.Movers.EntityAt(i);
                ref Position p = ref w.Positions.Get(e);
                FixVec2 before = p.Value;

                FixVec2 next = w.Map.ClampInside(p.Value + delta);
                if (w.Map.IsPassable(next)) p.Value = next;
                else
                {
                    FixVec2 slideX = w.Map.ClampInside(new FixVec2(p.Value.X + delta.X, p.Value.Y));
                    FixVec2 slideY = w.Map.ClampInside(new FixVec2(p.Value.X, p.Value.Y + delta.Y));
                    if (!delta.X.IsZero && w.Map.IsPassable(slideX)) p.Value = slideX;
                    else if (!delta.Y.IsZero && w.Map.IsPassable(slideY)) p.Value = slideY;
                }

                if (m.Moving)
                {
                    FixVec2 facing = m.Velocity.Normalized;
                    if (facing != FixVec2.Zero) p.Facing = facing;

                    // Stuck detection: crowd around the goal counts as arrival.
                    Fix64 moved = FixVec2.Distance(before, p.Value);
                    Fix64 expected = m.Speed * dt;
                    if (moved < expected * StuckFraction) m.StuckTicks++; else m.StuckTicks = 0;
                    if (m.StuckTicks >= StuckTicksToArrive)
                    {
                        Fix64 goalDist = m.GoalEntity > 0 ? w.EdgeDistance(e, m.GoalEntity) : FixVec2.Distance(p.Value, m.Target);
                        if (goalDist <= StuckArriveRadius) { m.Moving = false; m.StuckTicks = 0; }
                    }
                    if (FixMath.WithinDistance(p.Value, m.Target, SimConstants.ArriveRadius))
                    {
                        m.Moving = false;
                        m.StuckTicks = 0;
                    }
                }
            }
        }

        private void EnsurePush(int n)
        {
            if (_push.Length < n) _push = new FixVec2[System.Math.Max(n, _push.Length * 2)];
        }

        private static int DenseIndex(World w, int entity) => w.Movers.IndexOf(entity);

        private static FixVec2 DeterministicNudge(int a, int b)
        {
            // Two units exactly on top of each other: pick a fixed direction from their ids.
            int k = (a * 31 + b) & 7;
            return new FixVec2(Fix64.FromInt(FlowField.DX[k]), Fix64.FromInt(FlowField.DY[k])).Normalized;
        }
    }
}
