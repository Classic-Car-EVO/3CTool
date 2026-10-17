using Google.Protobuf;

using ThreeCTool.Core.Machines;
using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Feeder.Service.Storage;

/// <summary>Operator-editable settings (touch UI Setup screen) and the feeder identity, persisted in the database.</summary>
public sealed class SettingsStore
{
    private const string SettingsKey = "settings";
    private const string FeederIdKey = "feeder_id";
    private const string ImportedConfigKey = "imported_config_hash";

    private readonly FeederDatabase database;
    private readonly Lock gate = new();
    private FeederSettings current;

    public SettingsStore(FeederDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        FeederId = database.GetMeta(FeederIdKey) ?? CreateFeederId(database);
        var json = database.GetMeta(SettingsKey);
        current = Normalize(json is null ? Defaults() : JsonParser.Default.Parse<FeederSettings>(json));
    }

    public event EventHandler<FeederSettings>? Changed;

    public string FeederId { get; }

    public FeederSettings Current
    {
        get
        {
            lock (gate)
            {
                return current.Clone();
            }
        }
    }

    /// <summary>Config hash of the controller config imported into the desktop machine profile (empty = never imported).</summary>
    public string? ImportedConfigHash
    {
        get => database.GetMeta(ImportedConfigKey);
        set => database.SetMeta(ImportedConfigKey, string.IsNullOrEmpty(value) ? null : value);
    }

    public FeederSettings Update(FeederSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings.Clone());
        Validate(normalized);
        lock (gate)
        {
            current = normalized;
            database.SetMeta(SettingsKey, JsonFormatter.Default.Format(normalized));
        }

        Changed?.Invoke(this, normalized.Clone());
        return normalized.Clone();
    }

    public ConnectionSettings ConnectionSettings()
    {
        var s = Current;
        return new ConnectionSettings
        {
            Transport = s.Transport == Contracts.TransportKind.Tcp ? Core.Machines.TransportKind.Tcp : Core.Machines.TransportKind.Serial,
            SerialPort = s.SerialPort,
            BaudRate = s.BaudRate,
            Host = s.Host,
            TcpPort = s.TcpPort,
        };
    }

    public static FeederSettings Defaults() => new()
    {
        Name = Environment.MachineName,
        Transport = Contracts.TransportKind.Serial,
        SerialPort = "/dev/ttyUSB0",
        BaudRate = 115200,
        Host = "fluidnc.local",
        TcpPort = 23,
        SafeZMachine = -5,
        TouchPlateThickness = 10,
        ProbeMaxTravel = 50,
        ProbeFeed = 100,
        RecoveryMarginMm = 1,
        PlungeFeed = 100,
    };

    private static FeederSettings Normalize(FeederSettings settings)
    {
        var defaults = Defaults();
        settings.Name = string.IsNullOrWhiteSpace(settings.Name) ? defaults.Name : settings.Name.Trim();
        settings.SerialPort = string.IsNullOrWhiteSpace(settings.SerialPort) ? defaults.SerialPort : settings.SerialPort.Trim();
        settings.Host = string.IsNullOrWhiteSpace(settings.Host) ? defaults.Host : settings.Host.Trim();
        settings.BaudRate = settings.BaudRate <= 0 ? defaults.BaudRate : settings.BaudRate;
        settings.TcpPort = settings.TcpPort <= 0 ? defaults.TcpPort : settings.TcpPort;
        settings.TouchPlateThickness = settings.TouchPlateThickness <= 0 ? defaults.TouchPlateThickness : settings.TouchPlateThickness;
        settings.ProbeMaxTravel = settings.ProbeMaxTravel <= 0 ? defaults.ProbeMaxTravel : settings.ProbeMaxTravel;
        settings.ProbeFeed = settings.ProbeFeed <= 0 ? defaults.ProbeFeed : settings.ProbeFeed;
        settings.PlungeFeed = settings.PlungeFeed <= 0 ? defaults.PlungeFeed : settings.PlungeFeed;
        settings.RecoveryMarginMm = settings.RecoveryMarginMm < 0 ? defaults.RecoveryMarginMm : settings.RecoveryMarginMm;
        return settings;
    }

    private static void Validate(FeederSettings settings)
    {
        if (settings.Name.Length > 64)
        {
            throw new ArgumentException("Name is limited to 64 characters.");
        }

        if (settings.TcpPort > 65535)
        {
            throw new ArgumentException("TCP port must be 1..65535.");
        }

        if (!double.IsFinite(settings.SafeZMachine) || !double.IsFinite(settings.TouchPlateThickness) || settings.TouchPlateThickness > 100
            || settings.ProbeMaxTravel > 500 || settings.ProbeFeed > 2000 || settings.PlungeFeed > 5000 || settings.RecoveryMarginMm > 50)
        {
            throw new ArgumentException("A setting is out of range.");
        }
    }

    private static string CreateFeederId(FeederDatabase database)
    {
        var id = Guid.CreateVersion7().ToString("N");
        database.SetMeta(FeederIdKey, id);
        return id;
    }
}
