using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core.Machines;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;
using ThreeCTool.Streaming.Config;
using ThreeCTool.Streaming.Protocol;

using FeederShell = ThreeCTool.Feeder.ViewModels.MainViewModel;
using FeederTab = ThreeCTool.Feeder.ViewModels.FeederTab;
using FeederTransport = ThreeCTool.Feeder.Contracts.TransportKind;
using MachineTransport = ThreeCTool.Core.Machines.TransportKind;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A way to reach a feeder: the embedded direct connection or a paired feeder.</summary>
public sealed class MachineConnection
{
    private MachineConnection(PairedFeeder? feeder, string name, string details)
    {
        Feeder = feeder;
        Name = name;
        Details = details;
    }

    /// <summary>Null for the direct connection.</summary>
    public PairedFeeder? Feeder { get; }

    public bool IsDirect => Feeder is null;

    public string Name { get; }

    public string Details { get; }

    public static MachineConnection Direct() => new(null, "Direct (USB / TCP)", "Feeder service inside 3C Tool, controller on this computer");

    public static MachineConnection Paired(PairedFeeder feeder)
    {
        ArgumentNullException.ThrowIfNull(feeder);
        var synced = feeder.LastSyncAt is { } at ? ", feedback synced " + Display.LocalTime(at) : string.Empty;
        return new(feeder, feeder.Name, $"{feeder.Address} · paired {Display.LocalTime(feeder.PairedAt)}{synced}");
    }
}

/// <summary>A run in the feeder's history.</summary>
public sealed record RunRow(RunInfo Info, string Title, string Detail)
{
    public static RunRow From(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var started = run.StartedAt is null ? "-" : Display.LocalTime(run.StartedAt.ToDateTimeOffset());
        var ended = run.EndedAt is null ? "running" : Display.LocalTime(run.EndedAt.ToDateTimeOffset());
        var detail = string.Create(CultureInfo.InvariantCulture, $"{started} to {ended}, last line {run.LastLine}{(run.HasFeedback ? ", feedback given" : string.Empty)}");
        return new RunRow(run, $"{run.JobName} · {run.State}", detail);
    }
}

/// <summary>
/// The machine page: connects to the direct feeder or a paired one and hosts the feeder's own view models (jobs, run,
/// jog, setup, prompts), uploads the project's posted files, imports the controller configuration into the linked
/// machine profile, syncs run feedback into the library and shows the run history.
/// </summary>
public sealed partial class MachineViewModel : PageViewModel, IDisposable
{
    private readonly ProjectWorkspace workspace;
    private DirectFeeder? direct;
    private bool loadingLink;

    public MachineViewModel(IShell shell, ProjectWorkspace workspace)
        : base(shell, "Machine")
    {
        this.workspace = workspace;
        PairName = Environment.MachineName;
    }

    /// <summary>Creates the embedded feeder for a data directory (replaced in tests for fast timings).</summary>
    internal Func<string, DirectFeeder> CreateDirectFeeder { get; set; } = directory => new DirectFeeder(directory);

    public ObservableCollection<MachineConnection> Connections { get; } = [];

    public ObservableCollection<MachineProfile> Machines { get; } = [];

    public ObservableCollection<GcodeRow> Files { get; } = [];

    public ObservableCollection<RunRow> Runs { get; } = [];

