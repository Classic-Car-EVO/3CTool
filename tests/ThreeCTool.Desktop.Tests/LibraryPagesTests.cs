using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Desktop.ViewModels;

namespace ThreeCTool.Desktop.Tests;

/// <summary>File dialogs that return queued answers (null = cancelled).</summary>
internal sealed class FakeFileDialogs : IFileDialogs
{
    public Queue<string?> Answers { get; } = new();

    public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileType> types) => Next();

    public Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileType> types) => Next();

    public Task<string?> PickFolderAsync(string title) => Next();

    private Task<string?> Next() => Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Not reached: " + what);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Answers the confirmation the view model is showing.</summary>
    public static async Task AnswerAsync(MainViewModel main, bool confirm)
    {
        await UntilAsync(() => main.Overlay is ConfirmViewModel, "confirmation shown");
        var dialog = (ConfirmViewModel)main.Overlay!;
        (confirm ? dialog.ConfirmCommand : dialog.CancelCommand).Execute(null);
    }
}

public sealed class LibraryPagesTests : IAsyncDisposable
{
    internal static readonly string[] FluidNcConfig =
    [
        "name: Test mill",
        "planner_blocks: 32",
        "junction_deviation_mm: 0.02",
        "axes:",
        "  x:",
        "    steps_per_mm: 80.000",
        "    max_rate_mm_per_min: 5000",
        "    acceleration_mm_per_sec2: 250",
        "    max_travel_mm: 1300",
        "    soft_limits: true",
        "    homing:",
        "      cycle: 2",
        "      mpos_mm: 0",
        "  y:",
        "    steps_per_mm: 80",
        "    max_rate_mm_per_min: 5000",
        "    acceleration_mm_per_sec2: 250",
        "    max_travel_mm: 750",
    ];

    private readonly DesktopFixture fixture = new();
    private readonly FakeFileDialogs files = new();
    private readonly AppServices services;

    public LibraryPagesTests()
    {
        services = new AppServices(new AppPaths(fixture.Root));
    }

    private LibraryService Library => fixture.Library;

    public async ValueTask DisposeAsync()
    {
        services.Dispose();
        await fixture.DisposeAsync();
    }

    [Fact]
    public void NumberField_ParsesUnitsSignsAndReportsErrors()
    {
        double value = 6;
        var field = NumberField.Length("Diameter", () => value, v => value = v, LengthUnit.Inch);
        Assert.Equal("0.23622", field.Text);
        Assert.Equal("in", field.UnitText);

        field.Text = "6 mm";
        Assert.Equal(6, value, 9);
        field.Text = "1/4";
        Assert.Equal(6.35, value, 9);
        field.Text = "-0.5";
        Assert.Equal(-12.7, value, 9);
        Assert.Null(field.Error);

        field.Text = "abc";
        Assert.NotNull(field.Error);
        Assert.Equal(-12.7, value, 9);
        field.Refresh();
        Assert.Null(field.Error);
        Assert.Equal("-0.5", field.Text);

        var flutes = 2;
        var whole = NumberField.WholeNumber("Flutes", () => flutes, v => flutes = v);
        whole.Text = "2.5";
        Assert.NotNull(whole.Error);
        whole.Text = "3";
        Assert.Equal(3, flutes);

        double? depth = 1;
        var optional = NumberField.OptionalLength("Depth", () => depth, v => depth = v, LengthUnit.Millimeter);
        optional.Text = " ";
        Assert.Null(depth);
        Assert.Null(optional.Error);

        Assert.True(TableField.TryParse("3=0.02; 6 = 0.04", '=', out var pairs));
        Assert.Equal([(3.0, 0.02), (6.0, 0.04)], pairs);
        Assert.False(TableField.TryParse("3:0.02", '=', out _));
    }

