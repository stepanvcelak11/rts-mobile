using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    public enum AiDifficulty : byte { None = 0, Easy = 1, Normal = 2, Hard = 3 }

    /// <summary>Per-player state: economy, age, Home-City XP, research, and the player's own baked definitions.</summary>
    public sealed class PlayerState : IHashable
    {
        public readonly int Index;
        public readonly int CivIndex;
        public readonly Fix64[] Stockpile;   // per resource index
        public int Population;               // used
        public int PopulationCap;            // provided by buildings + base
        public bool Alive = true;

        /// <summary>Definitions with this civilization's passives and researched techs applied.</summary>
        public BakedDefs Defs;

        public int Age;                      // index into Defs.Ages
        public int AgeUpBuilding;            // entity researching the next age, 0 = none
        public int AgeUpRemaining;           // ticks

        public Fix64 Xp;                     // Home-City experience
        public int ShipmentsSent;
        public readonly bool[] Researched;   // per tech index (techs and once-per-game shipments)
        public int ShipmentsAvailable;       // computed every tick from Xp
        public readonly Fix64[] MarketPrice; // gold per 100 of each resource (buy price; sell = 70 %)

        public AiDifficulty Ai = AiDifficulty.None;
        public bool IsAi => Ai != AiDifficulty.None;

        // Statistics for the score screen / AI decisions.
        public int UnitsKilled, UnitsLost, BuildingsRazed;

        // Skirmish-AI memory (part of the hashed state so replays stay exact).
        public int AiLastWaveTick = -100000;
        public int AiAlertTick = -100000;      // last tick one of our things was hit
        public FixVec2 AiAlertPos;
        public int AiWaveTarget;               // building the current wave is marching on

        public PlayerState(int index, int civIndex, int resourceCount, int techCount)
        {
            Index = index;
            CivIndex = civIndex;
            Stockpile = new Fix64[resourceCount];
            Researched = new bool[techCount];
            MarketPrice = new Fix64[resourceCount];
            for (int i = 0; i < resourceCount; i++) MarketPrice[i] = Fix64.FromInt(100);
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

        public static Fix64 TotalCost(Fix64[] cost)
        {
            Fix64 sum = Fix64.Zero;
            for (int i = 0; i < cost.Length; i++) sum += cost[i];
            return sum;
        }

        public void Hash(ref Hasher h)
        {
            h.Add(Index); h.Add(CivIndex);
            for (int i = 0; i < Stockpile.Length; i++) h.Add(Stockpile[i]);
            h.Add(Population); h.Add(PopulationCap); h.Add(Alive);
            h.Add(Age); h.Add(AgeUpBuilding); h.Add(AgeUpRemaining);
            h.Add(Xp); h.Add(ShipmentsSent); h.Add(ShipmentsAvailable);
            for (int i = 0; i < MarketPrice.Length; i++) h.Add(MarketPrice[i]);
            for (int i = 0; i < Researched.Length; i++) h.Add(Researched[i]);
            h.Add((byte)Ai);
            h.Add(UnitsKilled); h.Add(UnitsLost); h.Add(BuildingsRazed);
            h.Add(AiLastWaveTick); h.Add(AiAlertTick); h.Add(AiAlertPos); h.Add(AiWaveTarget);
        }
    }
}
