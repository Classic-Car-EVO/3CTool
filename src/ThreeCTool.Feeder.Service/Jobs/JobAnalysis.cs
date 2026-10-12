using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Jobs;

namespace ThreeCTool.Feeder.Service.Jobs;

/// <summary>Per-job data derived from the file at upload: time table for progress/ETA, operator prompts and the preview.</summary>
public sealed class JobAnalysis
{
    private const int FormatVersion = 1;
    private const uint Magic = 0x3343_4A41;
    private const double ArcStepRadians = Math.PI / 36;
    private const double RotaryStepDegrees = 2;

    private readonly float[] cumulativeSeconds;

    private JobAnalysis(float[] cumulativeSeconds, IReadOnlyDictionary<int, double> spindlePrompts, IReadOnlyList<int> pauseLines)
    {
        this.cumulativeSeconds = cumulativeSeconds;
        SpindlePrompts = spindlePrompts;
        PauseLines = pauseLines;
    }

    public int LineCount => cumulativeSeconds.Length - 1;

    public double TotalSeconds => cumulativeSeconds[^1];

    /// <summary><c>M0</c>/<c>M1</c> lines reached with the spindle on, with the programmed RPM (the operator confirms the dial).</summary>
    public IReadOnlyDictionary<int, double> SpindlePrompts { get; }

    /// <summary>All <c>M0</c>/<c>M1</c> lines in file order.</summary>
    public IReadOnlyList<int> PauseLines { get; }

    /// <summary>Estimated machine time up to and including <paramref name="line"/> (seconds at 100 % override).</summary>
    public double SecondsThrough(int line) => cumulativeSeconds[Math.Clamp(line, 0, LineCount)];

    /// <summary>Scans the file once: time estimate per line, prompts and the decimated preview.</summary>
    public static (JobAnalysis Analysis, Preview Preview) Analyze(JobFile file, FeederOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        var interpreter = new GcodeInterpreter(options.RotaryRadiusMm);
        var times = new float[file.LineCount + 1];
        var spindlePrompts = new Dictionary<int, double>();
        var pauses = new List<int>();
        var segments = new List<MotionSegment>();
        var feeds = new List<(bool InverseTime, double Feed)>();
        double total = 0;
        foreach (var (line, text) in file.ReadLines())
        {
            if ((line & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var block = GcodeBlock.Parse(text);
            var segment = interpreter.Apply(block, line);
            var state = interpreter.State;
            if (segment is not null)
            {
                var feed = state.Inches && !state.InverseTime ? state.Feed * 25.4 : state.Feed;
                total += SegmentSeconds(segment, state.InverseTime, feed, options.RapidRateMmPerMin);
                segments.Add(segment);
                feeds.Add((state.InverseTime, feed));
            }

            if (block.HasG(4) && block.Get('P') is { } dwell && double.IsFinite(dwell))
            {
                total += Math.Max(0, dwell);
            }

            if (block.HasM(0) || block.HasM(1))
            {
                pauses.Add(line);
                if (state.Spindle is 3 or 4)
                {
                    spindlePrompts[line] = state.SpindleSpeed;
                }
            }

            if (line < times.Length)
            {
                times[line] = (float)total;
            }
        }

        for (var i = 1; i < times.Length; i++)
        {
            times[i] = Math.Max(times[i], times[i - 1]);
        }

        var preview = BuildPreview(segments, options.PreviewMaxVertices);
        return (new JobAnalysis(times, spindlePrompts, pauses), preview);
    }

    public void Save(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(cumulativeSeconds.Length);
        foreach (var t in cumulativeSeconds)
        {
            writer.Write(t);
        }

        writer.Write(PauseLines.Count);
        foreach (var line in PauseLines)
        {
            writer.Write(line);
        }

        writer.Write(SpindlePrompts.Count);
        foreach (var (line, rpm) in SpindlePrompts)
        {
            writer.Write(line);
            writer.Write(rpm);
        }
    }

    public static JobAnalysis Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
        {
            throw new InvalidDataException($"'{path}' is not a job analysis file of this version.");
        }

        var times = new float[reader.ReadInt32()];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = reader.ReadSingle();
        }

        var pauses = new int[reader.ReadInt32()];
        for (var i = 0; i < pauses.Length; i++)
        {
            pauses[i] = reader.ReadInt32();
        }

        var count = reader.ReadInt32();
        var prompts = new Dictionary<int, double>(count);
        for (var i = 0; i < count; i++)
        {
            prompts[reader.ReadInt32()] = reader.ReadDouble();
        }

        return new JobAnalysis(times, prompts, pauses);
    }

    private static double SegmentSeconds(MotionSegment segment, bool inverseTime, double feed, double rapidRate)
    {
        if (segment.Mode == MotionMode.Rapid)
        {
            return segment.Length / rapidRate * 60;
        }

        if (feed <= 0 || !double.IsFinite(feed))
        {
            return 0;
        }

        return inverseTime ? 60 / feed : segment.Length / feed * 60;
    }

