using System.Diagnostics;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Controller;
using ThreeCTool.Feeder.Service.Security;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Streaming;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Protocol;
using ThreeCTool.Streaming.Recovery;

namespace ThreeCTool.Feeder.Service.Jobs;

/// <summary>
/// Runs jobs on the controller (one at a time): start with pre-flight checks, pause/resume/stop, overrides, program
/// pause prompts, recovery of interrupted runs, and the run history with operator feedback.
/// </summary>
public sealed class JobManager : IHostedService, IDisposable
{
    private static readonly JobState[] RecoverableStates = [JobState.Stopped, JobState.Faulted, JobState.Interrupted];

    private readonly FeederDatabase database;
    private readonly JobStore store;
    private readonly ObservableJournal journal;
    private readonly ControllerSession session;
    private readonly SettingsStore settings;
    private readonly PromptBroker prompts;
    private readonly FeederOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<JobManager> logger;
    private readonly Lock gate = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private RunContext? active;
    private RunContext? last;
    private string? feedbackRunId;
    private bool recoveryAvailable;

    public JobManager(FeederDatabase database, JobStore store, ObservableJournal journal, ControllerSession session, SettingsStore settings,
        PromptBroker prompts, FeederOptions options, TimeProvider time, ILogger<JobManager> logger)
    {
        this.database = database;
        this.store = store;
        this.journal = journal;
        this.session = session;
        this.settings = settings;
        this.prompts = prompts;
        this.options = options;
        this.time = time;
        this.logger = logger;
        MarkUnfinishedRuns();
        RefreshFlags();
    }

    public event EventHandler? Changed;

