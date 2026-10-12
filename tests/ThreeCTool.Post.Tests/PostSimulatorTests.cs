using System.Globalization;

using Microsoft.Data.Sqlite;

using ThreeCTool.Core.Machines;
using ThreeCTool.FluidNcSimulator;
using ThreeCTool.Streaming;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Recovery;
using ThreeCTool.Streaming.Transport;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Post.Tests;

/// <summary>Posted files streamed to the FluidNC simulator: probing, expressions and the resulting tool tip heights.</summary>
public sealed class PostSimulatorTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Tool tip Z (physical machine coordinates) of the touch plate top and of the tool setter.</summary>
    private const double PlateTop = -400;

    private const double SetterTop = -500;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "3ctool-post-" + Guid.NewGuid().ToString("N"));
    private readonly Simulator simulator;
    private readonly TcpSimulatorServer server;
    private readonly List<ControllerConnection> connections = [];
    private readonly List<SqliteJobJournal> journals = [];

    public PostSimulatorTests()
    {
        Directory.CreateDirectory(folder);
        simulator = new Simulator(new SimulatorOptions
        {
            TimeScale = 100,
            ProbeSurfaceAt = (x, y) => Math.Abs(x - 50) < 5 && Math.Abs(y - 50) < 5 ? SetterTop : PlateTop,
        });
        server = new TcpSimulatorServer(simulator);
    }

    [Fact]
    public async Task ToolSetterSetup_SecondToolCutsAtTheSameWorkZ()
    {
        // The touch plate (10 mm) lies on the stock top at WCS Z 0, so WCS Z 0 must end up at tool tip Z −410.
        await HomeAsync();
        var machine = Machine();
        var setup = PostFixture.Setup(method: ProbeMethod.ToolSetter);
        var post = new FluidNcMillingPost();

        simulator.ToolLength = 40;
        var first = post.Post(PostFixture.Request(Moves(), setup, PostFixture.Tool("T1"), machine, first: true));
        var prompts = await RunAsync(first, "first.nc");
        Assert.Equal(PlateTop - 10, WorkZeroTip(40), 2);
        Assert.Equal(0, simulator.ToolLengthOffset, 2);
        Assert.Equal(SetterTop + 40, simulator.WorkOffset(5)[2], 2);
        Assert.Equal(
            [
                "Insert tool T1 in collet ER11 6, stickout 20 mm, set the spindle dial to 18000 RPM",
                "Place the touch plate on the stock under the tool, at most 600 mm below it, and attach the probe clip",
                "Remove the touch plate and the probe clip",
                "Start the spindle: confirm it runs at 18000 RPM",
            ],
            prompts);

        simulator.ToolLength = 55;
        var second = post.Post(PostFixture.Request(Moves(), setup, PostFixture.Tool("T2"), machine, first: false));
        prompts = await RunAsync(second, "second.nc");
        Assert.Equal(15, simulator.ToolLengthOffset, 2);
        Assert.Equal(PlateTop - 10, WorkZeroTip(55), 2);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(1 + 2 + 1, simulator.ProbeCount / 2);
    }

    [Fact]
    public async Task TouchPlateSetup_EveryFileSetsTheWorkZ()
    {
        await HomeAsync();
        var machine = Machine();
        var setup = PostFixture.Setup(method: ProbeMethod.TouchPlate, workOffset: Core.Projects.WorkOffset.G55);
        var post = new FluidNcMillingPost();

        simulator.ToolLength = 30;
        await RunAsync(post.Post(PostFixture.Request(Moves(), setup, PostFixture.Tool("T1"), machine)), "first.nc");
        Assert.Equal(PlateTop - 10, simulator.WorkOffset(1)[2] - 30, 2);

        simulator.ToolLength = 70;
        await RunAsync(post.Post(PostFixture.Request(Moves(), setup, PostFixture.Tool("T2"), machine, first: false)), "second.nc");
        Assert.Equal(PlateTop - 10, simulator.WorkOffset(1)[2] - 70, 2);
        Assert.Equal(0, simulator.ToolLengthOffset, 6);
    }

    [Fact]
    public async Task ToolSetterSetup_EmergencyStopMidCut_ReprobesTheSetterAndResumes()
    {
        await HomeAsync();
        var machine = Machine();
        var setup = PostFixture.Setup(method: ProbeMethod.ToolSetter);
        var post = new FluidNcMillingPost();
        simulator.ToolLength = 40;
        await RunAsync(post.Post(PostFixture.Request(Moves(), setup, PostFixture.Tool("T1"), machine, first: true)), "first.nc");

        simulator.ToolLength = 55;
        var second = post.Post(PostFixture.Request(LongMoves(), setup, PostFixture.Tool("T2"), machine, first: false));
        var (plan, messages) = await RunInterruptedAsync(second, "second.nc");

        Assert.NotNull(plan.Probe);
        Assert.Equal("(MC:PROBE BEGIN)", second.Lines[plan.Probe.BeginLine - 1]);
        Assert.DoesNotContain(messages, m => m.Contains("touch plate", StringComparison.Ordinal));
        Assert.Equal(15, simulator.ToolLengthOffset, 2);
        Assert.Equal(PlateTop - 10, WorkZeroTip(55), 2);
        Assert.Equal(SetterTop + 40, simulator.WorkOffset(5)[2], 2);
        Assert.Equal(-5, simulator.PhysicalPosition[2], 2);
    }

    [Fact]
    public async Task TouchPlateSetup_EmergencyStopMidCut_ReprobesThePlateAndResumes()
    {
        await HomeAsync();
        var setup = PostFixture.Setup(method: ProbeMethod.TouchPlate, workOffset: Core.Projects.WorkOffset.G55);
        simulator.ToolLength = 30;
        var output = new FluidNcMillingPost().Post(PostFixture.Request(LongMoves(), setup, PostFixture.Tool("T1"), Machine()));
        var (plan, messages) = await RunInterruptedAsync(output, "plate.nc");

        Assert.NotNull(plan.Probe);
        Assert.Equal("(MC:PROBE BEGIN TOUCHPLATE)", output.Lines[plan.Probe.BeginLine - 1]);
        Assert.Contains(messages, m => m.Contains("Place the touch plate", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Remove the touch plate", StringComparison.Ordinal));
        Assert.Equal(PlateTop - 10, simulator.WorkOffset(1)[2] - 30, 2);
        Assert.Equal(4, simulator.ProbeCount);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }

        journals.ForEach(j => j.Dispose());
        await server.DisposeAsync();
        simulator.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MachineProfile Machine()
    {
        var machine = PostFixture.Machine();
        machine.Probe = new ProbeConfig
        {
            ToolSetterX = 50,
            ToolSetterY = 50,
            ToolSetterStartZ = -5,
            MaxDistance = 600,
            SeekFeed = 3000,
            LatchFeed = 100,
            Retract = 2,
            TouchPlateThickness = 10,
        };
        return machine;
    }

    /// <summary>Plate spot (20, 15) is machine (120, 115) with G54/G55 X/Y at 100.</summary>
    private static List<ToolpathMove> Moves() =>
    [
        new(MoveKind.Rapid, 20, 15, 5),
        new(MoveKind.Plunge, 20, 15, -1),
        new(MoveKind.Cut, 30, 15, -1),
        new(MoveKind.Cut, 30, 25, -1),
        new(MoveKind.Rapid, 30, 25, 5),
    ];

    /// <summary>A zigzag of 100 passes 20 mm long at work Z −1, long enough to interrupt mid-cut.</summary>
    private static List<ToolpathMove> LongMoves()
    {
        var moves = new List<ToolpathMove> { new(MoveKind.Rapid, 20, 15, 5), new(MoveKind.Plunge, 20, 15, -1) };
        for (var i = 0; i < 100; i++)
        {
            var y = 15 + (i * 0.5);
            moves.Add(new(MoveKind.Cut, i % 2 == 0 ? 40 : 20, y, -1));
            moves.Add(new(MoveKind.Cut, i % 2 == 0 ? 40 : 20, y + 0.5, -1));
        }

        moves.Add(new(MoveKind.Rapid, moves[^1].X, moves[^1].Y, 5));
        return moves;
    }

    /// <summary>Physical tool tip Z when the controller is at work Z 0 in G54.</summary>
    private double WorkZeroTip(double toolLength) => simulator.WorkOffset(0)[2] + simulator.ToolLengthOffset - toolLength;

    private async Task<ControllerConnection> ConnectAsync()
    {
        var connection = new ControllerConnection(
            new TcpTransport("127.0.0.1", server.Port),
            new ConnectionOptions { StatusInterval = TimeSpan.FromMilliseconds(20) });
        await connection.OpenAsync();
        connections.Add(connection);
        return connection;
    }

    private async Task HomeAsync()
    {
        var connection = await ConnectAsync();
        await connection.HomeAsync();
        foreach (var p in new[] { 1, 2 })
        {
            await connection.ExecuteOkAsync(string.Create(CultureInfo.InvariantCulture, $"G10L2P{p}X100Y100Z-300"));
        }
    }

    /// <summary>Streams <paramref name="output"/>, confirming every M0 prompt; returns the prompts.</summary>
    private async Task<List<string>> RunAsync(PostOutput output, string name)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, output.Text);
        var file = await JobFile.OpenAsync(path);
        Assert.Equal(output.Sha256, file.Sha256);
        Assert.Equal(output.LineCount, file.LineCount);

        var connection = connections[0];
        var journal = new SqliteJobJournal(Path.Combine(folder, name + ".db"), TimeSpan.FromMilliseconds(50));
        journals.Add(journal);
        using var streamer = new JobStreamer(connection, file, journal, name);
        Assert.Empty(streamer.Validate());
        var prompts = new List<string>();
        streamer.PromptRequested += (_, message) =>
        {
            lock (prompts)
            {
                prompts.Add(message);
            }

            _ = Task.Run(streamer.ResumeAsync);
        };

        Assert.Equal(JobState.Completed, await streamer.RunAsync().WaitAsync(Timeout));
        return prompts;
    }

    /// <summary>
    /// Streams the file until 40 blocks into the cut, hits the emergency stop, then recovers and resumes
    /// to the end; returns the recovery plan and the operator confirmations.
    /// </summary>
    private async Task<(RecoveryPlan Plan, List<string> Messages)> RunInterruptedAsync(PostOutput output, string name)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, output.Text);
        var file = await JobFile.OpenAsync(path);
        var journal = new SqliteJobJournal(Path.Combine(folder, name + ".db"), TimeSpan.FromMilliseconds(50));
        journals.Add(journal);
        var operation = output.Lines.ToList().FindIndex(l => l.StartsWith("(Operation:", StringComparison.Ordinal));
        var target = simulator.ExecutedBlocks + output.Lines.Take(operation).Count(l => !l.StartsWith('(')) + 40;

        using (var streamer = new JobStreamer(connections[0], file, journal, name))
        {
            streamer.PromptRequested += (_, _) => _ = Task.Run(streamer.ResumeAsync);
            var run = streamer.RunAsync();
            Assert.True(simulator.WaitUntil(s => s.ExecutedBlocks > target, Timeout));
            simulator.EmergencyStop();
            Assert.Equal(JobState.Faulted, await run.WaitAsync(Timeout));
        }

        var connection = await ConnectAsync();
        var options = new RecoveryOptions();
        var plan = RecoveryPlanner.Plan(file, journal.Load(name)!, options);
        Assert.Equal(RecoveryAction.Resume, plan.Action);
        Assert.True(plan.ResumeLine > operation);
        var confirmations = new Confirmations();
        var resume = await new RecoveryExecutor(connection, file, journal, name, confirmations, options).ExecuteAsync(plan).WaitAsync(Timeout);
        Assert.NotNull(resume);
        using (var streamer = new JobStreamer(connection, file, journal, name))
        {
            Assert.Equal(JobState.Completed, await streamer.RunAsync(resume.Line, resume.FirstLinePrefix).WaitAsync(Timeout));
        }

        return (plan, confirmations.Messages);
    }

    private sealed class Confirmations : IOperatorInteraction
    {
        public List<string> Messages { get; } = [];

        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }

            return Task.FromResult(true);
        }
    }
}
