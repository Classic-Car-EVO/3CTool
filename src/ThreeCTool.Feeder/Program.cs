using System.ComponentModel;

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
        if (!options.Drm)
        {
            return builder.StartWithClassicDesktopLifetime(args);
        }

        try
        {
            return builder.StartLinuxDrm(args, options.DrmCard, options.Scaling);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"Cannot take over the display: {ex.Message} ({ex.NativeErrorCode}).");
            Console.Error.WriteLine(ex.NativeErrorCode == -13
                ? "Another program (usually a desktop session) is using the display. Boot the Pi to the console: sudo systemctl set-default multi-user.target && sudo reboot"
                : "Check the display connection and the --card option.");
            return 1;
        }
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
