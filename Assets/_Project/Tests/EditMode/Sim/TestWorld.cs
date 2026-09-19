using System.IO;
using RTS.Data;
using RTS.Net;
using RTS.Sim.Model;

namespace RTS.Tests
{
    /// <summary>Shared fixtures: loads the real JSON data from Assets/_Project/Resources/Data.</summary>
    internal static class TestWorld
    {
        private static GameData _data;

        public static string DataRoot
        {
            get
            {
                // Walk up from the test binary until we find the repo root (has Assets/_Project/Resources/Data).
                string dir = TestContextDir();
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "_Project", "Resources", "Data");
                    if (Directory.Exists(candidate)) return candidate;
                    dir = Path.GetDirectoryName(dir);
                }
                throw new DirectoryNotFoundException("Could not locate Assets/_Project/Resources/Data from " + TestContextDir());
            }
        }

        private static string TestContextDir() => Path.GetDirectoryName(typeof(TestWorld).Assembly.Location);

        public static GameData Data => _data ?? (_data = JsonLoader.LoadFromDirectory(DataRoot));

        public static WorldConfig DefaultConfig(uint seed = 1) => new WorldConfig
        {
            Seed = seed,
            PlayerCount = 2,
            MapId = "map.default",
            CivIds = new[] { "civ.crown", "civ.crown" },
            SpawnStartingUnits = true,
        };

        public static MatchRunner NewMatch(uint seed = 1, ICommandSource source = null) =>
            new MatchRunner(Data, DefaultConfig(seed), source ?? new LocalCommandSource());

        /// <summary>Runs ticks and returns every event raised during them.</summary>
        public static System.Collections.Generic.List<SimEvent> RunCollecting(MatchRunner m, int ticks)
        {
            var events = new System.Collections.Generic.List<SimEvent>();
            for (int i = 0; i < ticks; i++)
            {
                m.RunTicks(1);
                events.AddRange(m.World.Events);
            }
            return events;
        }

        public static bool Has(System.Collections.Generic.List<SimEvent> events, SimEventKind kind)
        {
            foreach (SimEvent ev in events) if (ev.Kind == kind) return true;
            return false;
        }

        /// <summary>First live entity matching a predicate, or 0.</summary>
        public static int Find(World w, System.Func<int, Identity, bool> pred)
        {
            for (int i = 0; i < w.Identities.Count; i++)
            {
                int e = w.Identities.EntityAt(i);
                if (pred(e, w.Identities.At(i))) return e;
            }
            return 0;
        }

        public static int[] UnitsOf(World w, int player, string unitId)
        {
            int idx = w.Defs.Data.UnitIndex(unitId);
            var list = new System.Collections.Generic.List<int>();
            for (int i = 0; i < w.Identities.Count; i++)
            {
                ref Identity id = ref w.Identities.At(i);
                if (id.Kind == EntityKind.Unit && id.Player == player && id.DefIndex == idx) list.Add(w.Identities.EntityAt(i));
            }
            return list.ToArray();
        }
    }
}
