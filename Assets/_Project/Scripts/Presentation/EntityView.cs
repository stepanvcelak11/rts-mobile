using RTS.Sim.Core;
using RTS.Sim.Model;
using UnityEngine;

namespace RTS.Presentation
{
    /// <summary>
    /// Visual for one simulation entity. Keeps the previous and current tick positions and
    /// interpolates between them at display rate. Construction sites grow with progress.
    /// </summary>
    public sealed class EntityView : MonoBehaviour
    {
        public int Entity { get; private set; }
        public EntityKind Kind { get; private set; }

        private Vector3 _prev, _cur;
        private Quaternion _prevRot, _curRot;
        private Transform _model;
        private Transform _selectionRing;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;
        private float _fullHeight = 1f;
        private static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorId = Shader.PropertyToID("_Color");

        public void Init(int entity, EntityKind kind, Transform model, Color tint)
        {
            Entity = entity;
            Kind = kind;
            _model = model;
            _renderers = model.GetComponentsInChildren<Renderer>();
            _mpb = new MaterialPropertyBlock();
            Tint(tint);
            _fullHeight = model.localScale.y;
            _selectionRing = CreateSelectionRing(model);
            _prev = _cur = transform.position;
            _prevRot = _curRot = transform.rotation;
        }

        private void Tint(Color c)
        {
            _mpb.SetColor(ColorId, c);
            _mpb.SetColor(LegacyColorId, c);
            foreach (Renderer r in _renderers) r.SetPropertyBlock(_mpb);
        }

        /// <summary>Called once per simulation tick with the new authoritative state.</summary>
        public void OnTick(Vector3 position, Vector3 facing, float constructionProgress)
        {
            _prev = _cur;
            _prevRot = _curRot;
            _cur = position;
            if (facing.sqrMagnitude > 0.0001f) _curRot = Quaternion.LookRotation(facing, Vector3.up);

            if (constructionProgress >= 0f && constructionProgress < 1f)
            {
                Vector3 s = _model.localScale;
                s.y = Mathf.Max(0.1f, _fullHeight * constructionProgress);
                _model.localScale = s;
                _model.localPosition = new Vector3(_model.localPosition.x, s.y * 0.5f, _model.localPosition.z);
            }
            else if (constructionProgress >= 1f && !Mathf.Approximately(_model.localScale.y, _fullHeight))
            {
                Vector3 s = _model.localScale;
                s.y = _fullHeight;
                _model.localScale = s;
                _model.localPosition = new Vector3(_model.localPosition.x, s.y * 0.5f, _model.localPosition.z);
            }
        }

        /// <summary>Snap without interpolation (spawn).</summary>
        public void Teleport(Vector3 position)
        {
            _prev = _cur = position;
            transform.position = position;
        }

        public void Interpolate(float alpha)
        {
            transform.position = Vector3.LerpUnclamped(_prev, _cur, alpha);
            transform.rotation = Quaternion.Slerp(_prevRot, _curRot, alpha);
        }

        public void SetSelected(bool selected)
        {
            if (_selectionRing != null) _selectionRing.gameObject.SetActive(selected);
        }

        private static Transform CreateSelectionRing(Transform model)
        {
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(ring.GetComponent<Collider>());
            ring.name = "SelectionRing";
            ring.transform.SetParent(model.parent, false);
            float w = Mathf.Max(model.localScale.x, model.localScale.z) * 1.4f;
            ring.transform.localScale = new Vector3(w, 0.02f, w);
            ring.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            Renderer r = ring.GetComponent<Renderer>();
            r.sharedMaterial = FallbackMaterials.Selection;
            ring.SetActive(false);
            return ring.transform;
        }
    }

    /// <summary>Runtime materials for the no-art fallback. Uses URP Lit/Unlit when present, Standard otherwise.</summary>
    public static class FallbackMaterials
    {
        private static Material _lit, _selection, _ghostOk, _ghostBad;

        public static Material Lit => _lit ??= Make("Universal Render Pipeline/Lit", "Standard", Color.white);
        public static Material Selection => _selection ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(1f, 1f, 1f, 0.9f));
        public static Material GhostOk => _ghostOk ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.3f, 0.9f, 0.4f, 0.6f), transparent: true);
        public static Material GhostBad => _ghostBad ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.95f, 0.3f, 0.25f, 0.6f), transparent: true);

        private static bool UsingUrp => UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;

        private static Material Make(string urpShader, string fallback, Color color, bool transparent = false)
        {
            // Without a URP asset assigned the URP shaders exist but render magenta, so prefer built-in then.
            Shader s = (UsingUrp ? Shader.Find(urpShader) : null) ?? Shader.Find(fallback) ?? Shader.Find(urpShader);
            var m = new Material(s) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (transparent)
            {
                // URP Unlit transparent surface type
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
                if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            return m;
        }
    }
}
