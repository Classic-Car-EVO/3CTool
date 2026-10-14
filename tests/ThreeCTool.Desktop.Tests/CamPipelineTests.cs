using System.Numerics;
using System.Security.Cryptography;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Feeder.Contracts;
using ThreeCTool.FluidNcSimulator;
using ThreeCTool.Geometry;

namespace ThreeCTool.Desktop.Tests;

/// <summary>A project with a box part, a roughing and a finishing operation, run through the whole CAM chain once.</summary>
public sealed class CamProjectFixture : IAsyncLifetime
{
    public DesktopFixture Files { get; } = new();

    public ProjectSession Session { get; private set; } = null!;

    public ComputeService Compute { get; } = new(ComputeBackend.Cpu);

    public CamPipeline Pipeline { get; private set; } = null!;

    public IReadOnlyList<Post.PostOutput> Outputs { get; private set; } = [];

    public Operation Rough { get; private set; } = null!;

    public Operation Finish { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var flat = DesktopFixture.FlatEndMill();
        var ball = DesktopFixture.BallEndMill();
        await Files.Library.SaveToolAsync(flat);
        await Files.Library.SaveToolAsync(ball);
        (Session, Rough, Finish) = await CreateProjectAsync("project");
        Pipeline = new CamPipeline(Session, Compute);
        Outputs = await Pipeline.RunAllAsync();
    }

    /// <summary>A 20 x 16 x 6 mm block in fitted stock: 6 mm flat Z-level roughing, then 3 mm ball parallel finishing.</summary>
    public async Task<(ProjectSession Session, Operation Rough, Operation Finish)> CreateProjectAsync(string folder)
    {
        var tools = await Files.Library.ListToolsAsync();
        var session = await ProjectSession.CreateAsync(
            Files.ProjectFolder(folder), Files.LibraryPath, "Block", await Files.DefaultMachineIdAsync(), await Files.MaterialIdAsync());
        await session.ImportPartAsync(Files.WriteBoxStl(folder + ".stl", Vector3.Zero, new Vector3(20, 16, 6)), LengthUnit.Millimeter);
        PartGeometry.FitBox(session.Stock, (await session.MillPartAsync()).Bounds, 2);
        await session.SaveAsync();
        await session.AddToolsAsync([.. tools.Select(t => t.Id)]);
        var setup = session.Setups[0];
        var rough = await session.AddOperationAsync(setup, StrategyType.ZLevelRoughing, tools.Single(t => t.Type == Core.Tooling.ToolType.FlatEnd).Id);
        var finish = await session.AddOperationAsync(setup, StrategyType.ParallelFinishing, tools.Single(t => t.Type == Core.Tooling.ToolType.BallEnd).Id);
        return (session, rough, finish);
    }

    public async Task DisposeAsync()
    {
        Pipeline?.Dispose();
        if (Session is not null)
        {
            await Session.DisposeAsync();
        }

        Compute.Dispose();
        await Files.DisposeAsync();
    }
}

