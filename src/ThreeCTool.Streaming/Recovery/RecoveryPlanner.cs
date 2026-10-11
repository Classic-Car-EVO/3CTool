using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming.Recovery;

public enum JobProcess
{
    Milling,
    Fdm,
}

public enum RecoveryAction
{
    Resume,

    /// <summary>Nothing had executed: start the job from the beginning.</summary>
    Restart,

    /// <summary>FDM after an e-stop or position loss: abort the print, heaters off.</summary>
    Abort,

    /// <summary>No safe resume point (e.g. position unknown); the operator must restart manually.</summary>
    Unrecoverable,
}

public sealed class RecoveryOptions
{
    public JobProcess Process { get; set; } = JobProcess.Milling;

    /// <summary>Distance to move back along the path before the interrupted line (default: milling 1 mm, FDM 0).</summary>
    public double? SafetyMarginMm { get; set; }

    public double SafeZMachine { get; set; } = -5;

    /// <summary>Rapid to this height above the resume point before plunging at reduced feed.</summary>
    public double ApproachClearanceMm { get; set; } = 2;

    public double PlungeFeedMmPerMin { get; set; } = 100;

    /// <summary>Lines before the executed estimate searched when cross-checking against the last reported position.</summary>
    public int CrossCheckWindowLines { get; set; } = 500;

    /// <summary>Reported position must be this close to a path segment to count as a match.</summary>
    public double PositionToleranceMm { get; set; } = 0.5;

    public double OffsetToleranceMm { get; set; } = 0.002;

    public double RotaryRadiusMm { get; set; } = 20;

    /// <summary>FDM: machine X/Y where the nozzle is primed before re-entry (null = prime in place above the part).</summary>
    public (double X, double Y)? FdmParkMachine { get; set; }

    public double FdmPrimeMm { get; set; } = 5;

    public double FdmPrimeFeedMmPerMin { get; set; } = 300;

    internal double Margin => SafetyMarginMm ?? (Process == JobProcess.Milling ? 1.0 : 0.0);
}

/// <summary>Probe routine in the file (between <c>(MC:PROBE BEGIN)</c> and <c>(MC:PROBE END)</c>) replayed to restore tool length.</summary>
public sealed record ProbeReplay(int BeginLine, int EndLine, ModalState StateBefore);

public sealed record RecoveryPlan(
    RecoveryAction Action,
    int ResumeLine,
    string? FirstLinePrefix,
    ModalState State,
    int InterruptedLine,
    ProbeReplay? Probe,
    IReadOnlyList<string> Notes)
{
    /// <summary>Program position (mm) where the resume line starts; see <see cref="ModalState.MachineAxes"/>.</summary>
    public IReadOnlyList<double> ResumePosition => State.Position;
}

/// <summary>Determines where and how a job resumes after an interruption (section 11.3 steps 2 and 6).</summary>
public static class RecoveryPlanner
{
    public static RecoveryPlan Plan(JobFile file, JobProgress progress, RecoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(options);
        if (progress.FileSha256 != file.Sha256)
        {
            throw new InvalidOperationException("Persisted progress belongs to a different file.");
        }

        var notes = new List<string>();
        if (options.Process == JobProcess.Fdm && progress.Interruption is InterruptionKind.Alarm or InterruptionKind.PowerLoss)
        {
            notes.Add($"FDM job interrupted by {progress.Interruption}{(progress.InterruptionCode is { } c ? $" {c}" : string.Empty)}: print aborted.");
            return new RecoveryPlan(RecoveryAction.Abort, 0, null, new ModalState(), progress.Executed, null, notes);
        }

        if (progress.Executed < 1)
        {
            notes.Add("Nothing had executed; the job restarts from the beginning.");
            return new RecoveryPlan(RecoveryAction.Restart, 1, null, new ModalState(), 0, null, notes);
        }

        // Pass 1: path segments near the interruption, barriers and probe blocks.
        var lastLine = Math.Min(Math.Max(Math.Max(progress.LastSent, progress.Executed), progress.LastAcked), file.LineCount);
        var windowStart = Math.Max(1, progress.Executed - options.CrossCheckWindowLines);
        var interpreter = new GcodeInterpreter(options.RotaryRadiusMm);
        var segments = new List<MotionSegment>();
        var barriers = new SortedSet<int>();
        var probeBlocks = new List<(int Begin, int End)>();
        var probeBegin = 0;
        foreach (var (line, text) in file.ReadLines())
        {
            if (line > lastLine)
            {
                break;
            }

            var block = GcodeBlock.Parse(text);
            if (GcodeInterpreter.IsBarrier(block))
            {
                barriers.Add(line);
            }

            if (block.Directive is { IsProbeBegin: true })
            {
                probeBegin = line;
            }
            else if (block.Directive is { IsProbeEnd: true } && probeBegin > 0)
            {
                probeBlocks.Add((probeBegin, line));
                probeBegin = 0;
            }

            if (interpreter.Apply(block, line) is { } segment && line >= windowStart)
            {
                segments.Add(segment);
            }
        }

        var interrupted = CrossCheck(progress, segments, options, notes);
        var resume = WalkBack(interrupted, segments, barriers, options.Margin, notes);

        // Resuming inside a probe routine re-runs the whole routine.
        var inside = probeBlocks.FirstOrDefault(b => b.Begin < resume && resume <= b.End);
        if (inside.Begin == 0 && probeBegin > 0 && probeBegin < resume)
        {
            inside = (probeBegin, int.MaxValue);
        }

        if (inside.Begin > 0)
        {
            resume = inside.Begin;
            notes.Add($"Interrupted inside the probe routine; resuming at its start (line {resume}).");
        }

        var replayBlock = probeBlocks.LastOrDefault(b => b.End < resume);

        // Pass 2: modal state before the resume line; advance (never past the interrupted line) until XYZ is known.
        interpreter.Reset();
        ModalState? state = null;
        ModalState? probeState = null;
        var chosen = 0;
        foreach (var (line, text) in file.ReadLines())
        {
            if (line == replayBlock.Begin)
            {
                probeState = interpreter.State.Clone();
            }

            if (line > Math.Max(interrupted, resume))
            {
                break;
            }

            if (line >= resume && (IsKnown(interpreter.State) || line == inside.Begin))
            {
                state = interpreter.State.Clone();
                chosen = line;
                break;
            }

            interpreter.Apply(GcodeBlock.Parse(text), line);
        }

        if (state is null)
        {
            notes.Add($"Tool position before line {resume} is unknown (after probing, homing or an offset change) and no later line up to {interrupted} has a known start position.");
            return new RecoveryPlan(RecoveryAction.Unrecoverable, resume, null, interpreter.State.Clone(), interrupted, null, notes);
        }

        if (chosen != resume)
        {
            notes.Add($"Position unknown before line {resume}; resuming at line {chosen} instead.");
        }

        var resumeBlock = GcodeBlock.Parse(file.ReadLine(chosen) ?? string.Empty);
        var prefix = resumeBlock.HasCode && !HasMotionWord(resumeBlock) ? MotionWord(state.Motion) : null;

        var probe = replayBlock.Begin > 0 && probeState is not null && inside.Begin == 0
            ? new ProbeReplay(replayBlock.Begin, replayBlock.End, probeState)
            : null;
        notes.Add($"Interrupted at line {interrupted}; resuming at line {chosen}.");
        return new RecoveryPlan(RecoveryAction.Resume, chosen, prefix, state, interrupted, probe, notes);
    }

