#if TOOLS
using Godot;
using System;
using System.Globalization;
using System.Linq;

namespace TalonVoice;

internal sealed record UiPointerAction(UiTarget Target, Vector2 Position, MouseButton Button, bool Twice)
{
    public void Commit()
    {
        UiActions.Guard(Target);
        // Do not activate a newly moved control or a stale hover target.
        if (!Target.Control.GetGlobalRect().HasPoint(Position)) throw new InvalidOperationException("The control moved; say the command again.");
        Viewport viewport = Target.Control.GetViewport();
        var hovered = viewport.GuiGetHoveredControl();
        if (hovered != Target.Control && !(hovered is not null && Target.Control.IsAncestorOf(hovered)))
            throw new InvalidOperationException("Godot did not focus the pointer on that control; focus the editor and try again.");
        viewport.PushInput(new InputEventMouseButton { Position = Position, GlobalPosition = Position, ButtonIndex = Button, ButtonMask = Button == MouseButton.Left ? MouseButtonMask.Left : MouseButtonMask.Right, Pressed = true, DoubleClick = Twice }, inLocalCoords: true);
        viewport.PushInput(new InputEventMouseButton { Position = Position, GlobalPosition = Position, ButtonIndex = Button, Pressed = false }, inLocalCoords: true);
    }
}

internal static class UiActions
{
    public static UiPointerAction? Focus(UiTarget target, bool selectAll = false)
    {
        Guard(target);
        if (target.Menu is PopupMenu menu) { menu.SetFocusedItem(target.Index); return null; }
        target.Window.GrabFocus();
        Control control = target.Control;
        if (control is EditorSpinSlider)
        {
            // Opens the editor's own numeric input; its native focus is deferred.
            return Click(target);
        }
        if (control is SpinBox spin) control = spin.GetLineEdit();
        if (control.FocusMode == Control.FocusModeEnum.None)
            throw new InvalidOperationException("This control has no keyboard focus. Say click or menu instead.");
        if (control is EditorProperty property) property.Select();
        control.GrabFocus();
        Hover(target);
        if (selectAll && control is LineEdit line) line.SelectAll();
        if (selectAll && control is TextEdit text) text.SelectAll();
        return null;
    }

