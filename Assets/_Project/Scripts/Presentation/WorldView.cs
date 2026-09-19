using System.Collections.Generic;
using RTS.Data;
using RTS.Net;
using RTS.Sim.Core;
using RTS.Sim.Model;
using UnityEngine;

namespace RTS.Presentation
{
    /// <summary>
    /// Mirrors the simulation into GameObjects: spawns/despawns views from world events after
    /// each tick and pushes fresh positions for interpolation. Never writes to the world.
    /// </summary>
    public sealed class WorldView
    {
        private readonly IMatchSession _session;
        private readonly ViewCatalog _catalog;
        private readonly Transform _root;
        private readonly Dictionary<int, EntityView> _views = new Dictionary<int, EntityView>();
        private readonly HashSet<int> _diedThisTick = new HashSet<int>();
        private readonly GroundView _ground;

        public IReadOnlyDictionary<int, EntityView> Views => _views;
        public ViewCatalog Catalog => _catalog;

        public WorldView(IMatchSession session, ViewCatalog catalog, Material groundMaterial, Transform root)
        {
            _session = session;
            _catalog = catalog;
            _root = new GameObject("WorldView").transform;
            _root.SetParent(root, false);
            _ground = new GroundView(session.World.Map, groundMaterial, _root);
        }

        /// <summary>Creates views for everything alive (scene start).</summary>
        public void SyncAll()
        {
            World w = _session.World;
            for (int i = 0; i < w.Identities.Count; i++) EnsureView(w.Identities.EntityAt(i));
            PushState(w);
            foreach (EntityView v in _views.Values) v.Teleport(v.transform.position);
        }

        /// <summary>Hooked to MatchRunner.TickCompleted.</summary>
        public void OnTick(World w)
        {
            _diedThisTick.Clear();
            foreach (SimEvent ev in w.Events)
            {
                switch (ev.Kind)
                {
                    case SimEventKind.Spawned:
                        EnsureView(ev.Entity);
                        break;
                    case SimEventKind.Died:
                        _diedThisTick.Add(ev.Entity);
                        break;
                    case SimEventKind.Despawned:
                        if (_views.TryGetValue(ev.Entity, out EntityView v))
                        {
                            if (_diedThisTick.Contains(ev.Entity) && v.transform.childCount > 0)
                                DeathFx.Play(v.transform.GetChild(0), v.transform.position);
                            Object.Destroy(v.gameObject);
                            _views.Remove(ev.Entity);
                        }
                        break;
                }
            }
            PushState(w);
        }

        public void Interpolate(float alpha)
        {
            foreach (EntityView v in _views.Values)
                if (v.Kind == EntityKind.Unit || v.Kind == EntityKind.Projectile) v.Interpolate(alpha);
        }

        public bool TryGetView(int entity, out EntityView view) => _views.TryGetValue(entity, out view);

        private void PushState(World w)
        {
            foreach (KeyValuePair<int, EntityView> kv in _views)
            {
                int e = kv.Key;
                EntityView v = kv.Value;
                float hp = w.Healths.TryGet(e, out Health h) && !h.MaxHp.IsZero ? (h.Hp / h.MaxHp).ToFloat() : 1f;
                if (v.Kind == EntityKind.Unit || v.Kind == EntityKind.Projectile)
                {
                    if (!w.Positions.TryGet(e, out Position p)) continue;
                    float y = v.Kind == EntityKind.Projectile ? ProjectileHeight(w, e) : 0f;
                    v.OnTick(SimToUnity.ToWorld(p.Value, y), SimToUnity.ToWorld(p.Facing), -1f, hp);
                }
                else if (v.Kind == EntityKind.Building)
                {
                    float progress = w.Constructions.TryGet(e, out Construction c) ? c.Progress.ToFloat() : 1f;
                    v.OnTick(v.transform.position, Vector3.zero, progress, hp);
                }
            }
        }

        /// <summary>Simple arc: projectiles rise and fall over their flight.</summary>
        private static float ProjectileHeight(World w, int e)
        {
            Projectile pr = w.Projectiles.Get(e);
            float t = pr.TotalTicks > 0 ? 1f - pr.TicksLeft / (float)pr.TotalTicks : 1f;
            float arc = pr.SourceKind == EntityKind.Building ? 2.5f : 1.2f;
            return 0.8f + Mathf.Sin(t * Mathf.PI) * arc;
        }

