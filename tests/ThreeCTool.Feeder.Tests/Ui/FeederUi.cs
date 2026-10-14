using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using ThreeCTool.Feeder.Services;
using ThreeCTool.Feeder.ViewModels;
using ThreeCTool.Feeder.Views;
using ThreeCTool.Rendering;

using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("ThreeCTool.Feeder.Tests.Ui.UiTestFramework", "ThreeCTool.Feeder.Tests")]

namespace ThreeCTool.Feeder.Tests.Ui;

/// <summary>Avalonia's UI thread belongs to whichever thread touches it first, so the session must start before any test.</summary>
public sealed class UiTestFramework : XunitTestFramework
{
    public UiTestFramework(IMessageSink messageSink)
        : base(messageSink) => UiSession.Start();
}

/// <summary>Shared headless Avalonia session (Skia rendering, so frames can be captured).</summary>
public static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(UiSession), AvaloniaTestIsolationLevel.PerAssembly));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs a test on the UI thread.</summary>
    public static Task RunAsync(Func<Task> test) => Session.Value.Dispatch(async () =>
    {
        await test();
        return true;
    }, CancellationToken.None);

    public static void Start() => RunAsync(() => Task.CompletedTask).GetAwaiter().GetResult();
}

/// <summary>The touch UI in an 800x480 headless window, connected to a feeder harness over its Unix socket.</summary>
internal sealed class FeederUi : IDisposable
{
    private FeederUi(MainViewModel viewModel, Window window)
    {
        ViewModel = viewModel;
        Window = window;
    }

    public MainViewModel ViewModel { get; }

    public Window Window { get; }

    public static FeederUi Open(string socketPath, PreviewRenderer renderer = PreviewRenderer.Software)
    {
        var viewModel = new MainViewModel(new FeederConnection(socketPath, TimeSpan.FromMilliseconds(200)), renderer);
        var window = new Window { Width = 800, Height = 480, Content = new MainView { DataContext = viewModel } };
        window.Show();
        viewModel.Start();
        return new FeederUi(viewModel, window);
    }

    public async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + FeederHarness.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                var vm = ViewModel;
                throw new TimeoutException($"UI condition not reached: {what} (machine {vm.MachineState}, run {vm.Run.State}, overlay {vm.Overlay?.GetType().Name ?? "none"}, message '{vm.Message}')");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Waits for a named, visible and enabled control.</summary>
    public async Task<T> FindAsync<T>(string name)
        where T : Control
    {
        T? found = null;
        await WaitAsync(() =>
        {
            Window.UpdateLayout();
            found = Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
            return found is not null;
        }, $"{typeof(T).Name} '{name}' shown");
        return found!;
    }

    /// <summary>Taps a named button with the (headless) mouse, so overlays and disabled states are honoured.</summary>
    public async Task ClickAsync(string name) => Tap(await FindAsync<Button>(name));

    public void Tap(Control control)
    {
        control.BringIntoView();
        Window.UpdateLayout();
        var center = Center(control);
        Window.MouseDown(center, MouseButton.Left);
        Window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public Point Center(Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
        ?? throw new InvalidOperationException($"{control.Name} is not in the window.");

    /// <summary>Renders a frame and counts pixels inside <paramref name="control"/> that differ from its top-left pixel.</summary>
    public int CountDrawnPixels(Control control)
    {
        Window.UpdateLayout();
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        using var buffer = frame.Lock();
        var scale = buffer.Size.Width / Window.Bounds.Width;
        var origin = control.TranslatePoint(default, Window) ?? throw new InvalidOperationException("Not in the window.");
        int Pixel(int x, int y) => Marshal.ReadInt32(buffer.Address, (y * buffer.RowBytes) + (4 * x));

        var x0 = (int)Math.Ceiling(origin.X * scale);
        var y0 = (int)Math.Ceiling(origin.Y * scale);
        var x1 = Math.Min(buffer.Size.Width, (int)((origin.X + control.Bounds.Width) * scale));
        var y1 = Math.Min(buffer.Size.Height, (int)((origin.Y + control.Bounds.Height) * scale));
        var background = Pixel(x0, y0);
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (Pixel(x, y) != background)
                {
                    count++;
                }
            }
        }

        return count;
    }

    public void Dispose()
    {
        Window.Close();
        ViewModel.Dispose();
    }
}
