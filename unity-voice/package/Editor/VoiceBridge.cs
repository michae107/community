using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Talon.UnityVoice
{
    [InitializeOnLoad]
    internal static class VoiceBridge
    {
        static readonly string Session = Guid.NewGuid().ToString("N");
        static readonly Regex RequestName = new Regex("^[a-f0-9]{32}\\.request\\.json$", RegexOptions.CultureInvariant);
        static readonly int Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        static readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "talon-unity-" + Environment.UserName, Pid.ToString());
        static double nextPoll;
        static double nextScan;
        static double nextState;
        static double nextCleanup;
        static string error = "";

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, uint mode);

        static VoiceBridge()
        {
            if (Application.isBatchMode) return;
            try
            {
                var parent = Path.GetDirectoryName(DirectoryPath);
                PrivateDirectory(parent);
                PrivateDirectory(DirectoryPath);
                TreeHints.Start();
                EditorApplication.update += Update;
                EditorApplication.quitting += Stop;
                AssemblyReloadEvents.beforeAssemblyReload += Stop;
                WriteState();
            }
            catch (Exception exception)
            {
                Debug.LogError("Talon Voice could not start: " + exception.Message);
            }
        }

        static void PrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Voice connection directory cannot be a symbolic link.");
            if (Application.platform == RuntimePlatform.LinuxEditor && chmod(path, 448) != 0)
                throw new IOException("Cannot secure the voice connection directory.");
        }

        static void Stop()
        {
            EditorApplication.update -= Update;
            EditorApplication.quitting -= Stop;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            TreeHints.Stop();
            UiHints.Clear();
            try { File.Delete(Path.Combine(DirectoryPath, "state.json")); }
            catch (IOException) { }
        }

        static void Update()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + .05;
            try
            {
                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
                {
                    foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.request.json").Take(8)) Process(file);
                    if (now >= nextScan)
                    {
                        bool active = UnityEditorInternal.InternalEditorUtility.isApplicationActive;
                        nextScan = now + (active ? .35 : 1.5);
                        if (active)
                        {
                            UiHints.Refresh();
                            EditorApplication.RepaintHierarchyWindow();
                            EditorApplication.RepaintProjectWindow();
                        }
                    }
                }
                if (now >= nextState) { nextState = now + 1; WriteState(); }
                if (now >= nextCleanup)
                {
                    nextCleanup = now + 30;
                    TreeHints.Prune();
                    foreach (var file in Directory.EnumerateFiles(DirectoryPath).Where(p =>
                        p.EndsWith(".response.json") || p.EndsWith(".request.json") || p.EndsWith(".tmp")))
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromSeconds(30)) File.Delete(file);
                }
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                // Back off on persistent editor/API errors instead of producing
                // a log every frame or repeatedly scanning a broken control.
                nextScan = now + 3;
                nextPoll = now + 1;
                try { WriteState(); } catch (IOException) { }
            }
        }

        static void Process(string file)
        {
            if (!RequestName.IsMatch(Path.GetFileName(file))) return;
            var responsePath = file.Replace(".request.json", ".response.json");
            Reply reply;
            try
            {
                if (new FileInfo(file).Length > 131072) throw new ArgumentException("Request too large.");
                var request = JsonUtility.FromJson<Request>(File.ReadAllText(file));
                File.Delete(file);
                if (request == null || request.id + ".request.json" != Path.GetFileName(file) || request.session != Session)
                    throw new ArgumentException("Stale or invalid voice session.");
                long age = Protocol.Now - request.created;
                if (age < -500 || age > 2500) throw new ArgumentException("Voice command expired.");
                if (!UnityEditorInternal.InternalEditorUtility.isApplicationActive)
                    throw new InvalidOperationException("Unity is no longer the active application.");
                reply = Dispatch(request.method, request.args ?? new Arguments());
                error = "";
            }
            catch (Exception exception) { reply = new Reply { error = exception.GetBaseException().Message }; }
            Protocol.Write(responsePath, reply);
        }

        static Reply Dispatch(string method, Arguments args)
        {
            if (method == "status") return new Reply { count = TreeHints.Count + UiHints.Count,
                message = "Unity Voice: " + TreeHints.Count + " tree hints, " + UiHints.Count + " UI hints. " + error };
            if (method == "focus") return EditorCommands.Focus(args.pane);
            if (method == "editor") return EditorCommands.Action(args.action);
            if (method == "tabs") return EditorCommands.Tabs(args.action, args.index);
            if (method == "ui") return UiHints.Command(args);
            if (method.StartsWith("tree.")) return TreeHints.Command(method, args);
            if (method == "hints")
            {
                if (args.action == "on") TreeHints.Enabled = UiHints.Enabled = true;
                else if (args.action == "off") TreeHints.Enabled = UiHints.Enabled = false;
                else if (args.action == "toggle") TreeHints.Enabled = UiHints.Enabled = !UiHints.Enabled;
                else if (args.action == "all") UiHints.Scope = "all";
                else if (args.action == "inspector") UiHints.Scope = "inspector";
                else if (args.action != "refresh") throw new ArgumentException("Unknown hint action.");
                UiHints.Refresh();
                EditorApplication.RepaintHierarchyWindow();
                EditorApplication.RepaintProjectWindow();
                return new Reply();
            }
            throw new ArgumentException("Unknown voice command.");
        }

        static void WriteState()
        {
            Protocol.Write(Path.Combine(DirectoryPath, "state.json"), new ConnectionState
            {
                pid = Pid, session = Session, project = Path.GetDirectoryName(Application.dataPath),
                updated = Protocol.Now, hints = UiHints.Enabled, treeHints = TreeHints.Count,
                uiHints = UiHints.Count, error = error
            });
        }
    }
}