[Collection(CamProjectGroup.Name)]
public sealed class CamPipelineTests(CamProjectFixture project)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RunAll_PostsOneVerifiedFilePerTool()
    {
        var session = project.Session;
        Assert.Equal(2, project.Outputs.Count);
        Assert.Equal(2, project.Pipeline.FeedPlans.Count);
        Assert.False(project.Pipeline.SimulationIsStale);
        Assert.All(session.Setups[0].Operations, o => Assert.NotNull(o.Conditions));

        var files = await session.ListGcodeFilesAsync();
        Assert.Equal(project.Outputs.Select(o => o.FileName), files.Select(f => f.Path));
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(PartGeometry.Resolve(session.Folder, file.Path));
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.NotNull(file.ToolId);
            Assert.True(file.Rpm > 0);
        }

        var sections = GcodeSections.Parse(project.Outputs[0].Lines);
        Assert.Equal(StrategyType.ZLevelRoughing, Assert.Single(sections).Strategy);
        Assert.StartsWith("(Operation: ", project.Outputs[0].Lines[sections[0].FirstLine - 1], StringComparison.Ordinal);

        foreach (var op in new[] { project.Rough, project.Finish })
        {
            var result = await session.GetToolpathResultAsync(op.Id);
            Assert.NotNull(result);
            Assert.True(result.MoveCount > 0);
            Assert.True(File.Exists(session.ToolpathCachePath(op.Id)));
        }
    }

    [Fact]
    public async Task Generate_ReusesCacheAndRegeneratesOnlyChangedAndLaterOperations()
    {
        var (session, rough, finish) = await project.CreateProjectAsync("cache");
        await using var _ = session;
        using (var first = new CamPipeline(session, project.Compute))
        {
            Assert.All(await first.GenerateAsync(), g => Assert.False(g.FromCache));
        }

        using var fresh = new CamPipeline(session, project.Compute);
        var cached = await fresh.GenerateAsync();
        Assert.All(cached, g => Assert.True(g.FromCache));

        var finishHash = fresh.InputHash(finish);
        finish.Parameters.RasterAngle += 45;
        await session.SaveAsync();
        Assert.NotEqual(finishHash, fresh.InputHash(finish));
        var second = await fresh.GenerateAsync();
        Assert.True(second.Single(g => g.Operation.Id == rough.Id).FromCache);
        Assert.False(second.Single(g => g.Operation.Id == finish.Id).FromCache);

        var roughHash = fresh.InputHash(rough);
        finishHash = fresh.InputHash(finish);
        rough.Parameters.Stepdown = 1.5;
        await session.SaveAsync();
        Assert.NotEqual(roughHash, fresh.InputHash(rough));
        Assert.NotEqual(finishHash, fresh.InputHash(finish));
        Assert.All(await fresh.GenerateAsync(), g => Assert.False(g.FromCache));
        Assert.True(fresh.SimulationIsStale);
    }

    [Fact]
    public async Task SplitStock_MachinesEveryPieceWithItsOwnFiles()
    {
        var (session, rough, finish) = await project.CreateProjectAsync("tiled");
        await using var _ = session;
        var bounds = (await session.MillPartAsync()).Bounds;
        StockTiling.Fit(session.Stock, "Board", 14, 20, 5, (bounds.Min.X, bounds.Min.Y, bounds.Min.Z, bounds.Max.X, bounds.Max.Y, bounds.Max.Z), 2);
        await session.SaveAsync();
        Assert.Equal((28.0, 20.0, 10.0), (session.Stock.SizeX, session.Stock.SizeY, session.Stock.SizeZ));

        using var pipeline = new CamPipeline(session, project.Compute);
        Assert.True(pipeline.IsTiled);
        Assert.Empty(pipeline.TilingProblems());
        var pieces = pipeline.Pieces;
        Assert.Equal(4, pieces.Count);

        var outputs = await pipeline.RunAllAsync();
        Assert.Empty(pipeline.EmptyPieces);
        Assert.Equal(4, pipeline.Simulations.Count);
        Assert.Equal(
            Enumerable.Range(1, 4).SelectMany(p => project.Outputs.Select(o => $"gcode/P0{p}_{o.FileName["gcode/".Length..]}")),
            outputs.Select(o => o.FileName));
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], outputs.Select(o => o.Piece));
        Assert.Equal(outputs.Select(o => (o.FileName, o.Piece)), (await session.ListGcodeFilesAsync()).Select(f => (f.Path, f.Piece)));
        Assert.Contains(outputs[0].Lines, l => l.StartsWith("(Mount piece 1 of 4", StringComparison.Ordinal));
        Assert.DoesNotContain(outputs[1].Lines, l => l.StartsWith("(Mount piece", StringComparison.Ordinal));

        foreach (var piece in pieces)
        {
            foreach (var op in new[] { rough, finish })
            {
                var generated = pipeline.GetToolpath(op.Id, piece.Number);
                Assert.NotNull(generated);
                Assert.Equal(piece.Number, generated.Piece);
                Assert.Equal(piece.Number, (await session.GetToolpathResultAsync(op.Id, piece.Number))?.Piece);
                // Roughing clears a band of about one more tool radius around the piece.
                var reach = (op == rough ? 3 : 1) * session.FindTool(op.ToolId)!.Diameter / 2;
                var frame = generated.Toolpath.Frame;
                var cuts = generated.Toolpath.Moves.Where(m => m.Kind == Toolpaths.MoveKind.Cut).Select(m => frame.ToolInPart(m).Tip).ToList();
                Assert.NotEmpty(cuts);
                Assert.All(cuts, m => Assert.InRange(m.X, piece.MinX - reach - 0.01, piece.MaxX + reach + 0.01));
                Assert.All(cuts, m => Assert.InRange(m.Z, piece.MinZ - 0.5, piece.MaxZ));
            }
        }

        Assert.NotEqual(pipeline.InputHash(rough, 1), pipeline.InputHash(rough, 2));
        using (var reloaded = new CamPipeline(session, project.Compute))
        {
            Assert.Equal(8, await reloaded.LoadCacheAsync());
        }

        session.Setups[0].Orientation = SetupOrientation.Indexed;
        var problem = Assert.Single(pipeline.TilingProblems());
        Assert.Contains("not 3-axis", problem, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.GenerateAsync());
    }

    [Fact]
    public async Task DirectFeeder_UploadsPostedFilesWithLearningTags()
    {
        var session = project.Session;
        var file = (await session.ListGcodeFilesAsync())[0];
        var metadata = JobUploader.Describe(session, file);
        Assert.Equal("Block", metadata.ProjectName);
        Assert.Equal(file.ToolId, metadata.ToolId);
        Assert.Equal(session.Material!.Id, metadata.MaterialId);

        var simulator = new Simulator(new SimulatorOptions { TimeScale = 100 });
        await using var server = new TcpSimulatorServer(simulator);
        var folder = Path.Combine(project.Files.Root, "feeder");
        await using var feeder = new DirectFeeder(
            folder,
            o =>
            {
                o.ReconnectInterval = TimeSpan.FromMilliseconds(100);
                o.StatusInterval = TimeSpan.FromMilliseconds(20);
                o.StatusMinInterval = TimeSpan.FromMilliseconds(10);
                o.StatusHeartbeat = TimeSpan.FromMilliseconds(200);
            },
            s => s.AddLogging(l => l.ClearProviders().SetMinimumLevel(LogLevel.Warning)));
        await feeder.StartAsync();
        Assert.True(feeder.IsRunning);

        var settings = await feeder.GetSettingsAsync();
        settings.Transport = TransportKind.Tcp;
        settings.Host = "127.0.0.1";
        settings.TcpPort = server.Port;
        Assert.Equal(server.Port, (await feeder.UpdateSettingsAsync(settings)).TcpPort);
        await WaitConnectedAsync(feeder.Client);

        var path = PartGeometry.Resolve(session.Folder, file.Path);
        var job = await JobUploader.UploadAsync(feeder.Client, path, file.Sha256, metadata);
        Assert.Equal(file.LineCount, job.LineCount);

        var listed = Assert.Single((await feeder.Client.ListJobsAsync(new Empty())).Jobs);
        var tags = listed.Header.Tags;
        Assert.Equal(file.SetupId.ToString(), tags[FeederTags.SetupId]);
        Assert.Equal(file.ToolId.ToString(), tags[FeederTags.ToolId]);
        var lines = GcodeSections.SplitLines(await File.ReadAllTextAsync(path));
        Assert.Equal(GcodeSections.Parse(lines), FeederTags.ParseSections(tags[FeederTags.Sections]));

        await Assert.ThrowsAsync<InvalidDataException>(() => JobUploader.UploadAsync(feeder.Client, path, new string('0', 64), metadata));
        await feeder.StopAsync();
        Assert.False(feeder.IsRunning);
    }

    private static async Task WaitConnectedAsync(Feeder.Contracts.Feeder.FeederClient client)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var call = client.WatchStatus(new Empty(), cancellationToken: cts.Token);
        await foreach (var status in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            if (status.Connection == ConnectionState.Connected)
            {
                return;
            }
        }
    }
}
