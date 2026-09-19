using RTS.Sim.Model;
using UnityEngine;

namespace RTS.Input
{
    /// <summary>Translucent footprint preview while placing a building: green = valid, red = not.</summary>
    public sealed class BuildGhost : MonoBehaviour
    {
        private Transform _box;
        private Renderer _renderer;
        private Material _ok, _bad;

        public bool Visible => gameObject.activeSelf;

        public static BuildGhost Create(Transform parent, Material ok, Material bad)
        {
            var go = new GameObject("BuildGhost");
            go.transform.SetParent(parent, false);
            var ghost = go.AddComponent<BuildGhost>();
            ghost._ok = ok;
            ghost._bad = bad;
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(box.GetComponent<Collider>());
            box.transform.SetParent(go.transform, false);
            ghost._box = box.transform;
            ghost._renderer = box.GetComponent<Renderer>();
            go.SetActive(false);
            return ghost;
        }

        public void Show(int x, int y, int w, int h, PlacementResult result)
        {
            gameObject.SetActive(true);
            float height = 0.6f;
            _box.localScale = new Vector3(w, height, h);
            _box.position = new Vector3(x + w * 0.5f, height * 0.5f + 0.01f, y + h * 0.5f);
            _renderer.sharedMaterial = result == PlacementResult.Ok ? _ok : _bad;
        }

        public void Hide() => gameObject.SetActive(false);
    }
}
