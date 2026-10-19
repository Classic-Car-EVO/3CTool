using System.Collections.Concurrent;
using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>Shared state and helpers of one toolpath generation run (3-axis working frame).</summary>
internal sealed class StrategyContext
{
    private readonly ConcurrentDictionary<(double Z, bool Part, bool Obstacles), PathsD> _forbidden = new();
    private readonly Lazy<PathsD> _boundary;
    private readonly Lazy<List<double>> _flatTips;
    private readonly IProgress<double>? _progress;

    public StrategyContext(ToolpathRequest request, MachiningSetup setup, ToolpathSettings settings, List<string> warnings, IProgress<double>? progress, CancellationToken token)
    {
        Request = request;
        Setup = setup;
        Settings = settings;
        Warnings = warnings;
        _progress = progress;
        Token = token;
        Tool = ToolAssembly.Create(request.Tool, request.Collet, warnings);
        Stocked = Tool.WithStockToLeave(settings.RadialStock, settings.AxialStock);
        _boundary = new(CreateBoundary);
        _flatTips = new(() =>
        {
            var flats = FlatHeights(Setup.Part);
            if (Setup.Obstacles is { } obstacles)
            {
                flats.AddRange(FlatHeights(obstacles));
            }

            return [.. flats.Select(h => h + Settings.AxialStock)];
        });
    }

    public ToolpathRequest Request { get; }

    public MachiningSetup Setup { get; }

    public ToolpathSettings Settings { get; }

    public List<string> Warnings { get; }

    public CancellationToken Token { get; }

    /// <summary>Real tool assembly.</summary>
    public ToolAssembly Tool { get; }

    /// <summary>Tool assembly grown by the stock to leave: its tip positions keep the stock on the part.</summary>
    public ToolAssembly Stocked { get; }

    public double Radius => Tool.Radius;

    public double Tolerance => Settings.Tolerance;

    public double Clearance => Setup.ClearancePlane(Settings.RetractClearance);

    /// <summary>Fiber spacing of the stocked tool for the operation tolerance.</summary>
    public double FiberSpacing => FiberContours.Spacing(Stocked.Radius, Tolerance);

    /// <summary>Region the tool centre is limited to.</summary>
    public PathsD Boundary => _boundary.Value;

    /// <summary>Lowest tip Z for clearing strategies.</summary>
    public double ClearingFloor => Settings.MinZ ?? Math.Max(Setup.PartBounds.Min.Z, Setup.StockBounds.Min.Z);

    /// <summary>Lowest tip Z for finishing strategies (where nothing is below).</summary>
    public double FinishingFloor => Settings.MinZ ?? Setup.PartBounds.Min.Z;

    public void Report(double fraction) => _progress?.Report(Math.Clamp(fraction, 0, 1));

    public ToolpathBuilder NewBuilder()
    {
        var a = Setup.Frame.BaseAngle;
        return new ToolpathBuilder(Settings, Radius, Clearance, (p, kind) => new ToolpathMove(kind, p.X, p.Y, p.Z, a));
    }

    /// <summary>
    /// Forbidden tip region of the stocked tool at <paramref name="z"/> (cached). Heights on an upward flat face
    /// (where the fibers graze the face and the result is ill-conditioned) are evaluated a hair above it.
    /// </summary>
    public PathsD Forbidden(double z, bool part = true, bool obstacles = true) =>
        _forbidden.GetOrAdd((AboveFlats(z), part, obstacles), key =>
        {
            Token.ThrowIfCancellationRequested();
            var (min, max) = Area();
            return FiberContours.Forbidden(Stocked, Setup, key.Z, min, max, FiberSpacing, key.Part, key.Obstacles);
        });

    private double AboveFlats(double z)
    {
        const double margin = 2e-3;
        foreach (var flat in _flatTips.Value)
        {
            if (Math.Abs(z - flat) < margin)
            {
                return flat + margin;
            }
        }

        return z;
    }

    /// <summary>Tip area of the clearing strategies at <paramref name="z"/>: over the stock, in the boundary, clear of part and keep-outs.</summary>
    public PathsD ClearingRegion(double z)
    {
        var stock = Polygons.Offset(StockAbove(z), 0.9 * Radius, Tolerance);
        return Polygons.Difference(Polygons.Intersect(stock, Boundary), Forbidden(z));
    }

    /// <summary>Stock material just above <paramref name="z"/> (what a level there removes; non-empty at the stock bottom).</summary>
    public PathsD StockAbove(double z) => Setup.StockSection(z + 1e-3);

    /// <summary>Lowest safe tip Z of the stocked tool at XY points (−∞ where nothing is below).</summary>
    public float[] Drop(ReadOnlySpan<Vector2> points)
    {
        Token.ThrowIfCancellationRequested();
        return Stocked.Drop(Setup, points);
    }

    /// <summary>
    /// Evenly spaced levels from <paramref name="top"/> (exclusive) down to <paramref name="bottom"/> (inclusive) with
    /// steps of at most <paramref name="step"/>.
    /// </summary>
    public static List<double> Levels(double top, double bottom, double step)
    {
        var levels = new List<double>();
        if (top - bottom < 1e-6)
        {
            return levels;
        }

        var count = Math.Max(1, (int)Math.Ceiling(((top - bottom) / step) - 1e-9));
        for (var k = 1; k <= count; k++)
        {
            levels.Add(top - ((top - bottom) * k / count));
        }

        return levels;
    }

