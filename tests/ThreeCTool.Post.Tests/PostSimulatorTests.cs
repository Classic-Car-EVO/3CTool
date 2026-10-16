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

    /// <summary>Tool tip Z (physical machine coordinates) of the touch plate top on the material and on the bed.</summary>
    private const double PlateTop = -400;

    private const double BedPlateTop = -430;

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

            // WCS X0 Y0 is machine X100 Y100.
            ProbeSurfaceAt = (x, y) => Math.Abs(x - 100) < 5 && Math.Abs(y - 100) < 5 ? BedPlateTop : PlateTop,
        });
        server = new TcpSimulatorServer(simulator);
    }

    [Fact]
    public async Task TouchPlateOnTheMaterial_EveryFileSetsTheWorkZ()
    {
        // The touch plate (10 mm) lies on the stock top at WCS Z 0, so WCS Z 0 must end up at tool tip Z −410.
        await HomeAsync();
        var machine = Machine();
        var setup = PostFixture.Setup(workOffset: Core.Projects.WorkOffset.G55);
        var post = new FluidNcMillingPost();

        simulator.ToolLength = 30;
        var (prompts, questions) = await RunAsync(post.Post(PostFixture.Request(Moves(), setup, PostFixture.Tool("T1"), machine)), "first.nc", yes: false);
        Assert.Equal(PlateTop - 10, simulator.WorkOffset(1)[2] - 30, 2);
        Assert.Equal(
            [
                "Insert tool T1 in collet ER11 6, stickout 20 mm, set the spindle dial to 18000 RPM",
                "Place the touch plate on the material at X20 Y15 under the tool, at most 600 mm below it, and attach the probe clip",
                "Remove the touch plate and the probe clip",
                "Start the spindle: confirm it runs at 18000 RPM",
            ],
            prompts);
        Assert.Equal(["Probe the touch plate on the bed at X0 Y0? Yes: plate on the bed at X0 Y0. No: plate on the material at X20 Y15"], questions);
        Assert.Equal(2, simulator.ProbeCount);

        simulator.ToolLength = 70;
        await RunAsync(post.Post(PostFixture.Request(Moves(), setup, PostFixture.Tool("T2"), machine, first: false)), "second.nc", yes: false);
        Assert.Equal(PlateTop - 10, simulator.WorkOffset(1)[2] - 70, 2);
        Assert.Equal(0, simulator.ToolLengthOffset, 6);
        Assert.Equal(4, simulator.ProbeCount);
    }

    [Fact]
    public async Task TouchPlateOnTheBed_SetsTheWorkZFromTheStockBottom()
    {
        // Stock bottom at WCS Z −20 on the bed: the plate on the bed puts WCS Z 0 at 20 − 10 mm above its top.
        await HomeAsync();
        simulator.ToolLength = 30;
        var (prompts, _) = await RunAsync(new FluidNcMillingPost().Post(PostFixture.Request(Moves(), machine: Machine())), "bed.nc", yes: true);

        Assert.Equal(BedPlateTop + 10, simulator.WorkOffset(0)[2] - 30, 2);
        Assert.Contains("Place the touch plate on the bed at X0 Y0 under the tool, at most 600 mm below it, and attach the probe clip", prompts);
        Assert.DoesNotContain(prompts, p => p.Contains("on the material", StringComparison.Ordinal));
        Assert.Equal(2, simulator.ProbeCount);
    }

    [Fact]
    public async Task TouchPlateSetup_EmergencyStopMidCut_ResumesWithoutReprobing()
    {
        await HomeAsync();
        var setup = PostFixture.Setup(workOffset: Core.Projects.WorkOffset.G55);
        simulator.ToolLength = 30;
        var output = new FluidNcMillingPost().Post(PostFixture.Request(LongMoves(), setup, PostFixture.Tool("T1"), Machine()));
        var (plan, messages) = await RunInterruptedAsync(output, "plate.nc");

        Assert.Null(plan.Probe);
        Assert.DoesNotContain(messages, m => m.Contains("touch plate", StringComparison.Ordinal));
        Assert.Equal(PlateTop - 10, simulator.WorkOffset(1)[2] - 30, 2);
        Assert.Equal(2, simulator.ProbeCount);
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

    /// <summary>Streams <paramref name="output"/>, confirming every M0 prompt and answering the questions; returns both.</summary>
    private async Task<(List<string> Prompts, List<string> Questions)> RunAsync(PostOutput output, string name, bool yes)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, output.Text);
        var file = await JobFile.OpenAsync(path);
        Assert.Equal(output.Sha256, file.Sha256);
        Assert.Equal(output.LineCount, file.LineCount);

        var connection = connections[0];
        var journal = new SqliteJobJournal(Path.Combine(folder, name + ".db"), TimeSpan.FromMilliseconds(50));
        journals.Add(journal);
        var questions = new List<string>();
        using var streamer = new JobStreamer(connection, file, journal, name)
        {
            AskOperator = (question, _) =>
            {
                questions.Add(question.Text);
                return Task.FromResult<bool?>(yes);
            },
        };
        Assert.Empty(streamer.Validate());
        var prompts = new List<string>();
        streamer.PromptRequested += (_, prompt) =>
        {
            lock (prompts)
            {
                prompts.Add(prompt.Message);
            }

            _ = Task.Run(streamer.ResumeAsync);
        };

        Assert.Equal(JobState.Completed, await streamer.RunAsync().WaitAsync(Timeout));
        return (prompts, questions);
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

        using (var streamer = new JobStreamer(connections[0], file, journal, name) { AskOperator = Material })
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
        using (var streamer = new JobStreamer(connection, file, journal, name) { AskOperator = Material })
        {
            Assert.Equal(JobState.Completed, await streamer.RunAsync(resume.Line, resume.FirstLinePrefix).WaitAsync(Timeout));
        }

        return (plan, confirmations.Messages);

        static Task<bool?> Material(JobQuestion question, CancellationToken token) => Task.FromResult<bool?>(false);
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
