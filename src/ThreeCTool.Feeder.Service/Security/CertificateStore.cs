using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ThreeCTool.Feeder.Service.Security;

/// <summary>
/// The feeder's self-signed TLS certificate (ECDSA P-256, 10 years). Desktops pin its SHA-256 fingerprint at pairing,
/// so it must survive restarts: it is stored as a PKCS#12 file readable by the service user only.
/// </summary>
public static class CertificateStore
{
    public static X509Certificate2 LoadOrCreate(string path, string subjectName)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        }

        var pfx = Create(subjectName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(pfx);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    private static byte[] Create(string subjectName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName("3C Tool Feeder " + subjectName);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(SafeDnsName(Environment.MachineName));
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    private static string SafeDnsName(string name)
    {
        var cleaned = new string([.. name.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')]);
        return cleaned.Length == 0 ? "feeder" : cleaned;
    }
}
