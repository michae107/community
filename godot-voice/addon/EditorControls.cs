#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TalonVoice;

internal static class EditorControls
{
    private static readonly Dictionary<string, Node> _classes = new();
    private static readonly List<Window> _windows = new();
    private static readonly List<TabBar> _dockTabs = new();
    private static SceneTree? _tree;
    private static bool _indexDirty = true;

    public static void Initialize(SceneTree tree)
    {
        Shutdown();
        _tree = tree;
        tree.TreeChanged += InvalidateIndex;
    }

    public static void Shutdown()
    {
        if (GodotObject.IsInstanceValid(_tree)) _tree!.TreeChanged -= InvalidateIndex;
        _tree = null;
        _classes.Clear(); _windows.Clear(); _dockTabs.Clear();
        _indexDirty = true;
    }

    private static void InvalidateIndex() => _indexDirty = true;

    private static void EnsureIndex()
    {
        if (!_indexDirty) return;
        _classes.Clear(); _windows.Clear(); _dockTabs.Clear();
        foreach (Node node in Walk(EditorInterface.Singleton.GetBaseControl()))
        {
            _classes.TryAdd(node.GetClass().ToString(), node);
            if (node is Window window) _windows.Add(window);
            if (node is TabBar tabs && tabs.GetPath().ToString().Contains("DockSlot")) _dockTabs.Add(tabs);
        }
        _indexDirty = false;
    }

    public static IReadOnlyList<Window> Windows { get { EnsureIndex(); return _windows; } }
    public static IReadOnlyList<TabBar> DockTabs { get { EnsureIndex(); return _dockTabs; } }

    public static IEnumerable<Node> Walk(Node node, bool visibleOnly = false)
    {
        // Edited scenes and preview worlds aren't editor chrome. Also avoid
        // allocating a Godot Array for every node visited across the interop boundary.
        if (node is SubViewport or HintOverlay) yield break;
        if (visibleOnly && (node is Control control && !control.IsVisibleInTree() || node is Window { Visible: false })) yield break;
        yield return node;
        int count = node.GetChildCount(includeInternal: true);
        for (int i = 0; i < count; i++)
            foreach (Node descendant in Walk(node.GetChild(i, includeInternal: true), visibleOnly)) yield return descendant;
    }

    public static Node? Find(string nativeClass)
    {
        EnsureIndex();
        return _classes.GetValueOrDefault(nativeClass);
    }

    public static Tree? SceneTree => Find("SceneTreeDock") is Node dock
        ? Walk(dock).OfType<Tree>().FirstOrDefault(t => t.IsVisibleInTree()) : null;

    public static TabBar? SceneTabs => Find("EditorSceneTabs") is Node tabs
        ? Walk(tabs).OfType<TabBar>().FirstOrDefault() : null;

    public static TreeItem? FirstVisibleRow(Tree tree)
    {
        TreeItem? root = tree.GetRoot();
        // Start inside the visible viewport, then backtrack only its visible
        // rows. Avoid walking thousands of rows above a scrolled filesystem.
        TreeItem? item = tree.GetItemAtPosition(tree.Size / 2);
        if (item is null) return tree.HideRoot ? root?.GetFirstChild() : root;
        while (item.GetPrevVisible() is TreeItem previous)
        {
            if (tree.HideRoot && previous == root || tree.GetItemAreaRect(previous, 0).End.Y <= 0) break;
            item = previous;
        }
        return item;
    }

    public static void Reveal(Control control)
    {
        var dock = WalkParents(control).OfType<EditorDock>().FirstOrDefault();
        dock?.MakeVisible();
        Node child = control;
        for (Node? parent = control.GetParent(); parent is not null; parent = parent.GetParent())
        {
            if (parent is TabContainer tabs && child is Control tab)
            {
                int index = tabs.GetTabIdxFromControl(tab);
                if (index >= 0) tabs.CurrentTab = index;
            }
            child = parent;
        }
        if (control.FocusMode != Control.FocusModeEnum.None) control.GrabFocus();
    }

    private static IEnumerable<Node> WalkParents(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent()) yield return current;
    }

    public static void Focus3DViewport()
    {
        var editor = EditorInterface.Singleton;
        editor.SetMainScreenEditor("3D");
        Control? surface = null;
        Control? focused = editor.GetBaseControl().GetViewport().GuiGetFocusOwner();
        for (int i = 0; i < 4; i++)
        {
            var container = editor.GetEditorViewport3D(i).GetParent<Control>();
            if (!container.IsVisibleInTree()) continue;
            // Godot's rendered SubViewport has input disabled. The sibling
            // full-size Control is the editor's keyboard/mouse input surface.
            var candidate = container.GetParent().GetChildren().OfType<Control>()
                .FirstOrDefault(c => c != container && c.IsVisibleInTree() && c.FocusMode == Control.FocusModeEnum.All);
            if (candidate is null) continue;
            surface ??= candidate;
            if (candidate == focused || focused is not null && candidate.IsAncestorOf(focused))
            {
                surface = candidate;
                break;
            }
        }
        if (surface is null) throw new InvalidOperationException("No visible 3D editor viewport is available.");
        surface.GetWindow().GrabFocus();
        surface.GrabFocus();
        Viewport viewport = surface.GetViewport();
        Vector2 center = surface.GetGlobalRect().GetCenter();
        viewport.WarpMouse(center);
        viewport.Notification((int)Viewport.NotificationVpMouseEnter);
        viewport.PushInput(new InputEventMouseMotion { Position = center, GlobalPosition = center }, inLocalCoords: true);
    }

    public static void Key(Key key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        Viewport viewport = ActiveDialog() ?? EditorInterface.Singleton.GetBaseControl().GetViewport();
        var press = new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt };
        viewport.PushInput(press);
        var release = (InputEventKey)press.Duplicate();
        release.Pressed = false;
        viewport.PushInput(release);
    }

    public static Window? ActiveDialog()
    {
        EnsureIndex();
        // Visibility can change without a tree change, so check it live. The
        // structural scan itself runs only when nodes are added/removed/reordered.
        for (int i = _windows.Count - 1; i >= 0; i--)
            if (GodotObject.IsInstanceValid(_windows[i]) && _windows[i].IsInsideTree()
                && _windows[i].Visible && (!_windows[i].Unfocusable || _windows[i] is PopupMenu)) return _windows[i];
        return null;
    }

    public static void Cancel()
    {
        if (ActiveDialog() is Window dialog)
        {
            if (dialog.HasMethod("get_cancel_button") && dialog.Call("get_cancel_button").AsGodotObject() is BaseButton cancel)
                cancel.EmitSignal(BaseButton.SignalName.Pressed);
            else dialog.EmitSignal(Window.SignalName.CloseRequested);
            if (dialog.Visible) dialog.Hide();
        }
        else Key(Godot.Key.Escape);
    }

    public static void Shortcut(string name)
    {
        var shortcut = EditorInterface.Singleton.GetEditorSettings().GetShortcut(name);
        if (shortcut is null) throw new InvalidOperationException("Unknown editor shortcut: " + name);
        // This is the same event used by Godot's command palette. It also works
        // for native actions that have no keyboard binding.
        EditorInterface.Singleton.GetBaseControl().GetViewport().PushInput(new InputEventShortcut { Shortcut = shortcut });
    }
}
#endif
