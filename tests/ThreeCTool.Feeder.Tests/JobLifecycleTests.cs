using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.FluidNcSimulator;

namespace ThreeCTool.Feeder.Tests;

public sealed class JobLifecycleTests
{
    [Fact]
    public async Task Upload_ListPreviewAndDeduplicate()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var (client, _) = await feeder.PairAsync();
        var (lines, _) = TestJobs.Zigzag(20);

        var job = await FeederHarness.UploadAsync(client, lines, "bracket.nc");
        Assert.Equal(lines.Count, job.LineCount);
        Assert.Equal("bracket.nc", job.Header.Name);

        var again = await FeederHarness.UploadAsync(client, lines, "bracket-v2.nc");
        Assert.Equal(job.JobId, again.JobId);
        var listed = Assert.Single((await client.ListJobsAsync(new Empty())).Jobs);
        Assert.Equal("bracket-v2.nc", listed.Header.Name);

        var preview = await client.GetPreviewAsync(new JobRef { JobId = job.JobId });
        Assert.Equal(job.JobId, preview.JobId);
        Assert.True(preview.Positions.Count >= 3 * 20);
        Assert.Equal(preview.Positions.Count, 3 * preview.Lines.Count);
        Assert.Equal(preview.StripStarts.Count, preview.StripRapid.Count);
        Assert.Equal(0f, preview.BoundsMin[0], 3);
        Assert.Equal(40f, preview.BoundsMax[0], 3);
        Assert.Equal(-1f, preview.BoundsMin[2], 3);
        Assert.False(preview.Rotary);

