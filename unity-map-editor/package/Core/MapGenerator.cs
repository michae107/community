using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace IdleRpg.MapEditing
{

public sealed class GeneratedMap
{
    public MapDocument Document { get; }
    public MapSolution Solution { get; }
    public GeneratedMap(MapDocument document, MapSolution solution) { Document = document; Solution = solution; }
}

/// <summary>Seeded path layouts checked against the same tile definitions used by hand editing.</summary>
public static class MapGenerator
{
    public static GeneratedMap GenerateSolved(int seed, IReadOnlyList<TileVariant> catalog, CancellationToken token)
    {
        for (int attempt = 0; attempt < 24; attempt++)
        {
            token.ThrowIfCancellationRequested();
            MapDocument document = Generate(seed, attempt);
            try { return new GeneratedMap(document, MapSolver.Solve(document, catalog, token)); }
            catch (InvalidOperationException) when (attempt < 23) { }
        }
        throw new InvalidOperationException("Could not generate a compatible layout for this seed.");
    }

    public static MapDocument Generate(int seed, int attempt = 0)
    {
        var random = new Random(unchecked(seed + attempt * 104729));
        int width = random.Next(23, 30);
        int height = 15 + 2 * random.Next(4);
        int crossing = random.Next(4, height - 4);
        int riverCenter = random.Next(9, 13);
        int shore = Math.Max(riverCenter + 7, width - random.Next(4, 6));
        var document = new MapDocument { Name = "Springtide Reach", Width = width, Height = height, Seed = seed };
        void Add(PathKind kind, IEnumerable<GridPoint> points)
            => document.Paths.Add(new MapPath
            {
                Id = $"generated-{seed}-{document.Paths.Count}", Kind = kind, Points = points.ToList()
            });

        var river = new List<GridPoint> { new(riverCenter, 0) };
        int previousDirection = 1;
        for (int y = 1; y < height; y++)
        {
            GridPoint previous = river[^1];
            double target = riverCenter + Math.Sin(y * 0.38 + seed % 17) * 1.6;
            int direction = random.NextDouble() < (previous.X < target ? 0.72 : 0.28) ? 1 : 2;
            if (y == crossing + 1) direction = previousDirection;
            river.Add(HexPathGrid.Neighbor(previous, direction));
            previousDirection = direction;
        }
        Add(PathKind.River, river);
        int roadEnd = shore - 3;
        Add(PathKind.Road, new[] { new GridPoint(1 + random.Next(2), crossing), new GridPoint(roadEnd, crossing) });
        int inlandLimit = river.Min(p => p.X) - 3;
        foreach (int x in new[] { 3, Math.Max(5, inlandLimit - 1) }.Distinct())
        foreach (bool north in new[] { true, false })
        {
            if (random.NextDouble() < 0.22) continue;
            var branch = new List<GridPoint> { new(x, crossing) };
            int length = random.Next(2, north ? crossing : height - crossing - 1);
            for (int i = 0; i < length; i++)
            {
                GridPoint end = branch[^1];
                int direction = north ? random.Next(4, 6) : random.Next(1, 3);
                GridPoint next = HexPathGrid.Neighbor(end, direction);
                if (next.X < 1 || next.X > inlandLimit || next.Y < 1 || next.Y >= height - 1) break;
                branch.Add(next);
            }
            if (branch.Count > 1) Add(PathKind.Road, branch);
        }
        var coast = new List<GridPoint>();
        for (int y = 0; y < height; y += 3)
            coast.Add(new GridPoint(Math.Max(Math.Min(river.Max(p => p.X) + 5, width - 3), Math.Min(width - 3, shore + random.Next(-1, 2))), y));
        if (coast[^1].Y != height - 1) coast.Add(new GridPoint(coast[^1].X, height - 1));
        Add(PathKind.Coast, coast);
        bool mirror = random.Next(2) == 0;
        bool flip = random.Next(2) == 0;
        foreach (MapPath path in document.Paths)
        {
            path.Points = path.Points.Select(p => new GridPoint(
                mirror ? width - 1 - p.X - (p.Y & 1) : p.X,
                flip ? height - 1 - p.Y : p.Y)).ToList();
            path.WaterSide = mirror != flip ? WaterSide.Right : WaterSide.Left;
        }
        return document;
    }
}
}
