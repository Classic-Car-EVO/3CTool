using System.Numerics;

using ThreeCTool.TestSupport;

using Xunit;

namespace ThreeCTool.Geometry.Tests;

public sealed class MeshClipperTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Clip_SphereHalves_AddUpToTheWholeAndAreClosed(int axis)
    {
        var sphere = TestMeshes.Sphere(10, 24, 48);
        var whole = sphere.SignedVolume();

        var below = MeshClipper.Clip(sphere, axis, 3.3, keepBelow: true);
        var above = MeshClipper.Clip(sphere, axis, 3.3, keepBelow: false);

        Assert.True(below.SignedVolume() > 0);
        Assert.True(above.SignedVolume() > 0);
        Assert.Equal(whole, below.SignedVolume() + above.SignedVolume(), whole * 1e-4);
        Assert.True(below.SignedVolume() > above.SignedVolume());
        Assert.True(Coordinate(below.Bounds.Max, axis) <= 3.3 + 1e-4);
        Assert.True(Coordinate(above.Bounds.Min, axis) >= 3.3 - 1e-4);
        AssertClosed(below);
        AssertClosed(above);
    }

    [Fact]
    public void ClipToBox_Box_GivesTheIntersection()
    {
        var box = TestMeshes.Box(Vector3.Zero, new Vector3(100, 60, 30));

        var piece = MeshClipper.ClipToBox(box, 40, 70, null, 20, 10, null);

        Assert.Equal(30 * 20 * 20, piece.SignedVolume(), 1e-2);
        Assert.Equal(new Vector3(40, 0, 10), piece.Bounds.Min);
        Assert.Equal(new Vector3(70, 20, 30), piece.Bounds.Max);
        AssertClosed(piece);
    }

    [Fact]
    public void Clip_NonConvexMesh_KeepsBothBranches()
    {
        var mushroom = TestMeshes.Mushroom();
        var bounds = mushroom.Bounds;
        var middle = (bounds.Min.Y + bounds.Max.Y) / 2;

        var front = MeshClipper.Clip(mushroom, 1, middle, keepBelow: true);
        var back = MeshClipper.Clip(mushroom, 1, middle, keepBelow: false);

        Assert.Equal(mushroom.SignedVolume(), front.SignedVolume() + back.SignedVolume(), mushroom.SignedVolume() * 1e-4);
        AssertClosed(front);
        AssertClosed(back);
    }

    [Fact]
    public void Clip_PlaneOutsideTheMesh_KeepsAllOrNothing()
    {
        var box = TestMeshes.Box(Vector3.Zero, new Vector3(10, 10, 10));

        Assert.Same(box, MeshClipper.Clip(box, 2, 20, keepBelow: true));
        Assert.Equal(0, MeshClipper.Clip(box, 2, 20, keepBelow: false).TriangleCount);
        Assert.Equal(0, MeshClipper.Clip(box, 0, 0, keepBelow: true).TriangleCount);
    }

    [Fact]
    public void Clip_AtAFaceOfTheMesh_KeepsTheWholeMesh()
    {
        var box = TestMeshes.Box(Vector3.Zero, new Vector3(10, 10, 10));

        var below = MeshClipper.Clip(box, 2, 10, keepBelow: true);

        Assert.Equal(1000, below.SignedVolume(), 1e-3);
        AssertClosed(below);
    }

    private static float Coordinate(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    /// <summary>A closed surface has zero total vector area (the sum of its area-weighted normals).</summary>
    private static void AssertClosed(Mesh mesh)
    {
        var sum = Vector3.Zero;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            sum += Vector3.Cross(b - a, c - a) / 2;
        }

        Assert.True(sum.Length() < 1e-3 * mesh.SurfaceArea(), $"Open surface: vector area {sum}, area {mesh.SurfaceArea()}.");
    }
}
