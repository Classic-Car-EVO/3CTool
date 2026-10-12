using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

using ThreeCTool.Compute;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Simulation;

public sealed class SimulationResult : IDisposable
{
    internal SimulationResult(VerificationReport report, DexelStock stock)
    {
        Report = report;
        Stock = stock;
    }

    public VerificationReport Report { get; }

    /// <summary>In-process stock after the last step (part frame); owned by the result.</summary>
    public DexelStock Stock { get; }

    public void Dispose() => Stock.Dispose();
}

/// <summary>
/// Runs the whole project sequence on the dexel stock: material removal and deposition, collision checks of tool,
/// holder and heads against in-process stock, part and keep-outs, travel limits, gouges, deviation and cycle time.
/// </summary>
public static class Simulator
{
    public static SimulationResult Run(SimulationJob job, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var steps = job.Steps.Select(s => PreparedStep.Create(s, job.Options.MaxAngleStep)).ToList();
        var stock = StockBuilder.Create(job, steps, out var resolution);
        try
        {
            using var run = new SimulationRun(job, steps, stock, resolution, progress, cancellationToken);
            run.Execute();
            return new SimulationResult(run.Report, stock);
        }
        catch
        {
            stock.Dispose();
            throw;
        }
    }

    private readonly record struct PoseRef(int Segment, float T);

    /// <summary>Colliding pose with the mask over the whole body (cutter primitives first).</summary>
    private readonly record struct Hit(PoseRef Pose, ulong Mask);

    private readonly record struct Cell(int X, int Y, int Z);

    private sealed class SimulationRun : IDisposable
    {
        private readonly SimulationJob _job;
        private readonly SimulationOptions _options;
        private readonly List<PreparedStep> _steps;
        private readonly DexelStock _stock;
        private readonly float _resolution;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _token;
        private readonly GpuMesh _part;
        private readonly MotionPlanner _planner;
        private readonly Toolhead? _spindle;
        private readonly float _cellSize;
        private readonly Dictionary<IssueKind, int> _issueCounts = [];
        private readonly Dictionary<IssueKind, int> _suppressed = [];
        private readonly int _totalSegments;
        private int _doneSegments;

        /// <summary>Removed volume (mm³) per move of the current milling step when it has an engagement limiter.</summary>
        private double[]? _removed;

        public SimulationRun(SimulationJob job, List<PreparedStep> steps, DexelStock stock, float resolution, IProgress<double>? progress, CancellationToken token)
        {
            _job = job;
            _options = job.Options;
            _steps = steps;
            _stock = stock;
            _resolution = resolution;
            _progress = progress;
            _token = token;
            _part = new GpuMesh(job.Compute, job.Part);
            _planner = new MotionPlanner(job.Machine);
            _spindle = job.Machine.Toolheads.Find(t => t.Type == ToolheadType.Spindle);
            _cellSize = MathF.Max(2 * resolution, 0.5f);
            _totalSegments = Math.Max(1, steps.Sum(s => s.Segments.Count));
            Report.Resolution = resolution;
        }

        public VerificationReport Report { get; } = new();

        private float Threshold => (float)Math.Max(_job.Tolerances.GougeThreshold, 1e-4);

        public void Dispose() => _part.Dispose();

        public void Execute()
        {
            HashSet<Cell> initial = [];
            HashSet<Cell> attributed = [];
            if (_options.AttributeGouges)
            {
                foreach (var cluster in Clusters(Deviation()))
                {
                    initial.UnionWith(cluster.Cells);
                }
            }

            foreach (var step in _steps)
            {
                _token.ThrowIfCancellationRequested();
                RunStep(step, initial, attributed);
            }

            var samples = Deviation();
            SummarizeDeviation(samples);
            foreach (var cluster in Clusters(samples))
            {
                if (cluster.Cells.Overlaps(attributed))
                {
                    continue;
                }

                var worst = samples[cluster.Worst];
                var message = cluster.Cells.Overlaps(initial)
                    ? $"The part extends {-worst.Distance:0.###} mm beyond the stock here and is never built up."
                    : $"Material is missing {-worst.Distance:0.###} mm below the part surface ({cluster.Count} samples).";
                Add(new VerificationIssue(IssueKind.Gouge, IssueSeverity.Error, null, -1, -1, worst.Point, null, message));
            }

            var overflow = _stock.OverflowCount;
            if (overflow > 0)
            {
                Add(new VerificationIssue(IssueKind.StockModel, IssueSeverity.Warning, null, -1, -1, null, null,
                    $"{overflow} dexel updates exceeded the segment capacity; some material was kept in the model."));
            }

            _progress?.Report(1);
        }

