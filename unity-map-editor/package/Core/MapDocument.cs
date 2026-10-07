using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace IdleRpg.MapEditing
{
    public enum PathKind { Road, River, Coast }
    public enum WaterSide { Left, Right }
    public enum MapShape { Rectangle, Hexagon }
    public enum SurfaceKind { Land, Road, River, Coast, Ocean }

    public readonly struct GridPoint : IEquatable<GridPoint>
    {
        public int X { get; }
        public int Y { get; }
        [JsonConstructor] public GridPoint(int x, int y) { X = x; Y = y; }
        // Preserve Godot's source-space math. Only the Unity renderer mirrors X
        // to match glTFast's right-handed -> left-handed model conversion.
        [JsonIgnore] public Vector3 Position => new Vector3((X + ((Y & 1) == 1 ? .5f : 0) - 5) * 2.025f, 0, (Y - 4) * 1.7537f);
        public bool Equals(GridPoint other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GridPoint point && Equals(point);
        public override int GetHashCode() => unchecked(X * 397 ^ Y);
        public static bool operator ==(GridPoint a, GridPoint b) => a.Equals(b);
        public static bool operator !=(GridPoint a, GridPoint b) => !a.Equals(b);
        public override string ToString()
        {
            long column = X < 0 ? -(long)X : (long)X + 1;
            string letters = "";
            while (column > 0) { column--; letters = (char)('A' + column % 26) + letters; column /= 26; }
            return (X < 0 ? "-" : "") + letters + ((long)Y + 1).ToString(CultureInfo.InvariantCulture);
        }
        public static bool TryParse(string address, out GridPoint point)
        {
            point = default;
            if (string.IsNullOrWhiteSpace(address)) return false;
            string value = address.Trim().ToUpperInvariant();
            bool negative = value[0] == '-';
            int index = negative ? 1 : 0, start = index;
            long column = 0;
            while (index < value.Length && value[index] >= 'A' && value[index] <= 'Z')
            {
                column = column * 26 + value[index++] - 'A' + 1;
                if (column > int.MaxValue) return false;
            }
            if (index == start || !int.TryParse(value.Substring(index), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out int row) || row == int.MinValue) return false;
            point = new GridPoint(negative ? -(int)column : (int)column - 1, row - 1);
            return true;
        }
    }

    public sealed class MapPath
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public PathKind Kind { get; set; }
        public WaterSide WaterSide { get; set; } = WaterSide.Left;
        public List<GridPoint> Points { get; set; } = new List<GridPoint>();
    }

    public sealed class MapDocument
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "New region";
        public MapShape Shape { get; set; }
        public int Width { get; set; } = 21;
        public int Height { get; set; } = 15;
        public int Radius { get; set; } = 8;
        public int? Seed { get; set; }
        public string Biome { get; set; } = "res://src/map/biomes/Summer.tres";
        public List<MapPath> Paths { get; set; } = new List<MapPath>();
        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 32,
            Converters = { new StringEnumConverter() }
        };
        public string Serialize() => JsonConvert.SerializeObject(this, JsonSettings);
        public MapDocument Copy() => Deserialize(Serialize());
        public static MapDocument Deserialize(string text)
        {
            if (text.Length > 4000000) throw new ArgumentException("Map document is too large.");
            var result = JsonConvert.DeserializeObject<MapDocument>(text, JsonSettings) ?? throw new ArgumentException("Empty map document.");
            result.Validate();
            return result;
        }
        public void Validate()
        {
            if (Version != 1) throw new ArgumentException("Unsupported map version.");
            if (!Enum.IsDefined(typeof(MapShape), Shape) || Width < 3 || Width > 60 || Height < 3 || Height > 60 || Radius < 1 || Radius > 25)
                throw new ArgumentException("Use dimensions 3–60, or a hex radius 1–25.");
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Biome) || Paths == null || Paths.Count > 500)
                throw new ArgumentException("Invalid map name, biome, or paths.");
            var ids = new HashSet<string>();
            foreach (var path in Paths)
                if (path == null || string.IsNullOrEmpty(path.Id) || !ids.Add(path.Id) ||
                    !Enum.IsDefined(typeof(PathKind), path.Kind) || !Enum.IsDefined(typeof(WaterSide), path.WaterSide) ||
                    path.Points == null || path.Points.Count > 5000 || path.Points.Any(p => Math.Abs((long)p.X) > 200 || Math.Abs((long)p.Y) > 200))
                    throw new ArgumentException("Invalid or duplicate path data.");
        }
        public HashSet<GridPoint> Cells()
        {
            var cells = new HashSet<GridPoint>();
            if (Shape == MapShape.Rectangle)
            {
                for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++) cells.Add(new GridPoint(x, y));
            }
            else
            {
                var center = new GridPoint(Radius, Radius);
                for (int y = 0; y <= Radius * 2; y++)
                for (int x = 0; x <= Radius * 2 + 1; x++)
                    if (HexPathGrid.Distance(center, new GridPoint(x, y)) <= Radius) cells.Add(new GridPoint(x, y));
            }
            return cells;
        }
    }

    public static class HexPathGrid
    {
        public static GridPoint Neighbor(GridPoint p, int direction)
        {
            int odd = p.Y & 1;
            (int x, int y) = direction switch
            {
                0 => (1, 0), 1 => (odd, 1), 2 => (odd - 1, 1),
                3 => (-1, 0), 4 => (odd - 1, -1), _ => (odd, -1)
            };
            return new GridPoint(p.X + x, p.Y + y);
        }
        static Vector3 Cube(GridPoint p)
        {
            int q = p.X - (p.Y - (p.Y & 1)) / 2;
            return new Vector3(q, -q - p.Y, p.Y);
        }
        public static int Distance(GridPoint a, GridPoint b)
        {
            var d = Vector3.Abs(Cube(a) - Cube(b));
            return (int)Math.Max(d.X, Math.Max(d.Y, d.Z));
        }
        public static IEnumerable<GridPoint> Line(GridPoint a, GridPoint b)
        {
            int count = Distance(a, b);
            for (int i = 0; i <= count; i++)
            {
                var cube = Vector3.Lerp(Cube(a), Cube(b), count == 0 ? 0 : (float)i / count) + new Vector3(.00001f, .00001f, -.00002f);
                var rounded = new Vector3((float)Math.Round(cube.X), (float)Math.Round(cube.Y), (float)Math.Round(cube.Z));
                var delta = Vector3.Abs(rounded - cube);
                if (delta.X > delta.Y && delta.X > delta.Z) rounded.X = -rounded.Y - rounded.Z;
                else if (delta.Y > delta.Z) rounded.Y = -rounded.X - rounded.Z;
                else rounded.Z = -rounded.X - rounded.Y;
                int row = (int)rounded.Z;
                yield return new GridPoint((int)rounded.X + (row - (row & 1)) / 2, row);
            }
        }
        public static List<GridPoint> Expand(MapPath path)
        {
            var result = new List<GridPoint>();
            foreach (var point in path.Points)
                if (result.Count == 0) result.Add(point);
                else result.AddRange(Line(result[result.Count - 1], point).Skip(1));
            return result;
        }
    }
}
