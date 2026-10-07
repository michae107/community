#if TOOLS || DEBUG
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TalonVoice;

internal sealed record UiTarget(string Key, string Text, string Kind, Control Control,
    Rect2 Rect, TreeItem? Item = null, int Index = -1, PopupMenu? Menu = null)
{
    public string Letters { get; set; } = "";
    public Window Window => Control.GetWindow();
}

// Editor chrome or runtime UI roots, never both in the same process.
internal sealed class UiHints
{
    private readonly Dictionary<string, string> _labels = new();
    private bool _inDialog;
    public List<UiTarget> Targets { get; } = new();
    public string Scope { get; set; } = "all";
    public bool Enabled { get; set; } = true;

    public void Reset() { _labels.Clear(); Refresh(); }

    public void Refresh()
    {
        Targets.Clear();
        Window? dialog = UiEnvironment.ActiveDialog();
        _inDialog = dialog is not null;
        if (dialog is not null)
        {
            if (dialog is PopupMenu menu) AddMenu(menu);
            Visit(dialog, dialog);
            return;
        }
        if (!Engine.IsEditorHint())
        {
            foreach (Control control in UiEnvironment.RuntimeUiRoots(UiEnvironment.RuntimeRoot))
                Visit(control, UiEnvironment.RuntimeRoot);
            return;
        }
#if TOOLS
        Node? root = Scope switch
        {
            "inspector" => EditorControls.Find("InspectorDock"),
            "signals" => EditorControls.Find("SignalsDock"),
            "groups" => EditorControls.Find("GroupsDock"),
            _ => EditorInterface.Singleton.GetBaseControl(),
        };
        Window window = EditorInterface.Singleton.GetBaseControl().GetWindow();
        // Dock scopes reduce clutter, but global editor actions must remain
        // reachable without switching hints back to the entire editor.
        if (Scope != "all" && EditorControls.Find("EditorTitleBar") is Node toolbar)
            Visit(toolbar, window);
        if (root is not null) Visit(root, window);
#endif
    }

    private void Visit(Node node, Window window)
    {
        if (node is HintOverlay || node is SubViewport || node is Window other && other != window) return;
        if (node is Control control)
        {
            if (!control.IsVisibleInTree()) return;
            string kind = control.GetClass().ToString();
            // Existing gold/blue/purple badges already cover these collections.
            if (control is MenuBar menus) { AddMenus(menus); return; }
            if (control is Tree tree) { if (!IsExistingTree(tree)) AddTree(tree); return; }
            if (control is ItemList list) { if (!IsExistingTree(list)) AddList(list); return; }
            if (control is TabBar tabs) { if (!IsExistingTree(tabs)) AddTabs(tabs); return; }
            if (control is BaseButton button && !button.Disabled || control is LineEdit { Editable: true }
                || control is TextEdit { Editable: true }
#if TOOLS
                || control is EditorSpinSlider { ReadOnly: false }
#endif
                || control is SpinBox { Editable: true } || control is Slider { Editable: true })
            {
                bool nestedInput = false;
#if TOOLS
                nestedInput = control is EditorSpinSlider && HasVisibleInput(control);
#endif
                if (!ReadOnlyProperty(control) && !nestedInput) AddControl(control, kind, UiEnvironment.Rect(control));
                // SpinBox's internal LineEdit is the same input, not another target.
                if (control is SpinBox) return;
#if TOOLS
                if (control is EditorSpinSlider && !HasVisibleInput(control)) return;
#endif
            }
#if TOOLS
            else if (kind == "EditorInspectorSection")
            {
                AddInspectorSection(control);
            }
            else if (control is EditorProperty property)
            {
                // Property-name badge gives access to its native context menu
                // (reset, copy/paste, keying, resource operations).
                var rect = property.GetGlobalRect();
                rect.Size = new Vector2(rect.Size.X * .42f, Math.Min(rect.Size.Y, 24 * EditorInterface.Singleton.GetEditorScale()));
                if (!property.ReadOnly) AddControl(property, "property", rect);
            }
#endif
        }
        int count = node.GetChildCount(includeInternal: true);
        for (int i = 0; i < count; i++) Visit(node.GetChild(i, includeInternal: true), window);
    }

