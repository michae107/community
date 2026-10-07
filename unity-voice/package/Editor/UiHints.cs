using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Talon.UnityVoice
{
    internal static class UiHints
    {
        sealed class Target
        {
            public VisualElement element;
            public EditorWindow window;
            public Label badge;
            public string letters;
            public string identity;
            public Rect rect;
            public int generation;
        }

        const string OverlayName = "talon-voice-hints";
        static readonly Dictionary<VisualElement, Target> Targets = new Dictionary<VisualElement, Target>();
        static readonly Dictionary<string, Target> Labels = new Dictionary<string, Target>();
        static readonly Dictionary<EditorWindow, VisualElement> Overlays = new Dictionary<EditorWindow, VisualElement>();
        static EditorWindow[] windows = Array.Empty<EditorWindow>();
        static double nextWindows;
        static int nextLabel;
        static int generation;
        public static bool Enabled = true;
        public static string Scope = "all";
        public static int Count => Targets.Count;

        public static void Clear()
        {
            foreach (var layer in Overlays.Values) layer.RemoveFromHierarchy();
            Overlays.Clear();
            Targets.Clear();
            Labels.Clear();
        }

        public static void Refresh()
        {
            if (!Enabled) { Clear(); return; }
            generation++;
            if (EditorApplication.timeSinceStartup >= nextWindows)
            {
                windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                nextWindows = EditorApplication.timeSinceStartup + 1;
            }
            foreach (var window in windows)
            {
                if (window == null) continue;
                var root = window.rootVisualElement;
                if (root?.panel == null || root.worldBound.width < 2 || root.worldBound.height < 2) continue;
                if (Native.Get(Native.Get(window, "m_Parent"), "actualView") is EditorWindow active && active != window) continue;
                if (Scope == "inspector" && !window.GetType().Name.Contains("Inspector")) continue;
                if (!Overlays.TryGetValue(window, out var layer) || layer.parent != root)
                {
                    layer?.RemoveFromHierarchy();
                    layer = new VisualElement { name = OverlayName, pickingMode = PickingMode.Ignore };
                    layer.style.position = Position.Absolute;
                    layer.style.left = layer.style.top = layer.style.right = layer.style.bottom = 0;
                    root.Add(layer);
                    Overlays[window] = layer;
                }
                int budget = 12000;
                Walk(root, window, layer, ref budget);
                layer.BringToFront();
            }
            foreach (var target in Targets.Values.Where(t => t.generation != generation).ToArray()) Remove(target);
            foreach (var entry in Overlays.Where(p => p.Key == null || p.Value.panel == null).ToArray())
            {
                entry.Value.RemoveFromHierarchy();
                Overlays.Remove(entry.Key);
            }
        }

        static void Walk(VisualElement element, EditorWindow window, VisualElement layer, ref int budget)
        {
            if (--budget < 0 || element.name == OverlayName || !element.visible ||
                element.resolvedStyle.display == DisplayStyle.None) return;
            bool control = IsControl(element);
            if (control && element.enabledInHierarchy && TryRect(element, out var rect))
            {
                var identity = Identity(element);
                if (Targets.TryGetValue(element, out var old) && (old.identity != identity || old.window != window)) Remove(old);
                if (!Targets.TryGetValue(element, out var target))
                {
                    var letters = Native.Letters(nextLabel++);
                    var badge = new Label(letters) { pickingMode = PickingMode.Ignore };
                    badge.style.position = Position.Absolute;
                    badge.style.backgroundColor = new Color(.55f, 1, .45f, .97f);
                    badge.style.color = Color.black;
                    badge.style.fontSize = 10;
                    badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                    badge.style.paddingLeft = badge.style.paddingRight = 2;
                    badge.style.height = 14;
                    target = new Target { element = element, window = window, badge = badge,
                        letters = letters, identity = identity };
                    Targets[element] = target;
                    Labels[letters] = target;
                    layer.Add(badge);
                }
                target.rect = rect;
                target.generation = generation;
                var local = layer.WorldToLocal(rect.position);
                target.badge.style.left = local.x;
                target.badge.style.top = local.y;
                // Compound vectors expose scalar children. Simple fields and
                // buttons are one action, not a second hint on their text child.
                if (IsLeafControl(element)) return;
            }
            for (int i = 0; i < element.hierarchy.childCount; i++) Walk(element.hierarchy[i], window, layer, ref budget);
        }

        static bool IsLeafControl(VisualElement e) => e is Button || e is Toggle ||
            e is TextField || e is FloatField || e is DoubleField || e is IntegerField || e is LongField ||
            e is Slider || e is SliderInt || e is DropdownField || e is EnumField ||
            e is UnityEditor.UIElements.ObjectField || e is UnityEditor.UIElements.ColorField;

        static bool IsControl(VisualElement e)
        {
            if (e is IMGUIContainer || e is Label || e is Foldout || e is ScrollView) return false;
            return IsLeafControl(e) || (e.focusable && e.tabIndex >= 0 && !(e is BaseVerticalCollectionView));
        }

        static string Identity(VisualElement e)
        {
            var text = e is TextElement t && !(e is TextField) ? t.text : "";
            var binding = e is IBindable b ? b.bindingPath : "";
            return e.GetType().FullName + ":" + e.name + ":" + binding + ":" + text + ":" + e.tooltip;
        }

        static bool TryRect(VisualElement e, out Rect rect)
        {
            rect = e.worldBound;
            if (e.panel == null || !e.visible || !e.enabledInHierarchy) return false;
            for (var p = e.parent; p != null; p = p.parent)
            {
                if (!p.visible || p.resolvedStyle.display == DisplayStyle.None) return false;
                if (p.parent == null)
                {
                    var bound = p.worldBound;
                    rect = Rect.MinMaxRect(Math.Max(rect.xMin, bound.xMin), Math.Max(rect.yMin, bound.yMin),
                        Math.Min(rect.xMax, bound.xMax), Math.Min(rect.yMax, bound.yMax));
                }
            }
            if (Native.Get(e, "worldClip") is Rect clip)
                rect = Rect.MinMaxRect(Math.Max(rect.xMin, clip.xMin), Math.Max(rect.yMin, clip.yMin),
                    Math.Min(rect.xMax, clip.xMax), Math.Min(rect.yMax, clip.yMax));
            if (rect.width < 4 || rect.height < 4 || float.IsNaN(rect.x)) return false;
            var picked = e.panel.Pick(rect.center);
            return picked == e || (picked != null && e.Contains(picked));
        }

        static void Remove(Target target)
        {
            target.badge.RemoveFromHierarchy();
            Targets.Remove(target.element);
            Labels.Remove(target.letters);
        }

        public static Reply Command(Arguments args)
        {
            if (!Enabled || !Labels.TryGetValue(args.letters, out var target))
                throw new InvalidOperationException("No visible control has that hint.");
            // Validate the exact same element and binding immediately before the
            // action. Recycled rows and changed popups must not retarget a hint.
            if (target.window == null || Identity(target.element) != target.identity ||
                (Native.Get(Native.Get(target.window, "m_Parent"), "actualView") is EditorWindow active && active != target.window) ||
                !TryRect(target.element, out var rect) || Vector2.Distance(rect.center, target.rect.center) > 2)
            {
                Refresh();
                throw new InvalidOperationException("The control moved or changed. Read its current hint and retry.");
            }
            var e = target.element;
            if (args.action == "set") { SetNumber(e, args.value); return new Reply(); }
            if (args.action == "text")
            {
                if (!(e is TextField field)) throw new NotSupportedException("This control is not a text field.");
                field.value = args.text;
                return new Reply();
            }
            if (args.action == "scroll")
            {
                var scroll = e.GetFirstAncestorOfType<ScrollView>();
                if (scroll == null) throw new NotSupportedException("This control has no exposed scroll container.");
                scroll.scrollOffset += new Vector2(0, args.value * 80);
                return new Reply();
            }
            target.window.Focus();
            if (args.action == "focus" || args.action == "change")
            {
                e.Focus();
                if (e is TextField field) field.SelectAll();
            }
            var local = target.window.rootVisualElement.WorldToLocal(rect.center);
            return Reply.Pointer(Native.ScreenPoint(target.window, local));
        }

        static void SetNumber(VisualElement e, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("A finite number is required.");
            if (e is FloatField f) f.value = value;
            else if (e is DoubleField d) d.value = value;
            else if (e is IntegerField i) i.value = checked((int)value);
            else if (e is LongField l) l.value = checked((long)value);
            else if (e is Slider s) s.value = value;
            else if (e is SliderInt si) si.value = checked((int)value);
            else throw new NotSupportedException("This control does not expose a numeric value.");
        }
    }
}
