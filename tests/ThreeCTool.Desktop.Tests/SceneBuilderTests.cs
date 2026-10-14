using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Geometry;
using ThreeCTool.Rendering;
using ThreeCTool.TestSupport;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Desktop.Tests;

public sealed class SceneBuilderTests
{
    [Fact]
    public void ToPath_SplitsRapidAndFeedStrips_WithMoveNumbers()
    {
        var toolpath = new Toolpath
        {
            Moves =
            [
                new(MoveKind.Rapid, 0, 0, 10),
                new(MoveKind.Rapid, 0, 0, 1),
                new(MoveKind.Plunge, 0, 0, -1),
                new(MoveKind.Cut, 10, 0, -1),
                new(MoveKind.Rapid, 10, 0, 10),
            ],
        };

        var path = SceneBuilder.ToPath(toolpath);

        Assert.Equal([0, 2, 5], path.StripStarts);
        Assert.Equal([true, false, true], path.StripRapid);
        Assert.Equal([0, 1, 1, 2, 3, 3, 4], path.Lines);
        Assert.Equal(new Vector3(10, 0, -1), path.Vertex(4));
        Assert.Equal(new Vector3(10, 0, 10), path.Vertex(6));
        Assert.Same(PolylinePath.Empty, SceneBuilder.ToPath(new Toolpath()));
    }

    [Fact]
    public void ToPath_SimultaneousRotaryMoves_FollowTheArcInThePartFrame()
    {
        var toolpath = new Toolpath
        {
            Frame = new SetupFrame(SetupOrientation.SimultaneousRotary, 0, 0, 0, 0),
            Moves = [new(MoveKind.Cut, 0, 0, 5, 0), new(MoveKind.Cut, 0, 0, 5, 90)],
        };

        var path = SceneBuilder.ToPath(toolpath);

        Assert.Equal(1 + (int)(90 / SceneBuilder.MaxRotaryStep), path.VertexCount);
        Assert.All(Enumerable.Range(0, path.VertexCount), i =>
            Assert.Equal(5, new Vector2(path.Vertex(i).Y, path.Vertex(i).Z).Length(), 4));
        Assert.Equal(0, path.Vertex(path.VertexCount - 1).Z, 4);
        Assert.Equal(5, path.Vertex(path.VertexCount - 1).Y, 4);
        Assert.All(path.Lines.Skip(1), line => Assert.Equal(1, line));
    }

    [Fact]
    public void ProjectMeshes_PlacesSetupFixedZonesInThePartFrame_AndSkipsMachineZones()
    {
        var setupId = Guid.NewGuid();
        var body = new MeshBody("body", BodyRole.Mill, TestMeshes.Box(Vector3.Zero, new Vector3(10)));
        var part = new LoadedPart(new PartModel { Name = "part" }, [body], body.Mesh.Bounds);
        static KeepOutZone Zone(KeepOutAttachment attachment, Guid? setup, string name) => new()
        {
            Attachment = attachment,
            SetupId = setup,
            Solid = new SimpleSolid { Name = name, Shape = SolidShape.Box, SizeX = 2, SizeY = 2, Height = 2 },
        };

        var meshes = SceneBuilder.ProjectMeshes(
            [part],
            TestMeshes.Box(new Vector3(-1), new Vector3(11)),
            [
                Zone(KeepOutAttachment.Setup, setupId, "clamp"),
                Zone(KeepOutAttachment.SetupFixed, setupId, "tailstock"),
                Zone(KeepOutAttachment.SetupFixed, Guid.NewGuid(), "unknown setup"),
                Zone(KeepOutAttachment.Machine, null, "wall"),
            ],
            id => id == setupId ? new SetupFrame(SetupOrientation.ThreeAxis, 5, 0, 0, 0) : null);

        Assert.Equal(["part/body", "Stock", "clamp", "tailstock"], meshes.Select(m => m.Name));
        Assert.Equal([SceneLayer.Part, SceneLayer.Stock, SceneLayer.KeepOut, SceneLayer.KeepOut], meshes.Select(m => m.Layer));
        Assert.Equal(-1, meshes[2].Mesh.Bounds.Min.X, 4);
        Assert.Equal(4, meshes[3].Mesh.Bounds.Min.X, 4);
        Assert.True(meshes[1].Color.W < 1);
    }

    [Fact]
    public void SimulationPoints_AreColouredByDeviationAndHeight()
    {
        var heat = SceneBuilder.DeviationPoints([new DeviationSample(Vector3.Zero, -1), new DeviationSample(Vector3.One, 0)], 0.01, 1, 0.5f);
        Assert.Equal([Heatmap.Gouge, Heatmap.OnSize], heat.Colors);
        Assert.Equal(0.5f, heat.Size);

        var stock = SceneBuilder.StockPoints([new Vector3(0, 0, 0), new Vector3(0, 0, 10)], 0.5f);
        Assert.True(stock.Colors[0].X < stock.Colors[1].X);
        Assert.Equal(SceneColors.SimulatedStock, stock.Colors[1]);
    }

    [Fact]
    public void ToolMarker_UsesStickoutAndToolAxis()
    {
        var frame = new SetupFrame(SetupOrientation.Indexed, 0, 0, 0, 90);
        var marker = SceneBuilder.ToolMarker(frame, new ToolpathMove(MoveKind.Cut, 0, 0, 5, 90), DesktopFixture.FlatEndMill());

        Assert.Equal(6, marker.Diameter);
        Assert.Equal(25, marker.Length);
        Assert.Equal(0, marker.Tip.Z, 4);
        Assert.Equal(5, marker.Tip.Y, 4);
        Assert.Equal(1, marker.Axis.Y, 4);
    }
}