        private void RunStep(PreparedStep p, HashSet<Cell> initial, HashSet<Cell> attributed)
        {
            _issueCounts.Clear();
            _suppressed.Clear();
            var step = p.Step;
            var notes = new List<string>();
            var body = step switch
            {
                MillingStep m => CollisionBody.ForMilling(p.Cutter, m.Tool, m.Collet, _spindle, _options.ContactTolerance, notes),
                DepositionStep d => CollisionBody.ForHead(d.Head, _options.ContactTolerance, notes),
                _ => throw new InvalidOperationException(),
            };
            foreach (var note in notes)
            {
                Add(new VerificationIssue(IssueKind.MissingData, IssueSeverity.Warning, step.Id, -1, -1, null, null, note));
            }

            _removed = step is MillingStep { Limiter: not null } milling ? new double[milling.Toolpath.Moves.Count] : null;
            var before = _stock.Volume();
            var stockHits = StockPass(p, body);
            var volumeChange = _stock.Volume() - before;
            if (p.IsMilling)
            {
                PartPass(p, body, stockHits);
            }

            KeepOutPass(p, body);
            TravelPass(p);
            Report.Steps.Add(Timing(p, volumeChange));

            if (p.IsMilling && _options.AttributeGouges)
            {
                var samples = Deviation();
                foreach (var cluster in Clusters(samples))
                {
                    if (cluster.Cells.Overlaps(attributed))
                    {
                        attributed.UnionWith(cluster.Cells);
                        continue;
                    }

                    if (cluster.Cells.Overlaps(initial))
                    {
                        initial.UnionWith(cluster.Cells);
                        continue;
                    }

                    attributed.UnionWith(cluster.Cells);
                    var worst = samples[cluster.Worst];
                    var source = NearestMove(p, worst.Point);
                    Add(new VerificationIssue(IssueKind.Gouge, IssueSeverity.Error, step.Id, source, source, worst.Point, "cutter",
                        $"Gouge {-worst.Distance:0.###} mm below the part surface ({cluster.Count} samples) near move {source}."));
                }
            }

            foreach (var (kind, count) in _suppressed)
            {
                Report.Issues.Add(new VerificationIssue(kind, IssueSeverity.Error, step.Id, -1, -1, null, null,
                    $"{count} more {kind} findings in '{step.Name}' are not listed."));
            }
        }

        // ---- Collision against the in-process stock (time-ordered) ----

