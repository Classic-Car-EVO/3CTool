namespace ThreeCTool.Core.Machines;

/// <summary>A feeder Pi this desktop is paired with (gRPC over TLS with a pinned certificate and a bearer token).</summary>
public sealed class PairedFeeder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Stable id the feeder reports in its info; re-pairing the same feeder updates this row.</summary>
    public string FeederId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>gRPC base address, e.g. <c>https://mill-feeder.local:50051</c>.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the feeder's TLS certificate; connections to any other certificate are refused.</summary>
    public string CertificateFingerprint { get; set; } = string.Empty;

    /// <summary>Pairing id on the feeder (shown in its pairing list, used to revoke).</summary>
    public string PairingId { get; set; } = string.Empty;

    /// <summary>Bearer token issued at pairing; the feeder only stores its hash.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Machine profile the feeder drives.</summary>
    public Guid? MachineProfileId { get; set; }

    public DateTimeOffset PairedAt { get; set; }

    /// <summary>Last successful feedback sync.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }
}
