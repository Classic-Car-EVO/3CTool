using System.Text;

using ThreeCTool.Streaming.Protocol;
using ThreeCTool.Streaming.Transport;

namespace ThreeCTool.Streaming;

public enum FlowControl
{
    /// <summary>Grbl character counting: keep the controller RX buffer as full as possible.</summary>
    CharacterCounting,

    /// <summary>One line in flight at a time.</summary>
    SendResponse,
}

public enum LineOutcome
{
    Ok,
    Error,

    /// <summary>No response will come: the controller was reset, alarmed or disconnected while the line was pending.</summary>
    Discarded,
}

public sealed record LineResult(LineOutcome Outcome, int ErrorCode, IReadOnlyList<ControllerMessage> Responses)
{
    public bool IsOk => Outcome == LineOutcome.Ok;
}

public sealed class ConnectionOptions
{
    public FlowControl FlowControl { get; set; } = FlowControl.CharacterCounting;

    /// <summary>Controller serial RX buffer in bytes (Grbl: 128). Updated from <c>Bf:</c> by <see cref="ControllerConnection.DetectBufferSizesAsync"/>.</summary>
    public int RxBufferSize { get; set; } = 128;

    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class ControllerDisconnectedException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>A line queued to the controller; completes on its <c>ok</c>/<c>error</c> response.</summary>
public sealed class PendingLine
{
    private readonly TaskCompletionSource<LineResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingLine(string text, int byteCount, object? tag)
    {
        Text = text;
        ByteCount = byteCount;
        Tag = tag;
    }

    public string Text { get; }

    /// <summary>Bytes occupied in the controller RX buffer, including the newline.</summary>
    public int ByteCount { get; }

    public object? Tag { get; }

    public Task<LineResult> Completion => completion.Task;

    internal List<ControllerMessage> Responses { get; } = [];

    internal void Complete(LineOutcome outcome, int code) => completion.TrySetResult(new LineResult(outcome, code, Responses.ToArray()));
}

/// <summary>
/// Line protocol to a FluidNC controller: flow-controlled line sending with in-order <c>ok</c>/<c>error</c> matching,
/// real-time commands, periodic status polling and reset/alarm/disconnect detection.
/// </summary>
public sealed class ControllerConnection : IAsyncDisposable
{
    private readonly ITransport transport;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly object sync = new();
    private readonly Queue<PendingLine> pending = new();
    private readonly List<(Func<StatusReport, bool> Predicate, TaskCompletionSource<StatusReport> Tcs)> statusWaiters = [];
    private readonly List<TaskCompletionSource<WelcomeMessage>> welcomeWaiters = [];
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource spaceSignal = NewSignal();
    private int pendingBytes;
    private Task? readLoop;
    private Task? pollLoop;
    private AxisVector? workOffset;
    private OverrideValues? overrides;
    private volatile bool connected;
    private volatile bool needsResync;
    private bool disposed;

    public ControllerConnection(ITransport transport, ConnectionOptions? options = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Options = options ?? new ConnectionOptions();
    }

    public event EventHandler<ControllerMessage>? MessageReceived;

    public event EventHandler<StatusReport>? StatusReceived;

    public event EventHandler<AlarmMessage>? AlarmReceived;

    /// <summary>Raised on every start-up/reset banner (own soft reset, reset pin, e-stop or controller reboot).</summary>
    public event EventHandler<WelcomeMessage>? ResetDetected;

    public event EventHandler<Exception?>? Disconnected;

    public ConnectionOptions Options { get; }

    public string Description => transport.Description;

    public bool IsConnected => connected;

    /// <summary>
    /// True after an alarm discarded pending lines: responses to them may still arrive, so no new lines are accepted
    /// until <see cref="ResynchronizeAsync"/> resets the controller.
    /// </summary>
    public bool NeedsResync => needsResync;

    /// <summary>Last status report, with machine/work position, work offset and overrides completed from earlier reports.</summary>
    public StatusReport? LastStatus { get; private set; }

    public WelcomeMessage? LastWelcome { get; private set; }

    public int OutstandingBytes
    {
        get
        {
            lock (sync)
            {
                return pendingBytes;
            }
        }
    }

    public int OutstandingLines
    {
        get
        {
            lock (sync)
            {
                return pending.Count;
            }
        }
    }

    /// <summary>Highest number of unacknowledged bytes seen (diagnostics for flow control).</summary>
    public int MaxOutstandingBytes { get; private set; }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (readLoop is not null)
        {
            throw new InvalidOperationException("A connection can only be opened once.");
        }

        await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        connected = true;
        readLoop = Task.Run(() => ReadLoopAsync(lifetime.Token), CancellationToken.None);
        pollLoop = Task.Run(() => PollLoopAsync(lifetime.Token), CancellationToken.None);
    }

