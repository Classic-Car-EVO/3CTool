using System.Numerics;

using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;

namespace ThreeCTool.Compute;

/// <summary>A horizontal fiber at height <c>Start.Z</c> running from Start along a unit XY direction.</summary>
public readonly record struct Fiber(Vector3 Start, Vector2 Direction, float Length)
{
    public Vector3 PointAt(float t) => Start + (new Vector3(Direction, 0) * t);
}

/// <summary>A blocked range along a fiber, in distance from the fiber start.</summary>
public readonly record struct FiberInterval(float Enter, float Exit);

/// <summary>
/// Push-cutter (waterline) queries: along each fiber, where the cutter tip may not be without the cutter or its
/// (infinitely tall) cylinder intersecting the mesh. Interval ends are the waterline contour crossings.
/// </summary>
public static class PushCutter
{
    private const int MaxSplitDepth = 8;

    public static FiberInterval[][] Run(GpuMesh mesh, CutterProfile cutter, ReadOnlySpan<Fiber> fibers)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var result = new FiberInterval[fibers.Length][];
        var pending = new List<(int Fiber, float From, float To, int Depth)>(fibers.Length);
        var parts = new List<FiberInterval>[fibers.Length];

        // Long fibers are cut into segments: more threads in flight and tighter BVH culling per thread.
        var segment = MathF.Max(4 * cutter.Radius, 8f);
        for (var i = 0; i < fibers.Length; i++)
        {
            var length = fibers[i].Length;
            var pieces = Math.Max(1, (int)MathF.Ceiling(length / segment));
            for (var p = 0; p < pieces; p++)
            {
                pending.Add((i, length * p / pieces, length * (p + 1) / pieces, 0));
            }

            parts[i] = [];
        }

        var fiberArray = fibers.ToArray();

        // Fibers whose interval list overflows are split in half and recomputed; at the depth limit the
        // (conservatively merged) result is accepted.
        while (pending.Count > 0)
        {
            var (intervals, overflowed) = Launch(mesh, cutter, fiberArray, pending);
            var retry = new List<(int Fiber, float From, float To, int Depth)>();
            for (var p = 0; p < pending.Count; p++)
            {
                var (fiber, from, to, depth) = pending[p];
                if (overflowed[p] && depth < MaxSplitDepth)
                {
                    var middle = 0.5f * (from + to);
                    retry.Add((fiber, from, middle, depth + 1));
                    retry.Add((fiber, middle, to, depth + 1));
                }
                else
                {
                    foreach (var interval in intervals[p])
                    {
                        parts[fiber].Add(new FiberInterval(interval.Enter + from, interval.Exit + from));
                    }
                }
            }

            pending = retry;
        }

        for (var i = 0; i < fibers.Length; i++)
        {
            result[i] = Merge(parts[i]);
        }

