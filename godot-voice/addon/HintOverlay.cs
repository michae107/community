#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace TalonVoice;

[Tool]
public partial class HintOverlay : Control
{
    internal NativeHints? Hints;
    internal UiHints? UiHints;
    private List<(string Scope, string Letters, Rect2 Rect)> _drawTargets = new();
    private List<(string Scope, string Letters, Rect2 Rect)> _nextTargets = new();
    private Vector2 _lastPosition;
    private Vector2 _lastSize;
    private float _lastScale;

    internal void UpdateVisuals(bool showNative)
    {
        _nextTargets.Clear();
        if (showNative && Hints is { Enabled: true })
            foreach (var target in Hints.Targets) _nextTargets.Add((target.Scope, target.Letters, target.Rect));
        if (UiHints is { Enabled: true })
        {
            Window window = GetWindow();
            foreach (var target in UiHints.Targets)
                if (GodotObject.IsInstanceValid(target.Control) && target.Window == window)
                    _nextTargets.Add((target.Kind == "menu" ? "menu" : "ui", target.Letters, target.Rect));
        }
        float scale = EditorInterface.Singleton.GetEditorScale();
        bool changed = _lastPosition != GlobalPosition || _lastSize != Size || _lastScale != scale
            || _nextTargets.Count != _drawTargets.Count;
        for (int i = 0; !changed && i < _nextTargets.Count; i++) changed = _nextTargets[i] != _drawTargets[i];
        if (!changed) return;
        (_drawTargets, _nextTargets) = (_nextTargets, _drawTargets);
        _lastPosition = GlobalPosition; _lastSize = Size; _lastScale = scale;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Font font = GetThemeDefaultFont();
        int size = (int)(12 * EditorInterface.Singleton.GetEditorScale());
        // Draw only cached badge data: no editor traversal or live Control
        // dereferences during rendering, including after Inspector rebuilds.
        foreach (var target in _drawTargets)
        {
            if (target.Scope is "ui" or "menu") continue;
            var textSize = font.GetStringSize(target.Letters, fontSize: size);
            var area = target.Rect;
            var position = new Vector2(area.End.X - textSize.X - 14, area.Position.Y + (area.Size.Y - size - 4) / 2) - GlobalPosition;
            if (target.Scope is "tabs" or "docks") position.Y = area.End.Y - size - 5 - GlobalPosition.Y;
            var rect = new Rect2(position, new Vector2(textSize.X + 8, size + 4));
            Color color = target.Scope switch { "scene" => new("ffd166"), "files" => new("8bd5ff"), _ => new("c4a7ff") };
            DrawRect(rect, new Color("18202d"));
            DrawRect(rect, color, false);
            DrawString(font, position + new Vector2(4, font.GetAscent(size) + 1), target.Letters, fontSize: size, modulate: color);
        }
        foreach (var target in _drawTargets)
        {
            if (target.Scope is not ("ui" or "menu")) continue;
            var textSize = font.GetStringSize(target.Letters, fontSize: size);
            Vector2 position = target.Rect.Position - GlobalPosition;
            // Top-right corner keeps values readable; property/section labels
            // live on the left side, away from the input's own badge.
            position.X += target.Rect.Size.X - textSize.X - 8;
            position.Y += target.Scope == "menu" ? (target.Rect.Size.Y - size - 2) / 2 : -3;
            position.X = Mathf.Clamp(position.X, 0, Math.Max(0, Size.X - textSize.X - 8));
            position.Y = Math.Max(0, position.Y);
            var rect = new Rect2(position, new Vector2(textSize.X + 6, size + 2));
            DrawRect(rect, new Color("142b25"));
            DrawRect(rect, new Color("92f2bd"), false);
            DrawString(font, position + new Vector2(3, font.GetAscent(size)), target.Letters, fontSize: size, modulate: new Color("92f2bd"));
        }
    }
}
#endif
