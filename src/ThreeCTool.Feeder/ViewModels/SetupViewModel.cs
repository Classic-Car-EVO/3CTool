using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;

namespace ThreeCTool.Feeder.ViewModels;

public sealed class PairingItemViewModel(PairingInfo info)
{
    public string Id => info.PairingId;

    public string Name => info.ClientName;

    public string Details => "paired " + (info.PairedAt is null ? "-" : Display.LocalTime(info.PairedAt.ToDateTimeOffset()))
        + (info.LastSeenAt is null ? string.Empty : ", last seen " + Display.LocalTime(info.LastSeenAt.ToDateTimeOffset()));
}

/// <summary>One editable setting: tapping it opens the on-screen keyboard.</summary>
public sealed partial class SettingViewModel(string label, string unit, bool numeric, Func<FeederSettings, string> get, Action<FeederSettings, string> set)
    : ViewModelBase
{
    public string Label { get; } = label;

    public string Unit { get; } = unit;

    public bool Numeric { get; } = numeric;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    public void Load(FeederSettings settings) => Value = get(settings);

    public void Store(FeederSettings settings) => set(settings, Value);
}

/// <summary>Settings, pairing with desktops, feeder and controller information.</summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly IFeederShell shell;
    private int pairingCount = -1;

    public SetupViewModel(IFeederShell shell)
    {
        this.shell = shell;
        General =
        [
            Text("Feeder name", s => s.Name, (s, v) => s.Name = v),
        ];
        Serial =
        [
            Number("Baud rate", "", s => s.BaudRate, (s, v) => s.BaudRate = (int)v),
        ];
        Tcp =
        [
            Text("Controller host", s => s.Host, (s, v) => s.Host = v),
            Number("TCP port", "", s => s.TcpPort, (s, v) => s.TcpPort = (int)v),
        ];
        Machine =
        [
            Number("Safe Z (machine)", "mm", s => s.SafeZMachine, (s, v) => s.SafeZMachine = v),
            Number("Touch plate thickness", "mm", s => s.TouchPlateThickness, (s, v) => s.TouchPlateThickness = v),
            Number("Probe max travel", "mm", s => s.ProbeMaxTravel, (s, v) => s.ProbeMaxTravel = v),
            Number("Probe feed", "mm/min", s => s.ProbeFeed, (s, v) => s.ProbeFeed = v),
            Number("Recovery margin", "mm", s => s.RecoveryMarginMm, (s, v) => s.RecoveryMarginMm = v),
            Number("Re-entry plunge feed", "mm/min", s => s.PlungeFeed, (s, v) => s.PlungeFeed = v),
        ];
    }

    public IReadOnlyList<SettingViewModel> General { get; }

    public IReadOnlyList<SettingViewModel> Serial { get; }

    public IReadOnlyList<SettingViewModel> Tcp { get; }

    public IReadOnlyList<SettingViewModel> Machine { get; }

    public ObservableCollection<string> SerialPorts { get; } = [];

    public ObservableCollection<PairingItemViewModel> Pairings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial))]
    public partial bool IsTcp { get; set; }

    public bool IsSerial => !IsTcp;

    [ObservableProperty]
    public partial string? SerialPort { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool Dirty { get; private set; }

    [ObservableProperty]
    public partial string PairingCode { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PairingFingerprint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PairingExpires { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool PairingActive { get; private set; }

    [ObservableProperty]
    public partial string FeederName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FeederId { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Version { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Fingerprint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Controller { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfigText { get; private set; } = string.Empty;

    private DateTimeOffset? PairingExpiresAt { get; set; }

    public async Task LoadAsync()
    {
        await LoadInfoAsync();
        await LoadSettingsAsync();
        await LoadPairingsAsync();
    }

    public void Apply(FeederStatus? status)
    {
        Controller = status is null ? string.Empty
            : string.Join(" · ", new[] { status.Controller, status.Firmware, status.Connection.ToString() }.Where(s => s.Length > 0));
        if (status is null)
        {
            return;
        }

        if (PairingActive && !status.PairingActive)
        {
            ClearPairingCode();
        }

        if (pairingCount != status.PairingCount)
        {
            var first = pairingCount < 0;
            pairingCount = status.PairingCount;
            if (!first)
            {
                _ = LoadPairingsAsync();
            }
        }

        UpdateCountdown();
    }

    [RelayCommand]
    private async Task EditAsync(SettingViewModel setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var text = await shell.RequestTextAsync(setting.Label + (setting.Unit.Length > 0 ? $" ({setting.Unit})" : string.Empty), setting.Value, setting.Numeric);
        if (text is null)
        {
            return;
        }

        text = text.Trim();
        if (setting.Numeric && !Display.TryParse(text, out _))
        {
            shell.ShowMessage($"'{text}' is not a number.");
            return;
        }

        if (text != setting.Value)
        {
            setting.Value = text;
            Dirty = true;
        }
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        SerialPortList? list = null;
        if (await shell.RunAsync(async () => list = await shell.Admin.ListSerialPortsAsync(new())) && list is not null)
        {
            var selected = SerialPort;
            SerialPorts.Clear();
            foreach (var port in list.Ports.Append(selected).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal))
            {
                SerialPorts.Add(port!);
            }

            SerialPort = selected;
        }
    }

    [RelayCommand(CanExecute = nameof(Dirty))]
    private async Task SaveAsync()
    {
        var settings = new FeederSettings
        {
            Transport = IsTcp ? TransportKind.Tcp : TransportKind.Serial,
            SerialPort = SerialPort ?? string.Empty,
        };
        foreach (var setting in AllSettings())
        {
            setting.Store(settings);
        }

        FeederSettings? saved = null;
        if (await shell.RunAsync(async () => saved = await shell.Admin.UpdateSettingsAsync(settings)) && saved is not null)
        {
            Show(saved);
            shell.ShowMessage("Settings saved.");
        }
    }

    [RelayCommand(CanExecute = nameof(Dirty))]
    private Task RevertAsync() => LoadSettingsAsync();

    [RelayCommand]
    private async Task StartPairingAsync()
    {
        PairingCode? code = null;
        if (await shell.RunAsync(async () => code = await shell.Admin.StartPairingAsync(new())) && code is not null)
        {
            PairingCode = code.Code;
            PairingFingerprint = CertificateFingerprint.Format(code.CertificateFingerprint);
            PairingExpiresAt = code.ExpiresAt?.ToDateTimeOffset();
            PairingActive = true;
            UpdateCountdown();
        }
    }

    [RelayCommand]
    private async Task CancelPairingAsync()
    {
        if (await shell.RunAsync(async () => await shell.Admin.CancelPairingAsync(new())))
        {
            ClearPairingCode();
        }
    }

    [RelayCommand]
    private async Task RevokeAsync(PairingItemViewModel pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        if (await shell.ConfirmAsync("Revoke pairing", $"Revoke '{pairing.Name}'? That computer can no longer control this feeder until paired again.", "Revoke", danger: true)
            && await shell.RunAsync(async () => await shell.Admin.RevokePairingAsync(new PairingRef { PairingId = pairing.Id })))
        {
            await LoadPairingsAsync();
        }
    }

    [RelayCommand]
    private async Task ReadConfigAsync()
    {
        ConfigText = "Reading...";
        ControllerConfigInfo? config = null;
        if (await shell.RunAsync(async () => config = await shell.Client.ReadControllerConfigAsync(new())) && config is not null)
        {
            var lines = new List<string>
            {
                "Firmware: " + (config.FirmwareVersion.Length > 0 ? config.FirmwareVersion : "unknown"),
                "Config hash: " + (config.ConfigHash.Length > 0 ? config.ConfigHash[..Math.Min(16, config.ConfigHash.Length)] : "-"),
                string.Create(CultureInfo.InvariantCulture, $"Config: {config.ConfigText.Split('\n').Length} lines"),
            };
            lines.AddRange(config.Warnings.Select(w => "Warning: " + w));
            ConfigText = string.Join("\n", lines);
        }
        else
        {
            ConfigText = string.Empty;
        }
    }

    partial void OnIsTcpChanged(bool value) => Dirty = true;

    partial void OnSerialPortChanged(string? value) => Dirty = true;

    private static SettingViewModel Text(string label, Func<FeederSettings, string> get, Action<FeederSettings, string> set) =>
        new(label, string.Empty, false, get, set);

    private static SettingViewModel Number(string label, string unit, Func<FeederSettings, double> get, Action<FeederSettings, double> set) =>
        new(label, unit, true, s => Display.Number(get(s)), (s, v) => set(s, Display.TryParse(v, out var d) ? d : 0));

    private IEnumerable<SettingViewModel> AllSettings() => General.Concat(Serial).Concat(Tcp).Concat(Machine);

    private async Task LoadInfoAsync()
    {
        FeederInfo? info = null;
        if (await shell.RunAsync(async () => info = await shell.Client.GetInfoAsync(new())) && info is not null)
        {
            FeederName = info.Name;
            FeederId = info.FeederId;
            Version = info.Version;
            Fingerprint = CertificateFingerprint.Format(info.CertificateFingerprint);
        }
    }

    private async Task LoadSettingsAsync()
    {
        FeederSettings? settings = null;
        if (await shell.RunAsync(async () => settings = await shell.Admin.GetSettingsAsync(new())) && settings is not null)
        {
            Show(settings);
            await RefreshPortsAsync();
            Dirty = false;
        }
    }

    private async Task LoadPairingsAsync()
    {
        PairingList? list = null;
        if (await shell.RunAsync(async () => list = await shell.Admin.ListPairingsAsync(new())) && list is not null)
        {
            Pairings.Clear();
            foreach (var pairing in list.Pairings)
            {
                Pairings.Add(new PairingItemViewModel(pairing));
            }
        }
    }

    private void Show(FeederSettings settings)
    {
        foreach (var setting in AllSettings())
        {
            setting.Load(settings);
        }

        IsTcp = settings.Transport == TransportKind.Tcp;
        SerialPort = settings.SerialPort;
        FeederName = settings.Name;
        Dirty = false;
    }

    private void ClearPairingCode()
    {
        PairingActive = false;
        PairingCode = PairingFingerprint = PairingExpires = string.Empty;
        PairingExpiresAt = null;
    }

    private void UpdateCountdown()
    {
        if (PairingActive && PairingExpiresAt is { } expires)
        {
            var left = expires - DateTimeOffset.UtcNow;
            PairingExpires = left > TimeSpan.Zero ? "expires in " + Display.Duration(left.TotalSeconds) : "expired";
        }
    }
}
