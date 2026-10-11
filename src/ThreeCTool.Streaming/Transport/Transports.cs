using System.IO.Ports;
using System.Net.Sockets;

using ThreeCTool.Core.Machines;

namespace ThreeCTool.Streaming.Transport;

/// <summary>Byte stream to the controller (USB serial or TCP telnet).</summary>
public interface ITransport : IAsyncDisposable
{
    string Description { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Reads available bytes; returns 0 when the connection was closed.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

public static class Transports
{
    public static ITransport Create(ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Transport switch
        {
            TransportKind.Tcp => new TcpTransport(settings.Host, settings.TcpPort),
            _ => new SerialTransport(settings.SerialPort, settings.BaudRate),
        };
    }
}

/// <summary>FluidNC telnet (raw TCP, no option negotiation).</summary>
public sealed class TcpTransport(string host, int port) : ITransport
{
    private TcpClient? client;
    private NetworkStream? stream;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public string Description => $"tcp://{host}:{port}";

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        stream = client.GetStream();
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        Stream.WriteAsync(data, cancellationToken);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        Stream.ReadAsync(buffer, cancellationToken);

    public ValueTask DisposeAsync()
    {
        stream?.Dispose();
        client?.Dispose();
        return ValueTask.CompletedTask;
    }

    private NetworkStream Stream => stream ?? throw new InvalidOperationException("Transport is not open.");
}

/// <summary>
/// USB serial. DTR and RTS stay de-asserted: on ESP32 boards (FluidNC) asserting them on open resets the controller.
/// </summary>
public sealed class SerialTransport(string portName, int baudRate) : ITransport
{
    private SerialPort? port;

    public string Description => $"serial://{portName}@{baudRate}";

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
        };
        port.Open();
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var stream = Port.BaseStream;
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await Port.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            // Device unplugged or port closed.
            return 0;
        }
    }

    public ValueTask DisposeAsync()
    {
        port?.Dispose();
        return ValueTask.CompletedTask;
    }

    private SerialPort Port => port ?? throw new InvalidOperationException("Transport is not open.");
}
