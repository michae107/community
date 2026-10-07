using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IdleRpg.MapEditing;
using UnityEngine;
using UnityEngine.Rendering;

namespace IdleRpg.UnityMapEditor
{
    // One atlas and material for every tile; overlays reuse the imported meshes.
    // World-space projection keeps the lettering upright as terrain tiles rotate.
    internal sealed class CoordinateSurface : IDisposable
    {
        const string AtlasPath = "Assets/IdleRpgMap/src/map/CoordinateGlyphs.png";
        const string OverlayName = "__HexMapCoordinates";
        readonly Material material;
        readonly Texture2D coverage;
        readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        bool visible = true;

        public CoordinateSurface()
        {
            var shader = Shader.Find("Hidden/IdleRpg/TileCoordinates");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("The tile-coordinate shader has not imported successfully.");
            // The source atlas has a dark outline in its alpha channel. Extract
            // white-stroke coverage before generating mips, as Godot's coverage
            // resource does. This avoids both the outline and squared coverage.
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!ImageConversion.LoadImage(source, File.ReadAllBytes(AtlasPath))) throw new IOException("Cannot load the coordinate glyph atlas.");
                var pixels = source.GetPixels32();
                var values = new byte[pixels.Length];
                for (int i = 0; i < values.Length; i++) values[i] = (byte)((pixels[i].r * pixels[i].a + 127) / 255);
                coverage = new Texture2D(source.width, source.height, TextureFormat.R8, true, true)
                {
                    name = "Tile coordinate coverage", hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 16
                };
                coverage.SetPixelData(values, 0);
                coverage.Apply(true, true);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
            material = new Material(shader) { name = "Tile coordinates (editor only)", hideFlags = HideFlags.HideAndDontSave };
            material.SetTexture("_GlyphAtlas", coverage);
        }

        public bool Visible
        {
            get => visible;
            set
            {
                visible = value;
                renderers.RemoveAll(r => !r);
                foreach (var renderer in renderers) renderer.enabled = value;
            }
        }

        public void Attach(GameObject tile, GridPoint point)
        {
            string address = point.ToString();
            if (address.Length > 24) throw new ArgumentException("Coordinate is too long for the glyph atlas.");
            var packed = new int[8];
            for (int i = 0; i < address.Length; i++)
            {
                char c = address[i];
                int glyph = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 36;
                packed[i / 3] |= glyph << ((i % 3) * 6);
            }
            var block = new MaterialPropertyBlock();
            block.SetVector("_GlyphsA", new Vector4(packed[0], packed[1], packed[2], packed[3]));
            block.SetVector("_GlyphsB", new Vector4(packed[4], packed[5], packed[6], packed[7]));
            block.SetFloat("_CharacterCount", address.Length);
            block.SetFloat("_CharacterWidth", Mathf.Min(.62f, 1.8f / address.Length));
            block.SetVector("_TileOrigin", MapPreview.World(point));
            foreach (var filter in tile.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.sharedMesh || !filter.GetComponent<MeshRenderer>()) continue;
                var overlay = new GameObject(OverlayName, typeof(MeshFilter), typeof(MeshRenderer))
                {
                    hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild
                };
                overlay.transform.SetParent(filter.transform, false);
                overlay.GetComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var renderer = overlay.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = Enumerable.Repeat(material, filter.sharedMesh.subMeshCount).ToArray();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                renderer.SetPropertyBlock(block);
                renderer.enabled = visible;
                renderers.Add(renderer);
            }
        }

        public void Prune() => renderers.RemoveAll(r => !r);

        public static void RemoveFromExport(GameObject root)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true).Where(t => t.name == OverlayName).ToArray())
                UnityEngine.Object.DestroyImmediate(transform.gameObject);
        }

        public void Dispose()
        {
            renderers.Clear();
            UnityEngine.Object.DestroyImmediate(material);
            UnityEngine.Object.DestroyImmediate(coverage);
        }
    }
}
