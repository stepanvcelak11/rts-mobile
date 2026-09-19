using RTS.Sim.Model;

namespace RTS.Net
{
    /// <summary>
    /// What the input and UI layers need from a running match, without depending on how it is
    /// hosted (Unity bootstrap, headless server, test harness).
    /// </summary>
    public interface IMatchSession
    {
        World World { get; }
        ICommandSource Source { get; }
        /// <summary>Player index this device controls.</summary>
        int LocalPlayer { get; }
        /// <summary>Interpolation fraction between the last two ticks (presentation only).</summary>
        float Alpha { get; }
    }
}