        private HashSet<PoseRef> StockPass(PreparedStep p, CollisionBody body)
        {
            var segments = p.Segments;
            var hits = new List<Hit>();
            DexelStock? snapshot = null;
            try
            {
                var start = 0;
                while (start < segments.Count)
                {
                    _token.ThrowIfCancellationRequested();
                    var poses = new List<PoseRef>();
                    var end = start;
                    while (end < segments.Count && (end == start || poses.Count < _options.ChunkPoses))
                    {
                        AddPoses(p, end, poses);
                        end++;
                    }

                    if (p.IsMilling)
                    {
                        var candidates = Candidates(TestStock(_stock, p, body, poses), poses);
                        if (candidates.Count == 0)
                        {
                            p.Apply(_stock, CollectionsMarshal.AsSpan(segments)[start..end], _removed);
                        }
                        else
                        {
                            hits.AddRange(Refine(_stock, p, body, start, end, candidates));
                        }
                    }
                    else
                    {
                        // Deposits only add material: the state after the chunk bounds every state within it.
                        snapshot ??= _stock.Clone();
                        snapshot.CopyFrom(_stock);
                        p.Apply(_stock, CollectionsMarshal.AsSpan(segments)[start..end]);
                        var candidates = Candidates(TestStock(_stock, p, body, poses), poses);
                        if (candidates.Count > 0)
                        {
                            hits.AddRange(Refine(snapshot, p, body, start, end, candidates));
                        }
                    }

                    _doneSegments += end - start;
                    _progress?.Report(0.99 * _doneSegments / _totalSegments);
                    start = end;
                }
            }
            finally
            {
                snapshot?.Dispose();
            }

            ReportHits(p, body, hits, s => s.Rapid ? IssueKind.RapidCollision : IssueKind.StockCollision, p.IsMilling ? "the in-process stock" : "printed material or stock");
            return [.. hits.Select(h => h.Pose)];
        }

        /// <summary>
        /// Replays a chunk on <paramref name="scratch"/> (state before the chunk) in execution order and tests every
        /// candidate pose against the material present when the tool gets there. Leaves the state after the chunk.
        /// </summary>
        private List<Hit> Refine(DexelStock scratch, PreparedStep p, CollisionBody body, int start, int end, List<PoseRef> candidates)
        {
            const int maxBuckets = 16;
            var segments = p.Segments;
            var confirmed = new List<Hit>();
            var pending = start;
            foreach (var group in candidates.GroupBy(c => c.Segment).OrderBy(g => g.Key))
            {
                p.Apply(scratch, CollectionsMarshal.AsSpan(segments)[pending..group.Key], Measured(p));
                var segment = segments[group.Key];
                var sorted = group.OrderBy(c => c.T).ToList();
                var bucketSize = (sorted.Count + maxBuckets - 1) / maxBuckets;
                var applied = 0f;
                for (var b = 0; b < sorted.Count; b += bucketSize)
                {
                    var bucket = sorted.GetRange(b, Math.Min(bucketSize, sorted.Count - b));
                    if (segment.Active && bucket[0].T > applied)
                    {
                        p.Apply(scratch, [PreparedStep.Partial(segment, applied, bucket[0].T)], Measured(p));
                        applied = bucket[0].T;
                    }

                    var masks = TestStock(scratch, p, body, bucket);
                    for (var i = 0; i < bucket.Count; i++)
                    {
                        if (masks[i] != 0)
                        {
                            confirmed.Add(new Hit(bucket[i], masks[i]));
                        }
                    }
                }

                if (segment.Active && applied < 1)
                {
                    p.Apply(scratch, [PreparedStep.Partial(segment, applied, 1)], Measured(p));
                }

                pending = group.Key + 1;
            }

            p.Apply(scratch, CollectionsMarshal.AsSpan(segments)[pending..end], Measured(p));
            return confirmed;
        }

        /// <summary>Removal is measured while replaying milling chunks (deposits are replayed on a snapshot).</summary>
        private double[]? Measured(PreparedStep p) => p.IsMilling ? _removed : null;

        private ulong[] TestStock(DexelStock target, PreparedStep p, CollisionBody body, IReadOnlyList<PoseRef> poses) =>
            TestPoses(p, body, poses, wcs: false, alwaysFull: false, (primitives, array) => CollisionSweep.AgainstStock(target, primitives, array, _options.CollisionClearance));

        // ---- Part and keep-outs ----

        private void PartPass(PreparedStep p, CollisionBody body, HashSet<PoseRef> stockHits)
        {
            var hits = new List<Hit>();
            foreach (var batch in PoseBatches(p))
            {
                var masks = TestPoses(p, body, batch, wcs: false, alwaysFull: false, (primitives, array) => CollisionSweep.AgainstMesh(_part, primitives, array, _options.CollisionClearance, solid: true));
                for (var i = 0; i < batch.Count; i++)
                {
                    if (masks[i] != 0 && !stockHits.Contains(batch[i]))
                    {
                        hits.Add(new Hit(batch[i], masks[i]));
                    }
                }
            }

            ReportHits(p, body, hits, _ => IssueKind.PartCollision, "the part");
        }

