using System.Runtime.Versioning;

using ThreeCTool.Core.Machines;
using ThreeCTool.FluidNcSimulator;
using ThreeCTool.Streaming.Config;
using ThreeCTool.Streaming.Protocol;
using ThreeCTool.Streaming.Transport;

namespace ThreeCTool.Streaming.Tests;

public sealed class ControllerConfigTests
{
    private static readonly string[] Yaml =
    [
        "name: Test mill",
        "planner_blocks: 32",
        "start:",
        "  must_home: false",
        "axes:",
        "  x:",
        "    steps_per_mm: 80.000",
        "    max_rate_mm_per_min: 5000",
        "    acceleration_mm_per_sec2: 250",
        "    max_travel_mm: 1300",
        "    soft_limits: true",
        "    homing:",
        "      cycle: 2",
        "      positive_direction: false",
        "      mpos_mm: 0",
        "    motor0:",
        "      stepstick:",
        "        direction_pin: gpio.14:low",
        "    motor1:",
        "      stepstick:",
        "        direction_pin: gpio.15",
        "  y:",
        "    steps_per_mm: 80",
        "    motor0:",
        "      null_motor:",
        "ok",
    ];

    [Fact]
    public void Parse_ReadsNestedYaml()
    {
        var config = ControllerConfig.Parse(Yaml);

        Assert.Equal("Test mill", config.Name);
        Assert.Equal(32, config.PlannerBlocks);
        Assert.False(config.MustHome);
        var axes = config.Axes();
        Assert.Equal([AxisName.X, AxisName.Y], axes.Select(a => a.Axis));
        var x = axes[0];
        Assert.Equal(80, x.StepsPerUnit);
        Assert.Equal(5000, x.MaxRatePerMinute);
        Assert.Equal(250, x.AccelerationPerSecond2);
        Assert.Equal(1300, x.MaxTravel);
        Assert.True(x.SoftLimits);
        Assert.Equal(2, x.HomingCycle);
        Assert.Equal(2, x.MotorCount);
        Assert.True(x.DirectionInverted);
        Assert.Equal(1, axes[1].MotorCount);
    }

    [Fact]
    public void Hash_IgnoresBlankLinesAndOk_ButNotValues()
    {
        var a = ControllerConfig.Parse(Yaml);
        var b = ControllerConfig.Parse(["", .. Yaml, "", "[MSG: done]"]);
        var c = ControllerConfig.Parse(Yaml.Select(l => l.Replace("80.000", "80.001", StringComparison.Ordinal)));

        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
    }

    [Fact]
    public void Compare_ReportsDriftAndMissingAxes()
    {
        var config = ControllerConfig.Parse(Yaml);
        var profile = new MachineProfile { Name = "test" };
        ProfileImport.Apply(profile, config, null, DateTimeOffset.UnixEpoch);
        Assert.Empty(ProfileImport.Compare(profile, config));
        Assert.Equal(config.Hash, profile.Controller.ConfigHash);

        profile.GetAxis(AxisName.X)!.StepsPerUnit = 100;
        profile.Axes.Add(new AxisConfig { Axis = AxisName.Z });
        var mismatches = ProfileImport.Compare(profile, config);

        Assert.Contains(mismatches, m => m.Axis == AxisName.X && m.Setting == "steps per unit" && m.ProfileValue == "100" && m.ControllerValue == "80");
        Assert.Contains(mismatches, m => m.Axis == AxisName.Z && m.ControllerValue == "missing");
    }

    [Fact]
    public void Apply_ImportsPlannerSettings_AndCompareReportsDrift()
    {
        var config = ControllerConfig.Parse(["junction_deviation_mm: 0.02", .. Yaml]);
        var profile = new MachineProfile { Name = "test" };
        ProfileImport.Apply(profile, config, null, DateTimeOffset.UnixEpoch);
        Assert.Equal(0.02, profile.JunctionDeviation);
        Assert.Equal(32, profile.PlannerBlocks);
        Assert.Equal(0.01, ControllerConfig.Parse(Yaml).JunctionDeviation);

        profile.JunctionDeviation = 0.05;
        profile.PlannerBlocks = 16;
        var mismatches = ProfileImport.Compare(profile, config);
        Assert.Contains(mismatches, m => m.Axis == null && m.Setting == "junction deviation");
        Assert.Contains(mismatches, m => m.Axis == null && m.Setting == "planner blocks" && m.ControllerValue == "32");
    }

