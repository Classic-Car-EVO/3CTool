using System.Globalization;

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

    /// <summary>The stock and tool currently shown match <see cref="Time"/>.</summary>
    public bool IsSettled => !seeking;

    /// <summary>Shows a new simulation at its end state.</summary>
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
        Time = Duration;
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

    [RelayCommand]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }

        if (!IsAvailable)
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
        if (IsAvailable)
        {
            _ = ShowAsync();
        }
    }

    partial void OnShowDeviationChanged(bool value)
    {
        if (IsAvailable)
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
        // At the end of the cycle the finished stock is shown (Locate would give the last move).
        var (step, move) = (time < report.CycleTime ? report.Locate(time) : null) ?? (report.Steps.Count, 0);
        PositionText = Describe(report, step, move, time);
        var end = step >= report.Steps.Count;
        if (end)
        {
            scene.Progress = null;
            scene.Tool = null;
        }
        else
        {
            scene.Progress = (step, move);
            scene.Tool = step < steps.Count && tools.FirstOrDefault(t => t.Id == steps[step].Toolpath.ToolId) is { } tool
                && steps[step].Toolpath.Moves is { Count: > 0 } moves
                ? SceneBuilder.ToolMarker(steps[step].Toolpath.Frame, moves[Math.Min(move, moves.Count - 1)], tool)
                : null;
        }

        if (deviation && report.Deviation is { Samples.Length: > 0 } d)
        {
            var range = Math.Max(Math.Max(d.MaxLeftover, d.MaxGouge), 4 * tolerance);
            scene.SetPoints([SceneBuilder.DeviationPoints(d.Samples, tolerance, range, report.Resolution)]);
            return;
        }

        if (end && playback is null)
        {
            // The finished stock is the simulation's own; no playback needed.
            var final = await owner.SurfacePointsAsync(simulation.Stock, token);
            token.ThrowIfCancellationRequested();
            scene.SetPoints([SceneBuilder.StockPoints(final, report.Resolution)]);
            return;
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
        token.ThrowIfCancellationRequested();
        scene.SetPoints([SceneBuilder.StockPoints(points, playback.Resolution)]);
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