    [Fact]
    public async Task Tools_ValidateSaveRevertAndLogWear()
    {
        var main = await OpenAsync();
        var tools = main.Library.Tools;
        await tools.ShowAsync();
        Assert.Null(tools.Editor);

        await tools.NewCommand.ExecuteAsync(null);
        var editor = Assert.IsType<ToolEditorViewModel>(tools.Editor);
        Assert.True(editor.IsNew);
        editor.Model.Name = "8 mm flat";
        editor.Diameter.Text = "8";
        editor.Stickout.Text = "abc";
        await tools.SaveCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("Stickout", main.Message, StringComparison.Ordinal);
        Assert.Empty(await Library.ListToolsAsync());

        editor.Stickout.Text = "60";
        await tools.SaveCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("overall length", main.Message, StringComparison.Ordinal);

        editor.Stickout.Text = "1 in";
        await tools.SaveCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        var saved = Assert.Single(await Library.ListToolsAsync());
        Assert.Equal(8, saved.Diameter);
        Assert.Equal(25.4, saved.Stickout, 9);
        Assert.Equal(saved.Id, tools.Selected?.Id);
        Assert.False(tools.Editor!.IsNew);
        Assert.False(tools.Editor.HasChanges);

        tools.Editor.Type = ToolType.BullNose;
        Assert.True(tools.Editor.ShowCornerRadius);
        Assert.True(tools.Editor.HasChanges);
        tools.RevertCommand.Execute(null);
        Assert.Equal(ToolType.FlatEnd, tools.Editor!.Type);

        tools.WearNotes = "chipped";
        await tools.LogWearCommand.ExecuteAsync(WearEventKind.Breakage);
        await Wait.UntilAsync(() => tools.WearLog.Count == 1, "wear entry listed");
        Assert.Equal("chipped", tools.WearLog[0].Notes);
        Assert.Equal(ToolStatus.Broken, (await Library.ListToolsAsync()).Single().Status);
        Assert.Equal(ToolStatus.Broken, tools.Editor!.Model.Status);
    }

    [Fact]
    public async Task SwitchingEntries_WithUnsavedChanges_AsksFirst()
    {
        await Library.SaveToolAsync(DesktopFixture.FlatEndMill());
        await Library.SaveToolAsync(DesktopFixture.BallEndMill());
        var main = await OpenAsync();
        var tools = main.Library.Tools;
        await tools.ShowAsync();
        var first = tools.Selected!;
        var second = tools.Items.Single(t => t != first);

        tools.Editor!.Model.Name = "edited";
        tools.Selected = second;
        await Wait.AnswerAsync(main, confirm: false);
        await Wait.UntilAsync(() => main.Overlay is null && tools.Selected == first, "selection kept");
        Assert.Equal("edited", tools.Editor!.Model.Name);

        tools.Selected = second;
        await Wait.AnswerAsync(main, confirm: true);
        await Wait.UntilAsync(() => tools.Editor?.Model.Id == second.Id, "switched");
        Assert.Same(second, tools.Selected);
        Assert.DoesNotContain(await Library.ListToolsAsync(), t => t.Name == "edited");
    }

    [Fact]
    public async Task Machines_ImportFluidNcConfig_SaveAndDetectChanges()
    {
        var path = Path.Combine(fixture.Root, "config.yaml");
        await File.WriteAllLinesAsync(path, FluidNcConfig);
        var main = await OpenAsync();
        var machines = main.Library.Machines;
        await machines.ShowAsync();
        var editor = machines.Editor!;

        files.Answers.Enqueue(path);
        await machines.ImportConfigCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);
        Assert.Equal(["X", "Y"], editor.Axes.Select(a => a.Axis));
        Assert.True(editor.HasChanges);

