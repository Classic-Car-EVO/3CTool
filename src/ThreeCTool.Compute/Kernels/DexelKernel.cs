using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Kernels;

/// <summary>
/// One dexel direction. Axis 0: rays along Z over (u, v) = (X, Y); 1: along X over (Y, Z); 2: along Y over (Z, X).
/// Ray (iu, iv) passes through u = OriginU + (iu + ½)·Resolution (same for v). Segments are absolute w-coordinates.
/// </summary>
internal struct DexelGridView
{
    public int Axis;
    public int CountU;
    public int CountV;
    public float OriginU;
    public float OriginV;
    public float Resolution;
    public float MinW;
    public float MaxW;
    public int Capacity;

    /// <summary>Per ray: <see cref="Capacity"/> (start, end) pairs, sorted and disjoint.</summary>
    public ArrayView<float> Segments;
    public ArrayView<int> Counts;
}

internal struct DexelSweepArgs
{
    public DexelGridView Grid;
    public CutterProfile Cutter;

    /// <summary>Per move five entries: tip at start, tip displacement, unit tool axis, swept box min, swept box max.</summary>
    public ArrayView<Float3> Moves;

    /// <summary>CSR move lists per tile of TileSize × TileSize rays.</summary>
    public ArrayView<int> TileStart;
    public ArrayView<int> TileMoves;
    public int TileSize;
    public int TilesU;

    /// <summary>0 = remove material (milling), 1 = add material (deposition).</summary>
    public int Add;

    /// <summary>1 = always use the general (oblique) solver; tests use it to cross-check the fast paths.</summary>
    public int ForceGeneral;
    public float MinLength;
    public ArrayView<int> Overflow;

    /// <summary>Per move piece: removed length summed over the rays, in units of 1/<see cref="MeasureScale"/> mm.</summary>
    public ArrayView<long> Removed;

    /// <summary>0 = no measurement; otherwise units per mm of removed ray length.</summary>
    public float MeasureScale;
}

internal struct DexelMeshArgs
{
    public DexelGridView Grid;
    public MeshView Mesh;
    public ArrayView<int> Overflow;
}

/// <summary>
/// The tool body at one position is convex (a convex profile of revolution up to its cutting height), so the volume
/// swept along a straight move is convex and every ray meets it in one interval. Three solvers find it: ray parallel
/// to the tool axis (1D minimum of the lower envelope), ray perpendicular to it (union of moving cross-section chords)
/// and the general case (exact, per convex primitive of the cutter).
/// </summary>
internal static class DexelKernel
{
    public const int CrossingCapacity = 64;
    private const int Iterations = 30;

    /// <summary>Inscribed frustums per bull-nose corner arc in the general solver (radial error ≤ 3.1e-4·corner radius).</summary>
    private const int ArcSegments = 32;

    /// <summary>Slope of the convex extension of the profile beyond its radius.</summary>
    private const float Steep = 1e4f;

    public static void Sweep(Index1D index, DexelSweepArgs args)
    {
        var grid = args.Grid;
        var iu = index % grid.CountU;
        var iv = index / grid.CountU;
        var tile = ((iv / args.TileSize) * args.TilesU) + (iu / args.TileSize);
        var u = grid.OriginU + ((iu + 0.5f) * grid.Resolution);
        var v = grid.OriginV + ((iv + 0.5f) * grid.Resolution);
        var capacity = grid.Capacity;
        var list = grid.Segments.SubView(index * 2 * capacity, 2 * capacity);
        var count = grid.Counts[index];
        var overflow = false;
        var end = args.TileStart[tile + 1];
        for (var m = args.TileStart[tile]; m < end; m++)
        {
            var move = 5 * args.TileMoves[m];
            var boxMin = args.Moves[move + 3];
            var boxMax = args.Moves[move + 4];
            var bu = U(grid.Axis, boxMin);
            var bv = V(grid.Axis, boxMin);
            var tu = U(grid.Axis, boxMax);
            var tv = V(grid.Axis, boxMax);
            if ((args.Add != 0 || count > 0) && u >= bu && u <= tu && v >= bv && v <= tv
                && Interval(grid.Axis, u, v, args.Moves[move], args.Moves[move + 1], args.Moves[move + 2], args.Cutter, args.ForceGeneral != 0, out var a, out var b))
            {
                if (args.Add != 0)
                {
                    a = MathF.Max(a, grid.MinW);
                    b = MathF.Min(b, grid.MaxW);
                    if (b - a >= args.MinLength)
                    {
                        count = IntervalList.Insert(list, count, capacity, a, b, ref overflow);
                    }
                }
                else
                {
                    if (args.MeasureScale > 0)
                    {
                        var removed = IntervalList.Overlap(list, count, a, b);
                        if (removed > 0)
                        {
                            Atomic.Add(ref args.Removed[args.TileMoves[m]], (long)((removed * args.MeasureScale) + 0.5f));
                        }
                    }

                    count = IntervalList.Subtract(list, count, capacity, a, b, args.MinLength, ref overflow);
                }
            }
        }

        grid.Counts[index] = count;
        if (overflow)
        {
            Atomic.Add(ref args.Overflow[0], 1);
        }
    }

