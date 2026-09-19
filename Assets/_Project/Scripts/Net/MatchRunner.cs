using System;
using RTS.Data;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;

namespace RTS.Net
{
    /// <summary>
    /// Owns a World and an ICommandSource and advances the world with a fixed-step
    /// accumulator. Engine-agnostic: Unity feeds it Time.deltaTime, tests feed it whole
    /// ticks, a headless server feeds it a Stopwatch.
    /// </summary>
    public sealed class MatchRunner
    {
        public World World { get; }
        public ICommandSource Source { get; }

        /// <summary>Fraction of the way from the previous tick to the next one (presentation interpolation).</summary>
        public float Alpha => (float)(_accumulator / TickSecondsDouble);   // presentation-only

        /// <summary>Set when Poll() returned null (waiting for peers); presentation may show a "connection" hint.</summary>
        public bool Stalled { get; private set; }

        /// <summary>Raised after every completed tick, before the next one begins.</summary>
        public event Action<World> TickCompleted;

        private const double TickSecondsDouble = 1.0 / SimConstants.TickRate;   // presentation-only
        private const int MaxTicksPerFrame = 5;
        private double _accumulator;

        public MatchRunner(GameData data, WorldConfig config, ICommandSource source)
        {
            World = new World(data, config);
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>Advance with wall-clock delta; runs 0..MaxTicksPerFrame ticks.</summary>
        public void Advance(double deltaSeconds)
        {
            if (deltaSeconds < 0) deltaSeconds = 0;
            _accumulator += deltaSeconds;
            int ticks = 0;
            while (_accumulator >= TickSecondsDouble && ticks < MaxTicksPerFrame)
            {
                if (!StepOnce()) break;   // stalled: keep the accumulator, retry next frame
                _accumulator -= TickSecondsDouble;
                ticks++;
            }
            // Never let a long hitch queue up seconds of catch-up.
            if (_accumulator > TickSecondsDouble * MaxTicksPerFrame) _accumulator = TickSecondsDouble * MaxTicksPerFrame;
        }

        /// <summary>Runs exactly one tick if commands are available. Returns false when stalled.</summary>
        public bool StepOnce()
        {
            TickCommands tc = Source.Poll(World.Tick);
            if (tc == null) { Stalled = true; return false; }
            Stalled = false;
            World.Step(tc);
            (Source as ReplayRecorder)?.RecordHash(World.Tick, World.LastHash);
            TickCompleted?.Invoke(World);
            return true;
        }

        /// <summary>Runs <paramref name="ticks"/> ticks back to back (tests, headless, replays).</summary>
        public void RunTicks(int ticks)
        {
            for (int i = 0; i < ticks; i++)
                if (!StepOnce()) throw new InvalidOperationException("Command source stalled at tick " + World.Tick);
        }
    }
}
