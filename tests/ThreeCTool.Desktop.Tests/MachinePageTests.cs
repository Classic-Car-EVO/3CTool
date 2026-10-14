using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using Google.Protobuf.WellKnownTypes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ThreeCTool.Desktop.Services;
using ThreeCTool.Desktop.ViewModels;
using ThreeCTool.Desktop.Views;
using ThreeCTool.FluidNcSimulator;

using FeederTab = ThreeCTool.Feeder.ViewModels.FeederTab;
using MachineTransport = ThreeCTool.Core.Machines.TransportKind;

namespace ThreeCTool.Desktop.Tests;

/// <summary>Shares the (slow) posted CAM project between the pipeline and machine page tests.</summary>
[CollectionDefinition(Name)]
public sealed class CamProjectGroup : ICollectionFixture<CamProjectFixture>
{
    public const string Name = "CAM project";
}

[Collection(CamProjectGroup.Name)]
public sealed class MachinePageTests(CamProjectFixture project) : IAsyncDisposable
{
    private readonly DesktopFixture desktop = new();

    public async ValueTask DisposeAsync() => await desktop.DisposeAsync();

    [Fact]
    public Task DirectConnection_SendsJobsImportsConfigAndSyncs() => UiSession.RunAsync(async () =>
    {
        using var simulator = new Simulator(new SimulatorOptions { TimeScale = 100 });
        await using var server = new TcpSimulatorServer(simulator);

        // The desktop's own library; the built-in default machine has the same id as the project's.
        var machineId = await desktop.DefaultMachineIdAsync();
        Assert.Equal(project.Session.Project.MachineProfileId, machineId);
        var profile = (await desktop.Library.GetMachineAsync(machineId))!;
        profile.Connection.Transport = MachineTransport.Tcp;
        profile.Connection.Host = "127.0.0.1";
        profile.Connection.TcpPort = server.Port;
        await desktop.Library.SaveMachineAsync(profile);

        using var services = new AppServices(new AppPaths(desktop.Root));
        var main = new MainViewModel(services, new FakeFileDialogs());
        var page = main.Machine;
        page.CreateDirectFeeder = directory => new DirectFeeder(
            directory,
            o =>
            {
                o.ReconnectInterval = TimeSpan.FromMilliseconds(100);
                o.StatusInterval = TimeSpan.FromMilliseconds(20);
                o.StatusMinInterval = TimeSpan.FromMilliseconds(10);
                o.StatusHeartbeat = TimeSpan.FromMilliseconds(200);
            },
            s => s.AddLogging(l => l.ClearProviders().SetMinimumLevel(LogLevel.Warning)));
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            await main.InitializeAsync();
            await main.Workspace.ReplaceAsync(await ProjectSession.OpenAsync(project.Session.Folder, desktop.LibraryPath));
            main.SelectedPage = page;
            await FindAsync<ListBox>(window, "Connections");
            await Wait.UntilAsync(() => page.Connections.Count == 1 && page.Files.Count == 2 && page.Machines.Count == 1, "page loaded");
            Assert.True(page.SelectedConnection!.IsDirect);
            Assert.Null(page.LinkedMachine);
            Assert.False(page.ForgetCommand.CanExecute(null));
            Assert.False(page.UploadCommand.CanExecute(null));
            Capture(window);

            page.LinkedMachine = page.Machines[0];
            await Wait.UntilAsync(() => services.Settings.DirectMachineProfileId == machineId, "direct link saved");

            await page.ConnectCommand.ExecuteAsync(null);
            var feeder = page.Feeder!;
            Assert.NotNull(feeder);
            Assert.True(feeder.IsLocal);
            Assert.True(page.IsDirectConnected);
            await FindAsync<TabControl>(window, "Tabs");
            await page.ApplyMachineSettingsCommand.ExecuteAsync(null);
            await Wait.UntilAsync(() => feeder.IsControllerConnected, "controller connected");
            Assert.Equal(server.Port, (await feeder.Admin.GetSettingsAsync(new Empty())).TcpPort);
            Capture(window);

            await page.UploadCommand.ExecuteAsync(null);
            var job = Assert.Single(feeder.Jobs.Jobs);
            Assert.Same(job, feeder.Jobs.Selected);
            Assert.Equal((int)FeederTab.Jobs, feeder.SelectedTab);
            Assert.Equal(100, page.UploadProgress);
            Capture(window);

            var import = page.ImportConfigCommand.ExecuteAsync(null);
            await Wait.AnswerAsync(main, confirm: true);
            await import;
            var config = await feeder.Client.ReadControllerConfigAsync(new Empty());
            Assert.Equal(config.ConfigHash, (await desktop.Library.GetMachineAsync(machineId))!.Controller.ConfigHash);
            Assert.Equal(config.ConfigHash, page.LinkedMachine!.Controller.ConfigHash);

            await page.SyncFeedbackCommand.ExecuteAsync(null);
            Assert.StartsWith("No new run feedback", page.SyncSummary, StringComparison.Ordinal);
            await page.RefreshRunsCommand.ExecuteAsync(null);
            Assert.Empty(page.Runs);

            await page.DisconnectCommand.ExecuteAsync(null);
            Assert.Null(page.Feeder);
            Assert.False(page.IsConnected);
            Capture(window);
        }
        finally
        {
            window.Close();
            await main.Workspace.ReplaceAsync(null);
            main.Dispose();
        }
    });

    private static void Capture(Window window) => Assert.NotNull(window.CaptureRenderedFrame());

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
