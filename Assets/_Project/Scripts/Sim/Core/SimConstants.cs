namespace RTS.Sim.Core
{
    /// <summary>Global simulation constants. Changing these changes replay compatibility.</summary>
    public static class SimConstants
    {
        /// <summary>Simulation ticks per second.</summary>
        public const int TickRate = 20;

        /// <summary>Seconds per tick as fixed point (1/20).</summary>
        public static readonly Fix64 TickSeconds = Fix64.Ratio(1, TickRate);

        /// <summary>Converts a duration in seconds (data) to ticks, rounding up so 0.1 s is at least 1 tick.</summary>
        public static int SecondsToTicks(decimal seconds)
        {
            decimal ticks = seconds * TickRate;
            int whole = (int)ticks;
            return ticks > whole ? whole + 1 : whole;
        }

        /// <summary>Entity id meaning "none".</summary>
        public const int NoEntity = 0;

        /// <summary>Player index used for neutral things (resource nodes, wildlife).</summary>
        public const int NeutralPlayer = -1;

        /// <summary>Owner of wild animals and treasure guardians: hostile to every real player.</summary>
        public const int WildPlayer = -2;

        /// <summary>Radius (cells) inside which a unit counts as "arrived" at a move target.</summary>
        public static readonly Fix64 ArriveRadius = Fix64.Ratio(1, 4);

        /// <summary>Extra reach (cells) beyond the target footprint edge for gathering/building.</summary>
        public static readonly Fix64 InteractReach = Fix64.Ratio(3, 4);
        /// <summary>Ticks a worker may stand without progress before it is considered stalled.</summary>
        public const int StallTicks = 20;
        /// <summary>Extra reach granted to a stalled worker (crowd around a bush or a drop-off).</summary>
        public static readonly Fix64 StallReachSlack = Fix64.One;
        /// <summary>Gathered resources reach the stockpile continuously (AoE3); a deposit event fires every this many units per villager.</summary>
        public static readonly Fix64 TrickleEventEvery = Fix64.FromInt(5);
        /// <summary>Villagers working within this distance of an own camp that accepts the resource gather faster.</summary>
        public static readonly Fix64 CampAuraRadius = Fix64.FromInt(10);
        public static readonly Fix64 CampAuraBonus = Fix64.Ratio(5, 4);
        /// <summary>How far a stalled gatherer looks for a reachable node of the same resource before the wide search.</summary>
        public static readonly Fix64 StallRetargetRadius = Fix64.FromInt(4);

        /// <summary>How far a villager searches for another node of the same resource after depletion.</summary>
        public static readonly Fix64 NodeSearchRadius = Fix64.FromInt(20);
    }
}
