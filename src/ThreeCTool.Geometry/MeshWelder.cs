using System.Numerics;

namespace ThreeCTool.Geometry;

public static class MeshWelder
{
    /// <summary>Builds an indexed mesh from a triangle soup, merging bit-identical positions.</summary>
    public static Mesh FromTriangleSoup(IReadOnlyList<Vector3> corners, string name = "")
    {
        ArgumentNullException.ThrowIfNull(corners);
        var lookup = new Dictionary<Vector3, int>(corners.Count / 2);
        var vertices = new List<Vector3>(corners.Count / 2);
        var indices = new int[corners.Count - (corners.Count % 3)];
        for (var i = 0; i < indices.Length; i++)
        {
            var p = corners[i];
            if (!lookup.TryGetValue(p, out var index))
            {
                index = vertices.Count;
                lookup.Add(p, index);
                vertices.Add(p);
            }

            indices[i] = index;
        }

        return new Mesh([.. vertices], indices, name);
    }

    /// <summary>
    /// Merges vertices closer than <paramref name="tolerance"/> (mm) using a spatial hash.
    /// Each vertex merges into the first earlier vertex within tolerance; unused vertices are dropped.
    /// </summary>
    public static Mesh Weld(Mesh mesh, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);

        var inverseCell = 1f / tolerance;
        var toleranceSquared = tolerance * tolerance;
        var cells = new Dictionary<(int, int, int), List<int>>();
        var remap = new int[mesh.VertexCount];
        var vertices = new List<Vector3>();

        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var p = mesh.Vertices[i];
            var cell = CellOf(p, inverseCell);
            var found = FindNear(cells, vertices, cell, p, toleranceSquared);
            if (found < 0)
            {
                found = vertices.Count;
                vertices.Add(p);
                if (!cells.TryGetValue(cell, out var list))
                {
                    cells[cell] = list = [];
                }

                list.Add(found);
            }

            remap[i] = found;
        }

        var indices = new int[mesh.Indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = remap[mesh.Indices[i]];
        }

        return new Mesh([.. vertices], indices, mesh.Name);
    }

    private static (int, int, int) CellOf(Vector3 p, float inverseCell) =>
        ((int)MathF.Floor(p.X * inverseCell), (int)MathF.Floor(p.Y * inverseCell), (int)MathF.Floor(p.Z * inverseCell));

    private static int FindNear(Dictionary<(int, int, int), List<int>> cells, List<Vector3> vertices, (int X, int Y, int Z) cell, Vector3 p, float toleranceSquared)
    {
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue((cell.X + dx, cell.Y + dy, cell.Z + dz), out var list))
                    {
                        continue;
                    }

                    foreach (var candidate in list)
                    {
                        if (Vector3.DistanceSquared(vertices[candidate], p) <= toleranceSquared)
                        {
                            return candidate;
                        }
                    }
                }
            }
        }

        return -1;
    }
}
