using System.Diagnostics;
using System.Globalization;
using System.Text;

using ThreeCTool.Core.Machines;

namespace ThreeCTool.FluidNcSimulator;

public enum SimState
{
    Idle,
    Run,
    Hold,
    Jog,
    Home,
    Alarm,
}

/// <summary>
/// Simulated FluidNC controller for streamer and recovery tests: Grbl line protocol with per-channel RX buffers,
/// a planner, constant-velocity motion (scaled time), feed hold, soft reset, alarms, probing, homing, work offsets
/// and fault injection (e-stop, power loss, dropped connections).
/// </summary>
public sealed class Simulator : IDisposable
{
    private const double MmPerInch = 25.4;
    private const string AllowedLetters = "GMFSTNXYZABIJKRPLEQ";
    private static readonly double[] SupportedG =
        [0, 1, 2, 3, 4, 10, 17, 18, 19, 20, 21, 28, 28.1, 30, 30.1, 38.2, 38.3, 38.4, 38.5, 43.1, 49, 53, 54, 55, 56, 57, 58, 59, 80, 90, 90.1, 91, 91.1, 92, 92.1, 93, 94];

    private static readonly double[] SupportedM = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 30, 62, 63, 64, 65, 67, 68];

    private readonly object gate = new();
    private readonly List<SimChannel> channels = [];
    private readonly Queue<PlannerBlock> planner = new();
    private readonly Queue<Step> steps = new();
    private readonly double[][] wcs;
    private readonly int axisCount;
    private readonly Thread thread;
    private readonly List<string> receivedLines = [];
    private volatile bool running = true;
    private double[] mpos;
    private readonly double[] physical;
    private double[] parserPos;
    private readonly double[] g92;
    private readonly double[] g28;
    private readonly double[] g30;
    private double[] probePosition;
    private bool probeSuccess;
    private double tlo;
    private double toolLength;
    private Modal modal = new();
    private PlannerBlock? current;
    private SimChannel? lineChannel;
    private bool okPending;
    private readonly HashSet<SimChannel> restartedChannels = [];
    private bool critical;
    private int holdSub;
    private double holdTimer;
    private bool resumeAfterHold;
    private bool jogCancel;
    private bool m0Suspended;
    private bool probeContact;
    private int feedOverride = 100;
    private int rapidOverride = 100;
    private int spindleOverride = 100;
    private int reportCount;
    private bool overridesChanged = true;
    private bool offsetChanged = true;
    private double currentSpeed;

    public Simulator(SimulatorOptions? options = null)
    {
        Options = options ?? new SimulatorOptions();
        axisCount = Options.Axes.Count;
        mpos = new double[axisCount];
        physical = new double[axisCount];
        parserPos = new double[axisCount];
        g92 = new double[axisCount];
        g28 = new double[axisCount];
        g30 = new double[axisCount];
        probePosition = new double[axisCount];
        wcs = [.. Enumerable.Range(0, 6).Select(_ => new double[axisCount])];
        State = Options.MustHome ? SimState.Alarm : SimState.Idle;
        thread = new Thread(Loop) { IsBackground = true, Name = "FluidNC simulator" };
        thread.Start();
    }

    public SimulatorOptions Options { get; }

    public SimState State { get; private set; }

    public bool IsPowered { get; private set; } = true;

    public bool IsHomed { get; private set; }

    public int ExecutedBlocks { get; private set; }

    public int RxOverflows { get; private set; }

    public int MaxRxFill { get; private set; }

    public int HomingCount { get; private set; }

    public int ProbeCount { get; private set; }

    public int SoftResetCount { get; private set; }

    public int Spindle => Read(() => modal.Spindle);

    public bool Flood => Read(() => modal.Flood);

    public double ToolLengthOffset => Read(() => tlo);

    /// <summary>Length of the tool in the spindle (mm): a longer tool touches a probe surface at a higher machine Z.</summary>
    public double ToolLength
    {
        get => Read(() => toolLength);
        set
        {
            lock (gate)
            {
                toolLength = value;
            }
        }
    }

    public int FeedOverride => Read(() => feedOverride);

    public Dictionary<int, bool> DigitalOutputs { get; } = [];

    public Dictionary<int, double> AnalogOutputs { get; } = [];

    public string Banner => $"Grbl 3.9 [FluidNC v{Options.Version} (sim) '$' for help]";

    public IReadOnlyList<string> ReceivedLines => Read(() => receivedLines.ToArray());

    /// <summary>Position the controller believes it is at (differs from <see cref="PhysicalPosition"/> after lost steps).</summary>
    public double[] MachinePosition => Read(() => (double[])mpos.Clone());

    public double[] PhysicalPosition => Read(() => (double[])physical.Clone());

    public double[] WorkOffset(int index) => Read(() => (double[])wcs[index].Clone());

    /// <summary>Overwrites a stored work offset (e.g. to emulate lost or corrupted controller flash).</summary>
    public void SetWorkOffset(int index, params double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (gate)
        {
            Array.Clear(wcs[index]);
            Array.Copy(values, wcs[index], Math.Min(values.Length, axisCount));
            offsetChanged = true;
        }
    }

    public SimChannel Connect(string name, Action<string> send, Action? close = null)
    {
        var channel = new SimChannel(name, send, close);
        lock (gate)
        {
            channels.Add(channel);
        }

        return channel;
    }

    public void Disconnect(SimChannel channel)
    {
        lock (gate)
        {
            channels.Remove(channel);
        }
    }

    /// <summary>Bytes from a client: real-time commands act immediately, the rest goes to the channel's RX buffer.</summary>
    public void Receive(SimChannel channel, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (gate)
        {
            if (!IsPowered)
            {
                return;
            }

            foreach (var b in data)
            {
                if (!Realtime(channel, b))
                {
                    if (channel.Rx.Count >= Options.RxBufferSize)
                    {
                        RxOverflows++;
                    }
                    else
                    {
                        channel.Rx.Enqueue(b);
                        MaxRxFill = Math.Max(MaxRxFill, channel.Rx.Count);
                    }
                }
            }
        }
    }

    /// <summary>E-stop through the reset pin: motion aborted, steps lost, alarm until homed.</summary>
    public void EmergencyStop()
    {
        lock (gate)
        {
            LosePosition();
            Broadcast("ALARM:3");
            ResetVolatile();
            State = SimState.Alarm;
            Broadcast(Banner);
            Broadcast("[MSG:'$H'|'$X' to unlock]");
        }
    }

    /// <summary>Controller power lost: clients dropped, volatile state and position lost; G54-G59 persist in flash.</summary>
    public void PowerLoss()
    {
        List<SimChannel> dropped;
        lock (gate)
        {
            IsPowered = false;
            ResetVolatile();
            mpos = new double[axisCount];
            parserPos = new double[axisCount];
            IsHomed = false;
            critical = false;
            dropped = [.. channels.Where(c => c.CanClose)];
            channels.RemoveAll(c => c.CanClose);
        }

        dropped.ForEach(c => c.Close());
    }

    public void PowerOn()
    {
        lock (gate)
        {
            IsPowered = true;
            State = Options.MustHome ? SimState.Alarm : SimState.Idle;
            Broadcast(Banner);
            if (State == SimState.Alarm)
            {
                Broadcast("[MSG:'$H'|'$X' to unlock]");
            }
        }
    }

    /// <summary>Drops all network clients; the controller keeps executing what it has buffered.</summary>
    public void DropConnections()
    {
        List<SimChannel> dropped;
        lock (gate)
        {
            dropped = [.. channels.Where(c => c.CanClose)];
            channels.RemoveAll(c => c.CanClose);
        }

        dropped.ForEach(c => c.Close());
    }

    public bool WaitUntil(Func<Simulator, bool> condition, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition(this))
            {
                return true;
            }

            Thread.Sleep(2);
        }

        return condition(this);
    }

    public void Dispose()
    {
        running = false;
        thread.Join();
    }

    private static string Format(IEnumerable<double> values) =>
        string.Join(',', values.Select(v => v.ToString("0.000", CultureInfo.InvariantCulture)));

    /// <summary>How far the tool tip is below the probe surface under it (≥ 0 = contact).</summary>
    private double ProbeDepth()
    {
        var surface = Options.ProbeSurfaceAt?.Invoke(physical[0], physical[1]) ?? Options.ProbeSurfaceMachineZ;
        return surface - (physical[2] - toolLength);
    }

    /// <summary>Numbered parameters readable by expressions: probe result, work offsets, tool length offset.</summary>
    private double? Parameter(int number)
    {
        if (number is >= 5061 and <= 5069)
        {
            return number - 5061 < axisCount ? probePosition[number - 5061] : 0;
        }

        if (number == 5070)
        {
            return probeSuccess ? 1 : 0;
        }

        if (number is >= 5221 and <= 5329)
        {
            var (system, axis) = Math.DivRem(number - 5221, 20);
            return axis < axisCount ? wcs[system][axis] : axis < 9 ? 0 : null;
        }

        return number == 5403 ? tlo : null;
    }

    private static (int A, int B, int Linear) PlaneAxes(int plane) => plane switch
    {
        18 => (2, 0, 1),
        19 => (1, 2, 0),
        _ => (0, 1, 2),
    };

    private T Read<T>(Func<T> read)
    {
        lock (gate)
        {
            return read();
        }
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        while (running)
        {
            Thread.Sleep(1);
            var now = sw.Elapsed;
            var dt = (now - last).TotalSeconds * Options.TimeScale;
            last = now;
            lock (gate)
            {
                if (IsPowered)
                {
                    Tick(dt);
                }
            }
        }
    }

    private void Tick(double dt)
    {
        if (State == SimState.Hold && holdSub == 1)
        {
            Move(dt, 0.5);
            holdTimer -= dt;
            if (holdTimer <= 0)
            {
                holdSub = 0;
                currentSpeed = 0;
                if (jogCancel)
                {
                    jogCancel = false;
                    current = null;
                    planner.Clear();
                    parserPos = (double[])mpos.Clone();
                    State = SimState.Idle;
                }
                else if (resumeAfterHold)
                {
                    Resume();
                }
            }
        }
        else if (State is SimState.Idle or SimState.Run or SimState.Jog)
        {
            Move(dt, 1);
        }

        ProcessParser(dt);
    }

    private bool MotionActive => current is not null || planner.Count > 0;

    private void Move(double dt, double factor)
    {
        var remaining = dt;
        currentSpeed = 0;
        while (remaining > 1e-12)
        {
            if (current is null)
            {
                if (!planner.TryDequeue(out var next))
                {
                    if (State is SimState.Run or SimState.Jog)
                    {
                        State = SimState.Idle;
                    }

                    return;
                }

                current = next;
                current.Start = (double[])mpos.Clone();
                current.Length = Distance(current.Start, current.Target);
                if (State is SimState.Idle)
                {
                    State = current.Jog ? SimState.Jog : SimState.Run;
                }
            }

            var block = current;
            var speed = Speed(block) * factor;
            var left = block.Length - block.Done;
            if (left <= 1e-12 || speed <= 0)
            {
                SetPosition(block.Target);
                Finish();
                continue;
            }

            currentSpeed = speed;
            var travel = speed * remaining;
            if (travel >= left)
            {
                remaining -= left / speed;
                block.Done = block.Length;
                SetPosition(block.Target);
            }
            else
            {
                block.Done += travel;
                remaining = 0;
                var t = block.Done / block.Length;
                SetPosition([.. block.Start!.Select((s, i) => s + ((block.Target[i] - s) * t))]);
            }

            if (block.Probe && block.Target[2] < block.Start![2] && ProbeDepth() >= 0)
            {
                // Contact: stop exactly at the surface.
                var overshoot = ProbeDepth();
                var adjusted = (double[])mpos.Clone();
                adjusted[2] += overshoot;
                SetPosition(adjusted);
                probeContact = true;
                current = null;
                ExecutedBlocks++;
                parserPos = (double[])mpos.Clone();
                return;
            }

            if (block.Done >= block.Length)
            {
                Finish();
            }
        }
    }

    private void Finish()
    {
        current = null;
        ExecutedBlocks++;
    }

    private double Speed(PlannerBlock block)
    {
        double perMinute;
        if (block.Duration > 0)
        {
            perMinute = block.Length / block.Duration * 60 * feedOverride / 100;
        }
        else if (block.Rapid)
        {
            perMinute = Options.RapidRate * rapidOverride / 100;
        }
        else
        {
            perMinute = Math.Min(block.Rate, Options.RapidRate) * (block.Jog ? 100 : feedOverride) / 100;
        }

        return perMinute / 60;
    }

    private static double Distance(double[] a, double[] b)
    {
        var xyz = 0.0;
        for (var i = 0; i < Math.Min(3, a.Length); i++)
        {
            xyz += (b[i] - a[i]) * (b[i] - a[i]);
        }

        if (xyz > 1e-18)
        {
            return Math.Sqrt(xyz);
        }

        var other = 0.0;
        for (var i = 3; i < a.Length; i++)
        {
            other = Math.Max(other, Math.Abs(b[i] - a[i]));
        }

        return other;
    }

    private void SetPosition(double[] target)
    {
        for (var i = 0; i < axisCount; i++)
        {
            physical[i] += target[i] - mpos[i];
            mpos[i] = target[i];
        }
    }

    private void ProcessParser(double dt)
    {
        for (var guard = 0; guard < 64; guard++)
        {
            if (critical)
            {
                channels.ForEach(c => c.Rx.Clear());
                return;
            }

            while (steps.Count > 0)
            {
                if (!Advance(steps.Peek(), ref dt))
                {
                    return;
                }

                steps.Dequeue();
            }

            if (okPending)
            {
                lineChannel?.Send("ok");
                okPending = false;
            }

            lineChannel = null;
            var channel = channels.Find(c => c.HasLine);
            if (channel is null)
            {
                return;
            }

            var bytes = new List<byte>();
            while (channel.Rx.TryDequeue(out var b) && b != (byte)'\n')
            {
                if (b != (byte)'\r')
                {
                    bytes.Add(b);
                }
            }

            var text = Encoding.UTF8.GetString([.. bytes]).Trim();
            receivedLines.Add(text);
            lineChannel = channel;
            var response = Execute(channel, text);
            if (response is not null)
            {
                channel.Send(response);
            }
            else
            {
                okPending = !critical && State != SimState.Alarm || steps.Count > 0;
            }
        }
    }

    private bool Advance(Step step, ref double dt)
    {
        switch (step.Kind)
        {
            case StepKind.Blocks:
                while (step.Blocks!.Count > 0 && planner.Count < Options.PlannerBlocks)
                {
                    planner.Enqueue(step.Blocks.Dequeue());
                }

                return step.Blocks.Count == 0;
            case StepKind.Sync:
                if (MotionActive)
                {
                    return false;
                }

                step.Action!();
                return true;
            case StepKind.Dwell:
                if (MotionActive)
                {
                    return false;
                }

                step.Seconds -= dt;
                dt = 0;
                return step.Seconds <= 0;
            case StepKind.Probe:
                return AdvanceProbe(step);
            case StepKind.Pause:
                if (MotionActive)
                {
                    return false;
                }

                if (step.Phase == 0)
                {
                    step.Phase = 1;
                    State = SimState.Hold;
                    holdSub = 0;
                    m0Suspended = true;
                }

                return !m0Suspended;
            case StepKind.Homing:
                step.Seconds -= dt;
                dt = 0;
                if (step.Seconds > 0)
                {
                    return false;
                }

                for (var i = 0; i < axisCount; i++)
                {
                    if (Options.Axes[i].HomingCycle > 0)
                    {
                        mpos[i] = physical[i] = Options.Axes[i].HomingMachinePosition;
                    }
                }

                parserPos = (double[])mpos.Clone();
                IsHomed = true;
                HomingCount++;
                State = SimState.Idle;
                return true;
            default:
                return true;
        }
    }

    private bool AdvanceProbe(Step step)
    {
        if (MotionActive)
        {
            return false;
        }

        if (step.Phase == 0)
        {
            if (ProbeDepth() >= 0 && step.Code is 38.2 or 38.3)
            {
                Alarm(4);
                return false;
            }

            step.Phase = 1;
            probeContact = false;
            planner.Enqueue(step.Probe!);
            return false;
        }

        ProbeCount++;
        probeSuccess = probeContact;
        probePosition = (double[])mpos.Clone();
        parserPos = (double[])mpos.Clone();
        if (!probeContact && step.Code is 38.2 or 38.4)
        {
            Alarm(5);
            return false;
        }

        lineChannel?.Send($"[PRB:{Format(probePosition)}:{(probeSuccess ? 1 : 0)}]");
        return true;
    }

    private void Alarm(int code)
    {
        ResetMotion();
        State = SimState.Alarm;
        okPending = false;
        steps.Clear();
        channels.ForEach(c => c.Rx.Clear());
        Broadcast($"ALARM:{code}");
    }

    private void ResetMotion()
    {
        planner.Clear();
        current = null;
        currentSpeed = 0;
        parserPos = (double[])mpos.Clone();
    }

    private string? Execute(SimChannel channel, string text)
    {
        if (text.Length == 0)
        {
            return "ok";
        }

        if (Options.RestartOnFirstLine && restartedChannels.Add(channel))
        {
            ResetVolatile();
            IsHomed = false;
            State = SimState.Alarm;
            Broadcast(Banner);
            Broadcast("[MSG:INFO: ALARM: Unhomed]");
            Broadcast("ALARM:14");
            return null;
        }

        if (text.StartsWith('$'))
        {
            return System(channel, text);
        }

        if (State == SimState.Alarm)
        {
            return "error:9";
        }

        var code = SimLine.TryParse(text, out var line, Parameter);
        if (code != 0)
        {
            return $"error:{code}";
        }

        foreach (var (letter, value) in line.Words)
        {
            if (!AllowedLetters.Contains(letter, StringComparison.Ordinal)
                || (letter == 'G' && !SupportedG.Any(g => Math.Abs(g - value) < 1e-6))
                || (letter == 'M' && !SupportedM.Any(m => Math.Abs(m - value) < 1e-6)))
            {
                return "error:20";
            }
        }

        return ExecuteGcode(line);
    }

    private string? ExecuteGcode(SimLine line)
    {
        var lineNumber = (int)(line.Get('N') ?? 0);
        if (line.HasG(93))
        {
            modal.InverseTime = true;
        }

        if (line.HasG(94))
        {
            modal.InverseTime = false;
        }

        foreach (var g in line.All('G'))
        {
            switch (g)
            {
                case 17 or 18 or 19:
                    modal.Plane = (int)g;
                    break;
                case 20:
                    modal.Inches = true;
                    break;
                case 21:
                    modal.Inches = false;
                    break;
                case 90:
                    modal.Incremental = false;
                    break;
                case 91:
                    modal.Incremental = true;
                    break;
                case >= 54 and <= 59 when g == Math.Floor(g):
                    modal.Wcs = (int)g - 54;
                    offsetChanged = true;
                    break;
                default:
                    if (Math.Abs(g - 90.1) < 1e-6)
                    {
                        modal.ArcAbsolute = true;
                    }
                    else if (Math.Abs(g - 91.1) < 1e-6)
                    {
                        modal.ArcAbsolute = false;
                    }

                    break;
            }
        }

        var unit = modal.Inches ? MmPerInch : 1;
        double? inverseFeed = null;
        if (line.Get('F') is { } f)
        {
            if (modal.InverseTime)
            {
                inverseFeed = f;
            }
            else
            {
                modal.Feed = f * unit;
            }
        }

        var spindleSpeed = line.Get('S');
        if (spindleSpeed is { } s)
        {
            modal.SpindleSpeed = s;
        }

        if (line.Get('T') is { } tool)
        {
            modal.Tool = (int)tool;
        }

        if (line.HasM(3) || line.HasM(4) || line.HasM(5))
        {
            var value = line.HasM(3) ? 3 : line.HasM(4) ? 4 : 5;
            steps.Enqueue(Step.Sync(() => modal.Spindle = value));
        }

        if (line.HasM(7) || line.HasM(8) || line.HasM(9))
        {
            var mist = line.HasM(7);
            var flood = line.HasM(8);
            var off = line.HasM(9);
            steps.Enqueue(Step.Sync(() =>
            {
                modal.Mist = !off && (mist || modal.Mist);
                modal.Flood = !off && (flood || modal.Flood);
            }));
        }

        var port = (int)(line.Get('P') ?? 0);
        if (line.HasM(62) || line.HasM(63))
        {
            var on = line.HasM(62);
            steps.Enqueue(Step.Sync(() => DigitalOutputs[port] = on));
        }

        if (line.HasM(64) || line.HasM(65))
        {
            DigitalOutputs[port] = line.HasM(64);
        }

        if ((line.HasM(67) || line.HasM(68)) && line.Get('E') is { } analogPort)
        {
            var value = line.Get('Q') ?? 0;
            if (line.HasM(67))
            {
                steps.Enqueue(Step.Sync(() => AnalogOutputs[(int)analogPort] = value));
            }
            else
            {
                AnalogOutputs[(int)analogPort] = value;
            }
        }

        if (line.HasG(4))
        {
            steps.Enqueue(Step.Dwell(line.Get('P') ?? 0));
        }

        var axisWords = new List<(int Axis, double Value)>();
        foreach (var (letter, value) in line.Words)
        {
            var axis = "XYZAB".IndexOf(letter, StringComparison.Ordinal);
            if (axis >= 0 && axis < axisCount)
            {
                axisWords.Add((axis, axis < 3 ? value * unit : value));
            }
        }

        if (line.HasG(43.1))
        {
            tlo = (line.Get('Z') ?? 0) * unit;
            offsetChanged = true;
            return Finish(line);
        }

        if (line.HasG(49))
        {
            tlo = 0;
            offsetChanged = true;
        }

        if (line.HasG(10))
        {
            var p = (int)(line.Get('P') ?? 0);
            var index = p == 0 ? modal.Wcs : p - 1;
            var l = (int)(line.Get('L') ?? 0);
            if (index is < 0 or > 5 || l is not (2 or 20))
            {
                return "error:29";
            }

            foreach (var (axis, value) in axisWords)
            {
                wcs[index][axis] = l == 2 ? value : parserPos[axis] - g92[axis] - (axis == 2 ? tlo : 0) - value;
            }

            offsetChanged = true;
            return Finish(line);
        }

        if (line.HasG(28.1) || line.HasG(30.1))
        {
            Array.Copy(parserPos, line.HasG(28.1) ? g28 : g30, axisCount);
            return Finish(line);
        }

        if (line.HasG(28) || line.HasG(30))
        {
            var blocks = new Queue<PlannerBlock>();
            var intermediate = (double[])parserPos.Clone();
            foreach (var (axis, value) in axisWords)
            {
                intermediate[axis] = modal.Incremental ? parserPos[axis] + value : value + Offset(axis);
            }

            var stored = line.HasG(28) ? g28 : g30;
            var final = (double[])intermediate.Clone();
            for (var i = 0; i < axisCount; i++)
            {
                if (axisWords.Count == 0 || axisWords.Exists(w => w.Axis == i))
                {
                    final[i] = stored[i];
                }
            }

            if (SoftLimitViolated(intermediate) || SoftLimitViolated(final))
            {
                return SoftLimitAlarm();
            }

            blocks.Enqueue(new PlannerBlock(intermediate) { Rapid = true, Line = lineNumber });
            blocks.Enqueue(new PlannerBlock(final) { Rapid = true, Line = lineNumber });
            steps.Enqueue(Step.Queue(blocks));
            parserPos = final;
            return Finish(line);
        }

        if (line.HasG(92))
        {
            foreach (var (axis, value) in axisWords)
            {
                g92[axis] = parserPos[axis] - wcs[modal.Wcs][axis] - (axis == 2 ? tlo : 0) - value;
            }

            offsetChanged = true;
            return Finish(line);
        }

        if (line.HasG(92.1))
        {
            Array.Clear(g92);
            offsetChanged = true;
            return Finish(line);
        }

        foreach (var g in line.All('G'))
        {
            if (g is 0 or 1 or 2 or 3 or 80 || g is >= 38 and < 39)
            {
                modal.Motion = g;
            }
        }

        if (axisWords.Count > 0 && modal.Motion != 80)
        {
            var result = Motion(line, axisWords, lineNumber, inverseFeed);
            if (result is not null)
            {
                return result;
            }
        }

        return Finish(line);
    }

    private string? Motion(SimLine line, List<(int Axis, double Value)> axisWords, int lineNumber, double? inverseFeed)
    {
        var g53 = line.HasG(53);
        var target = (double[])parserPos.Clone();
        foreach (var (axis, value) in axisWords)
        {
            target[axis] = g53 ? value : modal.Incremental ? parserPos[axis] + value : value + Offset(axis);
        }

        var motion = modal.Motion;
        var rapid = motion == 0;
        if (!rapid && modal.InverseTime && inverseFeed is not > 0)
        {
            return "error:22";
        }

        if (!rapid && !modal.InverseTime && modal.Feed <= 0)
        {
            return "error:22";
        }

        var duration = modal.InverseTime && !rapid ? 60 / inverseFeed!.Value : 0;
        var blocks = new Queue<PlannerBlock>();
        if (motion is 2 or 3 && !g53)
        {
            var arc = Arc(line, parserPos, target, motion == 2);
            if (arc is null)
            {
                return "error:33";
            }

            foreach (var point in arc)
            {
                blocks.Enqueue(new PlannerBlock(point) { Rate = modal.Feed, Duration = duration / arc.Count, Line = lineNumber });
            }
        }
        else if (motion is >= 38 and < 39)
        {
            if (SoftLimitViolated(target))
            {
                return SoftLimitAlarm();
            }

            steps.Enqueue(Step.ProbeMove(new PlannerBlock(target) { Rate = modal.Feed, Probe = true, Line = lineNumber }, motion));
            parserPos = target;
            return null;
        }
        else
        {
            blocks.Enqueue(new PlannerBlock(target) { Rapid = rapid, Rate = modal.Feed, Duration = duration, Line = lineNumber });
        }

        if (blocks.Any(b => SoftLimitViolated(b.Target)))
        {
            return SoftLimitAlarm();
        }

        steps.Enqueue(Step.Queue(blocks));
        parserPos = target;
        return null;
    }

    private List<double[]>? Arc(SimLine line, double[] start, double[] end, bool clockwise)
    {
        var (a, b, _) = PlaneAxes(modal.Plane);
        var unit = modal.Inches ? MmPerInch : 1;
        double ca, cb;
        if (line.Get('R') is { } rWord)
        {
            var r = rWord * unit;
            var x = end[a] - start[a];
            var y = end[b] - start[b];
            var h2 = (4 * r * r) - (x * x) - (y * y);
            if (h2 < -1e-9)
            {
                return null;
            }

            var h = -Math.Sqrt(Math.Max(0, h2)) / Math.Sqrt((x * x) + (y * y));
            if (!clockwise)
            {
                h = -h;
            }

            if (r < 0)
            {
                h = -h;
            }

            ca = start[a] + ((x - (y * h)) / 2);
            cb = start[b] + ((y + (x * h)) / 2);
        }
        else
        {
            var ijk = new[] { line.Get('I') ?? 0, line.Get('J') ?? 0, line.Get('K') ?? 0 };
            if (modal.ArcAbsolute)
            {
                ca = (ijk[a] * unit) + Offset(a);
                cb = (ijk[b] * unit) + Offset(b);
            }
            else
            {
                ca = start[a] + (ijk[a] * unit);
                cb = start[b] + (ijk[b] * unit);
            }
        }

        var radius = Math.Sqrt(((start[a] - ca) * (start[a] - ca)) + ((start[b] - cb) * (start[b] - cb)));
        var a0 = Math.Atan2(start[b] - cb, start[a] - ca);
        var sweep = Math.Atan2(end[b] - cb, end[a] - ca) - a0;
        if (clockwise && sweep >= -1e-9)
        {
            sweep -= 2 * Math.PI;
        }
        else if (!clockwise && sweep <= 1e-9)
        {
            sweep += 2 * Math.PI;
        }

        var tolerance = Options.ArcTolerance;
        var segments = (int)Math.Clamp(Math.Floor(Math.Abs(0.5 * sweep * radius) / Math.Sqrt(tolerance * Math.Max((2 * radius) - tolerance, tolerance))), 1, 500);
        var points = new List<double[]>(segments);
        for (var k = 1; k <= segments; k++)
        {
            var t = k / (double)segments;
            var point = new double[axisCount];
            for (var i = 0; i < axisCount; i++)
            {
                point[i] = start[i] + ((end[i] - start[i]) * t);
            }

            if (k < segments)
            {
                var angle = a0 + (sweep * t);
                point[a] = ca + (radius * Math.Cos(angle));
                point[b] = cb + (radius * Math.Sin(angle));
            }

            points.Add(point);
        }

        return points;
    }

    /// <summary>Program flow at the end of a line; returns null (ok after the queued steps) or an immediate response.</summary>
    private string? Finish(SimLine line)
    {
        if (line.HasM(0))
        {
            steps.Enqueue(Step.ProgramPause());
        }

        if (line.HasM(2) || line.HasM(30))
        {
            steps.Enqueue(Step.Sync(() =>
            {
                modal = new Modal { Feed = modal.Feed, SpindleSpeed = modal.SpindleSpeed, Tool = modal.Tool, Inches = modal.Inches };
                offsetChanged = true;
            }));
        }

        return null;
    }

    private double Offset(int axis) => wcs[modal.Wcs][axis] + g92[axis] + (axis == 2 ? tlo : 0);

    private bool SoftLimitViolated(double[] target)
    {
        for (var i = 0; i < axisCount; i++)
        {
            var axis = Options.Axes[i];
            if (!axis.SoftLimits || axis.MaxTravel <= 0)
            {
                continue;
            }

            var min = axis.HomingPositiveDirection ? axis.HomingMachinePosition - axis.MaxTravel : axis.HomingMachinePosition;
            if (target[i] < min - 1e-6 || target[i] > min + axis.MaxTravel + 1e-6)
            {
                return true;
            }
        }

        return false;
    }

    private string? SoftLimitAlarm()
    {
        // Grbl: soft limit violation at parse time is a critical event; the controller waits for a reset.
        ResetMotion();
        steps.Clear();
        State = SimState.Alarm;
        critical = true;
        okPending = false;
        Broadcast("ALARM:2");
        Broadcast("[MSG:Reset to continue]");
        return null;
    }

    private string? System(SimChannel channel, string text)
    {
        var upper = text.ToUpperInvariant();
        switch (upper)
        {
            case "$X":
                if (State == SimState.Alarm)
                {
                    State = SimState.Idle;
                    channel.Send("[MSG:Caution: Unlocked]");
                }

                return "ok";
            case "$H":
                if (State is not (SimState.Idle or SimState.Alarm))
                {
                    return "error:8";
                }

                State = SimState.Home;
                steps.Enqueue(Step.Home(Options.HomingSeconds));
                return null;
            case "$#":
                for (var i = 0; i < 6; i++)
                {
                    channel.Send($"[G{54 + i}:{Format(wcs[i])}]");
                }

                channel.Send($"[G28:{Format(g28)}]");
                channel.Send($"[G30:{Format(g30)}]");
                channel.Send($"[G92:{Format(g92)}]");
                channel.Send($"[TLO:{tlo.ToString("0.000", CultureInfo.InvariantCulture)}]");
                channel.Send($"[PRB:{Format(probePosition)}:{(probeSuccess ? 1 : 0)}]");
                return "ok";
            case "$G":
                var coolant = modal.Mist ? "M7" : modal.Flood ? "M8" : "M9";
                channel.Send(string.Create(CultureInfo.InvariantCulture,
                    $"[GC:G{modal.Motion} G{54 + modal.Wcs} G{modal.Plane} G{(modal.Inches ? 20 : 21)} G{(modal.Incremental ? 91 : 90)} G{(modal.InverseTime ? 93 : 94)} M{modal.Spindle} {coolant} T{modal.Tool} F{modal.Feed} S{modal.SpindleSpeed}]"));
                return "ok";
            case "$I" or "$BUILD/INFO":
                channel.Send($"[VER:3.9 FluidNC v{Options.Version} (sim):]");
                channel.Send(string.Create(CultureInfo.InvariantCulture, $"[OPT:PHS,{Options.PlannerBlocks},{Options.RxBufferSize}]"));
                channel.Send("[MSG: Machine: 3C Tool simulator]");
                return "ok";
            case "$CD" or "$CONFIG/DUMP":
                foreach (var configLine in ConfigDump())
                {
                    channel.Send(configLine);
                }

                return "ok";
            case "$$":
                channel.Send("$10=1");
                channel.Send("$22=1");
                return "ok";
            default:
                return upper.StartsWith("$J=", StringComparison.Ordinal) ? Jog(text[3..]) : "error:3";
        }
    }

    private string Jog(string command)
    {
        if (State == SimState.Alarm)
        {
            return "error:9";
        }

        if (State is not (SimState.Idle or SimState.Jog))
        {
            return "error:8";
        }

        var code = SimLine.TryParse(command, out var line);
        if (code != 0)
        {
            return $"error:{code}";
        }

        var unit = line.HasG(20) ? MmPerInch : line.HasG(21) ? 1 : modal.Inches ? MmPerInch : 1;
        var incremental = line.HasG(91) || (!line.HasG(90) && modal.Incremental);
        if (line.Get('F') is not { } feed)
        {
            return "error:22";
        }

        var target = (double[])parserPos.Clone();
        foreach (var (letter, value) in line.Words)
        {
            var axis = "XYZAB".IndexOf(letter, StringComparison.Ordinal);
            if (axis < 0 || axis >= axisCount)
            {
                continue;
            }

            var v = axis < 3 ? value * unit : value;
            target[axis] = line.HasG(53) ? v : incremental ? parserPos[axis] + v : v + Offset(axis);
        }

        if (SoftLimitViolated(target))
        {
            return "error:15";
        }

        planner.Enqueue(new PlannerBlock(target) { Rate = feed * unit, Jog = true });
        parserPos = target;
        return "ok";
    }

    private bool Realtime(SimChannel channel, byte b)
    {
        switch (b)
        {
            case (byte)'?':
                channel.Send(StatusReport(channel));
                return true;
            case (byte)'!':
                FeedHold();
                return true;
            case (byte)'~':
                if (State == SimState.Hold)
                {
                    if (holdSub == 1)
                    {
                        resumeAfterHold = true;
                    }
                    else
                    {
                        Resume();
                    }
                }

                return true;
            case 0x18:
                SoftReset();
                return true;
            case 0x85:
                if (State == SimState.Jog)
                {
                    State = SimState.Hold;
                    holdSub = 1;
                    holdTimer = Options.HoldDecelSeconds;
                    jogCancel = true;
                }

                return true;
            case >= 0x80:
                ApplyOverride(b);
                return true;
            default:
                return false;
        }
    }

    private void ApplyOverride(byte b)
    {
        static int Clamp(int v) => Math.Clamp(v, 10, 200);
        switch (b)
        {
            case 0x90: feedOverride = 100; break;
            case 0x91: feedOverride = Clamp(feedOverride + 10); break;
            case 0x92: feedOverride = Clamp(feedOverride - 10); break;
            case 0x93: feedOverride = Clamp(feedOverride + 1); break;
            case 0x94: feedOverride = Clamp(feedOverride - 1); break;
            case 0x95: rapidOverride = 100; break;
            case 0x96: rapidOverride = 50; break;
            case 0x97: rapidOverride = 25; break;
            case 0x99: spindleOverride = 100; break;
            case 0x9A: spindleOverride = Clamp(spindleOverride + 10); break;
            case 0x9B: spindleOverride = Clamp(spindleOverride - 10); break;
            case 0x9C: spindleOverride = Clamp(spindleOverride + 1); break;
            case 0x9D: spindleOverride = Clamp(spindleOverride - 1); break;
            case 0xA0: modal.Flood = !modal.Flood; break;
            case 0xA1: modal.Mist = !modal.Mist; break;
            default: return;
        }

        overridesChanged = true;
    }

    private void FeedHold()
    {
        switch (State)
        {
            case SimState.Run or SimState.Jog:
                jogCancel = State == SimState.Jog;
                State = SimState.Hold;
                holdSub = 1;
                holdTimer = Options.HoldDecelSeconds;
                break;
            case SimState.Idle:
                State = SimState.Hold;
                holdSub = 0;
                break;
            default:
                break;
        }
    }

    private void Resume()
    {
        resumeAfterHold = false;
        m0Suspended = false;
        State = MotionActive ? SimState.Run : SimState.Idle;
    }

    private void SoftReset()
    {
        SoftResetCount++;
        var moving = State is SimState.Run or SimState.Jog or SimState.Home || (State == SimState.Hold && holdSub == 1);
        var alarmed = State == SimState.Alarm || moving;
        if (moving)
        {
            LosePosition();
            Broadcast("ALARM:3");
        }

        ResetVolatile();
        critical = false;
        State = alarmed || (Options.MustHome && !IsHomed) ? SimState.Alarm : SimState.Idle;
        Broadcast(Banner);
        if (State == SimState.Alarm)
        {
            Broadcast("[MSG:'$H'|'$X' to unlock]");
        }
    }

    private void LosePosition()
    {
        mpos[0] += Options.LostStepsMm;
        IsHomed = false;
    }

    private void ResetVolatile()
    {
        ResetMotion();
        steps.Clear();
        okPending = false;
        lineChannel = null;
        channels.ForEach(c => c.Rx.Clear());
        modal = new Modal();
        Array.Clear(g92);
        tlo = 0;
        holdSub = 0;
        resumeAfterHold = false;
        jogCancel = false;
        m0Suspended = false;
        feedOverride = rapidOverride = spindleOverride = 100;
        overridesChanged = true;
        offsetChanged = true;
    }

    private void Broadcast(string text) => channels.ForEach(c => c.Send(text));

    private string StatusReport(SimChannel channel)
    {
        var sb = new StringBuilder("<");
        sb.Append(State switch
        {
            SimState.Hold => $"Hold:{holdSub}",
            _ => State.ToString(),
        });
        sb.Append("|MPos:").Append(Format(mpos));
        if (Options.ReportBuffer)
        {
            sb.Append(CultureInfo.InvariantCulture, $"|Bf:{Options.PlannerBlocks - planner.Count - (current is null ? 0 : 1)},{Options.RxBufferSize - channel.Rx.Count}");
        }

        if (Options.UseLineNumbers && current is { Line: > 0 } block)
        {
            sb.Append(CultureInfo.InvariantCulture, $"|Ln:{block.Line}");
        }

        var speed = modal.Spindle == 5 ? 0 : modal.SpindleSpeed * spindleOverride / 100;
        sb.Append(CultureInfo.InvariantCulture, $"|FS:{Math.Round(currentSpeed * 60)},{Math.Round(speed)}");
        if (physical[2] <= Options.ProbeSurfaceMachineZ)
        {
            sb.Append("|Pn:P");
        }

        if (offsetChanged || reportCount % 10 == 0)
        {
            sb.Append("|WCO:").Append(Format(Enumerable.Range(0, axisCount).Select(Offset)));
            offsetChanged = false;
        }
        else if (overridesChanged || reportCount % 10 == 5)
        {
            sb.Append(CultureInfo.InvariantCulture, $"|Ov:{feedOverride},{rapidOverride},{spindleOverride}");
            overridesChanged = false;
        }

        reportCount++;
        return sb.Append('>').ToString();
    }

    private IEnumerable<string> ConfigDump()
    {
        var ci = CultureInfo.InvariantCulture;
        yield return "name: \"3C Tool simulator\"";
        yield return "board: \"sim\"";
        yield return string.Create(ci, $"planner_blocks: {Options.PlannerBlocks}");
        yield return "use_line_numbers: " + (Options.UseLineNumbers ? "true" : "false");
        yield return "start:";
        yield return "  must_home: " + (Options.MustHome ? "true" : "false");
        yield return "axes:";
        yield return "  shared_stepper_disable_pin: NO_PIN";
        var pin = 2;
        foreach (var axis in Options.Axes)
        {
            yield return "  " + axis.Axis.ToString().ToLowerInvariant() + ":";
            yield return string.Create(ci, $"    steps_per_mm: {axis.StepsPerUnit:0.000}");
            yield return string.Create(ci, $"    max_rate_mm_per_min: {axis.MaxRatePerMinute:0.000}");
            yield return string.Create(ci, $"    acceleration_mm_per_sec2: {axis.AccelerationPerSecond2:0.000}");
            yield return string.Create(ci, $"    max_travel_mm: {axis.MaxTravel:0.000}");
            yield return "    soft_limits: " + (axis.SoftLimits ? "true" : "false");
            yield return "    homing:";
            yield return string.Create(ci, $"      cycle: {axis.HomingCycle}");
            yield return "      positive_direction: " + (axis.HomingPositiveDirection ? "true" : "false");
            yield return string.Create(ci, $"      mpos_mm: {axis.HomingMachinePosition:0.000}");
            yield return "      feed_mm_per_min: 100.000";
            for (var m = 0; m < 2; m++)
            {
                yield return string.Create(ci, $"    motor{m}:");
                if (m >= axis.MotorCount)
                {
                    yield return "      null_motor:";
                    continue;
                }

                yield return "      hard_limits: false";
                yield return "      stepstick:";
                yield return string.Create(ci, $"        step_pin: gpio.{pin++}");
                yield return string.Create(ci, $"        direction_pin: gpio.{pin++}{(axis.DirectionInverted ? ":low" : string.Empty)}");
            }
        }
    }

    private sealed class Modal
    {
        public double Motion { get; set; }

        public int Plane { get; set; } = 17;

        public bool Inches { get; set; }

        public bool Incremental { get; set; }

        public bool ArcAbsolute { get; set; }

        public bool InverseTime { get; set; }

        public int Wcs { get; set; }

        public double Feed { get; set; }

        public double SpindleSpeed { get; set; }

        public int Spindle { get; set; } = 5;

        public bool Mist { get; set; }

        public bool Flood { get; set; }

        public int Tool { get; set; }
    }

    private sealed class PlannerBlock(double[] target)
    {
        public double[] Target { get; } = target;

        public double Rate { get; init; }

        /// <summary>Inverse-time duration in seconds (0 = use <see cref="Rate"/>).</summary>
        public double Duration { get; init; }

        public bool Rapid { get; init; }

        public bool Jog { get; init; }

        public bool Probe { get; init; }

        public int Line { get; init; }

        public double[]? Start { get; set; }

        public double Length { get; set; }

        public double Done { get; set; }
    }

    private enum StepKind
    {
        Blocks,
        Sync,
        Dwell,
        Probe,
        Pause,
        Homing,
    }

    private sealed class Step
    {
        public StepKind Kind { get; private init; }

        public Queue<PlannerBlock>? Blocks { get; private init; }

        public Action? Action { get; private init; }

        public double Seconds { get; set; }

        public PlannerBlock? Probe { get; private init; }

        public double Code { get; private init; }

        public int Phase { get; set; }

        public static Step Queue(Queue<PlannerBlock> blocks) => new() { Kind = StepKind.Blocks, Blocks = blocks };

        public static Step Sync(Action action) => new() { Kind = StepKind.Sync, Action = action };

        public static Step Dwell(double seconds) => new() { Kind = StepKind.Dwell, Seconds = seconds };

        public static Step ProbeMove(PlannerBlock block, double code) => new() { Kind = StepKind.Probe, Probe = block, Code = code };

        public static Step ProgramPause() => new() { Kind = StepKind.Pause };

        public static Step Home(double seconds) => new() { Kind = StepKind.Homing, Seconds = seconds };
    }
}
