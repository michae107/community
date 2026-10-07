using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Talon.UnityVoice
{
    internal static class TreeHints
    {
        sealed class Row
        {
            public string key;
            public string letters;
            public string scope;
            public int id;
            public string path;
            public EditorWindow window;
            public double seen;
        }

        static readonly Dictionary<string, Row> Rows = new Dictionary<string, Row>();
        static readonly Dictionary<string, Row> Labels = new Dictionary<string, Row>();
        static readonly Dictionary<string, int> Next = new Dictionary<string, int>();
        static GUIStyle badge;
        public static bool Enabled = true;
        public static int Count => Rows.Values.Count(r => EditorApplication.timeSinceStartup - r.seen < 1);

        public static void Start()
        {
            EditorApplication.hierarchyWindowItemOnGUI += HierarchyRow;
            EditorApplication.projectWindowItemOnGUI += ProjectRow;
        }

        public static void Stop()
        {
            EditorApplication.hierarchyWindowItemOnGUI -= HierarchyRow;
            EditorApplication.projectWindowItemOnGUI -= ProjectRow;
        }

        static void HierarchyRow(int id, Rect rect) => Draw("scene", id.ToString(), id, "", rect);

        static void ProjectRow(string guid, Rect rect)
        {
            if (Event.current.type != EventType.Repaint || !Enabled) return;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return;
            Draw("files", guid, 0, path, rect);
        }

        static void Draw(string scope, string identity, int id, string path, Rect rect)
        {
            if (!Enabled || Event.current.type != EventType.Repaint) return;
            var window = Native.CurrentGuiWindow();
            if (window == null) return;
            var key = window.GetInstanceID() + ":" + scope + ":" + identity;
            if (!Rows.TryGetValue(key, out var row))
            {
                Next.TryGetValue(scope, out int next);
                row = new Row { key = key, letters = Native.Letters(next), scope = scope,
                    id = id, path = path, window = window };
                Next[scope] = next + 1;
                Rows[key] = row;
                Labels[scope + ":" + row.letters] = row;
            }
            row.seen = EditorApplication.timeSinceStartup;
            badge ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.black }
            };
            float width = Math.Max(23, badge.CalcSize(new GUIContent(row.letters)).x + 6);
            var labelRect = new Rect(rect.xMax - width - 3, rect.y, width, Math.Min(17, rect.height));
            EditorGUI.DrawRect(labelRect, scope == "scene" ? new Color(1, .82f, .24f) : new Color(.45f, .8f, 1));
            GUI.Label(labelRect, row.letters, badge);
        }

        public static void Prune()
        {
            foreach (var row in Rows.Values.Where(r => r.window == null ||
                EditorApplication.timeSinceStartup - r.seen > 120).ToArray())
            {
                Rows.Remove(row.key);
                Labels.Remove(row.scope + ":" + row.letters);
            }
        }

        public static Reply Command(string method, Arguments args)
        {
            if (args.scope != "scene" && args.scope != "files") throw new ArgumentException("Unknown tree.");
            if (method.EndsWith("_root") || method == "tree.root") return Root(method, args);
            if (!Enabled) throw new InvalidOperationException("Turn hints on first.");
            if (!Labels.TryGetValue(args.scope + ":" + args.letters, out var row) || row.window == null ||
                EditorApplication.timeSinceStartup - row.seen > 1)
                throw new InvalidOperationException("That tree hint is no longer visible. Refresh hints and try again.");
            if (Native.Get(Native.Get(row.window, "m_Parent"), "actualView") is EditorWindow active && active != row.window)
                throw new InvalidOperationException("That tree tab is no longer visible.");
            var target = args.scope == "scene" ? EditorUtility.EntityIdToObject(row.id) :
                AssetDatabase.LoadMainAssetAtPath(row.path);
            if (target == null && args.scope == "scene" && SceneFor(row.id).IsValid())
                return SceneHeader(row, method);
            if (target == null) throw new InvalidOperationException("The target no longer exists.");
            row.window.Focus();
            Selection.activeObject = target;
            if (method == "tree.select") return new Reply();
            if (method == "tree.copy_path") return new Reply { clipboard = args.scope == "files" ? row.path : PathOf(target, row.path) };
            if (method == "tree.parent")
            {
                if (target is GameObject go && go.transform.parent != null) Selection.activeGameObject = go.transform.parent.gameObject;
                else if (args.scope == "files") Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(System.IO.Path.GetDirectoryName(row.path));
                return new Reply();
            }
            if (method == "tree.open")
            {
                if (args.scope == "files" && !AssetDatabase.IsValidFolder(row.path)) AssetDatabase.OpenAsset(target);
                else if (args.scope == "scene") SceneView.lastActiveSceneView?.FrameSelected();
                else Expand(row.window, args.scope, target, true, args.depth);
                return new Reply();
            }
            if (method == "tree.rename")
            {
                row.window.SendEvent(Event.KeyboardEvent("f2"));
                return new Reply();
            }
            bool expanded = method == "tree.expand";
            if (method == "tree.toggle") expanded = !IsExpanded(row.window, args.scope, target.GetInstanceID());
            else if (method != "tree.expand" && method != "tree.collapse") throw new ArgumentException("Unknown tree action.");
            if (args.scope == "files" && !AssetDatabase.IsValidFolder(row.path))
            {
                if (method == "tree.toggle") AssetDatabase.OpenAsset(target);
                return new Reply();
            }
            Expand(row.window, args.scope, target, expanded, args.depth);
            return new Reply();
        }

        static string PathOf(Object target, string assetPath)
        {
            if (!(target is GameObject go)) return assetPath;
            string path = go.name;
            for (var parent = go.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        static Reply SceneHeader(Row row, string method)
        {
            row.window.Focus();
            var tree = Native.Get(Native.Get(row.window, "sceneHierarchy"), "treeView");
            Native.Call(tree, "SetSelection", new EntityId[] { row.id }, true);
            if (method == "tree.select") return new Reply();
            if (method == "tree.copy_path") return new Reply { clipboard = SceneFor(row.id).path };
            if (method != "tree.expand" && method != "tree.collapse" && method != "tree.toggle" && method != "tree.open")
                throw new NotSupportedException("Use scene commands for a scene header, or target a GameObject row.");
            bool expanded = method == "tree.toggle" ? !IsExpanded(row.window, "scene", row.id) : method != "tree.collapse";
            SetExpanded(row.window, "scene", row.id, expanded);
            row.window.Repaint();
            return new Reply();
        }

        static Scene SceneFor(int handle)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.handle == handle) return scene;
            }
            return default;
        }

        static object Data(EditorWindow window, string scope)
        {
            var tree = scope == "scene" ? Native.Get(Native.Get(window, "sceneHierarchy"), "treeView") :
                Native.Get(window, "m_AssetTree") ?? Native.Get(window, "m_FolderTree");
            return Native.Get(tree, "data");
        }

        static bool IsExpanded(EditorWindow window, string scope, int id) => (bool)Native.Call(Data(window, scope), "IsExpanded", id);

        static void SetExpanded(EditorWindow window, string scope, int id, bool expanded)
        {
            if (scope == "scene") Native.Call(window, "SetExpanded", id, expanded);
            else Native.Call(Data(window, scope), "SetExpanded", id, expanded);
        }

        static void Expand(EditorWindow window, string scope, Object target, bool expanded, int depth)
        {
            depth = Mathf.Clamp(depth, 1, 8);
            int budget = 3000;
            void Visit(Object item, int remaining)
            {
                if (--budget < 0) throw new InvalidOperationException("Expansion stopped at 3000 items. Expand a smaller branch.");
                SetExpanded(window, scope, item.GetInstanceID(), expanded);
                if (remaining <= 1 || !expanded) return;
                if (item is GameObject go)
                {
                    foreach (Transform child in go.transform) Visit(child.gameObject, remaining - 1);
                }
                else
                {
                    foreach (var path in AssetDatabase.GetSubFolders(AssetDatabase.GetAssetPath(item)))
                        Visit(AssetDatabase.LoadMainAssetAtPath(path), remaining - 1);
                }
            }
            Visit(target, depth);
            window.Repaint();
        }

        static Reply Root(string method, Arguments args)
        {
            var window = Native.Window(args.scope == "scene" ? "SceneHierarchyWindow" : "ProjectBrowser");
            window.Focus();
            if (args.scope == "files")
            {
                var root = AssetDatabase.LoadMainAssetAtPath("Assets");
                Selection.activeObject = root;
                if (method != "tree.root") Expand(window, "files", root, method == "tree.expand_root", args.depth);
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    if (method == "tree.root") Selection.activeGameObject = scene.GetRootGameObjects().FirstOrDefault();
                    else
                    {
                        SetExpanded(window, "scene", scene.handle, method == "tree.expand_root");
                        foreach (var root in scene.GetRootGameObjects()) Expand(window, "scene", root, method == "tree.expand_root", args.depth);
                    }
                }
            }
            window.Repaint();
            return new Reply();
        }
    }
}
