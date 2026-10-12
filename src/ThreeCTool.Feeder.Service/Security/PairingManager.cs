using System.Security.Cryptography;
using System.Text;

using ThreeCTool.Feeder.Service.Storage;

namespace ThreeCTool.Feeder.Service.Security;

public enum PairingFailure
{
    NoActiveCode,
    WrongCode,
    TooManyAttempts,
}

public sealed class PairingException(PairingFailure failure, string message) : Exception(message)
{
    public PairingFailure Failure { get; } = failure;
}

public sealed record ActivePairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Section 11.5 pairing: the touch UI shows a 6-digit one-time code (valid for a few minutes, single use, locked after
/// repeated wrong guesses); the desktop exchanges it for a random bearer token. Only the token's SHA-256 is stored.
/// </summary>
public sealed class PairingManager(FeederDatabase database, FeederOptions options, TimeProvider time)
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    private readonly Lock gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastSeenWritten = new(StringComparer.Ordinal);
    private ActivePairingCode? active;
    private int failedAttempts;

    /// <summary>Raised when a code is shown, used, cancelled, or a pairing is added or revoked.</summary>
    public event EventHandler? Changed;

    public ActivePairingCode? ActiveCode
    {
        get
        {
            lock (gate)
            {
                return active is { } code && code.ExpiresAt > time.GetUtcNow() ? code : null;
            }
        }
    }

    public int Count => database.ListPairings().Count;

    public ActivePairingCode StartPairing()
    {
        ActivePairingCode code;
        lock (gate)
        {
            code = new ActivePairingCode(RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                time.GetUtcNow() + options.PairingCodeLifetime);
            active = code;
            failedAttempts = 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return code;
    }

    public void CancelPairing()
    {
        lock (gate)
        {
            active = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Consumes the active code; returns the new pairing id and the token (shown to the client once).</summary>
    public (string PairingId, string Token) Pair(string code, string clientName)
    {
        ArgumentNullException.ThrowIfNull(code);
        var name = string.IsNullOrWhiteSpace(clientName) ? "Desktop" : new string([.. clientName.Trim().Where(c => !char.IsControl(c))]);
        name = name.Length > 64 ? name[..64] : name;
        PairingException? failure = null;
        lock (gate)
        {
            if (active is not { } current || current.ExpiresAt <= time.GetUtcNow())
            {
                active = null;
                throw new PairingException(PairingFailure.NoActiveCode, "No pairing code is active on the feeder. Start pairing on its touch screen.");
            }

            var entered = code.Trim();
            if (entered.Length != current.Code.Length
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(current.Code), Encoding.ASCII.GetBytes(entered)))
            {
                failedAttempts++;
                if (failedAttempts >= options.PairingMaxAttempts)
                {
                    active = null;
                    failure = new PairingException(PairingFailure.TooManyAttempts, "Too many wrong codes; the pairing code was cancelled.");
                }
                else
                {
                    failure = new PairingException(PairingFailure.WrongCode, $"Wrong pairing code ({options.PairingMaxAttempts - failedAttempts} attempts left).");
                }
            }
            else
            {
                active = null;
            }
        }

        if (failure is not null)
        {
            if (failure.Failure == PairingFailure.TooManyAttempts)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            throw failure;
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var pairing = new PairingRow(Guid.CreateVersion7().ToString("N"), name, HashToken(token), time.GetUtcNow(), null);
        database.InsertPairing(pairing);
        Changed?.Invoke(this, EventArgs.Empty);
        return (pairing.Id, token);
    }

    /// <summary>Returns the pairing a bearer token belongs to, or null if it is unknown or revoked.</summary>
    public PairingRow? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256)
        {
            return null;
        }

        var pairing = database.FindPairingByTokenHash(HashToken(token));
        if (pairing is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        bool write;
        lock (gate)
        {
            write = !lastSeenWritten.TryGetValue(pairing.Id, out var last) || now - last >= LastSeenResolution;
            if (write)
            {
                lastSeenWritten[pairing.Id] = now;
            }
        }

        if (write)
        {
            database.TouchPairing(pairing.Id, now);
        }

        return pairing;
    }

    public IReadOnlyList<PairingRow> List() => database.ListPairings();

    public bool Revoke(string pairingId)
    {
        var removed = database.DeletePairing(pairingId);
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
