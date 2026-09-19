using System.Collections.Generic;
using RTS.Sim.Commands;

namespace RTS.Net
{
    /// <summary>Singleplayer: commands are always available, delayed by one tick for uniformity with lockstep.</summary>
    public sealed class LocalCommandSource : ICommandSource
    {
        private readonly Dictionary<int, TickCommands> _scheduled = new Dictionary<int, TickCommands>();
        private int _currentTick;

        public int InputDelay { get; }

        public LocalCommandSource(int inputDelay = 1)
        {
            InputDelay = inputDelay < 0 ? 0 : inputDelay;
        }

        public TickCommands Poll(int tick)
        {
            _currentTick = tick;
            if (_scheduled.TryGetValue(tick, out TickCommands tc))
            {
                _scheduled.Remove(tick);
                tc.SortDeterministic();
                return tc;
            }
            return new TickCommands(tick);
        }

        public void Submit(ICommand command)
        {
            int at = _currentTick + InputDelay;
            if (!_scheduled.TryGetValue(at, out TickCommands tc))
            {
                tc = new TickCommands(at);
                _scheduled.Add(at, tc);
            }
            tc.Commands.Add(command);
        }
    }
}