    public ObservableCollection<string> RunLog { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(ForgetCommand))]
    public partial MachineConnection? SelectedConnection { get; set; }

    /// <summary>Machine profile the selected connection drives.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportConfigCommand), nameof(ApplyMachineSettingsCommand))]
    public partial MachineProfile? LinkedMachine { get; set; }

    /// <summary>The connected feeder's view models; null when disconnected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDirectConnected))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand), nameof(UploadCommand), nameof(SyncFeedbackCommand), nameof(ImportConfigCommand),
        nameof(ApplyMachineSettingsCommand), nameof(RefreshRunsCommand))]
    public partial FeederShell? Feeder { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirectConnected))]
    public partial MachineConnection? Connected { get; private set; }

    public bool IsConnected => Feeder is not null;

    public bool IsDirectConnected => Feeder is not null && Connected?.IsDirect == true;

    [ObservableProperty]
    public partial bool IsPairing { get; set; }

    [ObservableProperty]
    public partial string PairAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PairCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PairName { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    public partial GcodeRow? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string FilesText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    public partial bool IsUploading { get; private set; }

    /// <summary>Upload progress in percent.</summary>
    [ObservableProperty]
    public partial double UploadProgress { get; private set; }

    [ObservableProperty]
    public partial RunRow? SelectedRun { get; set; }

    [ObservableProperty]
    public partial string SyncSummary { get; private set; } = string.Empty;

    public override async Task ShowAsync()
    {
        await LoadConnectionsAsync();
        await LoadFilesAsync();
    }

    public void Dispose()
    {
        var feeder = Feeder;
        Feeder = null;
        feeder?.Dispose();
        direct?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        direct = null;
    }

    private Guid? LinkedId(MachineConnection connection) =>
        connection.IsDirect ? Shell.Services.Settings.DirectMachineProfileId : connection.Feeder!.MachineProfileId;

    private async Task LoadConnectionsAsync()
    {
        IReadOnlyList<PairedFeeder> paired = [];
        IReadOnlyList<MachineProfile> machines = [];
        if (!await Shell.RunAsync(async () =>
        {
            paired = await Shell.Services.Feeders.ListAsync();
            machines = await Shell.Services.Library.ListMachinesAsync();
        }))
        {
            return;
        }

        var keep = SelectedConnection;
        Machines.Clear();
        foreach (var machine in machines)
        {
            Machines.Add(machine);
        }

        Connections.Clear();
        Connections.Add(MachineConnection.Direct());
        foreach (var feeder in paired)
        {
            Connections.Add(MachineConnection.Paired(feeder));
        }

        SelectedConnection = Connections.FirstOrDefault(c => c.Feeder?.Id == keep?.Feeder?.Id && c.IsDirect == keep?.IsDirect) ?? Connections[0];
        ShowLink();
    }

    private async Task LoadFilesAsync()
    {
        var rows = new List<GcodeRow>();
        var text = "Open a project and post it on the Machining page to send its G-code here.";
        if (workspace.HasProject)
        {
            await workspace.EditAsync(async session =>
            {
                foreach (var file in await session.ListGcodeFilesAsync())
                {
                    var tool = file.ToolId is { } id ? session.FindTool(id)?.Name : null;
                    var setup = session.Setups.FirstOrDefault(s => s.Id == file.SetupId)?.Name;
                    rows.Add(new GcodeRow(file, file.Path, string.Create(
                        CultureInfo.InvariantCulture,
                        $"{setup ?? "Setup"}, {tool ?? "tool"}, {file.LineCount} lines, about {PlaybackViewModel.Clock(file.EstimatedSeconds)}")));
                }

                text = rows.Count == 0
                    ? $"{session.Project.Name} has no posted files yet; post it on the Machining page."
                    : $"{session.Project.Name}, posted for {session.Machine.Name}";
            });
        }

        var keep = SelectedFile?.File.Path;
        Files.Clear();
        foreach (var row in rows)
        {
            Files.Add(row);
        }

        FilesText = text;
        SelectedFile = Files.FirstOrDefault(f => f.File.Path == keep) ?? Files.FirstOrDefault();
    }

    partial void OnSelectedConnectionChanged(MachineConnection? value) => ShowLink();

    private void ShowLink()
    {
        loadingLink = true;
        try
        {
            LinkedMachine = SelectedConnection is { } connection && LinkedId(connection) is { } id ? Machines.FirstOrDefault(m => m.Id == id) : null;
        }
        finally
        {
            loadingLink = false;
        }
    }

    partial void OnLinkedMachineChanged(MachineProfile? value)
    {
        if (!loadingLink && SelectedConnection is { } connection)
        {
            _ = SaveLinkAsync(connection, value?.Id);
        }
    }

    private Task<bool> SaveLinkAsync(MachineConnection connection, Guid? machineId)
    {
        if (connection.Feeder is { } feeder)
        {
            feeder.MachineProfileId = machineId;
            return Shell.RunAsync(() => Shell.Services.Feeders.SetMachineAsync(feeder.Id, machineId));
        }

        var settings = Shell.Services.Settings.Clone();
        settings.DirectMachineProfileId = machineId;
        return Shell.RunAsync(() => Shell.Services.SaveSettingsAsync(settings));
    }

    // ----- Connection -----

    private bool CanConnect() => SelectedConnection is not null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedConnection is not { } target || (Feeder is not null && !await DisconnectCoreAsync()))
        {
            return;
        }

        await Shell.RunAsync(async () =>
        {
            FeederConnection connection;
            if (target.Feeder is { } paired)
            {
                connection = new FeederConnection(FeederChannels.ForPairedFeeder(paired.Address, paired.CertificateFingerprint, paired.Token));
            }
            else
            {
                direct ??= CreateDirectFeeder(Shell.Services.Paths.DirectFeederDirectory);
                await direct.StartAsync();
                connection = new FeederConnection(direct.SocketPath);
            }

            var feeder = new FeederShell(connection, Shell.Services.Settings.Renderer, isLocal: target.IsDirect);
            feeder.PropertyChanged += OnFeederPropertyChanged;
            Connected = target;
            Feeder = feeder;
            Runs.Clear();
            RunLog.Clear();
            SyncSummary = string.Empty;
            feeder.Start();
        });
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task<bool> DisconnectAsync() => DisconnectCoreAsync();

    /// <summary>Disconnects (stopping the direct feeder); false when the user keeps a running direct job.</summary>
    private async Task<bool> DisconnectCoreAsync()
    {
        if (Feeder is not { } feeder)
        {
            return true;
        }

        if (Connected?.IsDirect == true && feeder.IsRunning
            && !await Shell.ConfirmAsync("Disconnect", "A job is running on the direct connection. Disconnecting stops the feeder and interrupts the job.", "Disconnect", danger: true))
        {
            return false;
        }

        feeder.PropertyChanged -= OnFeederPropertyChanged;
        Feeder = null;
        Connected = null;
        feeder.Dispose();
        if (direct is { } running)
        {
            await Shell.RunAsync(running.StopAsync);
        }

        return true;
    }

    private void OnFeederPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is FeederShell feeder && feeder == Feeder
            && ((e.PropertyName == nameof(FeederShell.IsConnected) && feeder.IsConnected)
                || (e.PropertyName == nameof(FeederShell.IsRunning) && !feeder.IsRunning)))
        {
            _ = RefreshRunsAsync();
        }
    }

    // ----- Pairing -----

    [RelayCommand]
    private void StartPairing()
    {
        PairCode = string.Empty;
        IsPairing = true;
    }

    [RelayCommand]
    private void CancelPairing() => IsPairing = false;

    [RelayCommand]
    private async Task PairAsync()
    {
        if (string.IsNullOrWhiteSpace(PairAddress) || string.IsNullOrWhiteSpace(PairCode) || string.IsNullOrWhiteSpace(PairName))
        {
            Shell.ShowMessage("Enter the feeder's address, the code shown on its screen and a name for this computer.", isError: true);
            return;
        }

        // The fingerprint callback runs on a pool thread; the confirmation is shown on this (UI) context.
        var scheduler = SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();
        var declined = false;
        Task<bool> ConfirmFingerprint(FeederInfo info, string fingerprint) => Task.Factory.StartNew(
            async () =>
            {
                var ok = await Shell.ConfirmAsync(
                    "Check the certificate",
                    $"Pair with '{info.Name}'? Compare this fingerprint with the one on the feeder's screen:\n\n{CertificateFingerprint.Format(fingerprint)}",
                    "They match");
                declined = !ok;
                return ok;
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        PairedFeeder? saved = null;
        var address = PairAddress.Trim();
        var code = PairCode.Trim();
        var name = PairName.Trim();
        try
        {
            if (!await Shell.RunAsync(async () =>
            {
                var result = await FeederPairing.PairAsync(address, code, name, ConfirmFingerprint);
                saved = await Shell.Services.Feeders.SaveAsync(result);
            }) || saved is null)
            {
                return;
            }
        }
        finally
        {
            if (declined)
            {
                Shell.ShowMessage("Pairing cancelled.");
            }
        }

        IsPairing = false;
        PairCode = string.Empty;
        await LoadConnectionsAsync();
        SelectedConnection = Connections.FirstOrDefault(c => c.Feeder?.Id == saved.Id) ?? SelectedConnection;
        Shell.ShowMessage($"Paired with {saved.Name}.");
    }

    private bool CanForget() => SelectedConnection is { IsDirect: false };

    [RelayCommand(CanExecute = nameof(CanForget))]
    private async Task ForgetAsync()
    {
        if (SelectedConnection is not { Feeder: { } feeder }
            || !await Shell.ConfirmAsync("Forget feeder", $"Forget '{feeder.Name}'? Pair again to reconnect. Revoke this computer on the feeder's Setup page as well.", "Forget", danger: true))
        {
            return;
        }

        if (Connected?.Feeder?.Id == feeder.Id && !await DisconnectCoreAsync())
        {
            return;
        }

        if (await Shell.RunAsync(() => Shell.Services.Feeders.RemoveAsync(feeder.Id)))
        {
            await LoadConnectionsAsync();
        }
    }

    // ----- Jobs -----

    private bool CanUpload() => Feeder is not null && SelectedFile is not null && !IsUploading;

    /// <summary>Sends the selected posted file to the connected feeder and selects it in the job list.</summary>
    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        if (Feeder is not { } feeder || Connected is not { } target || SelectedFile is not { } row)
        {
            return;
        }

        UploadMetadata? metadata = null;
        string? path = null;
        string projectMachine = string.Empty;
        Guid projectMachineId = default;
        if (!await workspace.EditAsync(session =>
        {
            metadata = JobUploader.Describe(session, row.File);
            path = PartGeometry.Resolve(session.Folder, row.File.Path);
            projectMachineId = session.Project.MachineProfileId;
            projectMachine = session.Machine.Name;
            return Task.CompletedTask;
        }) || metadata is null || path is null)
        {
            return;
        }

        if (LinkedId(target) is { } linked && linked != projectMachineId)
        {
            var name = Machines.FirstOrDefault(m => m.Id == linked)?.Name ?? "another machine";
            if (!await Shell.ConfirmAsync("Different machine", $"The project was posted for '{projectMachine}', but {target.Name} drives '{name}'. Upload anyway?", "Upload"))
            {
                return;
            }
        }

        IsUploading = true;
        UploadProgress = 0;
        try
        {
            JobInfo? job = null;
            var progress = new Progress<double>(p => UploadProgress = p * 100);
            if (await Shell.RunAsync(async () => job = await JobUploader.UploadAsync(feeder.Client, path, row.File.Sha256, metadata, progress)) && job is not null)
            {
                UploadProgress = 100;
                await feeder.Jobs.RefreshAsync();
                feeder.Jobs.Selected = feeder.Jobs.Jobs.FirstOrDefault(j => j.Id == job.JobId) ?? feeder.Jobs.Selected;
                feeder.ShowTab(FeederTab.Jobs);
                Shell.ShowMessage($"Sent {job.Header.Name} to {target.Name}.");
            }
        }
        finally
        {
            IsUploading = false;
        }
    }

    [RelayCommand]
    private Task RefreshFilesAsync() => LoadFilesAsync();

    // ----- Learning -----

    /// <summary>Imports the feeder's run feedback into the library and updates the feed corrections.</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task SyncFeedbackAsync()
    {
        if (Feeder is not { } feeder || Connected is not { } target)
        {
            return;
        }

        var sync = new FeederLearningSync(Shell.Services.Feeders, Shell.Services.Time);
        FeederSyncResult? result = null;
        if (!await Shell.RunAsync(async () => result = target.Feeder is { } paired ? await sync.SyncAsync(paired.Id) : await sync.SyncAsync(feeder.Client))
            || result is null)
        {
            return;
        }

        var lines = new List<string>
        {
            result.Imported + result.AlreadyImported == 0
                ? "No new run feedback."
                : string.Create(CultureInfo.InvariantCulture, $"Imported {result.Imported} run(s), {result.Learned} used for feed learning{(result.AlreadyImported > 0 ? $", {result.AlreadyImported} already in the library" : string.Empty)}."),
        };
        lines.AddRange(result.Notes);
        SyncSummary = string.Join("\n", lines);
        Shell.ShowMessage(lines[0]);
        if (target.Feeder is not null)
        {
            await LoadConnectionsAsync();
        }
    }

    // ----- Machine profile -----

    private bool CanUseLinkedMachine() => Feeder is not null && LinkedMachine is not null;

    /// <summary>Reads the controller's configuration and imports it into the linked machine profile.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLinkedMachine))]
    private async Task ImportConfigAsync()
    {
        if (Feeder is not { } feeder || LinkedMachine is not { } linked)
        {
            return;
        }

        ControllerConfigInfo? info = null;
        MachineProfile? machine = null;
        if (!await Shell.RunAsync(async () =>
        {
            info = await feeder.Client.ReadControllerConfigAsync(new());
            machine = await Shell.Services.Library.GetMachineAsync(linked.Id);
        }) || info is null || machine is null)
        {
            return;
        }

        var config = ControllerConfig.Parse(info.ConfigText.Split('\n').Select(l => l.TrimEnd('\r')));
        if (config.Axes().Count == 0)
        {
            Shell.ShowMessage("The controller returned a configuration without axes; nothing imported.", isError: true);
            return;
        }

        var changes = machine.Axes.Count > 0 ? ProfileImport.Compare(machine, config) : [];
        var summary = machine.Controller.ConfigHash == config.Hash ? "The profile already matches this configuration."
            : changes.Count == 0 ? $"{config.Axes().Count} axes will be imported."
            : $"{changes.Count} setting(s) differ:\n" + string.Join("\n", changes.Take(12).Select(c => "• " + c)) + (changes.Count > 12 ? "\n…" : string.Empty);
        var warnings = info.Warnings.Count > 0 ? "\n\nController warnings:\n" + string.Join("\n", info.Warnings.Select(w => "• " + w)) : string.Empty;
        if (!await Shell.ConfirmAsync("Import controller configuration", $"Import the controller's configuration into '{machine.Name}'? {summary}{warnings}", "Import"))
        {
            return;
        }

        ProfileImport.Apply(machine, config, FluidNcVersionInfo.TryParse(info.FirmwareVersion), Shell.Services.Time.GetUtcNow());
        if (await Shell.RunAsync(async () =>
        {
            await Shell.Services.Library.SaveMachineAsync(machine);
            await feeder.Client.SetImportedConfigAsync(new ImportedConfig { ConfigHash = config.Hash });
        }))
        {
            var index = Machines.IndexOf(linked);
            loadingLink = true;
            try
            {
                Machines[index] = machine;
                LinkedMachine = machine;
            }
            finally
            {
                loadingLink = false;
            }

            Shell.ShowMessage($"Imported {config.Axes().Count} axes into '{machine.Name}'. Projects using it pick up the change when updated from the library.");
        }
    }

    private bool CanApplyMachineSettings() => IsDirectConnected && LinkedMachine is not null;

    /// <summary>Copies the linked profile's controller connection, safe Z and probe settings to the direct feeder.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyMachineSettings))]
    private async Task ApplyMachineSettingsAsync()
    {
        if (Feeder is not { IsLocal: true } feeder || LinkedMachine is not { } machine)
        {
            return;
        }

        if (await Shell.RunAsync(async () =>
        {
            var settings = await feeder.Admin.GetSettingsAsync(new());
            var connection = machine.Connection;
            settings.Transport = connection.Transport == MachineTransport.Tcp ? FeederTransport.Tcp : FeederTransport.Serial;
            settings.SerialPort = connection.SerialPort;
            settings.BaudRate = connection.BaudRate;
            settings.Host = connection.Host;
            settings.TcpPort = connection.TcpPort;
            settings.SafeZMachine = machine.SafeZMachine;
            settings.TouchPlateThickness = machine.Probe.TouchPlateThickness;
            settings.ProbeMaxTravel = machine.Probe.MaxDistance;
            settings.ProbeFeed = machine.Probe.SeekFeed;
            await feeder.Admin.UpdateSettingsAsync(settings);
        }))
        {
            await feeder.Setup.LoadAsync();
            var where = machine.Connection.Transport == MachineTransport.Tcp
                ? string.Create(CultureInfo.InvariantCulture, $"{machine.Connection.Host}:{machine.Connection.TcpPort}")
                : machine.Connection.SerialPort;
            Shell.ShowMessage($"Direct connection set up from '{machine.Name}' ({where}).");
        }
    }

    // ----- Run history -----

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task RefreshRunsAsync()
    {
        if (Feeder is not { } feeder)
        {
            return;
        }

        RunList? list = null;
        if (await feeder.RunAsync(async () => list = await feeder.Client.ListRunsAsync(new ListRunsRequest())) && list is not null && feeder == Feeder)
        {
            var keep = SelectedRun?.Info.RunId;
            Runs.Clear();
            foreach (var run in list.Runs.OrderByDescending(r => r.StartedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue))
            {
                Runs.Add(RunRow.From(run));
            }

            SelectedRun = Runs.FirstOrDefault(r => r.Info.RunId == keep);
        }
    }

    partial void OnSelectedRunChanged(RunRow? value)
    {
        RunLog.Clear();
        if (value is not null)
        {
            _ = LoadRunLogAsync(value);
        }
    }

    private async Task LoadRunLogAsync(RunRow row)
    {
        if (Feeder is not { } feeder)
        {
            return;
        }

        RunLog? log = null;
        if (await feeder.RunAsync(async () => log = await feeder.Client.GetRunLogAsync(new RunRef { RunId = row.Info.RunId })) && log is not null && SelectedRun == row)
        {
            RunLog.Clear();
            foreach (var e in log.Events)
            {
                var at = e.At is null ? string.Empty : e.At.ToDateTimeOffset().ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ";
                var line = e.Line > 0 ? string.Create(CultureInfo.InvariantCulture, $" (line {e.Line})") : string.Empty;
                RunLog.Add($"{at}{e.Kind}{line}: {e.Message}");
            }
        }
    }
}
