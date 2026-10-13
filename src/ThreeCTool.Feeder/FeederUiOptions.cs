using System.Globalization;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Rendering;

namespace ThreeCTool.Feeder;

/// <summary>Command line of the touch UI.</summary>
public sealed record FeederUiOptions
{
    /// <summary>Run full screen on the Linux DRM/KMS console (Pi kiosk) instead of in a desktop window.</summary>
    public bool Drm { get; init; }

    /// <summary>DRM card, e.g. <c>/dev/dri/card1</c>; null = first usable card.</summary>
    public string? DrmCard { get; init; }

    public double Scaling { get; init; } = 1;

    public string SocketPath { get; init; } = FeederDefaults.UnixSocketPath;

    public PreviewRenderer Renderer { get; init; } = PreviewRenderer.Auto;

    public static string Usage =>
        """
        3ctool-feeder-ui [--drm [--card /dev/dri/cardN]] [--scale 1.0] [--socket PATH] [--renderer auto|gl|software]
        """;

    /// <summary>Parses the arguments; unknown or malformed ones throw <see cref="ArgumentException"/>.</summary>
    public static FeederUiOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new FeederUiOptions();
        for (var i = 0; i < args.Count; i++)
        {
            options = args[i] switch
            {
                "--drm" => options with { Drm = true },
                "--card" => options with { DrmCard = Value(args, ref i) },
                "--socket" => options with { SocketPath = Value(args, ref i) },
                "--scale" => options with { Scaling = Scale(Value(args, ref i)) },
                "--renderer" => options with { Renderer = ParseRenderer(Value(args, ref i)) },
                _ => throw new ArgumentException($"Unknown argument '{args[i]}'."),
            };
        }

        return options;
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{args[i]} needs a value.");
        }

        return args[++i];
    }

    private static double Scale(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0.5 and <= 4
            ? value
            : throw new ArgumentException($"Invalid scale '{text}' (0.5 to 4).");

    private static PreviewRenderer ParseRenderer(string text) => text.ToUpperInvariant() switch
    {
        "AUTO" => PreviewRenderer.Auto,
        "GL" => PreviewRenderer.OpenGl,
        "SOFTWARE" => PreviewRenderer.Software,
        _ => throw new ArgumentException($"Invalid renderer '{text}' (auto, gl or software)."),
    };
}
