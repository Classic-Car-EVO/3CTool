using System.Globalization;
using System.Runtime.InteropServices;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core.Tooling;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Rendering;
using ThreeCTool.Simulation;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>
/// Playback of the last simulation: a time slider over the whole job that rebuilds the in-process stock, the tool
/// position and the done part of the tool paths; or the deviation heatmap of the finished stock.
/// </summary>
public sealed partial class PlaybackViewModel : ObservableObject, IDisposable
{
    /// <summary>Step jumps land this far (seconds) past the step start: the boundary itself belongs to the previous step.</summary>
    private const double StepNudge = 1e-4;

    private readonly IShell shell;
    private readonly CamScene scene;
    private DispatcherTimer? timer;
    private CamPipeline? pipeline;
    private SimulationResult? result;
    private IReadOnlyList<GeneratedToolpath> steps = [];
    private IReadOnlyList<Tool> tools = [];
    private double[] starts = [];
    private double tolerance;
    private SimulationSession? playback;
    private CancellationTokenSource? running;
    private bool seeking;
    private bool disposePending;
    private int version;

    public PlaybackViewModel(IShell shell, CamScene scene)
    {
        this.shell = shell;
        this.scene = scene;
    }

    public static IReadOnlyList<Choice<double>> Speeds { get; } =
    [
        new("1x", 1),
        new("10x", 10),
        new("50x", 50),
        new("200x", 200),
        new("1000x", 1000),
    ];

    public static IReadOnlyList<Choice<(int Width, int Height)>> VideoSizes { get; } =
    [
        new("1280 × 720", (1280, 720)),
        new("1920 × 1080", (1920, 1080)),
        new("3840 × 2160", (3840, 2160)),
    ];

    /// <summary>Video length for the whole job (the finished part is held for another second).</summary>
    public static IReadOnlyList<Choice<double>> VideoLengths { get; } =
    [
        new("15 s", 15),
        new("30 s", 30),
        new("1 min", 60),
        new("2 min", 120),
        new("5 min", 300),
    ];

