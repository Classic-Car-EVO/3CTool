using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.FluidNcSimulator;

using FeederClient = ThreeCTool.Feeder.Contracts.Feeder.FeederClient;

namespace ThreeCTool.Feeder.Tests;

/// <summary>
/// Feeder daemon on a temp data directory (TLS on a loopback port, plus a Unix socket) driving a simulated FluidNC
/// controller over TCP.
/// </summary>
internal sealed class FeederHarness : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public static readonly double[] G54 = [100, 100, -300];

    private readonly List<GrpcChannel> channels = [];
    private bool named;
    private WebApplication? app;

    private FeederHarness(SimulatorOptions options)
    {
        Simulator = new Simulator(options);
        Server = new TcpSimulatorServer(Simulator);
        Directory.CreateDirectory(Folder);
    }

    public Simulator Simulator { get; }

    public TcpSimulatorServer Server { get; }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "3cf-" + Guid.NewGuid().ToString("N")[..12]);

    public string SocketPath => Path.Combine(Folder, "feeder.sock");

    public Uri Address { get; private set; } = null!;

    public WebApplication App => app ?? throw new InvalidOperationException("Not started.");

    public FeederClient Local { get; private set; } = null!;

    public LocalAdmin.LocalAdminClient Admin { get; private set; } = null!;

    public string Fingerprint { get; private set; } = string.Empty;

    public static async Task<FeederHarness> StartAsync(SimulatorOptions? options = null, bool home = true, Action<FeederOptions>? configure = null)
    {
        options ??= new SimulatorOptions { TimeScale = 100 };

        var harness = new FeederHarness(options);
        await harness.StartFeederAsync(configure);
        await harness.WaitStatusAsync(s => s.Connection == ConnectionState.Connected && s.MachineState.Length > 0);
        if (home)
        {
            await harness.Local.HomeAsync(new Empty());
            await harness.WaitStatusAsync(s => s.MachineState == "Idle");
            harness.Simulator.SetWorkOffset(0, G54);
        }

        return harness;
    }

    public async Task StartFeederAsync(Action<FeederOptions>? configure = null)
    {
        app = FeederHost.Build([], options =>
        {
            options.DataDirectory = Folder;
            options.Port = 0;
            options.ListenAddress = "127.0.0.1";
            options.UnixSocketPath = SocketPath;
            options.ReconnectInterval = TimeSpan.FromMilliseconds(100);
            options.StatusInterval = TimeSpan.FromMilliseconds(20);
            options.JournalCommitInterval = TimeSpan.FromMilliseconds(50);
            options.StatusMinInterval = TimeSpan.FromMilliseconds(10);
            options.StatusHeartbeat = TimeSpan.FromMilliseconds(200);
            options.JogKeepAlive = TimeSpan.FromMilliseconds(300);
            configure?.Invoke(options);
        }, services => services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Warning)));

        var settings = app.Services.GetRequiredService<SettingsStore>();
        var current = settings.Current;
        current.Transport = Contracts.TransportKind.Tcp;
        current.Host = "127.0.0.1";
        current.TcpPort = Server.Port;
        if (!named)
        {
            current.Name = "Test feeder";
            named = true;
        }

        settings.Update(current);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Address = new Uri(addresses.Single(a => a.StartsWith("https://", StringComparison.Ordinal)));
        var local = FeederChannels.ForUnixSocket(SocketPath);
        channels.Add(local);
        Local = new FeederClient(local);
        Admin = new LocalAdmin.LocalAdminClient(local);
        Fingerprint = (await Local.GetInfoAsync(new Empty())).CertificateFingerprint;
    }

    public async Task StopFeederAsync()
    {
        foreach (var channel in channels)
        {
            channel.Dispose();
        }

        channels.Clear();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
            app = null;
        }

        SqliteConnection.ClearAllPools();
    }

    public async Task RestartFeederAsync(Action<FeederOptions>? configure = null)
    {
        await StopFeederAsync();
        await StartFeederAsync(configure);
        await WaitStatusAsync(s => s.Connection == ConnectionState.Connected && s.MachineState.Length > 0);
    }

    /// <summary>Pairs a desktop over TLS and returns a client authenticated with its token.</summary>
    public async Task<(FeederClient Client, PairingResult Pairing)> PairAsync(string clientName = "Workshop PC")
    {
        var code = await Admin.StartPairingAsync(new Empty());
        var result = await FeederPairing.PairAsync(Address.ToString(), code.Code, clientName,
            (_, seen) => Task.FromResult(CertificateFingerprint.Matches(code.CertificateFingerprint, seen)));
        return (new FeederClient(Track(FeederChannels.ForPairedFeeder(Address.ToString(), result.Fingerprint, result.Token))), result);
    }

    public GrpcChannel Track(GrpcChannel channel)
    {
        channels.Add(channel);
        return channel;
    }

    public T Service<T>()
        where T : notnull => App.Services.GetRequiredService<T>();

    /// <summary>Watches the status stream until <paramref name="predicate"/> holds.</summary>
    public async Task<FeederStatus> WaitStatusAsync(Func<FeederStatus, bool> predicate, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? Timeout);
        FeederStatus? last = null;
        try
        {
            using var call = Local.WatchStatus(new Empty(), cancellationToken: cts.Token);
            await foreach (var status in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                last = status;
                if (predicate(status))
                {
                    return status;
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }

        throw new TimeoutException("Status condition not reached; last status: " + last);
    }

    /// <summary>Waits for the run to end, confirming every prompt (recovery confirmations, M0) on the way.</summary>
    public async Task<FeederStatus> RunToEndAsync(string runId, Func<Prompt, PromptAnswer>? answer = null)
    {
        answer ??= p => new PromptAnswer { Id = p.Id, Confirmed = true };
        var answered = new HashSet<string>();
        return await WaitStatusAsync(s =>
        {
            if (s.Prompt is { } prompt && answered.Add(prompt.Id))
            {
                Local.AnswerPrompt(answer(prompt));
            }

            return s.Job is { } job && job.RunId == runId && !s.Recovering && s.Prompt is null
                && job.State is "Completed" or "Stopped" or "Faulted" or "Interrupted" or "Aborted";
        });
    }

    public static async Task<JobInfo> UploadAsync(FeederClient client, IEnumerable<string> lines, string name = "job.nc", Action<UploadHeader>? header = null)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var head = new UploadHeader
        {
            Name = name,
            Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ProjectName = "Bracket",
            SetupName = "Top",
            ToolDescription = "6 mm flat",
            Material = "6061",
            PlannedRpm = 12000,
        };
        header?.Invoke(head);
        using var call = client.UploadJob();
        await call.RequestStream.WriteAsync(new UploadChunk { Header = head });
        for (var offset = 0; offset < bytes.Length; offset += 1000)
        {
            await call.RequestStream.WriteAsync(new UploadChunk { Data = ByteString.CopyFrom(bytes, offset, Math.Min(1000, bytes.Length - offset)) });
        }

        await call.RequestStream.CompleteAsync();
        return await call;
    }

    public async ValueTask DisposeAsync()
    {
        await StopFeederAsync();
        await Server.DisposeAsync();
        Simulator.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Test programs.</summary>
internal static class TestJobs
{
    /// <summary>Milling zigzag in G54 (40 mm cuts at Z-1) ending at Z5 with M30; returns the final machine position.</summary>
    public static (List<string> Lines, double[] FinalMachine) Zigzag(int moves = 150, int? pauseAt = null)
    {
        var lines = new List<string> { "(3C Tool test job)", "G21 G90 G94 G54", "G0 Z5", "G0 X0 Y0", "M3 S12000", "M8", "G1 Z-1 F300", "F3000" };
        double x = 0, y = 0;
        for (var i = 1; i <= moves; i++)
        {
            if (i == pauseAt)
            {
                lines.Add("M0 (Check the clamps)");
            }

            x = i % 2 == 1 ? 40 : 0;
            y = i * 0.5;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{x} Y{y:0.0}"));
        }

        lines.AddRange(["G0 Z5", "M9", "M5", "M30"]);
        var g54 = FeederHarness.G54;
        return (lines, [g54[0] + x, g54[1] + y, g54[2] + 5]);
    }

    public static void AssertPosition(double[] expected, double[] actual, double tolerance = 1e-3)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance,
                $"Axis {"XYZAB"[i]}: expected {expected[i]}, actual {actual[i]} (all: {string.Join(", ", actual)})");
        }
    }
}
