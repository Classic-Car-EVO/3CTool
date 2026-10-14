using System.Globalization;
using System.Numerics;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Rendering;

namespace ThreeCTool.Feeder.Services;

/// <summary>Display formatting and conversions shared by the view models.</summary>
public static class Display
{
    public static string Mm(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    public static string Degrees(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + "°";

    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary><c>m:ss</c> or <c>h:mm:ss</c>.</summary>
    public static string Duration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    public static string LocalTime(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out double value) =>
        double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static PolylinePath ToPath(Preview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new PolylinePath([.. preview.Positions], [.. preview.Lines], [.. preview.StripStarts], [.. preview.StripRapid]);
    }

    /// <summary>Tool point in the preview's part frame: the work position rotated back by A about X (as the feeder builds previews).</summary>
    public static Vector3 PartFrame(Axes work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var (sin, cos) = Math.SinCos(-work.A * Math.PI / 180);
        return new Vector3((float)work.X, (float)((work.Y * cos) - (work.Z * sin)), (float)((work.Y * sin) + (work.Z * cos)));
    }
}

/// <summary>Formatted X/Y/Z/A for the DRO.</summary>
public sealed record AxesText(string X, string Y, string Z, string A)
{
    public static readonly AxesText Unknown = new("-", "-", "-", "-");

    public static AxesText From(Axes? axes) =>
        axes is null ? Unknown : new(Display.Mm(axes.X), Display.Mm(axes.Y), Display.Mm(axes.Z), Display.Degrees(axes.A));
}