    public static UiPointerAction? Click(UiTarget target, MouseButton button = MouseButton.Left, bool twice = false)
    {
        Guard(target);
        if (target.Menu is PopupMenu menu)
        {
            if (button != MouseButton.Left) throw new InvalidOperationException("Menu items only support activation.");
            menu.SetFocusedItem(target.Index);
            if (menu.GetItemSubmenuNode(target.Index) is PopupMenu submenu)
            {
                submenu.Position = menu.Position + new Vector2I(menu.Size.X, (int)target.Rect.Position.Y);
                submenu.Popup();
                return null;
            }
            int id = menu.GetItemId(target.Index);
            bool checkable = menu.IsItemCheckable(target.Index);
            bool multistate = menu.GetItemMultistateMax(target.Index) > 0;
            // Use the same public signals as native activation. Menu windows
            // may refuse injected input when their native OS focus is elsewhere.
            for (Node? parent = menu; parent is PopupMenu popup; parent = popup.GetParent())
                if (checkable ? popup.HideOnCheckableItemSelection : multistate ? popup.HideOnStateItemSelection : popup.HideOnItemSelection)
                    popup.Hide();
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, id);
            if (GodotObject.IsInstanceValid(menu)) menu.EmitSignal(PopupMenu.SignalName.IndexPressed, target.Index);
            return null;
        }
        if (target.Kind == "row" && target.Control is Tree tree && target.Item is TreeItem item && button == MouseButton.Left)
        {
            tree.GrabFocus();
            if (!item.IsSelectable(0) && item.GetFirstChild() is not null) { item.Collapsed = !item.Collapsed; return null; }
            tree.DeselectAll(); item.Select(0); tree.ScrollToItem(item);
            tree.EmitSignal(Tree.SignalName.ItemSelected);
            if (twice) tree.EmitSignal(Tree.SignalName.ItemActivated);
            return null;
        }
        if (target.Kind == "item" && target.Control is ItemList list && button == MouseButton.Left)
        {
            list.GrabFocus(); list.DeselectAll(); list.Select(target.Index);
            list.EmitSignal(ItemList.SignalName.ItemSelected, target.Index);
            if (twice) list.EmitSignal(ItemList.SignalName.ItemActivated, target.Index);
            return null;
        }
        if (target.Kind == "tab" && target.Control is TabBar tabs && button == MouseButton.Left) { tabs.CurrentTab = target.Index; return null; }
        Vector2 position = target.Rect.GetCenter();
        // Clicking the left side of a header also works for narrow collapsed sections.
        if (target.Kind == "section") position.X = target.Rect.Position.X + Math.Min(18, target.Rect.Size.X / 2);
        Viewport viewport = target.Control.GetViewport();
        target.Window.GrabFocus();
        viewport.WarpMouse(position);
        viewport.Notification((int)Viewport.NotificationVpMouseEnter);
        viewport.PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position, Relative = Vector2.One }, inLocalCoords: true);
        return new UiPointerAction(target, position, button, twice);
    }

    public static void Hover(UiTarget target)
    {
        Guard(target);
        target.Control.GetViewport().WarpMouse(target.Rect.GetCenter());
        target.Control.GetViewport().Notification((int)Viewport.NotificationVpMouseEnter);
        target.Control.GetViewport().PushInput(new InputEventMouseMotion { Position = target.Rect.GetCenter(), GlobalPosition = target.Rect.GetCenter(), Relative = Vector2.One }, inLocalCoords: true);
    }

    public static object SetText(UiTarget target, string value, bool submit)
    {
        Guard(target);
        if (target.Control is EditorSpinSlider or SpinBox or Slider)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                throw new InvalidOperationException("Use a numeric value for this field (for example 'set air to minus two point five').");
            return SetNumber(target, number);
        }
        Focus(target);
        if (target.Control is LineEdit line)
        {
            line.SelectAll();
            line.DeleteText(0, line.Text.Length);
            line.InsertTextAtCaret(value);
            // Godot's programmatic LineEdit insertion does not emit text_changed.
            line.EmitSignal(LineEdit.SignalName.TextChanged, line.Text);
            if (submit) line.EmitSignal(LineEdit.SignalName.TextSubmitted, line.Text);
            return new { text = line.Text, submitted = submit };
        }
        if (target.Control is TextEdit text)
        {
            text.SelectAll(); text.InsertTextAtCaret(value);
            return new { text = text.Text, submitted = false };
        }
        throw new InvalidOperationException("This hint is not a text or numeric input.");
    }

    public static object SetNumber(UiTarget target, double value)
    {
        Guard(target);
        if (!double.IsFinite(value)) throw new InvalidOperationException("The value must be finite.");
        if (target.Control is not Godot.Range range || target.Control is ScrollBar)
            throw new InvalidOperationException("This hint is not a numeric field or slider.");
        // Range's value_changed is wired to the Inspector's property editor and
        // its normal undo/redo transaction. Never write scene properties directly.
        range.Value = value;
        return new { requested = value, value = range.Value, clamped = !Mathf.IsEqualApprox(value, range.Value) };
    }

    public static object Step(UiTarget target, int direction, int count)
    {
        Guard(target);
        if (target.Control is not Godot.Range range) throw new InvalidOperationException("This hint is not numeric.");
        return SetNumber(target, range.Value + direction * Math.Clamp(count, 1, 1000) * (range.Step > 0 ? range.Step : 1));
    }

    public static void Scroll(UiTarget target, int direction, int amount)
    {
        Guard(target);
        var scroll = Ancestors(target.Control).OfType<ScrollContainer>().FirstOrDefault();
        if (scroll is not null) scroll.ScrollVertical += direction * Math.Clamp(amount, 1, 100) * 80;
        else if (EditorControls.Walk(target.Control).OfType<VScrollBar>().FirstOrDefault(b => b.IsVisibleInTree()) is VScrollBar bar)
            bar.Value += direction * Math.Clamp(amount, 1, 100) * 80;
        else throw new InvalidOperationException("This control has no scrollable panel.");
    }

    public static bool NavigateInspector(InputEventKey input)
    {
        if (!input.Pressed || input.Keycode is not (Key.Up or Key.Down or Key.Pageup or Key.Pagedown)
            || input.CtrlPressed || input.AltPressed || input.MetaPressed || input.ShiftPressed
            || EditorControls.ActiveDialog() is not null) return false;
        var viewport = EditorInterface.Singleton.GetBaseControl().GetViewport();
        Control? focused = viewport.GuiGetFocusOwner();
        if (focused is null || !focused.IsVisibleInTree()) return false;
        var inspector = Ancestors(focused).OfType<EditorInspector>().FirstOrDefault();
        if (inspector is null) return false;
        if (input.Keycode is Key.Pageup or Key.Pagedown && focused is not TextEdit)
        {
            // Community's "scroll up/down" sends Page Up/Down. The property
            // label and single-line editors do not page their parent Inspector.
            var scroll = Ancestors(focused).OfType<ScrollContainer>().First();
            var bar = scroll.GetVScrollBar();
            bar.Value += (input.Keycode == Key.Pageup ? -1 : 1) * Math.Max(80, bar.Page * .9);
            return true;
        }
        // Property labels use accessibility-only focus in Godot. Ordinary
        // arrows cannot move between them. Do not intercept arrows in inputs:
        // numbers, dropdowns, multiline text, etc. retain their native behavior.
        if (focused is not EditorProperty current || input.Keycode is not (Key.Up or Key.Down)) return false;
        var rows = EditorControls.Walk(inspector).OfType<EditorProperty>()
            .Where(p => p.IsVisibleInTree() && p.Selectable && p.FocusMode != Control.FocusModeEnum.None
                && Ancestors(p).OfType<EditorInspector>().FirstOrDefault() == inspector).ToList();
        int index = rows.IndexOf(current);
        if (index < 0) return false;
        int next = index + (input.Keycode == Key.Up ? -1 : 1);
        if (next >= 0 && next < rows.Count)
        {
            rows[next].Select();
            rows[next].GrabFocus();
            inspector.EnsureControlVisible(rows[next]);
        }
        return true;
    }

    private static System.Collections.Generic.IEnumerable<Node> Ancestors(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent()) yield return current;
    }

    public static void Guard(UiTarget target)
    {
        if (!UiHints.Visible(target.Control, target.Rect)) throw new InvalidOperationException("That control is no longer visible.");
        if (EditorControls.ActiveDialog() is Window dialog && target.Window != dialog)
            throw new InvalidOperationException("Use a hint in the active popup or cancel it first.");
        if (target.Control is BaseButton { Disabled: true } or LineEdit { Editable: false }
            or TextEdit { Editable: false } or EditorSpinSlider { ReadOnly: true }
            or SpinBox { Editable: false } or Slider { Editable: false }
            || Ancestors(target.Control).OfType<EditorProperty>().Any(p => p.ReadOnly))
            throw new InvalidOperationException("This control is read-only or disabled.");
        if (target.Menu is PopupMenu menu && (target.Index >= menu.ItemCount || menu.IsItemDisabled(target.Index) || menu.IsItemSeparator(target.Index)))
            throw new InvalidOperationException("That menu item is unavailable.");
        if (target.Control is MenuBar menus && (target.Index >= menus.GetMenuCount() || menus.IsMenuDisabled(target.Index) || menus.IsMenuHidden(target.Index)))
            throw new InvalidOperationException("That menu is unavailable.");
    }

}
#endif