        private void KeepOutPass(PreparedStep p, CollisionBody body)
        {
            var step = p.Step;
            var setupSolids = _job.KeepOuts.Where(k => k.Attachment == KeepOutAttachment.Setup && k.SetupId == step.SetupId).ToList();
            var wcsMeshes = _job.KeepOuts
                .Where(k => k.Attachment == KeepOutAttachment.SetupFixed && k.SetupId == step.SetupId)
                .Select(k => SolidMeshes.FromSolid(k.Solid, 0.01))
                .ToList();
            var machine = _job.KeepOuts.Where(k => k.Attachment == KeepOutAttachment.Machine).ToList();
            if (machine.Count > 0)
            {
                if (_job.WorkOffsets.TryGetValue(step.SetupId, out var offset))
                {
                    var toWcs = Matrix4x4.CreateTranslation(-offset);
                    wcsMeshes.AddRange(machine.Select(k => SolidMeshes.FromSolid(k.Solid, 0.01).Transform(toWcs)));
                }
                else
                {
                    Add(new VerificationIssue(IssueKind.MissingData, IssueSeverity.Warning, step.Id, -1, -1, null, null,
                        $"No work offset for the setup of '{step.Name}'; machine keep-out zones are not checked."));
                }
            }

            if (body.Count == 0)
            {
                return;
            }

            if (setupSolids.Count > 0)
            {
                using var mesh = new GpuMesh(_job.Compute, Mesh.Merge(setupSolids.Select(k => SolidMeshes.FromSolid(k.Solid, 0.01)), "keep-outs"));
                KeepOutHits(p, body, mesh, wcs: false);
            }

            if (wcsMeshes.Count > 0)
            {
                using var mesh = new GpuMesh(_job.Compute, Mesh.Merge(wcsMeshes, "fixed keep-outs"));
                KeepOutHits(p, body, mesh, wcs: true);
            }
        }

        private void KeepOutHits(PreparedStep p, CollisionBody body, GpuMesh mesh, bool wcs)
        {
            var hits = new List<Hit>();
            foreach (var batch in PoseBatches(p))
            {
                var masks = TestPoses(p, body, batch, wcs, alwaysFull: true, (primitives, array) => CollisionSweep.AgainstMesh(mesh, primitives, array, _options.CollisionClearance, solid: true));
                for (var i = 0; i < batch.Count; i++)
                {
                    if (masks[i] != 0)
                    {
                        hits.Add(new Hit(batch[i], masks[i]));
                    }
                }
            }

            ReportHits(p, body, hits, _ => IssueKind.KeepOutCollision, wcs ? "a fixed keep-out zone" : "a setup keep-out zone");
        }

        // ---- Poses ----

        private void AddPoses(PreparedStep p, int segment, List<PoseRef> poses)
        {
            var s = p.Segments[segment];
            var length = Vector3.Distance(s.Move.From, s.Move.To);
            var n = Math.Max(1, (int)MathF.Ceiling(length / _options.SampleSpacing));
            if (segment == 0)
            {
                poses.Add(new PoseRef(0, 0));
            }

            for (var k = 1; k <= n; k++)
            {
                poses.Add(new PoseRef(segment, (float)k / n));
            }
        }

