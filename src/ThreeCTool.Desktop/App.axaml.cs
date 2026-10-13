using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Desktop.ViewModels;
using ThreeCTool.Desktop.Views;

namespace ThreeCTool.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices(AppPaths.Default);
            var window = new MainWindow();
            var viewModel = new MainViewModel(services, new StorageFileDialogs(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                viewModel.Workspace.Session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                services.Dispose();
            };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}