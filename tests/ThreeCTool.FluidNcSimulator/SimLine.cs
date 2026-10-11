using System.Globalization;
using System.Text;

namespace ThreeCTool.FluidNcSimulator;

/// <summary>G-code words of one line as parsed by the simulated controller.</summary>
internal sealed class SimLine
{
    private readonly List<(char Letter, double Value)> words;

    private SimLine(List<(char, double)> words) => this.words = words;

    public IReadOnlyList<(char Letter, double Value)> Words => words;

    public static int TryParse(string text, out SimLine line)
    {
        line = new SimLine([]);
        var clean = new StringBuilder();
        var inComment = false;
        foreach (var c in text)
        {
            if (inComment)
            {
                inComment = c != ')';
            }
            else if (c == '(')
            {
                inComment = true;
            }
            else if (c == ';')
            {
                break;
            }
            else if (!char.IsWhiteSpace(c))
            {
                clean.Append(char.ToUpperInvariant(c));
            }
        }

        var s = clean.ToString();
        var i = 0;
        while (i < s.Length)
        {
            var letter = s[i++];
            if (!char.IsAsciiLetterUpper(letter))
            {
                return 1; // Expected command letter
            }

            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '-' or '+'))
            {
                i++;
            }

            if (!double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return 2; // Bad number format
            }

            line.words.Add((letter, value));
        }

        return 0;
    }

    public bool HasG(double code) => words.Exists(w => w.Letter == 'G' && Math.Abs(w.Value - code) < 1e-6);

    public bool HasM(double code) => words.Exists(w => w.Letter == 'M' && Math.Abs(w.Value - code) < 1e-6);

    public double? Get(char letter)
    {
        foreach (var (l, v) in words)
        {
            if (l == letter)
            {
                return v;
            }
        }

        return null;
    }

    public IEnumerable<double> All(char letter) => words.Where(w => w.Letter == letter).Select(w => w.Value);
}