        private IEnumerable<List<PoseRef>> PoseBatches(PreparedStep p)
        {
            var batch = new List<PoseRef>();
            for (var j = 0; j < p.Segments.Count; j++)
            {
                AddPoses(p, j, batch);
                if (batch.Count >= 4 * _options.ChunkPoses)
                {
                    yield return batch;
                    batch = [];
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
        }

        /// <summary>
        /// Masks over the whole body per pose. Rapids (and <paramref name="alwaysFull"/> checks) include the cutter;
        /// feed moves test everything above it.
        /// </summary>
        private static ulong[] TestPoses(PreparedStep p, CollisionBody body, IReadOnlyList<PoseRef> poses, bool wcs, bool alwaysFull, Func<IReadOnlyList<CollisionPrimitive>, ToolPose[], uint[]> sweep)
        {
            var result = new ulong[poses.Count];
            foreach (var full in new[] { true, false })
            {
                var indices = new List<int>();
                var array = new List<ToolPose>();
                for (var i = 0; i < poses.Count; i++)
                {
                    var s = p.Segments[poses[i].Segment];
                    if ((alwaysFull || s.Rapid) == full)
                    {
                        indices.Add(i);
                        array.Add(Pose(s, poses[i].T, wcs));
                    }
                }

                if (array.Count == 0)
                {
                    continue;
                }

                var poseArray = array.ToArray();
                var masks = body.Test(full, primitives => sweep(primitives, poseArray), poseArray.Length);
                var shift = full ? 0 : body.CutterCount;
                for (var k = 0; k < indices.Count; k++)
                {
                    result[indices[k]] = masks[k] << shift;
                }
            }

            return result;
        }

        private static ToolPose Pose(Segment s, float t, bool wcs) => wcs
            ? new ToolPose(Vector3.Lerp(s.WcsFrom, s.WcsTo, t), Vector3.UnitZ, Vector3.UnitX)
            : new ToolPose(Vector3.Lerp(s.Move.From, s.Move.To, t), s.Move.Axis, Vector3.UnitX);

        private static List<PoseRef> Candidates(ulong[] masks, List<PoseRef> poses)
        {
            var result = new List<PoseRef>();
            for (var i = 0; i < masks.Length; i++)
            {
                if (masks[i] != 0)
                {
                    result.Add(poses[i]);
                }
            }

            return result;
        }

        private void ReportHits(PreparedStep p, CollisionBody body, List<Hit> hits, Func<Segment, IssueKind> kindOf, string target)
        {
            if (hits.Count == 0)
            {
                return;
            }

            hits.Sort((a, b) => a.Pose.Segment != b.Pose.Segment ? a.Pose.Segment.CompareTo(b.Pose.Segment) : a.Pose.T.CompareTo(b.Pose.T));
            var i = 0;
            while (i < hits.Count)
            {
                var first = p.Segments[hits[i].Pose.Segment];
                var kind = kindOf(first);
                var mask = hits[i].Mask;
                var last = first.Source;
                var j = i + 1;
                while (j < hits.Count)
                {
                    var s = p.Segments[hits[j].Pose.Segment];
                    if (kindOf(s) != kind || s.Source > last + 1)
                    {
                        break;
                    }

                    mask |= hits[j].Mask;
                    last = s.Source;
                    j++;
                }

                var names = body.Describe(mask, includeCutter: true);
                var moves = first.Source == last
                    ? $"move {first.Source}"
                    : $"moves {first.Source}-{last}";
                var what = kind == IssueKind.RapidCollision ? $"Rapid through {target}: {names}" : $"{Capitalize(names)} hits {target}";
                Add(new VerificationIssue(kind, IssueSeverity.Error, p.Step.Id, first.Source, last, Pose(first, hits[i].Pose.T, false).Tip, names, $"{what} at {moves}."));
                i = j;
            }
        }

        private static string Capitalize(string text) =>
            text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];

        private void Add(VerificationIssue issue)
        {
            var count = _issueCounts.GetValueOrDefault(issue.Kind);
            if (issue.StepId is not null && count >= _options.MaxIssuesPerKind)
            {
                _suppressed[issue.Kind] = _suppressed.GetValueOrDefault(issue.Kind) + 1;
                return;
            }

            _issueCounts[issue.Kind] = count + 1;
            Report.Issues.Add(issue);
        }

        // ---- Travel and time ----

        private void TravelPass(PreparedStep p)
        {
            var positions = Positions(p.Step);
            if (positions.Count == 0)
            {
                return;
            }

            var step = p.Step;
            var known = _job.WorkOffsets.TryGetValue(step.SetupId, out var offset);
            AxisName[] linear = [AxisName.X, AxisName.Y, AxisName.Z];
            for (var axis = 0; axis < 3; axis++)
            {
                var config = _job.Machine.GetAxis(linear[axis]);
                if (config is null || config.MaxTravel <= 0)
                {
                    continue;
                }

                var values = positions.Select(q => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z }).ToArray();
                var (min, max) = (values.Min(), values.Max());
                if (max - min > config.MaxTravel + 1e-6)
                {
                    Add(new VerificationIssue(IssueKind.TravelLimit, IssueSeverity.Error, step.Id, -1, -1, null, null,
                        $"'{step.Name}' needs {max - min:0.###} mm of {linear[axis]} travel; the machine has {config.MaxTravel:0.###} mm."));
                    continue;
                }

                if (known && config.HomingCycle > 0)
                {
                    var shift = axis switch { 0 => offset.X, 1 => offset.Y, _ => offset.Z };
                    var (low, high) = config.HomingPositiveDirection
                        ? (config.HomingMachinePosition - config.MaxTravel, config.HomingMachinePosition)
                        : (config.HomingMachinePosition, config.HomingMachinePosition + config.MaxTravel);
                    var index = Array.FindIndex(values, v => v + shift < low - 1e-6 || v + shift > high + 1e-6);
                    if (index >= 0)
                    {
                        Add(new VerificationIssue(IssueKind.TravelLimit, IssueSeverity.Error, step.Id, index, index, null, null,
                            $"Move {index} of '{step.Name}' reaches machine {linear[axis]} {values[index] + shift:0.###}, outside {low:0.###}..{high:0.###}."));
                    }
                }
            }

            var a = _job.Machine.GetAxis(AxisName.A);
            if (a is { SoftLimits: true, MaxTravel: > 0 })
            {
                var span = positions.Max(q => q.A) - positions.Min(q => q.A);
                if (span > a.MaxTravel + 1e-6)
                {
                    Add(new VerificationIssue(IssueKind.TravelLimit, IssueSeverity.Error, step.Id, -1, -1, null, null,
                        $"'{step.Name}' turns A through {span:0.###}°; the axis allows {a.MaxTravel:0.###}°."));
                }
            }
        }

