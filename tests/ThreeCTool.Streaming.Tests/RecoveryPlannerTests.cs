using Microsoft.Data.Sqlite;

using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Recovery;

namespace ThreeCTool.Streaming.Tests;

public sealed class RecoveryPlannerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "3ctool-planner-" + Guid.NewGuid().ToString("N"));

    public RecoveryPlannerTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task Plan_NothingExecuted_Restarts()
    {
        var file = await WriteAsync(Jobs.Zigzag(20).Lines);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 0), new RecoveryOptions());
        Assert.Equal(RecoveryAction.Restart, plan.Action);
        Assert.Equal(1, plan.ResumeLine);
    }

    [Fact]
    public async Task Plan_FdmAfterAlarm_Aborts()
    {
        var file = await WriteAsync(Jobs.Print(20).Lines);
        var progress = Progress(file, 15) with { Interruption = InterruptionKind.Alarm, InterruptionCode = 3 };
        var plan = RecoveryPlanner.Plan(file, progress, new RecoveryOptions { Process = JobProcess.Fdm });
        Assert.Equal(RecoveryAction.Abort, plan.Action);
    }

    [Fact]
    public async Task Plan_FdmAfterStop_ResumesAtInterruptedLineWithoutMargin()
    {
        var file = await WriteAsync(Jobs.Print(20).Lines);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 20), new RecoveryOptions { Process = JobProcess.Fdm });
        Assert.Equal(RecoveryAction.Resume, plan.Action);
        Assert.Equal(20, plan.ResumeLine);
    }

    [Fact]
    public async Task Plan_Milling_WalksBackOneSegmentAndRestoresModalState()
    {
        // Move i of the zigzag is on line 8 + i.
        var file = await WriteAsync(Jobs.Zigzag(60).Lines);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 28), new RecoveryOptions());

        Assert.Equal(RecoveryAction.Resume, plan.Action);
        Assert.Equal(28, plan.InterruptedLine);
        Assert.Equal(27, plan.ResumeLine);
        Assert.Null(plan.FirstLinePrefix);
        Assert.Null(plan.Probe);
        Assert.Equal([0, 9, -1], plan.ResumePosition.Take(3));
        Assert.Equal(3, plan.State.Spindle);
        Assert.Equal(12000, plan.State.SpindleSpeed);
        Assert.True(plan.State.Flood);
        Assert.Equal(3000, plan.State.Feed);
    }

    [Fact]
    public async Task Plan_EstimatedLine_CrossCheckedAgainstReportedPosition()
    {
        var file = await WriteAsync(Jobs.Zigzag(60).Lines);

        // Halfway along move 20 (line 28) in work coordinates (20, 9.75, -1); the line estimate says move 30.
        var progress = Progress(file, 38) with
        {
            ExecutedExact = false,
            MachinePosition = "120.000,109.750,-301.000,0.000,0.000",
            WorkOffset = "100.000,100.000,-300.000,0.000,0.000",
        };

        var estimated = RecoveryPlanner.Plan(file, progress, new RecoveryOptions());
        Assert.Equal(28, estimated.InterruptedLine);
        Assert.Equal(27, estimated.ResumeLine);

        var exact = RecoveryPlanner.Plan(file, progress with { ExecutedExact = true }, new RecoveryOptions());
        Assert.Equal(38, exact.InterruptedLine);

        var offPath = RecoveryPlanner.Plan(file, progress with { MachinePosition = "500.000,500.000,-301.000,0.000,0.000" }, new RecoveryOptions());
        Assert.Equal(38, offPath.InterruptedLine);
    }

    [Fact]
    public async Task Plan_MarginStopsAtBarrier()
    {
        // M0 before move 20: moves 1-19 on lines 9-27, M0 on 28, move 20 on 29.
        var file = await WriteAsync(Jobs.Zigzag(60, pauseAt: 20).Lines);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 29), new RecoveryOptions());
        Assert.Equal(29, plan.ResumeLine);
        Assert.Contains(plan.Notes, n => n.Contains("shortened", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_InsideProbeBlock_RerunsWholeRoutine()
    {
        // Probe block on lines 3-10; line 6 is the G38.2.
        var file = await WriteAsync(Jobs.Zigzag(60, probe: true).Lines);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 6), new RecoveryOptions());
        Assert.Equal(RecoveryAction.Resume, plan.Action);
        Assert.Equal(3, plan.ResumeLine);
        Assert.Null(plan.Probe);
    }

    [Fact]
    public async Task Plan_AfterProbeBlock_ReplaysIt()
    {
        // Move i is on line 16 + i when the probe block is present.
        var file = await WriteAsync(Jobs.Zigzag(60, probe: true).Lines);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 36), new RecoveryOptions());
        Assert.Equal(35, plan.ResumeLine);
        Assert.NotNull(plan.Probe);
        Assert.Equal(3, plan.Probe.BeginLine);
        Assert.Equal(10, plan.Probe.EndLine);
        Assert.Equal(0, plan.ResumePosition[0]);
    }

    [Fact]
    public async Task Plan_ResumeLineWithoutMotionWord_GetsPrefix()
    {
        var file = await WriteAsync(["G21 G90 G54", "G0 X0 Y0 Z5", "G1 Z-1 F300", "X10", "X20", "X30"]);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 6), new RecoveryOptions());
        Assert.Equal(5, plan.ResumeLine);
        Assert.Equal("G1", plan.FirstLinePrefix);
    }

    [Fact]
    public async Task Plan_UnknownPosition_IsUnrecoverable()
    {
        var file = await WriteAsync(["G21 G90 G54", "G1 X10 F100", "G1 X20", "G1 X30"]);
        var plan = RecoveryPlanner.Plan(file, Progress(file, 4), new RecoveryOptions());
        Assert.Equal(RecoveryAction.Unrecoverable, plan.Action);
    }

    [Fact]
    public async Task Plan_DifferentFile_Throws()
    {
        var file = await WriteAsync(Jobs.Zigzag(20).Lines);
        Assert.Throws<InvalidOperationException>(() => RecoveryPlanner.Plan(file, Progress(file, 5) with { FileSha256 = "0" }, new RecoveryOptions()));
    }

    private static JobProgress Progress(JobFile file, int executed) => new()
    {
        JobId = "job",
        FileSha256 = file.Sha256,
        State = JobState.Stopped,
        Interruption = InterruptionKind.Stop,
        LastSent = executed,
        LastAcked = executed,
        Executed = executed,
        ExecutedExact = true,
    };

    private async Task<JobFile> WriteAsync(IEnumerable<string> lines)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".nc");
        await File.WriteAllLinesAsync(path, lines);
        return await JobFile.OpenAsync(path);
    }
}
