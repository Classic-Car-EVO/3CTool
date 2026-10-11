using System.Numerics;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Geometry;

public sealed class MeshRepairOptions
{
    /// <summary>Weld vertices closer than this (mm) before repairing; 0 = only bit-identical vertices are shared.</summary>
    public float WeldTolerance { get; init; }

    public bool FillHoles { get; init; } = true;

    /// <summary>Holes with more boundary edges than this are left open (e.g. intentionally open surfaces).</summary>
    public int MaxHoleEdges { get; init; } = 2000;
}

/// <summary>
/// Removes degenerate and duplicate triangles, makes winding consistent and outward,
/// fills holes and reports what was done.
/// </summary>
public static class MeshRepair
{
    public static (Mesh Mesh, MeshRepairReport Report) Repair(Mesh mesh, MeshRepairOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new MeshRepairOptions();
        var report = new MeshRepairReport();

        if (options.WeldTolerance > 0)
        {
            mesh = MeshWelder.Weld(mesh, options.WeldTolerance);
        }

        var vertices = new List<Vector3>(mesh.Vertices);
        var triangles = RemoveBadTriangles(mesh, report);

        var edges = EdgeTable.Build(triangles);
        report.NonManifoldEdges = edges.CountNonManifold();
        report.FlippedTriangles = OrientConsistently(vertices, triangles, edges);

        if (options.FillHoles)
        {
            report.HolesFilled = FillHoles(vertices, triangles, options.MaxHoleEdges);
        }

        report.IsWatertight = EdgeTable.Build(triangles).IsClosedAndConsistent(triangles);

        var indices = new int[triangles.Count * 3];
        for (var t = 0; t < triangles.Count; t++)
        {
            (indices[t * 3], indices[(t * 3) + 1], indices[(t * 3) + 2]) = triangles[t];
        }

        return (new Mesh([.. vertices], indices, mesh.Name), report);
    }

    /// <summary>Checks closedness and consistent winding without changing the mesh.</summary>
    public static bool IsWatertight(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var triangles = new List<(int, int, int)>(mesh.TriangleCount);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            triangles.Add((mesh.Indices[t * 3], mesh.Indices[(t * 3) + 1], mesh.Indices[(t * 3) + 2]));
        }