    public static string? MotionWord(MotionMode mode) => mode switch
    {
        MotionMode.Rapid => "G0",
        MotionMode.Linear => "G1",
        MotionMode.ArcClockwise => "G2",
        MotionMode.ArcCounterClockwise => "G3",
        _ => null,
    };

    private static bool HasMotionWord(GcodeBlock block) =>
        block.Words.Any(w => w.Letter == 'G' && (w.Value is 0 or 1 or 2 or 3 or 80 || w.Value is >= 38 and < 39));

    private static bool IsKnown(ModalState state) =>
        !double.IsNaN(state.Position[0]) && !double.IsNaN(state.Position[1]) && !double.IsNaN(state.Position[2]);

    /// <summary>
    /// Exact line numbers are trusted. Otherwise the estimate is compared with the path segment closest to the last
    /// reported position and the earlier of the two is used.
    /// </summary>
    private static int CrossCheck(JobProgress progress, List<MotionSegment> segments, RecoveryOptions options, List<string> notes)
    {
        var executed = progress.Executed;
        if (progress.MachinePosition is null || progress.WorkOffset is null
            || !AxisVector.TryParse(progress.MachinePosition, out var machine) || !AxisVector.TryParse(progress.WorkOffset, out var offset))
        {
            notes.Add("No reported position to cross-check the executed line.");
            return executed;
        }

        var work = machine.Subtract(offset).ToArray();
        var machineArray = machine.ToArray();
        MotionSegment? best = null;
        var bestDistance = double.PositiveInfinity;
        var upper = Math.Max(executed, progress.LastAcked);
        foreach (var segment in segments)
        {
            if (segment.Line > upper)
            {
                continue;
            }

            var distance = segment.DistanceTo(work, machineArray);
            if (distance < bestDistance - 1e-9)
            {
                best = segment;
                bestDistance = distance;
            }
        }

        if (best is null || bestDistance > options.PositionToleranceMm)
        {
            notes.Add($"Last reported position does not lie on the path near line {executed}; using the line estimate.");
            return executed;
        }

        if (progress.ExecutedExact)
        {
            return executed;
        }

        if (best.Line < executed)
        {
            notes.Add($"Reported position matches line {best.Line}, before the estimate {executed}.");
            return best.Line;
        }

        return executed;
    }

    private static int WalkBack(int interrupted, List<MotionSegment> segments, SortedSet<int> barriers, double margin, List<string> notes)
    {
        var resume = interrupted;
        var distance = 0.0;
        for (var i = segments.Count - 1; i >= 0 && distance < margin; i--)
        {
            var segment = segments[i];
            if (segment.Line >= interrupted)
            {
                continue;
            }

            if (barriers.GetViewBetween(segment.Line, resume - 1).Count > 0)
            {
                notes.Add("Safety margin shortened: a tool change, probe or offset change lies within it.");
                break;
            }

            resume = segment.Line;
            distance += segment.Length;
        }

        return resume;
    }
}
