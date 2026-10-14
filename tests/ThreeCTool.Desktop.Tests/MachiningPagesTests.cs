using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Desktop.ViewModels;
using ThreeCTool.Geometry;
using ThreeCTool.Rendering;
using ThreeCTool.Simulation;

namespace ThreeCTool.Desktop.Tests;

public sealed class MachiningPagesTests : IAsyncDisposable
{
    private readonly DesktopFixture fixture = new();
    private readonly FakeFileDialogs files = new();
    private readonly AppServices services;
    private MainViewModel? main;

    public MachiningPagesTests()
    {
        services = new AppServices(new AppPaths(fixture.Root));
    }

    public async ValueTask DisposeAsync()
    {
        if (main is not null)
        {
            main.Dispose();
            await main.Workspace.ReplaceAsync(null);
        }

        services.Dispose();
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SetupsAndOperations_EditAndPersist()
    {
        var page = await OpenAsync();
        await page.ShowAsync();
        var session = main!.Workspace.Session!;
        var folder = session.Folder;
        var first = Assert.Single(page.Setups);
        Assert.Same(first, page.SelectedSetup);
        Assert.NotNull(page.SetupEditor);
        Assert.Empty(page.Tools);

        await page.AddOperationCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("Add tools", main.Message, StringComparison.Ordinal);
        main.DismissMessageCommand.Execute(null);

        await PrepareAsync();
        await page.ShowAsync();
        Assert.Equal(2, page.Tools.Count);
        var flat = page.Tools.Single(t => t.Type == ToolType.FlatEnd);
        var ball = page.Tools.Single(t => t.Type == ToolType.BallEnd);

        // Setups: add an indexed one, edit it, reorder and remove.
        page.NewOrientation = MachiningViewModel.Orientations.Single(o => o.Value == SetupOrientation.Indexed);
        await page.AddSetupCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Setups.Count);
        var second = page.SelectedSetup!;
        Assert.Same(page.Setups[1], second);
        var setupEditor = page.SetupEditor!;
        Assert.Same(second.Setup, setupEditor.Setup);
        Assert.True(setupEditor.IsIndexed);
        Assert.Equal(WcsReference.RotaryAxis, second.Setup.WcsReference);

        setupEditor.Name = "Flipped";
        setupEditor.IndexAngle.Text = "90";
        setupEditor.Orientation = SetupEditor.Orientations.Single(o => o.Value == SetupOrientation.ThreeAxis);
        await FlushAsync();
        Assert.Equal("Flipped", second.Title);
        Assert.Equal(90, second.Setup.IndexAngle, 9);
        Assert.Equal(SetupOrientation.ThreeAxis, second.Setup.Orientation);
        Assert.Equal(WcsReference.StockTopCenter, setupEditor.Reference.Value);
        Assert.False(setupEditor.IsRotary);

        // Multi-sided: the stock turned over, spun and held by tabs at a set depth.
        Assert.True(setupEditor.IsThreeAxis);
        Assert.Equal(StockFace.Top, setupEditor.Face.Value);
        Assert.Null(setupEditor.Spin.Value);
        Assert.StartsWith("Automatic", setupEditor.HoldingFields[0].Hint, StringComparison.Ordinal);
        setupEditor.Face = SetupEditor.Faces.Single(f => f.Value == StockFace.Bottom);
        setupEditor.Spin = SetupEditor.Spins.Single(s => s.Value == 180);
        setupEditor.Anchor = SetupEditor.Anchors.Single(a => a.Value == TurnAnchor.FrontLeft);
        setupEditor.HoldingFields[0].Text = "4";
        setupEditor.HoldingFields[1].Text = "3";
        await FlushAsync();
        Assert.Equal((StockFace.Bottom, (int?)180, TurnAnchor.FrontLeft), (second.Setup.Face, second.Setup.Spin, second.Setup.Anchor));
        Assert.Equal(((double?)4, 3), (second.Setup.DepthLimit, second.Setup.TabCount));
        Assert.StartsWith("3-axis, bottom face up", second.Detail, StringComparison.Ordinal);

        await page.MoveSetupUpCommand.ExecuteAsync(second);
        Assert.Equal("Flipped", page.Setups[0].Title);
        Assert.Equal("Flipped", session.Setups[0].Name);
        var removing = page.RemoveSetupCommand.ExecuteAsync(page.Setups[0]);
        await Wait.AnswerAsync(main, confirm: true);
        await removing;
        Assert.Same(first.Setup, Assert.Single(page.Setups).Setup);
        Assert.Single(session.Setups);

        // Operations: add, edit with defaults shown as hints, switch tool, duplicate, reorder, disable and remove.
        page.SelectedSetup = page.Setups[0];
        page.NewStrategy = MachiningViewModel.Strategies.Single(s => s.Value == StrategyType.ZLevelRoughing);
        page.NewTool = flat;
        await page.AddOperationCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        var row = Assert.Single(page.Operations);
        Assert.Same(row, page.SelectedOperation);
        var editor = page.OperationEditor!;
        Assert.True(editor.UsesPattern && editor.UsesEntry && editor.IsPlanar);
        Assert.Equal(string.Empty, editor.Stepover.Text);
        Assert.StartsWith("auto: ", editor.Stepover.Hint, StringComparison.Ordinal);
        var flatHint = editor.Stepdown.Hint;

        editor.Stepover.Text = "1.5";
        editor.Direction = OperationEditor.Directions.Single(d => d.Value == CutDirection.Conventional);
        editor.Name = "Rough";
        await FlushAsync();
        Assert.Equal(1.5, row.Operation.Parameters.Stepover);
        Assert.Equal(CutDirection.Conventional, row.Operation.Parameters.Direction);
        Assert.Equal("Rough", row.Title);

        editor.Stepdown.Text = "abc";
        Assert.True(editor.Stepdown.HasError);
        await page.GenerateCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.All(page.Feeds, f => Assert.Equal("Not planned yet.", f.Detail));
        editor.Stepdown.Text = string.Empty;
        main.DismissMessageCommand.Execute(null);

        editor.Tool = ball;
        await FlushAsync();
        Assert.Equal(ball.Id, row.Operation.ToolId);
        Assert.Contains(ball.Name, row.Detail, StringComparison.Ordinal);
        Assert.NotEqual(flatHint, editor.Stepdown.Hint);

        await page.DuplicateOperationCommand.ExecuteAsync(row);
        Assert.Equal(2, page.Operations.Count);
        var copy = page.SelectedOperation!;
        Assert.NotEqual(row.Operation.Id, copy.Operation.Id);
        await page.MoveOperationDownCommand.ExecuteAsync(page.Operations[0]);
        Assert.Equal(copy.Operation.Id, page.Operations[0].Operation.Id);
        var original = page.Operations[1];
        await page.ToggleOperationCommand.ExecuteAsync(original);
        Assert.False(original.Enabled);
        Assert.False(original.Operation.Enabled);
        Assert.Equal("Disabled", original.Status);

        removing = page.RemoveOperationCommand.ExecuteAsync(page.Operations[0]);
        await Wait.AnswerAsync(main, confirm: true);
        await removing;
        Assert.Equal(row.Operation.Id, Assert.Single(page.Operations).Operation.Id);

        // Everything is saved with the project.
        await main.Project.CloseCommand.ExecuteAsync(null);
        Assert.Empty(page.Setups);
        Assert.Null(page.OperationEditor);
        await main.Project.OpenFolderAsync(folder);
        await page.ShowAsync();
        var reopened = Assert.Single(page.Operations);
        Assert.Equal("Rough", reopened.Title);
        Assert.False(reopened.Enabled);
        Assert.Equal("1.5", page.OperationEditor!.Stepover.Text);
        Assert.Equal(ball.Id, page.OperationEditor.Tool?.Id);
        Assert.Equal(CutDirection.Conventional, page.OperationEditor.Direction.Value);
    }

