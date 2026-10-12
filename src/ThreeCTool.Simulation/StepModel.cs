using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Simulation;

/// <summary>Tool or head collision body in the tool frame (tip at the origin, +Z towards the spindle).</summary>
internal sealed class CollisionBody
{
    public const int MaxPrimitives = 2 * CollisionSweep.MaxPrimitives;

    private readonly List<CollisionPrimitive> _primitives = [];
    private readonly List<string> _names = [];

    /// <summary>Leading primitives that approximate the cutter (cutting, so excluded during feed moves).</summary>
    public int CutterCount { get; private set; }

    public int Count => _primitives.Count;

    public IReadOnlyList<CollisionPrimitive> Primitives => _primitives;

    /// <summary>Milling: cutter as stacked inscribed cylinders, shank, holder segments and the spindle head solids.</summary>
    public static CollisionBody ForMilling(CutterProfile cutter, Core.Tooling.Tool tool, Collet? collet, Toolhead? spindle, float contact, ICollection<string> notes)
    {
        var body = new CollisionBody();
        const int slices = 4;
        var profileTop = Math.Clamp(cutter.ProfileHeight, 0, cutter.Height);
        for (var k = 0; k < slices && profileTop > 0; k++)
        {
            var y0 = profileTop * k / slices;
            var y1 = profileTop * (k + 1) / slices;
            body.AddCylinder("cutter", MathF.Max(y0, contact), y1, cutter.RadiusAt(0.5f * (y0 + y1)) - contact);
        }

        body.AddCylinder("cutter", MathF.Max(profileTop, contact), cutter.Height, cutter.Radius - contact);
        body.CutterCount = body.Count;

        var stickout = (float)tool.Stickout;
        var shankRadius = (float)(tool.ShankDiameter > 0 ? tool.ShankDiameter / 2 : cutter.Radius);
        if (stickout <= 0)
        {
            notes.Add($"Tool '{tool.Name}' has no stickout; shank, holder and spindle head are not checked.");
            return body;
        }

        body.AddCylinder("shank", cutter.Height + contact, stickout, shankRadius - contact);
        if (collet is null || collet.Holder.Count == 0)
        {
            notes.Add($"Tool '{tool.Name}' has no collet holder outline; the holder is not checked.");
        }
        else
        {
            var z = stickout;
            for (var i = 0; i < collet.Holder.Count; i++)
            {
                var segment = collet.Holder[i];
                body.AddCylinder($"holder {i + 1}", z + contact, z + (float)segment.Length, (float)(segment.Diameter / 2) - contact);
                z += (float)segment.Length;
            }
        }

        if (spindle is null || spindle.CollisionModel.Count == 0)
        {
            notes.Add("The spindle toolhead has no collision model; spindle and dust shoe are not checked.");
        }
        else
        {
            body.AddSolids(spindle.CollisionModel, stickout, contact, float.NegativeInfinity);
        }

        return body;
    }

    /// <summary>Printing: the head solids relative to the nozzle tip; anything below the tip is ignored.</summary>
    public static CollisionBody ForHead(Toolhead head, float contact, ICollection<string> notes)
    {
        var body = new CollisionBody();
        if (head.CollisionModel.Count == 0)
        {
            notes.Add($"Toolhead '{head.Name}' has no collision model; the head is not checked.");
            return body;
        }

        body.AddSolids(head.CollisionModel, 0, contact, contact);
        return body;
    }

    public IReadOnlyList<CollisionPrimitive> Range(bool includeCutter) =>
        includeCutter ? _primitives : _primitives.GetRange(CutterCount, Count - CutterCount);

    /// <summary>Names of the primitives set in <paramref name="mask"/> (bit 0 = first primitive of the range).</summary>
    public string Describe(ulong mask, bool includeCutter)
    {
        var offset = includeCutter ? 0 : CutterCount;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (var k = 0; k < 64 && mask != 0; k++, mask >>= 1)
        {
            if ((mask & 1) != 0 && k + offset < _names.Count)
            {
                names.Add(_names[k + offset]);
            }
        }

        return string.Join(", ", names);
    }

