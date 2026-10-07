using Godot;
using System;
#if TOOLS || DEBUG
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using File = System.IO.File;
using Environment = System.Environment;
#endif

namespace TalonVoice;

// Automatically registered by the editor plugin. Exported games stay inert.
public partial class VoiceRuntime : Node
{
    public IDisposable? RegisterCommand(Node owner, string name, string phrase, VoiceArgument argument,
        Func<string, object?> handler)
    {
#if TOOLS || DEBUG
        return _endpoint.Length == 0 ? null : _commands.Register(owner, name, phrase, argument, handler);
#else
        return null;
#endif
    }

    public override void _Ready()
    {
#if TOOLS || DEBUG
        if (!Engine.IsEditorHint() && OS.IsDebugBuild() && OS.HasFeature("editor")
            && !OS.GetCmdlineUserArgs().Contains("--no-voice-hints"))
        {
            ProcessMode = ProcessModeEnum.Always;
            UiEnvironment.InitializeRuntime(GetTree());
            _endpoint = Path.Combine(Path.GetTempPath(), "talon-godot-" + Environment.UserName, Environment.ProcessId.ToString());
            Directory.CreateDirectory(_endpoint);
            if (!OperatingSystem.IsWindows())
            {
                var permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                File.SetUnixFileMode(Path.GetDirectoryName(_endpoint)!, permissions);
                File.SetUnixFileMode(_endpoint, permissions);
            }
            WriteState();
            SetProcess(true);
            SetProcessInput(true);
            return;
        }
#endif
        SetProcess(false);
        SetProcessInput(false);
    }

#if TOOLS || DEBUG
    private readonly UiHints _hints = new();
    private readonly VoiceCommands _commands = new();
    private readonly Dictionary<ulong, (CanvasLayer Layer, HintOverlay Overlay)> _overlays = new();
    private string _endpoint = "";
    private string _error = "";
    private double _pollTime, _refreshTime, _stateTime, _cleanupTime;
    private (string Id, UiPointerAction Action, ulong Frame, long Created)? _pending;

    public override void _Input(InputEvent input)
    {
        if (_endpoint.Length == 0 || UiEnvironment.ActiveDialog() is not null) return;
        if (input is InputEventKey key && UiActions.NavigateGameScroll(key, GetViewport()))
        {
            GetViewport().SetInputAsHandled();
            _refreshTime = 1;
        }
    }

    public override void _ExitTree()
    {
        _commands.Dispose();
        if (_endpoint.Length == 0) return;
        if (_pending is { } pending) Respond(pending.Id, null, "Game closed before the click.");
        _pending = null;
        File.Delete(Path.Combine(_endpoint, "state.json"));
        foreach (var pair in _overlays.Values)
            if (GodotObject.IsInstanceValid(pair.Layer)) pair.Layer.Free();
        _overlays.Clear();
        UiEnvironment.ShutdownRuntime();
    }

