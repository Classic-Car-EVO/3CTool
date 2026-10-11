namespace ThreeCTool.Streaming.Protocol;

/// <summary>FluidNC / Grbl error and alarm codes.</summary>
public static class FluidNcCodes
{
    public const int AlarmHardLimit = 1;
    public const int AlarmSoftLimit = 2;
    public const int AlarmAbortCycle = 3;
    public const int AlarmProbeFailInitial = 4;
    public const int AlarmProbeFailContact = 5;
    public const int AlarmHomingFailReset = 6;
    public const int AlarmControlPin = 11;
    public const int AlarmHardStop = 13;
    public const int AlarmUnhomed = 14;

    public const int ErrorSystemLocked = 9;

    private static readonly Dictionary<int, string> Errors = new()
    {
        [1] = "Expected command letter",
        [2] = "Bad number format",
        [3] = "Invalid statement",
        [4] = "Negative value",
        [5] = "Setting disabled",
        [6] = "Step pulse too short",
        [7] = "Failed to read settings",
        [8] = "Command requires idle state",
        [9] = "G-code locked out during alarm or jog",
        [10] = "Soft limits require homing",
        [11] = "Line too long",
        [12] = "Max step rate exceeded",
        [13] = "Safety door open",
        [14] = "Line length exceeded",
        [15] = "Jog travel exceeded",
        [16] = "Invalid jog command",
        [17] = "Laser mode requires PWM output",
        [18] = "No homing cycles defined",
        [20] = "Unsupported G-code command",
        [21] = "Modal group violation",
        [22] = "Undefined feed rate",
        [23] = "Command value not an integer",
        [24] = "Axis command conflict",
        [25] = "Word repeated",
        [26] = "No axis words",
        [27] = "Invalid line number",
        [28] = "Value word missing",
        [29] = "Unsupported coordinate system",
        [30] = "G53 requires G0 or G1",
        [31] = "Axis words exist",
        [32] = "No axis words in plane",
        [33] = "Invalid target",
        [34] = "Arc radius error",
        [35] = "No offsets in plane",
        [36] = "Unused words",
        [37] = "G43.1 dynamic axis error",
        [38] = "Max value exceeded",
        [39] = "P parameter max exceeded",
        [40] = "Check control pins",
    };

    private static readonly Dictionary<int, string> Alarms = new()
    {
        [AlarmHardLimit] = "Hard limit triggered; position lost",
        [AlarmSoftLimit] = "Soft limit: target exceeds machine travel",
        [AlarmAbortCycle] = "Reset while in motion; position lost",
        [AlarmProbeFailInitial] = "Probe not in the expected initial state",
        [AlarmProbeFailContact] = "Probe did not make contact",
        [AlarmHomingFailReset] = "Homing aborted by reset",
        [7] = "Homing failed: safety door opened",
        [8] = "Homing failed: pull-off did not clear the switch",
        [9] = "Homing failed: switch not found",
        [10] = "Spindle control failure",
        [AlarmControlPin] = "Control pin active at start-up",
        [12] = "Ambiguous homing switch",
        [AlarmHardStop] = "Hard stop",
        [AlarmUnhomed] = "Homing required",
        [15] = "Initialisation failure",
    };

    public static string ErrorDescription(int code) => Errors.TryGetValue(code, out var text) ? text : $"Error {code}";

    public static string AlarmDescription(int code) => Alarms.TryGetValue(code, out var text) ? text : $"Alarm {code}";

    /// <summary>Alarms after which the machine position can no longer be trusted (re-homing required).</summary>
    public static bool AlarmLosesPosition(int code) => code is AlarmHardLimit or AlarmAbortCycle or AlarmHomingFailReset
        or AlarmControlPin or AlarmHardStop or AlarmUnhomed or >= 7 and <= 9 or 12 or 15;
}
