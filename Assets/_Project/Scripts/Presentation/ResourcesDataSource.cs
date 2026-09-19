using System.Collections.Generic;
using RTS.Data;
using UnityEngine;

namespace RTS.Presentation
{
    /// <summary>
    /// Reads the JSON definitions that Unity imported as TextAssets from Resources/Data.
    /// Resources.LoadAll loses folder information, so each known folder is loaded explicitly
    /// and its name re-attached (the loader routes files by their relative path).
    /// </summary>
    public sealed class ResourcesDataSource : IDataSource
    {
        private const string Root = "Data";
        private static readonly string[] Folders = { "units", "buildings", "civs", "techs", "maps" };

        public IEnumerable<DataFile> ReadAll()
        {
            foreach (TextAsset ta in Resources.LoadAll<TextAsset>(Root))
            {
                // Top-level files only: LoadAll on a folder is recursive, so filter by name.
                if (ta.name == "economy" || ta.name == "ages")
                    yield return new DataFile(ta.name + ".json", ta.text);
            }
            foreach (string folder in Folders)
            {
                foreach (TextAsset ta in Resources.LoadAll<TextAsset>(Root + "/" + folder))
                    yield return new DataFile(folder + "/" + ta.name + ".json", ta.text);
            }
        }
    }
}
