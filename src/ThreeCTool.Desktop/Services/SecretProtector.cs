using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ThreeCTool.Desktop.Services;

/// <summary>
/// Encrypts secrets (feeder tokens) before they are written to the library database: DPAPI (current user) on
/// Windows, AES-GCM with a per-user key file (mode 0600) elsewhere. Values without a known prefix are treated as
/// legacy plain text.
/// </summary>
public sealed class SecretProtector(string keyPath)
{
    public const string KeyFileName = "secrets.key";

    private const string DpapiPrefix = "dpapi1:";
    private const string AesPrefix = "aes1:";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] Entropy = "3C Tool feeder token"u8.ToArray();
    private readonly Lock gate = new();
    private byte[]? key;

    public string KeyPath { get; } = keyPath;

    /// <summary>Key file next to the library database.</summary>
    public static SecretProtector ForLibrary(string libraryPath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(libraryPath))!, KeyFileName));

    public static bool IsProtected(string? value) =>
        value is not null && (value.StartsWith(DpapiPrefix, StringComparison.Ordinal) || value.StartsWith(AesPrefix, StringComparison.Ordinal));

    public string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
        {
            return DpapiPrefix + Convert.ToBase64String(DpapiProtect(bytes));
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[bytes.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(Key(), TagBytes))
        {
            aes.Encrypt(nonce, bytes, cipher, tag, Entropy);
        }

        return AesPrefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Decrypts a protected value; legacy plain text is returned unchanged.</summary>
    /// <exception cref="CryptographicException">The value was protected by another user or key.</exception>
    public string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new CryptographicException("The secret was protected with Windows DPAPI.");
            }

            return Encoding.UTF8.GetString(DpapiUnprotect(Convert.FromBase64String(stored[DpapiPrefix.Length..])));
        }

        if (stored.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            var data = Convert.FromBase64String(stored[AesPrefix.Length..]);
            if (data.Length < NonceBytes + TagBytes)
            {
                throw new CryptographicException("Protected secret is truncated.");
            }

            var plain = new byte[data.Length - NonceBytes - TagBytes];
            using var aes = new AesGcm(Key(), TagBytes);
            aes.Decrypt(data.AsSpan(0, NonceBytes), data.AsSpan(NonceBytes + TagBytes), data.AsSpan(NonceBytes, TagBytes), plain, Entropy);
            return Encoding.UTF8.GetString(plain);
        }

        return stored;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiProtect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiUnprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    private byte[] Key()
    {
        lock (gate)
        {
            if (key is not null)
            {
                return key;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(KeyPath))!);
            if (File.Exists(KeyPath))
            {
                var existing = File.ReadAllBytes(KeyPath);
                if (existing.Length != 32)
                {
                    throw new CryptographicException($"Key file '{KeyPath}' is invalid.");
                }

                return key = existing;
            }

            var created = RandomNumberGenerator.GetBytes(32);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                using var file = new FileStream(KeyPath, options);
                file.Write(created);
            }
            catch (IOException) when (File.Exists(KeyPath))
            {
                // Another process created it first.
                return key = File.ReadAllBytes(KeyPath);
            }

            return key = created;
        }
    }
}
