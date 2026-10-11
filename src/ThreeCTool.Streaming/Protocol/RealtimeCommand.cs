namespace ThreeCTool.Streaming.Protocol;

/// <summary>Single-byte real-time commands, executed by the controller immediately (not buffered).</summary>
public enum RealtimeCommand : byte
{
    StatusReport = (byte)'?',
    FeedHold = (byte)'!',
    CycleStart = (byte)'~',
    SoftReset = 0x18,
    SafetyDoor = 0x84,
    JogCancel = 0x85,
    FeedOverrideReset = 0x90,
    FeedOverridePlus10 = 0x91,
    FeedOverrideMinus10 = 0x92,
    FeedOverridePlus1 = 0x93,
    FeedOverrideMinus1 = 0x94,
    RapidOverrideReset = 0x95,
    RapidOverrideMedium = 0x96,
    RapidOverrideLow = 0x97,
    SpindleOverrideReset = 0x99,
    SpindleOverridePlus10 = 0x9A,
    SpindleOverrideMinus10 = 0x9B,
    SpindleOverridePlus1 = 0x9C,
    SpindleOverrideMinus1 = 0x9D,
    SpindleStop = 0x9E,
    FloodToggle = 0xA0,
    MistToggle = 0xA1,
}

public static class OverrideCommands
{
    public const int MinFeed = 10;
    public const int MaxFeed = 200;

    /// <summary>Real-time bytes that set the feed override to <paramref name="percent"/> (reset to 100 %, then ±10 / ±1 steps).</summary>
    public static IReadOnlyList<RealtimeCommand> FeedSequence(int percent)
    {
        var target = Math.Clamp(percent, MinFeed, MaxFeed);
        var result = new List<RealtimeCommand> { RealtimeCommand.FeedOverrideReset };
        var delta = target - 100;
        var tens = Math.Abs(delta) / 10;
        var ones = Math.Abs(delta) % 10;
        for (var i = 0; i < tens; i++)
        {
            result.Add(delta > 0 ? RealtimeCommand.FeedOverridePlus10 : RealtimeCommand.FeedOverrideMinus10);
        }

        for (var i = 0; i < ones; i++)
        {
            result.Add(delta > 0 ? RealtimeCommand.FeedOverridePlus1 : RealtimeCommand.FeedOverrideMinus1);
        }

        return result;
    }
}
