using System.IO;
using RTS.Input;
using RTS.Presentation;
using RTS.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace RTS.Editor
{
    /// <summary>
    /// Builds the Skirmish scene from code so the project has no hand-authored scene to keep in
    /// sync: RTS ▸ Create Skirmish Scene. Safe to re-run; it overwrites the scene file.
    /// </summary>
    public static class SkirmishSceneBuilder
    {
        private const string ScenePath = "Assets/_Project/Scenes/Skirmish.unity";
        private const string SettingsDir = "Assets/_Project/Settings";

        [MenuItem("RTS/Create Skirmish Scene")]
        public static void Create()
        {
            Directory.CreateDirectory("Assets/_Project/Scenes");
            Directory.CreateDirectory(SettingsDir);

            EnsureUrp();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Light
            var lightGo = new GameObject("Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.4f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(52f, 35f, 0f);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.6f, 0.68f);

            // Camera rig: Rig → Boom → Camera
            var rig = new GameObject("CameraRig");
            var boom = new GameObject("Boom");
            boom.transform.SetParent(rig.transform, false);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(boom.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.2f, 0.26f);
            camGo.AddComponent<AudioListener>();
            var rigComp = rig.AddComponent<CameraRig>();
            SetPrivate(rigComp, "cam", cam);
            SetPrivate(rigComp, "boom", boom.transform);

            // Game
            var game = new GameObject("Game");
            var bootstrap = game.AddComponent<GameBootstrap>();
            SetPrivate(bootstrap, "viewCatalog", GetOrCreateCatalog());
            SetPrivate(bootstrap, "groundMaterial", GetOrCreateGroundMaterial());

            // Input
            var input = new GameObject("Input");
            var gestures = input.AddComponent<GestureRecognizer>();
            var player = input.AddComponent<PlayerController>();
            SetPrivate(player, "cameraRig", rigComp);
            SetPrivate(player, "gestures", gestures);

            // HUD
            var hudGo = new GameObject("HUD");
            var doc = hudGo.AddComponent<UIDocument>();
            doc.panelSettings = GetOrCreatePanelSettings();
            var hud = hudGo.AddComponent<HudController>();

            // Glue
            var glueGo = new GameObject("SkirmishGlue");
            var glue = glueGo.AddComponent<SkirmishGlue>();
            SetPrivate(glue, "bootstrap", bootstrap);
            SetPrivate(glue, "player", player);
            SetPrivate(glue, "gestures", gestures);
            SetPrivate(glue, "hud", hud);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            Debug.Log("Skirmish scene created at " + ScenePath);
        }

        /// <summary>Creates and assigns a URP asset (+ Universal Renderer) when the project has none.</summary>
        [MenuItem("RTS/Setup URP")]
        public static void EnsureUrp()
        {
            if (GraphicsSettings.defaultRenderPipeline != null) return;
            Directory.CreateDirectory(SettingsDir);
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, SettingsDir + "/URP_Renderer.asset");
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.supportsHDR = false;
            pipeline.msaaSampleCount = 2;
            pipeline.shadowDistance = 60f;
            AssetDatabase.CreateAsset(pipeline, SettingsDir + "/URP.asset");
            GraphicsSettings.defaultRenderPipeline = pipeline;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }
            AssetDatabase.SaveAssets();
            Debug.Log("URP asset created and assigned (" + SettingsDir + "/URP.asset)");
            EnsureAlwaysIncludedShaders("Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit");
        }

        /// <summary>Runtime-created materials (fallback primitives, ghosts) need their shaders kept in builds.</summary>
        private static void EnsureAlwaysIncludedShaders(params string[] shaderNames)
        {
            var settings = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty list = settings.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) return;
            foreach (string name in shaderNames)
            {
                Shader shader = Shader.Find(name);
                if (shader == null) continue;
                bool present = false;
                for (int i = 0; i < list.arraySize; i++)
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) { present = true; break; }
                if (present) continue;
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static ViewCatalog GetOrCreateCatalog()
        {
            string path = SettingsDir + "/ViewCatalog.asset";
            var existing = AssetDatabase.LoadAssetAtPath<ViewCatalog>(path);
            if (existing != null) return existing;
            var catalog = ScriptableObject.CreateInstance<ViewCatalog>();
            AssetDatabase.CreateAsset(catalog, path);
            return catalog;
        }

        private static Material GetOrCreateGroundMaterial()
        {
            string path = SettingsDir + "/Ground.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Shader shader = (GraphicsSettings.defaultRenderPipeline != null ? Shader.Find("Universal Render Pipeline/Lit") : null) ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static PanelSettings GetOrCreatePanelSettings()
        {
            string path = SettingsDir + "/HudPanel.asset";
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (existing != null) return existing;
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            ps.referenceDpi = 160f;     // 1 UI unit = 1 dp, matching docs/03-CONTROLS-CAMERA.md
            ps.fallbackDpi = 160f;
            AssetDatabase.CreateAsset(ps, path);
            return ps;
        }

        private static void AddToBuildSettings(string scenePath)
        {
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.path == scenePath) return;
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes)
            {
                new EditorBuildSettingsScene(scenePath, true),
            };
            EditorBuildSettings.scenes = list.ToArray();
        }

        private static void SetPrivate(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(field);
            if (p == null) { Debug.LogError($"{target.GetType().Name} has no serialized field '{field}'"); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
