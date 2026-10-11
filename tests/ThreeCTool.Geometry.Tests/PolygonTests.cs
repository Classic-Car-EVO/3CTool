using System.Diagnostics;
using System.Numerics;

using Clipper2Lib;

using ThreeCTool.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace ThreeCTool.Geometry.Tests;

public sealed class PolygonTests(ITestOutputHelper output)
{
    [Fact]
    public void Slice_Box_GivesCounterClockwiseSquare()
    {
        var region = MeshSlicer.Slice(TestMeshes.Box(Vector3.Zero, new Vector3(10, 20, 10)), 5);

        Assert.Single(region);
        Assert.True(Clipper.IsPositive(region[0]));
        Assert.Equal(200, Polygons.Area(region), 6);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(30, 100)]
    [InlineData(35, 3600)]
    [InlineData(40, 3600)]
    [InlineData(40.001, 0)]
    [InlineData(0, 0)]
    public void Slice_Mushroom_AreaPerLevel(double z, double area)
    {
        Assert.Equal(area, Polygons.Area(MeshSlicer.Slice(TestMeshes.Mushroom(), z)), 6);
    }

    [Fact]
    public void Slice_Sphere_IsInscribedPolygon()
    {
        var region = MeshSlicer.Slice(TestMeshes.Sphere(10, 32, 64), 0);

        var expected = 100 * 32 * Math.Sin(2 * Math.PI / 64);
        Assert.Single(region);
        Assert.Equal(expected, Polygons.Area(region), 3);
    }

    [Fact]
    public void Slice_ManyHeights_MatchesSingle()
    {
        var mesh = TestMeshes.Sphere(10, 16, 32);
        double[] heights = [-5, 0, 5];

        var regions = MeshSlicer.Slice(mesh, heights);

        for (var i = 0; i < heights.Length; i++)
        {
            Assert.Equal(Polygons.Area(MeshSlicer.Slice(mesh, heights[i])), Polygons.Area(regions[i]), 9);
        }
    }

    [Fact]
    public void Silhouette_Mushroom_IsCap()
    {
        Assert.Equal(3600, Polygons.Area(MeshSlicer.Silhouette(TestMeshes.Mushroom())), 6);
    }

    [Fact]
    public void Silhouette_Sphere_IsEquatorPolygon()
    {
        var region = MeshSlicer.Silhouette(TestMeshes.Sphere(10, 32, 64));

        Assert.Single(region);
        Assert.Equal(100 * 32 * Math.Sin(2 * Math.PI / 64), Polygons.Area(region), 3);
    }

    [Fact]
    public void Silhouette_CarBody_IsFast()
    {
        if (TestModels.Find("CarBody.stl") is not { } path)
        {
            return;
        }

        var mesh = MeshImporter.Load(path).Bodies[0].Mesh;
        var watch = Stopwatch.StartNew();
        var region = MeshSlicer.Silhouette(mesh);
        output.WriteLine($"{mesh.TriangleCount} triangles: {watch.ElapsedMilliseconds} ms, {region.Sum(p => p.Count)} vertices");

        var size = mesh.Bounds.Size;
        Assert.InRange(Polygons.Area(region), 0.5 * size.X * size.Y, size.X * size.Y);
    }