    public override void _Process(double delta)
    {
        if (_endpoint.Length == 0) return;
        _pollTime += delta; _refreshTime += delta; _stateTime += delta; _cleanupTime += delta;
        try
        {
            if (_pending is { } pending && Engine.GetProcessFrames() > pending.Frame + 2)
            {
                _pending = null;
                string? error = null;
                try
                {
                    if (Now - pending.Created > 2500) throw new InvalidOperationException("Voice click expired.");
                    var live = _hints.Resolve(pending.Action.Target.Letters);
                    if (live.Key != pending.Action.Target.Key || live.Rect != pending.Action.Target.Rect)
                        throw new InvalidOperationException("The control moved before the click; say it again.");
                    pending.Action.Commit();
                }
                catch (Exception e) { error = e.Message; }
                Respond(pending.Id, new { ok = error is null }, error);
            }
            if (_pollTime >= .05) { _pollTime = 0; Poll(); }
            bool focused = (UiEnvironment.ActiveDialog() ?? GetWindow()).HasFocus();
            if (_refreshTime >= (focused ? .25 : 1))
            {
                _refreshTime = 0;
                if (_hints.Enabled) _hints.Refresh();
                else _hints.Targets.Clear();
                UpdateOverlays();
            }
            if (_stateTime >= 1) { _stateTime = 0; WriteState(); }
            if (_cleanupTime >= 30)
            {
                _cleanupTime = 0;
                foreach (string path in Directory.EnumerateFiles(_endpoint, "*.response.json"))
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-1)) File.Delete(path);
            }
        }
        catch (Exception e) { _error = e.Message; }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void WriteState() => VoiceJson.Write(Path.Combine(_endpoint, "state.json"), new
    {
        version = 1, kind = "game", pid = Environment.ProcessId,
        project = ProjectSettings.GlobalizePath("res://").TrimEnd('/'),
        focused = (UiEnvironment.ActiveDialog() ?? GetWindow()).HasFocus(), updated = Now,
        hints = _hints.Enabled, ui_hints = _hints.Enabled, ui_scope = "all", error = _error,
        commands = _commands.Describe(GetTree().CurrentScene),
    });

    private void Respond(string id, object? result, string? error)
    {
        _error = error ?? "";
        VoiceJson.Write(Path.Combine(_endpoint, id + ".response.json"), new { id, result, error });
        _refreshTime = 1;
    }

    private void Poll()
    {
        if (_pending is not null) return;
        foreach (string request in Directory.EnumerateFiles(_endpoint, "*.request.json").Take(8))
        {
            string id = Path.GetFileName(request).Replace(".request.json", "");
            if (id.Length != 32 || !id.All(Uri.IsHexDigit)) continue;
            try
            {
                if (new FileInfo(request).Length > 128 * 1024) throw new InvalidOperationException("Voice request is too large.");
                using var json = JsonDocument.Parse(File.ReadAllText(request));
                var root = json.RootElement;
                if (root.GetProperty("id").GetString() != id) throw new InvalidOperationException("Request ID mismatch.");
                long created = root.GetProperty("created").GetInt64();
                if (Now - created > 2500 || Now - created < -5000) throw new InvalidOperationException("Voice request expired.");
                object? result = Execute(root.GetProperty("method").GetString()!, root.GetProperty("args"));
                if (result is UiPointerAction action)
                    _pending = (id, action, Engine.GetProcessFrames(), created);
                else Respond(id, result, null);
            }
            catch (Exception e) { Respond(id, null, e.Message); }
            finally { File.Delete(request); }
            if (_pending is not null) break;
        }
    }

    private object? Execute(string method, JsonElement args)
    {
        string Text(string key, string fallback = "") => args.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
        int Number(string key, int fallback = 1) => args.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
        switch (method)
        {
            case "game.command":
                if (UiEnvironment.ActiveDialog() is not null)
                    throw new InvalidOperationException("Close the dialog before using scene voice commands.");
                return _commands.Invoke(GetTree().CurrentScene, Text("name"), Text("argument"));
            case "status":
                if (_hints.Enabled) _hints.Refresh();
                return new { kind = "game", scene = GetTree().CurrentScene?.SceneFilePath, error = _error,
                    commands = _commands.Describe(GetTree().CurrentScene),
                    hints = _hints.Targets.Select(t => new { letters = t.Letters, text = t.Text, kind = t.Kind, path = t.Control.GetPath().ToString() }).ToArray() };
            case "ui.hints.show": _hints.Enabled = true; break;
            case "ui.hints.hide": _hints.Enabled = false; _hints.Targets.Clear(); break;
            case "ui.hints.toggle": _hints.Enabled = !_hints.Enabled; break;
            case "ui.hints.reset": _hints.Reset(); break;
            case "ui.hints.scope":
                if (Text("scope") != "all") throw new InvalidOperationException("Game hints only support 'hints all'.");
                _hints.Enabled = true; break;
            default:
                if (!method.StartsWith("ui.")) throw new InvalidOperationException("Editor commands cannot run in a game.");
                if (!_hints.Enabled) throw new InvalidOperationException("Say 'hints on' first.");
                var target = _hints.Resolve(Text("letters"));
                switch (method)
                {
                    case "ui.click": return (object?)UiActions.Click(target) ?? new { ok = true };
                    case "ui.double_click": return (object?)UiActions.Click(target, twice: true) ?? new { ok = true };
                    case "ui.menu": return (object?)UiActions.Click(target, MouseButton.Right) ?? new { ok = true };
                    case "ui.focus": return (object?)UiActions.Focus(target) ?? new { ok = true };
                    case "ui.change":
                        if (target.Control is LineEdit or TextEdit) return UiActions.SetText(target, "", false);
                        return (object?)UiActions.Focus(target, true) ?? new { ok = true };
                    case "ui.hover": UiActions.Hover(target); break;
                    case "ui.text": return UiActions.SetText(target, Text("text"), args.TryGetProperty("submit", out var submit) && submit.GetBoolean());
                    case "ui.number": return UiActions.SetNumber(target, args.GetProperty("value").GetDouble());
                    case "ui.step": return UiActions.Step(target, Number("direction"), Number("count"));
                    case "ui.scroll": UiActions.Scroll(target, Number("direction"), Number("count")); break;
                    default: throw new InvalidOperationException("Unknown game voice command: " + method);
                }
                break;
        }
        _refreshTime = 1;
        return new { ok = true };
    }

    private void UpdateOverlays()
    {
        Window main = GetWindow();
        Window? popup = _hints.Enabled ? UiEnvironment.ActiveDialog() : null;
        foreach (var pair in _overlays.ToArray())
            if (!GodotObject.IsInstanceValid(pair.Value.Layer)
                || pair.Key != main.GetInstanceId() && pair.Key != popup?.GetInstanceId())
            {
                if (GodotObject.IsInstanceValid(pair.Value.Layer)) pair.Value.Layer.Free();
                _overlays.Remove(pair.Key);
            }
        Update(main);
        if (popup is not null) Update(popup);

        void Update(Window window)
        {
            ulong id = window.GetInstanceId();
            if (!_overlays.TryGetValue(id, out var pair))
            {
                var layer = new CanvasLayer { Name = "TalonVoiceOverlay", Layer = 1024, ProcessMode = ProcessModeEnum.Always };
                var overlay = new HintOverlay { Name = "Letters", UiHints = _hints, MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None };
                window.AddChild(layer);
                layer.AddChild(overlay);
                pair = (layer, overlay);
                _overlays[id] = pair;
            }
            pair.Overlay.Size = window.GetVisibleRect().Size;
            pair.Overlay.UpdateVisuals(showNative: false);
        }
    }
#endif
}
