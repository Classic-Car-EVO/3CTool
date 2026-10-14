using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Simulation;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Post;

/// <summary>
/// Milling post for FluidNC. Each file holds one tool in one setup:
/// <list type="number">
/// <item>header comments, modal preamble and a retract to the safe machine Z;</item>
/// <item>a tool-change prompt (M0);</item>
/// <item>work Z from the touch plate (every file of a touch-plate setup, the first file of a tool-setter setup); the
/// first file of a tool-setter setup also stores the setter reading of the reference tool in G59 Z;</item>
/// <item>tool-setter setups: tool length offset = setter reading − stored reference (FluidNC expressions);</item>
/// <item>spindle start (M3 S) plus an operator confirmation (M0), coolant and dust extraction;</item>
/// <item>the operations (arc fitted, inverse time for rotary feed moves) and the footer.</item>
/// </list>
/// Probe sequences are wrapped in <c>(MC:PROBE BEGIN)</c> / <c>(MC:PROBE END)</c> markers for job recovery.
/// </summary>
public sealed class FluidNcMillingPost : IPostProcessor<MillingPostRequest>
{
    /// <summary>Longest code line accepted (FluidNC's RX buffer is 128 bytes including the newline).</summary>
    public const int MaxLineLength = 120;

    /// <summary>Work offset whose Z stores the setter reading of the reference tool.</summary>
    public const WorkOffset ReferenceOffset = WorkOffset.G59;

    private const double MinimumMoveLength = 1e-3;

