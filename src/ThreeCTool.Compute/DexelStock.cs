using System.Numerics;

using ILGPU;
using ILGPU.Runtime;

using ThreeCTool.Compute.Kernels;
using ThreeCTool.Geometry;

namespace ThreeCTool.Compute;

/// <summary>Dexel ray direction. Z rays lie over (X, Y), X rays over (Y, Z), Y rays over (Z, X).</summary>
public enum DexelAxis
{
    Z = 0,
    X = 1,
    Y = 2,
}

/// <summary>
/// Straight move of the cutter tip (part frame) with a fixed tool axis pointing from the tip towards the spindle.
/// Simultaneous rotary moves must be split into short straight pieces by the caller (the tip path is an arc).
/// </summary>
public readonly record struct ToolMove(Vector3 From, Vector3 To, Vector3 Axis)
{
    public ToolMove(Vector3 from, Vector3 to)
        : this(from, to, Vector3.UnitZ)
    {
    }
}

/// <summary>Stock surface point with its signed distance to the part (negative = gouge, positive = leftover).</summary>
public readonly record struct DeviationSample(Vector3 Point, float Distance);

/// <summary>Host copy of one dexel direction.</summary>
public sealed class DexelGrid
{
    private readonly float[] _segments;
    private readonly int[] _counts;

    internal DexelGrid(DexelAxis axis, int countU, int countV, float originU, float originV, float resolution, int capacity, float[] segments, int[] counts)
    {
        Axis = axis;
        CountU = countU;
        CountV = countV;
        OriginU = originU;
        OriginV = originV;
        Resolution = resolution;
        Capacity = capacity;
        _segments = segments;
        _counts = counts;
    }

    public DexelAxis Axis { get; }

    public int CountU { get; }

    public int CountV { get; }

    public float OriginU { get; }

    public float OriginV { get; }

    public float Resolution { get; }

    public int Capacity { get; }

    /// <summary>Per ray (index iv·CountU + iu): Capacity (start, end) pairs; only the first Counts[ray] are valid.</summary>
    public IReadOnlyList<float> Segments => _segments;

    public IReadOnlyList<int> Counts => _counts;

    public ReadOnlySpan<float> Ray(int iu, int iv)
    {
        var ray = (iv * CountU) + iu;
        return _segments.AsSpan(ray * 2 * Capacity, 2 * _counts[ray]);
    }

    public Vector2 RayCenter(int iu, int iv) => new(OriginU + ((iu + 0.5f) * Resolution), OriginV + ((iv + 0.5f) * Resolution));

    /// <summary>Point on ray (iu, iv) at w in the part frame.</summary>
    public Vector3 Point(int iu, int iv, float w)
    {
        var c = RayCenter(iu, iv);
        return DexelKernel.Compose((int)Axis, c.X, c.Y, w).ToVector3();
    }
}

/// <summary>
/// Tri-dexel stock on the GPU: three orthogonal ray grids, each ray a sorted list of up to Capacity material
/// segments. Material is removed by cutter sweeps and added by deposited beads. When a ray runs out of segment slots
/// the smallest change is skipped (material kept) and <see cref="OverflowCount"/> increases.
/// </summary>
public sealed class DexelStock : IDisposable
{
    public const int DefaultCapacity = 8;
    private const int TileSize = 16;
    private const int MaxTileEntries = 1 << 23;

    private readonly Grid[] _grids;
    private readonly MemoryBuffer1D<int, Stride1D.Dense> _overflow;

    private DexelStock(ComputeContext compute, BoundingBox bounds, float resolution, int capacity)
    {
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("Stock bounds are empty.", nameof(bounds));
        }

        Compute = compute;
        Bounds = bounds;
        Resolution = resolution;
        Capacity = capacity;
        var size = bounds.Size;
        var counts = new[]
        {
            Math.Max(1, (int)MathF.Ceiling(size.X / resolution)),
            Math.Max(1, (int)MathF.Ceiling(size.Y / resolution)),
            Math.Max(1, (int)MathF.Ceiling(size.Z / resolution)),
        };
        var rays = ((long)counts[0] * counts[1]) + ((long)counts[1] * counts[2]) + ((long)counts[2] * counts[0]);
        var bytes = rays * ((8L * capacity) + 4);
        if (bytes > compute.Device.MemoryBytes / 2)
        {
            throw new InvalidOperationException($"Dexel stock needs {bytes >> 20} MB; increase the resolution value or reduce the capacity.");
        }

