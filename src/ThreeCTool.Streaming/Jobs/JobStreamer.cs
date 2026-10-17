using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming.Jobs;

public sealed class JobStreamerOptions
{
    /// <summary>Prefix lines with <c>N</c> numbers; the controller must report <c>Ln:</c> (<c>use_line_numbers: true</c>).</summary>
    public bool UseLineNumbers { get; set; }

    /// <summary>Planner blocks available when idle (<c>Bf:</c> first value, or <c>planner_blocks</c>).</summary>
    public int PlannerBlocks { get; set; } = 16;

    /// <summary>Machine Z to retract to after a stop.</summary>
    public double SafeZMachine { get; set; } = -5;

    /// <summary>Machine Z travel (low, high) from the controller configuration; moves outside it are never sent.</summary>
    public (double Low, double High)? MachineZTravel { get; set; }

    /// <summary>
    /// Machine Z of the spindle with the collet nut on the table. When set, no move may bring the tool tip (the
    /// file's <c>(MC:TOOL STICKOUT ...)</c> below the nut) below the table; null = not checked.
    /// </summary>
    public double? TableMachineZ { get; set; }

    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Heaters are switched off when a job stays paused this long (FDM safety, section 11.6).</summary>
    public TimeSpan PausedHeaterTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Yes/No question to the operator from an <c>(MC:ASK BEGIN)</c> block at file line <paramref name="Line"/>.</summary>
public sealed record JobQuestion(string Text, int Line);

/// <summary>Program pause (<c>M0</c>/<c>M1</c> at file line <paramref name="Line"/>) with the operator message (last comment).</summary>
public sealed record JobPrompt(string Message, int Line);

/// <summary>
/// Streams a job file to the controller (section 11.1): character-counting flow control, progress persisted to the
/// journal, executed-line tracking, pause/resume, M0 prompts, stop with retract, and host directives.
/// </summary>
public sealed class JobStreamer : IDisposable
{
    public const int MaxLineNumber = 9_999_999;
    private const int MotionHistory = 1024;
    private const int MaxHeightProblems = 5;
    private static readonly TimeSpan AlarmHandlerGrace = TimeSpan.FromSeconds(2);

    private readonly ControllerConnection connection;
    private readonly JobFile file;
    private readonly IJobJournal journal;
    private readonly IHostDirectiveHandler? directives;
    private readonly JobStreamerOptions options;
    private readonly Lock gate = new();
    private readonly Queue<SentLine> inFlight = new();
    private readonly LinkedList<int> ackedMotionLines = new();
    private readonly CancellationTokenSource runCancellation = new();
    private readonly TaskCompletionSource faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JobProgress progress;
    private Task? stopTask;
    private Task? followUpTask;
    private Timer? pausedHeaterTimer;
    private bool pauseRequested;
    private int startLine = 1;
    private string? pendingPrompt;
    private SentLine? prompted;
    private bool started;

    public JobStreamer(ControllerConnection connection, JobFile file, IJobJournal journal, string jobId,
        JobStreamerOptions? options = null, IHostDirectiveHandler? directives = null)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.file = file ?? throw new ArgumentNullException(nameof(file));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        this.directives = directives;
        this.options = options ?? new JobStreamerOptions();
        progress = journal.Load(jobId) ?? new JobProgress { JobId = jobId, FileSha256 = file.Sha256, UpdatedAt = DateTimeOffset.UtcNow };
        if (progress.FileSha256 != file.Sha256)
        {
            throw new InvalidOperationException($"Job '{jobId}' was recorded for a different file (SHA-256 mismatch).");
        }
    }

    public event EventHandler<JobState>? StateChanged;

    /// <summary>Raised when an <c>M0</c> pauses the program.</summary>
    public event EventHandler<JobPrompt>? PromptRequested;

