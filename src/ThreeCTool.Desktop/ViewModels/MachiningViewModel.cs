using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Feeds;
using ThreeCTool.Post;
using ThreeCTool.Rendering;
using ThreeCTool.Simulation;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>Planned feeds of one operation.</summary>
public sealed record FeedRow(string Title, string Detail, string Limit, string Warnings)
{
    public bool HasWarnings => Warnings.Length > 0;
}

/// <summary>A verification finding; <see cref="Step"/> is the simulation step index (−1 when not step-related).</summary>
public sealed record IssueRow(VerificationIssue Issue, int Step, string Title, string Detail)
{
    public bool IsError => Issue.Severity == IssueSeverity.Error;

    public bool IsWarning => Issue.Severity == IssueSeverity.Warning;
}

/// <summary>A posted G-code file of the project.</summary>
public sealed record GcodeRow(GcodeFile File, string Title, string Detail);

/// <summary>
/// The machining page: setups and operations, then generate → feeds → simulate → post with progress and cancel,
/// the results (toolpath statistics, feeds, verification report, G-code files) and a 3D view with simulation playback.
/// </summary>
public sealed partial class MachiningViewModel : PageViewModel, IDisposable
{
    private const int PreviewLines = 400;

    private static readonly FileType Gcode = new("G-code", ["*.nc", "*.gcode", "*.ngc"]);

    private readonly ProjectWorkspace workspace;
    private CamPipeline? pipeline;
    private ProjectSession? pipelineSession;
    private SimulationResult? shownSimulation;
    private CancellationTokenSource? cancel;
    private bool loading;

