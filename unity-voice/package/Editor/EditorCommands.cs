using System;
using System.Collections;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Talon.UnityVoice
{
    internal static class EditorCommands
    {
        static Type closedTab;

        public static Reply Focus(string pane)
        {
            string type = pane switch
            {
                "scene" => "SceneHierarchyWindow",
                "files" => "ProjectBrowser",
                "inspector" => "InspectorWindow",
                "editor" => "SceneView",
                "game" => "GameView",
                "console" => "ConsoleWindow",
                "animation" => "AnimationWindow",
                "animator" => "GraphTools.AnimatorControllerTool",
                _ => throw new ArgumentException("Unknown pane: " + pane)
            };
            var window = Native.Window(type);
            window.Focus();
            if (pane == "editor") return Reply.Pointer(Native.ScreenPoint(window, window.rootVisualElement.contentRect.center));
            return new Reply();
        }

        public static Reply Tabs(string action, int index)
        {
            var window = EditorWindow.focusedWindow;
            if (action == "reopen")
            {
                if (closedTab == null) throw new InvalidOperationException("No voice-closed tab to reopen.");
                EditorWindow.GetWindow(closedTab).Focus();
                return new Reply();
            }
            if (window == null) throw new InvalidOperationException("Focus an editor pane first.");
            if (action == "close") { closedTab = window.GetType(); window.Close(); return new Reply(); }
            if (action == "open" || action == "duplicate")
                throw new NotSupportedException("Unity tabs are editor panes. Use bar node, bar inspector, or bar tree to open a pane.");
            var panes = Native.Get(Native.Get(window, "m_Parent"), "m_Panes") as IList;
            if (panes == null || panes.Count == 0) throw new InvalidOperationException("The focused pane is not in a tab group.");
            int current = panes.IndexOf(window);
            int destination = action switch
            {
                "next" => (current + 1) % panes.Count,
                "previous" => (current + panes.Count - 1) % panes.Count,
                "final" => panes.Count - 1,
                "select" => index - 1,
                _ => throw new ArgumentException("Unknown tab command.")
            };
            if (destination < 0 || destination >= panes.Count) throw new ArgumentException("That tab number is outside this group.");
            ((EditorWindow)panes[destination]).Focus();
            return new Reply();
        }

        public static Reply Action(string action)
        {
            switch (action)
            {
                case "run": EditorApplication.isPlaying = true; break;
                case "stop": EditorApplication.isPlaying = false; break;
                case "pause": EditorApplication.isPaused = !EditorApplication.isPaused; break;
                case "step": EditorApplication.Step(); break;
                case "save": EditorSceneManager.SaveOpenScenes(); AssetDatabase.SaveAssets(); break;
                case "refresh": EditorApplication.delayCall += () => AssetDatabase.Refresh(); break;
                case "undo": Undo.PerformUndo(); break;
                case "redo": Undo.PerformRedo(); break;
                case "select": Tools.current = Tool.View; break;
                case "move": Tools.current = Tool.Move; break;
                case "rotate": Tools.current = Tool.Rotate; break;
                case "scale": Tools.current = Tool.Scale; break;
                case "rect": Tools.current = Tool.Rect; break;
                case "transform": Tools.current = Tool.Transform; break;
                case "local": Tools.pivotRotation = Tools.pivotRotation == PivotRotation.Local ? PivotRotation.Global : PivotRotation.Local; break;
                case "frame": SceneView.lastActiveSceneView?.FrameSelected(); break;
                case "2d": Native.Window("SceneView"); SceneView.lastActiveSceneView.in2DMode = true; break;
                case "3d": Native.Window("SceneView"); SceneView.lastActiveSceneView.in2DMode = false; break;
                case "preferences": SettingsService.OpenUserPreferences(); break;
                case "settings": SettingsService.OpenProjectSettings(); break;
                case "build": Menu("File/Build Profiles"); break;
                case "new_scene": Menu("File/New Scene"); break;
                case "open_scene": Menu("File/Open Scene"); break;
                case "duplicate": NodeMenu("Edit/Duplicate"); break;
                case "delete": NodeMenu("Edit/Delete"); break;
                case "copy": NodeMenu("Edit/Copy"); break;
                case "paste": Native.Window("SceneHierarchyWindow").Focus(); Menu("Edit/Paste"); break;
                case "empty": Native.Window("SceneHierarchyWindow").Focus(); Menu("GameObject/Create Empty"); break;
                default: throw new ArgumentException("Unknown editor action: " + action);
            }
            return new Reply();
        }

        static void Menu(string path)
        {
            if (!EditorApplication.ExecuteMenuItem(path)) throw new NotSupportedException("Menu command is unavailable: " + path);
        }

        static void NodeMenu(string path)
        {
            if (Selection.gameObjects.Length == 0 || Selection.objects.Any(EditorUtility.IsPersistent))
                throw new InvalidOperationException("Select scene objects with node first. This command cannot change project assets.");
            Native.Window("SceneHierarchyWindow").Focus();
            Menu(path);
        }
    }
}