    /// <summary>Fills each ray with the inside intervals of a closed mesh (signed crossings, winding &gt; 0).</summary>
    public static void FromMesh(Index1D index, DexelMeshArgs args)
    {
        var grid = args.Grid;
        var iu = index % grid.CountU;
        var iv = index / grid.CountU;
        var u = grid.OriginU + ((iu + 0.5f) * grid.Resolution);
        var v = grid.OriginV + ((iv + 0.5f) * grid.Resolution);
        var origin = Compose(grid.Axis, u, v, 0);
        var direction = Compose(grid.Axis, 0, 0, 1);

        var ts = LocalMemory.Allocate<float>(CrossingCapacity);
        var signs = LocalMemory.Allocate<int>(CrossingCapacity);
        var crossings = 0;
        var overflow = false;

        var mesh = args.Mesh;
        var i = 0;
        while (i < mesh.NodeCount)
        {
            var node = mesh.Nodes[i];
            var next = node.Skip;
            if (DeviceMath.LineHitsBox(origin, direction, node.Min, node.Max))
            {
                if (node.Count > 0)
                {
                    for (var k = 0; k < node.Count; k++)
                    {
                        var triangle = mesh.Triangles[node.Start + k];
                        if (MeshQueryKernels.Intersect(origin, direction, triangle, out var t, out _, out _))
                        {
                            var facing = Float3.Dot(direction, Float3.Cross(triangle.B - triangle.A, triangle.C - triangle.A));
                            if (crossings < CrossingCapacity)
                            {
                                ts[crossings] = t;
                                signs[crossings] = facing < 0 ? 1 : -1;
                                crossings++;
                            }
                            else
                            {
                                overflow = true;
                            }
                        }
                    }
                }
                else
                {
                    next = i + 1;
                }
            }

            i = next;
        }

        for (var k = 1; k < crossings; k++)
        {
            var t = ts[k];
            var s = signs[k];
            var j = k;
            while (j > 0 && ts[j - 1] > t)
            {
                ts[j] = ts[j - 1];
                signs[j] = signs[j - 1];
                j--;
            }

            ts[j] = t;
            signs[j] = s;
        }

        var capacity = grid.Capacity;
        var list = grid.Segments.SubView(index * 2 * capacity, 2 * capacity);
        var count = 0;
        var winding = 0;
        var start = 0f;
        for (var k = 0; k < crossings; k++)
        {
            // A ray through a shared edge or vertex hits several triangles at once with the same sense: count it once.
            var duplicate = k > 0 && signs[k] == signs[k - 1] && ts[k] - ts[k - 1] < 1e-4f;
            if (!duplicate)
            {
                var before = winding;
                winding += signs[k];
                if (before <= 0 && winding > 0)
                {
                    start = ts[k];
                }
                else if (before > 0 && winding <= 0)
                {
                    var a = MathF.Max(start, grid.MinW);
                    var b = MathF.Min(ts[k], grid.MaxW);
                    if (b > a)
                    {
                        count = IntervalList.Insert(list, count, capacity, a, b, ref overflow);
                    }
                }
            }
        }

        grid.Counts[index] = count;
        if (overflow)
        {
            Atomic.Add(ref args.Overflow[0], 1);
        }
    }

