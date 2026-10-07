using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Talon.UnityVoice
{
    // Internal APIs are isolated here. Missing members fail explicitly, rather
    // than silently clicking a nearby target after an editor update.
    internal static class Native
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;
        static readonly Dictionary<(Type, string), MemberInfo> Members = new Dictionary<(Type, string), MemberInfo>();

        public static object Get(object instance, string name)
        {
            if (instance == null) return null;
            var key = (instance.GetType(), name);
            if (!Members.TryGetValue(key, out var member))
            {
                for (var type = instance.GetType(); type != null; type = type.BaseType)
                {
                    member = (MemberInfo)type.GetField(name, Flags | BindingFlags.DeclaredOnly) ??
                        type.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
                    if (member != null) break;
                }
                Members[key] = member;
            }
            return member is FieldInfo field ? field.GetValue(instance) :
                member is PropertyInfo property ? property.GetValue(instance) : null;
        }

        public static object Call(object instance, string name, params object[] args)
        {
            if (instance == null) throw new InvalidOperationException("Unity interface is not available.");
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                foreach (var method in type.GetMethods(Flags | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name && m.GetParameters().Length == args.Length))
                {
                    var parameters = method.GetParameters();
                    var converted = new object[args.Length];
                    bool valid = true;
                    for (int i = 0; i < args.Length; i++)
                    {
                        var destination = parameters[i].ParameterType;
                        if (args[i] == null || destination.IsInstanceOfType(args[i])) converted[i] = args[i];
                        else
                        {
                            var conversion = destination.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                .FirstOrDefault(m => (m.Name == "op_Implicit" || m.Name == "op_Explicit") &&
                                    m.ReturnType == destination && m.GetParameters().Length == 1 &&
                                    m.GetParameters()[0].ParameterType.IsInstanceOfType(args[i]));
                            if (conversion == null) { valid = false; break; }
                            converted[i] = conversion.Invoke(null, new[] { args[i] });
                        }
                    }
                    if (valid) return method.Invoke(instance, converted);
                }
            }
            throw new NotSupportedException("Unity " + Application.unityVersion + " does not expose " + name + ".");
        }

        public static EditorWindow Window(string typeName, bool open = true)
        {
            var type = typeof(EditorWindow).Assembly.GetType("UnityEditor." + typeName);
            if (type == null)
                type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEditor." + typeName))
                    .FirstOrDefault(t => t != null);
            if (type == null) throw new NotSupportedException("Editor pane unavailable: " + typeName);
            var existing = Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>()
                .OrderByDescending(w => w == EditorWindow.focusedWindow).FirstOrDefault();
            return existing != null || !open ? existing : EditorWindow.GetWindow(type);
        }

        public static EditorWindow CurrentGuiWindow()
        {
            var viewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
            var view = viewType?.GetProperty("current", Flags)?.GetValue(null);
            return Get(view, "actualView") as EditorWindow;
        }

        public static string Letters(int index)
        {
            // Two-letter minimum. Never recycle a retired label within a session.
            string result = "";
            do { result = (char)('a' + index % 26) + result; index /= 26; }
            while (index > 0);
            return result.Length == 1 ? "a" + result : result;
        }

        public static Vector2 ScreenPoint(EditorWindow window, Vector2 rootLocal)
        {
            // Commands run outside OnGUI; use this window's backing scale,
            // rather than whichever GUI view last set pixelsPerPoint.
            var scale = (float)Call(Get(window, "m_Parent"), "GetBackingScaleFactor");
            return (window.position.position + rootLocal) * scale;
        }
    }
}
