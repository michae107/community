#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TalonVoice;

internal sealed record HintTarget(string Scope, string Key, string Text, string Path, Control Control,
    Rect2 Rect, TreeItem? Item = null, int Index = -1)
{
    public string Letters { get; set; } = "";
}

internal sealed class NativeHints
{
    private readonly Dictionary<string, Dictionary<string, string>> _labels = new();
    public List<HintTarget> Targets { get; } = new();
    public bool Enabled { get; set; } = true;

    public void Reset() { _labels.Clear(); Refresh(); }

    public void Refresh()
    {
        Targets.Clear();
        var scene = EditorControls.SceneTree;
        if (scene is not null) AddTree("scene", scene);
        var files = EditorInterface.Singleton.GetFileSystemDock();
        foreach (Node node in EditorControls.Walk(files, visibleOnly: true))
        {
            if (node is Tree tree) AddTree("files", tree);
            else if (node is ItemList list) AddList("files", list);
        }
        foreach (ItemList list in EditorControls.Walk(EditorInterface.Singleton.GetScriptEditor(), visibleOnly: true).OfType<ItemList>())
            AddList("scripts", list);
        var tabs = EditorControls.SceneTabs;
        if (tabs is not null) AddTabs("tabs", tabs);
        foreach (TabBar dockTabs in EditorControls.DockTabs)
            AddTabs("docks", dockTabs);
    }

    private void AddList(string scope, ItemList list)
    {
        for (int i = 0; i < list.ItemCount; i++)
        {
            var rect = list.GetItemRect(i);
            rect.Position += list.GlobalPosition;
            if (!Visible(list, rect)) continue;
            string path = list.GetItemMetadata(i).ToString();
            Add(new(scope, "list:" + list.GetInstanceId() + ":" + path, list.GetItemText(i), path, list, rect, Index: i));
        }
    }

    private void AddTabs(string scope, TabBar tabs)
    {
        if (tabs.IsVisibleInTree())
        {
            for (int i = 0; i < tabs.TabCount; i++)
            {
                var rect = tabs.GetTabRect(i);
                // Scrolled-out tabs retain rectangles; hit-testing excludes those stale positions.
                if (tabs.GetTabIdxAtPoint(rect.GetCenter()) != i) continue;
                rect.Position += tabs.GlobalPosition;
                if (Visible(tabs, rect)) Add(new(scope, "tab:" + tabs.GetInstanceId() + ":" + i, tabs.GetTabTitle(i), "", tabs, rect, Index: i));
            }
        }
    }

    private void AddTree(string scope, Tree tree)
    {
        if (!tree.IsVisibleInTree()) return;
        var item = EditorControls.FirstVisibleRow(tree);
        int visited = 0;
        while (item is not null && visited++ < 20000)
        {
            var rect = tree.GetItemAreaRect(item, 0);
            // Rows are ordered vertically; everything after this is offscreen.
            if (rect.Position.Y >= tree.Size.Y) break;
            if (item.GetButtonCount(0) > 0)
            {
                var button = tree.GetItemAreaRect(item, 0, 0);
                if (button.Position.X > rect.Position.X) rect.Size = new Vector2(button.Position.X - rect.Position.X, rect.Size.Y);
            }
            rect.Position += tree.GlobalPosition;
            if (Visible(tree, rect) && item.IsSelectable(0))
            {
                string path = item.GetMetadata(0).ToString();
                string key = "tree:" + tree.GetInstanceId() + ":" + (path.Length > 0 ? path : item.GetInstanceId().ToString());
                Add(new(scope, key, item.GetText(0), path, tree, rect, item));
            }
            item = item.GetNextVisible();
        }
    }

    private static bool Visible(Control control, Rect2 rect)
    {
        if (rect.Size.X <= 0 || rect.Size.Y <= 0 || !control.GetGlobalRect().HasPoint(rect.GetCenter())) return false;
        for (Node? node = control.GetParent(); node is Control parent; node = node.GetParent())
            if (parent.ClipContents && !parent.GetGlobalRect().HasPoint(rect.GetCenter())) return false;
        return true;
    }

    private void Add(HintTarget target)
    {
        if (!_labels.TryGetValue(target.Scope, out var labels)) _labels[target.Scope] = labels = new();
        if (!labels.TryGetValue(target.Key, out string? label)) labels[target.Key] = label = Letters(labels.Count);
        target.Letters = label;
        Targets.Add(target);
    }

    private static string Letters(int number)
    {
        string value = "";
        do { value = (char)('a' + number % 26) + value; number = number / 26 - 1; } while (number >= 0);
        return value;
    }

    public HintTarget Resolve(string scope, string letters)
    {
        Refresh();
        var target = Targets.FirstOrDefault(t => t.Scope == scope && t.Letters == letters.ToLowerInvariant());
        return target ?? throw new InvalidOperationException($"No visible {scope} hint '{letters}'. Show the dock or say 'godot marks reset'.");
    }

    public void Select(HintTarget target)
    {
        EditorControls.Reveal(target.Control);
        if (target.Item is TreeItem item && target.Control is Tree tree)
        {
            tree.DeselectAll();
            item.Select(0);
            tree.ScrollToItem(item);
            if (target.Scope == "scene")
            {
                var root = EditorInterface.Singleton.GetEditedSceneRoot();
                var node = root?.GetNodeOrNull(new NodePath(target.Path));
                if (node is not null)
                {
                    var selection = EditorInterface.Singleton.GetSelection();
                    selection.Clear();
                    selection.AddNode(node);
                    EditorInterface.Singleton.EditNode(node);
                }
                else tree.EmitSignal(Tree.SignalName.ItemSelected);
            }
            else tree.EmitSignal(Tree.SignalName.ItemSelected);
        }
        else if (target.Control is ItemList list)
        {
            list.DeselectAll();
            list.Select(target.Index);
            list.EnsureCurrentIsVisible();
            list.EmitSignal(ItemList.SignalName.ItemSelected, target.Index);
        }
        else if (target.Control is TabBar tabs) tabs.CurrentTab = target.Index;
    }
}
#endif