    /// <summary>
    /// Answers <c>(MC:ASK ...)</c> questions: true = Yes, false = No, null = stop the job. Required when the file asks.
    /// </summary>
    public Func<JobQuestion, CancellationToken, Task<bool?>>? AskOperator { get; set; }

    public JobState State
    {
        get
        {
            lock (gate)
            {
                return progress.State;
            }
        }
    }

    public JobProgress Progress
    {
        get
        {
            lock (gate)
            {
                return progress;
            }
        }
    }

    public string JobId => progress.JobId;

    /// <summary>
    /// Checks the whole file before streaming: line length vs. the RX buffer, handlers for host directives, and machine
    /// Z targets (<c>G53</c>, the safe Z) against the Z travel and the table.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var lineNumber = options.UseLineNumbers ? (int?)MaxLineNumber : null;
        var askBegin = 0;
        var askElse = false;
        var guard = new MachineZGuard(options);
        var heightProblems = 0;
        foreach (var (line, text) in file.ReadLines())
        {
            var block = GcodeBlock.Parse(text);
            if (guard.Active && guard.Check(block, line, null) is { } height && heightProblems++ < MaxHeightProblems)
            {
                problems.Add(height);
            }
            if (block.HasCode && block.ToWire(lineNumber).Length + 1 > connection.Options.RxBufferSize)
            {
                problems.Add($"Line {line} is longer than the controller RX buffer ({connection.Options.RxBufferSize} bytes).");
            }

            if (block.Directive is { IsMarker: false } directive && (directives is null || !directives.CanHandle(directive)))
            {
                problems.Add($"Line {line}: no handler for host directive {directive}.");
            }

            if (block.Directive is { IsAsk: true } ask)
            {
                if (ask.IsAskBegin)
                {
                    if (askBegin > 0)
                    {
                        problems.Add($"Line {line}: questions cannot be nested (the one on line {askBegin} is still open).");
                    }

                    if (AskOperator is null)
                    {
                        problems.Add($"Line {line}: no operator to answer the question {ask}.");
                    }

                    (askBegin, askElse) = (line, false);
                }
                else if (ask.IsAskElse && askBegin > 0 && !askElse)
                {
                    askElse = true;
                }
                else if (ask.IsAskEnd && askBegin > 0)
                {
                    askBegin = 0;
                }
                else
                {
                    problems.Add($"Line {line}: unexpected {ask}.");
                }
            }
        }

        if (askBegin > 0)
        {
            problems.Add($"Line {askBegin}: the question has no (MC:ASK END).");
        }

        if (guard.SafeZProblem() is { } safeZ)
        {
            problems.Insert(0, safeZ);
        }

