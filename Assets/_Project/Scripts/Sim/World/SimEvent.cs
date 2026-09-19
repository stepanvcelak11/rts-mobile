using RTS.Sim.Core;

namespace RTS.Sim.Model
{
    public enum SimEventKind : byte
    {
        Spawned,               // Entity
        Despawned,             // Entity
        StateChanged,          // Entity, A = new UnitState
        ResourceDeposited,     // Entity = villager, A = resource index, Amount
        ConstructionStarted,   // Entity = site
        ConstructionFinished,  // Entity = building
        UnitTrained,           // Entity = new unit, A = building
        NodeDepleted,          // Entity = node
        CommandRejected,       // A = player, B = reason (CommandRejectReason)
        Damaged,               // Entity = victim, A = attacker, Amount = damage
        Died,                  // Entity = unit or building, A = killer player
        ProjectileHit,         // Entity = projectile, A = target
        AgeAdvanced,           // Entity = 0, A = player, B = new age index
        ResearchFinished,      // Entity = building, A = player, B = tech index
        ShipmentArrived,       // Entity = town center, A = player, B = tech index
        MatchEnded,            // A = winner (-2 draw)
        UnderAttack,           // Entity = victim, A = owner (throttled ping for the HUD)
    }

    public enum CommandRejectReason : byte
    {
        None = 0,
        NotAffordable,
        BadPlacement,
        NotOwner,
        InvalidTarget,
        QueueFull,
        PopulationCap,
        LimitReached,
        WrongAge,
        AlreadyResearched,
        NotEnoughXp,
        Busy,
    }

    /// <summary>
    /// Something that happened during a tick that presentation may want to react to
    /// (spawn a view, play a sound). Events are cleared at the start of every tick;
    /// they are output only and never feed back into the simulation.
    /// </summary>
    public readonly struct SimEvent
    {
        public readonly SimEventKind Kind;
        public readonly int Entity;
        public readonly int A;
        public readonly int B;
        public readonly Fix64 Amount;

        public SimEvent(SimEventKind kind, int entity, int a = 0, int b = 0, Fix64 amount = default)
        {
            Kind = kind; Entity = entity; A = a; B = b; Amount = amount;
        }
    }
}
