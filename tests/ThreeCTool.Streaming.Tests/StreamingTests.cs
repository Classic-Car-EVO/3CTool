using ThreeCTool.FluidNcSimulator;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming.Tests;

public sealed class StreamingTests
{
    [Fact]
    public async Task RunAsync_CharacterCounting_CompletesWithoutRxOverflow()
    {
        await using var sim = await SimHarness.StartAsync();
        var (lines, final) = Jobs.Zigzag();
        var file = await sim.WriteJobAsync(lines);
        var setupLines = sim.Simulator.ReceivedLines.Count;
        var connection = await sim.ConnectAsync();
        var journal = sim.OpenJournal();
        using var streamer = new JobStreamer(connection, file, journal, "job1");

        var state = await streamer.RunAsync().WaitAsync(SimHarness.Timeout);

        Assert.Equal(JobState.Completed, state);
        Assert.Equal(0, sim.Simulator.RxOverflows);
        Assert.InRange(sim.Simulator.MaxRxFill, 1, sim.Simulator.Options.RxBufferSize);
        Assert.True(connection.MaxOutstandingBytes > 40, $"Character counting keeps several lines in flight ({connection.MaxOutstandingBytes} bytes).");
        Assert.InRange(connection.MaxOutstandingBytes, 1, 128);
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
        Jobs.AssertPosition(final, sim.Simulator.MachinePosition);

        // Every code line reached the controller, in order and without comments.
        var expected = lines.Select(Jobs.Wire).Where(l => l.Length > 0).ToList();
        var received = sim.Simulator.ReceivedLines.Skip(setupLines).Where(l => l is not ("$#" or "G4P0")).ToList();
        Assert.Equal(expected, received);

        var progress = journal.Load("job1")!;
        Assert.Equal(JobState.Completed, progress.State);
        Assert.Equal(file.LineCount, progress.Executed);
        Assert.Equal(file.LineCount, progress.LastAcked);
        Assert.NotNull(progress.WorkCoordinates);
        Assert.Equal(new AxisVector(100, 100, -300, 0, 0), WorkCoordinates.Deserialize(progress.WorkCoordinates).Get("G54"));
        Assert.Contains(journal.GetEvents("job1"), e => e.Kind == JobEventKinds.Started);
    }

