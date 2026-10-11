using System.Globalization;
using System.Text.RegularExpressions;

namespace ThreeCTool.Core;

public enum LengthUnit
{
    Millimeter,
    Centimeter,
    Meter,
    Inch,
}

/// <summary>All internal lengths are millimetres; other units are converted at the input boundary.</summary>
public static partial class Units
{
    public const double MillimetersPerInch = 25.4;

    public static double MillimetersPer(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeter => 1.0,
        LengthUnit.Centimeter => 10.0,
        LengthUnit.Meter => 1000.0,
        LengthUnit.Inch => MillimetersPerInch,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    public static double ToMillimeters(double value, LengthUnit unit) => value * MillimetersPer(unit);

    public static double FromMillimeters(double millimeters, LengthUnit unit) => millimeters / MillimetersPer(unit);

    /// <summary>
    /// Parses a length such as <c>6</c>, <c>6 mm</c>, <c>0.25in</c>, <c>1/4"</c> or <c>1 1/2 in</c> into millimetres.
    /// A number without a unit uses <paramref name="defaultUnit"/>.
    /// </summary>
    public static bool TryParseLength(string? text, LengthUnit defaultUnit, out double millimeters)
    {
        millimeters = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = LengthPattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        double value;
        if (match.Groups["num"].Success)
        {
            var whole = match.Groups["whole"].Success ? double.Parse(match.Groups["whole"].Value, CultureInfo.InvariantCulture) : 0;
            var numerator = double.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture);
            var denominator = double.Parse(match.Groups["den"].Value, CultureInfo.InvariantCulture);
            if (denominator == 0)
            {
                return false;
            }

            value = whole + (numerator / denominator);
        }
        else
        {
            value = double.Parse(match.Groups["dec"].Value, CultureInfo.InvariantCulture);
        }

        var unit = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "" => defaultUnit,
            "MM" => LengthUnit.Millimeter,
            "CM" => LengthUnit.Centimeter,
            "M" => LengthUnit.Meter,
            "IN" or "INCH" or "\"" => LengthUnit.Inch,
            _ => (LengthUnit?)null,
        };
        if (unit is null)
        {
            return false;
        }

        millimeters = ToMillimeters(value, unit.Value);
        return true;
    }

    [GeneratedRegex("""^(?:(?:(?<whole>\d+)\s+)?(?<num>\d+)\s*/\s*(?<den>\d+)|(?<dec>\d+(?:\.\d+)?|\.\d+))\s*(?<unit>mm|cm|m|in|inch|")?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthPattern();
}
