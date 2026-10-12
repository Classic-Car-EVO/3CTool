using Clipper2Lib;

using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths;

/// <summary>One continuous cutting pass, linked and entered by <see cref="ToolpathBuilder"/>.</summary>
public sealed class CutPass
{
    public CutPass(List<P3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Points = points;
    }

    /// <summary>Tip positions in order; closed loops repeat the first point at the end.</summary>
    public List<P3> Points { get; }

    public P3 Start => Points[0];

    public P3 End => Points[^1];

    /// <summary>Material reaches up to this height above the start; the entry descends from here (NaN = start Z).</summary>
    public double EntryTop { get; init; } = double.NaN;

    /// <summary>The start is surrounded by material at its level: ramp or helix in (else plunge in air).</summary>
    public bool InMaterial { get; init; }

    /// <summary>Region the tip may move in at the pass level (helix and lead checks); null = unknown.</summary>
    public PathsD? Region { get; init; }

    /// <summary>Wall-following pass that gets lead-in/out arcs.</summary>
    public bool Contour { get; init; }

    /// <summary>Explicit helix centre for the entry (its radius is the distance to the start).</summary>
    public PointD? HelixCenter { get; init; }

    public double Length
    {
        get
        {
            double length = 0;
            for (var i = 1; i < Points.Count; i++)
            {
                length += P3.Distance(Points[i - 1], Points[i]);
            }

            return length;
        }
    }

    public CutPass Reversed() => new(Enumerable.Reverse(Points).ToList())
    {
        EntryTop = EntryTop,
        InMaterial = InMaterial,
        Region = Region,
        Contour = Contour,
    };
}

/// <summary>
/// Turns ordered cutting passes into moves: stay-down links or retracts, rapid approaches, ramp/helix/plunge entries
/// and lead arcs. Works in the strategy frame (X, Y, Z with the tool along +Z); <see cref="Map"/> converts to WCS
/// moves (e.g. unwrapped rotary coordinates to X/Z/A).
/// </summary>
public sealed class ToolpathBuilder
{
    private readonly ToolpathSettings _settings;
    private readonly double _radius;
    private readonly List<ToolpathMove> _moves = [];
    private P3? _current;
    private int _plungeEntries;

