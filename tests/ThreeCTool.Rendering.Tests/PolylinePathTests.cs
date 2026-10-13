using System.Numerics;

namespace ThreeCTool.Rendering.Tests;

public sealed class PolylinePathTests
{
    [Fact]
    public void Strips_AndBounds()
    {
        var path = new PolylinePath(
            [0, 0, 5, 10, 0, 5, 10, 0, -1, 20, 5, -1, 20, 5, 5],
            [3, 4, 5, 6, 7],
            [0, 2, 4],
            [true, false, true]);

        Assert.Equal(5, path.VertexCount);
        Assert.Equal((0, 2), path.Strip(0));
        Assert.Equal((2, 2), path.Strip(1));
        Assert.Equal((4, 1), path.Strip(2));
        Assert.Equal(new Vector3(0, 0, -1), path.Min);
        Assert.Equal(new Vector3(20, 5, 5), path.Max);
        Assert.Equal(new Vector3(20, 5, -1), path.Vertex(3));
    }

    [Fact]
    public void InvalidData_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new PolylinePath([0, 0], [1], [0], [false]));
        Assert.Throws<ArgumentException>(() => new PolylinePath([0, 0, 0], [1], [0], []));
        Assert.Throws<ArgumentException>(() => new PolylinePath([0, 0, 0, 1, 1, 1], [1, 2], [1, 0], [false, false]));
        Assert.Equal(0, PolylinePath.Empty.VertexCount);
    }
}
