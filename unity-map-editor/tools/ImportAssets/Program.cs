using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

if (args.Length < 2 || args.Length > 3 || (args.Length == 3 && args[2] != "--update-catalog"))
    throw new ArgumentException("Usage: ImportAssets GODOT_PROJECT UNITY_PROJECT [--update-catalog]");
string source = Path.GetFullPath(args[0]);
string project = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(source, "project.godot")) ||
    !File.Exists(Path.Combine(project, "ProjectSettings/ProjectVersion.txt")))
    throw new ArgumentException("Both project roots must exist.");
string destination = Path.Combine(project, "Assets/IdleRpgMap");
var copied = new HashSet<string>();
void Copy(string relative)
{
    relative = relative.Replace('\\', '/');
    string from = Path.GetFullPath(Path.Combine(source, relative));
    if (!from.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new IOException("Asset reference escaped the source project.");
    string to = Path.Combine(destination, Path.GetRelativePath(source, from));
    if (!copied.Add(from)) return;
    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
    if (File.Exists(to))
    {
        if (!File.ReadAllBytes(from).SequenceEqual(File.ReadAllBytes(to)))
            throw new IOException("Refusing to overwrite a modified Unity asset: " + to);
    }
    else File.Copy(from, to);
    if (from.EndsWith(".gltf"))
    {
        using var json = JsonDocument.Parse(File.ReadAllText(from));
        foreach (string kind in new[] { "buffers", "images" })
            if (json.RootElement.TryGetProperty(kind, out var entries))
                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("uri", out var uri) && !uri.GetString()!.StartsWith("data:"))
                        Copy(Path.GetRelativePath(source, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(from)!, Uri.UnescapeDataString(uri.GetString()!)))));
    }
}
var catalog = new List<object>();
Copy("src/map/CoordinateGlyphs.png");
foreach (string definition in Directory.EnumerateFiles(Path.Combine(source, "src/map/tiles"), "*.tres", SearchOption.AllDirectories).Order())
{
    string text = File.ReadAllText(definition);
    if (!Regex.IsMatch(text, @"(?m)^GenerationEnabled = true$")) continue;
    string relative = Path.GetRelativePath(source, definition).Replace('\\', '/');
    string scene = Regex.Match(text, "type=\"PackedScene\"[^\\n]*path=\"res://([^\"]+)\"").Groups[1].Value;
    string sceneText = File.ReadAllText(Path.Combine(source, scene));
    string model = Regex.Match(sceneText, "path=\"res://([^\"]+\\.gltf)\"").Groups[1].Value;
    if (string.IsNullOrEmpty(model)) throw new IOException("Missing visual model: " + scene);
    string profiles = Regex.Match(text, @"EdgeProfiles = PackedStringArray\(([^\n]+)\)").Groups[1].Value;
    string[] edges = Regex.Matches(profiles, "\"([GSRW]+)\"").Select(m => m.Groups[1].Value).ToArray();
    if (edges.Length != 6 || edges.Any(e => e.Length != 10)) throw new IOException("Invalid tile edge data: " + definition);
    var surfaceMatch = Regex.Match(text, @"(?m)^Surface = (\d+)$");
    int surface = surfaceMatch.Success ? int.Parse(surfaceMatch.Groups[1].Value) : 0;
    Copy(model);
    var decorations = new List<object>();
    // Crossing scenes place a separate bridge on top of their river mesh.
    foreach (Match resource in Regex.Matches(sceneText, "\\[ext_resource type=\"PackedScene\"[^\\n]*path=\"res://([^\"]+)\"[^\\n]*id=\"([^\"]+)\"\\]"))
    {
        string propScene = resource.Groups[1].Value;
        if (!propScene.StartsWith("src/map/props/", StringComparison.Ordinal)) continue;
        string propText = File.ReadAllText(Path.Combine(source, propScene));
        string propModel = Regex.Match(propText, "path=\"res://([^\"]+\\.gltf)\"").Groups[1].Value;
        if (string.IsNullOrEmpty(propModel)) throw new IOException("Unsupported decoration: " + propScene);
        string block = Regex.Match(sceneText, "\\[node[^\\n]*instance=ExtResource\\(\"" + Regex.Escape(resource.Groups[2].Value) + "\"\\)\\]([^\\[]*)").Groups[1].Value;
        string transform = Regex.Match(block, @"transform = Transform3D\(([^)]+)\)").Groups[1].Value;
        float yaw = 0;
        if (!string.IsNullOrEmpty(transform))
        {
            float[] values = transform.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length != 12 || values[9] != 0 || values[10] != 0 || values[11] != 0 || values[4] != 1)
                throw new IOException("Unsupported decoration transform: " + propScene);
            yaw = MathF.Atan2(values[2], values[0]) * 180 / MathF.PI;
        }
        Copy(propModel);
        decorations.Add(new { Model = "Assets/IdleRpgMap/" + propModel, Yaw = yaw });
    }
    catalog.Add(new { Definition = "res://" + relative, Surface = surface, Edges = edges, Model = "Assets/IdleRpgMap/" + model, Decorations = decorations });
}
foreach (string map in Directory.EnumerateFiles(Path.Combine(source, "src/map_editor/maps"), "*.hexmap"))
    Copy(Path.GetRelativePath(source, map));
foreach (string texture in Directory.EnumerateFiles(Path.Combine(source, "assets/region/hex"), "*.png"))
    Copy(Path.GetRelativePath(source, texture));
string catalogPath = Path.Combine(destination, "TileCatalog.json");
string catalogJson = JsonSerializer.Serialize(new { Tiles = catalog }, new JsonSerializerOptions { WriteIndented = true });
if (File.Exists(catalogPath) && File.ReadAllText(catalogPath) != catalogJson && args.Length != 3)
    throw new IOException("Existing tile catalog differs. Review before replacing: " + catalogPath);
File.WriteAllText(catalogPath, catalogJson);
Console.WriteLine($"Copied {copied.Count} assets/documents and {catalog.Count} tile definitions. Godot sources unchanged.");