    [Fact]
    public async Task Run_GeneratesPlansSimulatesPostsAndPlaysBack()
    {
        var page = await OpenAsync();
        var folder = main!.Workspace.Session!.Folder;
        await PrepareAsync();
        await AddOperationsAsync(page);
        Assert.Equal("No toolpaths generated yet.", page.ToolpathSummary);

        await page.GenerateCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.False(page.IsRunning);
        Assert.StartsWith("2 of 2 toolpaths up to date", page.ToolpathSummary, StringComparison.Ordinal);
        Assert.All(page.Operations, o => Assert.Contains("moves", o.Status, StringComparison.Ordinal));
        Assert.Equal(2, page.Scene.Scene.Toolpaths.Count);
        Assert.Equal(page.SelectedOperation!.Operation.Id, page.Scene.Scene.SelectedToolpath);
        Assert.All(page.Feeds, f => Assert.Equal("Not planned yet.", f.Detail));

        await page.PlanFeedsCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.Equal(2, page.Feeds.Count);
        Assert.All(page.Feeds, f => Assert.Contains("rpm", f.Detail, StringComparison.Ordinal));
        Assert.All(page.Feeds, f => Assert.StartsWith("Limited by", f.Limit, StringComparison.Ordinal));
        Assert.All(page.Operations, o => Assert.Contains(" F", o.Status, StringComparison.Ordinal));

        await page.SimulateCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.True(page.HasSimulation);
        Assert.False(page.SimulationIsStale);
        Assert.False(page.BlocksOutput);
        Assert.StartsWith("Deviation from the part", page.DeviationSummary, StringComparison.Ordinal);
        var playback = page.Playback;
        Assert.True(playback.IsAvailable);
        await playback.SettleAsync();
        Assert.True(playback.Duration > 0);
        Assert.Equal(playback.Duration, playback.Time);
        var scene = page.Scene.Scene;
        Assert.NotEmpty(Assert.Single(scene.Points).Positions);
        Assert.Null(scene.Progress);

        playback.SeekTo(1, 2);
        await playback.SettleAsync();
        Assert.Equal((1, 2), scene.Progress);
        Assert.NotNull(scene.Tool);
        Assert.NotEmpty(playback.PositionText);
        Assert.NotEmpty(Assert.Single(scene.Points).Positions);
        playback.PreviousStepCommand.Execute(null);
        await playback.SettleAsync();
        Assert.Equal(1, scene.Progress?.Path);
        playback.PreviousStepCommand.Execute(null);
        await playback.SettleAsync();
        Assert.Equal(0, scene.Progress?.Path);
        Assert.True(playback.Time < 0.01);
        playback.NextStepCommand.Execute(null);
        await playback.SettleAsync();
        Assert.Equal(1, scene.Progress?.Path);

        Assert.True(playback.HasDeviation);
        playback.ShowDeviation = true;
        await playback.SettleAsync();
        Assert.NotEmpty(Assert.Single(scene.Points).Positions);
        playback.ToEndCommand.Execute(null);
        await playback.SettleAsync();
        Assert.Null(scene.Progress);

        await page.PostCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.Equal("Wrote 2 G-code files.", main.Message);
        Assert.Equal(2, page.Files.Count);
        Assert.Same(page.Files[0], page.SelectedFile);
        Assert.Contains("lines", page.Files[0].Detail, StringComparison.Ordinal);
        await Wait.UntilAsync(() => page.Preview.Length > 0, "G-code preview");
        Assert.Contains("(Operation: ", page.Preview, StringComparison.Ordinal);
        Assert.True(playback.IsAvailable, "posting reuses the up-to-date simulation");

        var target = Path.Combine(fixture.Root, "saved.nc");
        files.Answers.Enqueue(target);
        await page.SaveFileCommand.ExecuteAsync(page.Files[1]);
        Assert.False(main.MessageIsError, main.Message);
        Assert.Equal(
            await File.ReadAllTextAsync(PartGeometry.Resolve(folder, page.Files[1].File.Path)),
            await File.ReadAllTextAsync(target));
        var export = Directory.CreateDirectory(Path.Combine(fixture.Root, "export")).FullName;
        files.Answers.Enqueue(export);
        await page.ExportFilesCommand.ExecuteAsync(null);
        Assert.Equal(2, Directory.GetFiles(export).Length);

        // An edit makes the simulation stale and drops the playback.
        page.OperationEditor!.RasterAngle.Text = "45";
        await FlushAsync();
        await Wait.UntilAsync(() => page.SimulationIsStale, "simulation stale");
        Assert.False(playback.IsAvailable);
        Assert.Empty(scene.Points);
        Assert.StartsWith("1 of 2 toolpaths up to date", page.ToolpathSummary, StringComparison.Ordinal);

        // Reopened, the toolpaths still in date come from the cache.
        await main.Project.CloseCommand.ExecuteAsync(null);
        Assert.Empty(page.Scene.Scene.Toolpaths);
        await main.Project.OpenFolderAsync(folder);
        await page.ShowAsync();
        Assert.StartsWith("1 of 2 toolpaths up to date", page.ToolpathSummary, StringComparison.Ordinal);
        Assert.Equal(2, page.Files.Count);
        await page.RunAllCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.StartsWith("2 of 2", page.ToolpathSummary, StringComparison.Ordinal);
        Assert.True(page.Playback.IsAvailable);
    }