        private static List<MotionPosition> Positions(SimulationStep step) => step switch
        {
            MillingStep m => [.. m.Toolpath.Moves.Select(v => new MotionPosition(v.X, v.Y, v.Z, v.A))],
            DepositionStep d => [.. d.Moves.Select(v => new MotionPosition(v.X, v.Y, v.Z, v.A))],
            _ => [],
        };

        private StepSummary Timing(PreparedStep p, double volumeChange)
        {
            var step = p.Step;
            var positions = Positions(step);
            var blocks = new List<MotionBlock>(Math.Max(positions.Count - 1, 0));
            double filamentLength = 0, filamentMass = 0;
            float[] factors = [];
            if (step is MillingStep milling)
            {
                var feed = milling.Conditions?.FeedRate > 0 ? milling.Conditions.FeedRate : _options.DefaultFeed;
                var plunge = milling.Conditions?.PlungeRate > 0 ? milling.Conditions.PlungeRate : _options.DefaultPlungeFeed;
                if (milling.Conditions is not { FeedRate: > 0 })
                {
                    Add(new VerificationIssue(IssueKind.MissingData, IssueSeverity.Info, step.Id, -1, -1, null, null,
                        $"'{step.Name}' has no cutting conditions; the cycle time assumes {feed:0} mm/min feed and {plunge:0} mm/min plunge."));
                }

                var moves = milling.Toolpath.Moves;
                factors = FeedFactors(p, milling);
                for (var i = 1; i < moves.Count; i++)
                {
                    var m = moves[i];
                    var f = (m.Kind == MoveKind.Plunge ? plunge : feed) * factors[i];
                    var rapid = m.Kind == MoveKind.Rapid;
                    var inverse = !rapid && milling.Toolpath.UsesRotary && m.A != moves[i - 1].A;
                    if (inverse)
                    {
                        f /= Math.Max(Toolpath.MoveLength(moves[i - 1], m), 1e-3);
                    }

                    blocks.Add(new MotionBlock(m.X, m.Y, m.Z, m.A, f, rapid, inverse));
                }
            }
            else if (step is DepositionStep deposition)
            {
                var pathLength = new double[deposition.Moves.Count];
                foreach (var s in p.Segments)
                {
                    pathLength[s.Source] += Vector3.Distance(s.Move.From, s.Move.To);
                }

                double volume = 0;
                for (var i = 1; i < deposition.Moves.Count; i++)
                {
                    var m = deposition.Moves[i];
                    var rapid = m.Feed <= 0;
                    var f = rapid ? 0 : m.Feed / Math.Max(pathLength[i], 1e-3);
                    blocks.Add(new MotionBlock(m.X, m.Y, m.Z, m.A, f, rapid, InverseTime: !rapid));
                    if (m.Kind == PrintMoveKind.Extrude)
                    {
                        volume += m.BeadArea * pathLength[i];
                    }
                }

                var diameter = deposition.Filament?.Diameter > 0 ? deposition.Filament.Diameter : deposition.Head.Fdm?.FilamentDiameter ?? 1.75;
                filamentLength = volume / (Math.PI * diameter * diameter / 4);
                filamentMass = volume * (deposition.Filament?.Density ?? 0) / 1000;
            }

            var times = new double[Math.Max(positions.Count, 1)];
            if (positions.Count > 1)
            {
                var durations = _planner.Durations(positions[0], blocks);
                for (var i = 1; i < positions.Count; i++)
                {
                    times[i] = times[i - 1] + durations[i - 1];
                }
            }

            return new StepSummary
            {
                StepId = step.Id,
                Name = step.Name,
                Duration = times[^1],
                MoveEndTimes = times,
                FeedFactors = factors,
                VolumeChange = volumeChange,
                FilamentLength = filamentLength,
                FilamentMass = filamentMass,
            };
        }

