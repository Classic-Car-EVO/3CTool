using System.Globalization;
using System.Text.RegularExpressions;

namespace ThreeCTool.Streaming.Protocol;

/// <summary>Result of the last probe cycle (<c>[PRB:x,y,z:1]</c>), machine coordinates.</summary>
public sealed record ProbeResult(AxisVector Position, bool Success);

/// <summary>Work coordinate systems, G28/G30/G92 and tool length offset as reported by <c>$#</c>.</summary>
public sealed class WorkCoordinates
{
    public static readonly string[] WorkSystems = ["G54", "G55", "G56", "G57", "G58", "G59"];

    public Dictionary<string, AxisVector> Offsets { get; init; } = new(StringComparer.Ordinal);

    public double ToolLengthOffset { get; set; }

    public ProbeResult? LastProbe { get; set; }

    public AxisVector? Get(string name) => Offsets.TryGetValue(name, out var v) ? v : null;

    public WorkCoordinates Clone() => new()
    {
        Offsets = new Dictionary<string, AxisVector>(Offsets, StringComparer.Ordinal),
        ToolLengthOffset = ToolLengthOffset,
        LastProbe = LastProbe,
    };

    /// <summary>Compact text form (<c>G54=1,2,3;...;TLO=0.5</c>) used for persistence.</summary>
    public string Serialize()
    {
        var parts = Offsets.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}").ToList();
        parts.Add("TLO=" + ToolLengthOffset.ToString("R", CultureInfo.InvariantCulture));
        return string.Join(';', parts);
    }

    public static WorkCoordinates Deserialize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new WorkCoordinates();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "TLO")
            {
                result.ToolLengthOffset = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else if (AxisVector.TryParse(value, out var v))
            {
                result.Offsets[key] = v;
            }
        }

        return result;
    }
}

/// <summary>Controller firmware identification from the banner or <c>$Build/Info</c> (<c>[VER:3.9 FluidNC v3.9.5:]</c>).</summary>
public sealed partial record FluidNcVersionInfo(Version Version, string Text)
{
    /// <summary>Oldest FluidNC release the streamer and recovery engine were verified against (simulator only until spike 2).</summary>
    public static Version MinimumTested { get; } = new(3, 9, 0);

    public bool IsOlderThanTested => Version < MinimumTested;

    public static FluidNcVersionInfo? TryParse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = VersionPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var version = new Version(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        return new FluidNcVersionInfo(version, match.Value);
    }

    [GeneratedRegex(@"FluidNC\s+v(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}