    [Fact]
    public async Task Run_CancelsAndBlocksOutputOnErrorsUnlessOverridden()
    {
        var page = await OpenAsync();
        await PrepareAsync();
        await page.ShowAsync();
        page.NewStrategy = MachiningViewModel.Strategies.Single(s => s.Value == StrategyType.ZLevelRoughing);
        page.NewTool = page.Tools.Single(t => t.Type == ToolType.FlatEnd);
        await page.AddOperationCommand.ExecuteAsync(null);

        // Cancelled while waiting for an edit in progress.
        var gate = new TaskCompletionSource();
        var holding = main!.Workspace.EditAsync(_ => gate.Task);
        var run = page.RunAllCommand.ExecuteAsync(null);
        Assert.True(page.IsRunning);
        Assert.False(page.GenerateCommand.CanExecute(null));
        Assert.True(page.CancelCommand.CanExecute(null));
        page.CancelCommand.Execute(null);
        gate.SetResult();
        await holding;
        await run;
        Assert.False(page.IsRunning);
        Assert.Equal("Cancelled.", main.Message);
        Assert.Empty(page.Files);

        // A clamp fixed over the whole setup is hit by every move.
        var setup = main.Workspace.Session!.Setups[0];
        var clamp = new SimpleSolid { Name = "Clamp", Shape = SolidShape.Box, BottomZ = -50, Height = 200, SizeX = 300, SizeY = 300 };
        Assert.True(await main.Workspace.EditAsync(s => s.AddKeepOutAsync(new KeepOutZone { Attachment = KeepOutAttachment.SetupFixed, SetupId = setup.Id, Solid = clamp })));
        await page.RunAllCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("error(s)", main.Message, StringComparison.Ordinal);
        Assert.True(page.BlocksOutput);
        Assert.Empty(page.Files);
        var issue = page.Issues.First(i => i.Issue.Kind == IssueKind.KeepOutCollision);
        Assert.True(issue.IsError);
        Assert.Same(issue, page.Issues[0]);
        Assert.Equal(0, issue.Step);

        page.ShowIssueCommand.Execute(issue);
        await page.Playback.SettleAsync();
        Assert.Equal((0, Math.Max(issue.Issue.FirstMove, 0)), page.Scene.Scene.Progress);

        page.OverrideVerification = true;
        await page.PostCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.Single(page.Files);
    }

