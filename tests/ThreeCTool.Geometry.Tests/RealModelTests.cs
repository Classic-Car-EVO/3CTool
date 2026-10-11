using System.Numerics;

using ThreeCTool.Core.Projects;
using ThreeCTool.TestSupport;

using Xunit;

namespace ThreeCTool.Geometry.Tests;

/// <summary>
/// Regression tests against the real-world models in <c>testModels/</c>. Each test returns early when its file is absent,
/// so the suite also runs on checkouts without the models.
/// </summary>
public sealed class RealModelTests
{
    private const string Oscar = "oscar.3mf";
    private const string CarBody = "CarBody.stl";

    [Fact]
    public void Oscar3mf_ImportsTwoStackedWatertightBodies()
    {
        if (ModelPath(Oscar) is not { } path)
        {
            return;
        }

        var model = MeshImporter.Load(path);

        Assert.True(model.UnitsDeclared);
        Assert.Equal(["Körper3", "Körper5"], model.Bodies.Select(b => b.Name));
        Assert.All(model.Bodies, b => Assert.Equal(BodyRole.Mill, b.Role));

        var (pedestal, figure) = (model.Bodies[0].Mesh, model.Bodies[1].Mesh);
        Assert.Equal(11786, pedestal.TriangleCount);
        Assert.Equal(65960, figure.TriangleCount);
        Assert.Equal(165626, pedestal.SignedVolume(), 0);
        Assert.Equal(75845, figure.SignedVolume(), 0);
        Assert.Equal(pedestal.Bounds.Max.Z, figure.Bounds.Min.Z, 4);
        Assert.Equal(208.888f, model.Bounds.Size.Z, 2);
        Assert.All(model.Bodies, b => Assert.True(MeshRepair.IsWatertight(b.Mesh)));
    }

    [Fact]
    public void Oscar3mf_RepairIsANoOpOnCleanMeshes()
    {
        if (ModelPath(Oscar) is not { } path)
        {
            return;
        }

        foreach (var body in MeshImporter.Load(path).Bodies)
        {
            var (repaired, report) = MeshRepair.Repair(body.Mesh);
            Assert.True(report.IsWatertight);
            Assert.Equal(0, report.RemovedDegenerateTriangles + report.RemovedDuplicateTriangles + report.FlippedTriangles + report.HolesFilled);
            Assert.Equal(body.Mesh.TriangleCount, repaired.TriangleCount);
        }
    }

    [Fact]
    public void Oscar3mf_FigureIsFullyReachableAlongTheRotaryAxis()
    {
        if (ModelPath(Oscar) is not { } path)
        {
            return;
        }

        var figure = MeshImporter.Load(path).Bodies[1].Mesh;

        var rotary = OrientationAdvisor.Suggest(figure, MachiningMode.ThreePlusOne);
        Assert.Equal(Vector3.UnitZ, rotary[0].PartAxis);
        Assert.True(rotary[0].ReachableFraction > 0.99);

        // A single top-down setup reaches far less of an upright statuette than 3+1 does.
        var threeAxis = OrientationAdvisor.Suggest(figure, MachiningMode.ThreeAxis);
        Assert.True(threeAxis[0].ReachableFraction < 0.7);
    }

    [Fact]
    public void CarBodyStl_ImportsAsWatertightBinaryMesh()
    {
        if (ModelPath(CarBody) is not { } path)
        {
            return;
        }

        // The 80-byte header starts with "STLB", not "solid"; detection must rely on the size check.
        var model = MeshImporter.Load(path);

        Assert.False(model.UnitsDeclared);
        var body = Assert.Single(model.Bodies);
        Assert.Equal("CarBody", body.Name);
        Assert.Equal(208110, body.Mesh.TriangleCount);
        Assert.Equal(104035, body.Mesh.VertexCount);
        Assert.Equal(621152, body.Mesh.SignedVolume(), 0);
        Assert.Equal(166.221f, model.Bounds.Size.X, 2);
        Assert.Equal(395.618f, model.Bounds.Size.Y, 2);
        Assert.Equal(66.449f, model.Bounds.Size.Z, 2);
        Assert.True(MeshRepair.IsWatertight(body.Mesh));
    }

    [Fact]
    public void CarBodyStl_SurvivesWriteReadRoundTrip()
    {
        if (ModelPath(CarBody) is not { } path)
        {
            return;
        }

        var original = MeshImporter.Load(path).Bodies[0].Mesh;
        using var stream = new MemoryStream();
        StlWriter.Write(stream, original);
        stream.Position = 0;

        var reread = Assert.Single(StlReader.Read(stream, "roundtrip"));

        Assert.Equal(original.TriangleCount, reread.TriangleCount);
        Assert.Equal(original.VertexCount, reread.VertexCount);
        Assert.Equal(original.SignedVolume(), reread.SignedVolume(), 3);
        Assert.Equal(original.Bounds, reread.Bounds);
    }

    [Fact]
    public void CarBodyStl_BvhRaycastHitsTheRoof()
    {
        if (ModelPath(CarBody) is not { } path)
        {
            return;
        }

        var mesh = MeshImporter.Load(path).Bodies[0].Mesh;
        var bvh = new Bvh(mesh);
        var center = mesh.Bounds.Center;
        var origin = new Vector3(center.X, center.Y, mesh.Bounds.Max.Z + 10);

        Assert.True(bvh.Raycast(origin, -Vector3.UnitZ, 1000, out var hit));
        var (a, b, c) = mesh.GetTriangle(hit.Triangle);
        var point = origin - (Vector3.UnitZ * hit.Distance);
        Assert.InRange(point.Z, MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - 1e-3f, MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + 1e-3f);
        Assert.True(mesh.FaceNormal(hit.Triangle).Z > 0);
    }

    private static string? ModelPath(string fileName) => TestModels.Find(fileName);
}
