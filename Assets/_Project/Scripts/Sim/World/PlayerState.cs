using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    /// <summary>Per-player economy state: stockpile, population, civilization.</summary>
    public sealed class PlayerState : IHashable
    {
        public readonly int Index;
        public readonly int CivIndex;
        public readonly Fix64[] Stockpile;   // per resource index
        public int Population;               // used
        public int PopulationCap;            // provided by buildings + base
        public bool Alive = true;

        public PlayerState(int index, int civIndex, int resourceCount)
        {
            Index = index;
            CivIndex = civIndex;
            Stockpile = new Fix64[resourceCount];
        }

        public bool CanAfford(Fix64[] cost)
        {
            for (int i = 0; i < cost.Length; i++)
                if (Stockpile[i] < cost[i]) return false;
            return true;
        }

        public void Pay(Fix64[] cost)
        {
            for (int i = 0; i < cost.Length; i++) Stockpile[i] -= cost[i];
        }

        public void Refund(Fix64[] cost, Fix64 cap)
        {
            for (int i = 0; i < cost.Length; i++) Stockpile[i] = FixMath.Min(Stockpile[i] + cost[i], cap);
        }

        public void Hash(ref Hasher h)
        {
            h.Add(Index); h.Add(CivIndex);
            for (int i = 0; i < Stockpile.Length; i++) h.Add(Stockpile[i]);
            h.Add(Population); h.Add(PopulationCap); h.Add(Alive);
        }
    }
}
