using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;

namespace ThreeCTool.Feeder.ViewModels;

/// <summary>Operator prompt from the feeder: confirmations, program pauses (tool change) and spindle RPM instructions.</summary>
public sealed partial class PromptViewModel : ViewModelBase
{
    private readonly IFeederShell shell;

    public PromptViewModel(IFeederShell shell, Prompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        this.shell = shell;
        Prompt = prompt;
        Rpm = prompt.SuggestedRpm;
    }

    public Prompt Prompt { get; }

    public string Id => Prompt.Id;

    public string Title => Prompt.Kind switch
    {
        PromptKind.ProgramPause => "Program pause",
        PromptKind.Spindle => "Set the spindle speed",
        PromptKind.Choice => "Question",
        _ => "Confirm",
    };

    public string Message => Prompt.Message;

    public string LineText => Prompt.Line > 0 ? string.Create(CultureInfo.InvariantCulture, $"Line {Prompt.Line}") : string.Empty;

    public bool IsSpindle => Prompt.Kind == PromptKind.Spindle;

    /// <summary>Yes/No question: Confirm answers Yes, No answers No, Cancel stops the job.</summary>
    public bool IsChoice => Prompt.Kind == PromptKind.Choice;

    public bool CanCancel => Prompt.CanCancel;

    public string CancelText => IsChoice ? "Stop job" : "Cancel job step";

    public string ConfirmText => Prompt.Kind switch
    {
        PromptKind.ProgramPause => "Continue",
        PromptKind.Spindle => "Spindle running",
        PromptKind.Choice => "Yes",
        _ => "Confirm",
    };

    /// <summary>RPM set on the spindle dial (spindle prompts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RpmText))]
    public partial double Rpm { get; set; }

    public string RpmText => Display.Number(Rpm) + " rpm";

    /// <summary>Prompts are answered at the machine (touch screen or direct connection), not from a paired desktop.</summary>
    public bool CanAnswerHere => shell.IsLocal;

    /// <summary>Set once answered; the prompt closes when the feeder clears it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(CancelCommand), nameof(NoCommand))]
    public partial bool Answered { get; set; }

    [RelayCommand]
    private async Task EditRpmAsync()
    {
        var text = await shell.RequestTextAsync("Spindle RPM on the dial", Display.Number(Rpm), numeric: true);
        if (text is null)
        {
            return;
        }

        if (Display.TryParse(text, out var rpm) && rpm >= 0)
        {
            Rpm = rpm;
        }
        else
        {
            shell.ShowMessage($"'{text}' is not a valid RPM.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task ConfirmAsync() => AnswerAsync(true);

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task NoAsync() => AnswerAsync(true, choice: false);

    [RelayCommand(CanExecute = nameof(CanAnswerCancel))]
    private Task CancelAsync() => AnswerAsync(false);

    private bool CanAnswer() => !Answered && CanAnswerHere;

    private bool CanAnswerCancel() => CanAnswer() && CanCancel;

    private async Task AnswerAsync(bool confirmed, bool choice = true)
    {
        Answered = true;
        var answer = new PromptAnswer { Id = Id, Confirmed = confirmed, Rpm = IsSpindle && confirmed ? Rpm : 0, Choice = IsChoice && confirmed && choice };
        if (!await shell.RunAsync(async () => await shell.Client.AnswerPromptAsync(answer)))
        {
            Answered = false;
        }
    }
}

/// <summary>Recovery wizard entry: shows the plan for the interrupted run; the steps then run as prompts.</summary>
public sealed partial class RecoveryViewModel(IFeederShell shell, RecoveryPlanInfo plan) : ViewModelBase
{
    public RecoveryPlanInfo Plan { get; } = plan;

    public string JobName => Plan.JobName;

    public string Interruption => Plan.Interruption;

    public string Action => Plan.Action;

    public string LinesText => string.Create(CultureInfo.InvariantCulture, $"Interrupted at line {Plan.InterruptedLine}, resuming from line {Plan.ResumeLine}");

    public IReadOnlyList<string> Notes => Plan.Notes;

    public bool Available => Plan.Available;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (await shell.ConfirmAsync("Start recovery",
                "The feeder will now guide you through unlocking, homing, re-probing and re-entry. Keep a hand on the e-stop.",
                "Start recovery")
            && await shell.RunAsync(async () => await shell.Client.StartRecoveryAsync(new())))
        {
            shell.CloseOverlay(this);
            shell.ShowTab(FeederTab.Run);
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (await shell.ConfirmAsync("Discard recovery", $"Discard the interrupted run of '{JobName}'? It cannot be resumed afterwards.", "Discard", danger: true)
            && await shell.RunAsync(async () => await shell.Client.DiscardRecoveryAsync(new())))
        {
            shell.CloseOverlay(this);
        }
    }

    [RelayCommand]
    private void Close() => shell.CloseOverlay(this);
}

/// <summary>End-of-job feedback form; the desktop learns feeds from it.</summary>
public sealed partial class FeedbackViewModel : ViewModelBase
{
    private static readonly (double, string)[] Ratings = [(1, "1"), (2, "2"), (3, "3"), (4, "4"), (5, "5")];

    private readonly IFeederShell shell;

    public FeedbackViewModel(IFeederShell shell, string runId, string jobName, string finalState)
    {
        this.shell = shell;
        RunId = runId;
        JobName = jobName;
        FinalState = finalState;
    }

    public string RunId { get; }

    public string JobName { get; }

    public string FinalState { get; }

    public ChoiceGroup Finish { get; } = new("Surface finish (1 bad - 5 good)", Ratings, 0);

    public ChoiceGroup Chatter { get; } = new("Chatter / noise (1 bad - 5 none)", Ratings, 0);

    public ChoiceGroup Wear { get; } = new("Tool condition (1 worn - 5 sharp)", Ratings, 0);

    public ChoiceGroup WearEvent { get; } = new("Tool event", [
        ((double)Contracts.WearEvent.None, "None"),
        ((double)Contracts.WearEvent.Wear, "Worn"),
        ((double)Contracts.WearEvent.Breakage, "Broken"),
        ((double)Contracts.WearEvent.Replaced, "Replaced"),
    ], (double)Contracts.WearEvent.None);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesText))]
    public partial string Notes { get; set; } = string.Empty;

    public string NotesText => Notes.Length > 0 ? Notes : "Add notes...";

    [RelayCommand]
    private async Task EditNotesAsync()
    {
        if (await shell.RequestTextAsync("Notes", Notes, numeric: false) is { } text)
        {
            Notes = text.Trim();
        }
    }

    [RelayCommand]
    private Task SubmitAsync() => SendAsync(new FeedbackSubmission
    {
        RunId = RunId,
        FinishRating = (int)Finish.Value,
        ChatterRating = (int)Chatter.Value,
        WearRating = (int)Wear.Value,
        WearEvent = (Contracts.WearEvent)(int)WearEvent.Value,
        Notes = Notes,
    });

    [RelayCommand]
    private Task SkipAsync() => SendAsync(new FeedbackSubmission { RunId = RunId, Skipped = true });

    private async Task SendAsync(FeedbackSubmission submission)
    {
        if (await shell.RunAsync(async () => await shell.Client.SubmitFeedbackAsync(submission)))
        {
            shell.CloseOverlay(this);
        }
    }
}