    [Fact]
    public async Task RunAsync_SendResponse_KeepsOneLineInFlight()
    {
        await using var sim = await SimHarness.StartAsync();
        var (lines, final) = Jobs.Zigzag(40);
        var file = await sim.WriteJobAsync(lines);
        var connection = await sim.ConnectAsync(new ConnectionOptions { FlowControl = FlowControl.SendResponse, StatusInterval = TimeSpan.FromMilliseconds(20) });
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1");

        Assert.Equal(JobState.Completed, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));
        Assert.True(connection.MaxOutstandingBytes <= lines.Max(l => Jobs.Wire(l).Length) + 1);
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Fact]
    public async Task RunAsync_LineNumbers_ReportsExactExecutedLine()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { UseLineNumbers = true, TimeScale = 20 });
        var (lines, final) = Jobs.Zigzag(60);
        var file = await sim.WriteJobAsync(lines);
        var setupLines = sim.Simulator.ReceivedLines.Count;
        var connection = await sim.ConnectAsync();
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1", new JobStreamerOptions { UseLineNumbers = true });

        var run = streamer.RunAsync();
        var exactWhileRunning = false;
        while (!run.IsCompleted)
        {
            var progress = streamer.Progress;
            if (progress is { State: JobState.Running, ExecutedExact: true, Executed: > 10 } && progress.Executed < progress.LastSent)
            {
                exactWhileRunning = true;
            }

            await Task.Delay(5);
        }

        Assert.Equal(JobState.Completed, await run);
        Assert.True(exactWhileRunning);
        var received = sim.Simulator.ReceivedLines.Skip(setupLines).ToList();
        Assert.DoesNotContain(received, l => (l.StartsWith('G') && l != "G4P0") || l.StartsWith('M'));
        Assert.StartsWith("N2G21", received.First(l => l.StartsWith('N')));
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Fact]
    public async Task PauseAsync_HoldsMotion_ResumeCompletes()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var (lines, final) = Jobs.Zigzag(80);
        var file = await sim.WriteJobAsync(lines);
        var connection = await sim.ConnectAsync();
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1");
        var run = streamer.RunAsync();
        Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 15, SimHarness.Timeout));

        await streamer.PauseAsync();
        Assert.Equal(JobState.Paused, streamer.State);
        await connection.WaitForStatusAsync(s => s is { State: MachineState.Hold, SubState: 0 }, SimHarness.Timeout);
        var held = sim.Simulator.PhysicalPosition;
        var blocks = sim.Simulator.ExecutedBlocks;
        await Task.Delay(150);
        Jobs.AssertPosition(held, sim.Simulator.PhysicalPosition, 1e-9);
        Assert.Equal(blocks, sim.Simulator.ExecutedBlocks);

        await streamer.ResumeAsync();
        Assert.Equal(JobState.Completed, await run.WaitAsync(SimHarness.Timeout));
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Fact]
    public async Task RunAsync_M0_RaisesPromptAndWaitsForCycleStart()
    {
        await using var sim = await SimHarness.StartAsync();
        var (lines, final) = Jobs.Zigzag(60, pauseAt: 30);
        var file = await sim.WriteJobAsync(lines);
        var connection = await sim.ConnectAsync();
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1");
        var prompt = new TaskCompletionSource<JobPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
        streamer.PromptRequested += (_, message) => prompt.TrySetResult(message);
        var run = streamer.RunAsync();

        var shown = await prompt.Task.WaitAsync(SimHarness.Timeout);
        Assert.Equal("Change to tool 2", shown.Message);
        Assert.True(Jobs.Wire(lines[shown.Line - 1]).StartsWith('M'), $"Line {shown.Line} is the M0.");
        Assert.Equal(JobState.ProgramPaused, streamer.State);
        await Task.Delay(100);
        Assert.False(run.IsCompleted);
        Assert.Equal(SimState.Hold, sim.Simulator.State);

        await streamer.ResumeAsync();
        Assert.Equal(JobState.Completed, await run.WaitAsync(SimHarness.Timeout));
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Theory]
    [InlineData(true, "G0X10Y10", "G0X20Y20")]
    [InlineData(false, "G0X20Y20", "G0X10Y10")]
    public async Task RunAsync_Question_StreamsOnlyTheChosenBranch(bool yes, string sent, string skipped)
    {
        await using var sim = await SimHarness.StartAsync();
        string[] lines =
        [
            "G21 G90 G54", "G0 Z-1",
            "(Probe at X10 Y10?)", "(MC:ASK BEGIN)", "G0 X10 Y10", "(Yes branch)", "(MC:ASK ELSE)", "G0 X20 Y20", "(No branch)", "(MC:ASK END)",
            "G0 X5 Y5", "M0", "G0 X0 Y0",
        ];
        var file = await sim.WriteJobAsync(lines);
        var setupLines = sim.Simulator.ReceivedLines.Count;
        var connection = await sim.ConnectAsync();
        var journal = sim.OpenJournal();
        using var streamer = new JobStreamer(connection, file, journal, "job1");
        Assert.Contains(streamer.Validate(), p => p.Contains("no operator", StringComparison.Ordinal));
        var questions = new List<JobQuestion>();
        var states = new List<JobState>();
        streamer.AskOperator = (question, _) =>
        {
            questions.Add(question);
            states.Add(streamer.State);
            return Task.FromResult<bool?>(yes);
        };
        var prompts = new List<JobPrompt>();
        streamer.PromptRequested += (_, prompt) =>
        {
            prompts.Add(prompt);
            _ = Task.Run(streamer.ResumeAsync);
        };
        Assert.Empty(streamer.Validate());

        Assert.Equal(JobState.Completed, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));
        Assert.Equal([new JobQuestion("Probe at X10 Y10?", 4)], questions);
        Assert.Equal([JobState.ProgramPaused], states);
        var received = sim.Simulator.ReceivedLines.Skip(setupLines).ToList();
        Assert.Contains(sent, received);
        Assert.DoesNotContain(skipped, received);
        Assert.Equal([new JobPrompt(yes ? "Yes branch" : "No branch", 12)], prompts);
        Assert.Contains(journal.GetEvents("job1"), e => e.Message == $"Answered {(yes ? "Yes" : "No")}: Probe at X10 Y10?");
    }

    [Fact]
    public async Task RunAsync_QuestionCancelled_StopsTheJob()
    {
        await using var sim = await SimHarness.StartAsync();
        var file = await sim.WriteJobAsync(["G21 G90 G54", "(Go on?)", "(MC:ASK BEGIN)", "G0 X10", "(MC:ASK ELSE)", "(MC:ASK END)", "G0 X20"]);
        var setupLines = sim.Simulator.ReceivedLines.Count;
        var connection = await sim.ConnectAsync();
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1") { AskOperator = (_, _) => Task.FromResult<bool?>(null) };

        Assert.Equal(JobState.Stopped, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));
        Assert.DoesNotContain(sim.Simulator.ReceivedLines.Skip(setupLines), l => l.StartsWith("G0X", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_MalformedQuestions_AreReported()
    {
        await using var sim = await SimHarness.StartAsync();
        var file = await sim.WriteJobAsync(["(MC:ASK ELSE)", "(MC:ASK BEGIN)", "(MC:ASK BEGIN)", "(MC:ASK ELSE)", "(MC:ASK ELSE)"]);
        var connection = await sim.ConnectAsync();
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1") { AskOperator = (_, _) => Task.FromResult<bool?>(true) };

        var problems = streamer.Validate();
        Assert.Contains("Line 1: unexpected (MC:ASK ELSE).", problems);
        Assert.Contains(problems, p => p.StartsWith("Line 3: questions cannot be nested", StringComparison.Ordinal));
        Assert.Contains("Line 5: unexpected (MC:ASK ELSE).", problems);
        Assert.Contains("Line 3: the question has no (MC:ASK END).", problems);
    }

    [Fact]
    public async Task StopAsync_HoldsResetsAndRetracts()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var file = await sim.WriteJobAsync(Jobs.Zigzag().Lines);
        var connection = await sim.ConnectAsync();
        var journal = sim.OpenJournal();
        using var streamer = new JobStreamer(connection, file, journal, "job1");
        var run = streamer.RunAsync();
        Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 20, SimHarness.Timeout));

        await streamer.StopAsync().WaitAsync(SimHarness.Timeout);
        Assert.Equal(JobState.Stopped, await run.WaitAsync(SimHarness.Timeout));

        Assert.Equal(SimState.Idle, sim.Simulator.State);
        Assert.True(sim.Simulator.IsHomed, "A stop from a completed hold must not lose position.");
        Assert.Equal(-5, sim.Simulator.MachinePosition[2], 6);
        Jobs.AssertPosition(sim.Simulator.MachinePosition, sim.Simulator.PhysicalPosition, 1e-9);
        Assert.Equal(0, connection.OutstandingLines);

        var progress = journal.Load("job1")!;
        Assert.Equal(JobState.Stopped, progress.State);
        Assert.Equal(InterruptionKind.Stop, progress.Interruption);
        Assert.InRange(progress.Executed, 8, progress.LastSent);

        // The journal keeps the position where the cut was interrupted, not the retract.
        Assert.Equal(-301, AxisVector.Parse(progress.MachinePosition!).Z, 3);
    }

    [Fact]
    public async Task RunAsync_ControllerError_FaultsAndHolds()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var file = await sim.WriteJobAsync(Jobs.Zigzag(80, errorAt: 40).Lines);
        var connection = await sim.ConnectAsync();
        var heaters = new FakeHeaters();
        var journal = sim.OpenJournal();
        using var streamer = new JobStreamer(connection, file, journal, "job1", directives: heaters);

        Assert.Equal(JobState.Faulted, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));

        var progress = journal.Load("job1")!;
        Assert.Equal(InterruptionKind.Error, progress.Interruption);
        Assert.Equal(20, progress.InterruptionCode);
        var badLine = Enumerable.Range(1, file.LineCount).First(l => file.ReadLine(l) == "G5 X1");
        Assert.Contains(journal.GetEvents("job1"), e => e.Kind == JobEventKinds.Error && e.Line == badLine);
        await connection.WaitForStatusAsync(s => s is { State: MachineState.Hold, SubState: 0 }, SimHarness.Timeout);
        Assert.Equal(1, heaters.AllOffCount);
    }

    [Fact]
    public async Task RunAsync_SoftLimitAlarm_FaultsAndRequiresResync()
    {
        await using var sim = await SimHarness.StartAsync();
        var (lines, _) = Jobs.Zigzag(60);
        lines.Insert(40, "G0 X5000");
        var file = await sim.WriteJobAsync(lines);
        var connection = await sim.ConnectAsync();
        var journal = sim.OpenJournal();
        using var streamer = new JobStreamer(connection, file, journal, "job1");

        Assert.Equal(JobState.Faulted, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));

        var progress = journal.Load("job1")!;
        Assert.Equal(InterruptionKind.Alarm, progress.Interruption);
        Assert.Equal(FluidNcCodes.AlarmSoftLimit, progress.InterruptionCode);
        Assert.Equal(SimState.Alarm, sim.Simulator.State);
        Assert.True(connection.NeedsResync);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ExecuteAsync("G4P0"));

        await connection.ResynchronizeAsync();
        Assert.False(connection.NeedsResync);
        var unlock = await connection.UnlockAsync();
        Assert.True(unlock.IsOk);
    }

    [Fact]
    public async Task RunAsync_HeaterDirective_ExecutesBeforeFollowingLines()
    {
        await using var sim = await SimHarness.StartAsync();
        var (lines, final) = Jobs.Print(30);
        var file = await sim.WriteJobAsync(lines);
        var connection = await sim.ConnectAsync();
        var heaters = new FakeHeaters();
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1", directives: heaters);

        Assert.Equal(JobState.Completed, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));
        Assert.Equal(["MC:HEAT T0 210 WAIT", "MC:HEATERS OFF"], heaters.Executed);
        Jobs.AssertPosition(final, sim.Simulator.PhysicalPosition);
    }

    [Fact]
    public async Task RunAsync_FailingDirective_FaultsJob()
    {
        await using var sim = await SimHarness.StartAsync();
        var file = await sim.WriteJobAsync(Jobs.Print(30).Lines);
        var connection = await sim.ConnectAsync();
        var heaters = new FakeHeaters { Failure = new IOException("Moonraker unreachable") };
        var journal = sim.OpenJournal();
        using var streamer = new JobStreamer(connection, file, journal, "job1", directives: heaters);

        Assert.Equal(JobState.Faulted, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));
        Assert.Equal(InterruptionKind.Directive, journal.Load("job1")!.Interruption);
        Assert.True(heaters.AllOffCount >= 1);
    }

    [Fact]
    public async Task Validate_MissingDirectiveHandler_Throws()
    {
        await using var sim = await SimHarness.StartAsync(home: false);
        var file = await sim.WriteJobAsync(Jobs.Print(5).Lines);
        var connection = await sim.ConnectAsync();
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1");

        Assert.Equal(2, streamer.Validate().Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => streamer.RunAsync());
    }

    [Fact]
    public async Task Validate_MachineZOutsideTheTravelOrBelowTheTable_IsReported()
    {
        await using var sim = await SimHarness.StartAsync(home: false);
        var file = await sim.WriteJobAsync(
        [
            "(MC:TOOL STICKOUT 20)", "G21 G90 G54", "G53 G0 Z-5", "G0 X10 Y10", "G53 G0 Z5", "G53 G1 Z-90 F100", "G0 Z-500", "G53 X0",
        ]);
        var connection = await sim.ConnectAsync();
        var options = new JobStreamerOptions { SafeZMachine = 3, MachineZTravel = (-100, 0), TableMachineZ = -100 };
        using var streamer = new JobStreamer(connection, file, sim.OpenJournal(), "job1", options);

        var problems = streamer.Validate();

        Assert.Equal(3, problems.Count);
        Assert.StartsWith("The safe Z in the feeder settings: machine Z 3", problems[0], StringComparison.Ordinal);
        Assert.StartsWith("Line 5: machine Z 5", problems[1], StringComparison.Ordinal);
        Assert.StartsWith("Line 6: machine Z -90", problems[2], StringComparison.Ordinal);
        Assert.Contains("stickout 20", problems[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WorkMoveBelowTheTable_FaultsBeforeSendingIt()
    {
        await using var sim = await SimHarness.StartAsync();
        var file = await sim.WriteJobAsync(
        [
            "(MC:TOOL STICKOUT 20)", "G21 G90", "G10 L2 P1 X0 Y0 Z-600", "G54", "G0 Z-20", "G1 Z-29 F500", "G1 Z-31", "G0 Z0",
        ]);
        var setupLines = sim.Simulator.ReceivedLines.Count;
        var connection = await sim.ConnectAsync();
        var journal = sim.OpenJournal();
        var options = new JobStreamerOptions { MachineZTravel = (-650, 0), TableMachineZ = -650 };
        using var streamer = new JobStreamer(connection, file, journal, "job1", options);
        Assert.Empty(streamer.Validate());

        Assert.Equal(JobState.Faulted, await streamer.RunAsync().WaitAsync(SimHarness.Timeout));

        // Lines 4-6 are streamed without waiting for acks; the fault can come before the simulator has read them.
        Assert.True(sim.Simulator.WaitUntil(s => s.ReceivedLines.Contains("G1Z-29F500"), SimHarness.Timeout));
        var received = sim.Simulator.ReceivedLines.Skip(setupLines).ToList();
        Assert.Contains("G1Z-29F500", received);
        Assert.DoesNotContain("G1Z-31", received);
        Assert.True(sim.Simulator.MachinePosition[2] >= -629 - 1e-6);
        Assert.Contains(journal.GetEvents("job1"), e => e.Kind == JobEventKinds.Error && e.Line == 7 && e.Message.Contains("stickout 20", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetFeedOverrideAsync_ReachesControllerAndJournal()
    {
        await using var sim = await SimHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var file = await sim.WriteJobAsync(Jobs.Zigzag(60).Lines);
        var connection = await sim.ConnectAsync();
        var journal = sim.OpenJournal();
        using var streamer = new JobStreamer(connection, file, journal, "job1");
        var run = streamer.RunAsync();
        Assert.True(sim.Simulator.WaitUntil(s => s.ExecutedBlocks > 5, SimHarness.Timeout));

        await streamer.SetFeedOverrideAsync(137);

        Assert.True(sim.Simulator.WaitUntil(s => s.FeedOverride == 137, SimHarness.Timeout));
        await connection.WaitForStatusAsync(s => s.Overrides?.Feed == 137, SimHarness.Timeout);
        Assert.Equal(JobState.Completed, await run.WaitAsync(SimHarness.Timeout));
        Assert.Equal(137, journal.Load("job1")!.FeedOverride);
    }

    [Fact]
    public async Task JobStreamer_DifferentFileForJob_Throws()
    {
        await using var sim = await SimHarness.StartAsync(home: false);
        var journal = sim.OpenJournal();
        var first = await sim.WriteJobAsync(Jobs.Zigzag(5).Lines, "a.nc");
        journal.Record(new JobProgress { JobId = "job1", FileSha256 = first.Sha256, State = JobState.Stopped }, flush: true);
        var other = await sim.WriteJobAsync(Jobs.Zigzag(6).Lines, "b.nc");
        var connection = await sim.ConnectAsync();

        Assert.Throws<InvalidOperationException>(() => new JobStreamer(connection, other, journal, "job1"));
    }
}
