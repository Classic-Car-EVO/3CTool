using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Server.Kestrel.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Api;
using ThreeCTool.Feeder.Service.Controller;
using ThreeCTool.Feeder.Service.Jobs;
using ThreeCTool.Feeder.Service.Security;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Streaming.Jobs;

namespace ThreeCTool.Feeder.Service;

/// <summary>Builds the feeder daemon host: TLS gRPC on the LAN, plain gRPC on the local Unix socket.</summary>
public static class FeederHost
{
    public const string SystemConfigPath = "/etc/3ctool-feeder/feeder.json";
    public const string CertificateSubject = "3C Tool Feeder";

    /// <param name="args">Command line (configuration overrides like <c>--Feeder:Port=50052</c>).</param>
    /// <param name="configure">Adjusts the bound options (tests: temp directory, port 0).</param>
    /// <param name="services">Replaces or adds services (tests: simulator transport factory).</param>
    public static WebApplication Build(string[] args, Action<FeederOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddJsonFile(SystemConfigPath, optional: true, reloadOnChange: false);
        builder.Configuration.AddCommandLine(args);
        builder.Host.UseSystemd();

        var options = new FeederOptions();
        builder.Configuration.GetSection("Feeder").Bind(options);
        configure?.Invoke(options);
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.JobsDirectory);
        var certificate = CertificateStore.LoadOrCreate(options.CertificatePath, CertificateSubject);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = null;
            void Tls(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(certificate);
            }

            if (string.IsNullOrWhiteSpace(options.ListenAddress))
            {
                kestrel.ListenAnyIP(options.Port, Tls);
            }
            else
            {
                kestrel.Listen(System.Net.IPAddress.Parse(options.ListenAddress), options.Port, Tls);
            }

            if (!string.IsNullOrWhiteSpace(options.UnixSocketPath))
            {
                PrepareSocket(options.UnixSocketPath);
                kestrel.ListenUnixSocket(options.UnixSocketPath, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    LocalSocketFeature.Mark(listen);
                });
            }
        });

        var s = builder.Services;
        s.AddSingleton(options);
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(certificate);
        s.AddSingleton(_ => new FeederDatabase(options.DatabasePath));
        s.AddSingleton<SettingsStore>();
        s.AddSingleton<PairingManager>();
        s.AddSingleton<FeederIdentity>();
        s.AddSingleton(_ => new ObservableJournal(new SqliteJobJournal(options.JournalPath, options.JournalCommitInterval)));
        s.AddSingleton<JobStore>();
        s.AddSingleton<PromptBroker>();
        s.AddSingleton<ControllerSession>();
        s.AddHostedService(sp => sp.GetRequiredService<ControllerSession>());
        s.AddSingleton<JobManager>();
        s.AddHostedService(sp => sp.GetRequiredService<JobManager>());
        s.AddSingleton<MachineControl>();
        s.AddSingleton<StatusHub>();
        s.AddGrpc(grpc =>
        {
            grpc.Interceptors.Add<FeederInterceptor>();
            grpc.MaxReceiveMessageSize = FeederDefaults.MaxMessageBytes;
            grpc.MaxSendMessageSize = FeederDefaults.MaxMessageBytes;
        });
        services?.Invoke(s);

        var app = builder.Build();
        app.MapGrpcService<PairingService>();
        app.MapGrpcService<FeederService>();
        app.MapGrpcService<LocalAdminService>();

        if (!string.IsNullOrWhiteSpace(options.UnixSocketPath))
        {
            var socket = options.UnixSocketPath;
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                }
            });
        }

        return app;
    }

    private static void PrepareSocket(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
