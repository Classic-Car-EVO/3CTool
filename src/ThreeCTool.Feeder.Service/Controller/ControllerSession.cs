using System.Diagnostics;
using ThreeCTool.Core.Machines;
using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Streaming;
using ThreeCTool.Streaming.Config;
using ThreeCTool.Streaming.Protocol;
using ThreeCTool.Streaming.Transport;

namespace ThreeCTool.Feeder.Service.Controller;

/// <summary>What the feeder learned about the controller when it connected.</summary>
public sealed record ControllerIdentity(
    string Description,
    ControllerBuildInfo BuildInfo,
    ControllerConfig? Config,
    int PlannerBlocks,
    IReadOnlyList<string> Warnings)
{
    public string Firmware => BuildInfo.Version?.Version.ToString() ?? BuildInfo.VersionLine ?? "unknown";
}

/// <summary>
/// Keeps one controller connection open from the stored settings: connects, identifies the controller (build info,
/// config, buffer sizes), and reconnects after a disconnect or a settings change.
/// </summary>
public sealed class ControllerSession(
    SettingsStore settings,
    FeederOptions options,
    ILogger<ControllerSession> logger,
    Func<ConnectionSettings, ITransport>? transportFactory = null) : BackgroundService
{
    private const int IdentifyAttempts = 3;
    private static readonly TimeSpan StartupQuiet = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupQuietLimit = TimeSpan.FromSeconds(15);
    private readonly Lock gate = new();
    private readonly Func<ConnectionSettings, ITransport> createTransport = transportFactory ?? Transports.Create;
    private CancellationTokenSource? reconnect;
    private ControllerConnection? connection;
    private ControllerIdentity? identity;
    private ConnectionState state;
    private string? lastError;
    private string? connectedKey;
    private volatile int lastAlarm = -1;

    /// <summary>Code of the last alarm while the controller is still in the alarm state.</summary>
    public int? AlarmCode => LastStatus?.State == MachineState.Alarm && lastAlarm >= 0 ? lastAlarm : null;

    /// <summary>Connection state, identity, status or error changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A new connection was identified and is ready.</summary>
    public event EventHandler<ControllerConnection>? Connected;

    public ConnectionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public ControllerIdentity? Identity
    {
        get
        {
            lock (gate)
            {
                return identity;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (gate)
            {
                return lastError;
            }
        }
    }

    public StatusReport? LastStatus => CurrentOrNull?.LastStatus;

    /// <summary>The connection when connected and identified; otherwise null.</summary>
    public ControllerConnection? CurrentOrNull
    {
        get
        {
            lock (gate)
            {
                return state == ConnectionState.Connected && connection is { IsConnected: true } c ? c : null;
            }
        }
    }

    /// <summary>The identified connection, or <see cref="ControllerDisconnectedException"/>.</summary>
    public ControllerConnection Current => CurrentOrNull ?? throw new ControllerDisconnectedException("The controller is not connected.");

    /// <summary>Re-reads the controller configuration (machine must be idle, no job running).</summary>
    public async Task<ControllerConfig> RefreshConfigAsync(CancellationToken cancellationToken)
    {
        var config = await Current.QueryConfigAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (identity is not null)
            {
                identity = identity with { Config = config };
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settings.Changed += OnSettingsChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (gate)
                {
                    reconnect = cycle;
                }

                await RunConnectionAsync(cycle.Token).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (!cycle.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(options.ReconnectInterval, cycle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Settings changed: reconnect now.
                    }
                }
            }
        }
        finally
        {
            settings.Changed -= OnSettingsChanged;
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionSettings = settings.ConnectionSettings();
        lock (gate)
        {
            connectedKey = ConnectionKey(settings.Current);
        }

        ControllerConnection? current = null;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            SetState(ConnectionState.Connecting, null, null, null);
            var transport = createTransport(connectionSettings);
            current = new ControllerConnection(transport, new ConnectionOptions { StatusInterval = options.StatusInterval });
            current.Disconnected += (_, ex) => disconnected.TrySetResult();
            current.StatusReceived += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            current.AlarmReceived += (_, alarm) =>
            {
                lastAlarm = alarm.Code;
                Changed?.Invoke(this, EventArgs.Empty);
            };
            current.ResetDetected += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            await current.OpenAsync(cancellationToken).ConfigureAwait(false);
            var identified = await IdentifyAsync(current, cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Connected, current, identified, null);
            logger.ControllerConnected(current.Description, identified.Firmware, identified.PlannerBlocks);
            Connected?.Invoke(this, current);
            await disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Disconnected, null, null, "Controller connection lost.");
            logger.ControllerDisconnected(current.Description);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(ConnectionState.Disconnected, null, null, null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException
            or System.Net.Sockets.SocketException or ControllerCommandException or ArgumentException)
        {
            logger.ControllerConnectFailed(ex.Message);
            SetState(ConnectionState.Disconnected, null, null, ex.Message);
        }
        finally
        {
            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<ControllerIdentity> IdentifyAsync(ControllerConnection connection, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await IdentifyOnceAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (ControllerCommandException ex) when (ex.Result.Outcome == LineOutcome.Discarded && attempt < IdentifyAttempts)
            {
                // ESP32 boards reboot when the serial port opens; the start-up banner discards queries sent while booting.
                await WaitForQuietAsync(connection, cancellationToken).ConfigureAwait(false);
                if (connection.NeedsResync)
                {
                    await connection.ResynchronizeAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Waits until the controller has sent nothing but status reports for <see cref="StartupQuiet"/>.</summary>
    private static async Task WaitForQuietAsync(ControllerConnection connection, CancellationToken cancellationToken)
    {
        var last = Stopwatch.GetTimestamp();
        void OnMessage(object? sender, ControllerMessage message)
        {
            if (message is not StatusMessage)
            {
                Interlocked.Exchange(ref last, Stopwatch.GetTimestamp());
            }
        }

        var start = Stopwatch.GetTimestamp();
        connection.MessageReceived += OnMessage;
        try
        {
            while (Stopwatch.GetElapsedTime(Interlocked.Read(ref last)) < StartupQuiet && Stopwatch.GetElapsedTime(start) < StartupQuietLimit)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connection.MessageReceived -= OnMessage;
        }
    }

    private static async Task<ControllerIdentity> IdentifyOnceAsync(ControllerConnection connection, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        await connection.WaitForStatusAsync(_ => true, connection.Options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        var build = await connection.QueryBuildInfoAsync(cancellationToken).ConfigureAwait(false);
        if (build.Version is null)
        {
            warnings.Add($"Controller firmware is not recognized as FluidNC ({build.VersionLine ?? "no version"}).");
        }
        else if (build.Version.IsOlderThanTested)
        {
            warnings.Add($"FluidNC {build.Version.Version} is older than the tested {FluidNcVersionInfo.MinimumTested}.");
        }

        ControllerConfig? config = null;
        try
        {
            config = await connection.QueryConfigAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ControllerCommandException ex) when (ex.Result.Outcome != LineOutcome.Discarded)
        {
            warnings.Add($"Controller configuration could not be read: {ex.Message}");
        }

        var blocks = config?.PlannerBlocks ?? 16;
        if (await connection.DetectBufferSizesAsync(cancellationToken).ConfigureAwait(false) is { } buffers)
        {
            blocks = buffers.PlannerBlocks;
        }

        return new ControllerIdentity(connection.Description, build, config, blocks, warnings);
    }

    private void SetState(ConnectionState newState, ControllerConnection? current, ControllerIdentity? identified, string? error)
    {
        lock (gate)
        {
            state = newState;
            connection = current;
            identity = identified;
            lastError = error;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string ConnectionKey(FeederSettings s) => $"{s.Transport}|{s.SerialPort}|{s.BaudRate}|{s.Host}|{s.TcpPort}";

    private void OnSettingsChanged(object? sender, FeederSettings e)
    {
        lock (gate)
        {
            if (ConnectionKey(e) == connectedKey)
            {
                return;
            }

            try
            {
                reconnect?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Cycle already finished.
            }
        }
    }
}
