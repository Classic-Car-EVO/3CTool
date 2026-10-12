using System.Globalization;
using System.Text;

namespace ThreeCTool.Streaming.Gcode;

/// <summary>
/// A letter/number word such as <c>G38.2</c> or <c>X-1.25</c>; <see cref="Number"/> keeps the original text. A value
/// given as a parameter or expression (<c>Z#5063</c>, <c>Z[#5063-#5323]</c>) is evaluated by the controller: its
/// <see cref="Value"/> is NaN.
/// </summary>
public readonly record struct GcodeWord(char Letter, double Value, string Number)
{
    public bool IsExpression => Number.Length > 0 && Number[0] is '#' or '[';

    public bool IsCode(char letter, double code) => Letter == letter && Math.Abs(Value - code) < 1e-6;

    public override string ToString() => Letter + Number;
}

/// <summary>
/// Host directive embedded as a structured comment, e.g. <c>(MC:HEAT HOTEND 215 WAIT)</c> or <c>(MC:PROBE BEGIN)</c>.
/// Executed by the feeder, never sent to the controller.
/// </summary>
public sealed record HostDirective(string Verb, IReadOnlyList<string> Arguments, string Text)
{
    public const string Prefix = "MC:";

    public bool Wait => Arguments.Any(a => a.Equals("WAIT", StringComparison.OrdinalIgnoreCase));

    public bool IsProbeBegin => Verb == "PROBE" && Arguments is ["BEGIN", ..];

    public bool IsProbeEnd => Verb == "PROBE" && Arguments is ["END", ..];

    /// <summary>Markers only structure the file for recovery; they need no handler.</summary>
    public bool IsMarker => Verb == "PROBE";

    public static HostDirective? TryParse(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        var text = comment.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = text[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0
            ? null
            : new HostDirective(parts[0].ToUpperInvariant(), [.. parts.Skip(1).Select(p => p.ToUpperInvariant())], text);
    }

    public override string ToString() => $"({Text})";
}

/// <summary>One G-code line split into words, comments, an optional host directive or a <c>$</c> system command.</summary>
public sealed class GcodeBlock
{
    private GcodeBlock(string raw, List<GcodeWord> words, List<string> comments, string? systemCommand, string? unparsed)
    {
        Raw = raw;
        Words = words;
        Comments = comments;
        SystemCommand = systemCommand;
        Unparsed = unparsed;
        Directive = comments.Select(HostDirective.TryParse).FirstOrDefault(d => d is not null);
    }

    public string Raw { get; }

    public IReadOnlyList<GcodeWord> Words { get; }

    public IReadOnlyList<string> Comments { get; }

    public HostDirective? Directive { get; }

    /// <summary>A <c>$</c> line (<c>$H</c>, <c>$J=...</c>), passed through unchanged.</summary>
    public string? SystemCommand { get; }

    /// <summary>Comment-stripped text that could not be split into words; sent as-is so the controller reports the error.</summary>
    public string? Unparsed { get; }

    /// <summary>True if something must be sent to the controller.</summary>
    public bool HasCode => SystemCommand is not null || Unparsed is not null || Words.Any(w => w.Letter != 'N');

    public bool HasG(double code) => Words.Any(w => w.IsCode('G', code));

    public bool HasM(double code) => Words.Any(w => w.IsCode('M', code));

    public bool Has(char letter) => Words.Any(w => w.Letter == letter);

    public double? Get(char letter)
    {
        foreach (var word in Words)
        {
            if (word.Letter == letter)
            {
                return word.Value;
            }
        }

        return null;
    }

    /// <summary>Compact text for the controller: no comments or spaces, original <c>N</c> dropped, optional new line number and prefix.</summary>
    public string ToWire(int? lineNumber = null, string? prefix = null)
    {
        if (SystemCommand is not null)
        {
            return SystemCommand;
        }

        var sb = new StringBuilder();
        if (lineNumber is { } n)
        {
            sb.Append('N').Append(n.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append(prefix);
        if (Unparsed is not null)
        {
            return sb.Append(Unparsed).ToString();
        }

        foreach (var word in Words)
        {
            if (word.Letter != 'N')
            {
                sb.Append(word.Letter).Append(word.Number);
            }
        }

        return sb.ToString();
    }

    public static GcodeBlock Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var trimmed = raw.Trim();
        var comments = new List<string>();
        if (trimmed.StartsWith('$'))
        {
            return new GcodeBlock(raw, [], comments, trimmed, null);
        }

        var code = new StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '(')
            {
                var end = trimmed.IndexOf(')', i + 1);
                end = end < 0 ? trimmed.Length : end;
                comments.Add(trimmed[(i + 1)..end].Trim());
                i = end;
            }
            else if (c == ';')
            {
                comments.Add(trimmed[(i + 1)..].Trim());
                break;
            }
            else if (!char.IsWhiteSpace(c))
            {
                code.Append(char.ToUpperInvariant(c));
            }
        }

        var text = code.ToString();
        if (text is "%" or "")
        {
            return new GcodeBlock(raw, [], comments, null, null);
        }

        var words = new List<GcodeWord>();
        var pos = 0;
        while (pos < text.Length)
        {
            var letter = text[pos];
            var start = ++pos;
            if (char.IsAsciiLetterUpper(letter) && pos < text.Length && text[pos] is '#' or '[')
            {
                if (!SkipExpression(text, ref pos))
                {
                    return new GcodeBlock(raw, [], comments, null, text);
                }

                words.Add(new GcodeWord(letter, double.NaN, text[start..pos]));
                continue;
            }

            while (pos < text.Length && (char.IsAsciiDigit(text[pos]) || text[pos] is '.' or '-' or '+'))
            {
                pos++;
            }

            var number = text[start..pos];
            if (!char.IsAsciiLetterUpper(letter)
                || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return new GcodeBlock(raw, [], comments, null, text);
            }

            words.Add(new GcodeWord(letter, value, number));
        }

        return new GcodeBlock(raw, words, comments, null, null);
    }

    /// <summary>Skips a parameter reference (<c>#5063</c>, <c>#&lt;name&gt;</c>) or a bracketed expression.</summary>
    private static bool SkipExpression(string text, ref int pos)
    {
        if (text[pos] == '[')
        {
            var depth = 0;
            do
            {
                depth += text[pos] switch { '[' => 1, ']' => -1, _ => 0 };
                pos++;
            }
            while (depth > 0 && pos < text.Length);

            return depth == 0;
        }

        pos++;
        if (pos >= text.Length)
        {
            return false;
        }

        if (text[pos] == '<')
        {
            var end = text.IndexOf('>', pos);
            if (end < 0)
            {
                return false;
            }

            pos = end + 1;
            return true;
        }

        if (text[pos] is '#' or '[')
        {
            return SkipExpression(text, ref pos);
        }

        var start = pos;
        while (pos < text.Length && char.IsAsciiDigit(text[pos]))
        {
            pos++;
        }

        return pos > start;
    }
}
