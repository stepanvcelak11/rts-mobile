using RTS.Sim.Commands;

namespace RTS.Net
{
    /// <summary>
    /// The single seam between "who decides" and the simulation. Singleplayer, replay and
    /// lockstep multiplayer are just different implementations. The game loop calls
    /// <see cref="Poll"/> for the current tick and stalls while it returns null.
    /// </summary>
    public interface ICommandSource
    {
        /// <summary>Commands for <paramref name="tick"/>, or null if they are not available yet.</summary>
        TickCommands Poll(int tick);

        /// <summary>Schedules a local intent; it will execute at currentTick + InputDelay.</summary>
        void Submit(ICommand command);

        /// <summary>Tick-count latency between Submit and execution.</summary>
        int InputDelay { get; }
    }
}