    /// <summary>Runs a 32-primitive sweep per block of the range and merges the masks.</summary>
    public ulong[] Test(bool includeCutter, Func<IReadOnlyList<CollisionPrimitive>, uint[]> sweep, int poses)
    {
        var range = Range(includeCutter);
        var result = new ulong[poses];
        if (range.Count == 0 || poses == 0)
        {
            return result;
        }

        for (var start = 0; start < range.Count; start += CollisionSweep.MaxPrimitives)
        {
            var count = Math.Min(CollisionSweep.MaxPrimitives, range.Count - start);
            var masks = sweep([.. range.Skip(start).Take(count)]);
            for (var i = 0; i < poses; i++)
            {
                result[i] |= (ulong)masks[i] << start;
            }
        }

        return result;
    }

    private void AddSolids(IEnumerable<SimpleSolid> solids, float zOffset, float contact, float minBottom)
    {
        foreach (var solid in solids)
        {
            var bottom = MathF.Max((float)solid.BottomZ + zOffset + contact, minBottom);
            var top = (float)(solid.BottomZ + solid.Height) + zOffset - contact;
            var name = string.IsNullOrEmpty(solid.Name) ? "head" : solid.Name;
            if (solid.Shape == SolidShape.Cylinder)
            {
                AddCylinder(name, bottom, top, (float)solid.Radius - contact, (float)solid.CenterX, (float)solid.CenterY);
            }
            else
            {
                var half = new Vector2((float)(solid.SizeX / 2) - contact, (float)(solid.SizeY / 2) - contact);
                if (half.X > 0 && half.Y > 0 && top > bottom)
                {
                    var center = new Vector2((float)solid.CenterX, (float)solid.CenterY);
                    Add(name, CollisionPrimitive.Box(new Vector3(center - half, bottom), new Vector3(center + half, top)));
                }
            }
        }
    }

    private void AddCylinder(string name, float bottom, float top, float radius, float x = 0, float y = 0)
    {
        if (radius > 0 && top > bottom)
        {
            Add(name, CollisionPrimitive.Cylinder(bottom, top, radius, x, y));
        }
    }

    private void Add(string name, CollisionPrimitive primitive)
    {
        if (_primitives.Count >= MaxPrimitives)
        {
            throw new InvalidOperationException($"A collision body may have at most {MaxPrimitives} primitives.");
        }

        _primitives.Add(primitive);
        _names.Add(name);
    }
}

/// <summary>Straight piece of a move: tip sweep in the part frame plus the WCS tip positions (tool along +Z).</summary>
internal readonly record struct Segment(ToolMove Move, Vector3 WcsFrom, Vector3 WcsTo, int Source, bool Rapid, bool Active, float Width, float Height);

/// <summary>A step expanded for the simulator and the playback session.</summary>
internal sealed class PreparedStep
{
    private PreparedStep(SimulationStep step, SetupFrame frame, List<Segment> segments, int moveCount)
    {
        Step = step;
        Frame = frame;
        Segments = segments;
        MoveCount = moveCount;
    }

    public SimulationStep Step { get; }

    public SetupFrame Frame { get; }

    /// <summary>Pieces in execution order; <see cref="Segment.Source"/> is the move they belong to.</summary>
    public List<Segment> Segments { get; }

    public int MoveCount { get; }

    /// <summary>Cutter used for removal (milling only).</summary>
    public CutterProfile Cutter { get; private init; }

    public bool IsMilling => Step is MillingStep;

    public static PreparedStep Create(SimulationStep step, double maxAngleStep)
    {
        switch (step)
        {
            case MillingStep milling:
            {
                var moves = milling.Toolpath.Moves;
                var segments = Expand(milling.Toolpath.Frame, moves.Count, i => moves[i], i => moves[i].Kind == MoveKind.Rapid, _ => (0, 0), maxAngleStep);
                return new PreparedStep(step, milling.Toolpath.Frame, segments, moves.Count) { Cutter = CutterProfile.FromTool(milling.Tool) };
            }

            case DepositionStep deposition:
            {
                var moves = deposition.Moves;
                var segments = Expand(
                    deposition.Frame,
                    moves.Count,
                    i => new ToolpathMove(moves[i].Kind == PrintMoveKind.Travel ? MoveKind.Rapid : MoveKind.Cut, moves[i].X, moves[i].Y, moves[i].Z, moves[i].A),
                    i => moves[i].Kind == PrintMoveKind.Travel,
                    i => ((float)moves[i].Width, (float)moves[i].Height),
                    maxAngleStep);
                return new PreparedStep(step, deposition.Frame, segments, moves.Count);
            }

            default:
                throw new ArgumentException($"Unsupported step type {step.GetType().Name}.", nameof(step));
        }
    }