        return EdgeTable.Build(triangles).IsClosedAndConsistent(triangles);
    }

    private static List<(int A, int B, int C)> RemoveBadTriangles(Mesh mesh, MeshRepairReport report)
    {
        var diagonal = mesh.Bounds.Diagonal;
        var minDoubleArea = 1e-12f * diagonal * diagonal;
        var seen = new HashSet<(int, int, int)>();
        var result = new List<(int, int, int)>(mesh.TriangleCount);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = mesh.Indices[t * 3];
            var b = mesh.Indices[(t * 3) + 1];
            var c = mesh.Indices[(t * 3) + 2];
            var (pa, pb, pc) = mesh.GetTriangle(t);
            if (a == b || b == c || a == c || Vector3.Cross(pb - pa, pc - pa).Length() <= minDoubleArea)
            {
                report.RemovedDegenerateTriangles++;
                continue;
            }

            if (!seen.Add(SortedKey(a, b, c)))
            {
                report.RemovedDuplicateTriangles++;
                continue;
            }

            result.Add((a, b, c));
        }

        return result;
    }

    private static (int, int, int) SortedKey(int a, int b, int c)
    {
        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (b > c)
        {
            (b, c) = (c, b);
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        return (a, b, c);
    }

    /// <summary>
    /// Flood-fills each connected component across manifold edges so neighbours have opposite edge directions,
    /// then flips closed components with negative volume. Open components keep the majority of their original winding.
    /// </summary>
    private static int OrientConsistently(List<Vector3> vertices, List<(int A, int B, int C)> triangles, EdgeTable edges)
    {
        var count = triangles.Count;
        var flip = new bool[count];
        var visited = new bool[count];
        var flipped = 0;
        var queue = new Queue<int>();
        var component = new List<int>();

        for (var seed = 0; seed < count; seed++)
        {
            if (visited[seed])
            {
                continue;
            }

            component.Clear();
            visited[seed] = true;
            queue.Enqueue(seed);
            var closed = true;
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                component.Add(t);
                var (a, b, c) = Oriented(triangles[t], flip[t]);
                foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
                {
                    var shared = edges.TrianglesOn(from, to);
                    if (shared.Length != 2)
                    {
                        closed = false;
                        continue;
                    }

                    var neighbour = shared[0] == t ? shared[1] : shared[0];
                    if (visited[neighbour])
                    {
                        continue;
                    }

                    // A consistent neighbour traverses the shared edge as to -> from.
                    visited[neighbour] = true;
                    flip[neighbour] = HasDirectedEdge(triangles[neighbour], from, to);
                    queue.Enqueue(neighbour);
                }
            }

            bool reverse;
            if (closed)
            {
                var volume = 0.0;
                foreach (var t in component)
                {
                    var (a, b, c) = Oriented(triangles[t], flip[t]);
                    volume += Vector3.Dot(vertices[a], Vector3.Cross(vertices[b], vertices[c]));
                }

                reverse = volume < 0;
            }
            else
            {
                reverse = component.Count(t => flip[t]) * 2 > component.Count;
            }

            foreach (var t in component)
            {
                if (flip[t] != reverse)
                {
                    var (a, b, c) = triangles[t];
                    triangles[t] = (a, c, b);
                    flipped++;
                }
            }
        }

        return flipped;
    }

    private static (int, int, int) Oriented((int A, int B, int C) t, bool flip) => flip ? (t.A, t.C, t.B) : t;

    private static bool HasDirectedEdge((int A, int B, int C) t, int from, int to) =>
        (t.A == from && t.B == to) || (t.B == from && t.C == to) || (t.C == from && t.A == to);

    /// <summary>Closes boundary loops: a triangle for 3-edge loops, otherwise a fan around the loop centroid.</summary>
    private static int FillHoles(List<Vector3> vertices, List<(int A, int B, int C)> triangles, int maxHoleEdges)
    {
        var edges = EdgeTable.Build(triangles);

        // Boundary edge a->b is used once; the hole loop runs b->a.
        var next = new Dictionary<int, Queue<int>>();
        var boundaryCount = 0;
        foreach (var (from, to) in edges.BoundaryEdges(triangles))
        {
            if (!next.TryGetValue(to, out var targets))
            {
                next[to] = targets = new Queue<int>();
            }

            targets.Enqueue(from);
            boundaryCount++;
        }

        var filled = 0;
        var loop = new List<int>();
        foreach (var start in next.Keys.ToList())
        {
            while (next.TryGetValue(start, out var starts) && starts.Count > 0)
            {
                loop.Clear();
                var current = start;
                var closed = false;
                while (loop.Count <= boundaryCount && next.TryGetValue(current, out var targets) && targets.Count > 0)
                {
                    loop.Add(current);
                    current = targets.Dequeue();
                    if (current == start)
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed || loop.Count < 3 || loop.Count > maxHoleEdges)
                {
                    continue;
                }

                if (loop.Count == 3)
                {
                    triangles.Add((loop[0], loop[1], loop[2]));
                }
                else
                {
                    var centroid = Vector3.Zero;
                    foreach (var v in loop)
                    {
                        centroid += vertices[v];
                    }

                    var center = vertices.Count;
                    vertices.Add(centroid / loop.Count);
                    for (var i = 0; i < loop.Count; i++)
                    {
                        triangles.Add((loop[i], loop[(i + 1) % loop.Count], center));
                    }
                }

                filled++;
            }
        }

        return filled;
    }

    /// <summary>Undirected edge → triangles, built by sorting (edge key, triangle) pairs.</summary>
    private sealed class EdgeTable
    {
        private readonly long[] _keys;
        private readonly int[] _triangles;

        private EdgeTable(long[] keys, int[] triangles)
        {
            _keys = keys;
            _triangles = triangles;
        }

        public static EdgeTable Build(List<(int A, int B, int C)> triangles)
        {
            var keys = new long[triangles.Count * 3];
            var owners = new int[triangles.Count * 3];
            for (var t = 0; t < triangles.Count; t++)
            {
                var (a, b, c) = triangles[t];
                keys[t * 3] = Key(a, b);
                keys[(t * 3) + 1] = Key(b, c);
                keys[(t * 3) + 2] = Key(c, a);
                owners[t * 3] = owners[(t * 3) + 1] = owners[(t * 3) + 2] = t;
            }

            Array.Sort(keys, owners);
            return new EdgeTable(keys, owners);
        }

        public ReadOnlySpan<int> TrianglesOn(int a, int b)
        {
            var key = Key(a, b);
            var first = LowerBound(key);
            var last = first;
            while (last < _keys.Length && _keys[last] == key)
            {
                last++;
            }

            return _triangles.AsSpan(first, last - first);
        }

        public int CountNonManifold()
        {
            var count = 0;
            ForEachGroup((_, size) =>
            {
                if (size > 2)
                {
                    count++;
                }
            });
            return count;
        }

        public bool IsClosedAndConsistent(List<(int A, int B, int C)> triangles)
        {
            // Every undirected edge must have exactly two triangles traversing it in opposite directions.
            var ok = _keys.Length > 0;
            ForEachGroup((start, size) =>
            {
                if (size != 2)
                {
                    ok = false;
                    return;
                }

                var (lo, hi) = ((int)(_keys[start] >> 32), (int)(_keys[start] & 0xFFFFFFFF));
                ok &= HasDirectedEdge(triangles[_triangles[start]], lo, hi) != HasDirectedEdge(triangles[_triangles[start + 1]], lo, hi);
            });
            return ok;
        }

        /// <summary>Directed boundary edges (used by exactly one triangle), in that triangle's winding.</summary>
        public List<(int From, int To)> BoundaryEdges(List<(int A, int B, int C)> triangles)
        {
            var result = new List<(int, int)>();
            ForEachGroup((start, size) =>
            {
                if (size != 1)
                {
                    return;
                }

                var (lo, hi) = ((int)(_keys[start] >> 32), (int)(_keys[start] & 0xFFFFFFFF));
                var t = triangles[_triangles[start]];
                result.Add(HasDirectedEdge(t, lo, hi) ? (lo, hi) : (hi, lo));
            });
            return result;
        }

        private void ForEachGroup(Action<int, int> visit)
        {
            var i = 0;
            while (i < _keys.Length)
            {
                var j = i + 1;
                while (j < _keys.Length && _keys[j] == _keys[i])
                {
                    j++;
                }

                visit(i, j - i);
                i = j;
            }
        }

        private int LowerBound(long key)
        {
            int lo = 0, hi = _keys.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (_keys[mid] < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    }
}
