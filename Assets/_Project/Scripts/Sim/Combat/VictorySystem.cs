using RTS.Sim.Model;

namespace RTS.Sim.Systems
{
    /// <summary>
    /// A player is defeated when they own no buildings and no villagers. The match ends when at
    /// most one player is left standing. Checked once per second.
    /// </summary>
    public sealed class VictorySystem : ISystem
    {
        public void Step(World w)
        {
            if (w.Winner != -1 || w.Tick % 20 != 19) return;

            int alive = 0, last = -2;
            for (int p = 0; p < w.Players.Length; p++)
            {
                PlayerState ps = w.Players[p];
                if (!ps.Alive) continue;
                bool hasBuilding = false, hasVillager = false;
                for (int i = 0; i < w.Identities.Count && !(hasBuilding || hasVillager); i++)
                {
                    ref Identity id = ref w.Identities.At(i);
                    if (id.Player != p) continue;
                    if (id.Kind == EntityKind.Building) hasBuilding = true;
                    else if (id.Kind == EntityKind.Unit && w.DefsOf(p).Units[id.DefIndex].CanGather) hasVillager = true;
                }
                if (!hasBuilding && !hasVillager) { ps.Alive = false; continue; }
                alive++;
                last = p;
            }

            if (alive <= 1 && w.Players.Length > 1)
            {
                w.Winner = alive == 1 ? last : -2;
                w.Events.Add(new SimEvent(SimEventKind.MatchEnded, 0, w.Winner));
            }
        }
    }
}