    public PostOutput Post(MillingPostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();
        Validate(request, warnings);
        var writer = new Writer();
        var machine = request.Machine;
        var setup = request.Setup;
        var probe = machine.Probe;
        var safeZ = machine.SafeZMachine;
        var setter = setup.ProbeMethod == ProbeMethod.ToolSetter;
        var plate = !setter || request.FirstInSetup;
        var rotary = request.Operations[0].Toolpath.Frame;
        var seconds = EstimatedSeconds(request);

        // Header.
        var tool = request.Tool;
        writer.Comment("3C Tool milling program");
        writer.Comment($"Project: {request.Project.Name}");
        writer.Comment($"Setup {setup.Order}: {setup.Name}, {setup.WorkOffset}, {SetupText(setup)}");
        if (request.Piece is { } piece)
        {
            writer.Comment($"Piece {piece.Number} of {request.PieceCount}: {PieceText(piece)}");
        }

        writer.Comment($"Tool: {tool.Name}, {tool.Type}, D{Writer.Format(tool.Diameter)} mm, {tool.FluteCount} flutes, stickout {Writer.Format(tool.Stickout)} mm");
        writer.Comment($"Collet: {request.Collet?.Name ?? "unspecified"}");
        writer.Comment($"Material: {request.Material?.Name ?? "unspecified"}");
        writer.Comment($"Spindle: {Writer.Format(request.Rpm)} RPM");
        writer.Comment($"Probe: {(setter ? "tool setter" : "touch plate")}{(request.FirstInSetup ? ", first file of the setup" : string.Empty)}");
        writer.Comment($"Operations: {string.Join(", ", request.Operations.Select(o => o.Operation.Name))}");
        writer.Comment($"Estimated time: {TimeSpan.FromSeconds(Math.Round(seconds)):c}");
        writer.Comment($"Generator: 3C Tool {request.GeneratorVersion}");
        foreach (var (key, value) in request.InputHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.Comment($"Input {key}: {value}");
        }

        foreach (var warning in warnings)
        {
            writer.Comment($"WARNING: {warning}");
        }

        var wcs = $"G{54 + (int)setup.WorkOffset}";
        writer.Line($"G21 G90 G94 G17 G91.1 {wcs}");
        writer.MachineZ(safeZ);
        writer.Line("M5 M9");

        // Piece change.
        if (request.Piece is { } mount && request.FirstInSetup)
        {
            writer.Comment($"Mount piece {mount.Number} of {request.PieceCount} for setup {setup.Order}: {PieceText(mount)}");
            writer.Line("M0");
        }

        // Tool change.
        var dial = machine.Spindle.ManualDial ? $", set the spindle dial to {Writer.Format(request.Rpm)} RPM" : string.Empty;
        var collet = request.Collet is null ? string.Empty : $" in collet {request.Collet.Name}";
        writer.Comment($"Insert tool {tool.Name}{collet}, stickout {Writer.Format(tool.Stickout)} mm{dial}");
        writer.Line("M0");

        // Work Z from the touch plate.
        if (plate)
        {
            var spot = request.TouchPlate!.Value;
            writer.Comment($"{HostMarker} BEGIN TOUCHPLATE");
            writer.Line("G49");
            writer.MachineZ(safeZ);
            if (rotary.IsRotary)
            {
                writer.Rapid(a: rotary.BaseAngle);
            }

            writer.Rapid(x: spot.X, y: spot.Y);
            writer.Comment($"Place the touch plate on the stock under the tool, at most {Writer.Format(PlateSeek(request))} mm below it, and attach the probe clip");
            writer.Line("M0");
            ProbeDown(writer, PlateSeek(request), probe);
            writer.Line($"G10 L20 P{1 + (int)setup.WorkOffset} Z{Writer.Format(spot.SurfaceZ + probe.TouchPlateThickness)}");
            if (setter)
            {
                ProbeSetter(writer, request);
                writer.Line($"G10 L2 P{1 + (int)ReferenceOffset} Z[#{ProbeZParameter}]");
            }

            writer.MachineZ(safeZ);
            writer.Comment($"{HostMarker} END");
            writer.Comment("Remove the touch plate and the probe clip");
            writer.Line("M0");
        }

        // Tool length offset relative to the reference tool.
        if (setter)
        {
            writer.Comment($"{HostMarker} BEGIN");
            ProbeSetter(writer, request);
            writer.Line($"G43.1 Z[#{ProbeZParameter}-#{ReferenceZParameter}]");
            writer.MachineZ(safeZ);
            writer.Comment($"{HostMarker} END");
        }

        // Spindle, coolant, dust extraction.
        writer.Line($"M3 S{Writer.Format(request.Rpm)}");
        writer.Comment($"Start the spindle: confirm it runs at {Writer.Format(request.Rpm)} RPM");
        writer.Line("M0");
        switch (machine.Coolant.Mode)
        {
            case CoolantMode.Mist:
                writer.Line("M7");
                break;
            case CoolantMode.Flood:
                writer.Line("M8");
                break;
        }

        if (!string.IsNullOrWhiteSpace(machine.Coolant.DustExtractionOnCode))
        {
            writer.Line(machine.Coolant.DustExtractionOnCode.Trim());
        }

        var tolerance = Math.Min(machine.ArcFittingTolerance, request.Project.Tolerances.ArcFittingTolerance);
        foreach (var operation in request.Operations)
        {
            writer.Comment($"Operation: {operation.Operation.Name}, {operation.Operation.Strategy}");
            writer.MachineZ(safeZ);
            WriteToolpath(writer, operation, machine.ArcFitting && tolerance > 0 ? tolerance : null);
        }

        // Footer.
        writer.InverseTime(false);
        writer.Plane(17);
        writer.MachineZ(safeZ);
        writer.Line("M9");
        if (!string.IsNullOrWhiteSpace(machine.Coolant.DustExtractionOffCode))
        {
            writer.Line(machine.Coolant.DustExtractionOffCode.Trim());
        }

        writer.Line("M5");
        writer.Line("M30");

        var tooLong = writer.Lines.FindIndex(l => !l.StartsWith('(') && l.Length > MaxLineLength);
        if (tooLong >= 0)
        {
            throw new PostException($"Line {tooLong + 1} is longer than {MaxLineLength} characters: {writer.Lines[tooLong]}");
        }

        var text = string.Join('\n', writer.Lines) + "\n";
        return new PostOutput
        {
            FileName = FileName(request),
            Lines = writer.Lines,
            Text = text,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            EstimatedSeconds = seconds,
            Warnings = warnings,
            SetupId = setup.Id,
            Piece = request.Piece?.Number ?? 0,
            ToolId = tool.Id,
            Rpm = request.Rpm,
        };
    }

