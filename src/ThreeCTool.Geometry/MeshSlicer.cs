using System.Numerics;

using Clipper2Lib;

namespace ThreeCTool.Geometry;

/// <summary>Planar sections and XY silhouettes of closed meshes as normalized <see cref="Polygons"/> regions.</summary>
public static class MeshSlicer
{
    private const int SilhouetteChunk = 1024;

    /// <summary>
    /// Material region of the mesh just below the plane Z = <paramref name="z"/>: vertices on the plane count as above,
    /// so slicing exactly at a horizontal top face returns the face and slicing at a bottom face returns nothing.
    /// </summary>
    public static PathsD Slice(Mesh mesh, double z)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var segments = new List<(PointD From, PointD To)>();
        var vertices = mesh.Vertices;
        var indices = mesh.Indices;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var v0 = vertices[indices[3 * t]];
            var v1 = vertices[indices[(3 * t) + 1]];
            var v2 = vertices[indices[(3 * t) + 2]];
            var a0 = v0.Z >= z;
            var a1 = v1.Z >= z;
            var a2 = v2.Z >= z;
            if (a0 == a1 && a1 == a2)
            {
                continue;
            }

            // Isolated vertex k: the section runs from edge (k, k+1) to edge (k+2, k) when k is above, reversed when
            // below; this keeps the material on the left for counter-clockwise (outward) triangles.
            var (k, above) = a1 == a2 ? (0, a0) : a0 == a2 ? (1, a1) : (2, a2);
            var corners = k switch
            {
                0 => (v0, v1, v2),
                1 => (v1, v2, v0),
                _ => (v2, v0, v1),
            };
            var p = Crossing(corners.Item1, corners.Item2, z);
            var q = Crossing(corners.Item3, corners.Item1, z);
            if (p.x == q.x && p.y == q.y)
            {
                continue;
            }

            segments.Add(above ? (p, q) : (q, p));
        }

        return Polygons.Union(Chain(segments));
    }

    /// <summary>Sections at several heights, computed in parallel.</summary>
    public static PathsD[] Slice(Mesh mesh, IReadOnlyList<double> heights)
    {
        ArgumentNullException.ThrowIfNull(heights);
        var result = new PathsD[heights.Count];
        Parallel.For(0, heights.Count, i => result[i] = Slice(mesh, heights[i]));
        return result;
    }

    /// <summary>XY shadow of the mesh: the union of the projections of all non-vertical triangles.</summary>
    public static PathsD Silhouette(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var triangles = new List<PathD>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            var area = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
            if (Math.Abs(area) < 1e-12)
            {
                continue;
            }

            triangles.Add(area > 0
                ? new PathD([new PointD(a.X, a.Y), new PointD(b.X, b.Y), new PointD(c.X, c.Y)])
                : new PathD([new PointD(a.X, a.Y), new PointD(c.X, c.Y), new PointD(b.X, b.Y)]));
        }

        var chunks = triangles.Chunk(SilhouetteChunk).Select(c => new PathsD(c)).ToArray();
        while (chunks.Length > 1)
        {
            var merged = new PathsD[(chunks.Length + 1) / 2];
            var current = chunks;
            Parallel.For(0, merged.Length, i =>
            {
                merged[i] = 2 * i + 1 < current.Length
                    ? Polygons.Union(current[2 * i], current[(2 * i) + 1])
                    : Polygons.Union(current[2 * i]);
            });
            chunks = merged;
        }

        return chunks.Length == 0 ? [] : Polygons.Union(chunks[0]);
    }

    /// <summary>Plane crossing of an edge, computed from a canonical end order so both triangles get identical bits.</summary>
    private static PointD Crossing(Vector3 a, Vector3 b, double z)
    {
        if (Compare(a, b) > 0)
        {
            (a, b) = (b, a);
        }

        var t = (z - a.Z) / ((double)b.Z - a.Z);
        return new PointD(a.X + (t * ((double)b.X - a.X)), a.Y + (t * ((double)b.Y - a.Y)));
    }

    private static int Compare(Vector3 a, Vector3 b)
    {
        var c = a.X.CompareTo(b.X);
        if (c != 0)
        {
            return c;
        }

        c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.Z.CompareTo(b.Z);
    }

    private static PathsD Chain(List<(PointD From, PointD To)> segments)
    {
        var starts = new Dictionary<(double, double), List<int>>();
        for (var i = 0; i < segments.Count; i++)
        {
            var key = (segments[i].From.x, segments[i].From.y);
            if (!starts.TryGetValue(key, out var list))
            {
                starts[key] = list = [];
            }

            list.Add(i);
        }

        var used = new bool[segments.Count];
        var loops = new PathsD();
        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var path = new PathD();
            var current = i;
            while (current >= 0 && !used[current])
            {
                used[current] = true;
                path.Add(segments[current].From);
                var end = segments[current].To;
                current = -1;
                if (starts.TryGetValue((end.x, end.y), out var candidates))
                {
                    foreach (var candidate in candidates)
                    {
                        if (!used[candidate])
                        {
                            current = candidate;
                            break;
                        }
                    }
                }
            }

            if (path.Count >= 3)
            {
                loops.Add(path);
            }
        }

        return loops;
    }
}