    /// <summary>The interval [a, b] (ray w-coordinates) where the ray meets the volume swept by the cutter.</summary>
    public static bool Interval(int axis, float u, float v, Float3 from, Float3 delta, Float3 toolAxis, CutterProfile cutter, bool forceGeneral, out float a, out float b)
    {
        a = 0;
        b = 0;
        var origin = Compose(axis, u, v, 0);
        var direction = Compose(axis, 0, 0, 1);
        var c = Float3.Dot(direction, toolAxis);
        if (!forceGeneral && MathF.Abs(c) > 1 - 1e-6f)
        {
            return Parallel(origin, c, from, delta, toolAxis, cutter, ref a, ref b);
        }

        if (!forceGeneral && MathF.Abs(c) < 1e-6f)
        {
            return Perpendicular(origin, direction, from, delta, toolAxis, cutter, ref a, ref b);
        }

        return General(origin, direction, from, delta, toolAxis, cutter, ref a, ref b);
    }

    private static bool Parallel(Float3 origin, float c, Float3 from, Float3 delta, Float3 toolAxis, CutterProfile cutter, ref float a, ref float b)
    {
        Float3.Basis(toolAxis, out var e1, out var e2);
        var relative = origin - from;
        var lower = new LowerEnvelope
        {
            Cutter = cutter,
            X0 = Float3.Dot(relative, e1),
            Y0 = Float3.Dot(relative, e2),
            Ex = -Float3.Dot(delta, e1),
            Ey = -Float3.Dot(delta, e2),
            P0 = Float3.Dot(from, toolAxis),
            Dp = Float3.Dot(delta, toolAxis),
        };
        if (!DeviceMath.DiscRange(lower.X0, lower.Y0, lower.Ex, lower.Ey, cutter.Radius, out var s0, out var s1))
        {
            return false;
        }

        var bottom = -DeviceMath.GoldenMax(lower, s0, s1, Iterations, out _);
        var top = MathF.Max(lower.P0 + (s0 * lower.Dp), lower.P0 + (s1 * lower.Dp)) + cutter.Height;
        var originW = Float3.Dot(origin, toolAxis);
        var wBottom = (bottom - originW) / c;
        var wTop = (top - originW) / c;
        a = MathF.Min(wBottom, wTop);
        b = MathF.Max(wBottom, wTop);
        return true;
    }

    private static bool Perpendicular(Float3 origin, Float3 direction, Float3 from, Float3 delta, Float3 toolAxis, CutterProfile cutter, ref float a, ref float b)
    {
        var normal = Float3.Cross(toolAxis, direction);
        var chord = new Chord
        {
            Cutter = cutter,
            H0 = Float3.Dot(origin - from, toolAxis),
            Dh = -Float3.Dot(delta, toolAxis),
            D0 = Float3.Dot(origin - from, normal),
            Dd = -Float3.Dot(delta, normal),
            C0 = Float3.Dot(from - origin, direction),
            Dc = Float3.Dot(delta, direction),
            Mode = 0,
        };
        var reach = DeviceMath.GoldenMax(chord, 0, 1, Iterations, out var sBest);
        if (reach < 0)
        {
            return false;
        }

        var sLow = chord.Evaluate(0) >= 0 ? 0 : DeviceMath.Bisect(chord, 0, sBest, Iterations);
        var sHigh = chord.Evaluate(1) >= 0 ? 1 : DeviceMath.Bisect(chord, sBest, 1, Iterations);
        chord.Mode = 1;
        a = -DeviceMath.GoldenMax(chord, sLow, sHigh, Iterations, out _);
        chord.Mode = 2;
        b = DeviceMath.GoldenMax(chord, sLow, sHigh, Iterations, out _);
        return true;
    }

