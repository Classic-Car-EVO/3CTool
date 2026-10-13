using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using ThreeCTool.Feeder.Services;
using ThreeCTool.Feeder.ViewModels;
using ThreeCTool.Feeder.Views;

namespace ThreeCTool.Feeder;

public sealed partial class App : Application
{
    /// <summary>Set by <c>Main</c> before the app starts.</summary>
    public static FeederUiOptions Options { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Headless test sessions have no lifetime: tests create their own view models.
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
            {
                var viewModel = CreateViewModel();
                desktop.MainWindow = new MainWindow { DataContext = viewModel };
                desktop.Exit += (_, _) => viewModel.Dispose();
                break;
            }

            case ISingleViewApplicationLifetime single:
                single.MainView = new MainView { DataContext = CreateViewModel() };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainViewModel CreateViewModel()
    {
        var viewModel = new MainViewModel(new FeederConnection(Options.SocketPath), Options.Renderer);
        viewModel.Start();
        return viewModel;
    }
}