    [Fact]
    public void Offset_RoundCornersStayWithinArcTolerance()
    {
        var square = Polygons.Rectangle(0, 0, 10, 10);
        const double tolerance = 0.01;

        var grown = Polygons.Offset(square, 5, tolerance);

        Assert.Single(grown);
        Assert.Equal(100 + 200 + (Math.PI * 25), Polygons.Area(grown), 0.5);
        var path = grown[0];
        Assert.InRange(path.Count, 20, 200);
        for (var i = 0; i < path.Count; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % path.Count];
            Assert.InRange(Polygons.DistanceToBoundary(square, a), 5 - 1e-3, 5 + 1e-3);
            Assert.InRange(Polygons.DistanceToBoundary(square, new PointD((a.x + b.x) / 2, (a.y + b.y) / 2)), 5 - tolerance - 1e-3, 5 + 1e-3);
        }
    }

    [Fact]
    public void Open_RemovesThinParts()
    {
        var region = Polygons.Union(Polygons.Rectangle(0, 0, 10, 10), Polygons.Rectangle(10, 4, 30, 5));

        var opened = Polygons.Open(region, 1, 0.01);

        Assert.InRange(Polygons.Area(opened), 100 - (4 * (1 - (Math.PI / 4))) - 0.1, 100);
        Assert.All(opened.SelectMany(p => p), p => Assert.True(p.x <= 10.2, $"x = {p.x}"));
    }

    [Fact]
    public void ContainsAndSegments_RespectHoles()
    {
        var ring = Polygons.Difference(Polygons.Rectangle(0, 0, 10, 10), Polygons.Rectangle(4, 4, 6, 6));

        Assert.True(Polygons.Contains(ring, new PointD(1, 1)));
        Assert.False(Polygons.Contains(ring, new PointD(5, 5)));
        Assert.True(Polygons.Contains(ring, new PointD(0, 5)));
        Assert.False(Polygons.Contains(ring, new PointD(11, 5)));
        Assert.True(Polygons.ContainsSegment(ring, new PointD(1, 1), new PointD(9, 1)));
        Assert.False(Polygons.ContainsSegment(ring, new PointD(1, 5), new PointD(9, 5)));
        Assert.Equal(1, Polygons.DistanceToBoundary(ring, new PointD(3, 5)), 9);
    }

    [Fact]
    public void ClipLines_KeepsInsideParts()
    {
        var ring = Polygons.Difference(Polygons.Rectangle(0, 0, 10, 10), Polygons.Rectangle(4, 4, 6, 6));
        PathsD line = [new PathD([new PointD(-5, 5), new PointD(15, 5)])];

        var inside = Polygons.ClipLines(line, ring);
        var outside = Polygons.ClipLines(line, ring, inside: false);

        Assert.Equal(2, inside.Count);
        Assert.Equal(8, inside.Sum(p => Polygons.Length(p, false)), 6);
        Assert.Equal(12, outside.Sum(p => Polygons.Length(p, false)), 6);
    }

    [Fact]
    public void Circle_RespectsTolerance()
    {
        var circle = Polygons.Circle(new PointD(1, 2), 10, 0.01);

        Assert.True(Clipper.IsPositive(circle));
        var chord = Polygons.Distance(circle[0], circle[1]);
        Assert.True(10 - Math.Sqrt(100 - (chord * chord / 4)) <= 0.01 + 1e-9);
    }

    [Fact]
    public void Isolines_ConeField_GivesCircle()
    {
        const int n = 101;
        var field = new float[n * n];
        for (var j = 0; j < n; j++)
        {
            for (var i = 0; i < n; i++)
            {
                field[(j * n) + i] = 10 - MathF.Sqrt(((i - 50) * (i - 50)) + ((j - 50) * (j - 50))) * 0.2f;
            }
        }

        var contours = MarchingSquares.Isolines(field, n, n, new PointD(-10, -10), 0.2, 5);

        var loop = Assert.Single(contours);
        Assert.True(loop.Closed);
        Assert.True(Clipper.IsPositive(loop.Points));
        Assert.Equal(Math.PI * 25, Clipper.Area(loop.Points), 1);
        Assert.All(loop.Points, p => Assert.Equal(5, Math.Sqrt((p.x * p.x) + (p.y * p.y)), 2));
    }

    [Fact]
    public void Isolines_CutByBorder_AreOpen()
    {
        const int n = 11;
        var field = new float[n * n];
        for (var j = 0; j < n; j++)
        {
            for (var i = 0; i < n; i++)
            {
                field[(j * n) + i] = i;
            }
        }

        var contours = MarchingSquares.Isolines(field, n, n, new PointD(0, 0), 1, 4.5);

        var line = Assert.Single(contours);
        Assert.False(line.Closed);
        Assert.Equal(n, line.Points.Count);
        Assert.All(line.Points, p => Assert.Equal(4.5, p.x, 9));
        Assert.True(line.Points[^1].y < line.Points[0].y);
    }

    [Fact]
    public void MaskRegion_TracesCellBoundariesAndHoles()
    {
        const int n = 5;
        var mask = new bool[n * n];
        for (var j = 1; j < 4; j++)
        {
            for (var i = 1; i < 4; i++)
            {
                mask[(j * n) + i] = (i, j) != (2, 2);
            }
        }

        var region = MarchingSquares.MaskRegion(mask, n, n, new PointD(0, 0), 2);

        Assert.Equal(2, region.Count);
        Assert.True(Polygons.Contains(region, new PointD(3, 3)));
        Assert.False(Polygons.Contains(region, new PointD(5, 5)));
        Assert.False(Polygons.Contains(region, new PointD(1, 1)));
    }
}
