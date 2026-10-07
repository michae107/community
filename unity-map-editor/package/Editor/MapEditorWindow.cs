using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IdleRpg.MapEditing;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace IdleRpg.UnityMapEditor
{
    internal sealed class MapDraft : ScriptableObject
    {
        public string Json;
    }

    public sealed class MapEditorWindow : EditorWindow
    {
        const string StateKey = "IdleRpg.MapEditor.";
        const string DefaultMap = "Assets/IdleRpgMap/src/map_editor/maps/SpringtideReach.hexmap";
        [SerializeField] MapDraft draft;
        MapDocument document;
        List<TileVariant> catalog;
        MapPreview preview;
        CancellationTokenSource cancellation;
        Task<GeneratedMap> job;
        string jobName, jobPath, filePath = "", savedJson = "", selectedPath = "", tool = "select", statusText = "Loading…";
        bool jobCommit, frameOnReady;
        int selectedPoint = -1;
        Label status, modeLabel, selectionLabel;
        TextField nameField, addressField;
        IntegerField widthField, heightField, radiusField, seedField;
        EnumField shapeField;
        EnumField waterField;
        DropdownField biomeField;
        ScrollView pathsView, pointsView;
        VisualElement editing;
        VisualElement pathActions;
        Button cancelButton;

        bool Busy => job != null || (preview?.Busy ?? false);
        MapPath SelectedPath => document?.Paths.FirstOrDefault(p => p.Id == selectedPath);

        [MenuItem("Tools/Idle RPG/Map Editor")]
        public static void Open()
        {
            var inspector = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow", true);
            var window = GetWindow<MapEditorWindow>("Hex Map", true, inspector);
            window.titleContent = new GUIContent("Hex Map");
            window.minSize = new Vector2(240, 250);
            window.Show();
            window.Focus();
            window.Guard(window.FrameMap);
        }

        void EnsureDocked()
        {
            if (!this || docked) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += EnsureDocked;
                return;
            }
            // GetWindow's docking preference only applies to newly created tabs.
            // Preserve the current document before replacing the old floating tab.
            Persist();
            hasUnsavedChanges = false;
            Close();
            Open();
        }

        void OnEnable()
        {
            minSize = new Vector2(240, 250);
            EditorApplication.delayCall += EnsureDocked;
            titleContent = new GUIContent("Hex Map");
            saveChangesMessage = "Save your hex-map changes before closing? The editable document is separate from the exported Unity scene.";
            if (!draft) draft = CreateInstance<MapDraft>();
            draft.hideFlags = HideFlags.HideAndDontSave;
            filePath = SessionState.GetString(StateKey + "File", "");
            savedJson = SessionState.GetString(StateKey + "Saved", "");
            try
            {
                catalog = TileCatalog.Load(File.ReadAllText("Assets/IdleRpgMap/TileCatalog.json"));
                string json = string.IsNullOrEmpty(draft.Json) ? SessionState.GetString(StateKey + "Draft", "") : draft.Json;
                if (string.IsNullOrEmpty(json))
                {
                    filePath = DefaultMap;
                    json = File.ReadAllText(filePath);
                    savedJson = MapDocument.Deserialize(json).Serialize();
                }
                document = MapDocument.Deserialize(json);
                draft.Json = document.Serialize();
                QueuePreview();
            }
            catch (Exception exception) { SetStatus(exception.Message); }
            Undo.undoRedoPerformed += UndoChanged;
            EditorApplication.update += UpdateJob;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            UpdateDirty();
        }

        void OnDisable()
        {
            Persist();
            EditorApplication.delayCall -= InitializePreview;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            StopPreview();
            EditorApplication.update -= UpdateJob;
            EditorApplication.delayCall -= EnsureDocked;
            Undo.undoRedoPerformed -= UndoChanged;
        }

        void StopPreview()
        {
            cancellation?.Cancel();
            cancellation?.Dispose();
            cancellation = null;
            job = null;
            preview?.Dispose();
            preview = null;
        }

        void QueuePreview()
        {
            EditorApplication.delayCall -= InitializePreview;
            EditorApplication.delayCall += InitializePreview;
        }

        void InitializePreview()
        {
            if (!this || document == null || preview != null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                QueuePreview();
                return;
            }
            try
            {
                // Opening a stage unloads unused assets. Wait until Unity has
                // finished restoring rendering resources after a domain reload.
                preview = new MapPreview { Clicked = p => Guard(() => Tile(p)), Failed = PreviewFailed, Ready = PreviewReady,
                    Labels = SessionState.GetBool(StateKey + "Coordinates", true) };
                frameOnReady = true;
                StartSolve(document, "Load map", false);
            }
            catch (Exception exception) { PreviewFailed(exception.GetBaseException().Message); }
        }

        void PlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                Persist();
                EditorApplication.delayCall -= InitializePreview;
                StopPreview();
                SetStatus("Map preview will return when Play mode stops.");
            }
            else if (change == PlayModeStateChange.EnteredEditMode) QueuePreview();
            RefreshFields();
        }

        void FrameMap()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play mode before opening the map preview.");
            if (preview == null) QueuePreview();
            else
            {
                frameOnReady = true;
                preview.Frame();
                RefreshFields();
            }
        }

        void PreviewFailed(string message)
        {
            SetStatus(message);
            RefreshFields();
        }

        void OnDestroy()
        {
            if (draft) { Undo.ClearUndo(draft); DestroyImmediate(draft); }
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = root.style.paddingRight = 8;
            root.style.paddingTop = 6;
            modeLabel = new Label();
            modeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            root.Add(modeLabel);
            status = new Label(statusText);
            status.style.whiteSpace = WhiteSpace.Normal;
            status.style.marginBottom = 6;
            root.Add(status);
            cancelButton = new Button(Cancel) { text = "Cancel", tooltip = "Cancel map generation" };
            root.Add(cancelButton);
            var toolbar = new Toolbar();
            var files = new ToolbarMenu { text = "File", tooltip = "Map file actions" };
            files.menu.AppendAction("Open…", _ => Guard(OpenDocument));
            files.menu.AppendAction("Save", _ => Guard(() => SaveDocument(false)));
            files.menu.AppendAction("Save as…", _ => Guard(() => SaveDocument(true)));
            files.menu.AppendAction("Export scene…", _ => Guard(Export));
            toolbar.Add(files);
            var viewMenu = new ToolbarMenu { text = "View", tooltip = "Map view" };
            viewMenu.menu.AppendAction("Frame map", _ => Guard(FrameMap));
            toolbar.Add(viewMenu);
            root.Add(toolbar);
            editing = new ScrollView();
            editing.style.flexGrow = 1;
            root.Add(editing);
            nameField = new TextField("Map name");
            nameField.isDelayed = true;
            nameField.RegisterValueChangedCallback(e => Edit("Rename map", d => d.Name = e.newValue));
            editing.Add(nameField);
            biomeField = new DropdownField("Biome", new List<string> { "Summer", "Fall", "Winter", "Default" }, 0);
            biomeField.RegisterValueChangedCallback(e => Edit("Change biome", d => d.Biome = "res://src/map/biomes/" + e.newValue + ".tres"));
            editing.Add(biomeField);
            var creation = new Foldout { text = "New map and generation", value = false };
            editing.Add(creation);
            shapeField = new EnumField("Shape", MapShape.Rectangle);
            widthField = new IntegerField("Width") { value = 23 };
            heightField = new IntegerField("Height") { value = 15 };
            radiusField = new IntegerField("Hex radius") { value = 8 };
            seedField = new IntegerField("Generation seed") { value = 1 };
            creation.Add(shapeField); creation.Add(widthField); creation.Add(heightField); creation.Add(radiusField); creation.Add(seedField);
            var newButtons = Row(creation);
            Button(newButtons, "Create blank map", NewDocument);
            Button(newButtons, "Generate seeded map", Generate);
            var tools = Row(editing);
            foreach (var mode in new[] { "select", "road", "river", "coast" })
            {
                string selected = mode;
                Button(tools, char.ToUpper(mode[0]) + mode.Substring(1), () => SetTool(selected));
            }
            var labels = new Toggle("Coordinates and paths") { value = preview?.Labels ?? true,
                tooltip = "Show or hide the lettering on the tile surfaces and the path guides." };
            labels.RegisterValueChangedCallback(e =>
            {
                SessionState.SetBool(StateKey + "Coordinates", e.newValue);
                if (preview != null) preview.Labels = e.newValue;
                SceneView.RepaintAll();
            });
            editing.Add(labels);
            addressField = new TextField("Coordinate") { value = "A1", tooltip = "Tile coordinate, such as A1. Uses the selected tool." };
            editing.Add(addressField);
            var coordinateButtons = Row(editing);
            Button(coordinateButtons, "Add / select", () => Tile(Parse(addressField.value)));
            selectionLabel = new Label();
            editing.Add(selectionLabel);
            selectionLabel.style.whiteSpace = WhiteSpace.Normal;
            pathActions = new VisualElement();
            editing.Add(pathActions);
            var pathButtons = Row(pathActions);
            Button(pathButtons, "Edit spline", Extend);
            Button(pathButtons, "Finish path", Finish);
            Button(pathButtons, "Delete path", RemovePath);
            waterField = new EnumField("Water side", WaterSide.Left);
            waterField.RegisterValueChangedCallback(e => Guard(() => Water((WaterSide)e.newValue)));
            pathActions.Add(waterField);
            editing.Add(new Label("Paths (select a path to edit its points)"));
            pathsView = new ScrollView { style = { minHeight = 50, maxHeight = 200 } };
            editing.Add(pathsView);
            editing.Add(new Label("Selected path points"));
            pointsView = new ScrollView { style = { minHeight = 50, maxHeight = 240 } };
            editing.Add(pointsView);
            RefreshFields();
        }

        static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            parent.Add(row);
            return row;
        }

        void Button(VisualElement parent, string text, Action action)
        {
            parent.Add(new Button(() => Guard(action)) { text = text, tooltip = text });
        }

        void Guard(Action action)
        {
            try { action(); }
            catch (Exception exception) { SetStatus(exception.GetBaseException().Message); }
        }

        void SetStatus(string message)
        {
            statusText = message;
            if (status != null) status.text = message;
            Repaint();
        }

        void RefreshFields()
        {
            if (nameField == null || document == null) return;
            nameField.SetValueWithoutNotify(document.Name);
            string season = Path.GetFileNameWithoutExtension(document.Biome);
            biomeField.SetValueWithoutNotify(biomeField.choices.Contains(season) ? season : "Default");
            editing.SetEnabled(preview != null && !Busy && !EditorApplication.isPlayingOrWillChangePlaymode);
            cancelButton.style.display = job != null ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshSelection();
            UpdateDirty();
        }

        void RefreshSelection()
        {
            if (modeLabel == null || document == null) return;
            modeLabel.text = "Hex Map — " + tool.ToUpperInvariant() + " tool";
            pathActions.style.display = SelectedPath != null ? DisplayStyle.Flex : DisplayStyle.None;
            waterField.style.display = SelectedPath?.Kind == PathKind.Coast ? DisplayStyle.Flex : DisplayStyle.None;
            if (SelectedPath != null) waterField.SetValueWithoutNotify(SelectedPath.WaterSide);
            selectionLabel.text = "Selected tile: " + (preview?.Selected?.ToString() ?? "none") + "; path: " +
                (SelectedPath == null ? "none" : (document.Paths.IndexOf(SelectedPath) + 1) + " " + SelectedPath.Kind) +
                (selectedPoint >= 0 ? "; point " + (selectedPoint + 1) : "");
            pathsView.Clear();
            for (int i = 0; i < document.Paths.Count; i++)
            {
                var path = document.Paths[i];
                Button(pathsView, (path.Id == selectedPath ? "▶ " : "") + (i + 1) + ". " + path.Kind + " — " + path.Points.Count + " points" +
                    (path.Kind == PathKind.Coast ? " — water " + path.WaterSide : ""), () => SelectPath(path.Id));
            }
            pointsView.Clear();
            var selected = SelectedPath;
            if (selected == null) return;
            for (int i = 0; i < selected.Points.Count; i++)
            {
                int index = i;
                var row = Row(pointsView);
                var coordinate = new TextField("Point " + (i + 1)) { value = selected.Points[i].ToString(), isDelayed = true,
                    tooltip = "Edit this point's coordinate" };
                coordinate.style.flexGrow = 1;
                coordinate.style.minWidth = 100;
                coordinate.labelElement.style.minWidth = 48;
                coordinate.labelElement.style.width = 48;
                coordinate.RegisterCallback<FocusInEvent>(_ =>
                {
                    selectedPoint = index;
                    preview.Selected = selected.Points[index];
                    SceneView.RepaintAll();
                });
                coordinate.RegisterValueChangedCallback(e => Guard(() => { selectedPoint = index; MovePoint(Parse(e.newValue)); }));
                row.Add(coordinate);
                row.Add(new Button(() => Guard(() => { selectedPoint = index; RemovePoint(); }))
                    { text = "×", tooltip = "Delete point " + (i + 1) });
            }
        }

        void EnsureReady()
        {
            if (Busy) throw new InvalidOperationException("Wait for the current operation, or cancel generation.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play mode before editing a map.");
            if (document == null || preview == null) throw new InvalidOperationException("The map assets did not load. Close the tool, finish importing, and reopen it.");
        }

        void Edit(string description, Action<MapDocument> edit)
        {
            Guard(() =>
            {
                EnsureReady();
                var candidate = document.Copy();
                edit(candidate);
                candidate.Validate();
                StartSolve(candidate, description, true);
            });
        }

        void StartSolve(MapDocument candidate, string description, bool commit, string newFile = null)
        {
            cancellation?.Cancel();
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var token = cancellation.Token;
            jobName = description;
            jobCommit = commit;
            jobPath = newFile;
            job = Task.Run(() => new GeneratedMap(candidate, MapSolver.Solve(candidate, catalog, token)), token);
            SetStatus(description + " — checking tile connections…");
            RefreshFields();
        }

        void UpdateJob()
        {
            if (job == null || !job.IsCompleted) return;
            var finished = job;
            job = null;
            try
            {
                var result = finished.GetAwaiter().GetResult();
                // Resolve every model before replacing the editable document.
                preview.Build(result.Document, result.Solution);
                if (jobCommit) Undo.RecordObject(draft, jobName);
                document = result.Document;
                draft.Json = document.Serialize();
                if (jobPath != null)
                {
                    filePath = jobPath;
                    savedJson = string.IsNullOrEmpty(filePath) ? "" : draft.Json;
                    Undo.ClearUndo(draft);
                    selectedPath = "";
                    selectedPoint = -1;
                }
                SetStatus(jobName + " — building " + result.Solution.Tiles.Count + " tiles…");
                Persist();
            }
            catch (OperationCanceledException) { SetStatus("Operation cancelled or timed out. The previous map is unchanged."); }
            catch (Exception exception) { SetStatus(jobName + " rejected: " + exception.GetBaseException().Message); }
            finally { RefreshFields(); }
        }

        void PreviewReady()
        {
            SetStatus(preview.TileCount + " tiles ready. " + (string.IsNullOrEmpty(filePath) ? "Unsaved map." : Path.GetFileName(filePath)));
            RefreshFields();
            if (frameOnReady) { frameOnReady = false; preview.Frame(); }
        }

        void Cancel()
        {
            if (job == null) { SetStatus("No solver operation is running."); return; }
            cancellation.Cancel();
            SetStatus("Cancelling…");
        }

        void UndoChanged()
        {
            if (draft && !string.IsNullOrEmpty(draft.Json) && draft.Json != document?.Serialize())
                Guard(() => StartSolve(MapDocument.Deserialize(draft.Json), "Undo / redo", false));
        }

        void UpdateDirty() => hasUnsavedChanges = document != null && document.Serialize() != savedJson;
        void Persist()
        {
            if (!draft) return;
            SessionState.SetString(StateKey + "Draft", draft.Json ?? "");
            SessionState.SetString(StateKey + "Saved", savedJson);
            SessionState.SetString(StateKey + "File", filePath);
        }

        public override void SaveChanges()
        {
            if (SaveDocument(false)) base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            SessionState.EraseString(StateKey + "Draft");
            if (draft) draft.Json = savedJson;
            base.DiscardChanges();
        }

        bool ConfirmReplace()
        {
            EnsureReady();
            if (!hasUnsavedChanges) return true;
            int result = EditorUtility.DisplayDialogComplex("Replace map?", "Save the current map first?", "Save", "Cancel", "Discard");
            return result == 2 || result == 0 && SaveDocument(false);
        }

        bool SaveDocument(bool saveAs)
        {
            EnsureReady();
            string destination = filePath;
            // Never overwrite the copied reference maps unless the user explicitly chooses that path.
            if (saveAs || string.IsNullOrEmpty(destination) || destination.StartsWith("Assets/IdleRpgMap/src/", StringComparison.Ordinal))
                destination = EditorUtility.SaveFilePanel("Save editable map", Application.dataPath, document.Name, "hexmap");
            if (string.IsNullOrEmpty(destination)) return false;
            string json = document.Serialize();
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            filePath = destination;
            savedJson = json;
            Persist();
            UpdateDirty();
            AssetDatabase.Refresh();
            SetStatus("Saved " + destination);
            return true;
        }

        void OpenDocument()
        {
            if (!ConfirmReplace()) return;
            string path = EditorUtility.OpenFilePanel("Open hex map", Application.dataPath + "/IdleRpgMap/src/map_editor/maps", "hexmap");
            if (string.IsNullOrEmpty(path)) return;
            var candidate = MapDocument.Deserialize(File.ReadAllText(path));
            frameOnReady = true;
            StartSolve(candidate, "Open map", false, path);
        }

        void NewDocument()
        {
            if (!ConfirmReplace()) return;
            var candidate = new MapDocument { Shape = (MapShape)shapeField.value, Width = widthField.value, Height = heightField.value, Radius = radiusField.value };
            candidate.Validate();
            frameOnReady = true;
            StartSolve(candidate, "New map", false, "");
        }

        void Generate()
        {
            if (!ConfirmReplace()) return;
            int seed = seedField.value;
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var token = cancellation.Token;
            jobName = "Generate map"; jobCommit = false; jobPath = "";
            frameOnReady = true;
            job = Task.Run(() => MapGenerator.GenerateSolved(seed, catalog, token), token);
            SetStatus("Generating seed " + seed + " — solving in the background…");
            RefreshFields();
        }

        void Export()
        {
            EnsureReady();
            string path = EditorUtility.SaveFilePanelInProject("Export playable scene", document.Name, "unity", "Choose a Unity scene for the generated tiles.");
            if (string.IsNullOrEmpty(path)) return;
            preview.ExportScene(path);
            SetStatus("Exported " + path + ". Open it and press Play to view the map. Continue editing the .hexmap here.");
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
        }

        void SetTool(string value)
        {
            EnsureReady();
            tool = value;
            selectedPath = "";
            selectedPoint = -1;
            RefreshSelection();
        }

        void Finish()
        {
            EnsureReady();
            selectedPath = "";
            selectedPoint = -1;
            tool = "select";
            RefreshSelection();
        }

        void Extend()
        {
            EnsureReady();
            var path = SelectedPath ?? throw new InvalidOperationException("Select a path first.");
            tool = path.Kind.ToString().ToLowerInvariant();
            selectedPoint = -1;
            RefreshSelection();
        }

        static GridPoint Parse(string text) => GridPoint.TryParse(text.Replace(" ", ""), out var point)
            ? point : throw new ArgumentException("Use a tile coordinate such as A1.");

        void Tile(GridPoint point)
        {
            EnsureReady();
            if (!document.Cells().Contains(point)) throw new ArgumentException("That coordinate is outside this map.");
            preview.Selected = point;
            addressField?.SetValueWithoutNotify(point.ToString());
            SceneView.RepaintAll();
            if (tool == "select")
            {
                var candidates = document.Paths.Where(p => HexPathGrid.Expand(p).Contains(point)).ToList();
                if (candidates.Count > 0)
                {
                    var path = candidates.FirstOrDefault(p => p.Id != selectedPath) ?? candidates[0];
                    selectedPath = path.Id;
                    selectedPoint = path.Points.IndexOf(point);
                }
                RefreshSelection();
                return;
            }
            var kind = (PathKind)Enum.Parse(typeof(PathKind), tool, true);
            string id = SelectedPath?.Kind == kind ? selectedPath : Guid.NewGuid().ToString("N");
            selectedPath = id;
            Edit("Add " + tool + " point", candidate =>
            {
                var path = candidate.Paths.FirstOrDefault(p => p.Id == id);
                if (path == null) { path = new MapPath { Id = id, Kind = kind }; candidate.Paths.Add(path); }
                if (path.Points.Count == 0 || path.Points.Last() != point) path.Points.Add(point);
            });
        }

        void SelectPath(string id)
        {
            EnsureReady();
            selectedPath = id;
            selectedPoint = -1;
            tool = "select";
            RefreshSelection();
        }

        void MovePoint(GridPoint point)
        {
            EnsureReady();
            if (SelectedPath == null || selectedPoint < 0) throw new InvalidOperationException("Select a path and one of its points first.");
            if (!document.Cells().Contains(point)) throw new ArgumentException("Choose a coordinate inside this map.");
            string id = selectedPath; int index = selectedPoint;
            Edit("Move map point", d => d.Paths.First(p => p.Id == id).Points[index] = point);
            preview.Selected = point;
        }

        void RemovePoint()
        {
            EnsureReady();
            if (SelectedPath == null || selectedPoint < 0) throw new InvalidOperationException("Select a path point first.");
            string id = selectedPath; int index = selectedPoint;
            Edit("Delete map point", d =>
            {
                var path = d.Paths.First(p => p.Id == id);
                path.Points.RemoveAt(index);
                if (path.Points.Count == 0) d.Paths.Remove(path);
            });
            selectedPoint = -1;
        }

        void RemovePath()
        {
            EnsureReady();
            if (SelectedPath == null) throw new InvalidOperationException("Select a path first.");
            string id = selectedPath;
            Edit("Delete map path", d => d.Paths.RemoveAll(p => p.Id == id));
        }

        void Water(WaterSide side)
        {
            EnsureReady();
            if (SelectedPath?.Kind != PathKind.Coast) throw new InvalidOperationException("Select a coastline first.");
            string id = selectedPath;
            Edit("Change coast water side", d => d.Paths.First(p => p.Id == id).WaterSide = side);
        }

    }
}
