using System.Numerics;

namespace ThreeCTool.Rendering.Tests;

public sealed class OrbitCameraTests
{
    [Fact]
    public void Fit_ShowsEveryCornerOfTheBox()
    {
        var camera = new OrbitCamera();
        var min = new Vector3(-10, 0, -5);
        var max = new Vector3(200, 80, 3);
        foreach (var aspect in new[] { 0.5f, 1f, 16f / 9 })
        {
            foreach (var view in Enum.GetValues<CameraView>())
            {
                camera.SetView(view);
                camera.Fit(min, max, aspect);
                Assert.Equal((min + max) / 2, camera.Target);
                var viewProjection = camera.ViewProjection(aspect);
                foreach (var corner in Corners(min, max))
                {
                    var p = OrbitCamera.Project(corner, viewProjection, 1000 * aspect, 1000);
                    Assert.NotNull(p);
                    Assert.InRange(p.Value.X, 0, 1000 * aspect);
                    Assert.InRange(p.Value.Y, 0, 1000);
                }
            }
        }
    }

    [Fact]
    public void TopView_LooksDownWithXRightAndYUp()
    {
        var camera = new OrbitCamera();
        camera.SetView(CameraView.Top);
        camera.Fit(new Vector3(-50, -50, 0), new Vector3(50, 50, 0), 1);
        var viewProjection = camera.ViewProjection(1);

        var centre = OrbitCamera.Project(Vector3.Zero, viewProjection, 100, 100)!.Value;
        var plusX = OrbitCamera.Project(new Vector3(10, 0, 0), viewProjection, 100, 100)!.Value;
        var plusY = OrbitCamera.Project(new Vector3(0, 10, 0), viewProjection, 100, 100)!.Value;

        Assert.Equal(50, centre.X, 2);
        Assert.Equal(50, centre.Y, 2);
        Assert.True(plusX.X > centre.X + 1 && Math.Abs(plusX.Y - centre.Y) < 0.1f);
        Assert.True(plusY.Y < centre.Y - 1 && Math.Abs(plusY.X - centre.X) < 0.1f);
        Assert.True(camera.Eye.Z > 0);
    }

    [Fact]
    public void Pan_MovesContentWithTheFinger()
    {
        var camera = new OrbitCamera { Target = new Vector3(5, 5, 0), Distance = 100 };
        var point = new Vector3(5, 5, 0);
        var before = OrbitCamera.Project(point, camera.ViewProjection(1), 400, 400)!.Value;

        camera.Pan(40, -25, 400);

        var after = OrbitCamera.Project(point, camera.ViewProjection(1), 400, 400)!.Value;
        Assert.Equal(before.X + 40, after.X, 1);
        Assert.Equal(before.Y - 25, after.Y, 1);
    }

    [Fact]
    public void OrbitAndZoom_StayWithinLimits()
    {
        var camera = new OrbitCamera { MinDistance = 2, MaxDistance = 500, Distance = 100 };
        camera.Orbit(0.5f, 10);
        Assert.True(camera.Pitch < MathF.PI / 2);
        camera.Orbit(0, -20);
        Assert.True(camera.Pitch > -MathF.PI / 2);

        camera.Zoom(0.001f);
        Assert.Equal(2, camera.Distance);
        camera.Zoom(1e6f);
        Assert.Equal(500, camera.Distance);
        camera.Zoom(float.NaN);
        camera.Zoom(-1);
        Assert.Equal(500, camera.Distance);
    }

    [Fact]
    public void Project_PointBehindTheEye_IsNull()
    {
        var camera = new OrbitCamera { Distance = 10 };
        var behind = camera.Eye + (camera.Direction * 5);
        Assert.Null(OrbitCamera.Project(behind, camera.ViewProjection(1), 100, 100));
    }

    private static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
    {
        for (var i = 0; i < 8; i++)
        {
            yield return new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        }
    }
}
