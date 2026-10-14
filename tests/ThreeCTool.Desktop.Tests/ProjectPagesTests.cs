using System.Numerics;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Desktop.ViewModels;
using ThreeCTool.Geometry;
using ThreeCTool.Rendering;

namespace ThreeCTool.Desktop.Tests;

public sealed class ProjectPagesTests : IAsyncDisposable
{
    private readonly DesktopFixture fixture = new();
    private readonly FakeFileDialogs files = new();
    private readonly AppServices services;
    private MainViewModel? main;

    public ProjectPagesTests()
    {
        services = new AppServices(new AppPaths(fixture.Root));
    }

    public async ValueTask DisposeAsync()
    {
        if (main is not null)
        {
            await main.Workspace.ReplaceAsync(null);
        }

        services.Dispose();
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task CreateCloseAndReopen_KeepsRecentProjects()
    {
        var main = await OpenAsync();
        var project = main.Project;
        Assert.Same(project, main.SelectedPage);
        Assert.True(project.HasNoProject);
        Assert.NotNull(project.NewMachine);
        Assert.Equal(AppPaths.DefaultProjectsFolder, project.NewLocation);
        Assert.Equal("3C Tool", main.WindowTitle);

        project.NewLocation = fixture.Root;
        project.NewName = "a/b";
        await project.CreateCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.False(project.HasProject);

        var session = await CreateAsync("Bracket");
        var folder = session.Folder;
        Assert.True(project.HasProject);
        Assert.Equal("Opened 'Bracket'.", main.Message);
        Assert.Equal("Bracket - 3C Tool", main.WindowTitle);
        Assert.Equal([folder], services.Settings.RecentProjects);
        Assert.Equal("Bracket", project.Details.Name);
        Assert.Contains(project.Scene.Scene.Meshes, m => m.Layer == SceneLayer.Stock);

        project.Details.Name = " ";
        Assert.NotNull(project.Details.NameError);
        project.Details.Name = "Bracket v2";
        project.Details.Goal = ProjectDetailsViewModel.Goals.Single(g => g.Value == OptimizationGoal.Speed);
        await FlushAsync();
        Assert.Equal("Bracket v2", session.Project.Name);
        Assert.Equal("Bracket v2 - 3C Tool", main.WindowTitle);

        await project.CloseCommand.ExecuteAsync(null);
        Assert.True(project.HasNoProject);
        Assert.Equal("3C Tool", main.WindowTitle);
        var recent = Assert.Single(project.Recent);
        Assert.True(recent.Exists);
        Assert.Equal("Bracket", recent.Name);

        await project.OpenRecentCommand.ExecuteAsync(recent);
        Assert.True(project.HasProject);
        Assert.Equal("Bracket v2", project.Details.Name);
        Assert.Equal(OptimizationGoal.Speed, project.Details.Goal.Value);
        await project.CloseCommand.ExecuteAsync(null);

        files.Answers.Enqueue(folder);
        await project.OpenCommand.ExecuteAsync(null);
        Assert.True(project.HasProject);
        await project.CloseCommand.ExecuteAsync(null);

        var settings = services.Settings.Clone();
        DesktopSettingsStore.AddRecent(settings, Path.Combine(fixture.Root, "gone"));
        await services.SaveSettingsAsync(settings);
        await project.ShowAsync();
        var missing = project.Recent.Single(r => !r.Exists);
        var opening = project.OpenRecentCommand.ExecuteAsync(missing);
        await Wait.AnswerAsync(main, confirm: true);
        await opening;
        Assert.False(project.HasProject);
        Assert.DoesNotContain(project.Recent, r => r.Folder == missing.Folder);
        Assert.Equal([folder], (await new DesktopSettingsStore(fixture.LibraryPath).LoadAsync()).RecentProjects);

        files.Answers.Enqueue(fixture.Root);
        await project.OpenCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.False(project.HasProject);
    }

    [Fact]
    public async Task Parts_ImportTransformUndoAndPlace()
    {
        var main = await OpenAsync();
        var session = await CreateAsync();
        var parts = main.Project.Parts;
        var scene = main.Project.Scene;
        Assert.False(parts.HasSelection);

        files.Answers.Enqueue(fixture.WriteBoxStl("block.stl", Vector3.Zero, new Vector3(20, 10, 5)));
        await parts.ImportCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.Contains("mm was assumed", main.Message, StringComparison.Ordinal);
        var part = Assert.Single(parts.Parts);
        Assert.Same(part, parts.Selected);
        Assert.Equal("20 mm x 10 mm x 5 mm", parts.SizeText);
        Assert.Contains("watertight", parts.RepairText, StringComparison.Ordinal);
        Assert.Contains(scene.Scene.Meshes, m => m.Layer == SceneLayer.Part);

        parts.MoveX.Text = "5";
        await parts.MoveCommand.ExecuteAsync(null);
        Assert.Equal(["Move X 5, Y 0, Z 0 mm"], parts.Transforms);
        Assert.Equal(5f, scene.PartBounds[part.Id].Min.X, 3);

        await parts.QuarterTurnCommand.ExecuteAsync("Z+");
        Assert.Equal(2, parts.Transforms.Count);
        Assert.Equal(10f, scene.PartBounds[part.Id].Size.X, 3);
        Assert.Equal(15f, scene.PartBounds[part.Id].Center.X, 3);

        await parts.UndoCommand.ExecuteAsync(null);
        Assert.Single(parts.Transforms);
        Assert.Equal(20f, scene.PartBounds[part.Id].Size.X, 3);

        parts.ScaleFactor.Text = "2";
        await parts.ScaleCommand.ExecuteAsync(null);
        Assert.Equal("40 mm x 20 mm x 10 mm", parts.SizeText);
        Assert.Equal(15f, scene.PartBounds[part.Id].Center.X, 3);

        parts.MoveX.Text = "abc";
        await parts.MoveCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Equal(2, parts.Transforms.Count);
        Assert.StartsWith("Scale x2 around (15, 5, 2.5)", parts.Transforms[1], StringComparison.Ordinal);
        main.DismissMessageCommand.Execute(null);

        await parts.PlaceCommand.ExecuteAsync(StockAnchor.Top);
        var stock = session.Stock;
        Assert.Equal(stock.OriginZ + stock.SizeZ, scene.PartBounds[part.Id].Max.Z, 3);

        var clearing = parts.ClearTransformsCommand.ExecuteAsync(null);
        await Wait.AnswerAsync(main, confirm: true);
        await clearing;
        Assert.Empty(parts.Transforms);
        Assert.Equal(0f, scene.PartBounds[part.Id].Min.X, 3);

        parts.SelectedUnit = PartsViewModel.Units.Single(u => u.Value == LengthUnit.Inch);
        await Wait.UntilAsync(() => scene.PartBounds[part.Id].Size.X > 500, "inch rescale");
        Assert.Equal(LengthUnit.Inch, part.ImportUnit);
        parts.SelectedName = "Block";
        await FlushAsync();
        Assert.Equal("Block", session.Parts[0].Name);

        await parts.SuggestCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.NotEmpty(parts.Suggestions);
        await parts.ApplySuggestionCommand.ExecuteAsync(parts.Suggestions[0]);
        Assert.False(main.MessageIsError, main.Message);

        var removing = parts.RemoveCommand.ExecuteAsync(null);
        await Wait.AnswerAsync(main, confirm: true);
        await removing;
        Assert.Empty(parts.Parts);
        Assert.Empty(session.Parts);
        Assert.False(parts.HasSelection);
        Assert.DoesNotContain(scene.Scene.Meshes, m => m.Layer == SceneLayer.Part);
    }

    [Fact]
    public async Task Stock_FitsSwitchesShapeAndPersists()
    {
        var main = await OpenAsync();
        var session = await CreateAsync();
        var folder = session.Folder;
        var stockPage = main.Project.Stock;
        Assert.True(stockPage.IsBox);

        await stockPage.FitToPartCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("Import a model", main.Message, StringComparison.Ordinal);

        files.Answers.Enqueue(fixture.WriteBoxStl("block.stl", Vector3.Zero, new Vector3(20, 10, 6)));
        await main.Project.Parts.ImportCommand.ExecuteAsync(null);
        stockPage.Margin!.Text = "2";
        await stockPage.FitToPartCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.Equal(24, session.Stock.SizeX, 3);
        Assert.Equal(-2, session.Stock.OriginZ, 3);
        Assert.Equal("24", stockPage.BoxFields[3].Text);

        stockPage.Type = StockViewModel.Types.Single(t => t.Value == StockType.Cylinder);
        await Wait.UntilAsync(() => stockPage.IsCylinder, "cylinder shown");
        await FlushAsync();
        Assert.Equal(StockType.Cylinder, session.Stock.Type);
        Assert.Equal(5, session.Stock.OriginY, 3);
        Assert.Equal(3, session.Stock.OriginZ, 3);

        await stockPage.FitToPartCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        var radius = Math.Sqrt((5 * 5) + (3 * 3));
        Assert.Equal(2 * (radius + 2), session.Stock.Diameter, 2);
        Assert.Equal(3, session.Stock.OriginZ, 2);

        stockPage.CylinderFields[3].Text = "0";
        Assert.NotNull(stockPage.CylinderFields[3].Error);
        stockPage.CylinderFields[3].Text = "30";
        await FlushAsync();
        Assert.Equal(30, session.Stock.Length, 9);

        stockPage.Type = StockViewModel.Types.Single(t => t.Value == StockType.Mesh);
        await Wait.UntilAsync(() => stockPage.Type.Value == StockType.Cylinder, "cancelled mesh choice reverted");
        Assert.Equal(StockType.Cylinder, session.Stock.Type);

        files.Answers.Enqueue(fixture.WriteBoxStl("blank.stl", new Vector3(-5), new Vector3(30)));
        await stockPage.LoadMeshCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.True(stockPage.IsMesh);
        Assert.Contains(main.Project.Scene.Scene.Meshes, m => m.Layer == SceneLayer.Stock);

        stockPage.Type = StockViewModel.Types.Single(t => t.Value == StockType.Box);
        await Wait.UntilAsync(() => stockPage.IsBox, "box shown");
        stockPage.BoxFields[4].Text = "50";
        await main.Project.CloseCommand.ExecuteAsync(null);

        await main.Project.OpenFolderAsync(folder);
        Assert.Equal(StockType.Box, main.Workspace.Session!.Stock.Type);
        Assert.Equal(50, main.Workspace.Session.Stock.SizeY, 9);
        Assert.Equal("50", stockPage.BoxFields[4].Text);
    }

    [Fact]
    public async Task Stock_StandardSizeSplitsIntoPiecesWithEditableSeams()
    {
        var library = services.Library;
        var otherMaterial = (await library.ListMaterialsAsync())[0].Id;
        await library.SaveStockSizeAsync(new Core.Materials.StockSize { Name = "Board", SizeX = 14, SizeY = 20, SizeZ = 5 });
        await library.SaveStockSizeAsync(new Core.Materials.StockSize { Name = "Other", MaterialId = otherMaterial, SizeX = 50, SizeY = 50, SizeZ = 5 });
        var main = await OpenAsync();
        var session = await CreateAsync();
        var folder = session.Folder;
        var stockPage = main.Project.Stock;
        Assert.Null(session.Project.MaterialId);
        Assert.Equal([StockViewModel.FreeSizeLabel, "Board (14 × 20 × 5 mm)"], stockPage.Sizes.Select(s => s.Label));
        Assert.Equal(stockPage.Sizes[0], stockPage.Size);
        Assert.False(stockPage.HasPieceSize);

        files.Answers.Enqueue(fixture.WriteBoxStl("block.stl", Vector3.Zero, new Vector3(20, 16, 6)));
        await main.Project.Parts.ImportCommand.ExecuteAsync(null);
        stockPage.Margin!.Text = "2";
        stockPage.Size = stockPage.Sizes[1];
        await Wait.UntilAsync(() => stockPage.HasPieceSize, "pieces shown");
        await FlushAsync();
        var stock = session.Stock;
        Assert.Equal((28.0, 20.0, 10.0), (stock.SizeX, stock.SizeY, stock.SizeZ));
        Assert.Equal((-4.0, -2.0, -2.0), (stock.OriginX, stock.OriginY, stock.OriginZ));
        Assert.StartsWith("4 pieces of Board: 2 along X × 1 along Y × 2 layers", stockPage.PiecesText, StringComparison.Ordinal);
        Assert.Equal(4, stockPage.PieceList.Count);
        Assert.Equal(["14", string.Empty, "5"], stockPage.SeamFields.Select(f => f.Text));
        Assert.Equal(4, stockPage.Layout!.Pieces.Count);
        Assert.NotNull(stockPage.Layout.Part);
        Assert.Null(stockPage.TilingProblem);

        var seamsX = stockPage.SeamFields[0];
        seamsX.Text = "20";
        Assert.True(seamsX.HasError);
        seamsX.Text = "a";
        Assert.True(seamsX.HasError);
        Assert.Equal([14.0], stock.SeamsX);

        stockPage.BoxFields[3].Text = "30";
        await Wait.UntilAsync(() => session.Stock.SeamsX.Count == 2, "seams re-spaced");
        await FlushAsync();
        Assert.Equal([10.0, 20.0], session.Stock.SeamsX);
        Assert.StartsWith("6 pieces", stockPage.PiecesText, StringComparison.Ordinal);
        Assert.False(seamsX.HasError);

        seamsX.Text = "12; 22";
        Assert.False(seamsX.HasError);
        await FlushAsync();
        Assert.Equal([12.0, 22.0], session.Stock.SeamsX);

        await stockPage.MoveSeamCommand.ExecuteAsync(new SeamMove(StockAxis.X, 1, 29));
        Assert.Equal([12.0, 26.0], session.Stock.SeamsX);
        Assert.Equal("12; 26", seamsX.Text);
        await stockPage.EvenSeamsCommand.ExecuteAsync(null);
        Assert.Equal([10.0, 20.0], session.Stock.SeamsX);
        Assert.Equal([5.0], session.Stock.SeamsZ);

        await main.Project.CloseCommand.ExecuteAsync(null);
        await main.Project.OpenFolderAsync(folder);
        Assert.True(stockPage.HasPieceSize);
        Assert.Equal("Board (14 × 20 × 5 mm)", stockPage.Size.Label);
        Assert.Equal("10; 20", stockPage.SeamFields[0].Text);

        stockPage.Size = stockPage.Sizes[0];
        await Wait.UntilAsync(() => !stockPage.HasPieceSize, "pieces hidden");
        await FlushAsync();
        session = main.Workspace.Session!;
        Assert.Null(session.Stock.PieceName);
        Assert.Empty(session.Stock.SeamsX);
        Assert.Equal(30, session.Stock.SizeX, 9);
    }

    [Fact]
    public void Stock_SeamTextParsesUnitsAndSeparators()
    {
        Assert.True(SeamField.TryParse("600; 1200 ;\n1 in", LengthUnit.Millimeter, out var seams));
        Assert.Equal([600.0, 1200.0, 25.4], seams);
        Assert.True(SeamField.TryParse(" ", LengthUnit.Millimeter, out seams));
        Assert.Empty(seams);
        Assert.False(SeamField.TryParse("600, 1200", LengthUnit.Millimeter, out _));
        Assert.Equal("1; 2", SeamField.Format([25.4, 50.8], LengthUnit.Inch));
    }

    [Fact]
    public void Stock_ChangeTypeKeepsTheStockInPlace()
    {
        var stock = new Stock { Type = StockType.Box, OriginX = 1, OriginY = 2, OriginZ = 3, SizeX = 40, SizeY = 20, SizeZ = 10 };
        StockViewModel.ChangeType(stock, StockType.Cylinder);
        Assert.Equal((40.0, 20.0, 12.0, 8.0), (stock.Length, stock.Diameter, stock.OriginY, stock.OriginZ));
        StockViewModel.ChangeType(stock, StockType.Box);
        Assert.Equal((2.0, 3.0), (stock.OriginY, stock.OriginZ));
        Assert.Equal(StockType.Box, stock.Type);
    }

    [Fact]
    public async Task Details_ToolsMaterialToleranceAndLibraryChanges()
    {
        var tool = DesktopFixture.FlatEndMill();
        await fixture.Library.SaveToolAsync(tool);
        var main = await OpenAsync();
        var materials = main.Project.Materials;
        var first = materials[0];
        var other = materials.First(m => m.ToleranceScale != first.ToleranceScale);
        main.Project.NewMaterial = first;
        var session = await CreateAsync();
        var folder = session.Folder;
        var details = main.Project.Details;
        Assert.Equal(first.Id, details.Material?.Id);
        Assert.False(details.HasLibraryChanges);

        await details.AddToolsCommand.ExecuteAsync(null);
        Assert.Contains("Tick", main.Message, StringComparison.Ordinal);
        details.AvailableTools.Single(t => t.Tool.Id == tool.Id).IsSelected = true;
        await details.AddToolsCommand.ExecuteAsync(null);
        Assert.Equal(tool.Id, Assert.Single(details.ProjectTools).Id);
        Assert.DoesNotContain(details.AvailableTools, t => t.Tool.Id == tool.Id);
        Assert.NotNull(session.FindTool(tool.Id));

        await details.RemoveToolCommand.ExecuteAsync(details.ProjectTools[0]);
        Assert.Empty(details.ProjectTools);
        Assert.Null(session.FindTool(tool.Id));

        details.RescaleTolerances = true;
        details.Material = details.Materials.Single(m => m.Id == other.Id);
        await Wait.UntilAsync(() => details.SnapshotSummary.Contains(other.Name, StringComparison.Ordinal), "material changed");
        await FlushAsync();
        Assert.Equal(other.Id, session.Project.MaterialId);
        var expected = Tolerances.ForScale(other.ToleranceScale);
        Assert.Equal(expected.FinishingTolerance, session.Project.Tolerances.FinishingTolerance, 9);
        Assert.Equal(NumberField.FormatLength(expected.FinishingTolerance, LengthUnit.Millimeter), details.Tolerances[1].Text);

        details.Tolerances[1].Text = "0";
        Assert.Equal("Finishing tolerance must be greater than zero.", details.Tolerances[1].Error);
        details.Tolerances[1].Text = "0.05";
        await FlushAsync();
        await main.Project.CloseCommand.ExecuteAsync(null);

        var machine = (await fixture.Library.GetMachineAsync(await fixture.DefaultMachineIdAsync()))!;
        machine.SafeZMachine = -9;
        await fixture.Library.SaveMachineAsync(machine);
        await main.Project.OpenFolderAsync(folder);
        Assert.Contains("1 library item(s) changed", main.Message, StringComparison.Ordinal);
        session = main.Workspace.Session!;
        Assert.Equal(0.05, session.Project.Tolerances.FinishingTolerance, 9);
        Assert.True(details.HasLibraryChanges);
        var row = Assert.Single(details.LibraryChanges);
        Assert.True(row.CanRefresh);
        Assert.StartsWith("Machine:", row.Text, StringComparison.Ordinal);

        await details.RefreshAllSnapshotsCommand.ExecuteAsync(null);
        Assert.Empty(details.LibraryChanges);
        Assert.False(details.HasLibraryChanges);
        Assert.Equal(-9, session.Machine.SafeZMachine, 9);
    }

    [Fact]
    public async Task KeepOuts_ValidateAddAndRemove()
    {
        var main = await OpenAsync();
        var session = await CreateAsync();
        var keepOuts = main.Project.KeepOuts;
        Assert.True(keepOuts.NeedsSetup);
        Assert.Equal(session.Setups[0].Id, keepOuts.Setup?.Id);

        keepOuts.PositionFields[3].Text = "0";
        await keepOuts.AddCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Empty(session.KeepOuts);
        main.DismissMessageCommand.Execute(null);

        keepOuts.Name = "Vise jaw";
        keepOuts.PositionFields[3].Text = "15";
        keepOuts.BoxFields[0].Text = "80";
        await keepOuts.AddCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        var row = Assert.Single(keepOuts.Zones);
        Assert.Equal("Vise jaw", row.Title);
        Assert.Contains("box 80 x 20 x 15 mm", row.Detail, StringComparison.Ordinal);
        Assert.Equal(KeepOutAttachment.SetupFixed, row.Zone.Attachment);
        Assert.Equal(session.Setups[0].Id, row.Zone.SetupId);
        Assert.Contains(main.Project.Scene.Scene.Meshes, m => m.Layer == SceneLayer.KeepOut);

        keepOuts.Attachment = KeepOutsViewModel.Attachments.Single(a => a.Value == KeepOutAttachment.Machine);
        keepOuts.Shape = KeepOutsViewModel.Shapes.Single(s => s.Value == SolidShape.Cylinder);
        Assert.False(keepOuts.NeedsSetup);
        Assert.False(keepOuts.IsBox);
        await keepOuts.AddCommand.ExecuteAsync(null);
        Assert.Equal(2, keepOuts.Zones.Count);
        var clamp = keepOuts.Zones[1];
        Assert.Equal("Keep-out 2", clamp.Title);
        Assert.Null(clamp.Zone.SetupId);
        Assert.Equal(SolidShape.Cylinder, clamp.Zone.Solid.Shape);

        var removing = keepOuts.RemoveCommand.ExecuteAsync(row);
        await Wait.AnswerAsync(main, confirm: true);
        await removing;
        Assert.Equal(clamp.Zone.Id, Assert.Single(session.KeepOuts).Id);

        main.Project.Scene.ShowKeepOuts = false;
        Assert.False(main.Project.Scene.Scene.IsVisible(SceneLayer.KeepOut));
    }

    private async Task<MainViewModel> OpenAsync()
    {
        main = new MainViewModel(services, files);
        await main.InitializeAsync();
        Assert.False(main.MessageIsError, main.Message);
        return main;
    }

    private async Task<ProjectSession> CreateAsync(string name = "Bracket")
    {
        var project = main!.Project;
        project.NewLocation = fixture.Root;
        project.NewName = name;
        await project.CreateCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        return main.Workspace.Session!;
    }

    /// <summary>Waits for the edits queued so far (auto-saves run in the background).</summary>
    private async Task FlushAsync() => Assert.True(await main!.Workspace.EditAsync(_ => Task.CompletedTask));
}