    [Fact]
    public async Task Run_SplitStockMachinesEachPieceAndShowsThemOneAtATime()
    {
        var page = await OpenAsync();
        await PrepareAsync();
        Assert.True(await main!.Workspace.EditAsync(async s =>
        {
            var b = (await s.MillPartAsync()).Bounds;
            StockTiling.Fit(s.Stock, "Board", 14, 20, 10, (b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z), 2);
            await s.SaveAsync();
        }));
        await AddOperationsAsync(page);
        Assert.True(page.IsTiled);
        Assert.Equal(["Piece 1 (column 1, row 1, layer 1)", "Piece 2 (column 2, row 1, layer 1)"], page.PieceChoices.Select(c => c.Label));
        Assert.Equal(page.PieceChoices[0], page.SelectedPiece);
        Assert.StartsWith("The stock is split into 2 pieces", page.TilingSummary, StringComparison.Ordinal);
        Assert.Empty(page.TilingProblems);

        await page.RunAllCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.StartsWith("Piece 1: 2 of 2 toolpaths up to date", page.ToolpathSummary, StringComparison.Ordinal);
        Assert.Equal(4, page.Files.Count);
        Assert.Equal(["Piece 1, ", "Piece 1, ", "Piece 2, ", "Piece 2, "], page.Files.Select(f => f.Detail[..9]));
        Assert.All(page.Files, f => Assert.Matches(@"^gcode/P0[12]_01_", f.File.Path));
        Assert.True(page.Playback.IsAvailable);
        var firstPaths = page.Scene.Scene.Toolpaths.ToList();

        page.SelectedPiece = page.PieceChoices[1];
        await Wait.UntilAsync(() => page.ToolpathSummary.StartsWith("Piece 2: 2 of 2", StringComparison.Ordinal), "piece 2 shown");
        Assert.NotEqual(firstPaths, page.Scene.Scene.Toolpaths);
        await page.Playback.SettleAsync();
        Assert.True(page.Playback.IsAvailable);

        main.Workspace.Session!.Setups[0].Orientation = SetupOrientation.Indexed;
        await page.GenerateCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("not 3-axis", main.Message, StringComparison.Ordinal);
        Assert.Contains("not 3-axis", page.TilingProblems, StringComparison.Ordinal);
    }

