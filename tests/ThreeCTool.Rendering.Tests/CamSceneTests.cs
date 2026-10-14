using System.Numerics;

using ThreeCTool.TestSupport;

namespace ThreeCTool.Rendering.Tests;

public sealed class CamSceneTests
{
    private static SceneToolpath Toolpath(string name, params float[] positions) =>
        new(Guid.NewGuid(), name, new PolylinePath(positions, [.. Enumerable.Range(0, positions.Length / 3)], [0], [false]), SceneColors.Operation(0));

    [Fact]
    public void ContentChanges_BumpVersion_DisplayChangesOnlyRedraw()
    {
        var scene = new CamScene();
        var changes = 0;
        scene.Changed += (_, _) => changes++;

        scene.SetMeshes([new SceneMesh(SceneLayer.Part, "box", TestMeshes.Box(Vector3.Zero, Vector3.One, "box"), SceneColors.Part)]);
        Assert.Equal(1, scene.Version);
        scene.ShowRapids = false;
        scene.SetVisible(SceneLayer.Stock, false);
        scene.SetVisible(SceneLayer.Stock, false);

        Assert.Equal(1, scene.Version);
        Assert.Equal(3, changes);
        Assert.False(scene.IsVisible(SceneLayer.Stock));
    }

    [Fact]
    public void Progress_MarksEarlierPathsDone_AndSelectionDimsOthers()
    {
        var scene = new CamScene();
        var a = Toolpath("a", 0, 0, 0, 1, 0, 0);
        var b = Toolpath("b", 0, 1, 0, 1, 1, 0);
        var c = Toolpath("c", 0, 2, 0, 1, 2, 0);
        scene.SetToolpaths([a, b, c]);

        Assert.Equal(0, scene.ProgressLine(0));
        scene.Progress = (1, 7);
        Assert.Equal([int.MaxValue, 7, 0], [scene.ProgressLine(0), scene.ProgressLine(1), scene.ProgressLine(2)]);

        Assert.Equal(1f, scene.ToolpathColor(0).W);
        scene.SelectedToolpath = b.Id;
        Assert.Equal(CamScene.DimFactor, scene.ToolpathColor(0).W, 5);
        Assert.Equal(1f, scene.ToolpathColor(1).W);
        Assert.Equal(ToolpathColors.Rapid.W * CamScene.DimFactor, scene.RapidColor(2).W, 5);
    }

    [Fact]
    public void Bounds_CoverVisibleLayersOnly()
    {
        var scene = new CamScene();
        Assert.Null(scene.Bounds());
        scene.Fit();

        scene.SetMeshes([new SceneMesh(SceneLayer.Stock, "stock", TestMeshes.Box(new Vector3(-5), new Vector3(5), "stock"), SceneColors.Stock)]);
        scene.SetToolpaths([Toolpath("a", 0, 0, 0, 20, 0, 0)]);
        scene.SetPoints([new ScenePoints(SceneLayer.Simulation, "p", [new Vector3(0, 0, 30)], [Heatmap.OnSize], 1)]);

        Assert.Equal((new Vector3(-5), new Vector3(20, 5, 30)), scene.Bounds()!.Value);
        scene.SetVisible(SceneLayer.Simulation, false);
        scene.SetVisible(SceneLayer.Toolpath, false);
        Assert.Equal((new Vector3(-5), new Vector3(5)), scene.Bounds()!.Value);

        scene.Fit();
        Assert.Equal(Vector3.Zero, scene.Camera.Target);
    }

    [Fact]
    public void ScenePoints_NeedOneColourPerPoint() =>
        Assert.Throws<ArgumentException>(() => new ScenePoints(SceneLayer.Simulation, "p", [Vector3.Zero, Vector3.One], [Heatmap.OnSize], 1));

    [Fact]
    public void Heatmap_GreenOnSize_RedGouge_BlueLeftover()
    {
        Assert.Equal(Heatmap.OnSize, Heatmap.Color(0.005f, 0.01f, 1));
        Assert.Equal(Heatmap.OnSize, Heatmap.Color(-0.01f, 0.01f, 1));
        Assert.Equal(Heatmap.Gouge, Heatmap.Color(-2, 0.01f, 1));
        Assert.Equal(Heatmap.Leftover, Heatmap.Color(1, 0.01f, 1));
        Assert.Equal(Heatmap.OnSize, Heatmap.Color(float.NaN, 0.01f, 1));

        var half = Heatmap.Color(0.5f, 0, 1);
        Assert.InRange(half.Z, Heatmap.OnSize.Z + 0.1f, Heatmap.Leftover.Z - 0.1f);
    }

    [Fact]
    public void ToolMesh_IsCylinderAlongAxis_AndFlatVerticesCoverEveryCorner()
    {
        var mesh = SceneGeometry.ToolMesh(new ToolMarker(new Vector3(1, 2, 3), new Vector3(2, 0, 0), 4, 10));

        var bounds = mesh.Bounds;
        Assert.Equal(1, bounds.Min.X, 4);
        Assert.Equal(11, bounds.Max.X, 4);
        Assert.Equal(0, bounds.Min.Y, 3);
        Assert.Equal(4, bounds.Max.Y, 3);
        Assert.Equal(1, bounds.Min.Z, 3);
        Assert.Equal(5, bounds.Max.Z, 3);
        Assert.True(mesh.SignedVolume() > 0);
        Assert.InRange(Math.Abs(mesh.SignedVolume()), 0.95 * Math.PI * 4 * 10, Math.PI * 4 * 10);

        Assert.Equal(mesh.TriangleCount * 18, SceneGeometry.FlatVertices(mesh).Length);
    }
}
