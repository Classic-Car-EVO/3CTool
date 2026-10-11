using ThreeCTool.Core.Machines;

namespace ThreeCTool.FluidNcSimulator;

public sealed class SimulatorOptions
{
    /// <summary>Axes in controller order (X, Y, Z, A, B); defaults match the built-in machine profile plus an FDM extruder on B.</summary>
    public IList<AxisConfig> Axes { get; init; } = DefaultAxes();

    public int RxBufferSize { get; set; } = 128;

    public int PlannerBlocks { get; set; } = 16;

    /// <summary>Simulated seconds per real second (e.g. 20 runs motion 20x faster than real time).</summary>
    public double TimeScale { get; set; } = 1;

    /// <summary>Report <c>Ln:</c> (FluidNC <c>use_line_numbers</c>).</summary>
    public bool UseLineNumbers { get; set; }

    /// <summary>Report <c>Bf:</c> (planner blocks and RX bytes available).</summary>
    public bool ReportBuffer { get; set; } = true;

    /// <summary>Start in alarm until homed (<c>start/must_home</c>).</summary>
    public bool MustHome { get; set; } = true;

    public double RapidRate { get; set; } = 6000;

    /// <summary>Physical machine Z at which a probe move makes contact (tool setter / touch plate).</summary>
    public double ProbeSurfaceMachineZ { get; set; } = -600;

    public string Version { get; set; } = "3.9.5";

    public double HomingSeconds { get; set; } = 0.5;

    /// <summary>Deceleration time of a feed hold (Hold:1 before Hold:0).</summary>
    public double HoldDecelSeconds { get; set; } = 0.05;

    /// <summary>X error injected when the controller loses position (reset during motion, e-stop).</summary>
    public double LostStepsMm { get; set; } = 0.7;

    public double ArcTolerance { get; set; } = 0.002;

    public static List<AxisConfig> DefaultAxes() =>
    [
        Linear(AxisName.X, 1300, 2, 2, positive: false),
        Linear(AxisName.Y, 750, 1, 2, positive: false),
        Linear(AxisName.Z, 650, 2, 1, positive: true),
        new AxisConfig
        {
            Axis = AxisName.A,
            StepsPerUnit = 44.444,
            MaxRatePerMinute = 7200,
            AccelerationPerSecond2 = 200,
        },
        new AxisConfig
        {
            Axis = AxisName.B,
            StepsPerUnit = 400,
            MaxRatePerMinute = 3000,
            AccelerationPerSecond2 = 1000,
        },
    ];

    private static AxisConfig Linear(AxisName axis, double travel, int motors, int cycle, bool positive) => new()
    {
        Axis = axis,
        StepsPerUnit = 100,
        MaxRatePerMinute = 6000,
        AccelerationPerSecond2 = 300,
        MaxTravel = travel,
        SoftLimits = true,
        MotorCount = motors,
        HomingCycle = cycle,
        HomingPositiveDirection = positive,
        HomingMachinePosition = 0,
    };
}

/// <summary>One client channel (TCP connection or serial port) with its own RX buffer, like a FluidNC channel.</summary>
public sealed class SimChannel(string name, Action<string> send, Action? close = null)
{
    internal Queue<byte> Rx { get; } = new();

    public string Name { get; } = name;

    internal void Send(string line) => send(line + "\r\n");

    internal bool CanClose => close is not null;

    internal void Close() => close?.Invoke();

    internal bool HasLine => Rx.Contains((byte)'\n');
}