    public MachiningViewModel(IShell shell, ProjectWorkspace workspace)
        : base(shell, "Machining")
    {
        this.workspace = workspace;
        Playback = new PlaybackViewModel(shell, Scene.Scene);
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectWorkspace.Session))
            {
                OnPropertyChanged(nameof(HasProject));
                OnPropertyChanged(nameof(HasNoProject));
                Reset();
            }
        };
        shell.Services.Compute.Resetting += OnComputeResetting;
    }

    public static IReadOnlyList<Choice<StrategyType>> Strategies { get; } =
        [.. Enum.GetValues<StrategyType>().Select(s => new Choice<StrategyType>(ProjectSession.Describe(s), s))];

    public static IReadOnlyList<Choice<SetupOrientation>> Orientations => SetupEditor.Orientations;

    public bool HasProject => workspace.HasProject;

    public bool HasNoProject => !workspace.HasProject;

    public ProjectSceneViewModel Scene { get; } = new();

    public PlaybackViewModel Playback { get; }

    [ObservableProperty]
    public partial PreviewRenderer Renderer { get; private set; }

    // ----- Setups and operations -----

    public ObservableCollection<SetupRow> Setups { get; } = [];

    [ObservableProperty]
    public partial SetupRow? SelectedSetup { get; set; }

    [ObservableProperty]
    public partial SetupEditor? SetupEditor { get; private set; }

    [ObservableProperty]
    public partial Choice<SetupOrientation> NewOrientation { get; set; } = SetupEditor.Orientations[0];

    public ObservableCollection<OperationRow> Operations { get; } = [];

    [ObservableProperty]
    public partial OperationRow? SelectedOperation { get; set; }

    [ObservableProperty]
    public partial OperationEditor? OperationEditor { get; private set; }

    public ObservableCollection<Tool> Tools { get; } = [];

    [ObservableProperty]
    public partial Choice<StrategyType> NewStrategy { get; set; } = Strategies[0];

    [ObservableProperty]
    public partial Tool? NewTool { get; set; }

    // ----- Run -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(PlanFeedsCommand), nameof(SimulateCommand), nameof(PostCommand), nameof(RunAllCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; private set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial string Stage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>Writes G-code even when the verification found errors.</summary>
    [ObservableProperty]
    public partial bool OverrideVerification { get; set; }

    // ----- Results -----

    [ObservableProperty]
    public partial string ToolpathSummary { get; private set; } = string.Empty;

    public ObservableCollection<FeedRow> Feeds { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSimulation))]
    public partial string SimulationSummary { get; private set; } = string.Empty;

    public bool HasSimulation => SimulationSummary.Length > 0;

    [ObservableProperty]
    public partial string DeviationSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool SimulationIsStale { get; private set; }

    [ObservableProperty]
    public partial bool BlocksOutput { get; private set; }

    public ObservableCollection<IssueRow> Issues { get; } = [];

    public ObservableCollection<GcodeRow> Files { get; } = [];

    [ObservableProperty]
    public partial GcodeRow? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string Preview { get; private set; } = string.Empty;

    public override async Task ShowAsync()
    {
        Renderer = Shell.Services.Settings.Renderer;
        Scene.ShowRapids = Shell.Services.Settings.ShowRapids;
        if (!workspace.HasProject)
        {
            return;
        }

        await workspace.EditAsync(async s =>
        {
            Load(s);
            await Scene.RefreshAsync(s);
            await Pipeline(s).LoadCacheAsync();
        });
        await RefreshResultsAsync();
    }

    public void Dispose()
    {
        Shell.Services.Compute.Resetting -= OnComputeResetting;
        cancel?.Cancel();
        Playback.Dispose();
        pipeline?.Dispose();
        pipeline = null;
    }

    // ----- Lists -----

    private void Load(ProjectSession session)
    {
        loading = true;
        var keep = SelectedSetup?.Setup.Id;
        try
        {
            Tools.Clear();
            foreach (var tool in session.Tools)
            {
                Tools.Add(tool);
            }

            NewTool = Tools.FirstOrDefault(t => t.Id == NewTool?.Id) ?? Tools.FirstOrDefault();
            Setups.Clear();
            foreach (var setup in session.Setups)
            {
                Setups.Add(new SetupRow(setup));
            }
        }
        finally
        {
            loading = false;
        }

        var selected = Setups.FirstOrDefault(r => r.Setup.Id == keep) ?? Setups.FirstOrDefault();
        if (ReferenceEquals(selected, SelectedSetup))
        {
            OnSelectedSetupChanged(selected);
        }
        else
        {
            SelectedSetup = selected;
        }
    }

    private void Reload()
    {
        if (workspace.Session is { } session)
        {
            Load(session);
        }
    }

    partial void OnSelectedSetupChanged(SetupRow? value)
    {
        if (loading)
        {
            return;
        }

        var session = workspace.Session;
        SetupEditor = value is null || session is null
            ? null
            : new SetupEditor(value.Setup, session.Machine, Shell.Services.Settings.DisplayUnit, () => SaveAsync(value));
        var keep = SelectedOperation?.Operation.Id;
        Operations.Clear();
        if (value is not null && session is not null)
        {
            foreach (var operation in value.Setup.Operations)
            {
                Operations.Add(new OperationRow(operation, ToolName(session, operation)));
            }
        }

        SelectedOperation = Operations.FirstOrDefault(r => r.Operation.Id == keep) ?? Operations.FirstOrDefault();
        UpdateStatus();
    }

    partial void OnSelectedOperationChanged(OperationRow? value)
    {
        var session = workspace.Session;
        OperationEditor = value is null || session is null
            ? null
            : new OperationEditor(value.Operation, [.. session.Tools], session.Project.Tolerances, Shell.Services.Settings.DisplayUnit, () => SaveAsync(value));
        Scene.Scene.SelectedToolpath = value?.Operation.Id;
    }

    private static string ToolName(ProjectSession session, Operation operation) => session.FindTool(operation.ToolId)?.Name ?? "missing tool";

    /// <summary>Saves an edit made in a setup or operation editor and refreshes what depends on it.</summary>
    private async Task SaveAsync(ObservableObject row)
    {
        if (await workspace.EditAsync(s => s.SaveAsync()) && workspace.Session is { } session)
        {
            switch (row)
            {
                case SetupRow setup:
                    setup.Refresh();
                    break;
                case OperationRow operation:
                    operation.Refresh(ToolName(session, operation.Operation));
                    break;
                default:
                    break;
            }

            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private async Task AddSetup()
    {
        Setup? added = null;
        if (await workspace.EditAsync(async s => added = await s.AddSetupAsync(NewOrientation.Value)))
        {
            Reload();
            SelectedSetup = Setups.FirstOrDefault(r => r.Setup.Id == added!.Id);
            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveSetup(SetupRow row)
    {
        var count = row.Setup.Operations.Count;
        var detail = count == 0 ? string.Empty : $" and its {count} operation{(count == 1 ? string.Empty : "s")}";
        if (await Shell.ConfirmAsync("Remove setup", $"Remove '{row.Setup.Name}'{detail}? Keep-out zones fixed to it are removed too.", "Remove", danger: true)
            && await workspace.EditAsync(s => s.RemoveSetupAsync(row.Setup)))
        {
            Reload();
            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private Task MoveSetupUp(SetupRow row) => MoveSetupAsync(row, -1);

    [RelayCommand]
    private Task MoveSetupDown(SetupRow row) => MoveSetupAsync(row, 1);

    private async Task MoveSetupAsync(SetupRow row, int delta)
    {
        if (await workspace.EditAsync(s => s.MoveSetupAsync(row.Setup, delta)))
        {
            Reload();
            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private async Task AddOperation()
    {
        if (SelectedSetup is not { } setup)
        {
            Shell.ShowMessage("Add a setup first.", isError: true);
            return;
        }

        if (NewTool is not { } tool)
        {
            Shell.ShowMessage("Add tools to the project first (Project page, Overview).", isError: true);
            return;
        }

        Operation? added = null;
        if (await workspace.EditAsync(async s => added = await s.AddOperationAsync(setup.Setup, NewStrategy.Value, tool.Id)))
        {
            Reload();
            SelectedOperation = Operations.FirstOrDefault(r => r.Operation.Id == added!.Id);
            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveOperation(OperationRow row)
    {
        if (await Shell.ConfirmAsync("Remove operation", $"Remove '{row.Operation.Name}'?", "Remove", danger: true)
            && await workspace.EditAsync(s => s.RemoveOperationAsync(row.Operation)))
        {
            Reload();
            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private async Task DuplicateOperation(OperationRow row)
    {
        Operation? copy = null;
        if (await workspace.EditAsync(async s => copy = await s.DuplicateOperationAsync(row.Operation)))
        {
            Reload();
            SelectedOperation = Operations.FirstOrDefault(r => r.Operation.Id == copy!.Id);
            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private Task MoveOperationUp(OperationRow row) => MoveOperationAsync(row, -1);

    [RelayCommand]
    private Task MoveOperationDown(OperationRow row) => MoveOperationAsync(row, 1);

    private async Task MoveOperationAsync(OperationRow row, int delta)
    {
        if (await workspace.EditAsync(s => s.MoveOperationAsync(row.Operation, delta)))
        {
            Reload();
            await RefreshResultsAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleOperation(OperationRow row)
    {
        row.Operation.Enabled = !row.Operation.Enabled;
        if (OperationEditor?.Operation == row.Operation)
        {
            OperationEditor.Enabled = row.Operation.Enabled;
        }

        await SaveAsync(row);
    }

    // ----- Run -----

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Generate() => RunStageAsync("Toolpaths", async (p, progress, token) =>
    {
        var generated = await p.GenerateAsync(progress, token);
        var cached = generated.Count(g => g.FromCache);
        Shell.ShowMessage($"{generated.Count} toolpath{(generated.Count == 1 ? string.Empty : "s")} ready ({cached} from the cache).");
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task PlanFeeds() => RunStageAsync("Feeds", async (p, _, token) =>
    {
        if (p.ExecutionOrder().Any(o => p.GetToolpath(o.Id) is null))
        {
            // Engagement-based feeds use the cut lengths of the toolpaths.
            await p.GenerateAsync(null, token);
        }

        var plans = await p.PlanFeedsAsync(token);
        var warnings = plans.Sum(g => g.Plan.Operations.Sum(o => o.Warnings.Count(w => w.Level != FeedWarningLevel.Info)));
        Shell.ShowMessage(warnings == 0 ? "Feeds planned." : $"Feeds planned with {warnings} warning(s); see Feeds.", isError: false);
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Simulate() => RunStageAsync("Simulation", async (p, progress, token) =>
    {
        await EnsureFeedsAsync(p, progress, token);
        var result = await p.SimulateAsync(progress, token);
        Shell.ShowMessage(DescribeReport(result.Report), result.Report.BlocksOutput);
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task Post() => RunStageAsync("Post", async (p, progress, token) =>
    {
        await EnsureFeedsAsync(p, progress, token);
        var outputs = await p.PostAsync(OverrideVerification, progress, token);
        Shell.ShowMessage(DescribeOutputs(outputs));
    });

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RunAll() => RunStageAsync("Toolpaths", async (p, progress, token) =>
    {
        var outputs = await p.RunAllAsync(OverrideVerification, progress, token);
        Shell.ShowMessage(DescribeOutputs(outputs));
    });

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => cancel?.Cancel();

    /// <summary>Plans feeds when an operation has none yet (simulation and post need them).</summary>
    private static async Task EnsureFeedsAsync(CamPipeline p, IProgress<CamProgress> progress, CancellationToken token)
    {
        if (p.ExecutionOrder().Any(o => o.Conditions is null))
        {
            await p.GenerateAsync(progress, token);
            await p.PlanFeedsAsync(token);
        }
    }

    private static string DescribeOutputs(IReadOnlyList<PostOutput> outputs)
    {
        var warnings = outputs.Sum(o => o.Warnings.Count);
        return $"Wrote {outputs.Count} G-code file{(outputs.Count == 1 ? string.Empty : "s")}"
            + (warnings == 0 ? "." : $" with {warnings} warning(s).");
    }

    private static string DescribeReport(VerificationReport report)
    {
        var errors = report.Issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = report.Issues.Count(i => i.Severity == IssueSeverity.Warning);
        return $"Simulated: cycle time {PlaybackViewModel.Clock(report.CycleTime)}, {errors} error(s), {warnings} warning(s).";
    }

    private async Task RunStageAsync(string stage, Func<CamPipeline, IProgress<CamProgress>, CancellationToken, Task> action)
    {
        if (IsRunning || workspace.Session is null)
        {
            return;
        }

        if (OperationEditor?.Invalid.FirstOrDefault() is { } invalid)
        {
            Shell.ShowMessage(invalid.Error!, isError: true);
            return;
        }

        using var cts = new CancellationTokenSource();
        cancel = cts;
        IsRunning = true;
        Stage = stage;
        Progress = 0;
        var progress = new System.Progress<CamProgress>(p =>
        {
            if (IsRunning)
            {
                Stage = p.Item.Length == 0 ? p.Stage : $"{p.Stage}: {p.Item}";
                Progress = Math.Clamp(p.Fraction, 0, 1) * 100;
            }
        });

        // The simulation shown may be replaced or disposed by the stage.
        Playback.Clear();
        shownSimulation = null;
        try
        {
            var ok = await workspace.EditAsync(s => action(Pipeline(s), progress, cts.Token));
            if (!ok && cts.IsCancellationRequested)
            {
                Shell.ShowMessage("Cancelled.");
            }
        }
        finally
        {
            cancel = null;
            IsRunning = false;
            Stage = string.Empty;
            Progress = 0;
        }

        await RefreshResultsAsync();
    }

    private CamPipeline Pipeline(ProjectSession session)
    {
        if (pipeline is null || !ReferenceEquals(pipelineSession, session))
        {
            pipeline?.Dispose();
            pipeline = new CamPipeline(session, Shell.Services.Compute, Shell.Services.Time);
            pipelineSession = session;
        }

        return pipeline;
    }

    // ----- Results -----

    /// <summary>Re-reads the toolpath, feed, simulation and G-code state and updates the 3D view.</summary>
    public async Task RefreshResultsAsync()
    {
        IReadOnlyList<GcodeFile> files = [];
        IReadOnlyList<GeneratedToolpath> current = [];
        SimulationResult? simulation = null;
        var stale = true;
        ProjectSession? session = null;
        var ok = await workspace.EditAsync(async s =>
        {
            session = s;
            var p = Pipeline(s);
            current = p.CurrentToolpaths();
            simulation = p.Simulation;
            stale = p.SimulationIsStale;
            files = await s.ListGcodeFilesAsync();
        });
        if (!ok || session is null)
        {
            return;
        }

        var unit = Shell.Services.Settings.DisplayUnit;
        Scene.Scene.SetToolpaths(SceneBuilder.Toolpaths(current));
        Scene.Scene.SelectedToolpath = SelectedOperation?.Operation.Id;
        var cut = current.Sum(g => g.Toolpath.CutLength);
        var moves = current.Sum(g => g.Toolpath.MoveCount);
        var enabled = session.Setups.Sum(s => s.Operations.Count(o => o.Enabled));
        ToolpathSummary = current.Count == 0
            ? "No toolpaths generated yet."
            : $"{current.Count} of {enabled} toolpath{(enabled == 1 ? string.Empty : "s")} up to date: {moves} moves, {Length(cut, unit)} of cutting.";
        UpdateStatus();
        UpdateFeeds(session, unit);
        await UpdateSimulationAsync(session, current, simulation, stale, unit);
        UpdateFiles(session, files);
    }

    private void UpdateStatus()
    {
        if (pipeline is not { } p || workspace.Session is not { } session || !ReferenceEquals(session, pipelineSession))
        {
            return;
        }

        var unit = Shell.Services.Settings.DisplayUnit;
        var warnings = p.FeedPlans.SelectMany(g => g.Plan.Operations)
            .ToDictionary(r => r.Request.OperationId, r => r.Warnings.Where(w => w.Level != FeedWarningLevel.Info).Select(w => w.Message));
        foreach (var row in Operations)
        {
            var op = row.Operation;
            var status = new List<string>();
            var notes = new List<string>();
            if (!op.Enabled)
            {
                status.Add("Disabled");
            }
            else if (p.GetToolpath(op.Id) is { } g)
            {
                status.Add($"{g.Toolpath.MoveCount} moves, {Length(g.Toolpath.CutLength, unit)} cut");
                notes.AddRange(g.Toolpath.Warnings);
            }
            else
            {
                status.Add("Toolpath not generated");
            }

            if (op.Conditions is { } c)
            {
                status.Add(string.Create(CultureInfo.InvariantCulture, $"S{c.Rpm:0} F{c.FeedRate:0}"));
            }

            if (warnings.TryGetValue(op.Id, out var feedWarnings))
            {
                notes.AddRange(feedWarnings);
            }

            row.Status = string.Join(", ", status);
            row.Warning = string.Join(" ", notes);
        }
    }

    private void UpdateFeeds(ProjectSession session, LengthUnit unit)
    {
        Feeds.Clear();
        var plans = pipeline?.FeedPlans.SelectMany(g => g.Plan.Operations).ToDictionary(r => r.Request.OperationId) ?? [];
        foreach (var setup in session.Setups)
        {
            foreach (var op in setup.Operations.Where(o => o.Enabled))
            {
                if (op.Conditions is not { } c)
                {
                    Feeds.Add(new FeedRow(op.Name, "Not planned yet.", string.Empty, string.Empty));
                    continue;
                }

                var detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{c.Rpm:0} rpm, feed {Rate(c.FeedRate, unit)}, plunge {Rate(c.PlungeRate, unit)}, chip load {Length(c.ChipLoad, unit)}, depth {Length(c.AxialDepth, unit)} x width {Length(c.RadialWidth, unit)}, {c.PowerW:0} W");
                var limit = string.Empty;
                var warnings = string.Empty;
                if (plans.TryGetValue(op.Id, out var result))
                {
                    var e = result.Evaluation;
                    limit = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Limited by {Limit(e.LimitedBy)}; power {e.PowerW:0} of {e.AvailablePowerW:0} W, deflection {e.Deflection * 1000:0.#} of {e.AllowedDeflection * 1000:0.#} µm, removal {e.MaterialRemovalRate / 1000:0.##} cm³/min");
                    warnings = string.Join(" ", result.Warnings.Where(w => w.Level != FeedWarningLevel.Info).Select(w => w.Message));
                }

                Feeds.Add(new FeedRow($"{setup.Name} / {op.Name}", detail, limit, warnings));
            }
        }
    }

    private static string Limit(FeedLimit limit) => limit switch
    {
        FeedLimit.ChipLoad => "chip load",
        FeedLimit.Power => "spindle power",
        FeedLimit.Deflection => "tool deflection",
        FeedLimit.ToolStrength => "tool strength",
        FeedLimit.MachineFeed => "machine feed rate",
        _ => limit.ToString(),
    };

    private async Task UpdateSimulationAsync(ProjectSession session, IReadOnlyList<GeneratedToolpath> current, SimulationResult? simulation, bool stale, LengthUnit unit)
    {
        SimulationIsStale = simulation is not null && stale;
        Issues.Clear();
        if (simulation is null)
        {
            SimulationSummary = string.Empty;
            DeviationSummary = string.Empty;
            BlocksOutput = false;
            Playback.Clear();
            shownSimulation = null;
            return;
        }

        var report = simulation.Report;
        SimulationSummary = DescribeReport(report);
        BlocksOutput = report.BlocksOutput;
        DeviationSummary = report.Deviation is { } d
            ? $"Deviation from the part: max gouge {Length(d.MaxGouge, unit)}, max leftover {Length(d.MaxLeftover, unit)}, mean leftover {Length(d.MeanLeftover, unit)}."
            : string.Empty;
        foreach (var issue in report.Issues.OrderByDescending(i => i.Severity))
        {
            var step = issue.StepId is { } id ? report.Steps.FindIndex(s => s.StepId == id) : -1;
            var where = step < 0 ? string.Empty : $"{report.Steps[step].Name}, moves {issue.FirstMove + 1}-{issue.LastMove + 1}";
            if (issue.Body is { Length: > 0 } body)
            {
                where = where.Length == 0 ? body : $"{where}, {body}";
            }

            Issues.Add(new IssueRow(issue, step, $"{issue.Severity}: {Kind(issue.Kind)}", where.Length == 0 ? issue.Message : $"{issue.Message} ({where})"));
        }

        if (stale)
        {
            // Step order and tool paths no longer match the simulation.
            Playback.Clear();
            shownSimulation = null;
        }
        else if (!ReferenceEquals(shownSimulation, simulation) && pipeline is { } p)
        {
            shownSimulation = simulation;
            await Playback.LoadAsync(p, simulation, current, [.. session.Tools], session.Project.Tolerances.FinishingTolerance);
        }
    }

    private static string Kind(IssueKind kind) => kind switch
    {
        IssueKind.Gouge => "gouge",
        IssueKind.StockCollision => "holder or shank hits the stock",
        IssueKind.RapidCollision => "rapid move through material",
        IssueKind.PartCollision => "collision with the part",
        IssueKind.KeepOutCollision => "keep-out zone hit",
        IssueKind.TravelLimit => "outside machine travel",
        IssueKind.StockModel => "stock model",
        IssueKind.MissingData => "missing data",
        IssueKind.FeedReduced => "feed reduced",
        _ => kind.ToString(),
    };

    private void UpdateFiles(ProjectSession session, IReadOnlyList<GcodeFile> files)
    {
        var keep = SelectedFile?.File.Path;
        Files.Clear();
        foreach (var file in files)
        {
            var tool = file.ToolId is { } id ? session.FindTool(id)?.Name : null;
            var setup = session.Setups.FirstOrDefault(s => s.Id == file.SetupId)?.Name;
            var rpm = file.Rpm is { } r ? string.Create(CultureInfo.InvariantCulture, $", {r:0} rpm") : string.Empty;
            var detail = string.Create(
                CultureInfo.InvariantCulture,
                $"{setup ?? "Setup"}, {tool ?? "tool"}, {file.LineCount} lines, about {PlaybackViewModel.Clock(file.EstimatedSeconds)}{rpm}");
            Files.Add(new GcodeRow(file, file.Path, detail));
        }

        SelectedFile = Files.FirstOrDefault(f => f.File.Path == keep) ?? Files.FirstOrDefault();
    }

    private static string Length(double mm, LengthUnit unit) => $"{NumberField.FormatLength(mm, unit)} {NumberField.Abbreviation(unit)}";

    private static string Rate(double mmPerMinute, LengthUnit unit) => $"{NumberField.FormatLength(mmPerMinute, unit)} {NumberField.Abbreviation(unit)}/min";

    partial void OnSelectedFileChanged(GcodeRow? value) => _ = LoadPreviewAsync(value);

    private async Task LoadPreviewAsync(GcodeRow? row)
    {
        if (row is null || workspace.Session is not { } session)
        {
            Preview = string.Empty;
            return;
        }

        var path = PartGeometry.Resolve(session.Folder, row.File.Path);
        string? text = null;
        await Shell.RunAsync(async () => text = await Task.Run(() =>
        {
            var lines = File.ReadLines(path).Take(PreviewLines + 1).ToList();
            var more = lines.Count > PreviewLines ? $"{Environment.NewLine}... ({row.File.LineCount - PreviewLines} more lines)" : string.Empty;
            return string.Join(Environment.NewLine, lines.Take(PreviewLines)) + more;
        }));
        if (ReferenceEquals(SelectedFile, row))
        {
            Preview = text ?? string.Empty;
        }
    }

    [RelayCommand]
    private void ShowIssue(IssueRow row)
    {
        if (row.Step >= 0)
        {
            Playback.ShowDeviation = false;
            Playback.SeekTo(row.Step, Math.Max(row.Issue.FirstMove, 0));
        }

        if (row.Issue.Location is { } location)
        {
            Scene.Scene.Focus(location, 15);
        }
    }

    [RelayCommand]
    private async Task SaveFile(GcodeRow row)
    {
        if (workspace.Session is not { } session)
        {
            return;
        }

        var source = PartGeometry.Resolve(session.Folder, row.File.Path);
        if (await Shell.Files.SaveFileAsync("Save G-code", Path.GetFileName(source), [Gcode, FileType.Any]) is { } target
            && await Shell.RunAsync(() => Task.Run(() => File.Copy(source, target, overwrite: true))))
        {
            Shell.ShowMessage($"Saved {Path.GetFileName(target)}.");
        }
    }

    [RelayCommand]
    private async Task ExportFiles()
    {
        if (workspace.Session is not { } session || Files.Count == 0)
        {
            Shell.ShowMessage("Post the project first.", isError: true);
            return;
        }

        var sources = Files.Select(f => PartGeometry.Resolve(session.Folder, f.File.Path)).ToList();
        if (await Shell.Files.PickFolderAsync("Copy the G-code files to") is { } folder
            && await Shell.RunAsync(() => Task.Run(() =>
            {
                foreach (var source in sources)
                {
                    File.Copy(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);
                }
            })))
        {
            Shell.ShowMessage($"Copied {sources.Count} file{(sources.Count == 1 ? string.Empty : "s")} to {folder}.");
        }
    }

    // ----- Session and compute changes -----

    private void Reset()
    {
        cancel?.Cancel();
        Playback.Clear();
        shownSimulation = null;
        pipeline?.Dispose();
        pipeline = null;
        pipelineSession = null;
        SelectedSetup = null;
        Setups.Clear();
        Operations.Clear();
        Tools.Clear();
        Feeds.Clear();
        Issues.Clear();
        Files.Clear();
        SetupEditor = null;
        OperationEditor = null;
        ToolpathSummary = string.Empty;
        SimulationSummary = string.Empty;
        DeviationSummary = string.Empty;
        SimulationIsStale = false;
        BlocksOutput = false;
        Scene.Clear();
        Scene.Scene.SetToolpaths([]);
    }

    private void OnComputeResetting(object? sender, EventArgs e)
    {
        // The playback stock lives on the accelerator being reset; the pipeline drops its simulation too.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Playback.Clear();
            shownSimulation = null;
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                Playback.Clear();
                shownSimulation = null;
            });
        }
    }
}