        return result;
    }

    /// <summary>
    /// Waterline fiber grid at height <paramref name="z"/>: X fibers (one per Y row) and Y fibers (one per X column)
    /// covering <paramref name="min"/>..<paramref name="max"/> at <paramref name="spacing"/>.
    /// </summary>
    public static (Fiber[] XFibers, FiberInterval[][] XIntervals, Fiber[] YFibers, FiberInterval[][] YIntervals) Grid(
        GpuMesh mesh, CutterProfile cutter, float z, Vector2 min, Vector2 max, float spacing)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacing);
        var rows = (int)MathF.Floor((max.Y - min.Y) / spacing) + 1;
        var columns = (int)MathF.Floor((max.X - min.X) / spacing) + 1;
        var xFibers = new Fiber[rows];
        for (var j = 0; j < rows; j++)
        {
            xFibers[j] = new Fiber(new Vector3(min.X, min.Y + (j * spacing), z), Vector2.UnitX, max.X - min.X);
        }

        var yFibers = new Fiber[columns];
        for (var i = 0; i < columns; i++)
        {
            yFibers[i] = new Fiber(new Vector3(min.X + (i * spacing), min.Y, z), Vector2.UnitY, max.Y - min.Y);
        }

        Fiber[] all = [.. xFibers, .. yFibers];
        var intervals = Run(mesh, cutter, all);
        return (xFibers, intervals[..rows], yFibers, intervals[rows..]);
    }

    private static (FiberInterval[][] Intervals, bool[] Overflowed) Launch(
        GpuMesh mesh, CutterProfile cutter, Fiber[] fibers, List<(int Fiber, float From, float To, int Depth)> pending)
    {
        var compute = mesh.Compute;
        var count = pending.Count;
        var starts = new Float3[count];
        var directions = new Float3[count];
        for (var p = 0; p < count; p++)
        {
            var (index, from, to, _) = pending[p];
            var fiber = fibers[index];
            var direction = Vector2.Normalize(fiber.Direction);
            starts[p] = Float3.From(fiber.Start + (new Vector3(direction, 0) * from));
            directions[p] = new Float3(direction.X, direction.Y, to - from);
        }

        var intervals = new FiberInterval[count][];
        var overflowed = new bool[count];
        var stride = 2 * PushCutterKernel.Capacity;
        var batch = Math.Min(count, compute.BatchSize(28 + (4 * stride)));
        var accelerator = compute.Accelerator;
        var hostIntervals = new float[batch * stride];
        var hostCounts = new int[batch];
        lock (compute.Gate)
        {
            using var startBuffer = accelerator.Allocate1D<Float3>(Math.Max(batch, 1));
            using var directionBuffer = accelerator.Allocate1D<Float3>(Math.Max(batch, 1));
            using var intervalBuffer = accelerator.Allocate1D<float>(Math.Max(batch, 1) * stride);
            using var countBuffer = accelerator.Allocate1D<int>(Math.Max(batch, 1));
            var kernel = compute.Kernel<PushCutterArgs>(PushCutterKernel.Run);
            for (var first = 0; first < count; first += batch)
            {
                var n = Math.Min(batch, count - first);
                compute.Upload(startBuffer.View, starts.AsSpan(first, n));
                compute.Upload(directionBuffer.View, directions.AsSpan(first, n));
                kernel(n, new PushCutterArgs
                {
                    Mesh = mesh.View,
                    Cutter = cutter,
                    Starts = startBuffer.View,
                    Directions = directionBuffer.View,
                    Intervals = intervalBuffer.View,
                    Counts = countBuffer.View,
                });
                compute.Download(intervalBuffer.View, hostIntervals.AsSpan(0, n * stride));
                compute.Download(countBuffer.View, hostCounts.AsSpan(0, n));
                for (var k = 0; k < n; k++)
                {
                    var c = hostCounts[k];
                    overflowed[first + k] = c < 0;
                    c = Math.Abs(c);
                    var list = new FiberInterval[c];
                    for (var m = 0; m < c; m++)
                    {
                        list[m] = new FiberInterval(hostIntervals[(k * stride) + (2 * m)], hostIntervals[(k * stride) + (2 * m) + 1]);
                    }

                    intervals[first + k] = list;
                }
            }
        }

        return (intervals, overflowed);
    }

    private static FiberInterval[] Merge(List<FiberInterval> parts)
    {
        if (parts.Count <= 1)
        {
            return [.. parts];
        }

        parts.Sort((a, b) => a.Enter.CompareTo(b.Enter));
        var merged = new List<FiberInterval> { parts[0] };
        for (var i = 1; i < parts.Count; i++)
        {
            var last = merged[^1];
            if (parts[i].Enter <= last.Exit + 1e-6f)
            {
                merged[^1] = last with { Exit = MathF.Max(last.Exit, parts[i].Exit) };
            }
            else
            {
                merged.Add(parts[i]);
            }
        }

        return [.. merged];
    }
}
