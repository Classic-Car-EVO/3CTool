using System.Numerics;

using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Feeds;
using ThreeCTool.Toolpaths;

using Xunit.Abstractions;

using static ThreeCTool.Simulation.Tests.Kit;

namespace ThreeCTool.Simulation.Tests;

[Collection(ComputeGroup.Name)]
public sealed class SimulatorTests(ComputeFixture fixture, ITestOutputHelper output)
{
    private static readonly Vector3 StockMin = Vector3.Zero;
    private static readonly Vector3 StockMax = new(40, 20, 10);

    /// <summary>Slot 30 × 6 mm with round ends, 3 mm deep.</summary>
    private const double SlotVolume = (30 * 6 * 3) + (Math.PI * 9 * 3);

    private SimulationResult Run(SimulationJob job)
    {
        var result = Simulator.Run(job);
        output.WriteLine(result.Report.Describe());
        return result;
    }

    private SimulationJob SlotJob(double partTop, params SimulationStep[] steps) =>
        Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, (float)partTop)), BoxStock(StockMin, StockMax), steps);

    [Fact]
    public void Slot_RemovesMaterial_WithoutFindings()
    {
        var step = Milling("slot", Flat(), Slot(5, 35, 10, 7), collet: Collet());
        using var result = Run(SlotJob(5, step));
        var report = result.Report;

        Assert.Empty(report.Errors());
        Assert.False(report.BlocksOutput);
        Assert.Equal(-SlotVolume, report.Steps[0].VolumeChange, SlotVolume * 0.03);
        Assert.Equal(8000 - SlotVolume, result.Stock.Volume(), SlotVolume * 0.03);
        Assert.Equal(0.5f, report.Resolution);

        Assert.NotNull(report.Deviation);
        Assert.Equal(0, report.Deviation.MaxGouge, 3);
        Assert.InRange(report.Deviation.MaxLeftover, 4, 5);
        Assert.NotEmpty(report.Deviation.Samples);

        var times = report.Steps[0].MoveEndTimes;
        Assert.Equal(4, times.Length);
        Assert.Equal(0, times[0]);
        Assert.True(times[2] - times[1] > 30 / (600.0 / 60), "the cut runs at 600 mm/min");
        Assert.Equal(times[^1], report.CycleTime, 9);
    }

    [Fact]
    public void Gouge_IsAttributedToTheMillingStep()
    {
        var step = Milling("low slot", Flat(), [.. Slot(5, 35, 10, 7), Rapid(20, 10, 40), Rapid(20, 10, 7.5), Rapid(20, 10, 40)], collet: Collet());
        using var result = Run(SlotJob(8, step));
        var report = result.Report;

        var gouge = Assert.Single(report.Issues, i => i.Kind == IssueKind.Gouge);
        Assert.Equal(step.Id, gouge.StepId);
        Assert.Equal(IssueSeverity.Error, gouge.Severity);
        Assert.InRange(gouge.FirstMove, 1, 2);
        Assert.NotNull(gouge.Location);
        Assert.InRange(gouge.Location.Value.Z, 6.5f, 8.5f);
        Assert.Equal(1, report.Deviation!.MaxGouge, 1);
        Assert.True(report.BlocksOutput);

        // Where the gouge removed the stock, a rapid back into the slot meets the part itself.
        var hit = Assert.Single(report.Issues, i => i.Kind == IssueKind.PartCollision);
        Assert.Equal(5, hit.FirstMove);
        Assert.Equal("cutter", hit.Body);
        Assert.DoesNotContain(report.Issues, i => i.Kind == IssueKind.RapidCollision);
    }

    [Fact]
    public void PartExtendingBeyondStock_IsReportedOnce_WithoutStep()
    {
        var step = Milling("air", Flat(), Slot(5, 35, 10, 30), collet: Collet());
        using var result = Run(SlotJob(12, step));

        var gouge = Assert.Single(result.Report.Issues, i => i.Kind == IssueKind.Gouge);
        Assert.Null(gouge.StepId);
        Assert.Contains("beyond the stock", gouge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Holder_HittingStock_IsReported()
    {
        // 20 mm stickout but 25 mm deep: the collet nut dives into the 30 mm block.
        var step = Milling("deep", Flat(stickout: 20), Slot(5, 35, 10, 5), collet: Collet());
        var job = Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, 2)), BoxStock(StockMin, new Vector3(40, 20, 30)), [step]);
        using var result = Run(job);
        var report = result.Report;

        var hit = Assert.Single(report.Issues, i => i.Kind == IssueKind.StockCollision);
        Assert.Equal(1, hit.FirstMove);
        Assert.Equal(2, hit.LastMove);
        Assert.Contains("holder 1", hit.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("cutter", hit.Body, StringComparison.Ordinal);
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.RapidCollision && i.FirstMove == 3);
        Assert.True(report.BlocksOutput);
        Assert.False(report.MayWriteOutput(userOverride: false));
        Assert.True(report.MayWriteOutput(userOverride: true));
    }

    [Fact]
    public void RapidThroughStock_IsReported_AndRemovesNothing()
    {
        var step = Milling("rapid", Flat(), [Rapid(-10, 10, 40), Rapid(-10, 10, 5), Rapid(50, 10, 5), Rapid(50, 10, 40)]);
        using var result = Run(SlotJob(2, step));
        var report = result.Report;

        var hit = Assert.Single(report.Issues, i => i.Kind == IssueKind.RapidCollision);
        Assert.Equal(2, hit.FirstMove);
        Assert.Equal(2, hit.LastMove);
        Assert.Contains("cutter", hit.Body, StringComparison.Ordinal);
        Assert.Equal(0, report.Steps[0].VolumeChange, 6);
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.MissingData && i.Message.Contains("holder", StringComparison.Ordinal));
    }

    [Fact]
    public void LaterSteps_SeeMaterialRemovedByEarlierSteps()
    {
        static SimulationStep Rapids() => Milling("rapid in slot", Flat(), [Rapid(5, 10, 40), Rapid(5, 10, 8), Rapid(35, 10, 8), Rapid(35, 10, 40)], collet: Collet());

        using var alone = Run(SlotJob(5, Rapids()));
        Assert.Contains(alone.Report.Issues, i => i.Kind == IssueKind.RapidCollision);

        var slot = Milling("slot", Flat(), Slot(5, 35, 10, 7), collet: Collet());
        var again = Milling("slot again", Flat(), Slot(5, 35, 10, 7), collet: Collet());
        using var sequence = Run(SlotJob(5, slot, Rapids(), again));
        var report = sequence.Report;

        Assert.Empty(report.Errors());
        Assert.Equal(3, report.Steps.Count);
        Assert.Equal(-SlotVolume, report.Steps[0].VolumeChange, SlotVolume * 0.03);
        Assert.Equal(0, report.Steps[1].VolumeChange, 6);
        Assert.Equal(0, report.Steps[2].VolumeChange, 1);
        Assert.Equal(report.Steps.Sum(s => s.Duration), report.CycleTime, 9);

        Assert.Equal((0, 0), report.Locate(0));
        Assert.Equal(1, report.Locate(report.Steps[0].Duration + 1e-3)!.Value.Step);
        Assert.Equal((2, 3), report.Locate(report.CycleTime));
        Assert.Null(report.Locate(report.CycleTime + 1));
    }

    [Fact]
    public void EngagementLimiter_SlowsTheSlot_AndKeepsNominalPasses()
    {
        var material = new Material
        {
            Name = "aluminium",
            SpecificCuttingForce = 700,
            KienzleExponent = 0.23,
            CuttingData = [new CuttingData { SurfaceSpeedMin = 150, SurfaceSpeedMax = 400, ChipLoad = [new ChipLoadPoint { Diameter = 6, ChipLoad = 0.04 }] }],
        };
        var tool = Flat();
        var engine = new FeedsEngine(new FeedContext { Machine = Machine(), Material = material });
        var feeds = engine.Plan([new FeedRequest { Strategy = StrategyType.AdaptiveRoughing, Tool = tool, Stepover = 1.5, Stepdown = 3, Tolerance = 0.05, RadialStock = 0.5 }]).Operations[0];
        MillingStep Limited(string name, double y, bool limit = true) => new()
        {
            Name = name,
            SetupId = SetupId,
            Tool = tool,
            Collet = Collet(),
            Conditions = feeds.ToConditions(),
            Limiter = limit ? feeds.Limiter : null,
            Toolpath = new Toolpath { Frame = SetupFrame.Identity, Moves = [.. Slot(5, 35, y, 7)] },
        };

        // Ray centres at 0.125 + k·0.25 put the 6 mm slot and the 1.5 mm side pass exactly on whole rows.
        var job = Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, 5)), BoxStock(StockMin, StockMax), [Limited("slot", 10), Limited("side", 11.5)], o => o.Resolution = 0.25f);
        using var result = Run(job);
        var report = result.Report;

        Assert.Empty(report.Errors());
        var slot = report.Steps[0].FeedFactors;
        var side = report.Steps[1].FeedFactors;
        Assert.Equal(4, slot.Length);
        Assert.InRange(slot[2], 0.1f, 0.95f);
        Assert.Equal(1f, slot[1]);
        Assert.Equal(1f, side[2]);
        var issue = Assert.Single(report.Issues, i => i.Kind == IssueKind.FeedReduced);
        Assert.Equal((report.Steps[0].StepId, 2, 2), (issue.StepId!.Value, issue.FirstMove, issue.LastMove));

        // The slowed cut takes longer, at most 1 / factor as long (acceleration ramps take the same time or less).
        var nominal = Limited("slot", 10, limit: false);
        using var plain = Run(Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, 5)), BoxStock(StockMin, StockMax), [nominal], o => o.Resolution = 0.25f));
        var slowed = report.Steps[0].MoveEndTimes[2] - report.Steps[0].MoveEndTimes[1];
        var fast = plain.Report.Steps[0].MoveEndTimes[2] - plain.Report.Steps[0].MoveEndTimes[1];
        Assert.InRange(slowed / fast, 1.03, (1 / slot[2]) + 1e-6);
        Assert.All(plain.Report.Steps[0].FeedFactors, f => Assert.Equal(1f, f));
    }

    [Fact]
    public void RotaryGroove_RemovesAnnulus()
    {
        var frame = new SetupFrame(SetupOrientation.SimultaneousRotary, 0, 0, 0, 0);
        var step = Milling("groove", Flat(), [Rapid(20, 0, 20), Plunge(20, 0, 8), Cut(20, 0, 8, 360), Rapid(20, 0, 20, 360)], frame, Collet());
        var stock = new Stock { Type = StockType.Cylinder, Length = 40, Diameter = 20 };
        var job = Job(fixture.Compute, SolidMeshes.XCylinder(0, 40, 0, 0, 7, 0.01), stock, [step]);
        using var result = Run(job);
        var report = result.Report;

        Assert.Empty(report.Errors());
        var annulus = Math.PI * (100 - 64) * 6;
        Assert.Equal(-annulus, report.Steps[0].VolumeChange, annulus * 0.05);

        // A turns 360° at no more than 1000°/min.
        var times = report.Steps[0].MoveEndTimes;
        Assert.True(times[2] - times[1] >= 360 / (1000.0 / 60));
    }

    [Fact]
    public void Deposition_AddsBeads_CountsFilament_AndDetectsHeadOnPrintedMaterial()
    {
        var head = new Toolhead
        {
            Name = "sprite",
            Type = ToolheadType.Fdm,
            CollisionModel = [new SimpleSolid { Name = "hotend", Shape = SolidShape.Cylinder, BottomZ = 0.2, Height = 30, Radius = 3 }],
        };
        static PrintMove Travel(double x, double y, double z) => new(PrintMoveKind.Travel, x, y, z, 0, 0, 0, 0);
        var step = new DepositionStep
        {
            Name = "print",
            SetupId = SetupId,
            Frame = SetupFrame.Identity,
            Head = head,
            Filament = new Filament { Name = "PLA", Diameter = 1.75, Density = 1.24 },
            Moves =
            [
                Travel(5, 10, 20),
                Travel(5, 10, 11),
                new PrintMove(PrintMoveKind.Extrude, 35, 10, 11, 0, 2, 1, 1200),
                Travel(35, 10, 15),
                Travel(20, -5, 15),
                Travel(20, -5, 10.5),
                Travel(20, 25, 10.5),
                Travel(20, 25, 20),
            ],
        };
        var job = Job(fixture.Compute, Box(StockMin, StockMax), BoxStock(StockMin, StockMax), [step], o => o.Resolution = 0.25f);
        using var result = Run(job);
        var report = result.Report;

        var bead = step.Moves[2].BeadArea * 30;
        Assert.Equal(1 + (Math.PI / 4), step.Moves[2].BeadArea, 9);
        Assert.InRange(report.Steps[0].VolumeChange, 0.85 * bead, 1.25 * bead);
        var filament = bead / (Math.PI * 1.75 * 1.75 / 4);
        Assert.Equal(filament, report.Steps[0].FilamentLength, 6);
        Assert.Equal(bead * 1.24 / 1000, report.Steps[0].FilamentMass, 9);

        // The head clears the stock but not the 1 mm bead it just printed.
        var hit = Assert.Single(report.Errors());
        Assert.Equal(IssueKind.RapidCollision, hit.Kind);
        Assert.Equal(6, hit.FirstMove);
        Assert.Equal("hotend", hit.Body);

        // Extrusion at 1200 mm/min.
        var times = report.Steps[0].MoveEndTimes;
        Assert.True(times[2] - times[1] >= 30 / (1200.0 / 60));
    }

    [Theory]
    [InlineData(KeepOutAttachment.SetupFixed, true)]
    [InlineData(KeepOutAttachment.Setup, false)]
    public void KeepOuts_FollowTheirAttachment(KeepOutAttachment attachment, bool expectHit)
    {
        // Indexed at 90°: the WCS +Z points along the part's +Y (or −Y) axis, so a solid placed above the WCS origin
        // is only in the way when it does not turn with the part.
        var frame = new SetupFrame(SetupOrientation.Indexed, 0, 0, 0, 90);
        var step = Milling("index 90", Flat(), [Rapid(-20, 0, 40, 90), Rapid(60, 0, 40, 90)], frame, Collet());
        var job = Job(
            fixture.Compute,
            Box(new Vector3(-5, -4, -4), new Vector3(45, 4, 4)),
            BoxStock(new Vector3(-5, -5, -5), new Vector3(45, 5, 5)),
            [step],
            o => o.KeepOuts.Add(new KeepOutZone { Attachment = attachment, SetupId = SetupId, Solid = Clamp() }));
        using var result = Run(job);

        var hits = result.Report.Issues.Where(i => i.Kind == IssueKind.KeepOutCollision).ToList();
        if (expectHit)
        {
            var hit = Assert.Single(hits);
            Assert.Equal(1, hit.FirstMove);
            Assert.Contains("cutter", hit.Body, StringComparison.Ordinal);
            Assert.Contains("fixed", hit.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(hits);
        }
    }

    [Fact]
    public void MachineKeepOuts_NeedTheWorkOffset()
    {
        var step = Milling("air", Flat(), [Rapid(-20, 10, 40), Rapid(60, 10, 40)], collet: Collet());
        var clamp = Clamp();
        (clamp.CenterX, clamp.CenterY, clamp.BottomZ) = (120, 210, -270);
        var zone = new KeepOutZone { Attachment = KeepOutAttachment.Machine, Solid = clamp };

        using var unknown = Run(Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, 5)), BoxStock(StockMin, StockMax), [step], o => o.KeepOuts.Add(zone)));
        Assert.DoesNotContain(unknown.Report.Issues, i => i.Kind == IssueKind.KeepOutCollision);
        Assert.Contains(unknown.Report.Issues, i => i.Kind == IssueKind.MissingData && i.Message.Contains("work offset", StringComparison.Ordinal));

        using var known = Run(Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, 5)), BoxStock(StockMin, StockMax), [step], o =>
        {
            o.KeepOuts.Add(zone);
            o.WorkOffsets[SetupId] = new Vector3(100, 200, -300);
        }));
        var hit = Assert.Single(known.Report.Issues, i => i.Kind == IssueKind.KeepOutCollision);
        Assert.Equal(1, hit.FirstMove);
    }

    [Fact]
    public void Travel_SpanAndAbsoluteLimits()
    {
        var wide = Milling("wide", Flat(), [Rapid(-10, 10, 50), Cut(120, 10, 50)], collet: Collet());
        using var span = Run(Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, 5)), BoxStock(StockMin, StockMax), [wide], o => o.Machine = Machine(travelX: 100)));
        var limit = Assert.Single(span.Report.Issues, i => i.Kind == IssueKind.TravelLimit);
        Assert.Contains("130", limit.Message, StringComparison.Ordinal);

        var machine = Machine();
        var x = machine.GetAxis(AxisName.X)!;
        (x.HomingCycle, x.HomingPositiveDirection, x.HomingMachinePosition) = (1, false, 0);
        var left = Milling("left", Flat(), [Rapid(60, 10, 50), Cut(20, 10, 50)], collet: Collet());
        using var absolute = Run(Job(fixture.Compute, Box(StockMin, new Vector3(40, 20, 5)), BoxStock(StockMin, StockMax), [left], o =>
        {
            o.Machine = machine;
            o.WorkOffsets[SetupId] = new Vector3(-50, 300, -400);
        }));
        var outside = Assert.Single(absolute.Report.Issues, i => i.Kind == IssueKind.TravelLimit);
        Assert.Equal(1, outside.FirstMove);
        Assert.Contains("-30", outside.Message, StringComparison.Ordinal);
    }

    /// <summary>4 × 4 mm post from z 30 to 50 above (20, 0).</summary>
    private static SimpleSolid Clamp() => new() { Name = "clamp", Shape = SolidShape.Box, CenterX = 20, CenterY = 0, BottomZ = 30, Height = 20, SizeX = 4, SizeY = 4 };
}