        await client.DeleteJobAsync(new JobRef { JobId = job.JobId });
        Assert.Empty((await client.ListJobsAsync(new Empty())).Jobs);
        var missing = await Assert.ThrowsAsync<RpcException>(async () => await client.GetPreviewAsync(new JobRef { JobId = job.JobId }));
        Assert.Equal(StatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Upload_WithWrongHash_IsRejected()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            FeederHarness.UploadAsync(feeder.Local, ["G0 X1", "M30"], header: h => h.Sha256 = new string('a', 64)));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);

        var badName = await Assert.ThrowsAsync<RpcException>(() =>
            FeederHarness.UploadAsync(feeder.Local, ["G0 X1", "M30"], name: ""));
        Assert.Equal(StatusCode.InvalidArgument, badName.StatusCode);
        Assert.Empty((await feeder.Local.ListJobsAsync(new Empty())).Jobs);
        Assert.Empty(Directory.GetFiles(Path.Combine(feeder.Folder, "jobs"), "*.tmp"));
    }

    [Fact]
    public async Task Job_RunsToCompletion_ThenFeedbackIsCollected()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var (client, _) = await feeder.PairAsync();
        var (lines, final) = TestJobs.Zigzag(60);
        var job = await FeederHarness.UploadAsync(client, lines, header: h => h.Tags[FeederTags.MaterialId] = Guid.NewGuid().ToString());

        var reply = await client.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        Assert.True(reply.Started, string.Join(" ", reply.Warnings));
        var running = await feeder.WaitStatusAsync(s => s.Job is { State: "Running" } j && j.RunId == reply.RunId);
        Assert.Equal(job.JobId, running.Job.JobId);
        Assert.Equal(12000, running.Job.PlannedRpm);

        // Manual controls are refused while streaming.
        var busy = await Assert.ThrowsAsync<RpcException>(async () => await client.HomeAsync(new Empty()));
        Assert.Equal(StatusCode.FailedPrecondition, busy.StatusCode);

        var end = await feeder.RunToEndAsync(reply.RunId);
        Assert.Equal("Completed", end.Job.State);
        Assert.Equal(1.0, end.Job.Progress, 3);
        Assert.Equal(0, end.Job.RemainingSeconds, 3);
        TestJobs.AssertPosition(final, feeder.Simulator.MachinePosition);
        Assert.Equal(reply.RunId, (await feeder.WaitStatusAsync(s => s.FeedbackRunId.Length > 0)).FeedbackRunId);

        var runs = await client.ListRunsAsync(new ListRunsRequest { JobId = job.JobId });
        var run = Assert.Single(runs.Runs);
        Assert.Equal("Completed", run.State);
        Assert.False(run.HasFeedback);
        var log = await client.GetRunLogAsync(new RunRef { RunId = reply.RunId });
        Assert.NotEmpty(log.Events);
        Assert.Equal(Enumerable.Range(1, log.Events.Count).Select(i => (long)i), log.Events.Select(e => e.Sequence));

        // Feedback is submitted at the machine and downloaded by the desktop.
        await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission
        {
            RunId = reply.RunId,
            FinishRating = 4,
            ChatterRating = 5,
            WearRating = 3,
            WearEvent = WearEvent.Wear,
            Notes = "Light burr on the left edge",
        });
        Assert.Empty((await feeder.WaitStatusAsync(s => s.FeedbackRunId.Length == 0)).FeedbackRunId);
        var feedback = await client.ListFeedbackAsync(new ListFeedbackRequest());
        var record = Assert.Single(feedback.Records);
        Assert.Equal(reply.RunId, record.RunId);
        Assert.Equal("Completed", record.FinalState);
        Assert.Equal(4, record.Feedback.FinishRating);
        Assert.Equal("Light burr on the left edge", record.Feedback.Notes);
        Assert.Equal(job.Header.Tags[FeederTags.MaterialId], record.Job.Tags[FeederTags.MaterialId]);
        Assert.True(record.RuntimeSeconds > 0);

        await client.AcknowledgeFeedbackAsync(new FeedbackAck { RunIds = { reply.RunId } });
        Assert.Empty((await client.ListFeedbackAsync(new ListFeedbackRequest())).Records);
        Assert.Single((await client.ListFeedbackAsync(new ListFeedbackRequest { IncludeAcknowledged = true })).Records);
        Assert.True((await client.ListRunsAsync(new ListRunsRequest())).Runs.Single().HasFeedback);
    }

    [Fact]
    public async Task ProgramPause_WithSpindleOn_AsksForDialRpmAtTheMachine()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var (client, _) = await feeder.PairAsync();
        var (lines, final) = TestJobs.Zigzag(40, pauseAt: 20);
        var job = await FeederHarness.UploadAsync(client, lines);
        var reply = await client.StartJobAsync(new StartJobRequest { JobId = job.JobId });

        var paused = await feeder.WaitStatusAsync(s => s.Prompt is not null);
        Assert.Equal(PromptKind.Spindle, paused.Prompt.Kind);
        Assert.Equal(12000, paused.Prompt.SuggestedRpm);
        Assert.Equal(lines.IndexOf("M0 (Check the clamps)") + 1, paused.Prompt.Line);
        Assert.Equal("ProgramPaused", paused.Job.State);

        // Remote resume is refused: the operator confirms at the machine.
        var remote = await Assert.ThrowsAsync<RpcException>(async () => await client.ResumeAsync(new Empty()));
        Assert.Equal(StatusCode.FailedPrecondition, remote.StatusCode);
        var wrongId = await Assert.ThrowsAsync<RpcException>(async () =>
            await feeder.Local.AnswerPromptAsync(new PromptAnswer { Id = "nope", Confirmed = true }));
        Assert.NotEqual(StatusCode.OK, wrongId.StatusCode);

        await feeder.Local.AnswerPromptAsync(new PromptAnswer { Id = paused.Prompt.Id, Confirmed = true, Rpm = 15000 });
        var end = await feeder.RunToEndAsync(reply.RunId);
        Assert.Equal("Completed", end.Job.State);
        TestJobs.AssertPosition(final, feeder.Simulator.MachinePosition);

        await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission { RunId = reply.RunId, Skipped = true });
        var record = Assert.Single((await client.ListFeedbackAsync(new ListFeedbackRequest())).Records);
        var rpm = Assert.Single(record.RpmChanges);
        Assert.Equal(15000, rpm.Rpm);
        Assert.Equal(paused.Prompt.Line, rpm.Line);
        Assert.True(record.Feedback.Skipped);
    }

    [Fact]
    public async Task ProgramPause_Cancelled_StopsTheRun()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var (lines, _) = TestJobs.Zigzag(40, pauseAt: 10);
        var job = await FeederHarness.UploadAsync(feeder.Local, lines);
        var reply = await feeder.Local.StartJobAsync(new StartJobRequest { JobId = job.JobId });

        var end = await feeder.RunToEndAsync(reply.RunId, p => new PromptAnswer { Id = p.Id, Confirmed = false });
        Assert.Equal("Stopped", end.Job.State);
        Assert.True(end.RecoveryAvailable);
    }

    [Fact]
    public async Task PauseResumeAndFeedOverride_AreRecorded()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var (client, _) = await feeder.PairAsync();
        var (lines, final) = TestJobs.Zigzag(80);
        var job = await FeederHarness.UploadAsync(client, lines);
        var reply = await client.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        await feeder.WaitStatusAsync(s => s.Job is { State: "Running", Line: > 15 });

        await client.SetFeedOverrideAsync(new FeedOverrideRequest { Percent = 150 });
        Assert.True(feeder.Simulator.WaitUntil(s => s.FeedOverride == 150, FeederHarness.Timeout));
        var invalid = await Assert.ThrowsAsync<RpcException>(async () => await client.SetFeedOverrideAsync(new FeedOverrideRequest { Percent = 500 }));
        Assert.Equal(StatusCode.InvalidArgument, invalid.StatusCode);

        await client.PauseAsync(new Empty());
        var paused = await feeder.WaitStatusAsync(s => s.Job is { State: "Paused" });
        Assert.Equal(150, paused.Job.FeedOverride);
        await client.SetSpindleRpmAsync(new SpindleRpmRequest { Rpm = 11000 });
        await client.ResumeAsync(new Empty());

        var end = await feeder.RunToEndAsync(reply.RunId);
        Assert.Equal("Completed", end.Job.State);
        TestJobs.AssertPosition(final, feeder.Simulator.MachinePosition);
        await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission { RunId = reply.RunId, FinishRating = 5 });
        var record = Assert.Single((await client.ListFeedbackAsync(new ListFeedbackRequest())).Records);
        Assert.Equal(150, Assert.Single(record.FeedOverrides).Percent);
        Assert.Equal(11000, Assert.Single(record.RpmChanges).Rpm);
    }

    [Fact]
    public async Task ConfigHashMismatch_NeedsAcceptedWarnings()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var config = await feeder.Local.ReadControllerConfigAsync(new Empty());
        Assert.NotEmpty(config.ConfigHash);
        Assert.Contains("3.9.5", config.FirmwareVersion, StringComparison.Ordinal);

        var (lines, _) = TestJobs.Zigzag(10);
        var job = await FeederHarness.UploadAsync(feeder.Local, lines, header: h => h.ConfigHash = new string('f', 64));
        var refused = await feeder.Local.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        Assert.False(refused.Started);
        Assert.Contains(refused.Warnings, w => w.Contains("configuration changed", StringComparison.Ordinal));

        var started = await feeder.Local.StartJobAsync(new StartJobRequest { JobId = job.JobId, AcceptWarnings = true });
        Assert.True(started.Started);
        Assert.Equal("Completed", (await feeder.RunToEndAsync(started.RunId)).Job.State);
        var log = await feeder.Local.GetRunLogAsync(new RunRef { RunId = started.RunId });
        Assert.Contains(log.Events, e => e.Message.Contains("Accepted warning", StringComparison.Ordinal));

        var matching = await FeederHarness.UploadAsync(feeder.Local, [.. lines, "(v2)"], header: h => h.ConfigHash = config.ConfigHash);
        Assert.True((await feeder.Local.StartJobAsync(new StartJobRequest { JobId = matching.JobId })).Started);
    }

    [Fact]
    public async Task EmergencyStop_RecoveryWizardResumesTheRun()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 40 });
        var (client, _) = await feeder.PairAsync();
        var (lines, final) = TestJobs.Zigzag();
        var job = await FeederHarness.UploadAsync(client, lines);
        var reply = await client.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        Assert.True(feeder.Simulator.WaitUntil(s => s.ExecutedBlocks > 50, FeederHarness.Timeout));
        feeder.Simulator.EmergencyStop();

        var faulted = await feeder.RunToEndAsync(reply.RunId);
        Assert.Equal("Faulted", faulted.Job.State);
        Assert.True((await feeder.WaitStatusAsync(s => s.RecoveryAvailable)).RecoveryAvailable);
        Assert.Empty(faulted.FeedbackRunId);
        var deleteRefused = await Assert.ThrowsAsync<RpcException>(async () => await client.DeleteJobAsync(new JobRef { JobId = job.JobId }));
        Assert.Equal(StatusCode.FailedPrecondition, deleteRefused.StatusCode);
        var feedbackRefused = await Assert.ThrowsAsync<RpcException>(async () =>
            await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission { RunId = reply.RunId, FinishRating = 3 }));
        Assert.Equal(StatusCode.FailedPrecondition, feedbackRefused.StatusCode);

        var plan = await client.GetRecoveryPlanAsync(new Empty());
        Assert.True(plan.Available, string.Join(" ", plan.Notes));
        Assert.Equal(reply.RunId, plan.RunId);
        Assert.Equal("Resume", plan.Action);
        Assert.Equal("Alarm", plan.Interruption);
        Assert.True(plan.ResumeLine > 1);

        await client.StartRecoveryAsync(new Empty());
        var end = await feeder.RunToEndAsync(reply.RunId);

        Assert.Equal("Completed", end.Job.State);
        Assert.False(end.RecoveryAvailable);
        Assert.Equal(reply.RunId, end.FeedbackRunId);
        Assert.Equal(2, feeder.Simulator.HomingCount);
        TestJobs.AssertPosition(final, feeder.Simulator.MachinePosition);
        TestJobs.AssertPosition(final, feeder.Simulator.PhysicalPosition);
        var log = await client.GetRunLogAsync(new RunRef { RunId = reply.RunId });
        Assert.Contains(log.Events, e => e.Kind == "recovery");
    }

    [Fact]
    public async Task DiscardRecovery_MovesRunToFeedback()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var (lines, _) = TestJobs.Zigzag();
        var job = await FeederHarness.UploadAsync(feeder.Local, lines);
        var reply = await feeder.Local.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        await feeder.WaitStatusAsync(s => s.Job is { State: "Running", Line: > 15 });
        await feeder.Local.StopAsync(new Empty());
        Assert.Equal("Stopped", (await feeder.RunToEndAsync(reply.RunId)).Job.State);
        Assert.True((await feeder.Local.GetRecoveryPlanAsync(new Empty())).Available);

        await feeder.Local.DiscardRecoveryAsync(new Empty());

        var status = await feeder.WaitStatusAsync(s => !s.RecoveryAvailable && s.FeedbackRunId == reply.RunId);
        Assert.False((await feeder.Local.GetRecoveryPlanAsync(new Empty())).Available);
        Assert.Equal(reply.RunId, status.FeedbackRunId);
    }

    [Fact]
    public async Task FeederRestartDuringRun_IsTreatedAsPowerLossAndRecovers()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var (lines, final) = TestJobs.Zigzag();
        var job = await FeederHarness.UploadAsync(feeder.Local, lines);
        var reply = await feeder.Local.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        await feeder.WaitStatusAsync(s => s.Job is { State: "Running", Line: > 15 });

        // A clean service stop stops the run; rewrite the row as if the feeder had lost power mid-run.
        await feeder.StopFeederAsync();
        using (var database = new FeederDatabase(Path.Combine(feeder.Folder, "feeder.db")))
        {
            var run = database.GetRun(reply.RunId)!;
            database.UpdateRun(run with { EndedAt = null, State = "Running", Recoverable = false });
        }

        await feeder.RestartFeederAsync();
        var runInfo = (await feeder.Local.ListRunsAsync(new ListRunsRequest())).Runs.Single();
        Assert.Equal("Interrupted", runInfo.State);
        var plan = await feeder.Local.GetRecoveryPlanAsync(new Empty());
        Assert.True(plan.Available, string.Join(" ", plan.Notes));
        Assert.Equal("PowerLoss", plan.Interruption);

        await feeder.Local.StartRecoveryAsync(new Empty());
        var end = await feeder.RunToEndAsync(reply.RunId);
        Assert.Equal("Completed", end.Job.State);
        TestJobs.AssertPosition(final, feeder.Simulator.MachinePosition);
    }

    [Fact]
    public async Task WatchEvents_ReplaysHistoryThenStreamsLive()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var (lines, _) = TestJobs.Zigzag(30, pauseAt: 15);
        var job = await FeederHarness.UploadAsync(feeder.Local, lines);
        var reply = await feeder.Local.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        var paused = await feeder.WaitStatusAsync(s => s.Prompt is not null);

        using var cts = new CancellationTokenSource(FeederHarness.Timeout);
        using var call = feeder.Local.WatchEvents(new WatchEventsRequest { RunId = reply.RunId }, cancellationToken: cts.Token);
        var events = new List<RunEvent>();
        var answered = false;
        await foreach (var e in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(e);
            if (!answered && e.Kind == "prompt")
            {
                answered = true;
                await feeder.Local.AnswerPromptAsync(new PromptAnswer { Id = paused.Prompt.Id, Confirmed = true });
            }

            if (e is { Kind: "state", Message: "Completed" })
            {
                break;
            }
        }

        Assert.True(answered, string.Join(", ", events.Select(e => e.Kind)));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.All(events, e => Assert.Equal(reply.RunId, e.RunId));
    }

    [Fact]
    public async Task Settings_AreValidatedAndPersisted()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var settings = await feeder.Admin.GetSettingsAsync(new Empty());
        settings.TouchPlateThickness = 15.2;
        settings.Name = "Mill feeder";
        await feeder.Admin.UpdateSettingsAsync(settings);

        settings.ProbeFeed = 5000;
        var invalid = await Assert.ThrowsAsync<RpcException>(async () => await feeder.Admin.UpdateSettingsAsync(settings));
        Assert.Equal(StatusCode.InvalidArgument, invalid.StatusCode);

        await feeder.RestartFeederAsync();
        var reloaded = await feeder.Admin.GetSettingsAsync(new Empty());
        Assert.Equal(15.2, reloaded.TouchPlateThickness);
        Assert.Equal("Mill feeder", (await feeder.Local.GetInfoAsync(new Empty())).Name);
        Assert.NotNull(await feeder.Admin.ListSerialPortsAsync(new Empty()));
    }
}
