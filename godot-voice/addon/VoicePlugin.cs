#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections;
using System.Text;
using File = System.IO.File;
using Environment = System.Environment;

namespace TalonVoice;

[Tool]
public partial class VoicePlugin : EditorPlugin
{
    private NativeHints _hints = new();
    private UiHints _uiHints = new();
    private readonly Dictionary<ulong, HintOverlay> _popupOverlays = new();
    private HintOverlay? _overlay;
    private string _endpoint = "";
    private double _refreshTime;
    private double _stateTime;
    private double _pollTime;
    private double _cleanupTime;
    private bool _executing;
    private (string Id, UiPointerAction Action, ulong Frame, long Created)? _pendingPointer;
    private string _lastError = "";
    private static EditorInterface Editor => EditorInterface.Singleton;

    public override void _EnterTree()
    {
        Name = "TalonVoice";
        EditorControls.Initialize(GetTree());
        _endpoint = Path.Combine(Path.GetTempPath(), "talon-godot-" + Environment.UserName, Environment.ProcessId.ToString());
        Directory.CreateDirectory(_endpoint);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.GetDirectoryName(_endpoint)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(_endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _overlay = new HintOverlay { Name = "TalonVoiceHints", Hints = _hints, UiHints = _uiHints, MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 4096 };
        Editor.GetBaseControl().AddChild(_overlay);
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddToolMenuItem("Talon Voice: Toggle letters", Callable.From(() => _hints.Enabled = !_hints.Enabled));
        AddToolMenuItem("Talon Voice: Reset letters", Callable.From(() => _hints.Reset()));
        WriteState();
        SetProcess(true);
        SetProcessInput(true);
        GD.Print("Talon Voice ready: " + ProjectSettings.GlobalizePath("res://"));
    }

    public override void _ExitTree()
    {
        SetProcess(false);
        SetProcessInput(false);
        if (_pendingPointer is { } pending)
            WriteJson(Path.Combine(_endpoint, pending.Id + ".response.json"), new { id = pending.Id, result = (object?)null, error = "Addon reloaded before the click; say the command again." });
        _pendingPointer = null;
        RemoveToolMenuItem("Talon Voice: Toggle letters");
        RemoveToolMenuItem("Talon Voice: Reset letters");
        if (GodotObject.IsInstanceValid(_overlay))
        {
            _overlay!.Hints = null;
            _overlay.UiHints = null;
            _overlay.Free();
        }
        _overlay = null;
        _hints = new();
        foreach (var overlay in _popupOverlays.Values)
            if (GodotObject.IsInstanceValid(overlay)) { overlay.UiHints = null; overlay.Free(); }
        _popupOverlays.Clear();
        _uiHints = new();
        EditorControls.Shutdown();
        if (_endpoint.Length > 0) File.Delete(Path.Combine(_endpoint, "state.json"));
    }

    public override void _Input(InputEvent input)
    {
        if (input is InputEventKey key && UiActions.NavigateInspector(key))
            GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (_executing) return;
        _refreshTime += delta;
        _stateTime += delta;
        _pollTime += delta;
        _cleanupTime += delta;
        try
        {
            if (_pendingPointer is { } pending && Engine.GetProcessFrames() > pending.Frame + 2)
            {
                _pendingPointer = null;
                string? failure = null;
                try
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pending.Created > 2500)
                        throw new InvalidOperationException("Voice click expired before dispatch.");
                    var live = _uiHints.Resolve(pending.Action.Target.Letters);
                    if (live.Key != pending.Action.Target.Key || live.Rect != pending.Action.Target.Rect)
                        throw new InvalidOperationException("The control moved before the click; say the command again.");
                    pending.Action.Commit();
                }
                catch (Exception e) { failure = e.Message; }
                WriteJson(Path.Combine(_endpoint, pending.Id + ".response.json"), new { id = pending.Id, result = new { ok = failure is null }, error = failure });
                _lastError = failure ?? "";
            }
            // Requests don't wait for a visual scan. Background editors need
            // fewer badge updates, but keep their command endpoint responsive.
            if (_pollTime >= .05) { _pollTime = 0; Poll(); }
            double refreshInterval = Editor.GetBaseControl().GetWindow().HasFocus() ? .25 : 1;
            if (_refreshTime >= refreshInterval)
            {
                _refreshTime = 0;
                Window? active = _hints.Enabled || _uiHints.Enabled ? EditorControls.ActiveDialog() : null;
                if (_hints.Enabled && active is null) _hints.Refresh();
                else _hints.Targets.Clear();
                if (_uiHints.Enabled) _uiHints.Refresh();
                else _uiHints.Targets.Clear();
                RefreshPopupOverlays(active);
                _overlay?.UpdateVisuals(showNative: active is null);
            }
            if (_stateTime >= 1) { _stateTime = 0; WriteState(); }
            if (_cleanupTime >= 30) { _cleanupTime = 0; CleanupResponses(); }
        }
        catch (Exception e)
        {
            if (_lastError != e.Message) GD.PushWarning("Talon Voice: " + e.Message);
            _lastError = e.Message;
        }
    }

    private void Poll()
    {
        if (_pendingPointer is not null) return;
        foreach (string request in Directory.EnumerateFiles(_endpoint, "*.request.json").Take(8))
        {
            string id = Path.GetFileName(request).Replace(".request.json", "");
            if (id.Length != 32 || !id.All(Uri.IsHexDigit)) continue;
            object? result = null;
            string? error = null;
            long created = 0;
            try
            {
                _executing = true;
                if (new FileInfo(request).Length > 128 * 1024) throw new InvalidOperationException("Voice request is too large.");
                using var json = JsonDocument.Parse(File.ReadAllText(request));
                var root = json.RootElement;
                if (root.GetProperty("id").GetString() != id) throw new InvalidOperationException("Request ID mismatch.");
                created = root.GetProperty("created").GetInt64();
                long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - created;
                if (age > 2500 || age < -5000) throw new InvalidOperationException("Voice request expired; say the command again.");
                result = Execute(root.GetProperty("method").GetString()!, root.GetProperty("args"));
                _lastError = "";
            }
            catch (Exception e) { error = e.Message; _lastError = e.Message; }
            finally { _executing = false; _refreshTime = 1; }
            if (result is UiPointerAction pointer && error is null)
            {
                _pendingPointer = (id, pointer, Engine.GetProcessFrames(), created);
                File.Delete(request);
                break;
            }
            WriteJson(Path.Combine(_endpoint, id + ".response.json"), new { id, result, error });
            File.Delete(request);
        }
    }

    private void CleanupResponses()
    {
        // Maintenance is separate from the latency-sensitive request polling.
        foreach (string response in Directory.EnumerateFiles(_endpoint, "*.response.json"))
            if (File.GetLastWriteTimeUtc(response) < DateTime.UtcNow.AddMinutes(-1)) File.Delete(response);
    }

    private void WriteState() => WriteJson(Path.Combine(_endpoint, "state.json"), new
    {
        version = 1, pid = Environment.ProcessId, project = ProjectSettings.GlobalizePath("res://").TrimEnd('/'),
        focused = Editor.GetBaseControl().GetWindow().HasFocus(), updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        hints = _hints.Enabled, error = _lastError,
        ui_hints = _uiHints.Enabled, ui_scope = _uiHints.Scope,
    });

    private void RefreshPopupOverlays(Window? active)
    {
        if (!_uiHints.Enabled) active = null;
        foreach (var pair in _popupOverlays.ToArray())
            if (!GodotObject.IsInstanceValid(pair.Value) || active is null || pair.Key != active.GetInstanceId())
            {
                if (GodotObject.IsInstanceValid(pair.Value)) { pair.Value.UiHints = null; pair.Value.Free(); }
                _popupOverlays.Remove(pair.Key);
            }
        if (active is null) return;
        ulong id = active.GetInstanceId();
        if (!_popupOverlays.TryGetValue(id, out var overlay))
        {
            overlay = new HintOverlay { Name = "TalonVoicePopupHints", UiHints = _uiHints, MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 4096 };
            active.AddChild(overlay);
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _popupOverlays[id] = overlay;
        }
        overlay.UpdateVisuals(showNative: false);
    }

    private static void WriteJson(string path, object value)
    {
        // JsonSerializer's shared type cache prevents collectible Godot assemblies
        // from unloading. Stream values without registering any project types.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteValue(writer, value);
        File.WriteAllText(path + ".tmp", Encoding.UTF8.GetString(stream.ToArray()));
        File.Move(path + ".tmp", path, true);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case IEnumerable values:
                writer.WriteStartArray();
                foreach (object? item in values) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStartObject();
                foreach (var property in value.GetType().GetProperties())
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.GetValue(value));
                }
                writer.WriteEndObject();
                break;
        }
    }

    private static string Text(JsonElement args, string key, string fallback = "") => args.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
    private static int Number(JsonElement args, string key, int fallback = 1) => args.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;

    private object? Execute(string method, JsonElement args)
    {
        if (method is not ("status" or "ui.inspect" or "shortcuts" or "editor.cancel") && !method.StartsWith("hints.") && !method.StartsWith("ui.") && EditorControls.ActiveDialog() is Window dialog)
            throw new InvalidOperationException("Finish or cancel the '" + dialog.Title + "' dialog first.");
        switch (method)
        {
            case "status":
                _hints.Refresh();
                _uiHints.Refresh();
                return new
                {
                    project = ProjectSettings.GlobalizePath("res://"), scene = Editor.GetEditedSceneRoot()?.SceneFilePath,
                    selected = Editor.GetSelection().GetSelectedNodes().Select(n => n.GetPath().ToString()).ToArray(),
                    focus = Editor.GetBaseControl().GetViewport().GuiGetFocusOwner()?.GetClass().ToString(),
                    hovered = Editor.GetBaseControl().GetViewport().GuiGetHoveredControl()?.GetClass().ToString(),
                    hints = _hints.Targets.Select(t => new { scope = t.Scope, letters = t.Letters, text = t.Text, path = t.Path }).ToArray(),
                    ui_hints = _uiHints.Targets.Select(t => new { letters = t.Letters, text = t.Text, kind = t.Kind,
                        path = t.Control.GetPath().ToString(), value = UiValue(t.Control), window = t.Window.Title.ToString(),
                        rect = new { x = (double)t.Rect.Position.X, y = (double)t.Rect.Position.Y, width = (double)t.Rect.Size.X, height = (double)t.Rect.Size.Y } }).ToArray(),
                    tabs = Editor.GetOpenScenes(), unsaved = Editor.GetUnsavedScenes(), playing = Editor.IsPlayingScene(), error = _lastError,
                    dialogs = EditorControls.Windows.Where(w => w.Visible && (!w.Unfocusable || w is PopupMenu)).Select(w => w.Title).ToArray(),
                    ui_windows = EditorControls.Windows.Where(w => w.Visible)
                        .Select(w => new { name = w.Name.ToString(), kind = w.GetClass().ToString(), unfocusable = w.Unfocusable, title = w.Title.ToString() }).ToArray(),
                };
            case "ui.inspect":
                string kind = Text(args, "class");
                Node inspectRoot = Text(args, "root_class") is string rootClass && rootClass.Length > 0
                    ? EditorControls.Find(rootClass) ?? throw new InvalidOperationException("Inspector root not found.") : Editor.GetBaseControl();
                return EditorControls.Walk(inspectRoot).OfType<Control>()
                    .Where(c => (kind.Length == 0 ? c is Tree or ItemList or TabBar or LineEdit or BaseButton : c.GetClass().ToString() == kind))
                    .Select(c => new { name = c.Name.ToString(), type = c.GetClass().ToString(), path = c.GetPath().ToString(), visible = c.IsVisibleInTree(), tooltip = c.TooltipText, text = c is Button button ? button.Text : c is LineEdit line ? line.PlaceholderText : "" }).Take(1000).ToArray();
            case "shortcuts": return Editor.GetEditorSettings().GetShortcutList().Where(s => s.Contains(Text(args, "filter"), StringComparison.OrdinalIgnoreCase)).ToArray();
            case "hints.show": _hints.Enabled = true; break;
            case "hints.hide": _hints.Enabled = false; break;
            case "hints.reset": _hints.Reset(); break;
            case "ui.hints.show": _uiHints.Enabled = true; break;
            case "ui.hints.hide": _uiHints.Enabled = false; break;
            case "ui.hints.toggle": _uiHints.Enabled = !_uiHints.Enabled; break;
            case "ui.hints.reset": _uiHints.Reset(); break;
            case "ui.hints.scope":
                string hintScope = Text(args, "scope", "all");
                if (hintScope is not ("all" or "inspector" or "signals" or "groups")) throw new InvalidOperationException("Unknown UI hint scope.");
                if (hintScope != "all") Focus(hintScope);
                _uiHints.Scope = hintScope; _uiHints.Enabled = true; _uiHints.Reset(); break;
            case "ui.click": case "ui.double_click": case "ui.menu": case "ui.focus": case "ui.change": case "ui.hover": case "ui.text": case "ui.number": case "ui.step": case "ui.scroll":
                var uiTarget = _uiHints.Resolve(Text(args, "letters"));
                switch (method)
                {
                    case "ui.click": return (object?)UiActions.Click(uiTarget) ?? new { ok = true };
                    case "ui.double_click": return (object?)UiActions.Click(uiTarget, twice: true) ?? new { ok = true };
                    case "ui.menu": return (object?)UiActions.Click(uiTarget, MouseButton.Right) ?? new { ok = true };
                    case "ui.focus": return (object?)UiActions.Focus(uiTarget) ?? new { ok = true };
                    case "ui.change":
                        if (uiTarget.Control is LineEdit or TextEdit) return UiActions.SetText(uiTarget, "", false);
                        return (object?)UiActions.Focus(uiTarget, true) ?? new { ok = true };
                    case "ui.hover": UiActions.Hover(uiTarget); break;
                    case "ui.text": return UiActions.SetText(uiTarget, Text(args, "text"), args.TryGetProperty("submit", out var submit) && submit.GetBoolean());
                    case "ui.number": return UiActions.SetNumber(uiTarget, args.GetProperty("value").GetDouble());
                    case "ui.step": return UiActions.Step(uiTarget, Number(args, "direction"), Number(args, "count"));
                    case "ui.scroll": UiActions.Scroll(uiTarget, Number(args, "direction"), Number(args, "count")); break;
                }
                break;
            case "focus": Focus(Text(args, "pane")); break;
            case "search":
                string scope = Text(args, "scope");
                Focus(scope);
                Node dock = scope == "scene" ? EditorControls.Find("SceneTreeDock")! : Editor.GetFileSystemDock();
                var search = EditorControls.Walk(dock).OfType<LineEdit>().FirstOrDefault(l => l.IsVisibleInTree() && l.PlaceholderText.Length > 0)
                    ?? throw new InvalidOperationException("Search field is not visible.");
                search.GrabFocus();
                if (args.TryGetProperty("text", out var query)) { search.Text = query.GetString() ?? ""; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); }
                search.SelectAll();
                break;
            case "tree.select": case "tree.open": case "tree.toggle": case "tree.expand": case "tree.collapse": case "tree.parent": case "tree.copy_path": case "tree.rename": case "tree.delete": case "tree.duplicate":
                TreeCommand(method, args); break;
            case "tree.root": case "tree.collapse_all":
                Focus(Text(args, "scope"));
                if (method == "tree.root" && Text(args, "scope") == "files") { Editor.GetFileSystemDock().NavigateToPath("res://"); break; }
                var tree = Text(args, "scope") == "scene" ? EditorControls.SceneTree : EditorControls.Walk(Editor.GetFileSystemDock()).OfType<Tree>().FirstOrDefault(t => t.IsVisibleInTree());
                if (tree?.GetRoot() is not TreeItem root) throw new InvalidOperationException("No visible tree.");
                if (method == "tree.collapse_all") { root.SetCollapsedRecursive(true); root.Collapsed = false; }
                else { root.Collapsed = false; _hints.Select(new("scene", "", root.GetText(0), root.GetMetadata(0).ToString(), tree, default, root)); }
                break;
            case "tree.key":
                Focus(Text(args, "scope"));
                EditorControls.Key(Text(args, "direction") switch { "up" => Godot.Key.Up, "down" => Godot.Key.Down, "left" => Godot.Key.Left, "right" => Godot.Key.Right, "home" => Godot.Key.Home, "end" => Godot.Key.End, "page_up" => Godot.Key.Pageup, "page_down" => Godot.Key.Pagedown, _ => throw new InvalidOperationException("Unknown direction.") });
                break;
            case "scene.action":
                Focus("scene");
                EditorControls.Shortcut("scene_tree/" + Text(args, "action"));
                break;
            case "files.action":
                Focus("files");
                EditorControls.Shortcut("filesystem_dock/" + Text(args, "action"));
                break;
            case "script.action":
                Focus("Script");
                EditorControls.Shortcut("script_editor/" + Text(args, "action"));
                break;
            case "view.action":
                Focus("viewport3d");
                EditorControls.Shortcut("spatial_editor/" + Text(args, "action"));
                break;
            case "scene.script":
                var node = Editor.GetSelection().GetSelectedNodes().FirstOrDefault() ?? throw new InvalidOperationException("Select a scene node first.");
                if (node.GetScript().AsGodotObject() is not Script script) throw new InvalidOperationException("Selected node has no script.");
                Editor.EditScript(script); break;
            case "files.up":
                string directory = Editor.GetCurrentDirectory();
                Editor.GetFileSystemDock().NavigateToPath(directory is "res://" or "" ? "res://" : directory.TrimEnd('/').GetBaseDir()); break;
            case "files.reveal":
                string path = Editor.GetCurrentPath();
                if (!path.StartsWith("res://")) throw new InvalidOperationException("Select a project file first.");
                OS.ShellShowInFileManager(ProjectSettings.GlobalizePath(path)); break;
            case "tabs.select":
                var tabs = EditorControls.SceneTabs ?? throw new InvalidOperationException("Scene tabs unavailable.");
                int index = Number(args, "index") - 1;
                if (index < 0 || index >= tabs.TabCount) throw new InvalidOperationException("No scene tab at that number.");
                tabs.CurrentTab = index; break;
            case "tabs.next": case "tabs.previous":
                var bar = EditorControls.SceneTabs ?? throw new InvalidOperationException("Scene tabs unavailable.");
                if (bar.TabCount > 0) bar.CurrentTab = (bar.CurrentTab + bar.TabCount + (method == "tabs.next" ? 1 : -1)) % bar.TabCount;
                break;
            case "editor.shortcut": EditorControls.Shortcut(Text(args, "name")); break;
            case "editor.cancel": EditorControls.Cancel(); break;
            case "editor.save": Editor.SaveScene(); break;
            case "editor.save_all": Editor.SaveAllScenes(); break;
            case "editor.run": Editor.PlayMainScene(); break;
            case "editor.run_scene": Editor.PlayCurrentScene(); break;
            case "editor.stop": Editor.StopPlayingScene(); break;
            case "editor.scan": Editor.GetResourceFilesystem().Scan(); break;
            case "editor.distraction": Editor.DistractionFreeMode = !Editor.DistractionFreeMode; break;
            default: throw new InvalidOperationException("Unknown voice command: " + method);
        }
        // Coalesce post-command badge updates with the next visual pass.
        _refreshTime = 1;
        return new { ok = true };
    }

    private static object? UiValue(Control control) => control switch
    {
        Godot.Range range => range.Value,
        LineEdit line when !line.Secret => line.Text,
        TextEdit text => text.Text,
        OptionButton option => option.Text,
        BaseButton button when button.ToggleMode => button.ButtonPressed,
        _ => null,
    };

    private void TreeCommand(string method, JsonElement args)
    {
        var target = _hints.Resolve(Text(args, "scope"), Text(args, "letters"));
        if (method == "tree.copy_path") { DisplayServer.ClipboardSet(target.Path); return; }
        // Selecting a row can reveal/expand it through native editor callbacks.
        // Toggle the state the user saw before selection, not that side effect.
        bool wasCollapsed = target.Item?.Collapsed ?? false;
        _hints.Select(target);
        switch (method)
        {
            case "tree.select": return;
            case "tree.rename": EditorControls.Shortcut(target.Scope == "scene" ? "scene_tree/rename" : "filesystem_dock/rename"); return;
            case "tree.delete": EditorControls.Shortcut(target.Scope == "scene" ? "scene_tree/delete" : "filesystem_dock/delete"); return;
            case "tree.duplicate": EditorControls.Shortcut("scene_tree/duplicate"); return;
            case "tree.parent":
                if (target.Item?.GetParent() is TreeItem parent)
                    _hints.Select(target with { Item = parent, Path = parent.GetMetadata(0).ToString(), Text = parent.GetText(0) });
                return;
            case "tree.collapse":
                if (target.Item is not TreeItem collapse) throw new InvalidOperationException("That hint is not a folder/tree row.");
                collapse.Collapsed = true; return;
            case "tree.expand":
                if (target.Item is not TreeItem expand) throw new InvalidOperationException("That hint is not a folder/tree row.");
                Expand(expand, Math.Clamp(Number(args, "depth"), 1, 8)); return;
            case "tree.toggle":
                if (target.Item is TreeItem toggle && toggle.GetFirstChild() is not null) { toggle.Collapsed = !wasCollapsed; return; }
                // A leaf node has nothing to expand: keep it selected in the
                // tree instead of unexpectedly switching to the 3D viewport.
                if (target.Scope == "scene") return;
                break;
        }
        if (target.Control is ItemList list) list.EmitSignal(ItemList.SignalName.ItemActivated, target.Index);
        else if (target.Scope == "files" && target.Control is Tree fileTree) fileTree.EmitSignal(Tree.SignalName.ItemActivated);
        else if (target.Scope == "scene") { Focus("viewport3d"); EditorControls.Shortcut("spatial_editor/focus_selection"); }
    }

    private static void Expand(TreeItem item, int depth)
    {
        item.Collapsed = false;
        if (depth <= 1) return;
        for (var child = item.GetFirstChild(); child is not null; child = child.GetNext()) Expand(child, depth - 1);
    }

    private static void Focus(string pane)
    {
        if (pane == "editor")
        {
            EditorControls.Focus3DViewport();
            return;
        }
        if (pane is "2D" or "3D" or "Script" or "AssetLib" or "Game")
        {
            Editor.SetMainScreenEditor(pane);
            if (pane == "Script" && Editor.GetScriptEditor().GetCurrentEditor() is ScriptEditorBase script)
                EditorControls.Reveal(script.GetBaseEditor());
            return;
        }
        if (pane is "viewport2d" or "viewport3d")
        {
            Editor.SetMainScreenEditor(pane == "viewport2d" ? "2D" : "3D");
            EditorControls.Reveal(pane == "viewport2d" ? Editor.GetEditorViewport2D().GetParent<Control>() : Editor.GetEditorViewport3D().GetParent<Control>());
            return;
        }
        if (pane is "output" or "debugger" or "animation" or "audio" or "shader")
        {
            string text = pane switch { "output" => "Output", "debugger" => "Debugger", "animation" => "Animation", "audio" => "Audio", _ => "Shader Editor" };
            var bottom = EditorControls.Find("EditorBottomPanel") ?? throw new InvalidOperationException("Bottom panels are unavailable.");
            var content = bottom.GetChildren().OfType<Control>().FirstOrDefault(c => c.Name.ToString() == text)
                ?? throw new InvalidOperationException("The " + text + " panel is not available in this editor layout.");
            if (!content.IsVisibleInTree()) EditorControls.Shortcut("bottom_panels/toggle_" + (pane == "shader" ? "shader_editor" : pane) + "_bottom_panel");
            EditorControls.Walk(content).OfType<Control>().FirstOrDefault(c => c.IsVisibleInTree() && c.FocusMode == Control.FocusModeEnum.All)?.GrabFocus();
            return;
        }
        Control? control = pane switch
        {
            "scene" => EditorControls.Find("SceneTreeDock") as Control,
            "files" => Editor.GetFileSystemDock(),
            "inspector" => Editor.GetInspector(),
            "node" or "signals" => EditorControls.Find("SignalsDock") as Control ?? EditorControls.Find("NodeDock") as Control,
            "groups" => EditorControls.Find("GroupsDock") as Control,
            "import" => EditorControls.Find("ImportDock") as Control,
            "history" => EditorControls.Find("HistoryDock") as Control,
            _ => null,
        };
        if (control is null) throw new InvalidOperationException("Pane not found: " + pane);
        EditorControls.Reveal(control);
        var focus = EditorControls.Walk(control).OfType<Control>().FirstOrDefault(c => c.IsVisibleInTree() && c is Tree or ItemList)
            ?? EditorControls.Walk(control).OfType<Control>().FirstOrDefault(c => c.IsVisibleInTree() && c.FocusMode == Control.FocusModeEnum.All);
        focus?.GrabFocus();
    }
}
#endif
