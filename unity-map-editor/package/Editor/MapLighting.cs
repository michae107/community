using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace IdleRpg.UnityMapEditor
{
    internal static class MapLighting
    {
        public static void Create(Scene scene)
        {
            var sun = Add(scene, "Sun", Quaternion.Euler(50, -30, 0), new Color(1, .96f, .88f), .95f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = .65f;
            sun.shadowBias = .035f;
            sun.shadowNormalBias = .2f;
            sun.shadowResolution = LightShadowResolution.High;
            // Gentle sky-side fill preserves the color on vertical tile edges.
            Add(scene, "Sky fill", Quaternion.Euler(35, 145, 0), new Color(.68f, .81f, 1), .3f);
            MapRenderQuality.AddVolume(scene);
        }

        static Light Add(Scene scene, string name, Quaternion rotation, Color color, float intensity)
        {
            var obj = new GameObject(name, typeof(Light));
            SceneManager.MoveGameObjectToScene(obj, scene);
            var light = obj.GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.transform.rotation = rotation;
            light.shadows = LightShadows.None;
            return light;
        }
    }
}
