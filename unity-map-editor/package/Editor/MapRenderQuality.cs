using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace IdleRpg.UnityMapEditor
{
    [InitializeOnLoad]
    internal static class MapRenderQuality
    {
        const string Folder = "Assets/IdleRpgMap/Rendering";
        const string ProfilePath = Folder + "/MapPostProcessing.asset";
        const string PipelinePath = Folder + "/MapHighQuality.asset";
        static readonly List<Volume> Volumes = new List<Volume>();
        static string AppliedKey => "IdleRpg.MapQuality.URP1." + Application.dataPath;
        public static bool Ready => EditorPrefs.GetBool(AppliedKey, false);

        static MapRenderQuality() => EditorApplication.delayCall += Configure;

        static void Configure()
        {
            if (Application.isBatchMode || Ready || !File.Exists("Assets/IdleRpgMap/TileCatalog.json")) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Configure;
                return;
            }
            try
            {
                if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/IdleRpgMap", "Rendering");
                var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Folder + "/MapRenderer.asset");
                if (!renderer)
                {
                    renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                    renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                    renderer.renderingMode = RenderingMode.ForwardPlus;
                    AssetDatabase.CreateAsset(renderer, Folder + "/MapRenderer.asset");
                    var ao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                    ao.name = "Ground contact and crevice shading";
                    AssetDatabase.AddObjectToAsset(ao, renderer);
                    var serialized = new SerializedObject(ao);
                    var settings = serialized.FindProperty("m_Settings");
                    settings.FindPropertyRelative("Downsample").boolValue = false;
                    settings.FindPropertyRelative("Intensity").floatValue = .8f;
                    settings.FindPropertyRelative("Radius").floatValue = .65f;
                    settings.FindPropertyRelative("DirectLightingStrength").floatValue = .15f;
                    settings.FindPropertyRelative("Samples").enumValueIndex = 0;
                    settings.FindPropertyRelative("BlurQuality").enumValueIndex = 0;
                    settings.FindPropertyRelative("Falloff").floatValue = 220;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    renderer.rendererFeatures.Add(ao);
                    renderer.SetDirty();
                    EditorUtility.SetDirty(renderer);
                }
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
                if (!pipeline)
                {
                    pipeline = UniversalRenderPipelineAsset.Create(renderer);
                    pipeline.supportsHDR = true;
                    pipeline.hdrColorBufferPrecision = HDRColorBufferPrecision._64Bits;
                    pipeline.msaaSampleCount = 8;
                    pipeline.renderScale = 1;
                    pipeline.supportsCameraDepthTexture = true;
                    pipeline.supportsCameraOpaqueTexture = true;
                    pipeline.mainLightShadowmapResolution = 4096;
                    pipeline.additionalLightsShadowmapResolution = 2048;
                    pipeline.shadowCascadeCount = 4;
                    pipeline.shadowDistance = 180;
                    pipeline.shadowDepthBias = .1f;
                    pipeline.shadowNormalBias = .2f;
                    var pipelineSettings = new SerializedObject(pipeline);
                    pipelineSettings.FindProperty("m_MainLightShadowsSupported").boolValue = true;
                    pipelineSettings.FindProperty("m_AdditionalLightShadowsSupported").boolValue = true;
                    pipelineSettings.FindProperty("m_SoftShadowsSupported").boolValue = true;
                    pipelineSettings.FindProperty("m_SoftShadowQuality").intValue = (int)SoftShadowQuality.High;
                    pipelineSettings.ApplyModifiedPropertiesWithoutUndo();
                    pipeline.colorGradingMode = ColorGradingMode.HighDynamicRange;
                    pipeline.colorGradingLutSize = 32;
                    AssetDatabase.CreateAsset(pipeline, PipelinePath);
                }
                var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
                if (!profile)
                {
                    profile = ScriptableObject.CreateInstance<VolumeProfile>();
                    AssetDatabase.CreateAsset(profile, ProfilePath);
                    var tone = profile.Add<Tonemapping>();
                    tone.mode.Override(TonemappingMode.ACES);
                    var bloom = profile.Add<Bloom>();
                    bloom.intensity.Override(.15f);
                    bloom.threshold.Override(1.25f);
                    bloom.scatter.Override(.6f);
                    bloom.highQualityFiltering.Override(true);
                    var color = profile.Add<ColorAdjustments>();
                    color.postExposure.Override(.15f);
                    color.contrast.Override(5);
                    color.saturation.Override(3);
                    foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
                    EditorUtility.SetDirty(profile);
                }
                PlayerSettings.colorSpace = ColorSpace.Linear;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                QualitySettings.antiAliasing = 8;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                QualitySettings.globalTextureMipmapLimit = 0;
                QualitySettings.shadows = UnityEngine.ShadowQuality.All;
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.VeryHigh;
                QualitySettings.shadowCascades = 4;
                QualitySettings.shadowDistance = 180;
                QualitySettings.lodBias = 2;
                QualitySettings.vSyncCount = 1;
                AssetDatabase.SaveAssets();
                foreach (var volume in Volumes) if (volume) volume.sharedProfile = profile;
                EditorPrefs.SetBool(AppliedKey, true);
                SceneView.RepaintAll();
            }
            catch (Exception exception) { Debug.LogError("Map rendering setup failed: " + exception.GetBaseException().Message); }
        }

        public static void AddVolume(Scene scene)
        {
            var obj = new GameObject("Map post-processing", typeof(Volume));
            SceneManager.MoveGameObjectToScene(obj, scene);
            var volume = obj.GetComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10;
            volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            Volumes.RemoveAll(v => !v);
            Volumes.Add(volume);
        }

        public static void ConfigureCamera(Camera camera)
        {
            camera.allowHDR = true;
            camera.allowMSAA = true;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.dithering = true;
            data.antialiasing = AntialiasingMode.None;
        }
    }
}