    private static bool HasVisibleInput(Control control) => control.GetChildren().OfType<LineEdit>().Any(c => c.IsVisibleInTree());
#if TOOLS
    private void AddInspectorSection(Control section)
    {
        float scale = UiEnvironment.Scale;
        var header = new Rect2(Vector2.Zero, new Vector2(section.Size.X, Math.Min(section.Size.Y, 28 * scale)));
        if (!Visible(section, UiEnvironment.LocalRect(section, header))) return;
        string property = SectionCheckProperty(section);
        if (property.Length > 0)
        {
            // Godot draws this switch itself, without a CheckBox child. Its
            // property tooltip identifies checkable sections (e.g. shadow_enabled).
            float margin = Mathf.Round(2 * scale);
            var contents = section.Call("get_vbox").AsGodotObject() as Node;
            var firstProperty = contents is null ? null : UiEnvironment.Walk(contents).OfType<EditorProperty>().FirstOrDefault();
            if (firstProperty is { Keying: true })
                margin += section.GetThemeIcon("GuiAnimationKey", "EditorIcons").GetWidth() + 8 * scale;
            var font = section.GetThemeFont("font", "Label");
            int fontSize = section.GetThemeFontSize("font_size", "Label");
            float width = section.GetThemeIcon("GuiChecked", "EditorIcons").GetWidth()
                + font.GetStringSize(section.Tr("On"), fontSize: fontSize).X + 2 * scale;
            var check = new Rect2(new Vector2(section.IsLayoutRtl() ? margin : section.Size.X - margin - width, 0), new Vector2(width, header.Size.Y));
            Add(new("section-check:" + section.GetInstanceId() + ":" + property, property + " / On", "section-checkbox", section, UiEnvironment.LocalRect(section, check)));
            // Keep the folding badge on the label side, away from the switch.
            header.Size = new Vector2(section.Size.X * .55f, header.Size.Y);
            if (section.IsLayoutRtl()) header.Position = new Vector2(section.Size.X - header.Size.X, 0);
        }
        AddControl(section, "section", UiEnvironment.LocalRect(section, header));
    }

    internal static string SectionCheckProperty(Control section)
    {
        if (section.GetClass().ToString() != "EditorInspectorSection" || !section.HasSignal("section_toggled_by_user")) return "";
        string[] parts = section.TooltipText.Split('|');
        return parts.Length == 3 && parts[0] == "property" ? parts[2] : "";
    }
#endif

    private static bool ReadOnlyProperty(Control control)
    {
#if TOOLS
        for (Node? parent = control; parent is not null; parent = parent.GetParent())
            if (parent is EditorProperty { ReadOnly: true }) return true;
#endif
        return false;
    }

    private bool IsExistingTree(Control control)
    {
        if (_inDialog || !Engine.IsEditorHint()) return false;
        for (Node? parent = control; parent is not null; parent = parent.GetParent())
            if (parent.GetClass().ToString() is "SceneTreeDock" or "FileSystemDock" or "ScriptEditor" or "EditorSceneTabs"
                || control is TabBar && parent.Name.ToString().StartsWith("DockSlot")) return true;
        return false;
    }

    private void AddControl(Control control, string kind, Rect2 rect)
    {
        // Don't build descriptions/keys for the many offscreen Inspector fields.
        if (Visible(control, rect)) Add(new("control:" + control.GetInstanceId(), Describe(control), kind, control, rect));
    }

    internal static string Describe(Control control)
    {
        string text = control switch
        {
#if TOOLS
            EditorProperty property => property.Label,
            EditorSpinSlider spin => spin.Label,
#endif
            Button button => button.Text,
            LineEdit { Secret: true } secret => secret.PlaceholderText,
            LineEdit line => line.PlaceholderText.Length > 0 ? line.PlaceholderText : line.Text,
            _ => control.TooltipText,
        };
#if TOOLS
        for (Node? parent = control.GetParent(); parent is Control; parent = parent.GetParent())
            if (parent is EditorProperty property) return property.Label + (text.Length > 0 ? " / " + text : "");
#endif
        if (text.Length == 0) text = control.TooltipText;
        return text.Length > 0 ? text : control.GetClass().ToString();
    }