        return problems;
    }

    /// <summary>
    /// Streams from <paramref name="firstLine"/> to the end. <paramref name="firstLinePrefix"/> is prepended to the first
    /// line (a modal motion word when resuming mid-file). Returns the final state.
    /// </summary>
    public async Task<JobState> RunAsync(int firstLine = 1, string? firstLinePrefix = null, CancellationToken cancellationToken = default)
    {
        if (started)
        {
            throw new InvalidOperationException("A job streamer can only run once.");
        }

        started = true;
        var problems = Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Job cannot be streamed: " + string.Join(" ", problems));
        }

        startLine = Math.Max(firstLine, 1);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCancellation.Token);
        var token = linked.Token;
        connection.StatusReceived += OnStatus;
        connection.AlarmReceived += OnAlarm;
        connection.Disconnected += OnDisconnected;
        connection.ResetDetected += OnReset;
        try
        {
            Update(p => p with
            {
                State = JobState.Running,
                Interruption = InterruptionKind.None,
                InterruptionCode = null,
                LastSent = startLine - 1,
                LastAcked = startLine - 1,
                Executed = startLine - 1,
                ExecutedExact = false,
            });
            AddEvent(JobEventKinds.Started, startLine, firstLine > 1 ? $"Resumed at line {startLine}" : $"Started ({file.LineCount} lines)", flush: true);
            StateChanged?.Invoke(this, JobState.Running);
            var guard = new MachineZGuard(options);
            if (guard.Active && startLine > 1)
            {
                // Modal state (WCS, units, stickout) of the skipped part; positions are unknown after recovery moves.
                foreach (var (line, text) in file.ReadLines().TakeWhile(l => l.Line < startLine))
                {
                    guard.Check(GcodeBlock.Parse(text), line, null);
                }

                guard.ForgetPositions();
            }

            var offsetQuery = await QueryOffsetsAsync(token).ConfigureAwait(false);
            var offsets = guard.Active ? await ReadOffsetsAsync(offsetQuery, startLine, token).ConfigureAwait(false) : null;

            var first = true;
            bool? answer = null;
            var skipping = false;
            foreach (var (line, text) in file.ReadLines(startLine))
            {
                token.ThrowIfCancellationRequested();
                var block = GcodeBlock.Parse(text);
                if (block.Directive is { IsAsk: true } ask)
                {
                    if (ask.IsAskBegin)
                    {
                        answer = await AskAsync(pendingPrompt ?? "Continue?", line, token).ConfigureAwait(false);
                        pendingPrompt = null;
                        skipping = answer == false;
                    }
                    else if (ask.IsAskElse)
                    {
                        // Also when started inside the Yes branch (no answer): only one branch ever runs.
                        skipping = answer != false;
                    }
                    else
                    {
                        (answer, skipping) = (null, false);
                    }

                    continue;
                }

                // The branch the operator did not choose is never sent.
                if (skipping)
                {
                    continue;
                }

                if (block.Comments.Count > 0 && block.Directive is null)
                {
                    pendingPrompt = block.Comments[^1];
                }

                if (block.Directive is { IsMarker: false } directive)
                {
                    await RunDirectiveAsync(directive, line, token).ConfigureAwait(false);
                }

                // A move outside the Z travel or with the tool tip below the table is never sent.
                if (guard.Active && guard.Check(block, line, offsets) is { } height)
                {
                    Fault(InterruptionKind.Error, 0, line, height + " The job was stopped before this line.");
                    throw new OperationCanceledException(height);
                }

                if (!block.HasCode)
                {
                    continue;
                }

                var prefix = first ? firstLinePrefix : null;
                first = false;
                var wire = block.ToWire(options.UseLineNumbers ? WireLineNumber(line) : null, prefix);
                var isPause = block.HasM(0) || block.HasM(1);
                await SendAsync(new SentLine(line, IsMotion(block, prefix), isPause ? pendingPrompt ?? "Program paused" : null, false), wire, token)
                    .ConfigureAwait(false);
                Update(p => p with { LastSent = line });
                if (GcodeInterpreter.ChangesOffsets(block))
                {
                    offsetQuery = await QueryOffsetsAsync(token).ConfigureAwait(false);
                    if (guard.Active)
                    {
                        // The next moves are checked against the offsets this line set (e.g. the probed work Z).
                        offsets = await ReadOffsetsAsync(offsetQuery, line, token).ConfigureAwait(false);
                    }
                }
            }

            // G4 P0 is acknowledged only after the planner has drained: the job has fully executed.
            var sync = await SendAsync(new SentLine(0, false, null, false), "G4P0", token).ConfigureAwait(false);
            await Task.WhenAny(sync.Completion, faulted.Task).WaitAsync(token).ConfigureAwait(false);
            if (sync.Completion.IsCompletedSuccessfully && sync.Completion.Result.IsOk && State == JobState.Running)
            {
                Update(p => p with { State = JobState.Completed, Executed = file.LineCount, ExecutedExact = true, LastAcked = file.LineCount });
                AddEvent(JobEventKinds.State, file.LineCount, "Completed", flush: true);
                StateChanged?.Invoke(this, JobState.Completed);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ControllerDisconnectedException or InvalidOperationException
            && (runCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested || !connection.IsConnected || connection.NeedsResync))
        {
            // Stop, fault or disconnect: the handlers have set the state.
            if (connection.NeedsResync && State == JobState.Running)
            {
                // The connection is marked for resync just before it raises the alarm on the reader thread.
                await Task.WhenAny(faulted.Task, Task.Delay(AlarmHandlerGrace, CancellationToken.None)).ConfigureAwait(false);
                if (State == JobState.Running)
                {
                    Fault(InterruptionKind.Alarm, 0, Progress.Executed, "Controller needs resynchronization");
                }
            }
        }
        finally
        {
            if (stopTask is { } stop)
            {
                await stop.ConfigureAwait(false);
            }

            Task? followUp;
            lock (gate)
            {
                followUp = followUpTask;
            }

            if (followUp is not null)
            {
                await followUp.ConfigureAwait(false);
            }

            connection.StatusReceived -= OnStatus;
            connection.AlarmReceived -= OnAlarm;
            connection.Disconnected -= OnDisconnected;
            connection.ResetDetected -= OnReset;
            DisposePausedTimer();
            journal.Flush();
        }

        if (cancellationToken.IsCancellationRequested && State == JobState.Running)
        {
            await StopAsync().ConfigureAwait(false);
        }

        return State;
    }

    /// <summary>Feed hold.</summary>
    public async Task PauseAsync()
    {
        lock (gate)
        {
            if (progress.State != JobState.Running)
            {
                return;
            }

            pauseRequested = true;
        }

        await connection.SendRealtimeAsync(RealtimeCommand.FeedHold).ConfigureAwait(false);
        SetState(JobState.Paused, "Paused (feed hold)");
    }

    /// <summary>Cycle start after a pause or an <c>M0</c> prompt.</summary>
    public async Task ResumeAsync()
    {
        lock (gate)
        {
            if (progress.State is not (JobState.Paused or JobState.ProgramPaused))
            {
                return;
            }

            pauseRequested = false;
        }

        await connection.SendRealtimeAsync(RealtimeCommand.CycleStart).ConfigureAwait(false);
        SetState(JobState.Running, "Resumed (cycle start)");
    }

    /// <summary>
    /// Section 11.2: feed hold, wait until motion has stopped, soft reset (position retained), then retract to safe
    /// machine Z. Pending lines are discarded; the job can be resumed with recovery.
    /// </summary>
    public Task StopAsync()
    {
        lock (gate)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            if (progress.State is JobState.Completed or JobState.Stopped or JobState.Aborted)
            {
                return Task.CompletedTask;
            }

            stopTask = StopCoreAsync();
            return stopTask;
        }
    }

    public async Task SetFeedOverrideAsync(int percent)
    {
        await connection.SetFeedOverrideAsync(percent).ConfigureAwait(false);
        var clamped = Math.Clamp(percent, 10, 200);
        Update(p => p with { FeedOverride = clamped });
        AddEvent(JobEventKinds.FeedOverride, Progress.Executed, $"Feed override {clamped}%");
    }

    /// <summary>Records the spindle RPM the operator set on the manual dial.</summary>
    public void RecordSpindleRpm(double rpm)
    {
        Update(p => p with { SpindleRpm = rpm });
        AddEvent(JobEventKinds.SpindleRpm, Progress.Executed, $"Spindle dial set to {rpm:0} RPM", flush: true);
    }

    public void Dispose()
    {
        DisposePausedTimer();
        runCancellation.Dispose();
    }

    /// <summary>Line number sent for file line <paramref name="line"/> (wraps at <see cref="MaxLineNumber"/>).</summary>
    public static int WireLineNumber(int line) => ((line - 1) % MaxLineNumber) + 1;

    /// <summary>Maps a reported <c>Ln:</c> back to the most recent file line at or before <paramref name="lastSent"/>.</summary>
    public static int FileLineFromWire(int reported, int lastSent)
    {
        var difference = ((WireLineNumber(lastSent) - reported) % MaxLineNumber + MaxLineNumber) % MaxLineNumber;
        return lastSent - difference;
    }

    private static bool IsMotion(GcodeBlock block, string? prefix)
    {
        if (block.SystemCommand is not null)
        {
            return block.SystemCommand.StartsWith("$J", StringComparison.OrdinalIgnoreCase);
        }

        var hasAxisWords = block.Words.Any(w => ModalState.AxisLetters.Contains(w.Letter, StringComparison.Ordinal));
        return hasAxisWords
            && (prefix is not null || !(block.HasG(10) || block.HasG(92) || block.HasG(43.1) || block.HasG(28.1) || block.HasG(30.1)));
    }

    private async Task<PendingLine> SendAsync(SentLine sent, string wire, CancellationToken token)
    {
        var pending = await connection.WriteLineAsync(wire, sent, token).ConfigureAwait(false);
        sent.Pending = pending;
        lock (gate)
        {
            inFlight.Enqueue(sent);
        }

        _ = pending.Completion.ContinueWith(_ => OnLineCompleted(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return pending;
    }

    private void OnLineCompleted()
    {
        List<(SentLine Line, LineResult Result)> completed = [];
        lock (gate)
        {
            // Responses arrive in order; continuations may not, so always consume from the head of the queue.
            while (inFlight.TryPeek(out var head) && head.Pending!.Completion.IsCompleted)
            {
                inFlight.Dequeue();
                var result = head.Pending.Completion.Result;
                completed.Add((head, result));
                if (result.IsOk && head.FileLine > 0)
                {
                    if (head.IsMotion)
                    {
                        ackedMotionLines.AddLast(head.FileLine);
                        if (ackedMotionLines.Count > MotionHistory)
                        {
                            ackedMotionLines.RemoveFirst();
                        }
                    }

                    progress = progress with { LastAcked = Math.Max(progress.LastAcked, head.FileLine), UpdatedAt = DateTimeOffset.UtcNow };
                }
            }

            if (completed.Count > 0)
            {
                journal.Record(progress);
            }
        }

        foreach (var (line, result) in completed)
        {
            if (line.IsOffsetQuery && result.IsOk)
            {
                var coordinates = ControllerQueries.ParseWorkCoordinates(result.Responses);
                Update(p => p with { WorkCoordinates = coordinates.Serialize(), ToolLengthOffset = coordinates.ToolLengthOffset });
            }
            else if (line.FileLine > 0 && result.Outcome == LineOutcome.Error)
            {
                Fault(InterruptionKind.Error, result.ErrorCode, line.FileLine,
                    $"error:{result.ErrorCode} ({FluidNcCodes.ErrorDescription(result.ErrorCode)}) on line {line.FileLine}");
            }
        }
    }

    private async Task<PendingLine> QueryOffsetsAsync(CancellationToken token) =>
        await SendAsync(new SentLine(0, false, null, true), "$#", token).ConfigureAwait(false);

    /// <summary>Waits for an offset query; without the offsets the tool height cannot be checked, so the job stops.</summary>
    private async Task<WorkCoordinates> ReadOffsetsAsync(PendingLine query, int line, CancellationToken token)
    {
        await Task.WhenAny(query.Completion, faulted.Task).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (query.Completion is { IsCompletedSuccessfully: true, Result.IsOk: true })
        {
            return ControllerQueries.ParseWorkCoordinates(query.Completion.Result.Responses);
        }

        // An alarm fails the pending lines just before it is raised: let its handler report it.
        await Task.WhenAny(faulted.Task, Task.Delay(AlarmHandlerGrace, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Fault(InterruptionKind.Error, 0, line, "The work offsets could not be read ($#), so the tool height cannot be checked.");
        throw new OperationCanceledException("The work offsets could not be read.");
    }

    /// <summary>Waits until the planner has drained (G4 P0 acknowledged): the machine stands still.</summary>
    private async Task DrainAsync(CancellationToken token)
    {
        var sync = await SendAsync(new SentLine(0, false, null, false), "G4P0", token).ConfigureAwait(false);
        await Task.WhenAny(sync.Completion, faulted.Task).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!sync.Completion.IsCompletedSuccessfully || !sync.Completion.Result.IsOk)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>Asks the operator a Yes/No question with the machine standing still; a cancelled question stops the job.</summary>
    private async Task<bool> AskAsync(string question, int line, CancellationToken token)
    {
        await DrainAsync(token).ConfigureAwait(false);
        SetState(JobState.ProgramPaused, "Question: " + question, JobEventKinds.Prompt);
        bool? answer;
        try
        {
            answer = await AskOperator!(new JobQuestion(question, line), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Fault(InterruptionKind.Directive, 0, line, $"Asking the operator failed: {ex.Message}");
            throw new OperationCanceledException("Asking the operator failed.", ex);
        }

        if (answer is not { } yes)
        {
            await StopAsync().ConfigureAwait(false);
            throw new OperationCanceledException("Stopped by the operator.");
        }

        token.ThrowIfCancellationRequested();
        AddEvent(JobEventKinds.Prompt, line, $"Answered {(yes ? "Yes" : "No")}: {question}", flush: true);
        SetState(JobState.Running, "Continuing");
        return yes;
    }

    private async Task RunDirectiveAsync(HostDirective directive, int line, CancellationToken token)
    {
        if (directive.Wait)
        {
            // Drain the planner first so the machine is not moving while waiting (e.g. for the hotend to heat).
            await DrainAsync(token).ConfigureAwait(false);
        }

        AddEvent(JobEventKinds.Directive, line, directive.ToString(), flush: directive.Wait);
        try
        {
            await directives!.ExecuteAsync(directive, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Fault(InterruptionKind.Directive, 0, line, $"Host directive {directive} failed: {ex.Message}");
            throw new OperationCanceledException("Host directive failed.", ex);
        }
    }

    private void OnStatus(object? sender, StatusReport report)
    {
        JobPrompt? prompt = null;
        lock (gate)
        {
            if (progress.State is not (JobState.Running or JobState.Paused or JobState.ProgramPaused))
            {
                return;
            }

            // M0 holds the controller and is acknowledged only after cycle start.
            if (progress.State == JobState.Running && !pauseRequested && report.State == MachineState.Hold
                && inFlight.FirstOrDefault(l => l.PausePrompt is not null) is { } m0 && !ReferenceEquals(m0, prompted))
            {
                prompted = m0;
                prompt = new JobPrompt(m0.PausePrompt!, m0.FileLine);
            }

            var (executed, exact) = EstimateExecuted(report);
            progress = progress with
            {
                Executed = executed,
                ExecutedExact = exact,
                MachinePosition = report.MachinePosition?.ToString() ?? progress.MachinePosition,
                WorkOffset = report.WorkOffset?.ToString() ?? progress.WorkOffset,
                FeedOverride = report.Overrides?.Feed ?? progress.FeedOverride,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            journal.Record(progress);
        }

        if (prompt is not null)
        {
            SetState(JobState.ProgramPaused, "Program paused: " + prompt.Message, JobEventKinds.Prompt);
            PromptRequested?.Invoke(this, prompt);
        }
    }

    private (int Executed, bool Exact) EstimateExecuted(StatusReport report)
    {
        if (options.UseLineNumbers && report.LineNumber is > 0 and var reported)
        {
            return (Math.Max(FileLineFromWire(reported, progress.LastSent), startLine - 1), true);
        }

        var outstanding = inFlight.Count;
        var blocksInUse = report.PlannerBlocksAvailable is { } available
            ? Math.Max(0, options.PlannerBlocks - available)
            : report.State is MachineState.Idle ? 0 : options.PlannerBlocks;
        if (blocksInUse == 0)
        {
            return (progress.LastAcked, outstanding == 0 && report.State == MachineState.Idle);
        }

        // The oldest planner block is executing: walk back over the last acknowledged motion lines. Arcs occupy
        // several blocks, so this errs towards earlier lines.
        var node = ackedMotionLines.Last;
        for (var i = 1; i < blocksInUse && node is not null; i++)
        {
            node = node.Previous;
        }

        return (Math.Max(node?.Value ?? (startLine - 1), startLine - 1), false);
    }

    private void OnAlarm(object? sender, AlarmMessage alarm)
    {
        if (stopTask is not null)
        {
            return;
        }

        Fault(InterruptionKind.Alarm, alarm.Code, Progress.Executed, $"ALARM:{alarm.Code} ({FluidNcCodes.AlarmDescription(alarm.Code)})");
    }

    private void OnReset(object? sender, WelcomeMessage welcome)
    {
        if (stopTask is not null)
        {
            return;
        }

        // A banner without our reset: e-stop/reset pin or controller reboot. Position may be lost.
        Fault(InterruptionKind.Alarm, FluidNcCodes.AlarmAbortCycle, Progress.Executed, "Controller reset during the job");
    }

    private void OnDisconnected(object? sender, Exception? error)
    {
        lock (gate)
        {
            if (progress.State is JobState.Completed or JobState.Stopped or JobState.Faulted or JobState.Aborted or JobState.Interrupted)
            {
                return;
            }

            progress = progress with { State = JobState.Interrupted, Interruption = InterruptionKind.Disconnect, UpdatedAt = DateTimeOffset.UtcNow };
            journal.Record(progress, flush: true);
        }

        AddEvent(JobEventKinds.Disconnect, Progress.Executed, "Connection lost: " + (error?.Message ?? "closed"), flush: true);
        StateChanged?.Invoke(this, JobState.Interrupted);
        Release(HeatersOffAsync());
    }

    private void Fault(InterruptionKind kind, int code, int line, string message)
    {
        lock (gate)
        {
            if (progress.State is JobState.Faulted or JobState.Completed or JobState.Stopped or JobState.Stopping
                or JobState.Interrupted or JobState.Aborted)
            {
                return;
            }

            progress = progress with { State = JobState.Faulted, Interruption = kind, InterruptionCode = code, UpdatedAt = DateTimeOffset.UtcNow };
            journal.Record(progress, flush: true);
        }

        AddEvent(kind == InterruptionKind.Alarm ? JobEventKinds.Alarm : JobEventKinds.Error, line, message, flush: true);
        StateChanged?.Invoke(this, JobState.Faulted);
        Release(FaultFollowUpAsync(kind));
    }

    /// <summary>
    /// Ends the run after a fault or disconnect. <paramref name="followUp"/> (feed hold, heaters off) is recorded before
    /// the run is released so that <see cref="RunAsync"/> awaits it and the streamer is not disposed under it.
    /// </summary>
    private void Release(Task followUp)
    {
        lock (gate)
        {
            followUpTask = followUp;
        }

        try
        {
            runCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A late connection event after the run ended and the streamer was disposed.
        }

        faulted.TrySetResult();
    }

    private async Task FaultFollowUpAsync(InterruptionKind kind)
    {
        if (kind == InterruptionKind.Error && connection.IsConnected)
        {
            try
            {
                // Lines after the failing one are still buffered: hold so the machine does not continue out of context.
                await connection.SendRealtimeAsync(RealtimeCommand.FeedHold).ConfigureAwait(false);
            }
            catch (ControllerDisconnectedException)
            {
            }
        }

        await HeatersOffAsync().ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        SetState(JobState.Stopping, "Stop requested");
        await runCancellation.CancelAsync().ConfigureAwait(false);
        var interruption = Progress.Interruption;
        try
        {
            if (connection.IsConnected)
            {
                await connection.SendRealtimeAsync(RealtimeCommand.FeedHold).ConfigureAwait(false);
                var stopped = await connection.WaitForStatusAsync(s => s.IsMotionStopped, options.StopTimeout).ConfigureAwait(false);
                await connection.SoftResetAsync().ConfigureAwait(false);
                if (stopped.State != MachineState.Alarm)
                {
                    var status = await connection.WaitForStatusAsync(_ => true, options.StopTimeout).ConfigureAwait(false);
                    if (status.State != MachineState.Alarm)
                    {
                        await connection.ExecuteOkAsync("G53G0Z" + GcodeInterpreter.Format(options.SafeZMachine), ControllerQueries.LongCommandTimeout)
                            .ConfigureAwait(false);
                        await connection.SynchronizeAsync().ConfigureAwait(false);
                        AddEvent(JobEventKinds.State, Progress.Executed, "Retracted to safe Z");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ControllerDisconnectedException or ControllerCommandException or TimeoutException)
        {
            AddEvent(JobEventKinds.Warning, Progress.Executed, "Stop sequence incomplete: " + ex.Message, flush: true);
        }

        Update(p => p with { State = JobState.Stopped, Interruption = interruption == InterruptionKind.None ? InterruptionKind.Stop : interruption }, flush: true);
        AddEvent(JobEventKinds.State, Progress.Executed, "Stopped", flush: true);
        StateChanged?.Invoke(this, JobState.Stopped);
        await HeatersOffAsync().ConfigureAwait(false);
    }

    private void SetState(JobState state, string message, string kind = JobEventKinds.State)
    {
        Update(p => p with { State = state }, flush: true);
        AddEvent(kind, Progress.Executed, message, flush: true);
        if (state is JobState.Paused or JobState.ProgramPaused)
        {
            StartPausedTimer();
        }
        else
        {
            DisposePausedTimer();
        }

        StateChanged?.Invoke(this, state);
    }

    private void StartPausedTimer()
    {
        if (directives is null)
        {
            return;
        }

        lock (gate)
        {
            pausedHeaterTimer?.Dispose();
            pausedHeaterTimer = new Timer(_ =>
            {
                if (State is JobState.Paused or JobState.ProgramPaused)
                {
                    AddEvent(JobEventKinds.Warning, Progress.Executed, "Paused too long: heaters switched off", flush: true);
                    _ = HeatersOffAsync();
                }
            }, null, options.PausedHeaterTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisposePausedTimer()
    {
        lock (gate)
        {
            pausedHeaterTimer?.Dispose();
            pausedHeaterTimer = null;
        }
    }

    private async Task HeatersOffAsync()
    {
        if (directives is null)
        {
            return;
        }

        try
        {
            await directives.AllHeatersOffAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AddEvent(JobEventKinds.Warning, Progress.Executed, "Switching heaters off failed: " + ex.Message, flush: true);
        }
    }

    private void Update(Func<JobProgress, JobProgress> change, bool flush = false)
    {
        lock (gate)
        {
            progress = change(progress) with { UpdatedAt = DateTimeOffset.UtcNow };
            journal.Record(progress, flush);
        }
    }

    private void AddEvent(string kind, int? line, string message, bool flush = false) =>
        journal.AddEvent(progress.JobId, new JobEvent(DateTimeOffset.UtcNow, kind, line, message), flush);

    private sealed class SentLine(int fileLine, bool isMotion, string? pausePrompt, bool isOffsetQuery)
    {
        public int FileLine { get; } = fileLine;

        public bool IsMotion { get; } = isMotion;

        public string? PausePrompt { get; } = pausePrompt;

        public bool IsOffsetQuery { get; } = isOffsetQuery;

        public PendingLine? Pending { get; set; }
    }
}
