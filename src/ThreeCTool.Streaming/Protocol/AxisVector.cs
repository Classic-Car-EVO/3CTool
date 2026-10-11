using System.Globalization;

using ThreeCTool.Core.Machines;

namespace ThreeCTool.Streaming.Protocol;

/// <summary>Immutable per-axis values in controller order (X, Y, Z, A, B, C). Missing axes read as 0.</summary>
public sealed class AxisVector : IEquatable<AxisVector>
{
    public const string Letters = "XYZABC";

    private readonly double[] values;

    public AxisVector(params double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length > Letters.Length)
        {
            throw new ArgumentException($"At most {Letters.Length} axes are supported.", nameof(values));
        }

        this.values = (double[])values.Clone();
    }

    public static AxisVector Zero { get; } = new(0, 0, 0, 0, 0);

    public int Count => values.Length;

    public double X => this[0];

    public double Y => this[1];

    public double Z => this[2];

    public double A => this[3];

    public double B => this[4];

    public double this[int index] => index >= 0 && index < values.Length ? values[index] : 0;

    public double this[AxisName axis] => this[(int)axis];

    public static int IndexOf(char letter) => Letters.IndexOf(char.ToUpperInvariant(letter), StringComparison.Ordinal);

    /// <summary>Parses a comma-separated list such as <c>1.000,-2.500,0.000</c>.</summary>
    public static bool TryParse(string text, out AxisVector vector)
    {
        vector = Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(',');
        if (parts.Length > Letters.Length)
        {
            return false;
        }

        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
            {
                return false;
            }
        }

        vector = new AxisVector(result);
        return true;
    }

    public static AxisVector Parse(string text) =>
        TryParse(text, out var vector) ? vector : throw new FormatException($"Invalid axis list '{text}'.");

    public AxisVector Add(AxisVector other) => Combine(other, (a, b) => a + b);

    public AxisVector Subtract(AxisVector other) => Combine(other, (a, b) => a - b);

    public AxisVector With(int index, double value)
    {
        var result = new double[Math.Max(values.Length, index + 1)];
        values.CopyTo(result, 0);
        result[index] = value;
        return new AxisVector(result);
    }

    /// <summary>Largest absolute per-axis difference over the axes present in either vector.</summary>
    public double MaxDifference(AxisVector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var max = 0.0;
        for (var i = 0; i < Math.Max(Count, other.Count); i++)
        {
            max = Math.Max(max, Math.Abs(this[i] - other[i]));
        }

        return max;
    }

    public double[] ToArray() => (double[])values.Clone();

    public bool Equals(AxisVector? other) => other is not null && MaxDifference(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as AxisVector);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var v in values)
        {
            hash.Add(v);
        }

        return hash.ToHashCode();
    }

    /// <summary>Same format as the controller reports (3 decimals, invariant culture).</summary>
    public override string ToString() => string.Join(',', values.Select(v => v.ToString("0.000", CultureInfo.InvariantCulture)));

    private AxisVector Combine(AxisVector other, Func<double, double, double> op)
    {
        ArgumentNullException.ThrowIfNull(other);
        var result = new double[Math.Max(Count, other.Count)];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = op(this[i], other[i]);
        }

        return new AxisVector(result);
    }
}