    /// <summary>
    /// Exact solver for any ray direction. The cutter is a union of convex primitives (spheres and frustums; corner
    /// arcs of bull-nose cutters as inscribed frustums). For one primitive, the (s, t) pairs with the ray point
    /// inside the moving primitive form a convex region bounded by a conic and lines; the extremes of t lie among the
    /// line vertices, conic–line intersections and the conic's t-tangent points.
    /// </summary>
    private static bool General(Float3 origin, Float3 direction, Float3 from, Float3 delta, Float3 toolAxis, CutterProfile cutter, ref float a, ref float b)
    {
        // Measure t from the ray point nearest the middle of the swept body to keep the conic coefficients small.
        var middle = from + (delta * 0.5f) + (toolAxis * (0.5f * cutter.Height));
        var reference = Float3.Dot(middle - origin, direction);
        var q0 = origin + (direction * reference) - from;
        var m = -delta;
        var setup = new SweepSetup
        {
            Q0 = q0,
            M = m,
            D = direction,
            H0 = Float3.Dot(q0, toolAxis),
            DW = Float3.Dot(direction, toolAxis),
            MW = Float3.Dot(m, toolAxis),
            Tolerance = 1e-5f * (Float3.Dot(q0, q0) + Float3.Dot(m, m) + ((cutter.Height + cutter.Radius) * (cutter.Height + cutter.Radius)) + 1),
        };

        var tMin = 1e30f;
        var tMax = -1e30f;
        var primitives = PrimitiveCount(cutter);
        for (var k = 0; k < primitives; k++)
        {
            Primitive(cutter, k, out var sphere, out var ha, out var hb, out var ra, out var rb);
            setup.Region(sphere, ha, hb, ra, rb, ref tMin, ref tMax);
        }

        if (tMin > tMax)
        {
            return false;
        }

        a = reference + tMin;
        b = reference + tMax;
        return true;
    }

    private static int PrimitiveCount(CutterProfile cutter)
    {
        var arc = cutter.CornerRadius > 0 ? (cutter.FlatRadius > 0 ? ArcSegments : 1) : 0;
        var cone = cutter.ProfileHeight > cutter.ArcEndHeight + 1e-6f ? 1 : 0;
        var cylinder = cutter.Height > cutter.ProfileHeight + 1e-6f ? 1 : 0;
        return arc + cone + cylinder;
    }

    /// <summary>
    /// Primitive k (bottom up). A sphere has centre height <paramref name="ha"/> and radius <paramref name="ra"/>;
    /// a frustum spans heights [ha, hb] with radii ra → rb.
    /// </summary>
    private static void Primitive(CutterProfile cutter, int k, out bool sphere, out float ha, out float hb, out float ra, out float rb)
    {
        sphere = false;
        var rc = cutter.CornerRadius;
        var arc = cutter.CornerRadius > 0 ? (cutter.FlatRadius > 0 ? ArcSegments : 1) : 0;
        var cone = cutter.ProfileHeight > cutter.ArcEndHeight + 1e-6f ? 1 : 0;
        if (k < arc && cutter.FlatRadius <= 0)
        {
            sphere = true;
            ha = rc;
            hb = rc;
            ra = rc;
            rb = rc;
        }
        else if (k < arc)
        {
            var end = MathF.Atan2(cutter.ArcEndRadius - cutter.FlatRadius, rc - cutter.ArcEndHeight);
            var a0 = end * k / ArcSegments;
            var a1 = end * (k + 1) / ArcSegments;
            ha = rc - (rc * MathF.Cos(a0));
            hb = rc - (rc * MathF.Cos(a1));
            ra = cutter.FlatRadius + (rc * MathF.Sin(a0));
            rb = cutter.FlatRadius + (rc * MathF.Sin(a1));
        }
        else if (k < arc + cone)
        {
            ha = cutter.ArcEndHeight;
            hb = cutter.ProfileHeight;
            ra = cutter.ArcEndRadius;
            rb = cutter.Radius;
        }
        else
        {
            ha = cutter.ProfileHeight;
            hb = cutter.Height;
            ra = cutter.Radius;
            rb = cutter.Radius;
        }
    }

    public static Float3 Compose(int axis, float u, float v, float w) => axis switch
    {
        0 => new Float3(u, v, w),
        1 => new Float3(w, u, v),
        _ => new Float3(v, w, u),
    };

    public static float U(int axis, Float3 p) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

    public static float V(int axis, Float3 p) => axis == 0 ? p.Y : axis == 1 ? p.Z : p.X;

    public static float W(int axis, Float3 p) => axis == 0 ? p.Z : axis == 1 ? p.X : p.Y;

    /// <summary>Negated bottom of the cutter (tool-axis coordinate) seen by a ray parallel to the axis, at move parameter s.</summary>
    private struct LowerEnvelope : IScalarFunction
    {
        public CutterProfile Cutter;
        public float X0;
        public float Y0;
        public float Ex;
        public float Ey;
        public float P0;
        public float Dp;

