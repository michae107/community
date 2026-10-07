using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IdleRpg.MapEditing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IdleRpg.UnityMapEditor
{
    internal sealed class MapStage : PreviewSceneStage
    {
        protected override GUIContent CreateHeaderContent() => new GUIContent("Hex Map Preview");
        protected override bool OnOpenStage()
        {
            if (!base.OnOpenStage()) return false;
            MapLighting.Create(scene);
            return true;
        }
        protected override void OnCloseStage() => base.OnCloseStage();
    }

    // A preview scene keeps the authoring tiles out of the user's open scenes.
    internal sealed class MapPreview : IDisposable
    {
        Scene scene;
        MapStage stage;
        MapSolution lastSolution;
        readonly Dictionary<Material, Material> materials = new Dictionary<Material, Material>();
        readonly Dictionary<string, GameObject> models = new Dictionary<string, GameObject>();
        GameObject root, building;
        MapSolution pending;
        int built;
        SceneView view;
        bool previousSceneLighting;
        bool previousImageEffects;
        CoordinateSurface coordinateSurface;
        HashSet<GridPoint> cells = new HashSet<GridPoint>();
        (PathKind Kind, Vector3[] Points)[] paths = Array.Empty<(PathKind, Vector3[])>();
        public bool Busy => pending != null;
        public int TileCount { get; private set; }
        public MapDocument Document { get; private set; }
        public bool Labels
        {
            get => coordinateSurface.Visible;
            set { coordinateSurface.Visible = value; if (view) view.Repaint(); }
        }
        public GridPoint? Selected;
        public Action<GridPoint> Clicked;
        public Action<string> Failed;
        public Action Ready;

        public MapPreview()
        {
            EnsureStage();
            SceneView.duringSceneGui += Draw;
            EditorApplication.update += Update;
        }

        public static Vector3 World(GridPoint point)
        {
            var p = point.Position;
            return new Vector3(-p.X, p.Y, p.Z);
        }

        public void Build(MapDocument document, MapSolution solution)
        {
            EnsureStage();
            CancelBuild();
            foreach (string path in solution.Tiles.SelectMany(t => new[] { t.Variant.Model }.Concat(t.Variant.Decorations.Select(d => d.Model))).Distinct())
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (!model) throw new InvalidOperationException("Tile is not imported yet: " + path + ". Wait for Unity's import, then reopen the map tool.");
                models[path] = model;
            }
            Document = document;
            cells = document.Cells();
            paths = document.Paths.Select(p => (p.Kind, p.Points.Select(point => World(point) + Vector3.up * .35f).ToArray())).ToArray();
            lastSolution = solution;
            SetBiome(document.Biome);
            building = new GameObject("Hex map preview");
            building.SetActive(false);
            SceneManager.MoveGameObjectToScene(building, scene);
            pending = solution;
            built = 0;
        }

        void SetBiome(string biome)
        {
            string season = Path.GetFileNameWithoutExtension(biome);
            string file = season == "Summer" || season == "Fall" || season == "Winter"
                ? "HexagonsMedieval" + season + ".png" : "hexagons_medieval.png";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/IdleRpgMap/assets/region/hex/" + file);
            foreach (var pair in materials)
                if (texture && pair.Key && pair.Value && pair.Key.mainTexture && pair.Key.mainTexture.name.Replace("_", "").StartsWith("HexagonsMedieval", StringComparison.OrdinalIgnoreCase))
                    pair.Value.mainTexture = texture;
        }

        Material CopyMaterial(Material source)
        {
            if (!source) return null;
            if (!materials.TryGetValue(source, out var material) || !material)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                {
                    name = source.name + " (map preview)", hideFlags = HideFlags.HideAndDontSave
                };
                material.mainTexture = source.mainTexture;
                material.mainTextureScale = source.mainTextureScale;
                material.mainTextureOffset = source.mainTextureOffset;
                material.color = source.color;
                float roughness = source.HasProperty("roughnessFactor") ? source.GetFloat("roughnessFactor") : .65f;
                material.SetFloat("_Smoothness", 1 - Mathf.Max(.55f, roughness));
                material.SetFloat("_Metallic", source.HasProperty("metallicFactor") ? source.GetFloat("metallicFactor") : 0);
                materials[source] = material;
                SetBiome(Document.Biome);
            }
            return material;
        }

        void Update()
        {
            if (pending == null || !MapRenderQuality.Ready || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                if (!stage || !scene.IsValid() || !building) throw new InvalidOperationException("Preview closed. Use View > Frame map to reopen it.");
                // Limit main-thread prefab work per editor update, independent of the solver.
                double deadline = EditorApplication.timeSinceStartup + .008;
                int count = 0;
                while (built < pending.Tiles.Count && count++ < 24 && EditorApplication.timeSinceStartup < deadline)
                {
                    var tile = pending.Tiles[built++];
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(models[tile.Variant.Model], building.transform);
                    instance.name = tile.Point + " " + Path.GetFileNameWithoutExtension(tile.Variant.Model);
                    instance.transform.localPosition = World(tile.Point);
                    instance.transform.localRotation = Quaternion.Euler(0, tile.Variant.Rotation * 60, 0);
                    foreach (var decoration in tile.Variant.Decorations)
                    {
                        var prop = (GameObject)PrefabUtility.InstantiatePrefab(models[decoration.Model], instance.transform);
                        prop.transform.localPosition = Vector3.zero;
                        prop.transform.localRotation = Quaternion.Euler(0, decoration.Yaw, 0);
                    }
                    foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
                        renderer.sharedMaterials = renderer.sharedMaterials.Select(CopyMaterial).ToArray();
                    coordinateSurface.Attach(instance, tile.Point);
                }
                if (built < pending.Tiles.Count) return;
                if (root) UnityEngine.Object.DestroyImmediate(root);
                coordinateSurface.Prune();
                root = building;
                root.SetActive(true);
                building = null;
                TileCount = built;
                pending = null;
                if (view) view.Repaint();
                Ready?.Invoke();
            }
            catch (Exception exception) { CancelBuild(); Failed?.Invoke(exception.Message); }
        }

        void CancelBuild()
        {
            pending = null;
            if (building) UnityEngine.Object.DestroyImmediate(building);
            building = null;
        }

        public Bounds Bounds()
        {
            var bounds = new Bounds(cells != null && cells.Count > 0 ? World(cells.First()) : Vector3.zero, Vector3.one);
            if (cells != null) foreach (var cell in cells) bounds.Encapsulate(World(cell));
            bounds.Expand(3);
            return bounds;
        }

        public void Frame()
        {
            EnsureStage();
            if (!root && !Busy && lastSolution != null) Build(Document, lastSolution);
            if (!view)
            {
                view = SceneView.lastActiveSceneView ? SceneView.lastActiveSceneView : EditorWindow.GetWindow<SceneView>();
                previousSceneLighting = view.sceneLighting;
                previousImageEffects = view.sceneViewState.showImageEffects;
            }
            view.sceneLighting = true;
            view.sceneViewState.showImageEffects = true;
            MapRenderQuality.ConfigureCamera(view.camera);
            var bounds = Bounds();
            view.LookAt(bounds.center, Quaternion.Euler(65, 180, 0), bounds.extents.magnitude, true, true);
            view.Focus();
            view.Repaint();
        }

        void Draw(SceneView drawingView)
        {
            if (drawingView != view || !stage || StageUtility.GetCurrentStage() != stage || Document == null || EditorApplication.isPlaying) return;
            var current = Event.current;
            if (current.type == EventType.Repaint && Labels)
            {
                foreach (var path in paths)
                {
                    Handles.color = path.Kind == PathKind.Road ? Color.yellow : path.Kind == PathKind.River ? Color.cyan : Color.magenta;
                    if (path.Points.Length > 1) Handles.DrawAAPolyLine(2, path.Points);
                }
            }
            if (Selected.HasValue && current.type == EventType.Repaint)
            {
                Handles.color = Color.white;
                Handles.DrawWireDisc(World(Selected.Value) + Vector3.up * .3f, Vector3.up, .9f);
            }
            if (current.alt || current.button != 0 || Busy) return;
            if (current.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (current.type != EventType.MouseDown) return;
            var ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float distance)) return;
            var hit = ray.GetPoint(distance);
            int y = Mathf.RoundToInt(hit.z / 1.7537f + 4);
            int x = Mathf.RoundToInt(-hit.x / 2.025f + 5 - ((y & 1) == 1 ? .5f : 0));
            var cellAtHit = new GridPoint(x, y);
            if (!cells.Contains(cellAtHit)) return;
            Clicked?.Invoke(cellAtHit);
            current.Use();
        }

        public void ExportScene(string destination)
        {
            if (!root || Busy) throw new InvalidOperationException("Wait for a completed map preview before exporting.");
            if (!destination.StartsWith("Assets/", StringComparison.Ordinal) || !destination.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Export to a Unity scene inside Assets.");
            var previous = SceneManager.GetActiveScene();
            // Create additively without an editor "new scene" operation, which
            // may leave the current stage and destroy the source preview.
            var output = SceneManager.CreateScene("Hex map export " + Guid.NewGuid().ToString("N"));
            var exportedMaterials = new Dictionary<Material, Material>();
            string materialFolder = Path.GetDirectoryName(destination).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(destination) + " Materials";
            if (!AssetDatabase.IsValidFolder(materialFolder)) AssetDatabase.CreateFolder(Path.GetDirectoryName(materialFolder).Replace('\\', '/'), Path.GetFileName(materialFolder));
            try
            {
                var copy = UnityEngine.Object.Instantiate(root);
                CoordinateSurface.RemoveFromExport(copy);
                copy.name = Document.Name;
                SceneManager.MoveGameObjectToScene(copy, output);
                foreach (var renderer in copy.GetComponentsInChildren<Renderer>())
                    renderer.sharedMaterials = renderer.sharedMaterials.Select(material =>
                    {
                        if (!material) return null;
                        if (!exportedMaterials.TryGetValue(material, out var saved))
                        {
                            saved = new Material(material) { name = material.name.Replace(" (map preview)", ""), hideFlags = HideFlags.None };
                            AssetDatabase.CreateAsset(saved, AssetDatabase.GenerateUniqueAssetPath(materialFolder + "/Tile.mat"));
                            exportedMaterials.Add(material, saved);
                        }
                        return saved;
                    }).ToArray();
                var bounds = Bounds();
                var cameraObject = new GameObject("Main Camera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, output);
                cameraObject.tag = "MainCamera";
                var camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x / 1.5f) + 4;
                camera.transform.SetPositionAndRotation(bounds.center + new Vector3(0, 60, 25), Quaternion.Euler(67.4f, 180, 0));
                camera.farClipPlane = 250;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.13f, .17f, .22f);
                MapRenderQuality.ConfigureCamera(camera);
                MapLighting.Create(output);
                if (!EditorSceneManager.SaveScene(output, destination)) throw new IOException("Unity could not save the exported scene.");
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(output, true);
            }
        }

        public void Dispose()
        {
            EditorApplication.update -= Update;
            SceneView.duringSceneGui -= Draw;
            CancelBuild();
            if (stage && StageUtility.GetCurrentStage() == stage) StageUtility.GoToMainStage();
            if (stage) UnityEngine.Object.DestroyImmediate(stage);
            if (view)
            {
                view.sceneLighting = previousSceneLighting;
                view.sceneViewState.showImageEffects = previousImageEffects;
            }
            coordinateSurface?.Dispose();
            ClearMaterials();
        }

        void ClearMaterials()
        {
            foreach (var material in materials.Values) if (material) UnityEngine.Object.DestroyImmediate(material);
            materials.Clear();
        }

        void EnsureStage()
        {
            if (!stage || !stage.scene.IsValid())
            {
                bool labels = coordinateSurface?.Visible ?? true;
                coordinateSurface?.Dispose();
                ClearMaterials();
                models.Clear();
                stage = ScriptableObject.CreateInstance<MapStage>();
                StageUtility.GoToStage(stage, true);
                scene = stage.scene;
                // Stage changes may unload assets; allocate owned resources only
                // after the new preview scene has been entered.
                coordinateSurface = new CoordinateSurface { Visible = labels };
            }
            else if (StageUtility.GetCurrentStage() != stage) StageUtility.GoToStage(stage, true);
        }
    }
}
