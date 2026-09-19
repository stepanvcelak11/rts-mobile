using System.Collections.Generic;
using RTS.Data;
using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    /// <summary>
    /// Applies data modifiers (civilization passives, technologies, shipments) to a player's
    /// <see cref="BakedDefs"/>. Runs at match start for civs and at research time for techs, so
    /// the per-tick systems only ever read plain numbers.
    ///
    /// target: "*" | "unit.x" | "bld.x" | "tag.x" | "shipment" | "player"
    /// stat:   hp · speed · los · armor.melee|ranged|siege · cost.&lt;res&gt; · trainSeconds · buildSeconds ·
    ///         attacks.*.damage|range|cooldownSeconds (or attacks.&lt;id&gt;.…) · gather.&lt;node&gt; · xpCost
    /// op:     mul | add | set
    /// </summary>
    public static class Modifiers
    {
        public static void ApplyCiv(BakedDefs defs, CivDef civ)
        {
            foreach (var kv in civ.replacements)
                if (defs.Data.TryUnitIndex(kv.Key, out int from) && defs.Data.TryUnitIndex(kv.Value, out int to))
                    defs.Replacements[from] = to;
            foreach (ModifierDef m in civ.modifiers) Apply(defs, m);
        }

        /// <summary>Applies one modifier. Returns the unit indices whose hp changed (callers rescale live units).</summary>
        public static List<int> Apply(BakedDefs defs, ModifierDef m)
        {
            var touchedUnits = new List<int>();
            if (m == null || string.IsNullOrEmpty(m.stat) || string.IsNullOrEmpty(m.target)) return touchedUnits;
            Fix64 value = Fix64.FromDecimal(m.value);

            if (m.target == "shipment")
            {
                if (m.stat == "xpCost") defs.ShipmentXpCostMultiplier = Combine(defs.ShipmentXpCostMultiplier, m.op, value);
                return touchedUnits;
            }
            if (m.target == "player") return touchedUnits;   // stockpile effects are applied by the shipment itself

            if (m.stat.StartsWith("gather."))
            {
                string node = m.stat.Substring("gather.".Length);
                for (int i = 0; i < defs.Nodes.Length; i++)
                    if (node == "*" || defs.Nodes[i].Id == node)
                        defs.GatherMultiplier[i] = Combine(defs.GatherMultiplier[i], m.op, value);
                return touchedUnits;
            }

            foreach (BakedUnit u in defs.Units)
                if (Matches(defs, m.target, u.Id, u.Tags) && ApplyToUnit(defs, u, m, value)) touchedUnits.Add(u.Index);
            foreach (BakedBuilding b in defs.Buildings)
                if (Matches(defs, m.target, b.Id, b.Tags)) ApplyToBuilding(defs, b, m, value);
            return touchedUnits;
        }

        private static bool Matches(BakedDefs defs, string target, string id, int[] tags)
        {
            if (target == "*") return true;
            if (target == id) return true;
            return target.StartsWith("tag.") && defs.TryTag(target, out int t) && BakedDefs.HasTag(tags, t);
        }

        /// <summary>Returns true when max hp changed.</summary>
        private static bool ApplyToUnit(BakedDefs defs, BakedUnit u, ModifierDef m, Fix64 v)
        {
            switch (m.stat)
            {
                case "hp": u.Hp = Combine(u.Hp, m.op, v); return true;
                case "speed": u.Speed = Combine(u.Speed, m.op, v); return false;
                case "los": u.Los = Combine(u.Los, m.op, v); return false;
                case "trainSeconds": u.TrainTicks = Ticks(u.TrainTicks, m.op, v); return false;
                case "gatherRate": u.GatherRateMultiplier = Combine(u.GatherRateMultiplier, m.op, v); return false;
            }
            if (TryArmor(m.stat, out int armor)) { u.Armor[armor] = Combine(u.Armor[armor], m.op, v); return false; }
            if (TryCost(defs, m.stat, out int res)) { u.Cost[res] = Combine(u.Cost[res], m.op, v); return false; }
            if (m.stat.StartsWith("attacks."))
                foreach (BakedAttack a in u.Attacks) ApplyToAttack(a, m, v);
            return false;
        }

        private static void ApplyToBuilding(BakedDefs defs, BakedBuilding b, ModifierDef m, Fix64 v)
        {
            switch (m.stat)
            {
                case "hp": b.Hp = Combine(b.Hp, m.op, v); return;
                case "los": b.Los = Combine(b.Los, m.op, v); return;
                case "buildSeconds": b.BuildTicks = Ticks(b.BuildTicks, m.op, v); return;
            }
            if (TryArmor(m.stat, out int armor)) { b.Armor[armor] = Combine(b.Armor[armor], m.op, v); return; }
            if (TryCost(defs, m.stat, out int res)) { b.Cost[res] = Combine(b.Cost[res], m.op, v); return; }
            if (m.stat.StartsWith("attacks.") && b.Attack != null) ApplyToAttack(b.Attack, m, v);
        }

        private static void ApplyToAttack(BakedAttack a, ModifierDef m, Fix64 v)
        {
            // attacks.<id|*>.<field>
            string[] parts = m.stat.Split('.');
            if (parts.Length != 3) return;
            if (parts[1] != "*" && parts[1] != a.Id) return;
            switch (parts[2])
            {
                case "damage": a.Damage = Combine(a.Damage, m.op, v); break;
                case "range": a.Range = Combine(a.Range, m.op, v); break;
                case "cooldownSeconds": a.CooldownTicks = Ticks(a.CooldownTicks, m.op, v); break;
            }
        }

        private static bool TryArmor(string stat, out int index)
        {
            index = -1;
            if (!stat.StartsWith("armor.")) return false;
            switch (stat.Substring(6)) { case "melee": index = 0; break; case "ranged": index = 1; break; case "siege": index = 2; break; }
            return index >= 0;
        }

        private static bool TryCost(BakedDefs defs, string stat, out int res)
        {
            res = -1;
            return stat.StartsWith("cost.") && defs.Data.TryResourceIndex(stat.Substring(5), out res);
        }

        private static Fix64 Combine(Fix64 current, string op, Fix64 v)
        {
            switch (op)
            {
                case "add": return current + v;
                case "set": return v;
                default: return current * v;
            }
        }

        private static int Ticks(int currentTicks, string op, Fix64 v)
        {
            switch (op)
            {
                case "add": return currentTicks + (v * SimConstants.TickRate).RoundToInt();
                case "set": return (v * SimConstants.TickRate).RoundToInt();
                default: return (Fix64.FromInt(currentTicks) * v).RoundToInt();
            }
        }
    }
}
