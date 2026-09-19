using System;
using System.IO;
using RTS.Data;
using RTS.Net;
using RTS.Sim.Model;
using UnityEditor;
using UnityEngine;

namespace RTS.Editor
{
    /// <summary>
    /// RTS ▸ Validate Data: loads the JSON from disk, bakes a world and runs 30 s of simulation
    /// headless inside the editor. Catches schema and balance mistakes before pressing Play.
    /// </summary>
    public static class DataValidatorMenu
    {
        private const string DataDir = "Assets/_Project/Resources/Data";

        [MenuItem("RTS/Validate Data")]
        public static void Validate()
        {
            try
            {
                GameData data = JsonLoader.LoadFromDirectory(Path.GetFullPath(DataDir));
                var config = new WorldConfig { Seed = 1, MapId = data.Maps.Count > 0 ? data.Maps[0].id : "map.default" };
                var runner = new MatchRunner(data, config, new LocalCommandSource());
                var t0 = DateTime.Now;
                runner.RunTicks(30 * 20);
                double ms = (DateTime.Now - t0).TotalMilliseconds;
                Debug.Log($"Data OK: {data.Units.Count} units, {data.Buildings.Count} buildings, {data.Civs.Count} civs, " +
                          $"{data.Techs.Count} techs, {data.Maps.Count} maps. 600 ticks in {ms:F0} ms, hash {runner.World.LastHash:X16}");
            }
            catch (Exception e)
            {
                Debug.LogError("Data validation failed: " + e.Message + "\n" + e.StackTrace);
            }
        }
    }
}