    /// <summary>A run is streaming or being recovered.</summary>
    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return active is not null;
            }
        }
    }

    public string? FeedbackRunId
    {
        get
        {
            lock (gate)
            {
                return feedbackRunId;
            }
        }
    }

    public bool RecoveryAvailable
    {
        get
        {
            lock (gate)
            {
                return recoveryAvailable && active is null;
            }
        }
    }

    public bool Recovering
    {
        get
        {
            lock (gate)
            {
                return active is { Recovering: true };
            }
        }
    }

    /// <summary>Id of the active run, else the most recent one.</summary>
    public string? CurrentRunId
    {
        get
        {
            lock (gate)
            {
                if ((active ?? last) is { } context)
                {
                    return context.Row.Id;
                }

                var runs = database.ListRuns(limit: 1);
                return runs.Count > 0 ? runs[0].Id : null;
            }
        }
    }

    /// <summary>Throws unless the machine is free for manual operations.</summary>
    public void EnsureIdle()
    {
        if (IsActive)
        {
            throw new FeederStateException("A job is running; stop it before using manual controls.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Service shutdown: a running job is stopped (feed hold, reset, retract) rather than left streaming.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RunContext? context;
        lock (gate)
        {
            context = active;
        }

        if (context is null)
        {
            return;
        }

        logger.StoppingOnShutdown(context.Row.Id);
        await context.RecoveryCancellation.CancelAsync().ConfigureAwait(false);
        if (context.Streamer is { } streamer)
        {
            await streamer.StopAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (context.Task is { } task)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StartJobReply> StartJobAsync(StartJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureIdle();
            var job = store.Get(request.JobId);
            var connection = session.Current;
            var identity = session.Identity ?? throw new FeederStateException("The controller is not identified yet.");
            var machineState = connection.LastStatus?.State;
            if (machineState is not MachineState.Idle)
            {
                // The last poll can lag a run that has just finished: ask once more before refusing.
                try
                {
                    machineState = (await connection.WaitForStatusAsync(s => s.State == MachineState.Idle, TimeSpan.FromSeconds(1), cancellationToken)
                        .ConfigureAwait(false)).State;
                }
                catch (TimeoutException)
                {
                    machineState = connection.LastStatus?.State;
                }
            }

            if (machineState is not MachineState.Idle)
            {
                throw new FeederStateException($"The machine must be idle to start a job (it is {machineState?.ToString() ?? "unknown"}).");
            }

            var file = await store.OpenAsync(job.Id, cancellationToken).ConfigureAwait(false);
            var analysis = await store.GetAnalysisAsync(job.Id, cancellationToken).ConfigureAwait(false);
            var warnings = new List<string>(identity.Warnings);
            if (job.Header.ConfigHash.Length > 0 && identity.Config is { } config
                && !string.Equals(config.Hash, job.Header.ConfigHash, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("The controller configuration changed since this file was posted; check feeds, travel and the A axis.");
            }

            var recoverable = database.LatestRecoverableRun();
            if (recoverable is not null)
            {
                warnings.Add($"Starting discards the recovery of the interrupted run of '{recoverable.Header.Name}'.");
            }

            var runId = Guid.CreateVersion7().ToString("N");
            var streamer = new JobStreamer(connection, file, journal, runId, StreamerOptions(identity)) { AskOperator = AskOperatorAsync };
            var problems = streamer.Validate();
            if (problems.Count > 0)
            {
                streamer.Dispose();
                throw new FeederStateException("The job cannot be streamed: " + string.Join(" ", problems.Take(5)));
            }

            if (warnings.Count > 0 && !request.AcceptWarnings)
            {
                streamer.Dispose();
                return new StartJobReply { Started = false, Warnings = { warnings } };
            }

            if (recoverable is not null)
            {
                database.UpdateRun(recoverable with { Recoverable = false, FeedbackPending = true });
            }

            var row = new RunRow
            {
                Id = runId,
                JobId = job.Id,
                Header = job.Header,
                StartedAt = time.GetUtcNow(),
                State = nameof(JobState.Running),
            };
            database.InsertRun(row);
            var context = new RunContext(row, job, file, analysis);
            lock (gate)
            {
                active = context;
            }

            foreach (var warning in warnings)
            {
                journal.AddEvent(runId, new JobEvent(time.GetUtcNow(), JobEventKinds.Warning, null, $"Accepted warning: {warning}"));
            }

            logger.RunStarting(job.Header.Name, runId);
            Begin(context, streamer, 1, null);
            RefreshFlags();
            return new StartJobReply { Started = true, RunId = runId, Warnings = { warnings } };
        }
        finally
        {
            startGate.Release();
        }
    }

    public Task PauseAsync()
    {
        var streamer = ActiveStreamer();
        if (streamer.State != JobState.Running)
        {
            throw new FeederStateException("The job is not running.");
        }

        return streamer.PauseAsync();
    }

    public Task ResumeAsync()
    {
        var streamer = ActiveStreamer();
        return streamer.State switch
        {
            JobState.Paused => streamer.ResumeAsync(),
            JobState.ProgramPaused => throw new FeederStateException("The program is waiting for the operator: answer the prompt on the feeder."),
            _ => throw new FeederStateException("The job is not paused."),
        };
    }

    public async Task StopAsync()
    {
        RunContext context;
        lock (gate)
        {
            context = active ?? throw new FeederStateException("No job is running.");
        }

        if (context.Recovering)
        {
            await context.RecoveryCancellation.CancelAsync().ConfigureAwait(false);
            return;
        }

        if (context.Streamer is { } streamer)
        {
            await streamer.StopAsync().ConfigureAwait(false);
        }
    }

    public async Task SetFeedOverrideAsync(int percent)
    {
        if (percent is < 10 or > 200)
        {
            throw new ArgumentException("Feed override must be between 10 and 200 %.");
        }

        var (context, streamer) = ActiveRun();
        await streamer.SetFeedOverrideAsync(percent).ConfigureAwait(false);
        database.AddChange(context.Row.Id, FeederDatabase.FeedChange, new RunChange(time.GetUtcNow(), streamer.Progress.Executed, percent));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetSpindleRpm(double rpm)
    {
        if (!(rpm > 0 && double.IsFinite(rpm)))
        {
            throw new ArgumentException("Spindle RPM must be positive.");
        }

        var (context, streamer) = ActiveRun();
        RecordRpm(context, streamer, rpm, streamer.Progress.Executed);
    }

    public async Task<RecoveryPlanInfo> GetRecoveryPlanAsync(CancellationToken cancellationToken)
    {
        if (IsActive || database.LatestRecoverableRun() is not { } run)
        {
            return new RecoveryPlanInfo { Available = false };
        }

        var info = new RecoveryPlanInfo { RunId = run.Id, JobId = run.JobId, JobName = run.Header.Name };
        try
        {
            var (plan, progress, _, _) = await PlanAsync(run, cancellationToken).ConfigureAwait(false);
            info.Available = plan.Action is RecoveryAction.Resume or RecoveryAction.Restart;
            info.Action = plan.Action.ToString();
            info.InterruptedLine = plan.InterruptedLine;
            info.ResumeLine = plan.ResumeLine;
            info.Interruption = progress.Interruption.ToString();
            info.Notes.AddRange(plan.Notes);
        }
        catch (Exception ex) when (ex is FeederStateException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            info.Available = false;
            info.Notes.Add(ex.Message);
        }

        return info;
    }

    public async Task StartRecoveryAsync(CancellationToken cancellationToken)
    {
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureIdle();
            var run = database.LatestRecoverableRun() ?? throw new FeederStateException("There is no interrupted run to recover.");
            var connection = session.Current;
            var identity = session.Identity ?? throw new FeederStateException("The controller is not identified yet.");
            var (plan, _, file, recoveryOptions) = await PlanAsync(run, cancellationToken).ConfigureAwait(false);
            if (plan.Action is not (RecoveryAction.Resume or RecoveryAction.Restart))
            {
                throw new FeederStateException($"The run cannot be resumed ({plan.Action}): {string.Join(" ", plan.Notes)}");
            }

            var job = store.Get(run.JobId);
            var analysis = await store.GetAnalysisAsync(run.JobId, cancellationToken).ConfigureAwait(false);
            var context = new RunContext(run, job, file, analysis) { Recovering = true };
            lock (gate)
            {
                active = context;
            }

            logger.RunRecovering(run.Id, plan.Action, plan.ResumeLine);
            RefreshFlags();
            context.Task = Task.Run(() => RecoverAsync(context, connection, identity, plan, recoveryOptions), CancellationToken.None);
        }
        finally
        {
            startGate.Release();
        }
    }

    public void DiscardRecovery()
    {
        EnsureIdle();
        var run = database.LatestRecoverableRun() ?? throw new FeederStateException("There is no interrupted run to discard.");
        database.UpdateRun(run with { Recoverable = false, FeedbackPending = true });
        journal.AddEvent(run.Id, new JobEvent(time.GetUtcNow(), JobEventKinds.Recovery, null, "Recovery discarded by the operator"), flush: true);
        RefreshFlags();
    }

    /// <summary>Deletes a job that is not running; a run of it that could still be recovered is discarded.</summary>
    public async Task DeleteJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.Get(jobId);
            lock (gate)
            {
                if (active?.Row.JobId == jobId)
                {
                    throw new FeederStateException("The job is running; stop it before deleting it.");
                }
            }

            if (database.LatestRecoverableRun() is { } run && run.JobId == jobId)
            {
                database.UpdateRun(run with { Recoverable = false, FeedbackPending = true });
                journal.AddEvent(run.Id, new JobEvent(time.GetUtcNow(), JobEventKinds.Recovery, null, "Recovery discarded: the job was deleted"), flush: true);
            }

            store.Delete(jobId);
            RefreshFlags();
        }
        finally
        {
            startGate.Release();
        }
    }

    public ActiveJob? Snapshot()
    {
        RunContext? context;
        bool finalised;
        lock (gate)
        {
            context = active ?? last;
            finalised = active is null;
        }

        if (context is null)
        {
            return null;
        }

        var progress = context.Streamer?.Progress ?? journal.Load(context.Row.Id);
        var executed = progress?.Executed ?? context.Row.LastLine;
        var analysis = context.Analysis;
        var total = analysis.TotalSeconds;
        var done = analysis.SecondsThrough(executed);
        var feedOverride = progress?.FeedOverride ?? 100;

        // A run only reports its end state once it is recorded (feedback/recovery flags set), so clients see a consistent end.
        var state = context.Recovering ? nameof(JobState.Recovering)
            : !finalised && progress?.State is JobState.Completed or JobState.Stopped or JobState.Faulted or JobState.Interrupted or JobState.Aborted
                ? nameof(JobState.Stopping)
                : progress?.State.ToString() ?? context.Row.State;
        return new ActiveJob
        {
            JobId = context.Job.Id,
            RunId = context.Row.Id,
            Name = context.Job.Header.Name,
            State = state,
            Line = executed,
            LineCount = context.File.LineCount,
            LineExact = progress?.ExecutedExact ?? false,
            Progress = total > 0 ? Math.Clamp(done / total, 0, 1) : context.File.LineCount > 0 ? (double)executed / context.File.LineCount : 0,
            ElapsedSeconds = context.Runtime.TotalSeconds,
            RemainingSeconds = Math.Max(0, total - done) * 100 / Math.Max(feedOverride, 10),
            SpindleRpm = progress?.SpindleRpm ?? 0,
            PlannedRpm = context.Job.Header.PlannedRpm,
            FeedOverride = feedOverride,
            Message = context.Message ?? string.Empty,
        };
    }

    public void SubmitFeedback(FeedbackSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var run = database.GetRun(submission.RunId) ?? throw new KeyNotFoundException($"Run '{submission.RunId}' not found.");
        lock (gate)
        {
            if (active?.Row.Id == run.Id)
            {
                throw new FeederStateException("The run is still active.");
            }
        }

        if (run.Recoverable)
        {
            throw new FeederStateException("The run can still be recovered; resume or discard it first.");
        }

        foreach (var rating in new[] { submission.FinishRating, submission.ChatterRating, submission.WearRating })
        {
            if (rating is < 0 or > 5)
            {
                throw new ArgumentException("Ratings must be 1-5 (0 = not rated).");
            }
        }

        if (!Enum.IsDefined(submission.WearEvent))
        {
            throw new ArgumentException("Unknown wear event.");
        }

        if (submission.Notes.Length > 4000)
        {
            throw new ArgumentException("Notes are limited to 4000 characters.");
        }

        database.UpdateRun(run with { Feedback = submission.Clone(), FeedbackAt = time.GetUtcNow(), FeedbackPending = false, Acknowledged = false });
        RefreshFlags();
    }

    public FeedbackList ListFeedback(bool includeAcknowledged)
    {
        var list = new FeedbackList();
        foreach (var run in database.ListFeedback(includeAcknowledged))
        {
            var record = new FeedbackRecord
            {
                RunId = run.Id,
                JobId = run.JobId,
                Job = run.Header,
                FinalState = run.State,
                StartedAt = ProtoConvert.ToTimestamp(run.StartedAt),
                EndedAt = ProtoConvert.ToTimestamp(run.EndedAt),
                RecordedAt = ProtoConvert.ToTimestamp(run.FeedbackAt),
                Feedback = run.Feedback,
                LastLine = run.LastLine,
                RuntimeSeconds = run.RuntimeSeconds,
                Acknowledged = run.Acknowledged,
            };
            record.FeedOverrides.AddRange(database.GetChanges(run.Id, FeederDatabase.FeedChange)
                .Select(c => new FeedOverrideChange { At = ProtoConvert.ToTimestamp(c.At), Line = c.Line, Percent = (int)c.Value }));
            record.RpmChanges.AddRange(database.GetChanges(run.Id, FeederDatabase.RpmChange)
                .Select(c => new Contracts.RpmChange { At = ProtoConvert.ToTimestamp(c.At), Line = c.Line, Rpm = c.Value }));
            list.Records.Add(record);
        }

        return list;
    }

    public void AcknowledgeFeedback(IEnumerable<string> runIds) => database.Acknowledge(runIds);

    public RunList ListRuns(string? jobId)
    {
        var list = new RunList();
        list.Runs.AddRange(database.ListRuns(string.IsNullOrEmpty(jobId) ? null : jobId).Select(ProtoConvert.ToRunInfo));
        return list;
    }

    public RunLog GetRunLog(string runId)
    {
        var run = database.GetRun(runId) ?? throw new KeyNotFoundException($"Run '{runId}' not found.");
        var log = new RunLog { Run = ProtoConvert.ToRunInfo(run) };
        log.Events.AddRange(journal.GetNumberedEvents(runId).Select(ProtoConvert.ToRunEvent));
        return log;
    }

    public void Dispose()
    {
        startGate.Dispose();
    }

    private JobStreamerOptions StreamerOptions(ControllerIdentity identity) => new()
    {
        UseLineNumbers = identity.Config?.UseLineNumbers ?? false,
        PlannerBlocks = identity.PlannerBlocks,
        SafeZMachine = settings.Current.SafeZMachine,
    };

    private RecoveryOptions RecoveryOptionsFromSettings()
    {
        var current = settings.Current;
        return new RecoveryOptions
        {
            Process = JobProcess.Milling,
            SafetyMarginMm = current.RecoveryMarginMm,
            SafeZMachine = current.SafeZMachine,
            PlungeFeedMmPerMin = current.PlungeFeed,
            RotaryRadiusMm = options.RotaryRadiusMm,
        };
    }

    private async Task<(RecoveryPlan Plan, JobProgress Progress, JobFile File, RecoveryOptions Options)> PlanAsync(RunRow run, CancellationToken cancellationToken)
    {
        var file = await store.OpenAsync(run.JobId, cancellationToken).ConfigureAwait(false);
        var progress = journal.Load(run.Id) ?? throw new FeederStateException("The run has no journal; it cannot be resumed.");
        var recoveryOptions = RecoveryOptionsFromSettings();
        var plan = await Task.Run(() => RecoveryPlanner.Plan(file, progress, recoveryOptions), cancellationToken).ConfigureAwait(false);
        return (plan, progress, file, recoveryOptions);
    }

    private async Task RecoverAsync(RunContext context, ControllerConnection connection, ControllerIdentity identity, RecoveryPlan plan,
        RecoveryOptions recoveryOptions)
    {
        var token = context.RecoveryCancellation.Token;
        try
        {
            var executor = new RecoveryExecutor(connection, context.File, journal, context.Row.Id, prompts, recoveryOptions);
            var resume = await executor.ExecuteAsync(plan, token).ConfigureAwait(false);
            if (resume is null)
            {
                if (journal.Load(context.Row.Id)?.State == JobState.Aborted)
                {
                    context.Row = context.Row with { State = nameof(JobState.Aborted), Recoverable = false, FeedbackPending = true };
                    database.UpdateRun(context.Row);
                }

                EndWithoutStreaming(context, "Recovery cancelled.");
                return;
            }

            var streamer = new JobStreamer(connection, context.File, journal, context.Row.Id, StreamerOptions(identity)) { AskOperator = AskOperatorAsync };
            context.Recovering = false;
            context.Row = context.Row with { EndedAt = null, State = nameof(JobState.Running), Recoverable = false };
            database.UpdateRun(context.Row);
            Begin(context, streamer, resume.Line, resume.FirstLinePrefix);
        }
        catch (OperationCanceledException)
        {
            EndWithoutStreaming(context, "Recovery cancelled.");
        }
        catch (Exception ex)
        {
            logger.RecoveryFailed(ex, context.Row.Id);
            journal.AddEvent(context.Row.Id, new JobEvent(time.GetUtcNow(), JobEventKinds.Recovery, null, $"Recovery failed: {ex.Message}"), flush: true);
            EndWithoutStreaming(context, $"Recovery failed: {ex.Message}");
        }
    }

    private void EndWithoutStreaming(RunContext context, string message)
    {
        context.Message = message;
        context.Recovering = false;
        Finalise(context);
    }

    /// <summary>Retires the run and publishes the feedback/recovery flags atomically, so no status shows the end without them.</summary>
    private void Finalise(RunContext context)
    {
        var feedback = database.LatestFeedbackPendingRun()?.Id;
        var recoverable = database.LatestRecoverableRun() is not null;
        lock (gate)
        {
            if (active == context)
            {
                active = null;
                last = context;
            }

            feedbackRunId = feedback;
            recoveryAvailable = recoverable;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Begin(RunContext context, JobStreamer streamer, int firstLine, string? prefix)
    {
        context.Streamer = streamer;
        context.PromptCancellation = new CancellationTokenSource();
        streamer.StateChanged += (_, state) => OnStreamerState(context, state);
        streamer.PromptRequested += (_, prompt) => OnProgramPause(context, streamer, prompt);
        context.Task = Task.Run(async () =>
        {
            JobState final;
            try
            {
                final = await streamer.RunAsync(firstLine, prefix, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.RunFailed(ex, context.Row.Id);
                context.Message = ex.Message;
                journal.AddEvent(context.Row.Id, new JobEvent(time.GetUtcNow(), JobEventKinds.Error, streamer.Progress.Executed, ex.Message), flush: true);
                final = streamer.State is JobState.Running or JobState.Paused or JobState.ProgramPaused or JobState.Pending ? JobState.Faulted : streamer.State;
            }

            Finish(context, streamer, final);
        }, CancellationToken.None);
    }

    private void Finish(RunContext context, JobStreamer streamer, JobState final)
    {
        context.StopClock();
        context.PromptCancellation?.Cancel();
        var progress = streamer.Progress;
        context.Row = context.Row with
        {
            EndedAt = time.GetUtcNow(),
            State = final.ToString(),
            LastLine = progress.Executed,
            RuntimeSeconds = context.Runtime.TotalSeconds,
            Recoverable = RecoverableStates.Contains(final),
            FeedbackPending = final is JobState.Completed or JobState.Aborted,
        };
        database.UpdateRun(context.Row);
        streamer.Dispose();
        logger.RunEnded(context.Row.Id, final, progress.Executed);
        Finalise(context);
    }

    private void OnStreamerState(RunContext context, JobState state)
    {
        if (state == JobState.Running)
        {
            context.StartClock();
        }
        else
        {
            context.StopClock();
        }

        if (state is not (JobState.Completed or JobState.Stopped or JobState.Faulted or JobState.Interrupted or JobState.Aborted))
        {
            context.Row = context.Row with { State = state.ToString() };
            database.UpdateRun(context.Row);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Yes/No question from the program, answered on the touch screen; cancelling stops the job.</summary>
    private async Task<bool?> AskOperatorAsync(JobQuestion question, CancellationToken cancellationToken)
    {
        var prompt = new Prompt { Kind = PromptKind.Choice, Message = question.Text, CanCancel = true, Line = question.Line };
        var answer = await prompts.AskAsync(prompt, cancellationToken).ConfigureAwait(false);
        return answer.Confirmed ? answer.Choice : null;
    }

    private void OnProgramPause(RunContext context, JobStreamer streamer, JobPrompt pause)
    {
        var line = pause.Line;
        var spindle = context.Analysis.SpindlePrompts.TryGetValue(line, out var rpm);
        var prompt = new Prompt
        {
            Kind = spindle ? PromptKind.Spindle : PromptKind.ProgramPause,
            Message = pause.Message,
            SuggestedRpm = spindle ? rpm : 0,
            CanCancel = true,
            Line = line,
        };
        var token = context.PromptCancellation?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await prompts.AskAsync(prompt, token).ConfigureAwait(false);
                if (!answer.Confirmed)
                {
                    await streamer.StopAsync().ConfigureAwait(false);
                    return;
                }

                if (spindle)
                {
                    RecordRpm(context, streamer, answer.Rpm > 0 ? answer.Rpm : rpm, line);
                }

                await streamer.ResumeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The run ended while the prompt was open.
            }
            catch (Exception ex)
            {
                logger.PromptFailed(ex);
                context.Message = ex.Message;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }, CancellationToken.None);
    }

    private void RecordRpm(RunContext context, JobStreamer streamer, double rpm, int line)
    {
        streamer.RecordSpindleRpm(rpm);
        database.AddChange(context.Row.Id, FeederDatabase.RpmChange, new RunChange(time.GetUtcNow(), line, rpm));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private JobStreamer ActiveStreamer() => ActiveRun().Streamer;

    private (RunContext Context, JobStreamer Streamer) ActiveRun()
    {
        lock (gate)
        {
            if (active is not { Recovering: false, Streamer: { } streamer } context)
            {
                throw new FeederStateException("No job is running.");
            }

            return (context, streamer);
        }
    }

    private void MarkUnfinishedRuns()
    {
        foreach (var run in database.UnfinishedRuns())
        {
            var progress = journal.Load(run.Id);
            var now = time.GetUtcNow();
            RunRow updated;
            if (progress?.State is JobState.Completed or JobState.Aborted)
            {
                updated = run with
                {
                    EndedAt = progress.UpdatedAt,
                    State = progress.State.ToString(),
                    LastLine = progress.Executed,
                    FeedbackPending = true,
                };
            }
            else
            {
                if (progress is not null)
                {
                    journal.Record(progress with
                    {
                        State = JobState.Interrupted,
                        Interruption = InterruptionKind.PowerLoss,
                        InterruptionCode = null,
                        UpdatedAt = now,
                    }, flush: true);
                    journal.AddEvent(run.Id, new JobEvent(now, JobEventKinds.Disconnect, progress.Executed,
                        "The feeder restarted during the run (power loss); position must be re-established."), flush: true);
                }

                updated = run with
                {
                    EndedAt = progress?.UpdatedAt ?? now,
                    State = nameof(JobState.Interrupted),
                    LastLine = progress?.Executed ?? 0,
                    Recoverable = progress is not null,
                };
            }

            database.UpdateRun(updated);
            logger.RunUnfinished(run.Id, updated.State);
        }
    }

    private void RefreshFlags()
    {
        var feedback = database.LatestFeedbackPendingRun()?.Id;
        var recoverable = database.LatestRecoverableRun() is not null;
        lock (gate)
        {
            feedbackRunId = feedback;
            recoveryAvailable = recoverable;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RunContext(RunRow row, JobRow job, JobFile file, JobAnalysis analysis)
    {
        private readonly Lock clockGate = new();
        private readonly Stopwatch clock = new();
        private TimeSpan before = TimeSpan.FromSeconds(row.RuntimeSeconds);

        public RunRow Row { get; set; } = row;

        public JobRow Job { get; } = job;

        public JobFile File { get; } = file;

        public JobAnalysis Analysis { get; } = analysis;

        public JobStreamer? Streamer { get; set; }

        public Task? Task { get; set; }

        public volatile bool Recovering;

        public volatile string? Message;

        public CancellationTokenSource RecoveryCancellation { get; } = new();

        public CancellationTokenSource? PromptCancellation { get; set; }

        public TimeSpan Runtime
        {
            get
            {
                lock (clockGate)
                {
                    return before + clock.Elapsed;
                }
            }
        }

        public void StartClock()
        {
            lock (clockGate)
            {
                clock.Start();
            }
        }

        public void StopClock()
        {
            lock (clockGate)
            {
                before += clock.Elapsed;
                clock.Reset();
            }
        }
    }
}