        /// <summary>
        /// Per move, the engagement limiter's feed factor for the removed cross-section (volume / length) over a
        /// window of contiguous cutting moves around it at least a quarter tool diameter long; rapids and plunges
        /// keep 1.
        /// </summary>
        private float[] FeedFactors(PreparedStep p, MillingStep milling)
        {
            var moves = milling.Toolpath.Moves;
            var factors = new float[moves.Count];
            Array.Fill(factors, 1f);
            if (milling.Limiter is not { } limiter || _removed is not { } removed)
            {
                return factors;
            }

            var length = new double[moves.Count];
            foreach (var s in p.Segments)
            {
                length[s.Source] += Vector3.Distance(s.Move.From, s.Move.To);
            }

            var window = Math.Max(0.25 * milling.Tool.Diameter, 4 * _resolution);
            bool Cutting(int i) => i >= 1 && i < moves.Count && moves[i].Kind is not (MoveKind.Rapid or MoveKind.Plunge);
            int slowed = 0, first = -1, last = -1;
            double heaviest = 0;
            for (var i = 1; i < moves.Count; i++)
            {
                if (!Cutting(i))
                {
                    continue;
                }

                double volume = removed[i], total = length[i];
                int low = i, high = i;
                while (total < window)
                {
                    var grown = false;
                    if (Cutting(low - 1))
                    {
                        low--;
                        volume += removed[low];
                        total += length[low];
                        grown = true;
                    }

                    if (total < window && Cutting(high + 1))
                    {
                        high++;
                        volume += removed[high];
                        total += length[high];
                        grown = true;
                    }

                    if (!grown)
                    {
                        break;
                    }
                }

                factors[i] = total > 1e-9 ? (float)limiter.Factor(volume / total) : 1f;
                if (factors[i] < 1)
                {
                    slowed++;
                    first = first < 0 ? i : first;
                    last = i;
                    heaviest = Math.Max(heaviest, volume / total);
                }
            }

            if (slowed > 0)
            {
                Add(new VerificationIssue(IssueKind.FeedReduced, IssueSeverity.Info, milling.Id, first, last, null, null,
                    $"{slowed} moves of '{milling.Name}' cut more than the nominal {limiter.NominalArea:0.##} mm² (up to {heaviest:0.##} mm²) and run at down to {factors.Min():P0} feed."));
            }

            return factors;
        }

