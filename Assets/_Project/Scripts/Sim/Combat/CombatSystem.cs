using System.Collections.Generic;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// Fires attacks and resolves damage. Units in the Attack state shoot when their target is in
    /// range and their cooldown has elapsed; turrets (town centers, towers) pick the nearest enemy
    /// unit in range; projectiles fly for a fixed number of ticks and hit on arrival.
    /// Damage = base × Π(multipliers for the target's tags) × (1 − armor[type]).
    /// </summary>
    public sealed class CombatSystem : ISystem
    {
        private static readonly List<int> Buffer = new List<int>(64);
        private static readonly Fix64 KillXpShare = Fix64.Half;

        public void Step(World w)
        {
            StepUnits(w);
            StepTurrets(w);
            StepProjectiles(w);
        }

        private static void StepUnits(World w)
        {
            for (int i = 0; i < w.Behaviours.Count; i++)
            {
                ref UnitBehaviour b = ref w.Behaviours.At(i);
                if (b.Cooldown > 0) b.Cooldown--;
                if (b.State != UnitState.Attack || b.Cooldown > 0) continue;
                int e = w.Behaviours.EntityAt(i);
                if (!BehaviorSystem.IsValidTarget(w, e, b.TargetEntity)) continue;

                Identity id = w.Identities.Get(e);
                BakedUnit u = w.DefsOf(id.Player).Units[id.DefIndex];
                if (!u.CanAttack) continue;
                BakedAttack attack = BehaviorSystem.ChooseAttack(w, e, u, b.TargetEntity);
                Fix64 dist = w.EdgeDistance(e, b.TargetEntity);
                if (dist > attack.Range || dist < attack.MinRange) continue;

                int attackIndex = System.Array.IndexOf(u.Attacks, attack);
                b.Cooldown = attack.CooldownTicks;
                if (attack.HasProjectile)
                    w.SpawnProjectile(e, id.Player, EntityKind.Unit, id.DefIndex, attackIndex, b.TargetEntity, w.Positions.Get(e).Value, attack.ProjectileSpeed);
                else
                    ApplyDamage(w, b.TargetEntity, e, id.Player, attack);
            }
        }

        private static void StepTurrets(World w)
        {
            for (int i = 0; i < w.Turrets.Count; i++)
            {
                ref Turret t = ref w.Turrets.At(i);
                if (t.Cooldown > 0) { t.Cooldown--; continue; }
                int e = w.Turrets.EntityAt(i);
                Identity id = w.Identities.Get(e);
                BakedBuilding b = w.DefsOf(id.Player).Buildings[id.DefIndex];
                if (b.Attack == null) continue;

                if (t.Target != 0 && (!w.Positions.Has(t.Target) || !World.AreEnemies(id.Player, w.Identities.Get(t.Target).Player)
                                      || !InTurretRange(w, e, t.Target, b.Attack.Range)))
                    t.Target = 0;
                if (t.Target == 0 && (w.Tick + e) % 4 == 0) t.Target = FindEnemyUnitNear(w, e, id.Player, b.Attack.Range);
                if (t.Target == 0) continue;

                t.Cooldown = b.Attack.CooldownTicks;
                w.SpawnProjectile(e, id.Player, EntityKind.Building, id.DefIndex, 0, t.Target, w.Footprints.Get(e).Center, b.Attack.ProjectileSpeed);
            }
        }

        private static bool InTurretRange(World w, int building, int target, Fix64 range)
        {
            Footprint fp = w.Footprints.Get(building);
            Fix64 d = fp.DistanceSqTo(w.Positions.Get(target).Value);
            return d <= range * range;
        }

        private static int FindEnemyUnitNear(World w, int building, int player, Fix64 range)
        {
            Footprint fp = w.Footprints.Get(building);
            Buffer.Clear();
            int r = range.CeilToInt();
            w.Units.QueryRect(fp.X, fp.Y, fp.X + fp.W - 1, fp.Y + fp.H - 1, r, Buffer);
            int best = 0;
            Fix64 bestD = range * range;
            foreach (int o in Buffer)
            {
                Identity id = w.Identities.Get(o);
                if (!World.AreEnemies(player, id.Player)) continue;
                if (w.Behaviours.Get(o).State == UnitState.Dead) continue;
                Fix64 d = fp.DistanceSqTo(w.Positions.Get(o).Value);
                if (d < bestD || (d == bestD && o < best)) { bestD = d; best = o; }
            }
            return best;
        }

        private static void StepProjectiles(World w)
        {
            for (int i = 0; i < w.Projectiles.Count; i++)
            {
                ref Projectile pr = ref w.Projectiles.At(i);
                int e = w.Projectiles.EntityAt(i);
                pr.TicksLeft--;
                bool targetAlive = w.Identities.Has(pr.Target);
                FixVec2 to = targetAlive ? w.TargetPoint(pr.Target) : w.Positions.Get(e).Value;
                if (pr.TicksLeft > 0)
                {
                    // Homing: re-aim at the target's current position every tick.
                    Fix64 t = Fix64.Ratio(pr.TotalTicks - pr.TicksLeft, pr.TotalTicks);
                    ref Position pos = ref w.Positions.Get(e);
                    pos.Value = FixVec2.Lerp(pr.Start, to, t);
                    pos.Facing = (to - pos.Value).Normalized;
                    continue;
                }
                if (targetAlive)
                {
                    BakedAttack attack = pr.SourceKind == EntityKind.Unit
                        ? w.DefsOf(pr.SourcePlayer).Units[pr.SourceDef].Attacks[pr.AttackIndex]
                        : w.DefsOf(pr.SourcePlayer).Buildings[pr.SourceDef].Attack;
                    if (attack != null) ApplyDamage(w, pr.Target, pr.Source, pr.SourcePlayer, attack);
                    w.Events.Add(new SimEvent(SimEventKind.ProjectileHit, e, pr.Target));
                }
                w.Despawn(e);
            }
        }

        /// <summary>Resolves one hit. Handles kill credit, retaliation and fleeing.</summary>
        public static void ApplyDamage(World w, int target, int attacker, int attackerPlayer, BakedAttack attack)
        {
            if (!w.Identities.TryGet(target, out Identity tid) || !w.Healths.Has(target)) return;
            Fix64[] armor;
            int[] tags;
            if (tid.Kind == EntityKind.Unit)
            {
                BakedUnit tu = w.DefsOf(tid.Player).Units[tid.DefIndex];
                armor = tu.Armor; tags = tu.Tags;
            }
            else if (tid.Kind == EntityKind.Building)
            {
                BakedBuilding tb = w.DefsOf(tid.Player).Buildings[tid.DefIndex];
                armor = tb.Armor; tags = tb.Tags;
            }
            else return;

            Fix64 mult = attack.MultiplierFor(tags);
            Fix64 reduction = FixMath.Clamp(armor[(int)attack.Type], Fix64.FromInt(-4), Fix64.Ratio(95, 100));
            Fix64 dmg = attack.Damage * mult * (Fix64.One - reduction);
            if (dmg <= Fix64.Zero) return;

            ref Health hp = ref w.Healths.Get(target);
            hp.Hp -= dmg;
            w.Events.Add(new SimEvent(SimEventKind.Damaged, target, attacker, attackerPlayer, dmg));
            if (tid.Player >= 0)
            {
                PlayerState victim = w.Players[tid.Player];
                if (w.Tick - victim.AiAlertTick >= 40 || hp.Hp <= Fix64.Zero)
                    w.Events.Add(new SimEvent(SimEventKind.UnderAttack, target, tid.Player));
                victim.AiAlertTick = w.Tick;
                victim.AiAlertPos = w.TargetPoint(target);
            }

            if (hp.Hp <= Fix64.Zero)
            {
                Kill(w, target, tid, attackerPlayer);
                return;
            }

            if (tid.Kind == EntityKind.Unit)
            {
                ref UnitBehaviour tb = ref w.Behaviours.Get(target);
                tb.LastAttacker = attacker;
                BakedUnit tu = w.DefsOf(tid.Player).Units[tid.DefIndex];

                // Villagers run home when badly hurt.
                if (tu.FleeHpPercent > Fix64.Zero && hp.Hp < hp.MaxHp * tu.FleeHpPercent && tb.State != UnitState.Flee)
                {
                    tb.TargetEntity = 0;
                    BehaviorSystem.SetState(w, target, ref tb, UnitState.Flee);
                    return;
                }
                // Defensive retaliation for idle / working soldiers.
                if (tu.CanAttack && BehaviorSystem.EffectiveAggro(tu, tb.Stance) != Aggro.Passive && attacker != 0 && w.Identities.Has(attacker)
                    && (tb.State == UnitState.Idle || tb.State == UnitState.Move) && BehaviorSystem.IsValidTarget(w, target, attacker))
                {
                    tb.TargetEntity = attacker;
                    tb.ResumeState = UnitState.Idle;
                    tb.LeashOrigin = w.Positions.Get(target).Value;
                    BehaviorSystem.SetState(w, target, ref tb, UnitState.Attack);
                }
            }
        }

        private static void Kill(World w, int target, Identity tid, int killerPlayer)
        {
            if (tid.Kind == EntityKind.Unit)
            {
                ref UnitBehaviour tb = ref w.Behaviours.Get(target);
                if (tb.State == UnitState.Dead) return;
                BehaviorSystem.SetState(w, target, ref tb, UnitState.Dead);
                w.Movers.Get(target).Moving = false;
                BakedUnit tu = w.DefsOf(tid.Player).Units[tid.DefIndex];
                if (tid.Player >= 0) w.Players[tid.Player].UnitsLost++;
                if (killerPlayer >= 0 && killerPlayer < w.Players.Length)
                {
                    w.Players[killerPlayer].UnitsKilled++;
                    w.AddXp(killerPlayer, PlayerState.TotalCost(tu.Cost) * KillXpShare);
                }
            }
            else
            {
                BakedBuilding tb = w.DefsOf(tid.Player).Buildings[tid.DefIndex];
                if (killerPlayer >= 0 && killerPlayer < w.Players.Length)
                {
                    w.Players[killerPlayer].BuildingsRazed++;
                    w.AddXp(killerPlayer, PlayerState.TotalCost(tb.Cost) * KillXpShare);
                }
                w.Despawn(target);
            }
            w.Events.Add(new SimEvent(SimEventKind.Died, target, killerPlayer));
        }
    }
}
