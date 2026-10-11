using System.Globalization;

using Microsoft.Data.Sqlite;

using ThreeCTool.FluidNcSimulator;
using ThreeCTool.Streaming;
using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Recovery;
using ThreeCTool.Streaming.Transport;

namespace ThreeCTool.Streaming.Tests;

/// <summary>Simulated FluidNC controller on a loopback TCP port, plus a temp folder for job files and journals.</summary>
internal sealed class SimHarness : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly List<ControllerConnection> connections = [];
    private readonly List<IDisposable> disposables = [];

    private SimHarness(SimulatorOptions options)
    {
        Simulator = new Simulator(options);
        Server = new TcpSimulatorServer(Simulator);
        Directory.CreateDirectory(Folder);
    }

    public Simulator Simulator { get; }

    public TcpSimulatorServer Server { get; }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "3ctool-stream-" + Guid.NewGuid().ToString("N"));

    public string JournalPath => Path.Combine(Folder, "journal.db");

    /// <summary>Starts the simulator (fast time) and, unless <paramref name="home"/> is false, homes it and sets G54.</summary>
    public static async Task<SimHarness> StartAsync(SimulatorOptions? options = null, bool home = true, double[]? g54 = null)
    {
        options ??= new SimulatorOptions();
        if (options.TimeScale == 1)
        {
            options.TimeScale = 100;
        }

        var harness = new SimHarness(options);
        if (home)
        {
            var connection = await harness.ConnectAsync();
            await connection.HomeAsync();
            var offset = g54 ?? Jobs.G54;
            await connection.ExecuteOkAsync(string.Create(CultureInfo.InvariantCulture, $"G10L2P1X{offset[0]}Y{offset[1]}Z{offset[2]}"));
            await connection.DisposeAsync();
            harness.connections.Remove(connection);
        }

        return harness;
    }

    public async Task<ControllerConnection> ConnectAsync(ConnectionOptions? options = null)
    {
        var connection = new ControllerConnection(
            new TcpTransport("127.0.0.1", Server.Port),
            options ?? new ConnectionOptions { StatusInterval = TimeSpan.FromMilliseconds(20) });
        await connection.OpenAsync();
        connections.Add(connection);
        return connection;
    }

    public async Task<JobFile> WriteJobAsync(IEnumerable<string> lines, string name = "job.nc")
    {
        var path = Path.Combine(Folder, name);
        await File.WriteAllLinesAsync(path, lines);
        return await JobFile.OpenAsync(path);
    }

    public SqliteJobJournal OpenJournal()
    {
        var journal = new SqliteJobJournal(JournalPath, TimeSpan.FromMilliseconds(50));
        disposables.Add(journal);
        return journal;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }

        disposables.ForEach(d => d.Dispose());
        await Server.DisposeAsync();
        Simulator.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Job files used by the streaming and recovery tests.</summary>
internal static class Jobs
{
    public static readonly double[] G54 = [100, 100, -300];

    /// <summary>
    /// Milling zigzag in G54: <paramref name="moves"/> 40 mm cuts at Z-1, ending at Z5 with M30. Returns the lines and
    /// the final machine position.
    /// </summary>
    public static (List<string> Lines, double[] FinalMachine) Zigzag(int moves = 150, bool probe = false, int? pauseAt = null, int? errorAt = null)
    {
        var lines = new List<string> { "(3C Tool test job)", "G21 G90 G94 G54" };
        if (probe)
        {
            lines.AddRange(ProbeBlock);
        }

        lines.AddRange(["G0 Z5", "G0 X0 Y0", "M3 S12000", "M8", "G1 Z-1 F300", "F3000"]);
        double x = 0, y = 0;
        for (var i = 1; i <= moves; i++)
        {
            if (i == pauseAt)
            {
                lines.Add("M0 (Change to tool 2)");
            }

            if (i == errorAt)
            {
                lines.Add("G5 X1");
            }

            x = i % 2 == 1 ? 40 : 0;
            y = i * 0.5;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{x} Y{y:0.0}"));
        }

        lines.AddRange(["G0 Z5", "M9", "M5", "M30"]);
        return (lines, [G54[0] + x, G54[1] + y, G54[2] + 5]);
    }

    /// <summary>Tool-setter style probe: touch off at machine X50 Y50 and zero G54 Z on the contact point.</summary>
    public static readonly string[] ProbeBlock =
    [
        "(MC:PROBE BEGIN)",
        "G53 G0 Z-5",
        "G53 G0 X50 Y50",
        "G91 G38.2 Z-400 F3000",
        "G90",
        "G10 L20 P1 Z0",
        "G0 Z5",
        "(MC:PROBE END)",
    ];

    /// <summary>FDM print in G54 with a heater directive and extrusion on B.</summary>
    public static (List<string> Lines, double[] FinalMachine) Print(int moves = 150)
    {
        var lines = new List<string> { "G21 G90 G94 G54", "(MC:HEAT T0 210 WAIT)", "G0 Z5", "G0 X0 Y0", "G92 B0", "G1 Z0.2 F600", "F1800" };
        double x = 0, y = 0, e = 0;
        for (var i = 1; i <= moves; i++)
        {
            x = i % 2 == 1 ? 30 : 0;
            y = i * 0.4;
            e += 1.5;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{x} Y{y:0.0} B{e:0.000}"));
        }

        lines.AddRange(["G0 Z10", "(MC:HEATERS OFF)", "M30"]);
        return (lines, [G54[0] + x, G54[1] + y, G54[2] + 10]);
    }

    public static void AssertPosition(double[] expected, double[] actual, double tolerance = 1e-3)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance,
                $"Axis {"XYZAB"[i]}: expected {expected[i]}, actual {actual[i]} (all: {string.Join(", ", actual)})");
        }
    }

    public static string Wire(string line) => GcodeBlock.Parse(line).ToWire();
}

/// <summary>Operator that confirms every prompt and records them.</summary>
internal sealed class AutoOperator(bool confirm = true) : IOperatorInteraction
{
    private readonly List<string> messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (messages)
            {
                return [.. messages];
            }
        }
    }

    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        lock (messages)
        {
            messages.Add(message);
        }

        return Task.FromResult(confirm);
    }
}

/// <summary>Records heater directives (stand-in for Moonraker on the feeder).</summary>
internal sealed class FakeHeaters : IHostDirectiveHandler
{
    private readonly List<string> executed = [];
    private int allOff;

    public int AllOffCount => Volatile.Read(ref allOff);

    public IReadOnlyList<string> Executed
    {
        get
        {
            lock (executed)
            {
                return [.. executed];
            }
        }
    }

    public Exception? Failure { get; set; }

    public bool CanHandle(HostDirective directive) => directive.Verb is "HEAT" or "HEATERS";

    public Task ExecuteAsync(HostDirective directive, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        lock (executed)
        {
            executed.Add(directive.Text);
        }

        return Task.CompletedTask;
    }

    public Task AllHeatersOffAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref allOff);
        return Task.CompletedTask;
    }
}
