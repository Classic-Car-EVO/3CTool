namespace ThreeCTool.Rendering;

public static class Primitives
{
    /// <summary>Axis-aligned cube centred on the origin, with flat per-face normals.</summary>
    public static (float[] Vertices, uint[] Indices) Cube(float size)
    {
        var h = size / 2f;
        ReadOnlySpan<(float Nx, float Ny, float Nz)> normals =
        [
            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
        ];
        var vertices = new List<float>(24 * 6);
        var indices = new List<uint>(36);
        foreach (var (nx, ny, nz) in normals)
        {
            // Two in-plane axes u, v with u x v = n so the winding is counter-clockwise from outside.
            var (ux, uy, uz) = (ny, nz, nx);
            var (vx, vy, vz) = ((ny * uz) - (nz * uy), (nz * ux) - (nx * uz), (nx * uy) - (ny * ux));
            var start = (uint)(vertices.Count / 6);
            foreach (var (su, sv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                vertices.AddRange(
                [
                    h * (nx + (su * ux) + (sv * vx)),
                    h * (ny + (su * uy) + (sv * vy)),
                    h * (nz + (su * uz) + (sv * vz)),
                    nx, ny, nz,
                ]);
            }

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }

        return ([.. vertices], [.. indices]);
    }
}
