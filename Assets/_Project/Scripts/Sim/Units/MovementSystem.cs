using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// Integrates unit positions toward their Mover target in a straight line, refusing to
    /// enter impassable cells (slides along one axis when blocked). Phase 3 replaces the
    /// straight line with flow-field steering + separation; the collision rule stays.
    /// </summary>
    public sealed class MovementSystem : ISystem
    {
        public void Step(World w)
        {
            Fix64 dt = SimConstants.TickSeconds;
            for (int i = 0; i < w.Movers.Count; i++)
            {
                ref Mover m = ref w.Movers.At(i);
                if (!m.Moving) continue;
                int e = w.Movers.EntityAt(i);
                ref Position p = ref w.Positions.Get(e);

                FixVec2 delta = m.Target - p.Value;
                Fix64 distSq = delta.LengthSq;
                if (distSq.IsZero) { continue; }

                Fix64 maxStep = m.Speed * dt;
                FixVec2 step = distSq <= maxStep * maxStep ? delta : delta.Normalized * maxStep;
                FixVec2 next = w.Map.ClampInside(p.Value + step);

                if (w.Map.IsPassable(next))
                {
                    p.Value = next;
                }
                else
                {
                    // Try sliding along X, then along Y.
                    FixVec2 slideX = w.Map.ClampInside(new FixVec2(p.Value.X + step.X, p.Value.Y));
                    FixVec2 slideY = w.Map.ClampInside(new FixVec2(p.Value.X, p.Value.Y + step.Y));
                    if (!step.X.IsZero && w.Map.IsPassable(slideX)) p.Value = slideX;
                    else if (!step.Y.IsZero && w.Map.IsPassable(slideY)) p.Value = slideY;
                    // else: blocked this tick; the behaviour keeps the target and retries
                }

                FixVec2 facing = step.Normalized;
                if (facing != FixVec2.Zero) p.Facing = facing;
            }
        }
    }
}
