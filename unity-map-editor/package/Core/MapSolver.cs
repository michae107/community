using System.Numerics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace IdleRpg.MapEditing
{

public sealed class SolvedTile
{
    public GridPoint Point { get; }
    public TileVariant Variant { get; }
    public SolvedTile(GridPoint point, TileVariant variant) { Point = point; Variant = variant; }
}
public sealed class MapSolution
{
    public List<SolvedTile> Tiles { get; }
    public int MatchedEdges { get; }
    public MapSolution(List<SolvedTile> tiles, int matchedEdges) { Tiles = tiles; MatchedEdges = matchedEdges; }
}

/// <summary>Turns tile-center paths into exact edge constraints, then solves compatible tile rotations.</summary>
public static class MapSolver
{
    private sealed class Cell
    {
        public int Road;
        public int River;
        public float WaterDistance = float.NegativeInfinity;
        public List<int> Options = new List<int>();
    }

    public static MapSolution Solve(MapDocument document, IReadOnlyList<TileVariant> variants,
        CancellationToken cancellation = default)
    {
        document.Validate();
        var clock = Stopwatch.StartNew();
        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds > 12)
                throw new InvalidOperationException("Solve limit reached. Simplify the paths or reduce the map size.");
        }
        var cells = document.Cells().ToDictionary(p => p, _ => new Cell());
        if (document.Paths.Count == 0)
        {
            TileVariant grass = variants.FirstOrDefault(variant =>
                variant.Definition.EndsWith("/Base/GrassHex.tres", StringComparison.Ordinal) && variant.Rotation == 0)
                ?? throw new InvalidOperationException("The plain grass tile is missing from the tile catalog.");
            var tiles = new List<SolvedTile>();
            int connections = 0;
            foreach (GridPoint point in cells.Keys.OrderBy(point => point.Y).ThenBy(point => point.X))
            {
                Check();
                tiles.Add(new SolvedTile(point, grass));
                connections += Enumerable.Range(0, 6).Count(direction => cells.ContainsKey(HexPathGrid.Neighbor(point, direction)));
            }
            return new MapSolution(tiles, connections / 2);
        }
        var shoreSegments = new List<(Vector2 A, Vector2 B, WaterSide Side)>();
        foreach (MapPath path in document.Paths)
        {
            List<GridPoint> points = HexPathGrid.Expand(path);
            for (int i = 1; i < points.Count; i++)
            {
                if (points[i - 1] == points[i]) continue;
                if (path.Kind == PathKind.Coast)
                {
                    Vector3 a = points[i - 1].Position;
                    Vector3 b = points[i].Position;
                    shoreSegments.Add((new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), path.WaterSide));
                    continue;
                }
                int direction = Enumerable.Range(0, 6).Single(d => HexPathGrid.Neighbor(points[i - 1], d) == points[i]);
                AddEdge(points[i - 1], direction, path.Kind);
                AddEdge(points[i], (direction + 3) % 6, path.Kind);
            }
            // The pack has no river source/endcap. At the map boundary continue the river off-map.
            if (path.Kind == PathKind.River && points.Count > 1)
            {
                ExtendBoundary(points[0], points[1]);
                ExtendBoundary(points[^1], points[^2]);
            }
        }

        void AddEdge(GridPoint point, int direction, PathKind kind)
        {
            if (!cells.TryGetValue(point, out Cell cell)) return;
            if (kind == PathKind.Road) cell.Road |= 1 << direction;
            else cell.River |= 1 << direction;
        }

        void ExtendBoundary(GridPoint end, GridPoint previous)
        {
            if (!cells.ContainsKey(end)) return;
            int incoming = Enumerable.Range(0, 6).Single(d => HexPathGrid.Neighbor(end, d) == previous);
            int outward = Enumerable.Range(0, 6).Where(d => !cells.ContainsKey(HexPathGrid.Neighbor(end, d)))
                .OrderBy(d => Math.Abs(Wrap(d - (incoming + 3), -3, 3))).DefaultIfEmpty(-1).First();
            if (outward >= 0) AddEdge(end, outward, PathKind.River);
        }

        foreach (var (point, cell) in cells)
        {
            Check();
            Vector2 position = new(point.Position.X, point.Position.Z);
            float nearest = float.PositiveInfinity;
            foreach (var (a, b, side) in shoreSegments)
            {
                Vector2 tangent = b - a;
                float t = Math.Max(0, Math.Min(1, Vector2.Dot(position - a, tangent) / tangent.LengthSquared()));
                float distance = Vector2.Distance(position, a + tangent * t);
                if (distance >= nearest) continue;
                nearest = distance;
                // XZ has south-positive Z; the geometric left side is the negative cross product.
                float cross = tangent.X * (position.Y - a.Y) - tangent.Y * (position.X - a.X);
                bool water = side == WaterSide.Left ? cross < 0 : cross > 0;
                cell.WaterDistance = distance * (water ? 1 : -1);
            }
            bool hasFeature = cell.Road != 0 || cell.River != 0;
            cell.Options = Enumerable.Range(0, variants.Count).Where(i =>
            {
                TileVariant v = variants[i];
                if (v.Road != cell.Road || v.River != cell.River) return false;
                if (hasFeature) return cell.WaterDistance < 1.6f;
                if (cell.WaterDistance > 2.3f) return v.Surface == SurfaceKind.Ocean;
                if (cell.WaterDistance < -2.3f) return v.Surface == SurfaceKind.Land;
                return v.Surface is SurfaceKind.Land or SurfaceKind.Coast or SurfaceKind.Ocean;
            }).ToList();
            if (cell.Options.Count == 0)
                throw new InvalidOperationException(cell.WaterDistance >= 1.6f && hasFeature
                    ? $"{point}: roads and rivers cannot cross open ocean. Move the path inland."
                    : cell.River != 0 && (cell.River & (cell.River - 1)) == 0
                        ? $"{point}: rivers must connect to another river or reach the map edge; this pack has no endcaps."
                        : $"{point}: no matching tile for this bend or crossing. Try a gentler bend or a straight river crossing.");
        }

        string Reverse(string value) => new(value.Reverse().ToArray());
        var reverseEdges = variants.Select(v => v.Edges.Select(Reverse).ToArray()).ToArray();
        bool Propagate(Dictionary<GridPoint, List<int>> wave, IEnumerable<GridPoint> changed)
        {
            var queue = new Queue<GridPoint>(changed);
            var queued = queue.ToHashSet();
            while (queue.TryDequeue(out GridPoint point))
            {
                Check();
                queued.Remove(point);
                for (int direction = 0; direction < 6; direction++)
                {
                    GridPoint neighbor = HexPathGrid.Neighbor(point, direction);
                    if (!wave.TryGetValue(neighbor, out List<int> options)) continue;
                    var allowed = wave[point].Select(i => variants[i].Edges[direction]).ToHashSet();
                    int removed = options.RemoveAll(i => !allowed.Contains(reverseEdges[i][(direction + 3) % 6]));
                    if (options.Count == 0) return false;
                    if (removed > 0 && queued.Add(neighbor)) queue.Enqueue(neighbor);
                }
            }
            return true;
        }

        var initial = cells.ToDictionary(p => p.Key, p => p.Value.Options.ToList());
        if (!Propagate(initial, initial.Keys))
            throw new InvalidOperationException("These paths need incompatible neighboring tiles. Move the crossing or shoreline points farther apart.");
        var random = new Random(document.Seed ?? 0);
        Dictionary<GridPoint, List<int>> solved = null;
        for (int attempt = 0; attempt < 80 && solved is null; attempt++)
        {
            var wave = initial.ToDictionary(p => p.Key, p => p.Value.ToList());
            bool valid = true;
            while (wave.Any(p => p.Value.Count > 1))
            {
                Check();
                var next = wave.Where(p => p.Value.Count > 1)
                    .OrderBy(p => p.Value.Count + random.NextDouble() * 0.1).First();
                double Weight(int index)
                {
                    var variant = variants[index];
                    float distance = cells[next.Key].WaterDistance;
                    if (variant.Surface == SurfaceKind.Ocean) return distance > 0.6f ? 8 : 0.15;
                    if (variant.Surface == SurfaceKind.Land) return distance < -0.6f ? 8 : 0.15;
                    return 3;
                }
                double draw = random.NextDouble() * next.Value.Sum(Weight);
                int choice = next.Value[^1];
                foreach (int option in next.Value)
                {
                    draw -= Weight(option);
                    if (draw <= 0) { choice = option; break; }
                }
                wave[next.Key] = new List<int> { choice };
                if (!Propagate(wave, new[] { next.Key })) { valid = false; break; }
            }
            if (valid) solved = wave;
        }
        if (solved is null) throw new InvalidOperationException("No compatible solution found. Adjust the nearby path bends and try again.");
        int matched = 0;
        foreach (var (point, options) in solved)
        for (int direction = 0; direction < 6; direction++)
        {
            if (!solved.TryGetValue(HexPathGrid.Neighbor(point, direction), out var neighbor)) continue;
            if (variants[options[0]].Edges[direction] != reverseEdges[neighbor[0]][(direction + 3) % 6])
                throw new InvalidOperationException($"Edge mismatch at {point}.");
            matched++;
        }
        return new MapSolution(solved.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X)
            .Select(p => new SolvedTile(p.Key, variants[p.Value[0]])).ToList(), matched / 2);
    }
    static int Wrap(int value, int min, int max)
    {
        int range = max - min;
        return ((value - min) % range + range) % range + min;
    }
}
}