    public static IReadOnlyList<Choice<int>> FrameRates { get; } =
    [
        new("24 fps", 24),
        new("30 fps", 30),
        new("60 fps", 60),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviation))]
    public partial bool IsAvailable { get; private set; }

    [ObservableProperty]
    public partial double Duration { get; private set; }

    [ObservableProperty]
    public partial double Time { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial Choice<double> Speed { get; set; } = Speeds[2];

    /// <summary>Shows the deviation heatmap of the finished stock instead of the in-process stock.</summary>
    [ObservableProperty]
    public partial bool ShowDeviation { get; set; }

    public bool HasDeviation => IsAvailable && result?.Report.Deviation is { Samples.Length: > 0 };

    [ObservableProperty]
    public partial Choice<(int Width, int Height)> VideoSize { get; set; } = VideoSizes[1];

    [ObservableProperty]
    public partial Choice<double> VideoLength { get; set; } = VideoLengths[2];

    [ObservableProperty]
    public partial Choice<int> FrameRate { get; set; } = FrameRates[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanControl))]
    public partial bool IsExporting { get; private set; }

    /// <summary>The playback controls work (no video export running).</summary>
    public bool CanControl => !IsExporting;

    /// <summary>Video export progress, 0..100.</summary>
    [ObservableProperty]
    public partial double ExportProgress { get; private set; }

    /// <summary>The stock and tool currently shown match <see cref="Time"/>.</summary>
    public bool IsSettled => !seeking;

    /// <summary>Shows a new simulation at its first move.</summary>
    /// <param name="toolpaths">Tool paths of the simulation steps, in step order.</param>
    public async Task LoadAsync(CamPipeline owner, SimulationResult simulation, IReadOnlyList<GeneratedToolpath> toolpaths, IReadOnlyList<Tool> projectTools, double finishingTolerance)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        Clear();
        pipeline = owner;
        result = simulation;
        steps = toolpaths;
        tools = projectTools;
        tolerance = finishingTolerance;
        var report = simulation.Report;
        // Summed like VerificationReport.Locate, so step starts match its boundaries exactly.
        var start = 0.0;
        starts = [.. report.Steps.Select(s =>
        {
            var begin = start;
            start += s.Duration;
            return begin;
        })];
        Duration = report.CycleTime;
        IsAvailable = true;
        OnPropertyChanged(nameof(HasDeviation));
        Time = 0;
        await ShowAsync();
    }

    /// <summary>Forgets the simulation (it changed or is about to be disposed) and clears the scene.</summary>
    public void Clear()
    {
        Pause();
        version++;
        running?.Cancel();
        DisposePlayback();
        pipeline = null;
        result = null;
        steps = [];
        starts = [];
        IsAvailable = false;
        Duration = 0;
        Time = 0;
        PositionText = string.Empty;
        scene.SetPoints([]);
        scene.Tool = null;
        scene.Progress = null;
    }

    /// <summary>Moves to the start of move <paramref name="move"/> of step <paramref name="step"/>.</summary>
    public void SeekTo(int step, int move)
    {
        if (result is not { } r || step < 0 || step >= r.Report.Steps.Count)
        {
            return;
        }

        Pause();
        var times = r.Report.Steps[step].MoveEndTimes;
        if (times.Length == 0)
        {
            Time = starts[step];
            return;
        }

        // Inside the move, not on its end: the rounded sum of start and end time can land in the next move.
        move = Math.Clamp(move, 0, times.Length - 1);
        var begin = move == 0 ? 0 : times[move - 1];
        Time = starts[step] + Math.Min(begin + StepNudge, (begin + times[move]) / 2);
    }

    /// <summary>Waits until the shown state matches <see cref="Time"/> (tests).</summary>
    public async Task SettleAsync()
    {
        while (seeking)
        {
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        timer?.Stop();
        version++;
        running?.Cancel();
        DisposePlayback();
    }

    /// <summary>
    /// Records the whole job with the current camera into an MP4 file; false when it failed (the error was shown) or
    /// was cancelled.
    /// </summary>
    public async Task<bool> ExportVideoAsync(string path)
    {
        if (!IsAvailable || IsExporting)
        {
            return false;
        }

        Pause();
        ShowDeviation = false;
        await SettleAsync();
        if (pipeline is not { } owner || result is not { } simulation || seeking)
        {
            return false;
        }

        var (width, height) = VideoSize.Value;
        var fps = FrameRate.Value;
        var frames = Math.Max(2, (int)Math.Round(VideoLength.Value * fps));
        var report = simulation.Report;
        var frame = scene.Clone();
        var started = version;
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        running = cts;
        seeking = true;
        OnPropertyChanged(nameof(IsSettled));
        IsExporting = true;
        ExportProgress = 0;
        var ok = false;
        try
        {
            ok = await shell.RunAsync(async () =>
            {
                await using var writer = await VideoWriter.StartAsync(path, width, height, fps, token);
                var rasterizer = new SoftwareRasterizer();
                var pixels = new byte[writer.FrameBytes];
                for (var k = 0; k < frames; k++)
                {
                    var time = report.CycleTime * k / (frames - 1);
                    var (step, move) = Locate(report, time);
                    PositionText = Describe(report, step, move, time);
                    ShowTool(scene, report, step, move);
                    ShowTool(frame, report, step, move);
                    var stock = await StockPointsAsync(owner, simulation, step, move, token);
                    token.ThrowIfCancellationRequested();
                    scene.SetPoints([stock]);
                    frame.SetPoints([stock]);
                    await Task.Run(
                        () =>
                        {
                            rasterizer.Render(frame, width, height);
                            MemoryMarshal.AsBytes(rasterizer.Pixels).CopyTo(pixels);
                        },
                        token);

                    // The finished part stays on screen for another second.
                    for (var copies = k == frames - 1 ? 1 + fps : 1; copies > 0; copies--)
                    {
                        await writer.WriteFrameAsync(pixels, token);
                    }

                    ExportProgress = 100.0 * (k + 1) / frames;
                }

                await writer.FinishAsync(token);
            });
        }
        finally
        {
            running = null;
            seeking = false;
            IsExporting = false;
            ExportProgress = 0;
            OnPropertyChanged(nameof(IsSettled));
            if (disposePending)
            {
                DisposePlayback();
            }

            if (IsAvailable)
            {
                // Back to the time shown before (or to a simulation loaded meanwhile).
                _ = ShowAsync();
            }
        }

        if (ok)
        {
            shell.ShowMessage($"Saved the video {Path.GetFileName(path)}.");
        }
        else if (token.IsCancellationRequested && started == version)
        {
            shell.ShowMessage("Video export cancelled.");
        }

        return ok;
    }

    [RelayCommand]
    private async Task ExportVideo()
    {
        if (!IsAvailable || IsExporting)
        {
            return;
        }

        if (VideoWriter.FindFfmpeg() is null)
        {
            shell.ShowMessage(VideoWriter.NotFoundMessage, isError: true);
            return;
        }

        if (await shell.Files.SaveFileAsync("Export the simulation as a video", "simulation.mp4", [new FileType("MP4 video", ["*.mp4"])]) is { } path)
        {
            await ExportVideoAsync(path);
        }
    }

    [RelayCommand]
    private void CancelExport()
    {
        if (IsExporting)
        {
            running?.Cancel();
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }

        if (!IsAvailable || IsExporting)
        {
            return;
        }

        if (Time >= Duration)
        {
            Time = 0;
        }

        ShowDeviation = false;
        IsPlaying = true;
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => Advance(timer.Interval.TotalSeconds);
        }

        timer.Start();
    }

    [RelayCommand]
    private void ToStart()
    {
        Pause();
        Time = 0;
    }

    [RelayCommand]
    private void ToEnd()
    {
        Pause();
        Time = Duration;
    }

    [RelayCommand]
    private void PreviousStep()
    {
        Pause();
        var current = starts.Where(s => s < Time - (2 * StepNudge)).DefaultIfEmpty(0).Max();
        Time = Math.Min(current + StepNudge, Duration);
    }

    [RelayCommand]
    private void NextStep()
    {
        Pause();
        var next = starts.Where(s => s > Time + StepNudge).DefaultIfEmpty(Duration).Min();
        Time = next >= Duration ? Duration : next + StepNudge;
    }

    private void Pause()
    {
        timer?.Stop();
        IsPlaying = false;
    }

    private void Advance(double seconds)
    {
        var next = Math.Min(Time + (seconds * Speed.Value), Duration);
        Time = next;
        if (next >= Duration)
        {
            Pause();
        }
    }

    partial void OnTimeChanged(double value)
    {
        if (IsAvailable && !IsExporting)
        {
            _ = ShowAsync();
        }
    }

    partial void OnShowDeviationChanged(bool value)
    {
        if (IsAvailable && !IsExporting)
        {
            _ = ShowAsync();
        }
    }

    /// <summary>Brings the scene to <see cref="Time"/>; while a seek runs, later requests only update the target.</summary>
    private async Task ShowAsync()
    {
        if (seeking)
        {
            return;
        }

        seeking = true;
        OnPropertyChanged(nameof(IsSettled));
        using var cts = new CancellationTokenSource();
        running = cts;
        var token = cts.Token;
        var started = version;
        try
        {
            double? shown = null;
            bool? deviation = null;
            await shell.RunAsync(async () =>
            {
                while (started == version && (shown != Time || deviation != ShowDeviation))
                {
                    (shown, deviation) = (Time, ShowDeviation);
                    await ShowAtAsync(shown.Value, deviation.Value, token);
                }
            });
        }
        finally
        {
            running = null;
            seeking = false;
            OnPropertyChanged(nameof(IsSettled));
            if (disposePending)
            {
                DisposePlayback();
            }

            if (started != version && IsAvailable)
            {
                // A new simulation was loaded while the old one was still being shown.
                _ = ShowAsync();
            }
        }
    }

    private async Task ShowAtAsync(double time, bool deviation, CancellationToken token)
    {
        if (pipeline is not { } owner || result is not { } simulation)
        {
            return;
        }

        var report = simulation.Report;
        var (step, move) = Locate(report, time);
        PositionText = Describe(report, step, move, time);
        ShowTool(scene, report, step, move);
        if (deviation && report.Deviation is { Samples.Length: > 0 } d)
        {
            var range = Math.Max(Math.Max(d.MaxLeftover, d.MaxGouge), 4 * tolerance);
            scene.SetPoints([SceneBuilder.DeviationPoints(d.Samples, tolerance, range, report.Resolution)]);
            return;
        }

        var stock = await StockPointsAsync(owner, simulation, step, move, token);
        token.ThrowIfCancellationRequested();
        scene.SetPoints([stock]);
    }

    /// <summary>Step and move at a time; the step count at the end of the cycle (the finished stock).</summary>
    private static (int Step, int Move) Locate(VerificationReport report, double time) =>
        (time < report.CycleTime ? report.Locate(time) : null) ?? (report.Steps.Count, 0);

    private void ShowTool(CamScene target, VerificationReport report, int step, int move)
    {
        if (step >= report.Steps.Count)
        {
            target.Progress = null;
            target.Tool = null;
            return;
        }

        target.Progress = (step, move);
        target.Tool = step < steps.Count && tools.FirstOrDefault(t => t.Id == steps[step].Toolpath.ToolId) is { } tool
            && steps[step].Toolpath.Moves is { Count: > 0 } moves
            ? SceneBuilder.ToolMarker(steps[step].Toolpath.Frame, moves[Math.Min(move, moves.Count - 1)], tool)
            : null;
    }

    /// <summary>The in-process stock after a move (the finished stock past the last step).</summary>
    private async Task<ScenePoints> StockPointsAsync(CamPipeline owner, SimulationResult simulation, int step, int move, CancellationToken token)
    {
        var end = step >= simulation.Report.Steps.Count;
        if (end && playback is null)
        {
            // The finished stock is the simulation's own; no playback needed.
            var final = await owner.SurfacePointsAsync(simulation.Stock, token);
            return SceneBuilder.StockPoints(final, simulation.Report.Resolution);
        }

        if (playback is null)
        {
            var created = await owner.CreatePlaybackAsync(token);
            if (token.IsCancellationRequested)
            {
                created.Dispose();
                token.ThrowIfCancellationRequested();
            }

            playback = created;
        }

        await owner.SeekAsync(playback, step, end ? 0 : move, token);
        var points = await owner.SurfacePointsAsync(playback.Stock, token);
        return SceneBuilder.StockPoints(points, playback.Resolution);
    }

    private static string Describe(VerificationReport report, int step, int move, double time)
    {
        var clock = $"{Clock(time)} / {Clock(report.CycleTime)}";
        if (step >= report.Steps.Count)
        {
            return $"Finished, {clock}";
        }

        var s = report.Steps[step];
        return string.Create(CultureInfo.InvariantCulture, $"{s.Name} ({step + 1}/{report.Steps.Count}), move {move + 1}/{s.MoveEndTimes.Length}, {clock}");
    }

    internal static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void DisposePlayback()
    {
        if (seeking)
        {
            disposePending = true;
            return;
        }

        disposePending = false;
        playback?.Dispose();
        playback = null;
    }
}
