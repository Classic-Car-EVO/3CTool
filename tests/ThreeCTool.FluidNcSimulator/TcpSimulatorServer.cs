using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace ThreeCTool.FluidNcSimulator;

/// <summary>Exposes a <see cref="Simulator"/> as a FluidNC telnet-style TCP endpoint (one channel per client).</summary>
public sealed class TcpSimulatorServer : IAsyncDisposable
{
    private readonly Simulator simulator;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly Task acceptTask;
    private int clientCount;

    public TcpSimulatorServer(Simulator simulator, int port = 0)
    {
        this.simulator = simulator;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptLoopAsync();
    }

    public int Port { get; }

    public int ConnectionCount => Volatile.Read(ref clientCount);

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        try
        {
            await acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        simulator.DropConnections();
        cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (!simulator.IsPowered)
            {
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        Interlocked.Increment(ref clientCount);
        client.NoDelay = true;
        var stream = client.GetStream();
        var output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var channel = simulator.Connect(
            $"tcp{client.Client.RemoteEndPoint}",
            line => output.Writer.TryWrite(line),
            () =>
            {
                output.Writer.TryComplete();
                client.Close();
            });
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in output.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(line)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
            }
        });

        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                simulator.Receive(channel, buffer.AsSpan(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
        }
        finally
        {
            simulator.Disconnect(channel);
            output.Writer.TryComplete();
            await writer.ConfigureAwait(false);
            client.Dispose();
            Interlocked.Decrement(ref clientCount);
        }
    }
}