        public readonly float Evaluate(float s)
        {
            var x = X0 + (s * Ex);
            var y = Y0 + (s * Ey);
            var rho = MathF.Min(MathF.Sqrt((x * x) + (y * y)), Cutter.Radius);
            return -(P0 + (s * Dp) + Cutter.HeightAt(rho));
        }
    }

    /// <summary>
    /// Ray perpendicular to the tool axis at height h(s) above the tip and distance d(s) from it. Mode 0: concave
    /// reach r(h) − |d| (≥ 0 where the ray meets the cross-section); 1: negated chord entry; 2: chord exit.
    /// </summary>
    private struct Chord : IScalarFunction
    {
        public CutterProfile Cutter;
        public float H0;
        public float Dh;
        public float D0;
        public float Dd;
        public float C0;
        public float Dc;
        public int Mode;

        public readonly float Evaluate(float s)
        {
            var h = H0 + (s * Dh);
            var d = D0 + (s * Dd);
            float r;
            if (h < 0)
            {
                r = Cutter.RadiusAt(0) + (Steep * h);
            }
            else if (h > Cutter.Height)
            {
                r = Cutter.Radius - (Steep * (h - Cutter.Height));
            }
            else
            {
                r = Cutter.RadiusAt(h);
            }

            if (Mode == 0)
            {
                return r - MathF.Abs(d);
            }

            var half = MathF.Sqrt(MathF.Max((r * r) - (d * d), 0));
            var center = C0 + (s * Dc);
            return Mode == 1 ? half - center : center + half;
        }
    }

    /// <summary>Ray point q(s, t) = Q0 + t·D + s·M relative to the tool tip at move parameter s.</summary>
    private struct SweepSetup
    {
        public Float3 Q0;
        public Float3 M;
        public Float3 D;
        public float H0;
        public float DW;
        public float MW;
        public float Tolerance;

        /// <summary>Widens [tMin, tMax] by the t-range where the ray meets one primitive swept over s ∈ [0, 1].</summary>
        public readonly void Region(bool sphere, float ha, float hb, float ra, float rb, ref float tMin, ref float tMax)
        {
            var region = new ConicRegion
            {
                L0 = new Float3(1, 0, 0),
                L1 = new Float3(-1, 0, 1),
                Tolerance = Tolerance,
                TMin = tMin,
                TMax = tMax,
            };
            var q0q0 = Float3.Dot(Q0, Q0);
            if (sphere)
            {
                // |q − ha·W|² ≤ ra²
                region.A = Float3.Dot(M, M);
                region.B = Float3.Dot(D, M);
                region.C = Float3.Dot(D, D);
                region.Dl = Float3.Dot(Q0, M) - (ha * MW);
                region.E = Float3.Dot(Q0, D) - (ha * DW);
                region.F = q0q0 - (2 * ha * H0) + (ha * ha) - (ra * ra);
                region.Lines = 2;
            }
            else
            {
                // |q|² − h² ≤ (r0 + k·h)², ha ≤ h ≤ hb
                var k = (rb - ra) / (hb - ha);
                var r0 = ra - (k * ha);
                var kappa = 1 + (k * k);
                region.A = Float3.Dot(M, M) - (kappa * MW * MW);
                region.B = Float3.Dot(D, M) - (kappa * DW * MW);
                region.C = Float3.Dot(D, D) - (kappa * DW * DW);
                region.Dl = Float3.Dot(Q0, M) - (kappa * H0 * MW) - (r0 * k * MW);
                region.E = Float3.Dot(Q0, D) - (kappa * H0 * DW) - (r0 * k * DW);
                region.F = q0q0 - (kappa * H0 * H0) - (2 * r0 * k * H0) - (r0 * r0);
                region.L2 = new Float3(MW, DW, H0 - ha);
                region.L3 = new Float3(-MW, -DW, hb - H0);
                region.Lines = 4;
            }

            region.Solve();
            tMin = region.TMin;
            tMax = region.TMax;
        }
    }