        editor.AddColletCommand.Execute(null);
        var collet = editor.Collets[^1];
        collet.Shank.Text = "1/8\"";
        collet.Holder.Text = "20x15; 32 x 10";
        await machines.SaveCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);

        var saved = Assert.Single(await Library.ListMachinesAsync());
        Assert.Equal(2, saved.Axes.Count);
        Assert.Equal(1300, saved.GetAxis(AxisName.X)!.MaxTravel);
        Assert.Equal(32, saved.PlannerBlocks);
        Assert.NotNull(saved.Controller.ConfigHash);
        var savedCollet = saved.Collets.Single(c => c.Id == collet.Collet.Id);
        Assert.Equal(3.175, savedCollet.ShankDiameter, 9);
        Assert.Equal([20.0, 32.0], savedCollet.Holder.Select(h => h.Diameter));

        await File.WriteAllLinesAsync(path, FluidNcConfig.Select(l => l.Replace("1300", "1250", StringComparison.Ordinal)));
        files.Answers.Enqueue(path);
        await machines.ImportConfigCommand.ExecuteAsync(null);
        Assert.Contains(machines.Editor!.ImportChanges, c => c.Contains("1250", StringComparison.Ordinal));

        await File.WriteAllLinesAsync(path, ["name: not a machine"]);
        files.Answers.Enqueue(path);
        await machines.ImportConfigCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("No axes", main.Message, StringComparison.Ordinal);

        main.DismissMessageCommand.Execute(null);
        await machines.DeleteCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains("at least one machine", main.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Materials_BuiltInsAreReadOnly_AndCanBeDuplicated()
    {
        var main = await OpenAsync();
        var materials = main.Library.Materials;
        await materials.ShowAsync();
        var builtIn = materials.Items.First(m => m.IsBuiltIn && m.CuttingData.Count > 0);
        materials.Selected = builtIn;
        Assert.True(materials.Editor!.IsReadOnly);
        Assert.False(materials.SaveCommand.CanExecute(null));

        await materials.DeleteCommand.ExecuteAsync(null);
        Assert.True(main.MessageIsError);
        Assert.Contains(await Library.ListMaterialsAsync(), m => m.Id == builtIn.Id);

        await materials.DuplicateCommand.ExecuteAsync(null);
        var copy = materials.Editor!;
        Assert.True(copy.IsNew);
        Assert.False(copy.IsReadOnly);
        copy.CuttingData[0].ChipLoad.Text = "3=0.05; 6=0.08";
        copy.AddCuttingDataCommand.Execute(null);
        await materials.SaveCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);

        var saved = (await Library.ListMaterialsAsync()).Single(m => m.Name == builtIn.Name + " (copy)");
        Assert.False(saved.IsBuiltIn);
        Assert.Equal(builtIn.CuttingData.Count + 1, saved.CuttingData.Count);
        Assert.Contains(saved.CuttingData.SelectMany(d => d.ChipLoad), p => p.ChipLoad == 0.08);
        Assert.Equal(saved.Id, materials.Selected?.Id);
    }

    [Fact]
    public async Task Learning_ListsAndResetsCorrections()
    {
        var tool = DesktopFixture.FlatEndMill();
        await Library.SaveToolAsync(tool);
        var materialId = await fixture.MaterialIdAsync();
        await using (var db = await LibraryDatabase.OpenAsync(fixture.LibraryPath))
        {
            db.FeedCorrections.Add(new FeedCorrection { MaterialId = materialId, ToolId = tool.Id, Strategy = StrategyType.Pencil, ChipLoadFactor = 1.1, SampleCount = 3, UpdatedAt = DateTimeOffset.UtcNow });
            db.JobFeedback.Add(new JobFeedback { ProjectName = "Bowl", RecordedAt = DateTimeOffset.UtcNow, MaterialId = materialId, ToolId = tool.Id, FinishRating = 4 });
            await db.SaveChangesAsync();
        }

        var main = await OpenAsync();
        var learning = main.Library.Learning;
        await learning.ShowAsync();
        var row = Assert.Single(learning.Corrections);
        Assert.Equal(tool.Name, row.Tool);
        Assert.Equal(3, row.Samples);
        Assert.Equal("Bowl", Assert.Single(learning.Feedback).Project);

        learning.SelectedCorrection = row;
        var cancelled = learning.ResetToolCommand.ExecuteAsync(null);
        await Wait.AnswerAsync(main, confirm: false);
        await cancelled;
        Assert.Single(await Library.ListCorrectionsAsync());

        var reset = learning.ResetAllCommand.ExecuteAsync(null);
        await Wait.AnswerAsync(main, confirm: true);
        await reset;
        Assert.Empty(learning.Corrections);
        Assert.Empty(await Library.ListCorrectionsAsync());
    }

    [Fact]
    public async Task Settings_SaveAppliesUnitsAndBackend()
    {
        await Library.SaveToolAsync(DesktopFixture.FlatEndMill());
        var main = await OpenAsync();
        var settings = main.Settings;
        await settings.ShowAsync();
        Assert.NotEmpty(settings.Devices);
        Assert.Equal(AppPaths.DefaultProjectsFolder, settings.ProjectsFolder);

        settings.DisplayUnit = SettingsViewModel.DisplayUnits.Single(u => u.Value == LengthUnit.Inch);
        settings.Backend = SettingsViewModel.Backends.Single(b => b.Value == ComputeBackend.Cpu);
        files.Answers.Enqueue(fixture.Root);
        await settings.BrowseProjectsFolderCommand.ExecuteAsync(null);
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.False(main.MessageIsError, main.Message);

        var stored = await new DesktopSettingsStore(fixture.LibraryPath).LoadAsync();
        Assert.Equal(LengthUnit.Inch, stored.DisplayUnit);
        Assert.Equal(ComputeBackend.Cpu, stored.ComputeBackend);
        Assert.Equal(fixture.Root, stored.ProjectsFolder);
        Assert.Equal(ComputeBackend.Cpu, services.Compute.Preferred);

        await main.Library.Tools.ShowAsync();
        Assert.Equal("0.23622", main.Library.Tools.Editor!.Diameter.Text);
    }

    private async Task<MainViewModel> OpenAsync()
    {
        var main = new MainViewModel(services, files);
        await main.InitializeAsync();
        Assert.False(main.MessageIsError, main.Message);
        return main;
    }
}
