using ThreeCTool.Streaming.Config;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming;

/// <summary>Firmware identification from <c>$I</c> (<c>$Build/Info</c>).</summary>
public sealed record ControllerBuildInfo(FluidNcVersionInfo? Version, string? VersionLine, string? Options, IReadOnlyList<string> Messages);

public static class ControllerQueries
{
    public static readonly TimeSpan LongCommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary><c>$#</c>: work coordinate systems, G28/G30/G92, tool length offset and last probe.</summary>
    public static async Task<WorkCoordinates> QueryWorkCoordinatesAsync(this ControllerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var result = await connection.ExecuteOkAsync("$#", null, cancellationToken).ConfigureAwait(false);
        return ParseWorkCoordinates(result.Responses);
    }

    public static WorkCoordinates ParseWorkCoordinates(IEnumerable<ControllerMessage> responses)
    {
        var coordinates = new WorkCoordinates();
        foreach (var feedback in responses.OfType<FeedbackMessage>())
        {
            ProtocolParser.TryApplyOffset(feedback, coordinates);
        }

        return coordinates;
    }

    /// <summary><c>$G</c>: active modal G-code state, e.g. <c>G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0</c>.</summary>
    public static async Task<string> QueryParserStateAsync(this ControllerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var result = await connection.ExecuteOkAsync("$G", null, cancellationToken).ConfigureAwait(false);
        return result.Responses.OfType<FeedbackMessage>().FirstOrDefault(f => f.Tag == "GC")?.Content ?? string.Empty;
    }

    public static async Task<ControllerBuildInfo> QueryBuildInfoAsync(this ControllerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var result = await connection.ExecuteOkAsync("$I", null, cancellationToken).ConfigureAwait(false);
        var feedback = result.Responses.OfType<FeedbackMessage>().ToList();
        var ver = feedback.FirstOrDefault(f => f.Tag == "VER");
        var version = FluidNcVersionInfo.TryParse(ver?.Content) ?? FluidNcVersionInfo.TryParse(connection.LastWelcome?.Firmware);
        return new ControllerBuildInfo(
            version,
            ver?.Content,
            feedback.FirstOrDefault(f => f.Tag == "OPT")?.Content,
            [.. feedback.Where(f => f.Tag == "MSG").Select(f => f.Content.Trim())]);
    }

    /// <summary><c>$CD</c> (<c>$Config/Dump</c>): the running controller configuration.</summary>
    public static async Task<ControllerConfig> QueryConfigAsync(this ControllerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var result = await connection.ExecuteOkAsync("$CD", null, cancellationToken).ConfigureAwait(false);
        return ControllerConfig.Parse(result.Responses.OfType<TextMessage>().Select(t => t.Raw));
    }

    /// <summary>Sends <c>$H</c> and waits until homing has finished.</summary>
    public static Task<LineResult> HomeAsync(this ControllerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteOkAsync("$H", LongCommandTimeout, cancellationToken);
    }

    public static Task<LineResult> UnlockAsync(this ControllerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteOkAsync("$X", null, cancellationToken);
    }

    /// <summary>Waits until all queued motion has finished (<c>G4 P0</c> is acknowledged only once the planner is empty).</summary>
    public static Task<LineResult> SynchronizeAsync(this ControllerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteOkAsync("G4 P0", LongCommandTimeout, cancellationToken);
    }

    /// <summary>Jogs with <c>$J=</c>; <paramref name="command"/> is the G-code part, e.g. <c>G91 X10 F1000</c>.</summary>
    public static Task<LineResult> JogAsync(this ControllerConnection connection, string command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ExecuteOkAsync("$J=" + command, null, cancellationToken);
    }

    public static async Task SetFeedOverrideAsync(this ControllerConnection connection, int percent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        foreach (var command in OverrideCommands.FeedSequence(percent))
        {
            await connection.SendRealtimeAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }
}
