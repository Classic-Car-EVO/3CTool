using Avalonia;
using Avalonia.OpenGL;
using System;

namespace ThreeCTool.Desktop;

sealed class Program
{
    private static bool s_forceGles;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // --gles: use EGL / OpenGL ES on Linux to exercise the Raspberry Pi shader path on a desktop.
        s_forceGles = args.Contains("--gles");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
        return s_forceGles
            ? builder.With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Egl],
                GlProfiles = [new GlVersion(GlProfileType.OpenGLES, 3, 0)],
            })
            : builder;
    }
}