    private void AddTree(Tree tree)
    {
        TreeItem? item = UiEnvironment.FirstVisibleRow(tree);
        for (int visited = 0; item is not null && visited < 20000; visited++, item = item.GetNextVisible())
        {
            var rect = tree.GetItemAreaRect(item, 0);
            if (rect.Position.Y >= tree.Size.Y) break;
            rect = UiEnvironment.LocalRect(tree, rect);
            Add(new("row:" + item.GetInstanceId(), item.GetText(0), "row", tree, rect, item));
            for (int i = 0; i < item.GetButtonCount(0); i++)
            {
                if (item.IsButtonDisabled(0, i)) continue;
                var button = tree.GetItemAreaRect(item, 0, i);
                button = UiEnvironment.LocalRect(tree, button);
                Add(new("row-button:" + item.GetInstanceId() + ":" + i, item.GetText(0) + " / button " + (i + 1), "row-button", tree, button, item, i));
            }
        }
    }

    private void AddList(ItemList list)
    {
        for (int i = 0; i < list.ItemCount; i++)
        {
            if (list.IsItemDisabled(i)) continue;
            var rect = UiEnvironment.LocalRect(list, list.GetItemRect(i));
            Add(new("item:" + list.GetInstanceId() + ":" + i, list.GetItemText(i), "item", list, rect, Index: i));
        }
    }

    private void AddTabs(TabBar tabs)
    {
        for (int i = 0; i < tabs.TabCount; i++)
        {
            var rect = tabs.GetTabRect(i);
            if (tabs.IsTabDisabled(i) || tabs.GetTabIdxAtPoint(rect.GetCenter()) != i) continue;
            rect = UiEnvironment.LocalRect(tabs, rect);
            Add(new("tab:" + tabs.GetInstanceId() + ":" + i, tabs.GetTabTitle(i), "tab", tabs, rect, Index: i));
        }
    }

    private void AddMenus(MenuBar menus)
    {
        // OS-native menu bars are outside Godot's overlay canvas.
        if (menus.IsNativeMenu()) return;
        Font font = menus.GetThemeFont("font");
        int fontSize = menus.GetThemeFontSize("font_size");
        Vector2 padding = menus.GetThemeStylebox("normal").GetMinimumSize();
        int offset = 0;
        for (int i = 0; i < menus.GetMenuCount(); i++)
        {
            if (menus.IsMenuHidden(i)) continue;
            string title = menus.Tr(menus.GetMenuTitle(i));
            Vector2 size = font.GetStringSize(title, fontSize: fontSize) + padding;
            float x = menus.IsLayoutRtl() ? menus.Size.X - offset - size.X : offset;
            var rect = UiEnvironment.LocalRect(menus, new Rect2(new Vector2(x, 0), size));
            if (!menus.IsMenuDisabled(i))
                Add(new("menubar:" + menus.GetInstanceId() + ":" + i, title, "menubar", menus, rect, Index: i));
            offset += (int)(size.X + menus.GetThemeConstant("h_separation"));
        }
    }

