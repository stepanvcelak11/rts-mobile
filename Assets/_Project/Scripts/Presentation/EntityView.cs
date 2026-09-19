using RTS.Sim.Core;
using RTS.Sim.Model;
using UnityEngine;

namespace RTS.Presentation
{
    /// <summary>
    /// Visual for one simulation entity. Keeps the previous and current tick positions and
    /// interpolates between them at display rate. Construction sites grow with progress;
    /// a health bar appears while the entity is damaged or selected.
    /// </summary>
    public sealed class EntityView : MonoBehaviour
    {
        public int Entity { get; private set; }
        public EntityKind Kind { get; private set; }
        public int Player { get; private set; }

        private Vector3 _prev, _cur;
        private Quaternion _prevRot, _curRot;
        private Transform _model;
        private Transform _selectionRing;
        private Transform _healthRoot, _healthFill;
        private Renderer _healthFillRenderer;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;
        private float _fullHeight = 1f;
        private float _barHeight = 1.2f;
        private float _hpFraction = 1f;
        private Quaternion _barRotation = Quaternion.identity;
        private bool _selected;
        private static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorId = Shader.PropertyToID("_Color");

        public void Init(int entity, EntityKind kind, int player, Transform model, Color tint, float barHeight)
        {
            Entity = entity;
            Kind = kind;
            Player = player;
            _model = model;
            _renderers = model.GetComponentsInChildren<Renderer>();
            _mpb = new MaterialPropertyBlock();
            Tint(tint);
            _fullHeight = model.localScale.y;
            _barHeight = barHeight;
            if (kind == EntityKind.Unit || kind == EntityKind.Building)
            {
                _selectionRing = CreateSelectionRing(model);
                CreateHealthBar();
            }
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
        public void OnTick(Vector3 position, Vector3 facing, float constructionProgress, float hpFraction)
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

            SetHealth(hpFraction);
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
            if (_healthRoot != null) _healthRoot.rotation = _barRotation;   // bars keep facing the camera, not the model
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            if (_selectionRing != null) _selectionRing.gameObject.SetActive(selected);
            RefreshHealthVisibility();
        }

        private void SetHealth(float fraction)
        {
            fraction = Mathf.Clamp01(fraction);
            if (Mathf.Approximately(fraction, _hpFraction)) { RefreshHealthVisibility(); return; }
            _hpFraction = fraction;
            if (_healthFill != null)
            {
                Vector3 s = _healthFill.localScale;
                s.x = Mathf.Max(0.001f, fraction);
                _healthFill.localScale = s;
                _healthFill.localPosition = new Vector3(-(1f - fraction) * 0.5f, 0f, -0.001f);
                _healthFillRenderer.sharedMaterial = fraction > 0.5f ? FallbackMaterials.HealthGood : fraction > 0.25f ? FallbackMaterials.HealthWarn : FallbackMaterials.HealthBad;
            }
            RefreshHealthVisibility();
        }

        private void RefreshHealthVisibility()
        {
            if (_healthRoot == null) return;
            bool show = _selected || _hpFraction < 0.999f;
            if (_healthRoot.gameObject.activeSelf != show) _healthRoot.gameObject.SetActive(show);
        }

        private void CreateHealthBar()
        {
            var root = new GameObject("HealthBar");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = new Vector3(0f, _barHeight, 0f);
            float width = Kind == EntityKind.Building ? Mathf.Max(1.2f, _model.localScale.x * 0.8f) : 0.8f;
            root.transform.localScale = new Vector3(width, 0.12f, 1f);

            var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(back.GetComponent<Collider>());
            back.transform.SetParent(root.transform, false);
            back.GetComponent<Renderer>().sharedMaterial = FallbackMaterials.HealthBack;

            var fill = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(fill.GetComponent<Collider>());
            fill.transform.SetParent(root.transform, false);
            fill.transform.localPosition = new Vector3(0f, 0f, -0.001f);
            _healthFillRenderer = fill.GetComponent<Renderer>();
            _healthFillRenderer.sharedMaterial = FallbackMaterials.HealthGood;

            // Face the camera once (the boom yaw/pitch is fixed); cheap billboard.
            Camera cam = Camera.main;
            if (cam != null) _barRotation = cam.transform.rotation;
            root.transform.rotation = _barRotation;
            _healthRoot = root.transform;
            _healthFill = fill.transform;
            root.SetActive(false);
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

    /// <summary>Short-lived shrink-and-sink effect played where a unit or building died.</summary>
    public sealed class DeathFx : MonoBehaviour
    {
        private float _t;
        private const float Duration = 0.6f;

        public static void Play(Transform model, Vector3 position)
        {
            if (model == null) return;
            var go = new GameObject("DeathFx");
            go.transform.position = position;
            model.SetParent(go.transform, true);
            go.AddComponent<DeathFx>();
        }

        private void Update()
        {
            _t += Time.deltaTime;
            float k = 1f - Mathf.Clamp01(_t / Duration);
            transform.localScale = new Vector3(k, k, k);
            transform.position += Vector3.down * (Time.deltaTime * 0.8f);
            if (_t >= Duration) Destroy(gameObject);
        }
    }

    /// <summary>Runtime materials for the no-art fallback. Uses URP Lit/Unlit when present, Standard otherwise.</summary>
    public static class FallbackMaterials
    {
        private static Material _lit, _selection, _ghostOk, _ghostBad, _projectile, _healthBack, _healthGood, _healthWarn, _healthBad;

        public static Material Lit => _lit ??= Make("Universal Render Pipeline/Lit", "Standard", Color.white);
        public static Material Selection => _selection ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(1f, 1f, 1f, 0.9f));
        public static Material GhostOk => _ghostOk ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.3f, 0.9f, 0.4f, 0.6f), transparent: true);
        public static Material GhostBad => _ghostBad ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.95f, 0.3f, 0.25f, 0.6f), transparent: true);
        public static Material Projectile => _projectile ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.15f, 0.12f, 0.1f));
        public static Material HealthBack => _healthBack ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.1f, 0.1f, 0.12f));
        public static Material HealthGood => _healthGood ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.3f, 0.85f, 0.35f));
        public static Material HealthWarn => _healthWarn ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.95f, 0.75f, 0.2f));
        public static Material HealthBad => _healthBad ??= Make("Universal Render Pipeline/Unlit", "Unlit/Color", new Color(0.9f, 0.25f, 0.2f));

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
