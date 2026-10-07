using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace IdleRpg.MapEditing
{
    public sealed class TileDecoration
    {
        public string Model { get; set; }
        public float Yaw { get; set; }
    }

    public sealed class TileSource
    {
        public string Definition { get; set; }
        public SurfaceKind Surface { get; set; }
        public string[] Edges { get; set; }
        public string Model { get; set; }
        public List<TileDecoration> Decorations { get; set; } = new List<TileDecoration>();
    }
    public sealed class TileVariant
    {
        public string Definition { get; }
        public int Rotation { get; }
        public SurfaceKind Surface { get; }
        public string[] Edges { get; }
        public int Road { get; }
        public int River { get; }
        public string Model { get; }
        public IReadOnlyList<TileDecoration> Decorations { get; }
        public TileVariant(TileSource tile, int rotation, string[] edges, int road, int river)
        { Definition = tile.Definition; Rotation = rotation; Surface = tile.Surface; Edges = edges; Road = road; River = river; Model = tile.Model; Decorations = tile.Decorations; }
    }
    public sealed class TileCatalog
    {
        public List<TileSource> Tiles { get; set; }
        public static List<TileVariant> Load(string json)
        {
            var catalog = JsonConvert.DeserializeObject<TileCatalog>(json);
            if (catalog?.Tiles == null) throw new ArgumentException("Missing tile catalog.");
            var variants = new List<TileVariant>();
            foreach (var tile in catalog.Tiles)
            {
                if (tile.Edges == null || tile.Edges.Length != 6 || tile.Edges.Any(e => e.Length != 10 || e.Any(c => !"GSRW".Contains(c))))
                    throw new ArgumentException("Invalid tile edges: " + tile.Definition);
                for (int rotation = 0; rotation < 6; rotation++)
                {
                    var edges = Enumerable.Range(0, 6).Select(d => tile.Edges[(d - rotation + 6) % 6]).ToArray();
                    if (variants.Any(v => v.Definition == tile.Definition && v.Edges.SequenceEqual(edges))) continue;
                    int road = Enumerable.Range(0, 6).Where(d => edges[d].Contains('R')).Sum(d => 1 << d);
                    int river = tile.Surface == SurfaceKind.River ? Enumerable.Range(0, 6).Where(d => edges[d].Contains('W')).Sum(d => 1 << d) : 0;
                    variants.Add(new TileVariant(tile, rotation, edges, road, river));
                }
            }
            return variants;
        }
    }
}
