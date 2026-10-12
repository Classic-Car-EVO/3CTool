using System.Globalization;
using System.Text;

namespace ThreeCTool.FluidNcSimulator;

/// <summary>G-code words of one line as parsed by the simulated controller.</summary>
internal sealed class SimLine
{
    private readonly List<(char Letter, double Value)> words;

    private SimLine(List<(char, double)> words) => this.words = words;

    public IReadOnlyList<(char Letter, double Value)> Words => words;

    /// <summary>
    /// Parses a line; word values may be numbered parameters (<c>#5063</c>) or bracketed expressions with
    /// <c>+ - * /</c> (FluidNC parameters and expressions). Returns 0 or a Grbl error code.
    /// </summary>
    public static int TryParse(string text, out SimLine line, Func<int, double?>? parameter = null)
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

            double value;
            if (i < s.Length && s[i] is '#' or '[')
            {
                var parsed = new ExpressionParser(s, i, parameter);
                if (!parsed.TryPrimary(out value))
                {
                    return 2;
                }

                i = parsed.Position;
            }
            else
            {
                var start = i;
                while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '-' or '+'))
                {
                    i++;
                }

                if (!double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return 2; // Bad number format
                }
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

/// <summary>Numbered parameters and <c>[...]</c> expressions with <c>+ - * /</c> and unary signs.</summary>
internal sealed class ExpressionParser(string text, int position, Func<int, double?>? parameter)
{
    public int Position { get; private set; } = position;

    public bool TryPrimary(out double value)
    {
        value = double.NaN;
        if (Position >= text.Length)
        {
            return false;
        }

        if (text[Position] == '[')
        {
            Position++;
            if (!TryExpression(out value) || Position >= text.Length || text[Position] != ']')
            {
                return false;
            }

            Position++;
            return true;
        }

        if (text[Position] == '#')
        {
            Position++;
            if (!TryPrimary(out var number) || number != Math.Floor(number) || parameter?.Invoke((int)number) is not { } found)
            {
                return false;
            }

            value = found;
            return true;
        }

        var start = Position;
        while (Position < text.Length && (char.IsAsciiDigit(text[Position]) || text[Position] == '.'))
        {
            Position++;
        }

        return double.TryParse(text.AsSpan(start, Position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private bool TryExpression(out double value)
    {
        if (!TryTerm(out value))
        {
            return false;
        }

        while (Position < text.Length && text[Position] is '+' or '-')
        {
            var op = text[Position++];
            if (!TryTerm(out var right))
            {
                return false;
            }

            value = op == '+' ? value + right : value - right;
        }

        return true;
    }

    private bool TryTerm(out double value)
    {
        if (!TryUnary(out value))
        {
            return false;
        }

        while (Position < text.Length && text[Position] is '*' or '/')
        {
            var op = text[Position++];
            if (!TryUnary(out var right))
            {
                return false;
            }

            value = op == '*' ? value * right : value / right;
        }

        return true;
    }

    private bool TryUnary(out double value)
    {
        if (Position < text.Length && text[Position] is '-' or '+')
        {
            var negative = text[Position++] == '-';
            if (!TryUnary(out value))
            {
                return false;
            }

            value = negative ? -value : value;
            return true;
        }

        return TryPrimary(out value);
    }
}