        // ---- Deviation and gouges ----

        private DeviationSample[] Deviation() => _stock.Deviation(_part, _options.DeviationRange);

        private void SummarizeDeviation(DeviationSample[] samples)
        {
            double maxGouge = 0, maxLeftover = 0, sum = 0;
            var count = 0;
            var range = _options.DeviationRange * 0.999f;
            foreach (var s in samples)
            {
                if (s.Distance < 0)
                {
                    maxGouge = Math.Max(maxGouge, -s.Distance);
                }
                else if (s.Distance < range)
                {
                    maxLeftover = Math.Max(maxLeftover, s.Distance);
                    sum += s.Distance;
                    count++;
                }
            }

            Report.Deviation = new DeviationSummary
            {
                MaxGouge = maxGouge,
                MaxLeftover = maxLeftover,
                MeanLeftover = count == 0 ? 0 : sum / count,
                Samples = samples,
            };
        }

        /// <summary>Connected groups of gouging samples (cells of about two dexel spacings, 26-neighbourhood).</summary>
        private List<(HashSet<Cell> Cells, int Worst, int Count)> Clusters(DeviationSample[] samples)
        {
            var threshold = -Threshold;
            var cells = new Dictionary<Cell, List<int>>();
            for (var i = 0; i < samples.Length; i++)
            {
                if (samples[i].Distance < threshold)
                {
                    var c = CellOf(samples[i].Point);
                    if (!cells.TryGetValue(c, out var list))
                    {
                        cells[c] = list = [];
                    }

                    list.Add(i);
                }
            }

            var result = new List<(HashSet<Cell>, int, int)>();
            var visited = new HashSet<Cell>();
            foreach (var seed in cells.Keys)
            {
                if (!visited.Add(seed))
                {
                    continue;
                }

                var members = new HashSet<Cell> { seed };
                var queue = new Queue<Cell>([seed]);
                var worst = -1;
                var count = 0;
                while (queue.TryDequeue(out var c))
                {
                    foreach (var i in cells[c])
                    {
                        count++;
                        if (worst < 0 || samples[i].Distance < samples[worst].Distance)
                        {
                            worst = i;
                        }
                    }

                    for (var dz = -1; dz <= 1; dz++)
                    {
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var n = new Cell(c.X + dx, c.Y + dy, c.Z + dz);
                                if (cells.ContainsKey(n) && visited.Add(n))
                                {
                                    members.Add(n);
                                    queue.Enqueue(n);
                                }
                            }
                        }
                    }
                }

                result.Add((members, worst, count));
            }

            return result;
        }

        private Cell CellOf(Vector3 p) =>
            new((int)MathF.Floor(p.X / _cellSize), (int)MathF.Floor(p.Y / _cellSize), (int)MathF.Floor(p.Z / _cellSize));

        /// <summary>Material-changing move whose tip path passes closest to <paramref name="point"/>.</summary>
        private static int NearestMove(PreparedStep p, Vector3 point)
        {
            var best = -1;
            var bestDistance = float.MaxValue;
            foreach (var s in p.Segments)
            {
                if (!s.Active)
                {
                    continue;
                }

                var d = s.Move.To - s.Move.From;
                var t = d.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(point - s.Move.From, d) / d.LengthSquared(), 0, 1) : 0;
                var distance = Vector3.Distance(point, s.Move.From + (d * t));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = s.Source;
                }
            }

            return best;
        }
    }
}
