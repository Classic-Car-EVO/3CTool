using ThreeCTool.FluidNcSimulator;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Recovery;

namespace ThreeCTool.Streaming.Tests;

/// <summary>End-to-end interruption and recovery against the simulated controller (section 11.3).</summary>
public sealed class RecoveryTests
{
    private const string JobId = "job1";

    [Fact]
    public async Task Disconnect_ReconnectAndResume_FinishesAtProgramEnd()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 40 });
        var (lines, final) = Jobs.Zigzag();
        var file = await sim.WriteJobAsync(lines);
        var journal = sim.OpenJournal();
        var connection = await sim.ConnectAsync();
        using (var streamer = new JobStreamer(connection, file, journal, JobId))
        {
            var run = streamer.RunAsync();
            Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 50, SimHarness.Timeout));
            sim.Simulator.DropConnections();
            Assert.Equal(JobState.Interrupted, await run.WaitAsync(SimHarness.Timeout));
        }

        var progress = journal.Load(JobId)!;
        Assert.Equal(InterruptionKind.Disconnect, progress.Interruption);

        // The controller drains what it had buffered while the host is away.
        Assert.True(sim.Simulator.WaitUntil(s => s.State == SimState.Idle, SimHarness.Timeout));
        var linesBeforeRecovery = sim.Simulator.ReceivedLines.Count;
        var resumed = await RecoverAndResumeAsync(sim, file, journal, new RecoveryOptions());

        Assert.Equal(JobState.Completed, resumed.State);
        Assert.True(resumed.Plan.ResumeLine <= progress.Executed);
        Assert.Equal(1, sim.Simulator.HomingCount);
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
        Jobs.AssertPosition(final, sim.Simulator.MachinePosition);
        Assert.Contains(resumed.Operator.Messages, m => m.Contains("12000 RPM", StringComparison.Ordinal));
        Assert.Contains("M8", sim.Simulator.ReceivedLines.Skip(linesBeforeRecovery));
        Assert.False(sim.Simulator.Flood);
    }

    [Fact]
    public async Task EmergencyStop_RehomesAndResumes_CorrectsLostSteps()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 40 });
        var (lines, final) = Jobs.Zigzag();
        var file = await sim.WriteJobAsync(lines);
        var journal = sim.OpenJournal();
        var connection = await sim.ConnectAsync();
        using (var streamer = new JobStreamer(connection, file, journal, JobId))
        {
            var run = streamer.RunAsync();
            Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 50, SimHarness.Timeout));
            sim.Simulator.EmergencyStop();
            Assert.Equal(JobState.Faulted, await run.WaitAsync(SimHarness.Timeout));
        }

        var progress = journal.Load(JobId)!;
        Assert.Equal(InterruptionKind.Alarm, progress.Interruption);
        Assert.Equal(3, progress.InterruptionCode);
        Assert.Equal(0.7, sim.Simulator.MachinePosition[0] - sim.Simulator.PhysicalPosition[0], 6);

        var resumed = await RecoverAndResumeAsync(sim, file, journal, new RecoveryOptions());

        Assert.Equal(JobState.Completed, resumed.State);
        Assert.Equal(2, sim.Simulator.HomingCount);
        Assert.Contains(resumed.Operator.Messages, m => m.Contains("position was lost", StringComparison.Ordinal));
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
        Jobs.AssertPosition(final, sim.Simulator.MachinePosition);
    }

    [Fact]
    public async Task PowerLoss_RestoresLostWorkOffsetAndResumes()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 40 });
        var (lines, final) = Jobs.Zigzag();
        var file = await sim.WriteJobAsync(lines);
        var journal = sim.OpenJournal();
        var connection = await sim.ConnectAsync();
        using (var streamer = new JobStreamer(connection, file, journal, JobId))
        {
            var run = streamer.RunAsync();
            Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 50, SimHarness.Timeout));
            sim.Simulator.PowerLoss();
            Assert.Equal(JobState.Interrupted, await run.WaitAsync(SimHarness.Timeout));
        }

        sim.Simulator.SetWorkOffset(0, 0, 0, 0);
        sim.Simulator.PowerOn();
        Assert.Equal(SimState.Alarm, sim.Simulator.State);

        var resumed = await RecoverAndResumeAsync(sim, file, journal, new RecoveryOptions());

        Assert.Equal(JobState.Completed, resumed.State);
        Assert.Equal(2, sim.Simulator.HomingCount);
        Jobs.AssertPosition(Jobs.G54, sim.Simulator.WorkOffset(0));
        Assert.Contains(journal.GetEvents(JobId), e => e.Kind == JobEventKinds.Recovery && e.Message.StartsWith("G54 restored", StringComparison.Ordinal));
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Fact]
    public async Task EmergencyStop_AfterProbe_ReplaysProbeRoutine()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 40, ProbeSurfaceMachineZ = -350 });
        var (lines, final) = Jobs.Zigzag(probe: true);
        final[2] = -350 + 5;
        var file = await sim.WriteJobAsync(lines);
        var journal = sim.OpenJournal();
        var connection = await sim.ConnectAsync();
        using (var streamer = new JobStreamer(connection, file, journal, JobId))
        {
            var run = streamer.RunAsync();
            Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 50, SimHarness.Timeout));
            Assert.Equal(-350, sim.Simulator.WorkOffset(0)[2], 6);
            sim.Simulator.EmergencyStop();
            Assert.Equal(JobState.Faulted, await run.WaitAsync(SimHarness.Timeout));
        }

        Assert.Equal(1, sim.Simulator.ProbeCount);
        var resumed = await RecoverAndResumeAsync(sim, file, journal, new RecoveryOptions());

        Assert.Equal(JobState.Completed, resumed.State);
        Assert.NotNull(resumed.Plan.Probe);
        Assert.Equal(2, sim.Simulator.ProbeCount);
        Assert.Equal(-350, sim.Simulator.WorkOffset(0)[2], 6);
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Fact]
    public async Task FdmStop_ReheatsPrimesAndResumes()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 40 });
        var (lines, final) = Jobs.Print();
        var file = await sim.WriteJobAsync(lines);
        var journal = sim.OpenJournal();
        var heaters = new FakeHeaters();
        var connection = await sim.ConnectAsync();
        using (var streamer = new JobStreamer(connection, file, journal, JobId, directives: heaters))
        {
            var run = streamer.RunAsync();
            Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 50, SimHarness.Timeout));
            await streamer.StopAsync();
            Assert.Equal(JobState.Stopped, await run.WaitAsync(SimHarness.Timeout));
        }

        Assert.Equal(1, heaters.AllOffCount);
        var options = new RecoveryOptions { Process = JobProcess.Fdm, FdmParkMachine = (20, 20) };
        var resumed = await RecoverAndResumeAsync(sim, file, journal, options, heaters);

        Assert.Equal(JobState.Completed, resumed.State);
        Assert.Equal(1, sim.Simulator.HomingCount);
        Assert.Equal(["MC:HEAT T0 210 WAIT", "MC:HEAT T0 210 WAIT", "MC:HEATERS OFF"], heaters.Executed);
        Assert.Contains(sim.Simulator.ReceivedLines, l => l == "G91G1B5F300");
        Assert.Contains(sim.Simulator.ReceivedLines, l => l == "G53G0X20Y20");
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Fact]
    public async Task FdmEmergencyStop_AbortsPrint()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 40 });
        var file = await sim.WriteJobAsync(Jobs.Print().Lines);
        var journal = sim.OpenJournal();
        var heaters = new FakeHeaters();
        var connection = await sim.ConnectAsync();
        using (var streamer = new JobStreamer(connection, file, journal, JobId, directives: heaters))
        {
            var run = streamer.RunAsync();
            Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 50, SimHarness.Timeout));
            sim.Simulator.EmergencyStop();
            Assert.Equal(JobState.Faulted, await run.WaitAsync(SimHarness.Timeout));
        }

        var plan = RecoveryPlanner.Plan(file, journal.Load(JobId)!, new RecoveryOptions { Process = JobProcess.Fdm });
        Assert.Equal(RecoveryAction.Abort, plan.Action);
        var executor = new RecoveryExecutor(connection, file, journal, JobId, new AutoOperator(), new RecoveryOptions { Process = JobProcess.Fdm }, heaters);
        Assert.Null(await executor.ExecuteAsync(plan));
        Assert.Equal(JobState.Aborted, journal.Load(JobId)!.State);
        Assert.True(heaters.AllOffCount >= 2);
    }

    [Fact]
    public async Task Recovery_OperatorCancels_ReturnsNull()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 40 });
        var file = await sim.WriteJobAsync(Jobs.Zigzag().Lines);
        var journal = sim.OpenJournal();
        var connection = await sim.ConnectAsync();
        using (var streamer = new JobStreamer(connection, file, journal, JobId))
        {
            var run = streamer.RunAsync();
            Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 30, SimHarness.Timeout));
            await streamer.StopAsync();
            await run.WaitAsync(SimHarness.Timeout);
        }

        var plan = RecoveryPlanner.Plan(file, journal.Load(JobId)!, new RecoveryOptions());
        var executor = new RecoveryExecutor(connection, file, journal, JobId, new AutoOperator(confirm: false), new RecoveryOptions());

        Assert.Null(await executor.ExecuteAsync(plan));
        Assert.Equal(SimState.Idle, sim.Simulator.State);
    }

    private static async Task<(JobState State, RecoveryPlan Plan, AutoOperator Operator)> RecoverAndResumeAsync(
        SimHarness sim, JobFile file, SqliteJobJournal journal, RecoveryOptions options, IHostDirectiveHandler? directives = null)
    {
        var connection = await sim.ConnectAsync();
        var plan = RecoveryPlanner.Plan(file, journal.Load(JobId)!, options);
        Assert.Equal(RecoveryAction.Resume, plan.Action);
        var operatorInteraction = new AutoOperator();
        var executor = new RecoveryExecutor(connection, file, journal, JobId, operatorInteraction, options, directives);
        var resume = await executor.ExecuteAsync(plan).WaitAsync(SimHarness.Timeout);
        Assert.NotNull(resume);
        using var streamer = new JobStreamer(connection, file, journal, JobId, directives: directives);
        var state = await streamer.RunAsync(resume.Line, resume.FirstLinePrefix).WaitAsync(SimHarness.Timeout);
        return (state, plan, operatorInteraction);
    }
}