        _grids = new Grid[3];
        try
        {
            for (var axis = 0; axis < 3; axis++)
            {
                _grids[axis] = new Grid(compute, axis, bounds, resolution, capacity, counts);
            }

            _overflow = compute.Accelerator.Allocate1D<int>(1);
            _overflow.MemSetToZero();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ComputeContext Compute { get; }

    public BoundingBox Bounds { get; }

    public float Resolution { get; }

    public int Capacity { get; }

    /// <summary>Number of ray updates that did not fit the segment capacity (material was kept).</summary>
    public long OverflowCount
    {
        get
        {
            var value = new int[1];
            lock (Compute.Gate)
            {
                Compute.Download(_overflow.View, value);
            }

            return value[0];
        }
    }

    /// <summary>Test hook: route every ray through the general solver.</summary>
    internal bool ForceGeneralSolver { get; set; }

    internal DexelGridView GridView(DexelAxis axis) => _grids[(int)axis].View;

    public static DexelStock Empty(ComputeContext compute, BoundingBox bounds, float resolution, int capacity = DefaultCapacity) =>
        new(compute, bounds, resolution, capacity);

    /// <summary>Rectangular block filling <paramref name="bounds"/>.</summary>
    public static DexelStock Box(ComputeContext compute, BoundingBox bounds, float resolution, int capacity = DefaultCapacity)
    {
        var stock = new DexelStock(compute, bounds, resolution, capacity);
        foreach (var grid in stock._grids)
        {
            grid.Fill(compute, (_, _) => (grid.MinW, grid.MaxW));
        }

        return stock;
    }

    /// <summary>Round bar on the A axis (X axis through Y = Z = 0) from <paramref name="x0"/> to <paramref name="x1"/>.</summary>
    public static DexelStock Cylinder(ComputeContext compute, float x0, float x1, float radius, float resolution, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        var bounds = new BoundingBox(new Vector3(MathF.Min(x0, x1), -radius, -radius), new Vector3(MathF.Max(x0, x1), radius, radius));
        var stock = new DexelStock(compute, bounds, resolution, capacity);
        var r2 = radius * radius;
        stock._grids[0].Fill(compute, (x, y) => Chord(r2, y, out var h) ? (-h, h) : null);
        stock._grids[1].Fill(compute, (y, z) => (y * y) + (z * z) <= r2 ? (bounds.Min.X, bounds.Max.X) : null);
        stock._grids[2].Fill(compute, (z, x) => Chord(r2, z, out var h) ? (-h, h) : null);
        return stock;

        static bool Chord(float r2, float offset, out float half)
        {
            half = MathF.Sqrt(MathF.Max(r2 - (offset * offset), 0));
            return offset * offset < r2;
        }
    }

    /// <summary>Stock shaped like a closed mesh (e.g. a casting or a printed part), sampled inside <paramref name="bounds"/>.</summary>
    public static DexelStock FromMesh(GpuMesh mesh, BoundingBox bounds, float resolution, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var stock = new DexelStock(mesh.Compute, bounds, resolution, capacity);
        var compute = mesh.Compute;
        lock (compute.Gate)
        {
            var kernel = compute.Kernel<DexelMeshArgs>(DexelKernel.FromMesh);
            foreach (var grid in stock._grids)
            {
                kernel(grid.Rays, new DexelMeshArgs { Grid = grid.View, Mesh = mesh.View, Overflow = stock._overflow.View });
            }

            compute.Accelerator.Synchronize();
        }

        return stock;
    }

    /// <summary>Removes the material swept by <paramref name="cutter"/> along <paramref name="moves"/>.</summary>
    public void Remove(CutterProfile cutter, ReadOnlySpan<ToolMove> moves)
    {
        if (cutter.Height <= 0)
        {
            throw new ArgumentException("The cutter needs a cutting height for material removal.", nameof(cutter));
        }

        Sweep(cutter, moves, 0, add: false);
    }

    /// <summary>
    /// Adds extruded beads: the moves are nozzle tip positions; each bead is a flat cylinder of
    /// <paramref name="width"/> and <paramref name="height"/> directly below the tip along the tool axis.
    /// </summary>
    public void Deposit(ReadOnlySpan<ToolMove> moves, float width, float height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Sweep(CutterProfile.Flat(width, height), moves, height, add: true);
    }

    public DexelGrid Read(DexelAxis axis)
    {
        var grid = _grids[(int)axis];
        var segments = new float[grid.Rays * 2 * Capacity];
        var counts = new int[grid.Rays];
        lock (Compute.Gate)
        {
            Compute.Download(grid.Segments.View, segments);
            Compute.Download(grid.Counts.View, counts);
        }

        return new DexelGrid(axis, grid.CountU, grid.CountV, grid.OriginU, grid.OriginV, Resolution, Capacity, segments, counts);
    }

    /// <summary>Top of the material per Z ray (row-major over X, Y); NaN where the ray is empty.</summary>
    public float[] Heightmap()
    {
        var grid = Read(DexelAxis.Z);
        var result = new float[grid.CountU * grid.CountV];
        for (var ray = 0; ray < result.Length; ray++)
        {
            var count = grid.Counts[ray];
            result[ray] = count > 0 ? grid.Segments[(ray * 2 * Capacity) + (2 * count) - 1] : float.NaN;
        }

        return result;
    }

    /// <summary>Material volume from the Z rays (mm³).</summary>
    public double Volume()
    {
        var grid = Read(DexelAxis.Z);
        double length = 0;
        for (var ray = 0; ray < grid.Counts.Count; ray++)
        {
            for (var k = 0; k < grid.Counts[ray]; k++)
            {
                var offset = (ray * 2 * Capacity) + (2 * k);
                length += grid.Segments[offset + 1] - grid.Segments[offset];
            }
        }

        return length * Resolution * Resolution;
    }

    /// <summary>Occupancy of the Z rays at height <paramref name="z"/> (row-major over X, Y).</summary>
    public bool[] SliceZ(float z)
    {
        var grid = Read(DexelAxis.Z);
        var result = new bool[grid.CountU * grid.CountV];
        for (var ray = 0; ray < result.Length; ray++)
        {
            for (var k = 0; k < grid.Counts[ray]; k++)
            {
                var offset = (ray * 2 * Capacity) + (2 * k);
                result[ray] |= grid.Segments[offset] <= z && z <= grid.Segments[offset + 1];
            }
        }

        return result;
    }

    /// <summary>All segment end points of the three grids: a point sampling of the stock surface.</summary>
    public Vector3[] SurfacePoints()
    {
        var points = new List<Vector3>();
        for (var axis = 0; axis < 3; axis++)
        {
            var grid = Read((DexelAxis)axis);
            for (var iv = 0; iv < grid.CountV; iv++)
            {
                for (var iu = 0; iu < grid.CountU; iu++)
                {
                    foreach (var w in grid.Ray(iu, iv))
                    {
                        points.Add(grid.Point(iu, iv, w));
                    }
                }
            }
        }

        return [.. points];
    }

    /// <summary>Signed distance of every stock surface point to the (closed) part mesh.</summary>
    public DeviationSample[] Deviation(GpuMesh part, float maxDistance = float.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(part);
        var points = SurfacePoints();
        var distances = MeshQueries.SignedDistance(part, points, maxDistance);
        var result = new DeviationSample[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            result[i] = new DeviationSample(points[i], distances[i].Distance);
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var grid in _grids)
        {
            grid?.Dispose();
        }

        _overflow?.Dispose();
    }

    private void Sweep(CutterProfile cutter, ReadOnlySpan<ToolMove> moves, float tipOffset, bool add)
    {
        var data = new List<Float3>(moves.Length * 5);
        var boxes = new List<(Vector3 Min, Vector3 Max)>(moves.Length);
        foreach (var move in moves)
        {
            var axis = Vector3.Normalize(move.Axis);
            if (!float.IsFinite(axis.X) || !float.IsFinite(axis.Y) || !float.IsFinite(axis.Z))
            {
                throw new ArgumentException("Tool axis must be non-zero.", nameof(moves));
            }

            var start = move.From - (axis * tipOffset);
            var end = move.To - (axis * tipOffset);
            var top = axis * cutter.Height;
            var extent = cutter.Radius * new Vector3(
                MathF.Sqrt(MathF.Max(1 - (axis.X * axis.X), 0)),
                MathF.Sqrt(MathF.Max(1 - (axis.Y * axis.Y), 0)),
                MathF.Sqrt(MathF.Max(1 - (axis.Z * axis.Z), 0)));

            // Long moves are split: pieces keep float precision local and their boxes tight.
            var pieces = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(start, end) / MathF.Max(2 * cutter.Radius, 1f)));
            for (var i = 0; i < pieces; i++)
            {
                var from = Vector3.Lerp(start, end, (float)i / pieces);
                var to = i == pieces - 1 ? end : Vector3.Lerp(start, end, (float)(i + 1) / pieces);
                var min = Vector3.Min(Vector3.Min(from, to), Vector3.Min(from + top, to + top)) - extent - new Vector3(1e-4f);
                var max = Vector3.Max(Vector3.Max(from, to), Vector3.Max(from + top, to + top)) + extent + new Vector3(1e-4f);
                if (new BoundingBox(min, max).Intersects(Bounds))
                {
                    data.Add(Float3.From(from));
                    data.Add(Float3.From(to - from));
                    data.Add(Float3.From(axis));
                    data.Add(Float3.From(min));
                    data.Add(Float3.From(max));
                    boxes.Add((min, max));
                }
            }
        }

