using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using ThreeCTool.Desktop.Services;
using ThreeCTool.Desktop.ViewModels;
using ThreeCTool.Desktop.Views;

namespace ThreeCTool.Desktop.Tests;

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
        var main = new MainViewModel(services, new FakeFileDialogs());
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            await main.InitializeAsync();
            await FindAsync<ListBox>(window, "Navigation");
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
