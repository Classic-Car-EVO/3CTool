using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service;

using FeederClient = ThreeCTool.Feeder.Contracts.Feeder.FeederClient;

namespace ThreeCTool.Desktop.Services;

/// <summary>
/// Direct connection: the feeder service hosted inside the desktop process (USB serial or TCP to the controller),
/// reached over its local socket with the same client as a paired feeder. TLS listens on loopback only.
/// </summary>
public sealed class DirectFeeder(string directory, Action<FeederOptions>? configure = null, Action<IServiceCollection>? services = null) : IAsyncDisposable
{
    public const string SocketFileName = "feeder.sock";

    private readonly SemaphoreSlim gate = new(1, 1);
    private WebApplication? app;
    private GrpcChannel? channel;

    public string Directory { get; } = Path.GetFullPath(directory);

    public string SocketPath => Path.Combine(Directory, SocketFileName);

    public bool IsRunning => app is not null;

    public FeederClient Client => new(channel ?? throw new InvalidOperationException("The direct feeder is not running."));

    public LocalAdmin.LocalAdminClient Admin => new(channel ?? throw new InvalidOperationException("The direct feeder is not running."));

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (app is not null)
            {
                return;
            }

            System.IO.Directory.CreateDirectory(Directory);
            var host = FeederHost.Build([], options =>
            {
                options.DataDirectory = Directory;
                options.Port = 0;
                options.ListenAddress = "127.0.0.1";
                options.UnixSocketPath = SocketPath;
                configure?.Invoke(options);
            }, services);
            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            app = host;
            channel = FeederChannels.ForUnixSocket(SocketPath);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            channel?.Dispose();
            channel = null;
            if (app is { } host)
            {
                app = null;
                await host.StopAsync().ConfigureAwait(false);
                await host.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FeederSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await Admin.GetSettingsAsync(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Changes the controller connection (the service reconnects with the new transport).</summary>
    public async Task<FeederSettings> UpdateSettingsAsync(FeederSettings settings, CancellationToken cancellationToken = default) =>
        await Admin.UpdateSettingsAsync(settings, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListSerialPortsAsync(CancellationToken cancellationToken = default)
    {
        var ports = await Admin.ListSerialPortsAsync(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. ports.Ports];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        gate.Dispose();
    }
}
