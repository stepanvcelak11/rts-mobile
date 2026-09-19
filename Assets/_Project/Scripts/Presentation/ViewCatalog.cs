using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTS.Presentation
{
    /// <summary>
    /// Maps definition ids ("unit.villager", "bld.house", "res.tree") to prefabs. Anything
    /// missing falls back to a coloured primitive so the game is playable before art exists.
    /// </summary>
    [CreateAssetMenu(menuName = "RTS/View Catalog", fileName = "ViewCatalog")]
    public sealed class ViewCatalog : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string id;
            public GameObject prefab;
        }

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();

        [Header("Player colours (index = player)")]
        [SerializeField] private Color[] playerColors =
        {
            new Color(0.23f, 0.51f, 0.96f),   // blue
            new Color(0.86f, 0.24f, 0.22f),   // red
            new Color(0.96f, 0.72f, 0.14f),   // yellow
            new Color(0.35f, 0.72f, 0.36f),   // green
        };

        private Dictionary<string, GameObject> _lookup;

        public GameObject PrefabFor(string id)
        {
            if (_lookup == null)
            {
                _lookup = new Dictionary<string, GameObject>();
                foreach (Entry e in entries)
                    if (!string.IsNullOrEmpty(e.id) && e.prefab != null) _lookup[e.id] = e.prefab;
            }
            return _lookup.TryGetValue(id, out GameObject p) ? p : null;
        }

        public Color PlayerColor(int player)
        {
            if (player < 0) return new Color(0.55f, 0.55f, 0.5f);
            return playerColors[player % playerColors.Length];
        }
    }
}