    /// <summary>
    /// Polylines in the part frame (A rotation removed). Points in machine coordinates (G53) or with unknown axes break
    /// the strip. When the path has more vertices than <paramref name="maxVertices"/>, points closer than an even
    /// share of the path length to the last kept point are dropped (strip ends are always kept).
    /// </summary>
    internal static Preview BuildPreview(IReadOnlyList<MotionSegment> segments, int maxVertices)
    {
        var points = new List<(float X, float Y, float Z, int Line, bool Rapid, bool Break)>();
        var rotary = false;
        double pathLength = 0;
        foreach (var segment in segments)
        {
            var usable = segment.Mode != MotionMode.Probe && Known(segment.Start) && Known(segment.End);
            if (!usable)
            {
                if (points.Count > 0)
                {
                    points[^1] = points[^1] with { Break = true };
                }

                continue;
            }

            rotary |= !double.IsNaN(segment.End[3]) && Math.Abs(segment.End[3] - Angle(segment.Start)) > 1e-9;
            var steps = 1;
            if (segment.IsArc)
            {
                steps = Math.Max(steps, (int)Math.Ceiling(Math.Abs(segment.Sweep) / ArcStepRadians));
            }

            var deltaA = Angle(segment.End) - Angle(segment.Start);
            steps = Math.Max(steps, (int)Math.Ceiling(Math.Abs(deltaA) / RotaryStepDegrees));
            var rapid = segment.Mode == MotionMode.Rapid;
            if (points.Count == 0 || points[^1].Break || points[^1].Rapid != rapid || !Same(points[^1], PartFrame(segment.Start)))
            {
                if (points.Count > 0)
                {
                    points[^1] = points[^1] with { Break = true };
                }

                var (sx, sy, sz) = PartFrame(segment.Start);
                points.Add((sx, sy, sz, segment.Line, rapid, false));
            }

            for (var s = 1; s <= steps; s++)
            {
                var (x, y, z) = PartFrame(segment.PointAt(s / (double)steps));
                var last = points[^1];
                pathLength += Math.Sqrt(Square(x - last.X) + Square(y - last.Y) + Square(z - last.Z));
                points.Add((x, y, z, segment.Line, rapid, false));
            }
        }

        if (points.Count > 0)
        {
            points[^1] = points[^1] with { Break = true };
        }

        var minSpacing = points.Count > maxVertices && maxVertices > 0 ? pathLength / maxVertices : 0;
        var preview = new Preview { Rotary = rotary, SourceSegments = segments.Count };
        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue];
        float[] max = [float.MinValue, float.MinValue, float.MinValue];
        var stripOpen = false;
        (float X, float Y, float Z) previous = default;
        double travelled = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            min[0] = Math.Min(min[0], p.X);
            min[1] = Math.Min(min[1], p.Y);
            min[2] = Math.Min(min[2], p.Z);
            max[0] = Math.Max(max[0], p.X);
            max[1] = Math.Max(max[1], p.Y);
            max[2] = Math.Max(max[2], p.Z);
            if (!stripOpen)
            {
                preview.StripStarts.Add(preview.Lines.Count);
                preview.StripRapid.Add(p.Rapid);
                stripOpen = true;
                Emit(preview, p.X, p.Y, p.Z, p.Line);
                travelled = 0;
            }
            else
            {
                // Path length since the last kept vertex (not straight-line distance, which would drop whole zigzags).
                travelled += Math.Sqrt(Square(p.X - previous.X) + Square(p.Y - previous.Y) + Square(p.Z - previous.Z));
                if (p.Break || minSpacing <= 0 || travelled >= minSpacing)
                {
                    Emit(preview, p.X, p.Y, p.Z, p.Line);
                    travelled = 0;
                }
            }

            previous = (p.X, p.Y, p.Z);
            if (p.Break)
            {
                stripOpen = false;
            }
        }

        if (preview.Lines.Count > 0)
        {
            preview.BoundsMin.AddRange(min);
            preview.BoundsMax.AddRange(max);
        }

        return preview;
    }

    private static void Emit(Preview preview, float x, float y, float z, int line)
    {
        preview.Positions.Add(x);
        preview.Positions.Add(y);
        preview.Positions.Add(z);
        preview.Lines.Add(line);
    }

    private static bool Known(IReadOnlyList<double> p) => !double.IsNaN(p[0]) && !double.IsNaN(p[1]) && !double.IsNaN(p[2]);

    private static double Angle(IReadOnlyList<double> p) => double.IsNaN(p[3]) ? 0 : p[3];

    private static bool Same((float X, float Y, float Z, int Line, bool Rapid, bool Break) a, (float X, float Y, float Z) b) =>
        Math.Abs(a.X - b.X) < 1e-4 && Math.Abs(a.Y - b.Y) < 1e-4 && Math.Abs(a.Z - b.Z) < 1e-4;

    /// <summary>Tool point relative to the part: the work point rotated back by the A angle about X.</summary>
    private static (float X, float Y, float Z) PartFrame(IReadOnlyList<double> p)
    {
        var a = -Angle(p) * Math.PI / 180;
        var (sin, cos) = Math.SinCos(a);
        return ((float)p[0], (float)((p[1] * cos) - (p[2] * sin)), (float)((p[1] * sin) + (p[2] * cos)));
    }

    private static double Square(double v) => v * v;
}
