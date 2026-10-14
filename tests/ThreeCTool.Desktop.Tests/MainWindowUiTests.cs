using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using ThreeCTool.Desktop.Services;
using ThreeCTool.Desktop.ViewModels;
using ThreeCTool.Desktop.Views;

using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("ThreeCTool.Desktop.Tests.UiTestFramework", "ThreeCTool.Desktop.Tests")]

namespace ThreeCTool.Desktop.Tests;

/// <summary>Avalonia's UI thread belongs to whichever thread touches it first, so the session must start before any test.</summary>
public sealed class UiTestFramework : XunitTestFramework
{
    public UiTestFramework(IMessageSink messageSink)
        : base(messageSink) => UiSession.Start();
}

/// <summary>Shared headless Avalonia session (Skia rendering, so frames are really drawn).</summary>
public static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(UiSession), AvaloniaTestIsolationLevel.PerAssembly));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static Task RunAsync(Func<Task> test) => Session.Value.Dispatch(async () =>
    {
        await test();
        return true;
    }, CancellationToken.None);

    public static void Start() => RunAsync(() => Task.CompletedTask).GetAwaiter().GetResult();
}

public sealed class MainWindowUiTests : IAsyncDisposable
{
    private readonly DesktopFixture fixture = new();

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    [Fact]
    public Task EveryPage_RendersAndBindsToItsViewModel() => UiSession.RunAsync(async () =>
    {
        await fixture.Library.SaveToolAsync(DesktopFixture.FlatEndMill());
        using var services = new AppServices(new AppPaths(fixture.Root));
        var files = new FakeFileDialogs();
        var main = new MainViewModel(services, files);
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            await main.InitializeAsync();
            await FindAsync<ListBox>(window, "Navigation");
            await FindAsync<TextBox>(window, "NewProjectName");
            await FindAsync<ComboBox>(window, "NewProjectMachine");
            await FindAsync<Button>(window, "OpenProject");
            var create = await FindAsync<Button>(window, "CreateProject");
            Capture(window);

            main.Project.NewLocation = fixture.Root;
            main.Project.NewName = "Bracket";
            create.Command!.Execute(null);
            var tabs = await FindAsync<TabControl>(window, "ProjectTabs");
            await FindAsync<Control>(window, "ProjectScene");
            await FindAsync<Button>(window, "CloseProject");
            var projectName = await FindAsync<TextBox>(window, "ProjectName");
            Assert.Equal("Bracket", projectName.Text);
            Assert.Equal("Bracket - 3C Tool", window.Title);
            await FindAsync<ItemsControl>(window, "AvailableTools");
            Capture(window);

            tabs.SelectedIndex = 1;
            var import = await FindAsync<Button>(window, "ImportModel");
            files.Answers.Enqueue(fixture.WriteBoxStl("block.stl", System.Numerics.Vector3.Zero, new System.Numerics.Vector3(20, 10, 5)));
            import.Command!.Execute(null);
            await FindAsync<ListBox>(window, "PartList");
            var partName = await FindAsync<TextBox>(window, "PartName");
            Assert.Equal("block", partName.Text);
            await FindAsync<Button>(window, "PlaceCenter");
            Capture(window);

            tabs.SelectedIndex = 2;
            await FindAsync<ComboBox>(window, "StockShape");
            await FindAsync<Control>(window, "BoxStockFields");
            await FindAsync<Button>(window, "FitStock");
            Capture(window);

            tabs.SelectedIndex = 3;
            await FindAsync<Control>(window, "KeepOutList");
            await FindAsync<Button>(window, "AddKeepOut");
            Capture(window);

            main.Project.Details.AvailableTools[0].IsSelected = true;
            await main.Project.Details.AddToolsCommand.ExecuteAsync(null);
            main.SelectedPage = main.Machining;
            await FindAsync<Control>(window, "MachiningScene");
            await FindAsync<Button>(window, "RunAll");
            var setupName = await FindAsync<TextBox>(window, "SetupName");
            Assert.Equal(main.Workspace.Session!.Setups[0].Name, setupName.Text);
            await FindAsync<ComboBox>(window, "SetupOrientation");
            Capture(window);

            var machiningTabs = await FindAsync<TabControl>(window, "MachiningTabs");
            machiningTabs.SelectedIndex = 1;
            await Wait.UntilAsync(() => main.Machining.NewTool is not null, "tools loaded");
            var addOperation = await FindAsync<Button>(window, "AddOperation");
            addOperation.Command!.Execute(null);
            await FindAsync<ListBox>(window, "OperationList");
            var operationName = await FindAsync<TextBox>(window, "OperationName");
            Assert.Equal(main.Machining.OperationEditor!.Name, operationName.Text);
            await FindAsync<ComboBox>(window, "OperationTool");
            Capture(window);

            machiningTabs.SelectedIndex = 2;
            await FindAsync<ItemsControl>(window, "FeedList");
            machiningTabs.SelectedIndex = 3;
            await FindAsync<TextBlock>(window, "SimulationSummary");
            machiningTabs.SelectedIndex = 4;
            await FindAsync<Button>(window, "ExportGcode");
            Capture(window);
            await main.Workspace.ReplaceAsync(null);

            main.SelectedPage = main.Library;
            await FindAsync<TabControl>(window, "LibraryTabs");

            var machineName = await FindAsync<TextBox>(window, "MachineName");
            Assert.Equal(main.Library.Machines.Editor!.Model.Name, machineName.Text);
            await FindAsync<Button>(window, "ImportConfig");
            await FindAsync<Button>(window, "SaveMachine");
            Capture(window);

            main.Library.SelectedSection = main.Library.Tools;
            var toolName = await FindAsync<TextBox>(window, "ToolName");
            await Wait.UntilAsync(() => toolName.Text == "6 mm flat", "tool editor bound");
            toolName.Text = "Renamed";
            Assert.Equal("Renamed", main.Library.Tools.Editor!.Model.Name);
            Assert.True(main.Library.Tools.Editor.HasChanges);
            var diameter = await FindAsync<ContentControl>(window, "ToolDiameter");
            var diameterText = diameter.GetVisualDescendants().OfType<TextBox>().First();
            Assert.Equal("6", diameterText.Text);
            diameterText.Text = "abc";
            Assert.True(main.Library.Tools.Editor.Diameter.HasError);
            await FindAsync<Button>(window, "LogReplaced");
            Capture(window);

            main.Library.Tools.RevertCommand.Execute(null);
            main.Library.SelectedSection = main.Library.Materials;
            await FindAsync<TextBox>(window, "MaterialName");
            Capture(window);

            main.Library.SelectedSection = main.Library.Learning;
            await FindAsync<ListBox>(window, "Corrections");
            await FindAsync<Button>(window, "ResetAll");
            Capture(window);

            main.SelectedPage = main.Settings;
            await FindAsync<ComboBox>(window, "Backend");
            await FindAsync<ComboBox>(window, "DisplayUnit");
            await FindAsync<Button>(window, "SaveSettings");
            Capture(window);

            var confirm = main.ConfirmAsync("Title", "Message", "Go", danger: true);
            var ok = await FindAsync<Button>(window, "ConfirmOk");
            ok.Command!.Execute(ok.CommandParameter);
            Assert.True(await confirm);
            Capture(window);

            main.ShowMessage("Something failed", isError: true);
            var dismiss = await FindAsync<Button>(window, "DismissMessage");
            dismiss.Command!.Execute(null);
            Assert.False(main.HasMessage);
        }
        finally
        {
            window.Close();
            main.Dispose();
        }
    });

    private static void Capture(Window window)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 0);
    }

    private static async Task<T> FindAsync<T>(Window window, string name)
        where T : Control
    {
        T? found = null;
        await Wait.UntilAsync(() =>
        {
            window.UpdateLayout();
            found = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
            return found is not null;
        }, "control " + name);
        return found!;
    }
}
