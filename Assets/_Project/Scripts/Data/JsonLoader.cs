using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace RTS.Data
{
    /// <summary>One JSON document from the data folder, identified by its path relative to that folder.</summary>
    public readonly struct DataFile
    {
        public readonly string RelativePath;   // e.g. "units/common.json", forward slashes
        public readonly string Json;

        public DataFile(string relativePath, string json)
        {
            RelativePath = relativePath.Replace('\\', '/');
            Json = json;
        }
    }

    /// <summary>Where the JSON comes from: disk (editor, headless, tests) or Unity Resources (device).</summary>
    public interface IDataSource
    {
        IEnumerable<DataFile> ReadAll();
    }

    /// <summary>Reads every *.json under a directory recursively.</summary>
    public sealed class DirectoryDataSource : IDataSource
    {
        private readonly string _root;
        public DirectoryDataSource(string root) { _root = root; }

        public IEnumerable<DataFile> ReadAll()
        {
            if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Data folder not found: " + _root);
            foreach (string path in Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories))
            {
                string rel = path.Substring(_root.Length).TrimStart('\\', '/');
                yield return new DataFile(rel, File.ReadAllText(path));
            }
        }
    }

    /// <summary>Builds a <see cref="GameData"/> from JSON documents. Numbers are parsed as decimal (exact).</summary>
    public static class JsonLoader
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            FloatParseHandling = FloatParseHandling.Decimal,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
        };

        public static GameData Load(IDataSource source)
        {
            EconomyDef economy = null;
            var ages = new List<AgeDef>();
            var units = new List<UnitDef>();
            var buildings = new List<BuildingDef>();
            var civs = new List<CivDef>();
            var techs = new List<TechDef>();
            var maps = new List<MapDef>();

            // Sort by path so the resulting indices are the same on every machine.
            var files = new List<DataFile>(source.ReadAll());
            files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

            foreach (DataFile f in files)
            {
                string p = f.RelativePath;
                try
                {
                    if (p == "economy.json") economy = Deserialize<EconomyDef>(f.Json);
                    else if (p == "ages.json") ages.AddRange(Items<AgeDef>(f.Json));
                    else if (p.StartsWith("units/")) units.AddRange(Items<UnitDef>(f.Json));
                    else if (p.StartsWith("buildings/")) buildings.AddRange(Items<BuildingDef>(f.Json));
                    else if (p.StartsWith("civs/")) civs.Add(Deserialize<CivDef>(f.Json));
                    else if (p.StartsWith("techs/")) techs.AddRange(Items<TechDef>(f.Json));
                    else if (p.StartsWith("maps/")) maps.Add(Deserialize<MapDef>(f.Json));
                    // anything else (schemas, notes) is ignored on purpose
                }
                catch (JsonException e)
                {
                    throw new InvalidDataException($"{p}: {e.Message}", e);
                }
            }

            if (economy == null) throw new InvalidDataException("economy.json is missing");
            return new GameData(economy, ages, units, buildings, civs, techs, maps);
        }

        public static GameData LoadFromDirectory(string root) => Load(new DirectoryDataSource(root));

        private static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        private static List<T> Items<T>(string json)
        {
            var file = Deserialize<ItemsFile<T>>(json);
            return file?.items ?? new List<T>();
        }
    }
}
