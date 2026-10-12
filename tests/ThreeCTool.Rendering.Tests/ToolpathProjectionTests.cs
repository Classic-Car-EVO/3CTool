using System.Numerics;

namespace ThreeCTool.Rendering.Tests;

public sealed class ToolpathProjectionTests
{
    // Rapid strip v0-v1 (lines 1-2), cut strip v2-v5 (lines 3-6).
    private static PolylinePath Path() => new(
        [0, 0, 5, 10, 0, 5, 10, 0, -1, 20, 0, -1, 20, 10, -1, 0, 10, -1],
        [1, 2, 3, 4, 5, 6],
        [0, 2],
        [true, false]);

    private static ToolpathScene Scene()
    {
        var scene = new ToolpathScene { Aspect = 1.5f };
        scene.SetPath(Path());
        scene.SetView(CameraView.Top);
        return scene;
    }

    [Fact]
    public void Styles_SplitAtProgressLine()
    {
        var scene = Scene();

        var initial = ToolpathProjection.Project(scene, 300, 200);
        Assert.Equal([PathStyle.Rapid, PathStyle.Cut], initial.Select(p => p.Style));
        Assert.Equal(4, initial[1].Points.Count);

        scene.ProgressLine = 4;
        var progressed = ToolpathProjection.Project(scene, 300, 200);
        Assert.Equal([PathStyle.Done, PathStyle.Done, PathStyle.Cut], progressed.Select(p => p.Style));
        Assert.Equal(2, progressed[1].Points.Count);
        Assert.Equal(3, progressed[2].Points.Count);
        Assert.Equal(progressed[1].Points[^1], progressed[2].Points[0]);
    }

    [Fact]
    public void Rapids_CanBeHidden_AndPointsAreOnScreen()
    {
        var scene = Scene();
        scene.ShowRapids = false;

        var lines = ToolpathProjection.Project(scene, 300, 200);

        var only = Assert.Single(lines);
        Assert.Equal(PathStyle.Cut, only.Style);
        Assert.All(only.Points, p => Assert.True(p.X is >= 0 and <= 300 && p.Y is >= 0 and <= 200, $"{p} off screen"));

        // Top view: +Y in the path is up on screen.
        Assert.True(only.Points[3].Y < only.Points[0].Y);
    }

    [Fact]
    public void Scene_RaisesChanged_AndResetsProgressOnNewPath()
    {
        var scene = Scene();
        var changes = 0;
        scene.Changed += (_, _) => changes++;

        scene.ProgressLine = 3;
        scene.ProgressLine = 3;
        scene.Marker = new Vector3(1, 2, 3);
        Assert.Equal(2, changes);

        var version = scene.PathVersion;
        scene.SetPath(Path());
        Assert.Equal(0, scene.ProgressLine);
        Assert.Equal(version + 1, scene.PathVersion);
        Assert.Empty(ToolpathProjection.Project(new ToolpathScene(), 100, 100));
    }
}