    private async Task<MachiningViewModel> OpenAsync()
    {
        await fixture.Library.SaveToolAsync(DesktopFixture.FlatEndMill());
        await fixture.Library.SaveToolAsync(DesktopFixture.BallEndMill());
        var settings = services.Settings.Clone();
        settings.ComputeBackend = ComputeBackend.Cpu;
        await services.SaveSettingsAsync(settings);
        main = new MainViewModel(services, files);
        await main.InitializeAsync();
        var project = main.Project;
        project.NewLocation = fixture.Root;
        project.NewName = "Block";
        var materialId = await fixture.MaterialIdAsync();
        project.NewMaterial = project.Materials.First(m => m.Id == materialId);
        await project.CreateCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        return main.Machining;
    }

    /// <summary>A 20 x 16 x 6 mm block in stock with a 2 mm margin, and both library tools in the project.</summary>
    private async Task PrepareAsync()
    {
        var stl = fixture.WriteBoxStl("block.stl", Vector3.Zero, new Vector3(20, 16, 6));
        var tools = await fixture.Library.ListToolsAsync();
        Assert.True(await main!.Workspace.EditAsync(async s =>
        {
            await s.ImportPartAsync(stl, LengthUnit.Millimeter);
            PartGeometry.FitBox(s.Stock, (await s.MillPartAsync()).Bounds, 2);
            await s.SaveAsync();
            await s.AddToolsAsync([.. tools.Select(t => t.Id)]);
        }));
    }

    /// <summary>Z-level roughing with the flat end mill, then parallel finishing with the ball end mill.</summary>
    private static async Task AddOperationsAsync(MachiningViewModel page)
    {
        await page.ShowAsync();
        page.NewStrategy = MachiningViewModel.Strategies.Single(s => s.Value == StrategyType.ZLevelRoughing);
        page.NewTool = page.Tools.Single(t => t.Type == ToolType.FlatEnd);
        await page.AddOperationCommand.ExecuteAsync(null);
        page.NewStrategy = MachiningViewModel.Strategies.Single(s => s.Value == StrategyType.ParallelFinishing);
        page.NewTool = page.Tools.Single(t => t.Type == ToolType.BallEnd);
        await page.AddOperationCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Operations.Count);
    }

    /// <summary>Waits for the edits queued so far (auto-saves run in the background).</summary>
    private async Task FlushAsync() => Assert.True(await main!.Workspace.EditAsync(_ => Task.CompletedTask));
}
