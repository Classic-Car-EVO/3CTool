using System.Numerics;

namespace ThreeCTool.Geometry.Tests;

internal static class TestMeshes
{
    /// <summary>Closed box with outward counter-clockwise triangles. Vertex i = x + 2y + 4z bits.</summary>
    public static Mesh Box(Vector3 min, Vector3 max, string name = "box")
    {
        var vertices = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            vertices[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        }

        int[] indices =
        [
            0, 2, 3, 0, 3, 1, // -Z
            4, 5, 7, 4, 7, 6, // +Z
            0, 1, 5, 0, 5, 4, // -Y
            2, 6, 7, 2, 7, 3, // +Y
            0, 4, 6, 0, 6, 2, // -X
            1, 3, 7, 1, 7, 5, // +X
        ];
        return new Mesh(vertices, indices, name);
    }

    public static Mesh UnitCube() => Box(Vector3.Zero, Vector3.One);

    public static Mesh Sphere(float radius, int stacks, int slices)
    {
        var vertices = new List<Vector3> { new(0, 0, radius) };
        for (var i = 1; i < stacks; i++)
        {
            var phi = MathF.PI * i / stacks;
            for (var j = 0; j < slices; j++)
            {
                var theta = 2 * MathF.PI * j / slices;
                vertices.Add(new Vector3(radius * MathF.Sin(phi) * MathF.Cos(theta), radius * MathF.Sin(phi) * MathF.Sin(theta), radius * MathF.Cos(phi)));
            }
        }

        var south = vertices.Count;
        vertices.Add(new Vector3(0, 0, -radius));

        int Ring(int ring, int j) => 1 + ((ring - 1) * slices) + (j % slices);

        var indices = new List<int>();
        for (var j = 0; j < slices; j++)
        {
            indices.AddRange([0, Ring(1, j), Ring(1, j + 1)]);
            for (var i = 1; i < stacks - 1; i++)
            {
                int a = Ring(i, j), b = Ring(i, j + 1), c = Ring(i + 1, j), d = Ring(i + 1, j + 1);
                indices.AddRange([a, c, d, a, d, b]);
            }

            indices.AddRange([south, Ring(stacks - 1, j + 1), Ring(stacks - 1, j)]);
        }

        return new Mesh([.. vertices], [.. indices], "sphere");
    }

    /// <summary>10x10x30 post with a 60x60x10 cap on top (z 30..40): the cap underside is an undercut from +Z.</summary>
    public static Mesh Mushroom() => Mesh.Merge(
        [Box(new Vector3(-5, -5, 0), new Vector3(5, 5, 30)), Box(new Vector3(-30, -30, 30), new Vector3(30, 30, 40))],
        "mushroom");
}
