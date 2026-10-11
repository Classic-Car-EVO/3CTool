using System.Globalization;
using System.Text.RegularExpressions;

namespace ThreeCTool.Streaming.Protocol;

/// <summary>Parses single lines of FluidNC / Grbl output.</summary>
public static partial class ProtocolParser
{
    public static ControllerMessage Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Trim();
        if (text.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            return new OkMessage(text);
        }

        if (text.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && TryLeadingInt(text.AsSpan(6), out var error))
        {
            return new ErrorMessage(error, text);
        }

        if (text.StartsWith("ALARM:", StringComparison.OrdinalIgnoreCase) && TryLeadingInt(text.AsSpan(6), out var alarm))
        {
            return new AlarmMessage(alarm, text);
        }

        if (text.Length > 2 && text[0] == '<' && text[^1] == '>' && TryParseStatus(text, out var report))
        {
            return new StatusMessage(report);
        }

        if (text.Length > 2 && text[0] == '[' && text[^1] == ']')
        {
            var body = text[1..^1];
            var colon = body.IndexOf(':', StringComparison.Ordinal);
            return colon > 0 && body[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c == '.')
                ? new FeedbackMessage(body[..colon], body[(colon + 1)..], text)
                : new FeedbackMessage(string.Empty, body, text);
        }

        var welcome = WelcomePattern().Match(text);
        if (welcome.Success)
        {
            var firmware = welcome.Groups[2].Value.Trim();
            return new WelcomeMessage(welcome.Groups[1].Value, firmware.Length == 0 ? null : firmware, text);
        }

        return new TextMessage(line.TrimEnd('\r', '\n'));
    }

    public static bool TryParseStatus(string text, out StatusReport report)
    {
        ArgumentNullException.ThrowIfNull(text);
        report = null!;
        var fields = text.Trim().TrimStart('<').TrimEnd('>').Split('|');
        if (fields.Length == 0 || fields[0].Length == 0)
        {
            return false;
        }

        var stateParts = fields[0].Split(':');
        var state = Enum.TryParse<MachineState>(stateParts[0], ignoreCase: true, out var parsed) ? parsed : MachineState.Unknown;
        var subState = stateParts.Length > 1 && int.TryParse(stateParts[1], CultureInfo.InvariantCulture, out var sub) ? sub : 0;

        AxisVector? mpos = null, wpos = null, wco = null;
        int? plannerFree = null, rxFree = null, line = null;
        double? feed = null, speed = null;
        OverrideValues? ov = null;
        string? pins = null, accessories = null;
        for (var i = 1; i < fields.Length; i++)
        {
            var colon = fields[i].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = fields[i][..colon];
            var value = fields[i][(colon + 1)..];
            switch (key)
            {
                case "MPos":
                    mpos = AxisVector.TryParse(value, out var m) ? m : null;
                    break;
                case "WPos":
                    wpos = AxisVector.TryParse(value, out var w) ? w : null;
                    break;
                case "WCO":
                    wco = AxisVector.TryParse(value, out var o) ? o : null;
                    break;
                case "Bf":
                    var bf = Ints(value);
                    if (bf.Length == 2)
                    {
                        plannerFree = bf[0];
                        rxFree = bf[1];
                    }

                    break;
                case "Ln":
                    line = int.TryParse(value, CultureInfo.InvariantCulture, out var ln) ? ln : null;
                    break;
                case "FS":
                    var fs = Doubles(value);
                    feed = fs.Length > 0 ? fs[0] : null;
                    speed = fs.Length > 1 ? fs[1] : null;
                    break;
                case "F":
                    feed = Doubles(value) is [var f, ..] ? f : null;
                    break;
                case "Ov":
                    var overrides = Ints(value);
                    ov = overrides.Length == 3 ? new OverrideValues(overrides[0], overrides[1], overrides[2]) : null;
                    break;
                case "Pn":
                    pins = value;
                    break;
                case "A":
                    accessories = value;
                    break;
                default:
                    break;
            }
        }

        report = new StatusReport(state, subState, mpos, wpos, wco, plannerFree, rxFree, line, feed, speed, ov, pins, accessories, text);
        return true;
    }

    /// <summary>Parses <c>[G54:x,y,z]</c>, <c>[TLO:z]</c> and <c>[PRB:x,y,z:1]</c> lines (the <c>$#</c> report) into <paramref name="target"/>.</summary>
    public static bool TryApplyOffset(FeedbackMessage message, WorkCoordinates target)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(target);
        switch (message.Tag)
        {
            case "G54" or "G55" or "G56" or "G57" or "G58" or "G59" or "G28" or "G30" or "G92":
                if (AxisVector.TryParse(message.Content, out var offset))
                {
                    target.Offsets[message.Tag] = offset;
                    return true;
                }

                return false;
            case "TLO":
                if (double.TryParse(message.Content, NumberStyles.Float, CultureInfo.InvariantCulture, out var tlo))
                {
                    target.ToolLengthOffset = tlo;
                    return true;
                }

                return false;
            case "PRB":
                if (TryParseProbe(message.Content, out var probe))
                {
                    target.LastProbe = probe;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    public static bool TryParseProbe(string content, out ProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(content);
        probe = null!;
        var colon = content.LastIndexOf(':');
        if (colon <= 0 || !AxisVector.TryParse(content[..colon], out var position))
        {
            return false;
        }

        probe = new ProbeResult(position, content[(colon + 1)..].Trim() == "1");
        return true;
    }

    private static bool TryLeadingInt(ReadOnlySpan<char> text, out int value)
    {
        var end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return int.TryParse(text[..end], CultureInfo.InvariantCulture, out value);
    }

    private static int[] Ints(string value) =>
        [.. value.Split(',').Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : 0)];

    private static double[] Doubles(string value) =>
        [.. value.Split(',').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)];

    [GeneratedRegex(@"^Grbl\s+(\S+)\s*\[(.*?)\s*'\$' for help\]", RegexOptions.CultureInvariant)]
    private static partial Regex WelcomePattern();
}
