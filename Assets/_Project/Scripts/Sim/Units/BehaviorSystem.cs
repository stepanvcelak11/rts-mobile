using System.Collections.Generic;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// The unit finite-state machine. Decides *what* a unit is doing and where it wants to go;
    /// MovementSystem moves it, EconomySystem/ProductionSystem/CombatSystem do the work once it
    /// is in place. States: Idle · Move · AttackMove · Attack · Flee · Gather · ReturnCargo · Build · Dead.
    /// </summary>
    public sealed class BehaviorSystem : ISystem
    {
        private static readonly List<int> Buffer = new List<int>(64);

        public void Step(World w)
        {
            for (int i = 0; i < w.Behaviours.Count; i++)
            {
                int e = w.Behaviours.EntityAt(i);
                ref UnitBehaviour b = ref w.Behaviours.At(i);
                ref Mover m = ref w.Movers.Get(e);

                switch (b.State)
                {
                    case UnitState.Idle: TickIdle(w, e, ref b, ref m); break;
                    case UnitState.Move: TickMove(w, e, ref b, ref m); break;
                    case UnitState.AttackMove: TickAttackMove(w, e, ref b, ref m); break;
                    case UnitState.Attack: TickAttack(w, e, ref b, ref m); break;
                    case UnitState.Flee: TickFlee(w, e, ref b, ref m); break;
                    case UnitState.Gather: TickGather(w, e, ref b, ref m); break;
                    case UnitState.ReturnCargo: TickReturn(w, e, ref b, ref m); break;
                    case UnitState.Build: TickBuild(w, e, ref b, ref m); break;
                    default: m.Moving = false; break;
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

        public static void OrderAttackMove(World w, int entity, FixVec2 target)
        {
            ref UnitBehaviour b = ref w.Behaviours.Get(entity);
            b.TargetPos = w.Map.ClampInside(target);
            b.ResumePos = b.TargetPos;
            b.TargetEntity = 0;
            SetState(w, entity, ref b, UnitState.AttackMove);
        }

        /// <summary>Explicit attack order: no leash, chase until dead.</summary>
        public static void OrderAttack(World w, int entity, int target)
        {
            ref UnitBehaviour b = ref w.Behaviours.Get(entity);
            b.TargetEntity = target;
            b.ResumeState = UnitState.Attack;
            b.LeashOrigin = w.Positions.Get(entity).Value;
            SetState(w, entity, ref b, UnitState.Attack);
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
            ref Mover m = ref w.Movers.Get(entity);
            m.Moving = false;
            m.GoalEntity = 0;
        }

        /// <summary>Auto-engage from Idle/AttackMove: remembers where to go back to.</summary>
        private static void Engage(World w, int e, ref UnitBehaviour b, int target, UnitState resume)
        {
            b.TargetEntity = target;
            b.ResumeState = resume;
            if (resume != UnitState.AttackMove) b.LeashOrigin = w.Positions.Get(e).Value;
            SetState(w, e, ref b, UnitState.Attack);
        }

        // ---- per-state ticks ----------------------------------------------------------

        private static void TickIdle(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            m.Moving = false;
            m.GoalEntity = 0;
            BakedUnit u = w.UnitDefOf(e);
            if (u.Aggro == Aggro.Passive || !u.CanAttack) return;
            if ((w.Tick + e) % 5 != 0) return;   // scan 4× per second, staggered
            int target = FindEnemy(w, e, u.Los, includeBuildings: u.Aggro == Aggro.Aggressive);
            if (target != 0) Engage(w, e, ref b, target, UnitState.Idle);
        }

        private static void TickMove(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            FixVec2 pos = w.Positions.Get(e).Value;
            if (FixMath.WithinDistance(pos, b.TargetPos, SimConstants.ArriveRadius) || (m.Target == b.TargetPos && !m.Moving && m.GoalEntity == 0 && b.Timer > 0))
            {
                m.Moving = false;
                b.Timer = 0;
                SetState(w, e, ref b, UnitState.Idle);
                return;
            }
            m.Target = b.TargetPos;
            m.GoalEntity = 0;
            m.Moving = true;
            b.Timer = 1;   // "movement started" marker so a stuck-arrival ends the state
        }

        private static void TickAttackMove(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            BakedUnit u = w.UnitDefOf(e);
            if (u.CanAttack && (w.Tick + e) % 4 == 0)
            {
                int target = FindEnemy(w, e, u.Los, includeBuildings: true);
                if (target != 0) { Engage(w, e, ref b, target, UnitState.AttackMove); m.Moving = false; return; }
            }
            FixVec2 pos = w.Positions.Get(e).Value;
            if (FixMath.WithinDistance(pos, b.TargetPos, SimConstants.ArriveRadius) || (m.Target == b.TargetPos && !m.Moving && b.Timer > 0))
            {
                m.Moving = false;
                b.Timer = 0;
                SetState(w, e, ref b, UnitState.Idle);
                return;
            }
            m.Target = b.TargetPos;
            m.GoalEntity = 0;
            m.Moving = true;
            b.Timer = 1;
        }

        private static void TickAttack(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            BakedUnit u = w.UnitDefOf(e);
            if (!u.CanAttack) { Resume(w, e, ref b, ref m); return; }

            if (!IsValidTarget(w, e, b.TargetEntity))
            {
                // Target gone: look for another one nearby before giving up.
                int next = FindEnemy(w, e, u.Los, includeBuildings: true);
                if (next != 0 && b.ResumeState != UnitState.Attack) { b.TargetEntity = next; }
                else if (next != 0 && b.ResumeState == UnitState.Attack) { b.TargetEntity = next; }
                else { Resume(w, e, ref b, ref m); return; }
            }

            // Leash for auto-engaged units: do not chase forever.
            if (b.ResumeState == UnitState.Idle && u.Aggro != Aggro.Aggressive)
            {
                FixVec2 pos = w.Positions.Get(e).Value;
                if (!FixMath.WithinDistance(pos, b.LeashOrigin, u.LeashRange))
                {
                    b.TargetPos = b.LeashOrigin;
                    b.TargetEntity = 0;
                    SetState(w, e, ref b, UnitState.Move);
                    return;
                }
            }

            BakedAttack attack = ChooseAttack(w, e, u, b.TargetEntity);
            Fix64 dist = w.EdgeDistance(e, b.TargetEntity);
            if (dist <= attack.Range)
            {
                m.Moving = false;
                m.GoalEntity = 0;
                FaceTowards(w, e, w.TargetPoint(b.TargetEntity));
            }
            else
            {
                bool targetIsUnit = w.Positions.Has(b.TargetEntity);
                m.Target = targetIsUnit ? w.TargetPoint(b.TargetEntity) : ApproachPoint(w, e, b.TargetEntity);
                m.GoalEntity = targetIsUnit ? -1 : b.TargetEntity;
                m.Moving = true;
            }
        }

        private static void Resume(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            b.TargetEntity = 0;
            m.Moving = false;
            if (b.ResumeState == UnitState.AttackMove)
            {
                b.TargetPos = b.ResumePos;
                SetState(w, e, ref b, UnitState.AttackMove);
            }
            else
            {
                SetState(w, e, ref b, UnitState.Idle);
            }
            b.ResumeState = UnitState.Idle;
        }

        private static void TickFlee(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            int player = w.Identities.Get(e).Player;
            if (!w.Identities.Has(b.TargetEntity) || w.Constructions.Has(b.TargetEntity))
            {
                b.TargetEntity = w.FindNearestDropOff(w.Positions.Get(e).Value, player, 0);
                if (b.TargetEntity == 0) { m.Moving = false; SetState(w, e, ref b, UnitState.Idle); return; }
            }
            if (w.IsAdjacent(e, b.TargetEntity, Fix64.Two))
            {
                m.Moving = false;
                b.TargetEntity = 0;
                SetState(w, e, ref b, UnitState.Idle);
                return;
            }
            m.Target = ApproachPoint(w, e, b.TargetEntity);
            m.GoalEntity = b.TargetEntity;
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
                m.GoalEntity = 0;
                FaceTowards(w, e, w.Footprints.Get(b.TargetEntity).Center);
            }
            else
            {
                m.Target = ApproachPoint(w, e, b.TargetEntity);
                m.GoalEntity = b.TargetEntity;
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
                m.GoalEntity = 0;
            }
            else
            {
                m.Target = ApproachPoint(w, e, b.TargetEntity);
                m.GoalEntity = b.TargetEntity;
                m.Moving = true;
            }
        }

        private static void TickBuild(World w, int e, ref UnitBehaviour b, ref Mover m)
        {
            if (!w.Constructions.Has(b.TargetEntity))
            {
                m.Moving = false;
                b.TargetEntity = 0;
                SetState(w, e, ref b, UnitState.Idle);
                return;
            }

            if (w.IsAdjacent(e, b.TargetEntity, SimConstants.InteractReach))
            {
                m.Moving = false;
                m.GoalEntity = 0;
                w.Constructions.Get(b.TargetEntity).BuildersThisTick++;
                FaceTowards(w, e, w.Footprints.Get(b.TargetEntity).Center);
            }
            else
            {
                m.Target = ApproachPoint(w, e, b.TargetEntity);
                m.GoalEntity = b.TargetEntity;
                m.Moving = true;
            }
        }

        // ---- combat helpers -------------------------------------------------------------

        public static bool IsValidTarget(World w, int attacker, int target)
        {
            if (target == 0 || !w.Identities.TryGet(target, out Identity t)) return false;
            if (t.Kind == EntityKind.ResourceNode || t.Kind == EntityKind.Projectile) return false;
            if (!World.AreEnemies(w.Identities.Get(attacker).Player, t.Player)) return false;
            if (w.Behaviours.TryGet(target, out UnitBehaviour tb) && tb.State == UnitState.Dead) return false;
            return true;
        }

        /// <summary>Melee attack when the target is adjacent and the unit has one, otherwise the primary attack.</summary>
        public static BakedAttack ChooseAttack(World w, int attacker, BakedUnit u, int target)
        {
            if (u.Attacks.Length > 1)
            {
                Fix64 d = w.EdgeDistance(attacker, target);
                for (int i = 1; i < u.Attacks.Length; i++)
                    if (u.Attacks[i].Type == DamageType.Melee && d <= u.Attacks[i].Range) return u.Attacks[i];
            }
            return u.Attacks[0];
        }

        /// <summary>Nearest enemy unit within <paramref name="radius"/> (edge distance); enemy buildings only if asked and no unit is found.</summary>
        public static int FindEnemy(World w, int e, Fix64 radius, bool includeBuildings)
        {
            int me = w.Identities.Get(e).Player;
            Position p = w.Positions.Get(e);
            int best = 0;
            Fix64 bestD = radius;

            Buffer.Clear();
            w.Units.Query(p.Value, radius + Fix64.One, Buffer);
            foreach (int o in Buffer)
            {
                if (o == e) continue;
                Identity id = w.Identities.Get(o);
                if (!World.AreEnemies(me, id.Player)) continue;
                if (w.Behaviours.Get(o).State == UnitState.Dead) continue;
                Fix64 d = w.EdgeDistance(e, o);
                if (d < bestD || (d == bestD && o < best)) { bestD = d; best = o; }
            }
            if (best != 0 || !includeBuildings) return best;

            for (int i = 0; i < w.Footprints.Count; i++)
            {
                int o = w.Footprints.EntityAt(i);
                Identity id = w.Identities.Get(o);
                if (id.Kind != EntityKind.Building || !World.AreEnemies(me, id.Player)) continue;
                Fix64 d = w.EdgeDistance(e, o);
                if (d < bestD || (d == bestD && o < best)) { bestD = d; best = o; }
            }
            return best;
        }

        // ---- helpers ------------------------------------------------------------------

        /// <summary>Closest point on the target footprint edge, pushed out by the unit radius.</summary>
        public static FixVec2 ApproachPoint(World w, int unit, int target)
        {
            ref Position p = ref w.Positions.Get(unit);
            Footprint fp = w.Footprints.Get(target);
            Fix64 pad = p.Radius + Fix64.Ratio(1, 4);
            Fix64 minX = Fix64.FromInt(fp.X) - pad, maxX = Fix64.FromInt(fp.X + fp.W) + pad;
            Fix64 minY = Fix64.FromInt(fp.Y) - pad, maxY = Fix64.FromInt(fp.Y + fp.H) + pad;
            Fix64 x = FixMath.Clamp(p.Value.X, minX, maxX);
            Fix64 y = FixMath.Clamp(p.Value.Y, minY, maxY);
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