        private void EnsureView(int entity)
        {
            if (_views.ContainsKey(entity)) return;
            World w = _session.World;
            if (!w.Identities.TryGet(entity, out Identity id)) return;

            string defId; Vector3 pos; Vector3 size; PrimitiveType shape; Color color; float height; float barHeight;
            switch (id.Kind)
            {
                case EntityKind.Unit:
                {
                    UnitDef def = w.DefsOf(id.Player).Units[id.DefIndex].Def;
                    defId = def.id;
                    pos = SimToUnity.ToWorld(w.Positions.Get(entity).Value);
                    float r = w.Positions.Get(entity).Radius.ToFloat();
                    height = def.stats.sizeClass == "large" ? 1.6f : def.stats.sizeClass == "medium" ? 1.3f : 1.0f;
                    size = new Vector3(r * 2f, height * 0.5f, r * 2f);   // capsule: y = half height
                    shape = def.tags.Contains("tag.cavalry") ? PrimitiveType.Capsule : def.tags.Contains("tag.artillery") ? PrimitiveType.Cube : PrimitiveType.Capsule;
                    color = _catalog != null ? _catalog.PlayerColor(id.Player) : Color.white;
                    if (def.behaviour != null && def.behaviour.canGather) color = Color.Lerp(color, Color.white, 0.35f);   // villagers lighter
                    barHeight = height + 0.35f;
                    break;
                }
                case EntityKind.Building:
                {
                    BuildingDef def = w.DefsOf(id.Player).Buildings[id.DefIndex].Def;
                    Footprint fp = w.Footprints.Get(entity);
                    defId = def.id;
                    height = Mathf.Clamp(Mathf.Min(fp.W, fp.H) * 0.6f, 1f, 4f);
                    if (def.id == "bld.tower") height = 3f;
                    pos = SimToUnity.FootprintCenter(fp.X, fp.Y, fp.W, fp.H);
                    size = new Vector3(fp.W * 0.92f, height, fp.H * 0.92f);
                    shape = PrimitiveType.Cube;
                    color = _catalog != null ? _catalog.PlayerColor(id.Player) * 0.85f : Color.gray;
                    barHeight = height + 0.4f;
                    break;
                }
                case EntityKind.Projectile:
                {
                    defId = "projectile";
                    pos = SimToUnity.ToWorld(w.Positions.Get(entity).Value, 1f);
                    size = w.Projectiles.Get(entity).SourceKind == EntityKind.Building ? new Vector3(0.25f, 0.25f, 0.25f) : new Vector3(0.15f, 0.15f, 0.15f);
                    shape = PrimitiveType.Sphere;
                    color = new Color(0.15f, 0.12f, 0.1f);
                    height = 0f;
                    barHeight = 0f;
                    break;
                }
                default:
                {
                    ResourceNodeDef def = w.Defs.Nodes[id.DefIndex].Def;
                    Footprint fp = w.Footprints.Get(entity);
                    defId = def.id;
                    pos = SimToUnity.FootprintCenter(fp.X, fp.Y, fp.W, fp.H);
                    shape = def.id == "res.tree" ? PrimitiveType.Cylinder : def.id == "res.mine" ? PrimitiveType.Cube : PrimitiveType.Sphere;
                    height = def.id == "res.tree" ? 1.8f : def.id == "res.mine" ? 1.2f : 0.7f;
                    size = def.id == "res.tree" ? new Vector3(0.6f, height * 0.5f, 0.6f) : new Vector3(fp.W * 0.8f, height, fp.H * 0.8f);
                    color = NodeColor(def.id);
                    barHeight = 0f;
                    break;
                }
            }

            var go = new GameObject(defId + " #" + entity);
            go.transform.SetParent(_root, false);
            go.transform.position = pos;

            Transform model;
            GameObject prefab = _catalog != null ? _catalog.PrefabFor(defId) : null;
            if (prefab != null)
            {
                model = Object.Instantiate(prefab, go.transform).transform;
                model.localPosition = Vector3.zero;
            }
            else
            {
                var prim = GameObject.CreatePrimitive(shape);
                Object.Destroy(prim.GetComponent<Collider>());   // picking goes through the sim, not physics
                prim.GetComponent<Renderer>().sharedMaterial = id.Kind == EntityKind.Projectile ? FallbackMaterials.Projectile : FallbackMaterials.Lit;
                model = prim.transform;
                model.SetParent(go.transform, false);
                model.localScale = size;
                float centerY = id.Kind == EntityKind.Projectile ? 0f
                              : shape == PrimitiveType.Capsule || shape == PrimitiveType.Cylinder ? size.y : size.y * 0.5f;
                model.localPosition = new Vector3(0f, centerY, 0f);
            }

            var view = go.AddComponent<EntityView>();
            view.Init(entity, id.Kind, id.Player, model, color, barHeight);
            _views.Add(entity, view);
        }

        private static Color NodeColor(string id)
        {
            switch (id)
            {
                case "res.tree": return new Color(0.16f, 0.45f, 0.2f);
                case "res.berries": return new Color(0.6f, 0.15f, 0.35f);
                case "res.mine": return new Color(0.85f, 0.7f, 0.2f);
                case "res.hunt": return new Color(0.55f, 0.35f, 0.2f);
                default: return Color.gray;
            }
        }
    }

    /// <summary>One textured quad for the whole map: a pixel per cell, point filtered.</summary>
    public sealed class GroundView
    {
        public GameObject GameObject { get; }

        public GroundView(GridMap map, Material material, Transform root)
        {
            var tex = new Texture2D(map.Width, map.Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[map.Width * map.Height];
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                {
                    Color32 c = TerrainColor(map.TerrainAt(x, y));
                    // Subtle checkerboard so cells are readable when placing buildings.
                    if (((x + y) & 1) == 0) { c.r = (byte)Mathf.Min(255, c.r + 6); c.g = (byte)Mathf.Min(255, c.g + 6); c.b = (byte)Mathf.Min(255, c.b + 6); }
                    pixels[y * map.Width + x] = c;
                }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            GameObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            GameObject.name = "Ground";
            Object.Destroy(GameObject.GetComponent<Collider>());
            GameObject.transform.SetParent(root, false);
            GameObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            GameObject.transform.position = new Vector3(map.Width * 0.5f, 0f, map.Height * 0.5f);
            GameObject.transform.localScale = new Vector3(map.Width, map.Height, 1f);

            Material m = new Material(material != null ? material : FallbackMaterials.Lit);
            m.mainTexture = tex;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            GameObject.GetComponent<Renderer>().sharedMaterial = m;
        }

        public static Color32 TerrainColor(TerrainType t)
        {
            switch (t)
            {
                case TerrainType.Dirt: return new Color32(140, 110, 70, 255);
                case TerrainType.Sand: return new Color32(215, 195, 140, 255);
                case TerrainType.Water: return new Color32(60, 120, 190, 255);
                case TerrainType.Cliff: return new Color32(110, 105, 100, 255);
                default: return new Color32(105, 160, 80, 255);
            }
        }
    }
}
