using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Feeder.Tests;

public sealed class MaintenanceTests
{
    [Fact]
    public async Task Requests_AreWrittenForThePathUnits()
    {
        var folder = Directory.CreateTempSubdirectory("3cf-maint-").FullName;
        try
        {
            await using var feeder = await FeederHarness.StartAsync(home: false, configure: o => o.MaintenanceDirectory = folder);
            Assert.True((await feeder.Admin.GetMaintenanceAsync(new Empty())).Available);

            foreach (var (action, file) in new[]
            {
                (MaintenanceAction.Console, "console.request"),
                (MaintenanceAction.Reboot, "reboot.request"),
                (MaintenanceAction.PowerOff, "poweroff.request"),
            })
            {
                await feeder.Admin.RequestMaintenanceAsync(new MaintenanceRequest { Action = action });
                Assert.True(File.Exists(Path.Combine(folder, file)), file);
            }

            var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
                await feeder.Admin.RequestMaintenanceAsync(new MaintenanceRequest()));
            Assert.Equal(StatusCode.InvalidArgument, unknown.StatusCode);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Requests_AreRefusedWithoutDirectoryOrWhileRunning()
    {
        var folder = Directory.CreateTempSubdirectory("3cf-maint-").FullName;
        try
        {
            await using (var plain = await FeederHarness.StartAsync(home: false))
            {
                Assert.False((await plain.Admin.GetMaintenanceAsync(new Empty())).Available);
                var unavailable = await Assert.ThrowsAsync<RpcException>(async () =>
                    await plain.Admin.RequestMaintenanceAsync(new MaintenanceRequest { Action = MaintenanceAction.Reboot }));
                Assert.Equal(StatusCode.FailedPrecondition, unavailable.StatusCode);
            }

            await using var feeder = await FeederHarness.StartAsync(configure: o => o.MaintenanceDirectory = folder);
            var job = await FeederHarness.UploadAsync(feeder.Local, TestJobs.Zigzag(60).Lines);
            var reply = await feeder.Local.StartJobAsync(new StartJobRequest { JobId = job.JobId });
            Assert.True(reply.Started, string.Join(" ", reply.Warnings));
            await feeder.WaitStatusAsync(s => s.Job is { State: "Running" });

            var busy = await Assert.ThrowsAsync<RpcException>(async () =>
                await feeder.Admin.RequestMaintenanceAsync(new MaintenanceRequest { Action = MaintenanceAction.PowerOff }));
            Assert.Equal(StatusCode.FailedPrecondition, busy.StatusCode);
            Assert.Empty(Directory.GetFiles(folder));
            await feeder.Local.StopAsync(new Empty());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