    /// <summary>Queues a line, waiting for RX buffer space first. The returned line completes on its response.</summary>
    public async Task<PendingLine> WriteLineAsync(string line, object? tag = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.TrimEnd('\r', '\n');
        if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
        {
            throw new ArgumentException("A line must not contain line breaks.", nameof(line));
        }

        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        if (bytes.Length > Options.RxBufferSize)
        {
            throw new ArgumentException($"Line is {bytes.Length} bytes, longer than the controller RX buffer ({Options.RxBufferSize}).", nameof(line));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingLine? entry = null;
            Task wait;
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (sync)
                {
                    if (!connected)
                    {
                        throw new ControllerDisconnectedException("Controller is not connected.");
                    }

                    if (needsResync)
                    {
                        throw new InvalidOperationException("Controller needs a reset after an alarm before new lines can be sent.");
                    }

                    var fits = Options.FlowControl == FlowControl.SendResponse
                        ? pending.Count == 0
                        : pendingBytes + bytes.Length <= Options.RxBufferSize;
                    if (fits)
                    {
                        entry = new PendingLine(text, bytes.Length, tag);
                        pending.Enqueue(entry);
                        pendingBytes += bytes.Length;
                        MaxOutstandingBytes = Math.Max(MaxOutstandingBytes, pendingBytes);
                    }

                    wait = spaceSignal.Task;
                }

                if (entry is not null)
                {
                    // Never cancel half-way through a line: a partial line would be completed by the next one.
                    await transport.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                    return entry;
                }
            }
            finally
            {
                writeLock.Release();
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a line and waits for its response.</summary>
    public async Task<LineResult> ExecuteAsync(string line, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var entry = await WriteLineAsync(line, null, cancellationToken).ConfigureAwait(false);
        try
        {
            return await entry.Completion.WaitAsync(timeout ?? Options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The response may still arrive later and would be matched to the wrong line.
            needsResync = true;
            throw;
        }
    }

    /// <summary>Like <see cref="ExecuteAsync"/> but throws <see cref="ControllerCommandException"/> unless the response is <c>ok</c>.</summary>
    public async Task<LineResult> ExecuteOkAsync(string line, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(line, timeout, cancellationToken).ConfigureAwait(false);
        return result.IsOk ? result : throw new ControllerCommandException(line, result);
    }

    public async ValueTask SendRealtimeAsync(RealtimeCommand command, CancellationToken cancellationToken = default)
    {
        if (!connected)
        {
            throw new ControllerDisconnectedException("Controller is not connected.");
        }

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transport.WriteAsync(new[] { (byte)command }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Requests status reports until one satisfies <paramref name="predicate"/>.</summary>
    public async Task<StatusReport> WaitForStatusAsync(Func<StatusReport, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var tcs = new TaskCompletionSource<StatusReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (!connected)
            {
                throw new ControllerDisconnectedException("Controller is not connected.");
            }

            statusWaiters.Add((predicate, tcs));
        }

        try
        {
            await SendRealtimeAsync(RealtimeCommand.StatusReport, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                statusWaiters.RemoveAll(w => w.Tcs == tcs);
            }
        }
    }

    /// <summary>Sends Ctrl-X and waits for the banner. All pending lines are discarded.</summary>
    public async Task<WelcomeMessage> SoftResetAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<WelcomeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            welcomeWaiters.Add(tcs);
        }

        try
        {
            await SendRealtimeAsync(RealtimeCommand.SoftReset, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout ?? Options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                welcomeWaiters.Remove(tcs);
            }
        }
    }

    /// <summary>Soft reset to get back to a known protocol state after an alarm or a lost response.</summary>
    public Task<WelcomeMessage> ResynchronizeAsync(CancellationToken cancellationToken = default) => SoftResetAsync(null, cancellationToken);

    /// <summary>
    /// Reads the controller's RX buffer size and planner block count from an idle <c>Bf:</c> report (null if the
    /// controller does not report buffer state). Updates <see cref="ConnectionOptions.RxBufferSize"/>.
    /// </summary>
    public async Task<(int PlannerBlocks, int RxBytes)?> DetectBufferSizesAsync(CancellationToken cancellationToken = default)
    {
        if (OutstandingLines > 0)
        {
            throw new InvalidOperationException("Buffer sizes can only be detected with no lines in flight.");
        }

        var report = await WaitForStatusAsync(s => s.State is MachineState.Idle or MachineState.Alarm, Options.CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (report.PlannerBlocksAvailable is not { } blocks || report.RxBytesAvailable is not { } rx)
        {
            return null;
        }

        Options.RxBufferSize = rx;
        return (blocks, rx);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
        foreach (var loop in new[] { readLoop, pollLoop })
        {
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        lifetime.Dispose();
        writeLock.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var line = new List<byte>(256);
        Exception? error = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                for (var i = 0; i < count; i++)
                {
                    var b = buffer[i];
                    if (b == (byte)'\n')
                    {
                        // Keep leading whitespace: $CD (config dump) output is indentation-structured YAML.
                        var text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd();
                        line.Clear();
                        if (text.Trim().Length > 0)
                        {
                            Dispatch(ProtocolParser.Parse(text));
                        }
                    }
                    else if (b != (byte)'\r')
                    {
                        line.Add(b);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            error = ex;
        }

        OnDisconnected(error);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && connected)
            {
                await SendRealtimeAsync(RealtimeCommand.StatusReport, cancellationToken).ConfigureAwait(false);
                await Task.Delay(Options.StatusInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException
            or System.Net.Sockets.SocketException)
        {
            // Connection closed; the read loop reports it.
        }
    }

    private void Dispatch(ControllerMessage message)
    {
        switch (message)
        {
            case OkMessage or ErrorMessage:
                PendingLine? head = null;
                lock (sync)
                {
                    if (pending.Count > 0)
                    {
                        head = pending.Dequeue();
                        pendingBytes -= head.ByteCount;
                        SignalSpace();
                    }
                }

                head?.Complete(message is ErrorMessage ? LineOutcome.Error : LineOutcome.Ok, (message as ErrorMessage)?.Code ?? 0);
                break;
            case StatusMessage status:
                OnStatus(status.Report);
                break;
            case AlarmMessage alarm:
                lock (sync)
                {
                    if (pending.Count > 0)
                    {
                        needsResync = true;
                    }
                }

                // Raise the event before discarding: waiters released by the discard must observe the handlers' state.
                AlarmReceived?.Invoke(this, alarm);
                DiscardPending();
                break;
            case WelcomeMessage welcome:
                needsResync = false;
                LastWelcome = welcome;
                ResetDetected?.Invoke(this, welcome);
                DiscardPending();
                List<TaskCompletionSource<WelcomeMessage>> waiters;
                lock (sync)
                {
                    waiters = [.. welcomeWaiters];
                }

                waiters.ForEach(w => w.TrySetResult(welcome));
                break;
            default:
                lock (sync)
                {
                    if (pending.Count > 0)
                    {
                        pending.Peek().Responses.Add(message);
                    }
                }

                break;
        }

        MessageReceived?.Invoke(this, message);
    }

    private void OnStatus(StatusReport raw)
    {
        if (raw.WorkOffset is not null)
        {
            workOffset = raw.WorkOffset;
        }

        if (raw.Overrides is not null)
        {
            overrides = raw.Overrides;
        }

        var wco = workOffset;
        var report = raw with
        {
            WorkOffset = wco,
            Overrides = overrides,
            MachinePosition = raw.MachinePosition ?? (raw.WorkPosition is not null && wco is not null ? raw.WorkPosition.Add(wco) : null),
            WorkPosition = raw.WorkPosition ?? (raw.MachinePosition is not null && wco is not null ? raw.MachinePosition.Subtract(wco) : null),
        };
        LastStatus = report;

        List<TaskCompletionSource<StatusReport>> satisfied;
        lock (sync)
        {
            satisfied = [.. statusWaiters.Where(w => w.Predicate(report)).Select(w => w.Tcs)];
        }

        satisfied.ForEach(t => t.TrySetResult(report));
        StatusReceived?.Invoke(this, report);
    }

    private void DiscardPending()
    {
        List<PendingLine> dropped;
        lock (sync)
        {
            dropped = [.. pending];
            pending.Clear();
            pendingBytes = 0;
            SignalSpace();
        }

        dropped.ForEach(p => p.Complete(LineOutcome.Discarded, 0));
    }

    private void SignalSpace()
    {
        var old = spaceSignal;
        spaceSignal = NewSignal();
        old.TrySetResult();
    }

    private void OnDisconnected(Exception? error)
    {
        List<TaskCompletionSource<StatusReport>> statuses;
        List<TaskCompletionSource<WelcomeMessage>> welcomes;
        lock (sync)
        {
            connected = false;
            statuses = [.. statusWaiters.Select(w => w.Tcs)];
            welcomes = [.. welcomeWaiters];
        }

        // Raise the event before releasing waiters so they observe the handlers' state.
        Disconnected?.Invoke(this, error);
        DiscardPending();
        var exception = new ControllerDisconnectedException("Connection to the controller was lost.", error);
        statuses.ForEach(t => t.TrySetException(exception));
        welcomes.ForEach(t => t.TrySetException(exception));
    }
}

public sealed class ControllerCommandException(string line, LineResult result)
    : Exception(result.Outcome == LineOutcome.Error
        ? $"'{line}' failed: error:{result.ErrorCode} ({FluidNcCodes.ErrorDescription(result.ErrorCode)})"
        : $"'{line}' was discarded by the controller.")
{
    public string Line { get; } = line;

    public LineResult Result { get; } = result;
}