    private void AddMenu(PopupMenu menu)
    {
        // Godot does not expose filtered row indices. Never display incorrect
        // option badges while a native menu search is rearranging/hiding rows.
        if (UiEnvironment.Walk(menu).OfType<LineEdit>().Any(l => l.IsVisibleInTree() && l.Text.Length > 0)) return;
        // PopupMenu exposes indices, not row rectangles. Reconstruct the native
        // row layout from its item canvas and theme, including scroll clipping.
        var canvas = UiEnvironment.Walk(menu).OfType<Control>().FirstOrDefault(c => c.GetClass().ToString() == "PopupMenuItems")
            ?? UiEnvironment.Walk(menu).OfType<ScrollContainer>().FirstOrDefault()?.GetChildren().OfType<Control>().FirstOrDefault();
        if (canvas is null) return;
        Font font = menu.GetThemeFont("font");
        int size = menu.GetThemeFontSize("font_size");
        Font separatorFont = menu.GetThemeFont("font_separator");
        int separatorSize = menu.GetThemeFontSize("font_separator_size");
        int separatorHeight = (int)Math.Max(menu.GetThemeStylebox("separator").GetMinimumSize().Y,
            Math.Max(menu.GetThemeStylebox("labeled_separator_left").GetMinimumSize().Y,
                menu.GetThemeStylebox("labeled_separator_right").GetMinimumSize().Y));
        int checkHeight = Math.Max(menu.GetThemeIcon("checked").GetHeight(), menu.GetThemeIcon("radio_checked").GetHeight());
        int themeIconWidth = menu.GetThemeConstant("icon_max_width");
        int gap = menu.GetThemeConstant("v_separation");
        int y = 0;
        for (int i = 0; i < menu.ItemCount; i++)
        {
            if (menu.HasMethod("is_item_hidden") && menu.Call("is_item_hidden", i).AsBool()) continue;
            bool separator = menu.IsItemSeparator(i);
            string text = menu.GetItemText(i);
            Font itemFont = separator ? separatorFont : font;
            int itemSize = separator ? separatorSize : size;
            // PopupMenu::_get_item_height uses its shaped TextLine height,
            // truncated to an integer, not Font.GetHeight(). Those differ even
            // in the default editor font; using the latter drifts by whole rows.
            int height = text.Length == 0 ? 0 : (int)itemFont.GetStringSize(menu.Tr(text), fontSize: itemSize).Y;
            if (height == 0 && !separator) height = (int)font.GetHeight(size);
            if (menu.GetItemIcon(i) is Texture2D icon)
            {
                int maxWidth = menu.GetItemIconMaxWidth(i);
                if (themeIconWidth > 0 && (maxWidth <= 0 || themeIconWidth < maxWidth)) maxWidth = themeIconWidth;
                float iconHeight = maxWidth > 0 && icon.GetWidth() > maxWidth ? icon.GetHeight() * (float)maxWidth / icon.GetWidth() : icon.GetHeight();
                height = Math.Max(height, (int)iconHeight);
            }
            if (!separator && (menu.IsItemCheckable(i) || menu.IsItemRadioCheckable(i))) height = Math.Max(height, checkHeight);
            if (separator) height = Math.Max(height, separatorHeight);
            var rect = UiEnvironment.LocalRect(canvas, new Rect2(new Vector2(0, y), new Vector2(canvas.Size.X, height + gap)));
            if (!separator && !menu.IsItemDisabled(i))
                // A rebuilt menu must not reuse a visible letter for different
                // content just because it occupies the same array index.
                Add(new("menu:" + menu.GetInstanceId() + ":" + i + ":" + menu.GetItemId(i) + ":" + text,
                    text, "menu", canvas, rect, Index: i, Menu: menu));
            y += height + gap;
        }
    }

    internal static bool Visible(Control control, Rect2 rect)
    {
        if (!GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree() || rect.Size.X <= 2 || rect.Size.Y <= 2) return false;
        if (!control.GetViewport().GetVisibleRect().HasPoint(rect.GetCenter())) return false;
        if (!UiEnvironment.Rect(control).HasPoint(rect.GetCenter())) return false;
        for (Node? node = control.GetParent(); node is Control parent; node = node.GetParent())
            if (parent.ClipContents && !UiEnvironment.Rect(parent).HasPoint(rect.GetCenter())) return false;
        return true;
    }

    private void Add(UiTarget target)
    {
        if (!Visible(target.Control, target.Rect)) return;
        if (!_labels.TryGetValue(target.Key, out string? letters))
        {
            int number = _labels.Count; letters = "";
            do { letters = (char)('a' + number % 26) + letters; number = number / 26 - 1; } while (number >= 0);
            _labels[target.Key] = letters;
        }
        target.Letters = letters;
        Targets.Add(target);
    }

    public UiTarget Resolve(string letters)
    {
        Refresh();
        return Targets.FirstOrDefault(t => t.Letters == letters.ToLowerInvariant())
            ?? throw new InvalidOperationException("No visible UI hint '" + letters + "'. Use the green labels; 'hints refresh' reassigns them.");
    }
}
#endif
