using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Feeder.Service;

/// <summary>Daemon configuration (section <c>Feeder</c> of appsettings / <c>/etc/3ctool-feeder/feeder.json</c>).</summary>
public sealed class FeederOptions
{
    /// <summary>Database, journal, TLS certificate and job files.</summary>
    public string DataDirectory { get; set; } = "/var/lib/3ctool-feeder";

    /// <summary>TLS port for paired desktops; 0 picks a free port (tests).</summary>
    public int Port { get; set; } = FeederDefaults.Port;

    /// <summary>Address the TLS endpoint binds to; empty = all interfaces.</summary>
    public string? ListenAddress { get; set; }

    /// <summary>Unix socket for the local touch UI; empty disables it.</summary>
    public string? UnixSocketPath { get; set; } = FeederDefaults.UnixSocketPath;

    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Status poll interval (<c>?</c>) of the controller connection.</summary>
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan PairingCodeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public int PairingMaxAttempts { get; set; } = 5;

    /// <summary>A continuous jog is cancelled when no keep-alive arrives within this time.</summary>
    public TimeSpan JogKeepAlive { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan JournalCommitInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Nominal radius used to weight A moves (time estimate, recovery).</summary>
    public double RotaryRadiusMm { get; set; } = 20;

    /// <summary>Rapid rate for time estimates when the controller config does not give one.</summary>
    public double RapidRateMmPerMin { get; set; } = 5000;

    public int PreviewMaxVertices { get; set; } = 150_000;

    public long MaxUploadBytes { get; set; } = 1L << 30;

    /// <summary>Minimum interval between two status messages to one watcher.</summary>
    public TimeSpan StatusMinInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>A status message is sent at least this often, even without changes.</summary>
    public TimeSpan StatusHeartbeat { get; set; } = TimeSpan.FromSeconds(1);

    public string DatabasePath => Path.Combine(DataDirectory, "feeder.db");

    public string JournalPath => Path.Combine(DataDirectory, "journal.db");

    public string JobsDirectory => Path.Combine(DataDirectory, "jobs");

    public string CertificatePath => Path.Combine(DataDirectory, "feeder.pfx");
}
