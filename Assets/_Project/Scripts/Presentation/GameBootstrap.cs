using System;
using System.IO;
using RTS.Data;
using RTS.Net;
using RTS.Sim.Model;
using UnityEngine;

namespace RTS.Presentation
{
    /// <summary>
    /// Creates the match on scene start, drives the fixed-step simulation from Update, and
    /// owns the WorldView. This is the only place where wall-clock time meets the simulation.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GameBootstrap : MonoBehaviour, IMatchSession
    {
        [Header("Match")]
        [SerializeField] private uint seed = 1;
        [SerializeField] private string mapId = "map.default";
        [SerializeField] private string[] civIds = { "civ.crown", "civ.compact" };
        [SerializeField] private AiDifficulty[] ai = { AiDifficulty.None, AiDifficulty.Normal };
        [SerializeField] private int localPlayer = 0;
        [SerializeField] private bool recordReplay = true;

        [Header("View")]
        [SerializeField] private ViewCatalog viewCatalog;
        [SerializeField] private Material groundMaterial;

        public static GameBootstrap Instance { get; private set; }

        public GameData Data { get; private set; }
        public MatchRunner Runner { get; private set; }
        public WorldView View { get; private set; }

        public World World => Runner.World;
        public ICommandSource Source => Runner.Source;
        public int LocalPlayer => localPlayer;
        public float Alpha => Runner.Alpha;
        public bool MatchOver => Runner.World.Winner != -1;

        /// <summary>Raised once the world exists (UI/Input bind here).</summary>
        public event Action<GameBootstrap> MatchStarted;

        private ReplayRecorder _recorder;

        private void Awake()
        {
            Instance = this;
            Application.targetFrameRate = 60;

            Data = JsonLoader.Load(new ResourcesDataSource());
            if (seed == 0) seed = (uint)System.Environment.TickCount;
            var config = new WorldConfig { Seed = seed, MapId = mapId, CivIds = civIds, PlayerCount = civIds.Length, Ai = ai };

            ICommandSource source = new LocalCommandSource();
            if (recordReplay)
            {
                string dir = Path.Combine(Application.persistentDataPath, "replays");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".rts");
                _recorder = new ReplayRecorder(source, File.Create(file), config);
                source = _recorder;
                Debug.Log("Recording replay to " + file);
            }

            Runner = new MatchRunner(Data, config, source);
            View = new WorldView(this, viewCatalog, groundMaterial, transform);
            View.SyncAll();
            Runner.TickCompleted += View.OnTick;
        }

        private void Start()
        {
            MatchStarted?.Invoke(this);
        }

        private void Update()
        {
            Runner.Advance(Time.unscaledDeltaTime);
        }

        private void LateUpdate()
        {
            View.Interpolate(Runner.Alpha);
        }

        private void OnDestroy()
        {
            _recorder?.Dispose();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationPause(bool paused)
        {
            // Background on mobile: flush the replay so a kill does not lose it.
            if (paused) _recorder?.Dispose();
        }
    }
}
