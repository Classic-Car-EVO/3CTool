using System.Globalization;

using ThreeCTool.Core.Machines;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming.Config;

/// <summary>A controller-owned setting that differs between the machine profile and the running controller.</summary>
public sealed record ProfileMismatch(AxisName? Axis, string Setting, string ProfileValue, string ControllerValue)
{
    public override string ToString() => $"{(Axis is { } a ? a + " " : string.Empty)}{Setting}: profile {ProfileValue}, controller {ControllerValue}";
}

/// <summary>What was learned about the controller on connect (section 11.1).</summary>
public sealed record ControllerIdentity(
    ControllerBuildInfo BuildInfo,
    ControllerConfig Config,
    IReadOnlyList<ProfileMismatch> Mismatches,
    IReadOnlyList<string> Warnings);

/// <summary>Imports controller-owned settings into a machine profile and detects drift (section 11.1a).</summary>
public static class ProfileImport
{
    private const double RelativeTolerance = 1e-6;

    /// <summary>Copies axis settings, version and config hash into <paramref name="profile"/>. 3C Tool-owned settings are untouched.</summary>
    public static void Apply(MachineProfile profile, ControllerConfig config, FluidNcVersionInfo? version, DateTimeOffset importedAt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(config);
        profile.Axes = [.. config.Axes()];
        profile.Controller.ConfigHash = config.Hash;
        profile.Controller.ConfigImportedAt = importedAt;
        if (version is not null)
        {
            profile.Controller.FluidNcVersion = version.Version.ToString(3);
        }
    }

    public static IReadOnlyList<ProfileMismatch> Compare(MachineProfile profile, ControllerConfig config)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(config);
        var result = new List<ProfileMismatch>();
        var controllerAxes = config.Axes();
        foreach (var actual in controllerAxes)
        {
            var expected = profile.GetAxis(actual.Axis);
            if (expected is null)
            {
                result.Add(new ProfileMismatch(actual.Axis, "axis", "missing", "configured"));
                continue;
            }

            Number(result, actual.Axis, "steps per unit", expected.StepsPerUnit, actual.StepsPerUnit);
            Number(result, actual.Axis, "max rate", expected.MaxRatePerMinute, actual.MaxRatePerMinute);
            Number(result, actual.Axis, "acceleration", expected.AccelerationPerSecond2, actual.AccelerationPerSecond2);
            Number(result, actual.Axis, "max travel", expected.MaxTravel, actual.MaxTravel);
            Number(result, actual.Axis, "homing machine position", expected.HomingMachinePosition, actual.HomingMachinePosition);
            Value(result, actual.Axis, "soft limits", expected.SoftLimits, actual.SoftLimits);
            Value(result, actual.Axis, "direction inverted", expected.DirectionInverted, actual.DirectionInverted);
            Value(result, actual.Axis, "motor count", expected.MotorCount, actual.MotorCount);
            Value(result, actual.Axis, "homing cycle", expected.HomingCycle, actual.HomingCycle);
            Value(result, actual.Axis, "homing positive direction", expected.HomingPositiveDirection, actual.HomingPositiveDirection);
        }

        foreach (var axis in profile.Axes.Where(a => controllerAxes.All(c => c.Axis != a.Axis)))
        {
            result.Add(new ProfileMismatch(axis.Axis, "axis", "configured", "missing"));
        }

        return result;
    }

    /// <summary>Reads version and configuration from a freshly opened connection and compares them with the profile.</summary>
    public static async Task<ControllerIdentity> IdentifyAsync(ControllerConnection connection, MachineProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(profile);
        var build = await connection.QueryBuildInfoAsync(cancellationToken).ConfigureAwait(false);
        var config = await connection.QueryConfigAsync(cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        if (build.Version is null)
        {
            warnings.Add("Controller firmware version could not be read; FluidNC is required.");
        }
        else if (build.Version.IsOlderThanTested)
        {
            warnings.Add($"FluidNC {build.Version.Version.ToString(3)} is older than the minimum tested version {FluidNcVersionInfo.MinimumTested.ToString(3)}.");
        }

        var mismatches = Compare(profile, config);
        if (mismatches.Count > 0)
        {
            warnings.Add($"Controller configuration differs from machine profile '{profile.Name}' ({mismatches.Count} settings); re-import recommended.");
        }

        return new ControllerIdentity(build, config, mismatches, warnings);
    }

    private static void Number(List<ProfileMismatch> result, AxisName axis, string name, double expected, double actual)
    {
        if (Math.Abs(expected - actual) > RelativeTolerance * Math.Max(1, Math.Max(Math.Abs(expected), Math.Abs(actual))))
        {
            result.Add(new ProfileMismatch(axis, name, expected.ToString("G", CultureInfo.InvariantCulture), actual.ToString("G", CultureInfo.InvariantCulture)));
        }
    }

    private static void Value<T>(List<ProfileMismatch> result, AxisName axis, string name, T expected, T actual)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            result.Add(new ProfileMismatch(axis, name, Convert.ToString(expected, CultureInfo.InvariantCulture) ?? string.Empty, Convert.ToString(actual, CultureInfo.InvariantCulture) ?? string.Empty));
        }
    }
}