    /// <summary>
    /// Applies the material change of <paramref name="segments"/> (removal or deposition) to the stock; for milling,
    /// adds the removed volume per source move to <paramref name="removed"/> when given.
    /// </summary>
    public void Apply(DexelStock stock, ReadOnlySpan<Segment> segments, double[]? removed = null)
    {
        if (IsMilling)
        {
            var moves = new List<ToolMove>(segments.Length);
            var sources = new List<int>(segments.Length);
            foreach (var s in segments)
            {
                if (s.Active)
                {
                    moves.Add(s.Move);
                    sources.Add(s.Source);
                }
            }

            if (moves.Count > 0)
            {
                var volumes = removed is null ? [] : new double[moves.Count];
                stock.Remove(Cutter, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(moves), volumes);
                for (var i = 0; i < volumes.Length; i++)
                {
                    removed![sources[i]] += volumes[i];
                }
            }

            return;
        }

        var batch = new List<ToolMove>();
        var (width, height) = (0f, 0f);
        foreach (var s in segments)
        {
            if (!s.Active || s.Width <= 0 || s.Height <= 0)
            {
                continue;
            }

            if (batch.Count > 0 && (s.Width != width || s.Height != height))
            {
                stock.Deposit(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch), width, height);
                batch.Clear();
            }

            (width, height) = (s.Width, s.Height);
            batch.Add(s.Move);
        }

        if (batch.Count > 0)
        {
            stock.Deposit(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch), width, height);
        }
    }

    /// <summary>Same segment cut off at parameter <paramref name="t"/> (0..1) along it.</summary>
    public static Segment Partial(Segment s, float t0, float t1) => s with
    {
        Move = new ToolMove(Vector3.Lerp(s.Move.From, s.Move.To, t0), Vector3.Lerp(s.Move.From, s.Move.To, t1), s.Move.Axis),
        WcsFrom = Vector3.Lerp(s.WcsFrom, s.WcsTo, t0),
        WcsTo = Vector3.Lerp(s.WcsFrom, s.WcsTo, t1),
    };

    private static List<Segment> Expand(
        SetupFrame frame,
        int count,
        Func<int, ToolpathMove> move,
        Func<int, bool> rapid,
        Func<int, (float Width, float Height)> bead,
        double maxAngleStep)
    {
        var result = new List<Segment>(count);
        for (var i = 1; i < count; i++)
        {
            var (from, to) = (move(i - 1), move(i));
            var turning = frame.IsRotary && to.A != from.A;
            var pieces = turning ? Math.Max(1, (int)Math.Ceiling(Math.Abs(to.A - from.A) / maxAngleStep)) : 1;
            var previous = InPart(frame, from);
            var (width, height) = bead(i);
            for (var k = 1; k <= pieces; k++)
            {
                var t = (double)k / pieces;
                var current = InPart(frame, Lerp(from, to, t));
                var axis = turning ? InPart(frame, Lerp(from, to, (k - 0.5) / pieces)).Axis : current.Axis;
                var wcsFrom = Lerp(from, to, (double)(k - 1) / pieces);
                var wcsTo = Lerp(from, to, t);
                result.Add(new Segment(new ToolMove(previous.Tip, current.Tip, axis), ToVector(wcsFrom.Position), ToVector(wcsTo.Position), i, rapid(i), !rapid(i), width, height));
                previous = current;
            }
        }

        return result;
    }

    private static ToolpathMove Lerp(ToolpathMove a, ToolpathMove b, double t) =>
        new(b.Kind, a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t), a.A + ((b.A - a.A) * t));

    private static (Vector3 Tip, Vector3 Axis) InPart(SetupFrame frame, ToolpathMove move)
    {
        var (tip, axis) = frame.ToolInPart(move);
        return (ToVector(tip), Vector3.Normalize(ToVector(axis)));
    }

    private static Vector3 ToVector(P3 p) => new((float)p.X, (float)p.Y, (float)p.Z);
}
