using System;
using System.IO;
using UnityEngine;

namespace Talon.UnityVoice
{
    [Serializable]
    internal sealed class Arguments
    {
        public string scope = "";
        public string letters = "";
        public string pane = "";
        public string text = "";
        public string action = "";
        public int depth = 1;
        public int index = 0;
        public float value = 0;
    }

    [Serializable]
    internal sealed class Request
    {
        public string id = "";
        public string session = "";
        public string method = "";
        public long created = 0;
        public Arguments args = new Arguments();
    }

    [Serializable]
    internal sealed class Reply
    {
        public string error = "";
        public string message = "";
        public bool movePointer;
        public float x;
        public float y;
        public string clipboard;
        public int count;

        public static Reply Pointer(Vector2 point)
        {
            return new Reply { movePointer = true, x = point.x, y = point.y };
        }
    }

    [Serializable]
    internal sealed class ConnectionState
    {
        public int version = 1;
        public string kind = "editor";
        public int pid;
        public string session;
        public string project;
        public long updated;
        public bool hints;
        public int treeHints;
        public int uiHints;
        public string error = "";
    }

    internal static class Protocol
    {
        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static void Write(string path, object value)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(value));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
