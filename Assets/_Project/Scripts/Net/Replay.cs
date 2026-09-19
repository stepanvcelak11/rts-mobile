using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RTS.Sim.Commands;
using RTS.Sim.Model;

namespace RTS.Net
{
    /// <summary>
    /// Replay file: header (config) followed by one record per tick that had commands,
    /// plus a hash checkpoint every <see cref="HashInterval"/> ticks. Small enough to attach to
    /// a bug report; deterministic enough to reproduce it.
    /// </summary>
    public static class ReplayFormat
    {
        public const uint Magic = 0x52545352;   // "RSTR"
        public const ushort Version = 1;
        public const int HashInterval = 20;    // once per second at 20 Hz

        public const byte RecCommands = 1;
        public const byte RecHash = 2;
        public const byte RecEnd = 3;

        public static void WriteConfig(BinaryWriter w, WorldConfig c)
        {
            w.Write(c.Seed);
            w.Write(c.PlayerCount);
            w.Write(c.MapId ?? "");
            w.Write(c.CivIds.Length);
            foreach (string civ in c.CivIds) w.Write(civ ?? "");
            w.Write(c.SpawnStartingUnits);
        }

        public static WorldConfig ReadConfig(BinaryReader r)
        {
            var c = new WorldConfig { Seed = r.ReadUInt32(), PlayerCount = r.ReadInt32(), MapId = r.ReadString() };
            int n = r.ReadInt32();
            c.CivIds = new string[n];
            for (int i = 0; i < n; i++) c.CivIds[i] = r.ReadString();
            c.SpawnStartingUnits = r.ReadBoolean();
            return c;
        }
    }

    /// <summary>Wraps another command source and records everything it hands out.</summary>
    public sealed class ReplayRecorder : ICommandSource, IDisposable
    {
        private readonly ICommandSource _inner;
        private readonly BinaryWriter _w;
        private bool _closed;

        public int InputDelay => _inner.InputDelay;

        public ReplayRecorder(ICommandSource inner, Stream output, WorldConfig config)
        {
            _inner = inner;
            _w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: false);
            _w.Write(ReplayFormat.Magic);
            _w.Write(ReplayFormat.Version);
            ReplayFormat.WriteConfig(_w, config);
        }

        public TickCommands Poll(int tick)
        {
            TickCommands tc = _inner.Poll(tick);
            if (!_closed && tc != null && tc.Commands.Count > 0)
            {
                _w.Write(ReplayFormat.RecCommands);
                _w.Write(tc.Tick);
                _w.Write(tc.Commands.Count);
                foreach (ICommand c in tc.Commands) CommandCodec.Write(_w, c);
            }
            return tc;
        }

        public void Submit(ICommand command) => _inner.Submit(command);

        /// <summary>Call after each World.Step so hash checkpoints land in the file.</summary>
        public void RecordHash(int tickJustCompleted, ulong hash)
        {
            if (_closed || tickJustCompleted % ReplayFormat.HashInterval != 0) return;
            _w.Write(ReplayFormat.RecHash);
            _w.Write(tickJustCompleted);
            _w.Write(hash);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _w.Write(ReplayFormat.RecEnd);
            _w.Flush();
            _w.Dispose();
        }
    }

    /// <summary>Plays a recorded command stream back. Submit() is ignored (a replay is read-only).</summary>
    public sealed class ReplayCommandSource : ICommandSource
    {
        private readonly Dictionary<int, TickCommands> _byTick = new Dictionary<int, TickCommands>();
        private readonly Dictionary<int, ulong> _hashes = new Dictionary<int, ulong>();

        public WorldConfig Config { get; }
        public int LastTick { get; }
        public int InputDelay => 0;

        public ReplayCommandSource(Stream input)
        {
            using (var r = new BinaryReader(input, Encoding.UTF8, leaveOpen: false))
            {
                if (r.ReadUInt32() != ReplayFormat.Magic) throw new InvalidDataException("Not a replay file");
                ushort version = r.ReadUInt16();
                if (version != ReplayFormat.Version) throw new InvalidDataException("Unsupported replay version " + version);
                Config = ReplayFormat.ReadConfig(r);

                int last = 0;
                while (true)
                {
                    byte rec = r.ReadByte();
                    if (rec == ReplayFormat.RecEnd) break;
                    int tick = r.ReadInt32();
                    if (tick > last) last = tick;
                    if (rec == ReplayFormat.RecCommands)
                    {
                        int n = r.ReadInt32();
                        var tc = new TickCommands(tick);
                        for (int i = 0; i < n; i++) tc.Commands.Add(CommandCodec.Read(r));
                        _byTick[tick] = tc;
                    }
                    else if (rec == ReplayFormat.RecHash)
                    {
                        _hashes[tick] = r.ReadUInt64();
                    }
                    else throw new InvalidDataException("Unknown replay record " + rec);
                }
                LastTick = last;
            }
        }

        public TickCommands Poll(int tick) => _byTick.TryGetValue(tick, out TickCommands tc) ? tc : new TickCommands(tick);

        public void Submit(ICommand command) { /* replays are read-only */ }

        /// <summary>Recorded hash after the given tick, if a checkpoint exists.</summary>
        public bool TryGetHash(int tick, out ulong hash) => _hashes.TryGetValue(tick, out hash);
    }
}
