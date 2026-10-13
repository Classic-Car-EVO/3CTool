using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;
using ThreeCTool.Rendering;

namespace ThreeCTool.Feeder.ViewModels;

/// <summary>Job view: progress, DRO, preview with progress and tool position, pause/resume/stop and overrides.</summary>
public sealed partial class RunViewModel(IFeederShell shell) : ViewModelBase
{
    public const int FeedStep = 10;
    public const int MinFeed = 10;
    public const int MaxFeed = 200;

    private string? previewJobId;

    public ToolpathScene Scene { get; } = new();

    public PreviewRenderer Renderer => shell.Renderer;

    [ObservableProperty]
    public partial bool HasJob { get; private set; }

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial string JobName { get; private set; } = "No job";

    [ObservableProperty]
    public partial string State { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LineText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RemainingText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int FeedOverride { get; private set; } = 100;

    [ObservableProperty]
    public partial string SpindleText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Warnings { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand))]
    public partial bool RecoveryAvailable { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    public partial bool CanPause { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    public partial bool CanResume { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool CanStop { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FeedDownCommand), nameof(FeedUpCommand), nameof(FeedResetCommand), nameof(EditRpmCommand))]
    public partial bool CanAdjust { get; private set; }

    /// <summary>Job states in which a run is in progress (not ended).</summary>
    public static bool IsActiveState(string state) =>
        state is "Pending" or "Running" or "Paused" or "ProgramPaused" or "Stopping" or "Recovering";

    public void Apply(FeederStatus? status)
    {
        var job = status?.Job;
        HasJob = job is not null;
        IsActive = job is not null && IsActiveState(job.State);
        RecoveryAvailable = status is { RecoveryAvailable: true, Recovering: false } && !IsActive;
        Warnings = status is null ? string.Empty : string.Join("\n", status.Warnings.Append(status.LastError).Where(w => w.Length > 0));
        if (job is null)
        {
            JobName = "No job";
            State = string.Empty;
            LineText = ProgressText = ElapsedText = RemainingText = SpindleText = Message = string.Empty;
            ProgressPercent = 0;
            FeedOverride = 100;
            CanPause = CanResume = CanStop = CanAdjust = false;
            Scene.Marker = null;
            return;
        }

        JobName = job.Name;
        State = status!.Recovering && job.State != "Recovering" ? "Recovering" : job.State;
        LineText = string.Create(CultureInfo.InvariantCulture, $"Line {(job.LineExact ? string.Empty : "~")}{job.Line} / {job.LineCount}");
        ProgressPercent = Math.Round(job.Progress * 100, 1);
        ProgressText = string.Create(CultureInfo.InvariantCulture, $"{job.Progress * 100:0}%");
        ElapsedText = Display.Duration(job.ElapsedSeconds);
        RemainingText = IsActive && job.RemainingSeconds > 0
            ? Display.Duration(job.RemainingSeconds) + " (ETA " + DateTime.Now.AddSeconds(job.RemainingSeconds).ToString("HH:mm", CultureInfo.InvariantCulture) + ")"
            : "-";
        FeedOverride = job.FeedOverride;
        SpindleText = job.PlannedRpm > 0
            ? $"{Display.Number(job.SpindleRpm)} rpm (planned {Display.Number(job.PlannedRpm)})"
            : $"{Display.Number(job.SpindleRpm)} rpm";
        Message = job.Message;
        CanPause = job.State == "Running";
        CanResume = job.State == "Paused";
        CanStop = IsActive && job.State != "Stopping";
        CanAdjust = job.State is "Running" or "Paused" or "ProgramPaused";

        if (job.JobId != previewJobId)
        {
            previewJobId = job.JobId;
            _ = LoadPreviewAsync(job.JobId);
        }

        Scene.ProgressLine = job.Line;
        Scene.Marker = IsActive && status.WorkPosition is { } work ? Display.PartFrame(work) : null;
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync() => await shell.RunAsync(async () => await shell.Client.PauseAsync(new()));

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync() => await shell.RunAsync(async () => await shell.Client.ResumeAsync(new()));

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (await shell.ConfirmAsync("Stop job", "Stop the job? The machine holds, resets and retracts; the run can be recovered later.", "Stop", danger: true))
        {
            await shell.RunAsync(async () => await shell.Client.StopAsync(new()));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private Task FeedDownAsync() => SetFeedAsync(FeedOverride - FeedStep);

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private Task FeedUpAsync() => SetFeedAsync(FeedOverride + FeedStep);

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private Task FeedResetAsync() => SetFeedAsync(100);

    /// <summary>The operator changed the spindle dial: record the new RPM for learning.</summary>
    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private async Task EditRpmAsync()
    {
        var current = shell.Status?.Job?.SpindleRpm ?? 0;
        var text = await shell.RequestTextAsync("New spindle RPM (dial)", Display.Number(current), numeric: true);
        if (text is null)
        {
            return;
        }

        if (!Display.TryParse(text, out var rpm) || rpm < 0)
        {
            shell.ShowMessage($"'{text}' is not a valid RPM.");
            return;
        }

        await shell.RunAsync(async () => await shell.Client.SetSpindleRpmAsync(new SpindleRpmRequest { Rpm = rpm }));
    }

    [RelayCommand(CanExecute = nameof(RecoveryAvailable))]
    private Task RecoverAsync() => shell.OpenRecoveryAsync();

    [RelayCommand]
    private void SetView(CameraView view) => Scene.SetView(view);

    private async Task SetFeedAsync(int percent)
    {
        var value = Math.Clamp(percent, MinFeed, MaxFeed);
        await shell.RunAsync(async () => await shell.Client.SetFeedOverrideAsync(new FeedOverrideRequest { Percent = value }));
    }

    private async Task LoadPreviewAsync(string jobId)
    {
        var path = await shell.GetPreviewAsync(jobId);
        if (jobId == previewJobId)
        {
            var progress = Scene.ProgressLine;
            Scene.SetPath(path ?? PolylinePath.Empty);
            Scene.ProgressLine = progress;
        }
    }
}