    /// <summary>
    /// Convex region Q(s, t) = A s² + 2B st + C t² + 2Dl s + 2E t + F ≤ 0 with lines α s + β t + γ ≥ 0 (as Float3).
    /// Collects the t-range of the extreme-point candidates that lie in the region.
    /// </summary>
    private struct ConicRegion
    {
        public float A;
        public float B;
        public float C;
        public float Dl;
        public float E;
        public float F;
        public Float3 L0;
        public Float3 L1;
        public Float3 L2;
        public Float3 L3;
        public int Lines;
        public float Tolerance;
        public float TMin;
        public float TMax;

        public void Solve()
        {
            for (var i = 0; i < Lines; i++)
            {
                var li = Line(i);
                for (var j = i + 1; j < Lines; j++)
                {
                    var lj = Line(j);
                    var det = (li.X * lj.Y) - (lj.X * li.Y);
                    if (MathF.Abs(det) > 1e-12f)
                    {
                        Consider(((li.Y * lj.Z) - (lj.Y * li.Z)) / det, ((lj.X * li.Z) - (li.X * lj.Z)) / det);
                    }
                }

                OnLine(li);
            }

            // Points of the conic where t is extreme: ∂Q/∂s = 0.
            OnLine(new Float3(A, B, Dl));
        }

        private readonly Float3 Line(int k) => k == 0 ? L0 : k == 1 ? L1 : k == 2 ? L2 : L3;

        private readonly float Q(float s, float t) =>
            (A * s * s) + (2 * B * s * t) + (C * t * t) + (2 * Dl * s) + (2 * E * t) + F;

        private void Consider(float s, float t)
        {
            var inside = Q(s, t) <= Tolerance;
            for (var k = 0; k < Lines; k++)
            {
                var l = Line(k);
                inside &= (l.X * s) + (l.Y * t) + l.Z >= -1e-4f;
            }

            if (inside)
            {
                TMin = MathF.Min(TMin, t);
                TMax = MathF.Max(TMax, t);
            }
        }

        /// <summary>Intersections of the conic with the line α s + β t + γ = 0.</summary>
        private void OnLine(Float3 l)
        {
            if (MathF.Abs(l.Y) >= MathF.Abs(l.X))
            {
                if (MathF.Abs(l.Y) > 1e-12f)
                {
                    // t = m s + c
                    var m = -l.X / l.Y;
                    var c = -l.Z / l.Y;
                    var count = Roots(A + (2 * B * m) + (C * m * m), (B * c) + (C * m * c) + Dl + (E * m), (C * c * c) + (2 * E * c) + F, out var x0, out var x1);
                    if (count > 0)
                    {
                        Consider(x0, (m * x0) + c);
                    }

                    if (count > 1)
                    {
                        Consider(x1, (m * x1) + c);
                    }
                }
            }
            else
            {
                // s = m t + c
                var m = -l.Y / l.X;
                var c = -l.Z / l.X;
                var count = Roots((A * m * m) + (2 * B * m) + C, (A * m * c) + (B * c) + (Dl * m) + E, (A * c * c) + (2 * Dl * c) + F, out var x0, out var x1);
                if (count > 0)
                {
                    Consider((m * x0) + c, x0);
                }

                if (count > 1)
                {
                    Consider((m * x1) + c, x1);
                }
            }
        }

        /// <summary>Real roots of a x² + 2b x + c = 0.</summary>
        private static int Roots(float a, float b, float c, out float x0, out float x1)
        {
            x0 = 0;
            x1 = 0;
            if (MathF.Abs(a) <= 1e-7f * (MathF.Abs(b) + MathF.Abs(c)))
            {
                if (MathF.Abs(b) < 1e-20f)
                {
                    return 0;
                }

                x0 = -c / (2 * b);
                return 1;
            }

            var discriminant = (b * b) - (a * c);
            if (discriminant < 0)
            {
                if (discriminant < -1e-6f * ((b * b) + MathF.Abs(a * c)))
                {
                    return 0;
                }

                discriminant = 0;
            }

            // Numerically stable form: avoids cancellation between -b and the root.
            var root = MathF.Sqrt(discriminant);
            var qv = b >= 0 ? -(b + root) : -(b - root);
            x0 = qv / a;
            x1 = MathF.Abs(qv) > 1e-30f ? c / qv : x0;
            return 2;
        }
    }
}
