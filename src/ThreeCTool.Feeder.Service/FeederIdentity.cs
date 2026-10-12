using System.Reflection;
using System.Security.Cryptography.X509Certificates;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Storage;

namespace ThreeCTool.Feeder.Service;

/// <summary>Feeder identity: id, display name, version and TLS certificate fingerprint.</summary>
public sealed class FeederIdentity(SettingsStore settings, X509Certificate2 certificate)
{
    public static string Version { get; } =
        typeof(FeederIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public string Fingerprint { get; } = CertificateFingerprint.Of(certificate);

    public FeederInfo Info() => new()
    {
        FeederId = settings.FeederId,
        Name = settings.Current.Name,
        Version = Version,
        CertificateFingerprint = Fingerprint,
    };
}
