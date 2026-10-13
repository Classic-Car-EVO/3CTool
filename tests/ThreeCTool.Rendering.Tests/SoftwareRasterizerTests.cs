using System.Numerics;

using ThreeCTool.TestSupport;

namespace ThreeCTool.Rendering.Tests;

public sealed class SoftwareRasterizerTests
{
    private const int Size = 101;
    private const int Centre = Size / 2;

    private static readonly SceneMesh Part =
        new(SceneLayer.Part, "part", TestMeshes.Box(new Vector3(-10, -10, -10), new Vector3(10, 10, 0)), SceneColors.Part);

    private static CamScene TopView(params SceneMesh[] meshes)
    {
        var scene = new CamScene { Aspect = 1 };
        scene.SetMeshes(meshes);
        scene.SetView(CameraView.Top);
        return scene;
    }

    private static uint PartTop(CamScene scene) => SoftwareRasterizer.Pack(SceneColors.Shade(SceneColors.Part, Vector3.UnitZ, scene.Camera.Direction));

    private static uint Render(CamScene scene)
    {
        var rasterizer = new SoftwareRasterizer();
        rasterizer.Render(scene, Size, Size);
        Assert.Equal(SoftwareRasterizer.Pack(ToolpathColors.Background), rasterizer[0, 0]);
        return rasterizer[Centre, Centre];
    }

    private static SceneToolpath Line(float z) =>
        new(Guid.NewGuid(), "op", new PolylinePath([-5, 0, z, 5, 0, z], [0, 1], [0], [false]), SceneColors.Operation(1));

    [Fact]
    public void OpaqueMesh_ShadedTowardsTheEye()
    {
        var scene = TopView(Part);
        Assert.Equal(PartTop(scene), Render(scene));
    }

    [Fact]
    public void TranslucentStock_BlendsOverThePart()
    {
        var stock = new SceneMesh(SceneLayer.Stock, "stock", TestMeshes.Box(new Vector3(-12, -12, -10), new Vector3(12, 12, 4)), SceneColors.Stock);
        var scene = TopView(Part, stock);

        var pixel = Render(scene);
        Assert.NotEqual(PartTop(scene), pixel);
        Assert.NotEqual(SoftwareRasterizer.Pack(ToolpathColors.Background), pixel);

        scene.SetVisible(SceneLayer.Stock, false);
        Assert.Equal(PartTop(scene), Render(scene));
    }

    [Fact]
    public void Toolpath_OnTheSurfaceIsDrawn_BelowItIsHidden()
    {
        var scene = TopView(Part);
        var onTop = Line(0);
        scene.SetToolpaths([onTop]);
        Assert.Equal(SoftwareRasterizer.Pack(onTop.Color), Render(scene));

        scene.Progress = (0, 1);
        Assert.Equal(SoftwareRasterizer.Pack(ToolpathColors.Done), Render(scene));

        scene.SetVisible(SceneLayer.Toolpath, false);
        Assert.Equal(PartTop(scene), Render(scene));

        scene.SetVisible(SceneLayer.Toolpath, true);
        scene.SetToolpaths([Line(-15)]);
        Assert.Equal(PartTop(scene), Render(scene));
    }

    [Fact]
    public void Points_AndToolMarker_AreDrawn()
    {
        var scene = TopView(Part);
        scene.SetPoints([new ScenePoints(SceneLayer.Simulation, "heat", [new Vector3(0, 0, 0.5f)], [Heatmap.Gouge], 1)]);
        Assert.Equal(SoftwareRasterizer.Pack(Heatmap.Gouge), Render(scene));

        scene.SetPoints([]);
        scene.Tool = new ToolMarker(new Vector3(0, 0, 1), Vector3.UnitZ, 4, 10);
        var withTool = Render(scene);
        Assert.NotEqual(PartTop(scene), withTool);
        Assert.True(((withTool >> 16) & 0xFF) > ((PartTop(scene) >> 16) & 0xFF), "The red tool tints the part.");
    }

    [Fact]
    public void NearPlaneTriangles_AreSkippedWithoutThrowing()
    {
        var scene = TopView(Part);
        scene.Camera.Distance = 1;
        scene.Camera.Target = new Vector3(0, 0, -5);
        var rasterizer = new SoftwareRasterizer();
        rasterizer.Render(scene, 40, 30);
        Assert.Equal(40 * 30, rasterizer.Pixels.Length);
    }
}