    /// <summary>
    /// Clearing levels, top down: the even <see cref="Levels"/> plus a level on each flat part face (offset by the axial
    /// stock) that no even level already cuts within the tolerance. Flats closer than the tolerance share one level.
    /// </summary>
    public List<ClearingLevel> ClearingLevels(double top, double bottom, double step)
    {
        var even = Levels(top, bottom, step);
        var levels = even.Select(z => new ClearingLevel(z, null)).ToList();
        double? kept = null;
        foreach (var flat in FlatHeights().Select(h => h + Settings.AxialStock).OrderDescending())
        {
            if (flat > top - Tolerance || flat <= bottom + 1e-3 || flat > kept - Tolerance
                || even.Any(z => z >= flat - 2e-3 && z <= flat + Tolerance))
            {
                continue;
            }

            kept = flat;
            levels.Add(new ClearingLevel(flat, even.First(z => z < flat)));
        }

        levels.Sort((a, b) => b.Z.CompareTo(a.Z));
        return levels;
    }

    /// <summary>
    /// Tip area of a clearing level. A flat level only takes what the even level below it cannot reach (the flat
    /// faces and their surroundings); the rest of its material is cut by that level within the stepdown.
    /// </summary>
    public PathsD LevelRegion(ClearingLevel level)
    {
        var region = ClearingRegion(level.Z);
        return level.Below is { } below
            ? Polygons.Open(Polygons.Difference(region, ClearingRegion(below)), Tolerance, Tolerance)
            : region;
    }

    /// <summary>Heights of upward-facing flat part faces.</summary>
    public List<double> FlatHeights() => FlatHeights(Setup.Part);

    private static List<double> FlatHeights(Mesh mesh)
    {
        var heights = new SortedSet<double>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var n = mesh.FaceNormal(t);
            if (n.Z > 0.999f)
            {
                var (a, _, _) = mesh.GetTriangle(t);
                heights.Add(Math.Round(a.Z, 3));
            }
        }

        var merged = new List<double>();
        foreach (var h in heights)
        {
            if (merged.Count == 0 || h - merged[^1] > 0.005)
            {
                merged.Add(h);
            }
        }

        return merged;
    }

    /// <summary>Sample spacing of surface links: the chord between samples stays within about the tolerance.</summary>
    public double LinkSampleStep => Math.Clamp(Math.Sqrt(2 * Radius * Tolerance), 0.02, Math.Max(Radius / 4, 0.05));

    /// <summary>
    /// Stay-down link over the part surface between two finishing points no farther than <paramref name="maxDistance"/>
    /// apart (XY): straight in XY, lifted to the stocked-tool drop height; null when too far.
    /// </summary>
    public List<P3>? SurfaceLink(P3 from, P3 to, double maxDistance)
    {
        var dxy = (to - from).LengthXY;
        if (dxy > maxDistance)
        {
            return null;
        }

        var count = Math.Max(1, (int)Math.Ceiling(dxy / LinkSampleStep));
        if (count == 1)
        {
            return [];
        }

        var xy = new Vector2[count - 1];
        for (var k = 1; k < count; k++)
        {
            var p = P3.Lerp(from, to, (double)k / count);
            xy[k - 1] = new Vector2((float)p.X, (float)p.Y);
        }

        var drop = Drop(xy);
        var link = new List<P3>(count - 1);
        for (var k = 1; k < count; k++)
        {
            var p = P3.Lerp(from, to, (double)k / count);
            link.Add(p with { Z = Math.Max(p.Z, Math.Max(drop[k - 1], FinishingFloor)) });
        }

        return link;
    }

    /// <summary>Plain XY area for fiber grids: boundary bounds plus the widest stage and a margin.</summary>
    private (Vector2 Min, Vector2 Max) Area()
    {
        var margin = Stocked.MaxRadius + (2 * FiberSpacing);
        var bounds = Clipper.GetBounds(Boundary);
        var part = Setup.PartBounds;
        var obstacles = Setup.Obstacles?.Bounds ?? part;
        var minX = Math.Max(bounds.left, Math.Min(part.Min.X, obstacles.Min.X)) - margin;
        var minY = Math.Max(bounds.top, Math.Min(part.Min.Y, obstacles.Min.Y)) - margin;
        var maxX = Math.Min(bounds.right, Math.Max(part.Max.X, obstacles.Max.X)) + margin;
        var maxY = Math.Min(bounds.bottom, Math.Max(part.Max.Y, obstacles.Max.Y)) + margin;
        return (new Vector2((float)minX, (float)minY), new Vector2((float)maxX, (float)maxY));
    }

    private PathsD CreateBoundary()
    {
        var offset = Settings.BoundaryOffset;
        return Settings.Boundary == BoundaryKind.PartSilhouette
            ? Polygons.Offset(Setup.PartSilhouette, offset, Tolerance)
            : Polygons.Offset(Setup.StockFootprint, Radius + offset, Tolerance);
    }
}

/// <summary>A clearing height; a flat level names the even level <paramref name="Below"/> that clears the rest under it.</summary>
internal readonly record struct ClearingLevel(double Z, double? Below)
{
    public bool IsFlat => Below is not null;
}
