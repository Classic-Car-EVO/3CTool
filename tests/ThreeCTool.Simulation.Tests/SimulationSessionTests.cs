using System.Numerics;

using static ThreeCTool.Simulation.Tests.Kit;

namespace ThreeCTool.Simulation.Tests;

[Collection(ComputeGroup.Name)]
public sealed class SimulationSessionTests(ComputeFixture fixture)
{
    private SimulationJob TwoSlots() => Job(
        fixture.Compute,
        Box(Vector3.Zero, new Vector3(40, 20, 5)),
        BoxStock(Vector3.Zero, new Vector3(40, 20, 10)),
        [Milling("first", Flat(), Slot(5, 35, 10, 7), collet: Collet()), Milling("second", Flat(), Slot(5, 35, 16, 8), collet: Collet())]);

    [Fact]
    public void Seek_ForwardAndBack_ReproducesTheStock()
    {
        var job = TwoSlots();
        using var session = SimulationSession.Create(job, maxCheckpoints: 2);
        Assert.Equal(2, session.StepCount);
        Assert.Equal(4, session.MoveCount(0));
        Assert.Equal(0.5f, session.Resolution);
        var full = session.Stock.Volume();
        Assert.Equal(8000, full, 3);

        session.Seek(0, 1);
        var plunged = session.Stock.Volume();
        Assert.Equal(full - (Math.PI * 9 * 3), plunged, 5.0);

        session.Seek(0, 3);
        var first = session.Stock.Volume();
        Assert.True(first < plunged - 400);

        session.Seek(1, 2);
        var midSecond = session.Stock.Volume();
        Assert.True(midSecond < first);
        Assert.Equal((1, 2), (session.Step, session.Move));

        session.Seek(2, 0);
        var end = session.Stock.Volume();
        using (var result = Simulator.Run(job))
        {
            Assert.Equal(result.Stock.Volume(), end, 6);
        }

        // Backwards: from the end to inside the first step and the very start.
        session.Seek(0, 1);
        Assert.Equal(plunged, session.Stock.Volume(), 6);
        session.Seek(1, 2);
        Assert.Equal(midSecond, session.Stock.Volume(), 6);
        session.Seek(1, 0);
        Assert.Equal(first, session.Stock.Volume(), 6);
        session.Seek(0, 0);
        Assert.Equal(full, session.Stock.Volume(), 6);

        // Move indices are clamped to the step.
        session.Seek(0, 99);
        Assert.Equal((0, 3), (session.Step, session.Move));
        Assert.Equal(first, session.Stock.Volume(), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Seek(3, 0));
    }

    [Fact]
    public void ToolAt_ReturnsMoveTargets()
    {
        using var session = SimulationSession.Create(TwoSlots());
        Assert.Equal(new Vector3(5, 10, 40), session.ToolAt(0, 0).Tip);
        var (tip, axis) = session.ToolAt(0, 2);
        Assert.Equal(new Vector3(35, 10, 7), tip);
        Assert.Equal(Vector3.UnitZ, axis);
        Assert.Equal(new Vector3(35, 16, 40), session.ToolAt(1, 3).Tip);
    }
}
