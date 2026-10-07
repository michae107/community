#if TOOLS || DEBUG
using Godot;
using System;
using System.Collections.Generic;

namespace TalonVoice;

// Shared UI operations, with separate editor and game roots.
internal static class UiEnvironment
{
    private static SceneTree? _runtimeTree;
    private static bool _dirty = true;
    private static readonly List<Window> _windows = new();
    private static readonly List<Control> _roots = new();
    public static Window RuntimeRoot => _runtimeTree?.Root ?? throw new InvalidOperationException("Game hints are not running.");
    public static float Scale
    {
        get
        {
#if TOOLS
            if (Engine.IsEditorHint()) return EditorInterface.Singleton.GetEditorScale();
#endif
            return 1;
        }
    }

    public static void InitializeRuntime(SceneTree tree)
    {
        _runtimeTree = tree;
        tree.TreeChanged += Invalidate;
        _dirty = true;
    }

    public static void ShutdownRuntime()
    {
        if (GodotObject.IsInstanceValid(_runtimeTree)) _runtimeTree!.TreeChanged -= Invalidate;
        _runtimeTree = null;
        _windows.Clear(); _roots.Clear();
        _dirty = true;
    }

    private static void Invalidate() => _dirty = true;

    private static void IndexRuntime()
    {
        if (!_dirty) return;
        _windows.Clear(); _roots.Clear();
        Index(RuntimeRoot, false);
        _dirty = false;
    }

    private static void Index(Node node, bool insideControl)
    {
        if (node is SubViewport or HintOverlay) return;
        if (node is Window window) { _windows.Add(window); insideControl = false; }
        if (node is Control control && !insideControl) _roots.Add(control);
        int count = node.GetChildCount(includeInternal: true);
        for (int i = 0; i < count; i++) Index(node.GetChild(i, includeInternal: true), insideControl || node is Control);
    }

    public static IEnumerable<Control> RuntimeUiRoots(Window window)
    {
        IndexRuntime();
        foreach (var root in _roots)
            if (GodotObject.IsInstanceValid(root) && root.GetWindow() == window) yield return root;
    }

    public static Window? ActiveDialog()
    {
#if TOOLS
        if (Engine.IsEditorHint()) return EditorControls.ActiveDialog();
#endif
        IndexRuntime();
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window window = _windows[i];
            if (window != RuntimeRoot && GodotObject.IsInstanceValid(window) && window.Visible
                && (!window.Unfocusable || window is PopupMenu)) return window;
        }
        return null;
    }

    public static IEnumerable<Node> Walk(Node node)
    {
        if (node is SubViewport or HintOverlay) yield break;
        yield return node;
        int count = node.GetChildCount(includeInternal: true);
        for (int i = 0; i < count; i++)
            foreach (Node child in Walk(node.GetChild(i, includeInternal: true))) yield return child;
    }

    public static TreeItem? FirstVisibleRow(Tree tree)
    {
        TreeItem? root = tree.GetRoot();
        TreeItem? item = tree.GetItemAtPosition(tree.Size / 2);
        if (item is null) return tree.HideRoot ? root?.GetFirstChild() : root;
        while (item.GetPrevVisible() is TreeItem previous)
        {
            if (tree.HideRoot && previous == root || tree.GetItemAreaRect(previous, 0).End.Y <= 0) break;
            item = previous;
        }
        return item;
    }

    public static Rect2 Rect(Control control) => LocalRect(control, new Rect2(Vector2.Zero, control.Size));
    public static Rect2 LocalRect(Control control, Rect2 rect) => control.GetGlobalTransformWithCanvas() * rect;
}
#endif