        if (boxes.Count == 0)
        {
            return;
        }

        var moveData = data.ToArray();
        lock (Compute.Gate)
        {
            using var moveBuffer = Compute.Accelerator.Allocate1D<Float3>(moveData.Length);
            Compute.Upload(moveBuffer.View, moveData);
            var kernel = Compute.Kernel<DexelSweepArgs>(DexelKernel.Sweep);
            foreach (var grid in _grids)
            {
                foreach (var (tileStart, tileMoves) in grid.Bin(boxes))
                {
                    using var startBuffer = Compute.Accelerator.Allocate1D(tileStart);
                    using var movesBuffer = Compute.Accelerator.Allocate1D(tileMoves);
                    kernel(grid.Rays, new DexelSweepArgs
                    {
                        Grid = grid.View,
                        Cutter = cutter,
                        Moves = moveBuffer.View,
                        TileStart = startBuffer.View,
                        TileMoves = movesBuffer.View,
                        TileSize = TileSize,
                        TilesU = grid.TilesU,
                        Add = add ? 1 : 0,
                        ForceGeneral = ForceGeneralSolver ? 1 : 0,
                        MinLength = Resolution * 1e-3f,
                        Overflow = _overflow.View,
                    });
                    Compute.Accelerator.Synchronize();
                }
            }
        }
    }

    private sealed class Grid : IDisposable
    {
        public Grid(ComputeContext compute, int axis, BoundingBox bounds, float resolution, int capacity, int[] counts)
        {
            Axis = axis;
            Resolution = resolution;
            Capacity = capacity;
            var min = Float3.From(bounds.Min);
            var max = Float3.From(bounds.Max);
            CountU = axis switch { 0 => counts[0], 1 => counts[1], _ => counts[2] };
            CountV = axis switch { 0 => counts[1], 1 => counts[2], _ => counts[0] };
            OriginU = DexelKernel.U(axis, min);
            OriginV = DexelKernel.V(axis, min);
            MinW = DexelKernel.W(axis, min);
            MaxW = DexelKernel.W(axis, max);
            TilesU = (CountU + TileSize - 1) / TileSize;
            TilesV = (CountV + TileSize - 1) / TileSize;
            Segments = compute.Accelerator.Allocate1D<float>((long)Rays * 2 * capacity);
            Counts = compute.Accelerator.Allocate1D<int>(Rays);
            Counts.MemSetToZero();
        }

        public int Axis { get; }

        public float Resolution { get; }

        public int Capacity { get; }

        public int CountU { get; }

        public int CountV { get; }

        public float OriginU { get; }

        public float OriginV { get; }

        public float MinW { get; }

        public float MaxW { get; }

        public int TilesU { get; }

        public int TilesV { get; }

        public int Rays => CountU * CountV;

        public MemoryBuffer1D<float, Stride1D.Dense> Segments { get; }

        public MemoryBuffer1D<int, Stride1D.Dense> Counts { get; }

        public DexelGridView View => new()
        {
            Axis = Axis,
            CountU = CountU,
            CountV = CountV,
            OriginU = OriginU,
            OriginV = OriginV,
            Resolution = Resolution,
            MinW = MinW,
            MaxW = MaxW,
            Capacity = Capacity,
            Segments = Segments.View,
            Counts = Counts.View,
        };

        /// <summary>Sets every ray to the single segment returned by <paramref name="segment"/>(u, v), or empty for null.</summary>
        public void Fill(ComputeContext compute, Func<float, float, (float A, float B)?> segment)
        {
            var segments = new float[Rays * 2 * Capacity];
            var counts = new int[Rays];
            for (var iv = 0; iv < CountV; iv++)
            {
                for (var iu = 0; iu < CountU; iu++)
                {
                    var ray = (iv * CountU) + iu;
                    if (segment(OriginU + ((iu + 0.5f) * Resolution), OriginV + ((iv + 0.5f) * Resolution)) is { } s)
                    {
                        segments[ray * 2 * Capacity] = MathF.Max(s.A, MinW);
                        segments[(ray * 2 * Capacity) + 1] = MathF.Min(s.B, MaxW);
                        counts[ray] = 1;
                    }
                }
            }

            lock (compute.Gate)
            {
                compute.Upload(Segments.View, segments);
                compute.Upload(Counts.View, counts);
            }
        }

        /// <summary>Per-tile move lists (CSR), split into launches of bounded size.</summary>
        public IEnumerable<(int[] TileStart, int[] TileMoves)> Bin(List<(Vector3 Min, Vector3 Max)> boxes)
        {
            var tiles = TilesU * TilesV;
            var first = 0;
            while (first < boxes.Count)
            {
                var counts = new int[tiles + 1];
                var last = first;
                long entries = 0;
                while (last < boxes.Count && (entries == 0 || entries < MaxTileEntries))
                {
                    if (TileRange(boxes[last], out var u0, out var u1, out var v0, out var v1))
                    {
                        for (var tv = v0; tv <= v1; tv++)
                        {
                            for (var tu = u0; tu <= u1; tu++)
                            {
                                counts[(tv * TilesU) + tu + 1]++;
                            }
                        }

                        entries += (long)(u1 - u0 + 1) * (v1 - v0 + 1);
                    }

                    last++;
                }

                for (var t = 0; t < tiles; t++)
                {
                    counts[t + 1] += counts[t];
                }

                var moves = new int[counts[tiles]];
                var fill = counts[..tiles];
                for (var m = first; m < last; m++)
                {
                    if (TileRange(boxes[m], out var u0, out var u1, out var v0, out var v1))
                    {
                        for (var tv = v0; tv <= v1; tv++)
                        {
                            for (var tu = u0; tu <= u1; tu++)
                            {
                                moves[fill[(tv * TilesU) + tu]++] = m;
                            }
                        }
                    }
                }

                if (moves.Length > 0)
                {
                    yield return (counts, moves);
                }

                first = last;
            }
        }

        public void Dispose()
        {
            Segments?.Dispose();
            Counts?.Dispose();
        }

        private bool TileRange((Vector3 Min, Vector3 Max) box, out int u0, out int u1, out int v0, out int v1)
        {
            var min = Float3.From(box.Min);
            var max = Float3.From(box.Max);
            var iu0 = Math.Max((int)MathF.Ceiling(((DexelKernel.U(Axis, min) - OriginU) / Resolution) - 0.5f), 0);
            var iu1 = Math.Min((int)MathF.Floor(((DexelKernel.U(Axis, max) - OriginU) / Resolution) - 0.5f), CountU - 1);
            var iv0 = Math.Max((int)MathF.Ceiling(((DexelKernel.V(Axis, min) - OriginV) / Resolution) - 0.5f), 0);
            var iv1 = Math.Min((int)MathF.Floor(((DexelKernel.V(Axis, max) - OriginV) / Resolution) - 0.5f), CountV - 1);
            var wOverlap = DexelKernel.W(Axis, max) >= MinW && DexelKernel.W(Axis, min) <= MaxW;
            u0 = iu0 / TileSize;
            u1 = iu1 / TileSize;
            v0 = iv0 / TileSize;
            v1 = iv1 / TileSize;
            return wOverlap && iu0 <= iu1 && iv0 <= iv1;
        }
    }
}