    [Theory]
    [InlineData(false, 0, 300, -5, 295)]
    [InlineData(true, 0, 100, -5, -5)]
    [InlineData(true, 0, 100, 3, -5)]
    [InlineData(false, 0, 2, -5, 0)]
    public void Apply_MovesTheSafeZIntoTheZTravel(bool positive, double mpos, double travel, double safeZ, double expected)
    {
        string[] z =
        [
            "  z:",
            "    max_travel_mm: " + travel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "    homing:",
            "      positive_direction: " + (positive ? "true" : "false"),
            "      mpos_mm: " + mpos.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "    motor0:",
            "      null_motor:",
        ];
        var config = ControllerConfig.Parse([.. Yaml[..^1], .. z, "ok"]);
        var profile = new MachineProfile { Name = "test", SafeZMachine = safeZ };

        ProfileImport.Apply(profile, config, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, profile.SafeZMachine, 9);
    }

    [Fact]
    public async Task IdentifyAsync_AgainstSimulator_MatchesImportedProfile()
    {
        await using var harness = await SimHarness.StartAsync(home: false);
        var connection = await harness.ConnectAsync();
        var profile = new MachineProfile { Name = "sim" };

        var first = await ProfileImport.IdentifyAsync(connection, profile);
        Assert.Equal("3.9.5", first.BuildInfo.Version!.Version.ToString(3));
        Assert.Equal(5, first.Mismatches.Count);
        Assert.Single(first.Warnings);

        ProfileImport.Apply(profile, first.Config, first.BuildInfo.Version, DateTimeOffset.UtcNow);
        var second = await ProfileImport.IdentifyAsync(connection, profile);
        Assert.Empty(second.Mismatches);
        Assert.Empty(second.Warnings);
        Assert.Equal(first.Config.Hash, second.Config.Hash);
        Assert.Equal("3.9.5", profile.Controller.FluidNcVersion);

        var expected = SimulatorOptions.DefaultAxes();
        Assert.Equal(expected.Select(a => (a.Axis, a.StepsPerUnit, a.MotorCount, a.HomingCycle, a.HomingPositiveDirection)),
            profile.Axes.Select(a => (a.Axis, a.StepsPerUnit, a.MotorCount, a.HomingCycle, a.HomingPositiveDirection)));
        Assert.Equal(16, second.Config.PlannerBlocks);
        Assert.True(second.Config.MustHome);

        // Profile edited to drop the B axis (FDM extruder): drift is reported.
        profile.Axes.RemoveAll(a => a.Axis == AxisName.B);
        var third = await ProfileImport.IdentifyAsync(connection, profile);
        Assert.Contains(third.Mismatches, m => m.Axis == AxisName.B && m.ProfileValue == "missing");
        Assert.Single(third.Warnings);
    }

    [Fact]
    public async Task IdentifyAsync_OldFirmware_Warns()
    {
        await using var harness = await SimHarness.StartAsync(new SimulatorOptions { Version = "3.7.2" }, home: false);
        var connection = await harness.ConnectAsync();
        var profile = new MachineProfile { Name = "sim" };
        var identity = await ProfileImport.IdentifyAsync(connection, profile);
        ProfileImport.Apply(profile, identity.Config, identity.BuildInfo.Version, DateTimeOffset.UtcNow);

        identity = await ProfileImport.IdentifyAsync(connection, profile);
        Assert.True(identity.BuildInfo.Version!.IsOlderThanTested);
        Assert.Contains(identity.Warnings, w => w.Contains("3.7.2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DetectBufferSizes_ReadsControllerBuffers()
    {
        await using var harness = await SimHarness.StartAsync(new SimulatorOptions { RxBufferSize = 256, PlannerBlocks = 24 }, home: false);
        var connection = await harness.ConnectAsync();
        var sizes = await connection.DetectBufferSizesAsync();
        Assert.Equal((24, 256), sizes);
        Assert.Equal(256, connection.Options.RxBufferSize);
    }
}

/// <summary>Runs only on Linux (pseudo-terminal tests).</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
        }
    }
}

public sealed class SerialTransportTests
{
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task SerialTransport_OverPseudoTerminal_HomesAndStreams()
    {
        using var simulator = new Simulator(new SimulatorOptions { TimeScale = 100 });
        using var port = new PtySimulatorPort(simulator);
        await using var connection = new ControllerConnection(
            new SerialTransport(port.DevicePath, 115200),
            new ConnectionOptions { StatusInterval = TimeSpan.FromMilliseconds(20) });
        await connection.OpenAsync();

        await connection.HomeAsync();
        await connection.ExecuteOkAsync("G0X10Y20Z-30");
        await connection.SynchronizeAsync();
        var idle = await connection.WaitForStatusAsync(s => s.State == MachineState.Idle, SimHarness.Timeout);

        Assert.Equal(new AxisVector(10, 20, -30, 0, 0), idle.MachinePosition);
        Assert.Equal(1, simulator.HomingCount);
    }
}