    /// <summary>
    /// Project-relative output path: <c>gcode/{setup order}_{setup}_T{file index}_{tool}.nc</c>, prefixed with
    /// <c>P{piece}_</c> when the stock is split into pieces so each piece's files sort together.
    /// </summary>
    public static string FileName(MillingPostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var piece = request.Piece is { Number: > 0 } p ? string.Create(CultureInfo.InvariantCulture, $"P{p.Number:00}_") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $"gcode/{piece}{request.Setup.Order:00}_{SafeName(request.Setup.Name, "setup")}_T{request.FileIndex:00}_{SafeName(request.Tool.Name, "tool")}.nc");
    }

    private static string PieceText(StockPiece piece) =>
        $"{piece.Position}, {Writer.Format(piece.SizeX)} x {Writer.Format(piece.SizeY)} x {Writer.Format(piece.SizeZ)} mm at X{Writer.Format(piece.MinX)} Y{Writer.Format(piece.MinY)} Z{Writer.Format(piece.MinZ)}";

    private const string HostMarker = "MC:PROBE";

    /// <summary>Probe result Z (machine coordinates).</summary>
    private const int ProbeZParameter = 5063;

    /// <summary>Z of <see cref="ReferenceOffset"/>.</summary>
    private const int ReferenceZParameter = 5221 + (20 * (int)ReferenceOffset) + 2;

    private static string SetupText(Setup setup) => setup.Orientation switch
    {
        SetupOrientation.Indexed => $"indexed A{Writer.Format(setup.IndexAngle)}",
        SetupOrientation.SimultaneousRotary => "simultaneous rotary",
        _ => "3-axis",
    };

    private static string SafeName(string name, string fallback)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Trim())
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '-' or '.';
            if (ok)
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var safe = builder.ToString().Trim('_', '.');
        return safe.Length == 0 ? fallback : safe;
    }

    private static void ProbeSetter(Writer writer, MillingPostRequest request)
    {
        var probe = request.Machine.Probe;
        writer.MachineZ(request.Machine.SafeZMachine);
        writer.Line($"G53 G0 X{Writer.Format(probe.ToolSetterX)} Y{Writer.Format(probe.ToolSetterY)}");
        writer.MachineZ(probe.ToolSetterStartZ);
        writer.Forget();
        ProbeDown(writer, SetterSeek(request), probe);
    }

    /// <summary>Seek down by <paramref name="distance"/>, back off, latch slowly and return to absolute mode.</summary>
    private static void ProbeDown(Writer writer, double distance, ProbeConfig probe)
    {
        writer.Line($"G91 G38.2 Z-{Writer.Format(distance)} F{Writer.Format(probe.SeekFeed)}");
        writer.Line($"G0 Z{Writer.Format(probe.Retract)}");
        writer.Line($"G38.2 Z-{Writer.Format(2 * probe.Retract)} F{Writer.Format(probe.LatchFeed)}");
        writer.Line("G90");
        writer.Forget();
    }

    private static (double Low, double High)? Range(AxisConfig? config) =>
        config is not { MaxTravel: > 0 }
            ? null
            : config.HomingPositiveDirection
                ? (config.HomingMachinePosition - config.MaxTravel, config.HomingMachinePosition)
                : (config.HomingMachinePosition, config.HomingMachinePosition + config.MaxTravel);

    /// <summary>Probe seek distance from <paramref name="startZ"/>, clamped to the Z travel below it.</summary>
    private static double Seek(MillingPostRequest request, double startZ)
    {
        var distance = request.Machine.Probe.MaxDistance;
        return Range(request.Machine.GetAxis(AxisName.Z)) is { } z ? Math.Min(distance, startZ - z.Low) : distance;
    }

    private static double PlateSeek(MillingPostRequest request) => Seek(request, request.Machine.SafeZMachine);

    private static double SetterSeek(MillingPostRequest request) => Seek(request, request.Machine.Probe.ToolSetterStartZ);

    private static void WriteToolpath(Writer writer, PostOperation operation, double? tolerance)
    {
        var toolpath = operation.Toolpath;
        var moves = toolpath.Moves;
        if (moves.Count == 0)
        {
            return;
        }

        var factors = operation.Summary?.FeedFactors is { } f && f.Length == moves.Count ? f : null;
        var first = moves[0];
        writer.Rapid(x: first.X, y: first.Y, a: toolpath.Frame.IsRotary ? first.A : null);
        writer.Rapid(z: first.Z);

        var run = new List<P3>();
        string? runFeed = null;
        for (var i = 1; i < moves.Count; i++)
        {
            var (previous, move) = (moves[i - 1], moves[i]);
            if (move.Kind == MoveKind.Rapid)
            {
                Flush();
                writer.Rapid(move.X, move.Y, move.Z, toolpath.Frame.IsRotary ? move.A : null);
                continue;
            }

            var feed = (move.Kind == MoveKind.Plunge ? operation.Conditions.PlungeRate : operation.Conditions.FeedRate) *
                (factors is null ? 1 : factors[i]);
            if (toolpath.UsesRotary && move.A != previous.A)
            {
                Flush();
                writer.InverseTime(true);
                writer.Feed(move.X, move.Y, move.Z, move.A, feed / Math.Max(Toolpath.MoveLength(previous, move), MinimumMoveLength));
                continue;
            }

            var text = Writer.Format(feed);
            if (runFeed != text)
            {
                Flush();
                run.Add(previous.Position);
                runFeed = text;
            }

            run.Add(move.Position);
        }

        Flush();

        void Flush()
        {
            if (run.Count > 1)
            {
                writer.InverseTime(false);
                var feed = double.Parse(runFeed!, CultureInfo.InvariantCulture);
                var index = 0;
                while (index < run.Count - 1)
                {
                    if (tolerance is { } t && ArcFitter.Fit(run, index, t) is { } arc)
                    {
                        writer.Arc(run[index], run[arc.End], arc, feed);
                        index = arc.End;
                    }
                    else
                    {
                        index++;
                        writer.Feed(run[index].X, run[index].Y, run[index].Z, null, feed);
                    }
                }
            }

            run.Clear();
            runFeed = null;
        }
    }

    /// <summary>Simulated duration when every operation has a summary; otherwise a feed / rapid-rate estimate.</summary>
    private static double EstimatedSeconds(MillingPostRequest request)
    {
        double total = 0;
        var rapid = new[] { AxisName.X, AxisName.Y, AxisName.Z }
            .Select(a => request.Machine.GetAxis(a)?.MaxRatePerMinute ?? 0)
            .Where(r => r > 0)
            .DefaultIfEmpty(5000)
            .Min();
        foreach (var operation in request.Operations)
        {
            if (operation.Summary is { } summary)
            {
                total += summary.Duration;
                continue;
            }

            var moves = operation.Toolpath.Moves;
            for (var i = 1; i < moves.Count; i++)
            {
                var length = Toolpath.MoveLength(moves[i - 1], moves[i]);
                var rate = moves[i].Kind switch
                {
                    MoveKind.Rapid => rapid,
                    MoveKind.Plunge => operation.Conditions.PlungeRate,
                    _ => operation.Conditions.FeedRate,
                };
                total += 60 * length / rate;
            }
        }

        return total;
    }

    private static void Validate(MillingPostRequest request, List<string> warnings)
    {
        var (machine, setup, tool) = (request.Machine, request.Setup, request.Tool);
        if (request.Operations.Count == 0)
        {
            throw new PostException("The file has no operations.");
        }

        var spindle = machine.Spindle;
        if (!(request.Rpm >= spindle.MinRpm && request.Rpm <= spindle.MaxRpm))
        {
            throw new PostException($"{Writer.Format(request.Rpm)} RPM is outside the spindle range {Writer.Format(spindle.MinRpm)}..{Writer.Format(spindle.MaxRpm)}.");
        }

        var frame = request.Operations[0].Toolpath.Frame;
        foreach (var (operation, toolpath, conditions, _) in request.Operations)
        {
            var name = operation.Name;
            if (operation.ToolId != tool.Id || toolpath.ToolId != tool.Id)
            {
                throw new PostException($"'{name}' uses a different tool than '{tool.Name}'.");
            }

            if (operation.SetupId != setup.Id)
            {
                throw new PostException($"'{name}' belongs to another setup than '{setup.Name}'.");
            }

            if (toolpath.Moves.Count == 0)
            {
                throw new PostException($"'{name}' has an empty toolpath.");
            }

            if (!(conditions.FeedRate > 0) || !(conditions.PlungeRate > 0))
            {
                throw new PostException($"'{name}' needs a feed rate and a plunge rate above zero.");
            }

            if (toolpath.Frame != frame || toolpath.Frame.Orientation != setup.Orientation)
            {
                throw new PostException($"'{name}' was generated for a different setup frame; regenerate it.");
            }

            warnings.AddRange(toolpath.Warnings.Select(w => $"{name}: {w}"));
        }

        if (frame.IsRotary && machine.GetAxis(AxisName.A) is null)
        {
            throw new PostException($"Setup '{setup.Name}' is rotary but the machine has no A axis.");
        }

        var setter = setup.ProbeMethod == ProbeMethod.ToolSetter;
        if (setter && setup.WorkOffset == ReferenceOffset)
        {
            throw new PostException($"Setup '{setup.Name}' uses {ReferenceOffset}, which stores the tool-setter reference; choose another work offset.");
        }

        var plate = !setter || request.FirstInSetup;
        if (plate && request.TouchPlate is null)
        {
            throw new PostException("The touch plate position is required to set the work Z.");
        }

        CheckVerification(request, warnings);
        CheckTravel(request, plate, setter);
    }

    private static void CheckVerification(MillingPostRequest request, List<string> warnings)
    {
        var steps = request.Operations.Select(o => o.Summary?.StepId).OfType<Guid>().ToHashSet();
        string? problem = null;
        if (request.Verification is not { } report)
        {
            problem = "the toolpaths have not been verified by simulation";
        }
        else
        {
            var errors = report.Issues
                .Where(i => i.Severity == IssueSeverity.Error && (i.StepId is null || steps.Contains(i.StepId.Value)))
                .ToList();
            if (errors.Count > 0)
            {
                problem = $"verification found {errors.Count} error(s), e.g. {errors[0].Message}";
            }
        }

        if (problem is null)
        {
            return;
        }

        if (!request.OverrideVerification)
        {
            throw new PostException($"Output blocked: {problem}.");
        }

        warnings.Add($"Verification overridden by the user: {problem}.");
    }

    private static void CheckTravel(MillingPostRequest request, bool plate, bool setter)
    {
        var machine = request.Machine;
        var probe = machine.Probe;
        AxisName[] axes = [AxisName.X, AxisName.Y, AxisName.Z];
        var ranges = axes.Select(a => Range(machine.GetAxis(a))).ToArray();

        // Machine-coordinate targets.
        Check(2, machine.SafeZMachine, "the safe Z");
        if (setter)
        {
            Check(0, probe.ToolSetterX, "the tool setter X");
            Check(1, probe.ToolSetterY, "the tool setter Y");
            Check(2, probe.ToolSetterStartZ, "the tool setter start Z");
            if (!(SetterSeek(request) > 2 * probe.Retract))
            {
                throw new PostException("There is no Z travel left below the tool setter start Z to probe.");
            }
        }

        if (plate && !(PlateSeek(request) > 2 * probe.Retract))
        {
            throw new PostException("There is no Z travel left below the safe Z to probe the touch plate.");
        }

        // WCS positions: travel span, and absolute limits when the work offset is known.
        var points = request.Operations.SelectMany(o => o.Toolpath.Moves.Select(m => m.Position)).ToList();
        if (plate && request.TouchPlate is { } spot)
        {
            points.Add(new P3(spot.X, spot.Y, points[0].Z));
        }

        for (var axis = 0; axis < 3; axis++)
        {
            if (ranges[axis] is not { } range)
            {
                continue;
            }

            var values = points.Select(p => ArcFitter.Coordinate(p, axis)).ToArray();
            var (min, max) = (values.Min(), values.Max());
            if (max - min > range.High - range.Low + 1e-6)
            {
                throw new PostException($"The toolpaths need {Writer.Format(max - min)} mm of {axes[axis]} travel; the machine has {Writer.Format(range.High - range.Low)} mm.");
            }

            if (request.WorkOffset is { } offset && machine.GetAxis(axes[axis]) is { HomingCycle: > 0 })
            {
                var shift = axis switch { 0 => offset.X, 1 => offset.Y, _ => offset.Z };
                if (min + shift < range.Low - 1e-6 || max + shift > range.High + 1e-6)
                {
                    throw new PostException($"The toolpaths reach machine {axes[axis]} {Writer.Format(min + shift < range.Low ? min + shift : max + shift)}, outside {Writer.Format(range.Low)}..{Writer.Format(range.High)}.");
                }
            }
        }

        if (machine.GetAxis(AxisName.A) is { SoftLimits: true, MaxTravel: > 0 } a)
        {
            var angles = request.Operations.SelectMany(o => o.Toolpath.Moves.Select(m => m.A)).ToList();
            if (angles.Max() - angles.Min() > a.MaxTravel + 1e-6)
            {
                throw new PostException($"The toolpaths turn A through {Writer.Format(angles.Max() - angles.Min())}°; the axis allows {Writer.Format(a.MaxTravel)}°.");
            }
        }

        void Check(int axis, double value, string what)
        {
            if (ranges[axis] is { } range && (value < range.Low - 1e-6 || value > range.High + 1e-6))
            {
                throw new PostException($"{char.ToUpperInvariant(what[0])}{what[1..]} ({axes[axis]} {Writer.Format(value)}) is outside the machine travel {Writer.Format(range.Low)}..{Writer.Format(range.High)}.");
            }
        }
    }

    /// <summary>Line builder with modal suppression of repeated motion words, axis words and feeds.</summary>
    private sealed class Writer
    {
        private readonly string?[] axes = new string?[4];
        private int motion = -1;
        private int plane = 17;
        private string? feed;
        private bool inverse;

        public List<string> Lines { get; } = [];

        public static string Format(double value)
        {
            var text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        public void Line(string text) => Lines.Add(text);

        public void Comment(string text)
        {
            var builder = new StringBuilder("(");
            foreach (var c in text)
            {
                builder.Append(c switch
                {
                    '(' => '[',
                    ')' => ']',
                    _ when char.IsControl(c) => ' ',
                    _ => c,
                });
            }

            Lines.Add(builder.Append(')').ToString());
        }

        /// <summary>The position or motion mode is no longer known (machine-coordinate, incremental or probe moves).</summary>
        public void Forget()
        {
            Array.Clear(axes);
            motion = -1;
        }

        public void MachineZ(double z)
        {
            Lines.Add($"G53 G0 Z{Format(z)}");
            axes[2] = null;
            motion = -1;
        }

        public void InverseTime(bool on)
        {
            if (on != inverse)
            {
                Lines.Add(on ? "G93" : "G94");
                inverse = on;
                feed = null;
            }
        }

        public void Plane(int code)
        {
            if (code != plane)
            {
                Lines.Add($"G{code}");
                plane = code;
            }
        }

        public void Rapid(double? x = null, double? y = null, double? z = null, double? a = null) =>
            Move(0, [x, y, z, a], null, null);

        public void Feed(double x, double y, double z, double? a, double rate) => Move(1, [x, y, z, a], rate, null);

        public void Arc(P3 start, P3 end, FittedArc arc, double rate)
        {
            var (first, second, _) = ArcFitter.PlaneAxes(arc.Plane);
            var offsets = new double?[3];

            // Offsets from the start as the controller sees it (rounded), so both radii match after rounding.
            offsets[first] = arc.CenterFirst - Round(ArcFitter.Coordinate(start, first));
            offsets[second] = arc.CenterSecond - Round(ArcFitter.Coordinate(start, second));
            var ijk = new StringBuilder();
            for (var i = 0; i < 3; i++)
            {
                if (offsets[i] is { } o)
                {
                    ijk.Append(' ').Append("IJK"[i]).Append(Format(o));
                }
            }

            Move(arc.Clockwise ? 2 : 3, [end.X, end.Y, end.Z, null], rate, ijk.ToString(), arc.Plane);
        }

        private static double Round(double value) => Math.Round(value, 4);

        private void Move(int code, double?[] targets, double? rate, string? arc, int? arcPlane = null)
        {
            var words = new StringBuilder();
            for (var i = 0; i < targets.Length; i++)
            {
                if (targets[i] is { } value)
                {
                    var text = Format(value);
                    if (text != axes[i])
                    {
                        words.Append(' ').Append("XYZA"[i]).Append(text);
                        axes[i] = text;
                    }
                }
            }

            if (words.Length == 0)
            {
                return;
            }

            var line = new StringBuilder();
            if (arcPlane is { } p && p != plane)
            {
                line.Append('G').Append(p).Append(' ');
                plane = p;
            }

            if (code != motion || arc is not null)
            {
                line.Append('G').Append(code);
                motion = code;
            }

            line.Append(words);
            if (arc is not null)
            {
                line.Append(arc);
            }

            if (rate is { } r)
            {
                var text = Format(r);
                if (inverse || text != feed)
                {
                    line.Append(" F").Append(text);
                    feed = text;
                }
            }

            Lines.Add(line.ToString().TrimStart());
        }
    }
}
