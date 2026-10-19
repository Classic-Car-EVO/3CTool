using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;
using ThreeCTool.Rendering;

namespace ThreeCTool.Feeder.ViewModels;

public sealed class JobItemViewModel(JobInfo info)
{
    public JobInfo Info { get; } = info;

    public string Id => Info.JobId;

    public string Name => Info.Header.Name;

    public string Details => string.Join(" · ", new[]
    {
        Info.Header.ProjectName,
        Info.Header.SetupName,
        Info.Header.ToolDescription,
        Info.Header.Material,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Summary => string.Join(" · ", new[]
    {
        string.Create(CultureInfo.InvariantCulture, $"{Info.LineCount} lines"),
        Info.Header.EstimatedSeconds > 0 ? "~" + Display.Duration(Info.Header.EstimatedSeconds) : null,
        Info.Header.PlannedRpm > 0 ? Display.Number(Info.Header.PlannedRpm) + " rpm" : null,
        Info.LastRunState.Length > 0 ? "last: " + Info.LastRunState : null,
    }.Where(s => s is not null));

    public string Uploaded => Info.UploadedAt is null ? string.Empty : Display.LocalTime(Info.UploadedAt.ToDateTimeOffset());
}

public sealed partial class JobsViewModel(IFeederShell shell) : ViewModelBase
{
    private int previewRequest;
    private string? activeJobId;
    private string? recoverableJobId;
    private bool reachable;

    public ObservableCollection<JobItemViewModel> Jobs { get; } = [];

    public ToolpathScene Scene { get; } = new();

    public PreviewRenderer Renderer => shell.Renderer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(DeleteCommand))]
    public partial JobItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(DeleteCommand))]
    public partial bool CanStartJobs { get; private set; }

    [ObservableProperty]
    public partial string StartHint { get; private set; } = string.Empty;

    public void Apply(FeederStatus? status)
    {
        var active = status?.Job is { } job && RunViewModel.IsActiveState(job.State);
        var newActiveJobId = active ? status!.Job.JobId : null;
        recoverableJobId = status is { RecoveryAvailable: true, Job: { } last } ? last.JobId : null;
        if (newActiveJobId != activeJobId || reachable != (status is not null))
        {
            activeJobId = newActiveJobId;
            reachable = status is not null;
            DeleteCommand.NotifyCanExecuteChanged();
        }

        CanStartJobs = status is { Connection: ConnectionState.Connected, MachineState: "Idle" } && !active && !status.Recovering;
        StartHint = status is null ? "Feeder service not reachable"
            : status.Connection != ConnectionState.Connected ? "Controller not connected"
            : active || status.Recovering ? "A job is running"
            : status.MachineState != "Idle" ? $"Machine is {status.MachineState}" + (status.MachineState == "Alarm" ? " - unlock or home first" : string.Empty)
            : string.Empty;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        JobList? list = null;
        if (!await shell.RunAsync(async () => list = await shell.Client.ListJobsAsync(new())) || list is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Jobs.Clear();
        foreach (var job in list.Jobs.OrderByDescending(j => j.UploadedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue))
        {
            Jobs.Add(new JobItemViewModel(job));
        }

        IsEmpty = Jobs.Count == 0;
        Selected = Jobs.FirstOrDefault(j => j.Id == selectedId) ?? Jobs.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Selected is not { } job)
        {
            return;
        }

        var tool = job.Info.Header.ToolDescription.Length > 0 ? $" ({job.Info.Header.ToolDescription})" : string.Empty;
        if (!await shell.ConfirmAsync("Start job",
                $"Start '{job.Name}'?\n\nCheck that the stock is clamped, the tool{tool} is loaded, the work zero is set and the area is clear.",
                "Start"))
        {
            return;
        }

        StartJobReply? reply = null;
        if (!await shell.RunAsync(async () => reply = await shell.Client.StartJobAsync(new StartJobRequest { JobId = job.Id })) || reply is null)
        {
            return;
        }

        if (!reply.Started && reply.Warnings.Count > 0)
        {
            if (!await shell.ConfirmAsync("Warnings", string.Join("\n\n", reply.Warnings) + "\n\nStart anyway?", "Start anyway", danger: true)
                || !await shell.RunAsync(async () => reply = await shell.Client.StartJobAsync(new StartJobRequest { JobId = job.Id, AcceptWarnings = true })))
            {
                return;
            }
        }

        if (reply.Started)
        {
            shell.ShowTab(FeederTab.Run);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } job)
        {
            return;
        }

        var message = job.Info.Recoverable || job.Id == recoverableJobId
            ? $"Delete '{job.Name}' from the feeder?\n\nIts interrupted run can then no longer be recovered."
            : $"Delete '{job.Name}' from the feeder?";
        if (await shell.ConfirmAsync("Delete job", message, "Delete", danger: true)
            && await shell.RunAsync(async () => await shell.Client.DeleteJobAsync(new JobRef { JobId = job.Id })))
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void SetView(CameraView view) => Scene.SetView(view);

    private bool CanStart() => Selected is not null && CanStartJobs;

    private bool CanDelete() => Selected is not null && reachable && Selected.Id != activeJobId;

    partial void OnSelectedChanged(JobItemViewModel? value) => _ = LoadPreviewAsync(value);

    private async Task LoadPreviewAsync(JobItemViewModel? job)
    {
        var request = ++previewRequest;
        if (job is null)
        {
            Scene.SetPath(PolylinePath.Empty);
            PreviewText = string.Empty;
            return;
        }

        PreviewText = "Loading preview...";
        var path = await shell.GetPreviewAsync(job.Id);
        if (request != previewRequest)
        {
            return;
        }

        Scene.SetPath(path ?? PolylinePath.Empty);
        PreviewText = path is null ? "No preview available" : string.Empty;
    }
}
