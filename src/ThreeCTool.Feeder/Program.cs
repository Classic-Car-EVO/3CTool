using Avalonia;

namespace ThreeCTool.Feeder;

internal static class Program
{
    // Don't use Avalonia or anything relying on a SynchronizationContext before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        FeederUiOptions options;
        try
        {
            options = FeederUiOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(FeederUiOptions.Usage);
            return 2;
        }

        App.Options = options;
        var builder = BuildAvaloniaApp();
        return options.Drm
            ? builder.StartLinuxDrm(args, options.DrmCard, options.Scaling)
            : builder.StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
