using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;
using Grpc.Net.Client;

namespace ThreeCTool.Feeder.Contracts;

public static class FeederDefaults
{
    public const int Port = 50051;

    public const string UnixSocketPath = "/run/3ctool-feeder/feeder.sock";

    public const int MaxMessageBytes = 64 * 1024 * 1024;

    public const string AuthorizationHeader = "authorization";

    public const string BearerPrefix = "Bearer ";
}

/// <summary>SHA-256 fingerprints of the feeder's self-signed TLS certificate (pinned at pairing).</summary>
public static class CertificateFingerprint
{
    /// <summary>Lowercase hex SHA-256 of the DER-encoded certificate.</summary>
    public static string Of(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>Groups of four hex digits for comparing by eye, e.g. <c>3f2a 91c0 ...</c>.</summary>
    public static string Format(string fingerprint)
    {
        var hex = Normalize(fingerprint);
        return string.Join(' ', Enumerable.Range(0, (hex.Length + 3) / 4).Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - (i * 4)))));
    }

    public static bool Matches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
        {
            return false;
        }

        var a = Normalize(expected);
        var b = Normalize(actual);
        return a.Length == 64 && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
    }

    private static string Normalize(string fingerprint) =>
        new([.. fingerprint.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant)]);
}

/// <summary>gRPC channels to a feeder: pinned TLS with a bearer token (desktop) or the local Unix socket (touch UI).</summary>
public static class FeederChannels
{
    /// <summary>
    /// Channel to a paired feeder. The server certificate must match <paramref name="fingerprint"/>; chain and host
    /// name are not checked (the certificate is self-signed).
    /// </summary>
    public static GrpcChannel ForPairedFeeder(string address, string fingerprint, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentException.ThrowIfNullOrEmpty(token);
        var credentials = CallCredentials.FromInterceptor((_, metadata) =>
        {
            metadata.Add(FeederDefaults.AuthorizationHeader, FeederDefaults.BearerPrefix + token);
            return Task.CompletedTask;
        });
        return ForTls(address, certificate => certificate is not null && CertificateFingerprint.Matches(fingerprint, CertificateFingerprint.Of(certificate)), credentials);
    }

    /// <summary>Channel to the feeder daemon on this machine (no TLS; access is controlled by the socket's file permissions).</summary>
    public static GrpcChannel ForUnixSocket(string socketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = FeederDefaults.MaxMessageBytes,
            MaxSendMessageSize = FeederDefaults.MaxMessageBytes,
        });
    }

    /// <summary><c>host</c>, <c>host:port</c> or <c>https://host:port</c> as an https URI (default port <see cref="FeederDefaults.Port"/>).</summary>
    public static Uri NormalizeAddress(string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        var text = address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address;
        var uri = new Uri(text, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Feeder addresses must use https.", nameof(address));
        }

        return new UriBuilder(uri) { Port = uri.IsDefaultPort && !text.Contains(":443", StringComparison.Ordinal) ? FeederDefaults.Port : uri.Port, Path = "/" }.Uri;
    }

    internal static GrpcChannel ForTls(string address, Func<X509Certificate?, bool> validate, CallCredentials? credentials)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => validate(certificate),
            },
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        return GrpcChannel.ForAddress(NormalizeAddress(address), new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = credentials is null ? ChannelCredentials.SecureSsl : ChannelCredentials.Create(ChannelCredentials.SecureSsl, credentials),
            MaxReceiveMessageSize = FeederDefaults.MaxMessageBytes,
            MaxSendMessageSize = FeederDefaults.MaxMessageBytes,
        });
    }
}

/// <summary>Result of pairing; the desktop stores it (token + pinned fingerprint) in the library database.</summary>
public sealed record PairingResult(string FeederId, string Name, Uri Address, string Fingerprint, string PairingId, string Token);

public sealed class PairingRejectedException(string message) : Exception(message);

public static class FeederPairing
{
    /// <summary>
    /// Pairs with a feeder: connects over TLS, lets the user compare the observed certificate fingerprint with the one
    /// shown on the feeder's screen (<paramref name="confirmFingerprint"/>), and only then sends the one-time code.
    /// The certificate seen first is pinned for the rest of the exchange.
    /// </summary>
    public static async Task<PairingResult> PairAsync(
        string address,
        string code,
        string clientName,
        Func<FeederInfo, string, Task<bool>> confirmFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentNullException.ThrowIfNull(confirmFingerprint);
        string? observed = null;
        var gate = new Lock();
        using var channel = FeederChannels.ForTls(address, certificate =>
        {
            if (certificate is null)
            {
                return false;
            }

            var fingerprint = CertificateFingerprint.Of(certificate);
            lock (gate)
            {
                observed ??= fingerprint;
                return CertificateFingerprint.Matches(observed, fingerprint);
            }
        }, null);
        var client = new Pairing.PairingClient(channel);
        var info = await client.HelloAsync(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: cancellationToken).ConfigureAwait(false);
        string seen;
        lock (gate)
        {
            seen = observed ?? throw new PairingRejectedException("The feeder did not present a certificate.");
        }

        if (!CertificateFingerprint.Matches(info.CertificateFingerprint, seen))
        {
            throw new PairingRejectedException("The certificate presented does not belong to the feeder (possible interception).");
        }

        if (!await confirmFingerprint(info, seen).ConfigureAwait(false))
        {
            throw new PairingRejectedException("Certificate fingerprint was not confirmed.");
        }

        try
        {
            var reply = await client.PairAsync(new PairRequest { Code = code.Trim(), ClientName = clientName }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new PairingResult(reply.Feeder.FeederId, reply.Feeder.Name, FeederChannels.NormalizeAddress(address), seen, reply.PairingId, reply.Token);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.FailedPrecondition or StatusCode.ResourceExhausted)
        {
            throw new PairingRejectedException(ex.Status.Detail);
        }
    }
}
