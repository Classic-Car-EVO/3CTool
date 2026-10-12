using System.Globalization;

namespace ThreeCTool.Streaming.Gcode;

/// <summary>
/// Tracks modal state and positions while scanning a G-code file (FluidNC dialect) and produces motion segments.
/// </summary>
public sealed class GcodeInterpreter(double rotaryRadiusMm = 20)
{
    private const double MmPerInch = 25.4;

    public ModalState State { get; private set; } = new();

    /// <summary>Inside a <c>(MC:PROBE BEGIN)</c> ... <c>(MC:PROBE END)</c> block.</summary>
    public bool InProbeBlock { get; private set; }

    /// <summary>
    /// Lines that recovery must not resume across: program stops, tool changes, probing, offset changes and
    /// system commands. Resuming after them would skip state the controller no longer has.
    /// </summary>
    public static bool IsBarrier(GcodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.SystemCommand is not null || block.Directive is { IsMarker: true } || block.Directive is { Wait: true })
        {
            return true;
        }

        foreach (var w in block.Words)
        {
            if (w.Letter == 'M' && (w.IsCode('M', 0) || w.IsCode('M', 1) || w.IsCode('M', 2) || w.IsCode('M', 30) || w.IsCode('M', 6)))
            {
                return true;
            }

            if (w.Letter == 'G' && (w.Value is >= 38 and < 39 || w.IsCode('G', 10) || w.IsCode('G', 28) || w.IsCode('G', 30)
                || w.IsCode('G', 28.1) || w.IsCode('G', 30.1) || w.IsCode('G', 43.1) || w.IsCode('G', 49) || w.IsCode('G', 92) || w.IsCode('G', 92.1)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for blocks that change controller work offsets (reported by <c>$#</c>).</summary>
    public static bool ChangesOffsets(GcodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return block.Words.Any(w => w.Letter == 'G' && (w.IsCode('G', 10) || w.IsCode('G', 43.1) || w.IsCode('G', 49)
            || w.IsCode('G', 92) || w.IsCode('G', 92.1) || w.IsCode('G', 28.1) || w.IsCode('G', 30.1) || w.Value is >= 38 and < 39));
    }

    public void Reset(ModalState? state = null)
    {
        State = state?.Clone() ?? new ModalState();
        InProbeBlock = false;
    }

    /// <summary>Applies one block; returns the move it programs, if any.</summary>
    public MotionSegment? Apply(GcodeBlock block, int line)
    {
        ArgumentNullException.ThrowIfNull(block);
        var s = State;
        foreach (var comment in block.Comments)
        {
            if (HostDirective.TryParse(comment) is null && comment.Length > 0)
            {
                s.LastComment = comment;
            }
        }

        if (block.Directive is { } directive)
        {
            ApplyDirective(directive);
        }

        if (block.SystemCommand is { } system)
        {
            if (system.StartsWith("$H", StringComparison.OrdinalIgnoreCase) || system.StartsWith("$J", StringComparison.OrdinalIgnoreCase))
            {
                Array.Fill(s.Position, double.NaN);
            }

            return null;
        }

        if (block.Words.Count == 0)
        {
            return null;
        }

        var g = block.Words.Where(w => w.Letter == 'G').Select(w => w.Value).ToList();
        var m = block.Words.Where(w => w.Letter == 'M').Select(w => w.Value).ToList();
        bool HasG(double code) => g.Exists(v => Math.Abs(v - code) < 1e-6);
        bool HasM(double code) => m.Exists(v => Math.Abs(v - code) < 1e-6);

        if (HasG(93))
        {
            s.InverseTime = true;
        }

        if (HasG(94))
        {
            s.InverseTime = false;
        }

        if (block.Get('F') is { } f)
        {
            s.Feed = f;
        }

        if (block.Get('S') is { } speed)
        {
            s.SpindleSpeed = speed;
        }

        if (block.Get('T') is { } tool)
        {
            s.Tool = (int)tool;
        }

        if (HasM(3))
        {
            s.Spindle = 3;
        }
        else if (HasM(4))
        {
            s.Spindle = 4;
        }
        else if (HasM(5))
        {
            s.Spindle = 5;
        }

        if (HasM(7))
        {
            s.Mist = true;
        }

        if (HasM(8))
        {
            s.Flood = true;
        }

        if (HasM(9))
        {
            s.Mist = false;
            s.Flood = false;
        }

        if (block.Get('P') is { } port)
        {
            if (HasM(62) || HasM(64))
            {
                s.DigitalOutputs[(int)port] = true;
            }
            else if (HasM(63) || HasM(65))
            {
                s.DigitalOutputs[(int)port] = false;
            }
        }

        if ((HasM(67) || HasM(68)) && block.Get('E') is { } analog && block.Get('Q') is { } analogValue)
        {
            s.AnalogOutputs[(int)analog] = analogValue;
        }

        foreach (var code in g)
        {
            switch (code)
            {
                case 17 or 18 or 19:
                    s.Plane = (int)code;
                    break;
                case 20:
                    s.Inches = true;
                    break;
                case 21:
                    s.Inches = false;
                    break;
                case 90:
                    s.Incremental = false;
                    break;
                case 91:
                    s.Incremental = true;
                    break;
                case >= 54 and <= 59 when code == Math.Floor(code):
                    if (s.Wcs != (int)code)
                    {
                        s.Wcs = (int)code;
                        ForgetWorkAxes();
                    }

                    break;
                default:
                    if (Math.Abs(code - 90.1) < 1e-6)
                    {
                        s.ArcAbsolute = true;
                    }
                    else if (Math.Abs(code - 91.1) < 1e-6)
                    {
                        s.ArcAbsolute = false;
                    }

                    break;
            }
        }

        if (HasG(43.1))
        {
            // An expression (e.g. a probe result) is only known to the controller.
            var z = block.Get('Z') ?? 0;
            s.ToolLengthOffset = double.IsNaN(z) ? null : Scale(z, 2);
            ForgetWorkAxes(2);
        }
        else if (HasG(49))
        {
            s.ToolLengthOffset = 0;
            ForgetWorkAxes(2);
        }

        var axisWords = AxisWords(block);
        if (HasG(10))
        {
            var p = (int)(block.Get('P') ?? 0);
            var affectsActive = p == 0 || p + 53 == s.Wcs;
            var l = (int)(block.Get('L') ?? 0);
            if (affectsActive && (l == 2 || l == 20))
            {
                foreach (var (axis, value) in axisWords)
                {
                    if (l == 20)
                    {
                        // The current work position becomes the given value, whatever frame it was tracked in.
                        s.Position[axis] = value;
                        s.MachineAxes[axis] = false;
                    }
                    else if (!s.MachineAxes[axis])
                    {
                        s.Position[axis] = double.NaN;
                    }
                }
            }

            return FinishBlock(HasM);
        }

        if (HasG(28) || HasG(30))
        {
            Array.Fill(s.Position, double.NaN);
            return FinishBlock(HasM);
        }

        if (HasG(28.1) || HasG(30.1))
        {
            return FinishBlock(HasM);
        }

        if (HasG(92))
        {
            foreach (var (axis, value) in axisWords)
            {
                s.G92Offset[axis] += s.Position[axis] - value;
                s.G92Active[axis] = true;
                s.Position[axis] = value;
                s.MachineAxes[axis] = false;
            }

            return FinishBlock(HasM);
        }

        if (HasG(92.1))
        {
            for (var i = 0; i < ModalState.AxisCount; i++)
            {
                s.Position[i] += s.G92Offset[i];
                s.G92Offset[i] = 0;
                s.G92Active[i] = false;
            }

            return FinishBlock(HasM);
        }

        foreach (var code in g)
        {
            switch (code)
            {
                case 0:
                    s.Motion = MotionMode.Rapid;
                    break;
                case 1:
                    s.Motion = MotionMode.Linear;
                    break;
                case 2:
                    s.Motion = MotionMode.ArcClockwise;
                    break;
                case 3:
                    s.Motion = MotionMode.ArcCounterClockwise;
                    break;
                case >= 38 and < 39:
                    s.Motion = MotionMode.Probe;
                    break;
                case 80:
                    s.Motion = MotionMode.Cancel;
                    break;
                default:
                    break;
            }
        }

        MotionSegment? segment = null;
        if (axisWords.Count > 0 && s.Motion != MotionMode.Cancel)
        {
            segment = Move(block, line, axisWords, HasG(53));
        }

        return FinishBlock(HasM) ?? segment;
    }

    /// <summary>Formats a coordinate for the controller (mm, 4 decimals, invariant).</summary>
    public static string Format(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private MotionSegment? FinishBlock(Func<double, bool> hasM)
    {
        if (hasM(2) || hasM(30))
        {
            var s = State;
            s.Motion = MotionMode.Linear;
            s.Plane = 17;
            s.Incremental = false;
            s.InverseTime = false;
            if (s.Wcs != 54)
            {
                s.Wcs = 54;
                ForgetWorkAxes();
            }

            s.Spindle = 5;
            s.Mist = false;
            s.Flood = false;
        }

        return null;
    }

    private MotionSegment Move(GcodeBlock block, int line, List<(int Axis, double Value)> words, bool machine)
    {
        var s = State;
        var start = (double[])s.Position.Clone();
        var target = (double[])s.Position.Clone();
        var machineAxes = (bool[])s.MachineAxes.Clone();
        foreach (var (axis, value) in words)
        {
            if (machine)
            {
                target[axis] = value;
                machineAxes[axis] = true;
            }
            else if (s.Incremental)
            {
                target[axis] = s.Position[axis] + value;
            }
            else
            {
                target[axis] = value;
                machineAxes[axis] = false;
            }
        }

        double[]? center = null;
        double sweep = 0;
        var mode = s.Motion;
        if (mode is MotionMode.ArcClockwise or MotionMode.ArcCounterClockwise && !machine)
        {
            (center, sweep) = ArcCenter(block, start, target, mode == MotionMode.ArcClockwise);
        }

        if (mode == MotionMode.Probe)
        {
            foreach (var (axis, _) in words)
            {
                target[axis] = double.NaN;
            }
        }

        target.CopyTo(s.Position, 0);
        machineAxes.CopyTo(s.MachineAxes, 0);
        return new MotionSegment(line, machine && mode != MotionMode.Probe ? (mode == MotionMode.Rapid ? MotionMode.Rapid : MotionMode.Linear) : mode,
            start, target, machineAxes, s.Plane, center, sweep, rotaryRadiusMm);
    }

    private (double[]? Center, double Sweep) ArcCenter(GcodeBlock block, double[] start, double[] end, bool clockwise)
    {
        var s = State;
        var (a, b, _) = MotionSegment.PlaneAxes(s.Plane);
        if (double.IsNaN(start[a]) || double.IsNaN(start[b]) || double.IsNaN(end[a]) || double.IsNaN(end[b]))
        {
            return (null, 0);
        }

        double ca, cb;
        if (block.Get('R') is { } rWord)
        {
            var r = Scale(rWord, 0);
            var x = end[a] - start[a];
            var y = end[b] - start[b];
            var d = Math.Sqrt((4 * r * r) - (x * x) - (y * y));
            var h = double.IsNaN(d) ? 0 : -d / Math.Sqrt((x * x) + (y * y));
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
            var offsets = new[] { block.Get('I'), block.Get('J'), block.Get('K') };
            var oa = offsets[a] is { } va ? Scale(va, a) : 0;
            var ob = offsets[b] is { } vb ? Scale(vb, b) : 0;
            (ca, cb) = s.ArcAbsolute ? (oa, ob) : (start[a] + oa, start[b] + ob);
        }

        var a0 = Math.Atan2(start[b] - cb, start[a] - ca);
        var a1 = Math.Atan2(end[b] - cb, end[a] - ca);
        var sweep = a1 - a0;
        if (clockwise)
        {
            if (sweep >= -1e-9)
            {
                sweep -= 2 * Math.PI;
            }
        }
        else if (sweep <= 1e-9)
        {
            sweep += 2 * Math.PI;
        }

        return ([ca, cb], sweep);
    }

    private List<(int Axis, double Value)> AxisWords(GcodeBlock block)
    {
        var result = new List<(int, double)>();
        foreach (var word in block.Words)
        {
            var axis = ModalState.AxisLetters.IndexOf(word.Letter, StringComparison.Ordinal);
            if (axis >= 0)
            {
                result.Add((axis, Scale(word.Value, axis)));
            }
        }

        return result;
    }

    private double Scale(double value, int axis) => State.Inches && axis < 3 ? value * MmPerInch : value;

    private void ForgetWorkAxes(int? onlyAxis = null)
    {
        for (var i = 0; i < ModalState.AxisCount; i++)
        {
            if ((onlyAxis is null || onlyAxis == i) && !State.MachineAxes[i])
            {
                State.Position[i] = double.NaN;
            }
        }
    }

    private void ApplyDirective(HostDirective directive)
    {
        var heaters = State.Heaters;
        if (directive.Verb == "HEATERS" && directive.Arguments is ["OFF", ..])
        {
            heaters.Clear();
        }
        else if (directive.Verb == "HEAT" && directive.Arguments.Count >= 2
            && double.TryParse(directive.Arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            if (temperature > 0)
            {
                heaters[directive.Arguments[0]] = temperature;
            }
            else
            {
                heaters.Remove(directive.Arguments[0]);
            }
        }
        else if (directive.IsProbeBegin)
        {
            InProbeBlock = true;
        }
        else if (directive.IsProbeEnd)
        {
            InProbeBlock = false;
        }
    }
}
