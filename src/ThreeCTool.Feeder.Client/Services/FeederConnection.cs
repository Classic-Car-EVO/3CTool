using Google.Protobuf.WellKnownTypes;

using Grpc.Core;
using Grpc.Net.Client;

using ThreeCTool.Feeder.Contracts;

using FeederClient = ThreeCTool.Feeder.Contracts.Feeder.FeederClient;

namespace ThreeCTool.Feeder.Services;

/// <summary>
/// Connection to the local feeder daemon: clients plus a status stream that reconnects until disposed. Events are
/// raised on the synchronization context that called <see cref="Start"/> (the UI thread).
/// </summary>
public sealed class FeederConnection : IDisposable
{
    private readonly GrpcChannel channel;
    private readonly CancellationTokenSource stopping = new();
    private readonly TimeSpan retryInterval;
    private Task? watch;

    public FeederConnection(string socketPath, TimeSpan? retryInterval = null)
        : this(FeederChannels.ForUnixSocket(socketPath), retryInterval)
    {
    }

    public FeederConnection(GrpcChannel channel, TimeSpan? retryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        this.channel = channel;
        this.retryInterval = retryInterval ?? TimeSpan.FromSeconds(2);
        Client = new FeederClient(channel);
        Admin = new LocalAdmin.LocalAdminClient(channel);
    }

    /// <summary>A status arrived (connected) or the stream broke (<c>null</c>).</summary>
    public event EventHandler<FeederStatus?>? StatusChanged;

    public FeederClient Client { get; }

    public LocalAdmin.LocalAdminClient Admin { get; }

    public FeederStatus? Status { get; private set; }

    public bool IsConnected => Status is not null;

    /// <summary>Last reason the status stream failed (shown while disconnected).</summary>
    public string? LastFailure { get; private set; }

    public void Start() => watch ??= WatchAsync(stopping.Token);

    public void Dispose()
    {
        stopping.Cancel();
        channel.Dispose();
        stopping.Dispose();
    }

    // Continuations stay on the caller's (UI) context so events are raised there.
    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var call = Client.WatchStatus(new Empty(), cancellationToken: cancellationToken);
                while (await call.ResponseStream.MoveNext(cancellationToken))
                {
                    LastFailure = null;
                    Status = call.ResponseStream.Current;
                    StatusChanged?.Invoke(this, Status);
                }

                LastFailure = "The feeder closed the status stream.";
            }
            catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LastFailure = ex.Status.Detail.Length > 0 ? ex.Status.Detail : ex.StatusCode.ToString();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            Status = null;
            StatusChanged?.Invoke(this, null);

            try
            {
                await Task.Delay(retryInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