    public ToolpathBuilder(ToolpathSettings settings, double toolRadius, double clearance, Func<P3, MoveKind, ToolpathMove> map)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _radius = toolRadius;
        Clearance = clearance;
        Map = map ?? throw new ArgumentNullException(nameof(map));
    }

    /// <summary>Retract height (Z in the strategy frame).</summary>
    public double Clearance { get; }

    public Func<P3, MoveKind, ToolpathMove> Map { get; }

    /// <summary>
    /// Stay-down link from the end of one pass to the start of the next: intermediate points (excluding the start of
    /// the next pass), or null to retract. Ignored when linking is <see cref="Core.Projects.LinkingMode.Retract"/>.
    /// </summary>
    public Func<P3, P3, List<P3>?>? StayDownLink { get; set; }

    public P3? Current => _current;

    public int PlungeEntries => _plungeEntries;

    public List<ToolpathMove> Moves => _moves;

    public void AddPass(CutPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (pass.Points.Count < 2)
        {
            return;
        }

        var leadIn = pass.Contour && !pass.InMaterial ? Lead(pass, true) : null;
        var leadOut = pass.Contour ? Lead(pass, false) : null;
        var start = leadIn?[0] ?? pass.Start;
        if (!TryStayDown(start))
        {
            Approach(pass, start, leadIn is not null);
        }

        if (leadIn is not null)
        {
            foreach (var p in leadIn.Skip(1))
            {
                Emit(p, MoveKind.LeadIn);
            }
        }

        foreach (var p in pass.Points.Skip(1))
        {
            Emit(p, MoveKind.Cut);
        }

        if (leadOut is not null)
        {
            foreach (var p in leadOut.Skip(1))
            {
                Emit(p, MoveKind.LeadOut);
            }
        }
    }

    /// <summary>Feed moves that are not part of a pass (e.g. adaptive cutting), linked like a pass start.</summary>
    public void Feed(P3 target, MoveKind kind) => Emit(target, kind);

    /// <summary>Retract to the clearance height.</summary>
    public void Retract()
    {
        if (_current is { } c && c.Z < Clearance - 1e-9)
        {
            Emit(c with { Z = Clearance }, MoveKind.Rapid);
        }
    }

    /// <summary>Retracts and returns the moves.</summary>
    public List<ToolpathMove> Finish()
    {
        Retract();
        return _moves;
    }

    private bool TryStayDown(P3 start)
    {
        if (_current is not { } current || _settings.Linking == Core.Projects.LinkingMode.Retract || StayDownLink is null)
        {
            return false;
        }

        if (P3.Distance(current, start) < 1e-9)
        {
            return true;
        }

        var link = StayDownLink(current, start);
        if (link is null)
        {
            return false;
        }

        foreach (var p in link)
        {
            Emit(p, MoveKind.Link);
        }

        Emit(start, MoveKind.Link);
        return true;
    }

    private void Approach(CutPass pass, P3 start, bool hasLead)
    {
        if (_current is null)
        {
            Emit(start with { Z = Clearance }, MoveKind.Rapid);
        }
        else
        {
            Retract();
            Emit(start with { Z = Clearance }, MoveKind.Rapid);
        }

        var top = double.IsNaN(pass.EntryTop) ? start.Z : Math.Max(pass.EntryTop, start.Z);
        var approach = Math.Min(Clearance, top + _settings.RetractClearance);
        if (approach > _current!.Value.Z - 1e-9)
        {
            approach = _current.Value.Z;
        }

        Emit(start with { Z = approach }, MoveKind.Rapid);
        if (!pass.InMaterial || hasLead || top - start.Z < 1e-6)
        {
            Emit(start, MoveKind.Plunge);
            return;
        }

        Emit(start with { Z = top }, MoveKind.Plunge);
        var entry = _settings.Entry switch
        {
            Core.Projects.EntryMode.Helix => Helix(pass, top) ?? Ramp(pass, top),
            Core.Projects.EntryMode.Ramp => pass.HelixCenter is null ? Ramp(pass, top) : Helix(pass, top),
            _ => null,
        };
        if (entry is null)
        {
            _plungeEntries++;
            Emit(start, MoveKind.Plunge);
            return;
        }

        foreach (var (p, kind) in entry)
        {
            Emit(p, kind);
        }
    }

    /// <summary>Helix ending at the pass start, inside the pass region, then one flat turn.</summary>
    private List<(P3, MoveKind)>? Helix(CutPass pass, double top)
    {
        var start = pass.Start;
        var sign = _settings.Direction == Core.Projects.CutDirection.Conventional ? -1 : 1;
        if (pass.HelixCenter is { } given)
        {
            return HelixAround(given, Polygons.Distance(given, new PointD(start.X, start.Y)), start, top, sign);
        }

        if (pass.Region is null)
        {
            return null;
        }

        var (tx, ty) = Direction(pass.Points, 0);
        foreach (var factor in (ReadOnlySpan<double>)[0.75, 0.5, 0.25])
        {
            var rh = factor * _radius;
            foreach (var side in (ReadOnlySpan<int>)[1, -1])
            {
                var center = new PointD(start.X - (side * ty * rh), start.Y + (side * tx * rh));
                if (Polygons.Contains(pass.Region, center) && Polygons.DistanceToBoundary(pass.Region, center) >= rh - 1e-6)
                {
                    return HelixAround(center, rh, start, top, sign);
                }
            }
        }

        return null;
    }

    private List<(P3, MoveKind)> HelixAround(PointD center, double rh, P3 start, double top, int sign)
    {
        var depth = top - start.Z;
        var slope = Math.Tan(_settings.RampAngle * Math.PI / 180);
        var turns = Math.Max(1, (int)Math.Ceiling(depth / (2 * Math.PI * rh * slope)));
        var perTurn = Polygons.CircleSegments(rh, Math.Max(_settings.Tolerance, 1e-3));
        var phi0 = Math.Atan2(start.Y - center.y, start.X - center.x);
        var moves = new List<(P3, MoveKind)>();
        var total = turns * perTurn;
        for (var k = 1; k <= total + perTurn; k++)
        {
            var phi = phi0 + (sign * 2 * Math.PI * k / perTurn);
            var z = k >= total ? start.Z : top - (depth * k / total);
            var p = k % perTurn == 0
                ? start with { Z = z }
                : new P3(center.x + (rh * Math.Cos(phi)), center.y + (rh * Math.Sin(phi)), z);
            moves.Add((p, k > total ? MoveKind.Cut : MoveKind.Ramp));
        }

        return moves;
    }

    /// <summary>Zig-zag ramp along the start of the pass at the ramp angle, ending at the pass start.</summary>
    private List<(P3, MoveKind)>? Ramp(CutPass pass, double top)
    {
        var points = pass.Points;
        var available = Math.Min(pass.Length, Math.Max(2 * _radius, 1));
        if (available < 0.5 * _radius)
        {
            return null;
        }

        var depth = top - pass.Start.Z;
        var travel = depth / Math.Tan(_settings.RampAngle * Math.PI / 180);
        var legs = Math.Max(2, (int)Math.Ceiling(travel / available));
        if (legs % 2 == 1)
        {
            legs++;
        }

        var leg = travel / legs;

        // Polyline prefix of length `leg` (forward along the pass).
        var prefix = new List<(double S, P3 P)> { (0, points[0]) };
        double s = 0;
        for (var i = 1; i < points.Count && s < leg; i++)
        {
            var d = P3.Distance(points[i - 1], points[i]);
            if (s + d >= leg)
            {
                prefix.Add((leg, P3.Lerp(points[i - 1], points[i], (leg - s) / d)));
                s = leg;
                break;
            }

            s += d;
            prefix.Add((s, points[i]));
        }

        if (s < leg - 1e-9)
        {
            return null;
        }

        var moves = new List<(P3, MoveKind)>();
        for (var l = 0; l < legs; l++)
        {
            var forward = l % 2 == 0;
            var sequence = forward ? prefix.Skip(1) : Enumerable.Reverse(prefix).Skip(1);
            foreach (var (ps, p) in sequence)
            {
                var along = (l * leg) + (forward ? ps : leg - ps);
                moves.Add((p with { Z = top - (depth * along / travel) }, MoveKind.Ramp));
            }
        }

        moves[^1] = (pass.Start, MoveKind.Ramp);
        return moves;
    }

    /// <summary>Quarter-arc lead on the side away from the material (left for climb), or null when not allowed.</summary>
    private List<P3>? Lead(CutPass pass, bool entry)
    {
        var r = _settings.LeadRadius;
        if (r <= 0)
        {
            return null;
        }

        var points = pass.Points;
        var (tx, ty) = entry ? Direction(points, 0) : Direction(points, points.Count - 1);
        var side = _settings.Direction == Core.Projects.CutDirection.Conventional ? -1.0 : 1.0;
        var (nx, ny) = (-ty * side, tx * side);
        var anchor = entry ? pass.Start : pass.End;
        var center = new PointD(anchor.X + (r * nx), anchor.Y + (r * ny));
        var segments = Math.Max(2, Polygons.CircleSegments(r, Math.Max(_settings.Tolerance, 1e-3)) / 4);
        var arc = new List<P3>(segments + 1);
        for (var k = 0; k <= segments; k++)
        {
            // Angle 0 = anchor (−n from the centre); entry runs from −90° to 0°, exit from 0° to +90° (CCW for side +1).
            var t = entry ? -1 + ((double)k / segments) : (double)k / segments;
            var angle = side * t * Math.PI / 2;
            var (sin, cos) = Math.SinCos(angle);
            var ux = (-nx * cos) - (-ny * sin);
            var uy = (-nx * sin) + (-ny * cos);
            arc.Add(new P3(center.x + (r * ux), center.y + (r * uy), anchor.Z));
        }

        if (entry)
        {
            arc[^1] = anchor;
        }
        else
        {
            arc[0] = anchor;
        }

        var allowed = pass.Region is not null && arc.All(p => Polygons.Contains(pass.Region, new PointD(p.X, p.Y)));
        return allowed ? arc : null;
    }

    private static (double X, double Y) Direction(List<P3> points, int index)
    {
        var (a, b) = index == 0 ? (0, 1) : (index - 1, index);
        for (var step = 0; step < points.Count; step++)
        {
            var dx = points[b].X - points[a].X;
            var dy = points[b].Y - points[a].Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length > 1e-9)
            {
                return (dx / length, dy / length);
            }

            (a, b) = index == 0 ? (a, Math.Min(b + 1, points.Count - 1)) : (Math.Max(a - 1, 0), b);
        }

        return (1, 0);
    }

    private void Emit(P3 p, MoveKind kind)
    {
        if (_current is { } c && P3.Distance(c, p) < 1e-9)
        {
            return;
        }

        _moves.Add(Map(p, kind));
        _current = p;
    }
}
