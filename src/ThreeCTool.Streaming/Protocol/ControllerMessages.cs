namespace ThreeCTool.Streaming.Protocol;

public enum MachineState
{
    Unknown,
    Idle,
    Run,
    Hold,
    Jog,
    Home,
    Alarm,
    Check,
    Door,
    Sleep,
}

/// <summary>Feed, rapid and spindle override percentages (<c>Ov:</c>).</summary>
public sealed record OverrideValues(int Feed, int Rapid, int Spindle);

/// <summary>One parsed <c>&lt;...&gt;</c> status report. Fields the controller omitted are null.</summary>
public sealed record StatusReport(
    MachineState State,
    int SubState,
    AxisVector? MachinePosition,
    AxisVector? WorkPosition,
    AxisVector? WorkOffset,
    int? PlannerBlocksAvailable,
    int? RxBytesAvailable,
    int? LineNumber,
    double? Feed,
    double? SpindleSpeed,
    OverrideValues? Overrides,
    string? Pins,
    string? Accessories,
    string Raw)
{
    /// <summary>Motion has stopped: idle, fully decelerated hold (<c>Hold:0</c>), alarm or sleep.</summary>
    public bool IsMotionStopped => State switch
    {
        MachineState.Idle or MachineState.Alarm or MachineState.Sleep or MachineState.Check => true,
        MachineState.Hold or MachineState.Door => SubState == 0,
        _ => false,
    };
}

/// <summary>A line received from the controller.</summary>
public abstract record ControllerMessage(string Raw);

public sealed record OkMessage(string Raw) : ControllerMessage(Raw);

public sealed record ErrorMessage(int Code, string Raw) : ControllerMessage(Raw)
{
    public string Description => FluidNcCodes.ErrorDescription(Code);
}

public sealed record AlarmMessage(int Code, string Raw) : ControllerMessage(Raw)
{
    public string Description => FluidNcCodes.AlarmDescription(Code);
}

public sealed record StatusMessage(StatusReport Report) : ControllerMessage(Report.Raw);

/// <summary>Start-up / reset banner, e.g. <c>Grbl 3.9 [FluidNC v3.9.5 (wifi) '$' for help]</c>.</summary>
public sealed record WelcomeMessage(string GrblVersion, string? Firmware, string Raw) : ControllerMessage(Raw);

/// <summary>Bracketed feedback such as <c>[MSG:...]</c>, <c>[GC:...]</c>, <c>[PRB:...]</c>, <c>[G54:...]</c>, <c>[VER:...]</c>.</summary>
public sealed record FeedbackMessage(string Tag, string Content, string Raw) : ControllerMessage(Raw);

/// <summary>Anything else (e.g. <c>$Config/Dump</c> YAML lines, help text).</summary>
public sealed record TextMessage(string Raw) : ControllerMessage(Raw);
