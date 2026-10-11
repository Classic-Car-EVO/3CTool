namespace ThreeCTool.Streaming.Jobs;

public enum JobState
{
    Pending,
    Running,
    Paused,

    /// <summary>Stopped at an <c>M0</c>/<c>M1</c> waiting for the operator (tool change prompt).</summary>
    ProgramPaused,
    Stopping,
    Stopped,
    Completed,

    /// <summary>Controller reported an error or alarm.</summary>
    Faulted,

    /// <summary>Connection lost while running.</summary>
    Interrupted,
    Recovering,
    Aborted,
}

public enum InterruptionKind
{
    None,
    Stop,
    Alarm,
    Error,
    Disconnect,
    PowerLoss,

    /// <summary>A host directive (heater, Moonraker) failed.</summary>
    Directive,
}

/// <summary>Persisted job progress (section 11.1): enough to plan a resume after any interruption.</summary>
public sealed record JobProgress
{
    public required string JobId { get; init; }

    public required string FileSha256 { get; init; }

    public JobState State { get; init; }

    public InterruptionKind Interruption { get; init; }

    /// <summary>Alarm or error code of the interruption.</summary>
    public int? InterruptionCode { get; init; }

    /// <summary>Last file line written to the controller.</summary>
    public int LastSent { get; init; }

    /// <summary>Last file line acknowledged (<c>ok</c>) by the controller.</summary>
    public int LastAcked { get; init; }

    /// <summary>Last file line known or estimated to be executing.</summary>
    public int Executed { get; init; }

    /// <summary><see cref="Executed"/> came from a reported line number (<c>Ln:</c>) rather than an estimate.</summary>
    public bool ExecutedExact { get; init; }

    /// <summary>Last reported machine position (serialised <see cref="Protocol.AxisVector"/>).</summary>
    public string? MachinePosition { get; init; }

    public string? WorkOffset { get; init; }

    /// <summary>Work coordinate systems from the last <c>$#</c> (<see cref="Protocol.WorkCoordinates.Serialize"/>).</summary>
    public string? WorkCoordinates { get; init; }

    public double? ToolLengthOffset { get; init; }

    /// <summary>Spindle RPM the operator confirmed on the manual dial.</summary>
    public double? SpindleRpm { get; init; }

    public int FeedOverride { get; init; } = 100;

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record JobEvent(DateTimeOffset At, string Kind, int? Line, string Message);

public static class JobEventKinds
{
    public const string Started = "started";
    public const string State = "state";
    public const string Alarm = "alarm";
    public const string Error = "error";
    public const string Disconnect = "disconnect";
    public const string Message = "message";
    public const string Directive = "directive";
    public const string Prompt = "prompt";
    public const string FeedOverride = "feed-override";
    public const string SpindleRpm = "spindle-rpm";
    public const string Offsets = "offsets";
    public const string Recovery = "recovery";
    public const string Warning = "warning";
}
