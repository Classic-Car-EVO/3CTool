using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;
using ThreeCTool.Rendering;

using FeederClient = ThreeCTool.Feeder.Contracts.Feeder.FeederClient;

namespace ThreeCTool.Feeder.ViewModels;

/// <summary>
/// Root of the touch UI: status bar, pages, and overlays (keyboard, confirmations, operator prompts, recovery,
/// feedback). Must be used on the UI thread.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IFeederShell, IDisposable
{
    private readonly FeederConnection connection;
    private readonly Dictionary<string, Task<PolylinePath?>> previews = [];
    private string? lastRunId;
    private string? dismissedFeedbackRunId;
    private bool wasConnected;

    public MainViewModel(FeederConnection connection, PreviewRenderer renderer = PreviewRenderer.Auto)
    {
        ArgumentNullException.ThrowIfNull(connection);
        this.connection = connection;
        Renderer = renderer;
        Jobs = new JobsViewModel(this);
        Run = new RunViewModel(this);
        Jog = new JogViewModel(this);
        Setup = new SetupViewModel(this);
        connection.StatusChanged += OnStatusChanged;
        Apply(null);
    }

    public FeederClient Client => connection.Client;

    public LocalAdmin.LocalAdminClient Admin => connection.Admin;

    public FeederStatus? Status => connection.Status;

    public PreviewRenderer Renderer { get; }

    public JobsViewModel Jobs { get; }

    public RunViewModel Run { get; }

    public JogViewModel Jog { get; }

    public SetupViewModel Setup { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    public partial bool IsControllerConnected { get; private set; }

    [ObservableProperty]
    public partial string ConnectionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MachineState { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial AxesText Work { get; private set; } = AxesText.Unknown;

    [ObservableProperty]
    public partial bool HasAlarm { get; private set; }

    [ObservableProperty]
    public partial string AlarmText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Overlay), nameof(HasOverlay))]
    public partial KeyboardViewModel? Keyboard { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Overlay), nameof(HasOverlay))]
    public partial ConfirmViewModel? Confirm { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Overlay), nameof(HasOverlay))]
    public partial PromptViewModel? Prompt { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Overlay), nameof(HasOverlay))]
    public partial RecoveryViewModel? Recovery { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Overlay), nameof(HasOverlay))]
    public partial FeedbackViewModel? Feedback { get; private set; }

    /// <summary>Topmost overlay: dialogs over operator prompts over recovery and feedback.</summary>
    public ViewModelBase? Overlay => (ViewModelBase?)Keyboard ?? (ViewModelBase?)Confirm ?? (ViewModelBase?)Prompt ?? (ViewModelBase?)Recovery ?? Feedback;

    public bool HasOverlay => Overlay is not null;

    public void Start() => connection.Start();

    public async Task<bool> RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action();
            return true;
        }
        catch (RpcException ex)
        {
            ShowError(ex.StatusCode switch
            {
                StatusCode.Unavailable => "The feeder service is not reachable.",
                _ when ex.Status.Detail.Length > 0 => ex.Status.Detail,
                _ => ex.StatusCode.ToString(),
            });
            return false;
        }
    }

    public async Task<string?> RequestTextAsync(string title, string initial, bool numeric)
    {
        Keyboard?.Cancel();
        var keyboard = new KeyboardViewModel(title, initial, numeric);
        Keyboard = keyboard;
        try
        {
            return await keyboard.Result;
        }
        finally
        {
            if (Keyboard == keyboard)
            {
                Keyboard = null;
            }
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false)
    {
        Confirm?.Cancel();
        var confirm = new ConfirmViewModel(title, message, confirmText, danger);
        Confirm = confirm;
        try
        {
            return await confirm.Result;
        }
        finally
        {
            if (Confirm == confirm)
            {
                Confirm = null;
            }
        }
    }

    public void ShowMessage(string message)
    {
        Message = message;
        MessageIsError = false;
    }

    public void ShowError(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    [RelayCommand]
    public void DismissMessage() => Message = null;

    public Task<PolylinePath?> GetPreviewAsync(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        if (!previews.TryGetValue(jobId, out var task) || (task.IsCompleted && task.Result is null))
        {
            task = LoadPreviewAsync(jobId);
            previews[jobId] = task;
        }

        return task;
    }

    public void ShowTab(FeederTab tab) => SelectedTab = (int)tab;

    public async Task OpenRecoveryAsync()
    {
        RecoveryPlanInfo? plan = null;
        if (!await RunAsync(async () => plan = await Client.GetRecoveryPlanAsync(new())) || plan is null)
        {
            return;
        }

        if (!plan.Available)
        {
            ShowMessage("There is no run to recover.");
            return;
        }

        Recovery = new RecoveryViewModel(this, plan);
    }

    public void CloseOverlay(object overlay)
    {
        if (overlay == Recovery)
        {
            Recovery = null;
        }
        else if (overlay is FeedbackViewModel feedback && feedback == Feedback)
        {
            dismissedFeedbackRunId = feedback.RunId;
            Feedback = null;
        }
    }

    public void Dispose()
    {
        connection.StatusChanged -= OnStatusChanged;
        Jog.Dispose();
        connection.Dispose();
    }

    private async Task<PolylinePath?> LoadPreviewAsync(string jobId)
    {
        Preview? preview = null;
        if (!await RunAsync(async () => preview = await Client.GetPreviewAsync(new JobRef { JobId = jobId })) || preview is null)
        {
            return null;
        }

        try
        {
            return Display.ToPath(preview);
        }
        catch (ArgumentException ex)
        {
            ShowError("Invalid preview: " + ex.Message);
            return null;
        }
    }

    private void OnStatusChanged(object? sender, FeederStatus? status) => Apply(status);

    private void Apply(FeederStatus? status)
    {
        IsConnected = status is not null;
        IsControllerConnected = status?.Connection == ConnectionState.Connected;
        ConnectionText = status is null
            ? "Feeder service not reachable" + (connection.LastFailure is { } failure ? $" ({failure})" : string.Empty)
            : status.Connection switch
            {
                ConnectionState.Connected => string.Empty,
                ConnectionState.Connecting => "Connecting to the controller...",
                _ => "Controller disconnected",
            };
        MachineState = status is null ? "Offline"
            : status.Connection != ConnectionState.Connected ? "No controller"
            : status.MachineState.Length > 0 ? status.MachineState : "Unknown";
        var wasRunning = IsRunning;
        IsRunning = status?.Job is { } job && RunViewModel.IsActiveState(job.State);
        Work = AxesText.From(status?.WorkPosition);
        HasAlarm = status is { AlarmCode: > 0 } || status?.MachineState == "Alarm";
        AlarmText = !HasAlarm ? string.Empty
            : status!.AlarmCode > 0 ? string.Create(CultureInfo.InvariantCulture, $"Alarm {status.AlarmCode}: {status.AlarmText}")
            : "Alarm: unlock or home the machine";

        Jobs.Apply(status);
        Run.Apply(status);
        Jog.Apply(status);
        Setup.Apply(status);
        ApplyOverlays(status);

        if (status is not null && !wasConnected)
        {
            _ = Jobs.RefreshAsync();
            _ = Setup.LoadAsync();
        }
        else if (wasRunning && !IsRunning && status is not null)
        {
            // The job list shows the new last-run state.
            _ = Jobs.RefreshAsync();
        }

        wasConnected = status is not null;
    }

    private void ApplyOverlays(FeederStatus? status)
    {
        if (status?.Prompt is { } prompt)
        {
            if (Prompt?.Id != prompt.Id)
            {
                Prompt = new PromptViewModel(this, prompt);
                ShowTab(FeederTab.Run);
            }
        }
        else
        {
            Prompt = null;
        }

        if (status?.Job is { } job && job.RunId != lastRunId)
        {
            lastRunId = job.RunId;
            if (RunViewModel.IsActiveState(job.State))
            {
                ShowTab(FeederTab.Run);
            }
        }

        if (Recovery is not null && status is not null && (!status.RecoveryAvailable || status.Recovering))
        {
            Recovery = null;
        }

        var feedbackRun = status?.FeedbackRunId ?? string.Empty;
        if (feedbackRun.Length == 0 || IsRunning)
        {
            Feedback = null;
        }
        else if (Feedback?.RunId != feedbackRun && feedbackRun != dismissedFeedbackRunId)
        {
            var name = status!.Job is { } last && last.RunId == feedbackRun ? last.Name : "the last job";
            var state = status.Job is { } ended && ended.RunId == feedbackRun ? ended.State : string.Empty;
            Feedback = new FeedbackViewModel(this, feedbackRun, name, state);
        }
    }
}
