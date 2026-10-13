using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.FluidNcSimulator;

namespace ThreeCTool.Feeder.Tests;

public sealed class MachineControlTests
{
    [Fact]
    public async Task Home_FromAlarm_ReachesIdle()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var alarm = await feeder.WaitStatusAsync(s => s.MachineState.Length > 0);
        Assert.Equal("Alarm", alarm.MachineState);
        Assert.Equal(ConnectionState.Connected, alarm.Connection);
        Assert.Contains("3.9.5", alarm.Firmware, StringComparison.Ordinal);

        await feeder.Local.HomeAsync(new Empty());

        await feeder.WaitStatusAsync(s => s.MachineState == "Idle");
        Assert.True(feeder.Simulator.IsHomed);
    }

    [Fact]
    public async Task StepJog_MovesIncrementally()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var before = feeder.Simulator.MachinePosition;

        await feeder.Local.JogAsync(new JogRequest { X = 10, Y = 5, Feed = 3000 });

        Assert.True(feeder.Simulator.WaitUntil(s => s.State == SimState.Idle && Math.Abs(s.MachinePosition[0] - before[0] - 10) < 1e-3, FeederHarness.Timeout));
        Assert.Equal(before[1] + 5, feeder.Simulator.MachinePosition[1], 3);
        var status = await feeder.WaitStatusAsync(s => Math.Abs(s.MachinePosition.X - (before[0] + 10)) < 1e-3);
        Assert.Equal(before[1] + 5, status.MachinePosition.Y, 3);
    }

    [Fact]
    public async Task ContinuousJog_StopsWhenKeepAlivesStop()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 1 });
        var start = feeder.Simulator.MachinePosition[0];
        using var call = feeder.Local.JogContinuous();
        await call.RequestStream.WriteAsync(new JogHold { Direction = new JogRequest { X = 1, Feed = 1200 } });
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(100);
            await call.RequestStream.WriteAsync(new JogHold());
        }

        // No more keep-alives: the watchdog cancels the jog although the stream is still open.
        Assert.True(feeder.Simulator.WaitUntil(s => s.State == SimState.Idle && s.MachinePosition[0] > start, FeederHarness.Timeout));
        var travelled = feeder.Simulator.MachinePosition[0] - start;
        Assert.InRange(travelled, 1, 40);
        await call.RequestStream.CompleteAsync();
        await call;
    }

    [Fact]
    public async Task ContinuousJog_StopsWhenStreamCompletes()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 1 });
        var start = feeder.Simulator.MachinePosition[1];
        using var call = feeder.Local.JogContinuous();
        await call.RequestStream.WriteAsync(new JogHold { Direction = new JogRequest { Y = 1, Feed = 1200 } });
        await Task.Delay(150);
        await call.RequestStream.CompleteAsync();
        await call;

        Assert.True(feeder.Simulator.WaitUntil(s => s.State == SimState.Idle, FeederHarness.Timeout));
        Assert.InRange(feeder.Simulator.MachinePosition[1] - start, 0.5, 20);
    }

    [Fact]
    public async Task WorkZeroAndTouchPlate_SetG54()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 100, ProbeSurfaceMachineZ = -400 });
        await feeder.Local.JogAsync(new JogRequest { X = 200, Y = 150, Z = -350, Feed = 6000 });
        await feeder.WaitStatusAsync(s => s.MachineState == "Idle" && s.MachinePosition.Z < -349.9);

        await feeder.Local.SetWorkZeroAsync(new WorkZeroRequest { X = true, Y = true });
        var g54 = feeder.Simulator.WorkOffset(0);
        var position = feeder.Simulator.MachinePosition;
        Assert.Equal(position[0], g54[0], 3);
        Assert.Equal(position[1], g54[1], 3);

        var settings = await feeder.Admin.GetSettingsAsync(new Empty());
        settings.TouchPlateThickness = 10;
        await feeder.Admin.UpdateSettingsAsync(settings);
        var probe = await feeder.Local.ProbeTouchPlateAsync(new ProbeRequest { MaxTravel = 80, Feed = 300 });

        Assert.Equal(-400, probe.WorkZMachine, 2);
        Assert.Equal(-410, feeder.Simulator.WorkOffset(0)[2], 2);
        Assert.Equal(1, feeder.Simulator.ProbeCount);
        // WCO is reported every few status reports, so the first Idle status may still carry the old offset.
        var status = await feeder.WaitStatusAsync(s => s.MachineState == "Idle" && Math.Abs(s.WorkPosition.Z - 12) < 0.01);
        Assert.Equal(12, status.WorkPosition.Z, 2);
    }

    [Fact]
    public async Task Probe_WithoutContact_FailsAndLeavesOffsets()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 100, ProbeSurfaceMachineZ = -600 });
        var before = feeder.Simulator.WorkOffset(0);

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await feeder.Local.ProbeTouchPlateAsync(new ProbeRequest { MaxTravel = 20, Feed = 1000 }));

        Assert.NotEqual(StatusCode.OK, error.StatusCode);
        Assert.Equal(before[2], feeder.Simulator.WorkOffset(0)[2], 6);
    }

    [Fact]
    public async Task ResetAndUnlock_ClearAlarm()
    {
        await using var feeder = await FeederHarness.StartAsync();
        feeder.Simulator.EmergencyStop();
        var alarm = await feeder.WaitStatusAsync(s => s.MachineState == "Alarm");
        Assert.Equal(3, alarm.AlarmCode);
        Assert.NotEmpty(alarm.AlarmText);

        await feeder.Local.ResetAsync(new Empty());
        await feeder.Local.UnlockAsync(new Empty());

        var idle = await feeder.WaitStatusAsync(s => s.MachineState == "Idle");
        Assert.Equal(0, idle.AlarmCode);
    }

    [Fact]
    public async Task ControllerPowerLoss_ReconnectsAutomatically()
    {
        await using var feeder = await FeederHarness.StartAsync();
        feeder.Simulator.PowerLoss();
        await feeder.WaitStatusAsync(s => s.Connection != ConnectionState.Connected);

        feeder.Simulator.PowerOn();

        var status = await feeder.WaitStatusAsync(s => s.Connection == ConnectionState.Connected && s.MachineState == "Alarm");
        Assert.Contains("3.9.5", status.Firmware, StringComparison.Ordinal);
        Assert.True(feeder.Server.ConnectionCount >= 1);
    }

    [Fact]
    public async Task Disconnected_ManualCommandsAreUnavailable()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var settings = await feeder.Admin.GetSettingsAsync(new Empty());
        settings.TcpPort = 1;
        await feeder.Admin.UpdateSettingsAsync(settings);
        var status = await feeder.WaitStatusAsync(s => s.Connection != ConnectionState.Connected && s.LastError.Length > 0);
        Assert.NotEmpty(status.LastError);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await feeder.Local.HomeAsync(new Empty()));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
    }
}
