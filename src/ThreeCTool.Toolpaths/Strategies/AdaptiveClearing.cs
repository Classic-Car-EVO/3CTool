using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Compute;
using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>
/// Adaptive (constant engagement) roughing. Each level's material is a host-side raster; a walker advances the tool
/// in short steps, turning so the engaged arc of the cutter stays at the angle of the nominal stepover, and removes
/// the swept capsule. New chains start in the air next to material, else on a helix in the largest open spot.
/// </summary>
internal static class AdaptiveClearing
{
    public static void Run(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var stockTop = (double)context.Setup.StockBounds.Max.Z;
        var top = Math.Min(settings.MaxZ ?? stockTop, stockTop);
        var levels = context.Levels(top, context.ClearingFloor, settings.Stepdown, flats: true);
        var entryTop = stockTop;
        for (var i = 0; i < levels.Count; i++)
        {
            var z = levels[i];
            var region = context.ClearingRegion(z);
            if (region.Count > 0)
            {
                new AdaptiveLevel(context, builder, region, z, entryTop).Run();
            }

            entryTop = z;
            context.Report((i + 1.0) / levels.Count);
        }
    }

    private sealed class AdaptiveLevel
    {
        private const int Samples = 12;
        private const int Bisections = 6;
        private const long MaxCells = 25_000_000;

        private readonly StrategyContext _context;
        private readonly ToolpathBuilder _builder;
        private readonly PathsD _region;
        private readonly double _z;
        private readonly double _entryTop;
        private readonly double _radius;
        private readonly double _step;
        private readonly double _target;
        private readonly double _side;
        private readonly PointD _origin;
        private readonly double _res;
        private readonly int _nx;
        private readonly int _ny;
        private readonly bool[] _material;
        private readonly bool[] _allowed;
        private readonly Vector2[] _circle;
        private readonly List<int> _skipped = [];
        private int _dirtyI0, _dirtyJ0, _dirtyI1 = -1, _dirtyJ1 = -1;

        public AdaptiveLevel(StrategyContext context, ToolpathBuilder builder, PathsD region, double z, double entryTop)
        {
            _context = context;
            _builder = builder;
            _region = region;
            _z = z;
            _entryTop = entryTop;
            _radius = context.Radius;
            var stepover = Math.Min(context.Settings.Stepover, context.Radius);
            var reach = Polygons.Offset(region, _radius, context.Tolerance);
            var bounds = Clipper.GetBounds(reach);
            var (w, h) = (bounds.right - bounds.left, bounds.bottom - bounds.top);
            _res = Math.Max(Math.Clamp(Math.Min(_radius / 8, stepover / 4), 0.02, 1), Math.Sqrt(w * h / MaxCells));
            _nx = Math.Max((int)Math.Ceiling(w / _res), 1) + 2;
            _ny = Math.Max((int)Math.Ceiling(h / _res), 1) + 2;
            _origin = new PointD(bounds.left - _res, bounds.top - _res);
            _material = RegionRaster.Rasterize(Polygons.Intersect(context.StockAbove(z), reach), _origin, _res, _nx, _ny, 0.5);

            // Eroded by one cell so any point of an allowed cell (and short chords between them) stays in the region;
            // the wall finishing pass takes the remaining sliver.
            _allowed = RegionRaster.Rasterize(Polygons.Offset(region, -_res, context.Tolerance), _origin, _res, _nx, _ny, 0.5);
            _step = Math.Clamp(stepover, 2 * _res, 0.5 * _radius);

            // Engaged share of the circumference for a straight cut at the nominal stepover: acos(1 − 2s/D) / 2π.
            _target = Math.Acos(1 - (2 * stepover / (2 * _radius))) / (2 * Math.PI);
            _side = context.Settings.Direction == CutDirection.Conventional ? -1 : 1;
            var rho = _radius + (0.5 * _res);
            var count = Math.Clamp((int)Math.Ceiling(2 * Math.PI * rho / (0.75 * _res)), 24, 1024);
            _circle = new Vector2[count];
            for (var k = 0; k < count; k++)
            {
                var (sin, cos) = Math.SinCos(2 * Math.PI * k / count);
                _circle[k] = new Vector2((float)(rho * cos), (float)(rho * sin));
            }
        }

        public void Run()
        {
            SetAsideUnreachable();
            var remaining = _material.Count(m => m);
            var minimum = Math.Max(4, (int)(0.25 * _radius * _step / (_res * _res)));
            DistanceField2D? wallDistance = null;
            var distance = new float[_material.Length];
            (_dirtyI0, _dirtyJ0, _dirtyI1, _dirtyJ1) = (0, 0, _nx - 1, _ny - 1);
            var tried = new bool[_material.Length];
            Queue<int>? starts = null;
            while (remaining > minimum)
            {
                _context.Token.ThrowIfCancellationRequested();
                if (_dirtyI1 >= 0)
                {
                    RefreshDistance(distance);
                    starts = null;
                }

                var from = _builder.Current;
                starts ??= AirStarts(distance, from, tried);
                PointD? start = starts.Count > 0 ? Center(starts.Dequeue()) : null;
                PointD? helix = null;
                double? heading = null;
                var enclosed = start is null;
                if (start is null)
                {
                    wallDistance ??= DistanceField2D.FromRegion(_context.Setup.Compute, _allowed, _nx, _ny, (float)_res, new Vector2((float)_origin.x, (float)_origin.y));
                    var (center, rh) = HelixSpot(distance, wallDistance, tried);
                    if (center is null)
                    {
                        break;
                    }

                    var c = center.Value;
                    if (_context.Settings.Entry == EntryMode.Plunge)
                    {
                        start = c;
                    }
                    else
                    {
                        helix = c;
                        start = new PointD(c.x + rh, c.y);
                        remaining -= RemoveDisk(c, rh + _radius);
                        heading = _side > 0 ? Math.PI / 2 : -Math.PI / 2;
                    }
                }

                var first = start.Value;
                var link = from is { } f && Math.Abs(f.Z - _z) < 1e-9 && LinkClear(new PointD(f.X, f.Y), first);
                var chain = Walk(first, heading, out var removed);
                remaining -= removed;
                if (chain.Count < 2 && helix is null)
                {
                    // Nothing to engage from here: try another start, the material stays for later passes.
                    tried[Cell(first.x, first.y)] = true;
                    continue;
                }

                var points = chain.Select(p => new P3(p.x, p.y, _z)).ToList();
                if (points.Count < 2)
                {
                    points.Add(points[0]);
                }

                _builder.StayDownLink = (_, _) => link ? [] : null;
                _builder.AddPass(new CutPass(points)
                {
                    EntryTop = _entryTop,
                    InMaterial = enclosed,
                    Region = _region,
                    HelixCenter = helix,
                });
                starts = null;
            }

            foreach (var k in _skipped)
            {
                _material[k] = true;
            }

            FinishWalls();
            var left = _material.Count(m => m) * _res * _res;
            if (left > Math.Max(1, _radius * _radius))
            {
                _context.Warnings.Add($"Adaptive: about {left:0.#} mm² of material left at Z {_z:0.###} where the tool could not engage.");
            }
        }

        /// <summary>Material farther than the tool radius from every allowed cell (wall slivers) goes to the wall pass.</summary>
        private void SetAsideUnreachable()
        {
            var reach = DistanceField2D.FromSites(_context.Setup.Compute, _allowed, _nx, _ny, (float)_res, new Vector2((float)_origin.x, (float)_origin.y)).Values;
            var limit = _radius + _res;
            for (var k = 0; k < _material.Length; k++)
            {
                if (_material[k] && reach[k] > limit)
                {
                    _material[k] = false;
                    _skipped.Add(k);
                }
            }
        }

        /// <summary>Free cells just outside the tool's reach of material, nearest to the tool first.</summary>
        private Queue<int> AirStarts(float[] values, P3? from, bool[] tried)
        {
            var candidates = new List<(int Cell, double Score)>();
            for (var k = 0; k < _material.Length; k++)
            {
                if (!_allowed[k] || tried[k])
                {
                    continue;
                }

                var d = values[k];
                if (d <= _radius || d > _radius + (2 * _res))
                {
                    continue;
                }

                var c = Center(k);
                var score = from is { } f ? ((c.x - f.X) * (c.x - f.X)) + ((c.y - f.Y) * (c.y - f.Y)) : 0;
                candidates.Add((k, score));
            }

            return new(candidates.OrderBy(c => c.Score).Select(c => c.Cell));
        }

        /// <summary>Allowed cell farthest from the region edge among cells next to material.</summary>
        private (PointD? Center, double Radius) HelixSpot(float[] material, DistanceField2D wall, bool[] tried)
        {
            var best = -1;
            var bestDepth = 0.0;
            for (var k = 0; k < _material.Length; k++)
            {
                if (_allowed[k] && !tried[k] && material[k] <= _radius && -wall.Values[k] > bestDepth)
                {
                    (best, bestDepth) = (k, -wall.Values[k]);
                }
            }

            var rh = Math.Min(0.75 * _radius, bestDepth - _res);
            return best < 0 || rh < 0.1 * _radius ? (null, 0) : (Center(best), rh);
        }

        private List<PointD> Walk(PointD start, double? heading, out int removed)
        {
            var points = new List<PointD> { start };
            removed = RemoveCapsule(start, start, _radius);
            var direction = heading ?? MaterialHeading(start);
            if (direction is null)
            {
                return points;
            }

            var angle = direction.Value;
            var p = start;
            var lastUseful = 0;
            var idle = 0;
            var maxIdle = (int)Math.Ceiling(2 * _radius / _step) + 4;

            // Every step either removes material or counts as idle, so the walk ends.
            while (true)
            {
                var next = Choose(p, angle);
                if (next is null)
                {
                    break;
                }

                angle = next.Value;
                var q = new PointD(p.x + (_step * Math.Cos(angle)), p.y + (_step * Math.Sin(angle)));
                var cut = RemoveCapsule(p, q, _radius);
                removed += cut;
                points.Add(q);
                p = q;
                if (cut > 0)
                {
                    lastUseful = points.Count - 1;
                    idle = 0;
                }
                else if (++idle > maxIdle)
                {
                    break;
                }
            }

            if (lastUseful < points.Count - 1)
            {
                points.RemoveRange(lastUseful + 1, points.Count - lastUseful - 1);
            }

            return points;
        }

        /// <summary>Heading with the nearest material on the cutting side (right for climb).</summary>
        private double? MaterialHeading(PointD p)
        {
            double sx = 0, sy = 0;
            var scale = (_radius + (2 * _res)) / (_radius + (0.5 * _res));
            foreach (var c in _circle)
            {
                if (IsMaterial(p.x + (c.X * scale), p.y + (c.Y * scale)))
                {
                    sx += c.X;
                    sy += c.Y;
                }
            }

            if (sx == 0 && sy == 0)
            {
                return null;
            }

            return Math.Atan2(sy, sx) + (_side * Math.PI / 2);
        }

        /// <summary>
        /// Sweeps from away-from-material to into-material and returns the first heading reaching the target
        /// engagement (refined by bisection); null when nothing is engaged any more.
        /// </summary>
        private double? Choose(PointD p, double heading)
        {
            const double span = 100 * Math.PI / 180;
            double Angle(double t) => heading + (_side * (span - (2 * span * t)));
            double Engagement(double t)
            {
                var a = Angle(t);
                var q = new PointD(p.x + (_step * Math.Cos(a)), p.y + (_step * Math.Sin(a)));
                return Allowed(q) ? Engaged(q) : double.NaN;
            }

            var previousT = double.NaN;
            var previousE = double.NaN;
            var bestT = double.NaN;
            var bestE = 0.0;
            for (var k = 0; k < Samples; k++)
            {
                var t = (double)k / (Samples - 1);
                var e = Engagement(t);
                if (double.IsNaN(e))
                {
                    previousT = double.NaN;
                    continue;
                }

                if (e >= _target)
                {
                    if (double.IsNaN(previousT))
                    {
                        return Angle(t);
                    }

                    var (lo, hi) = (previousT, t);
                    for (var b = 0; b < Bisections; b++)
                    {
                        var mid = 0.5 * (lo + hi);
                        var em = Engagement(mid);
                        if (!double.IsNaN(em) && em < _target)
                        {
                            lo = mid;
                        }
                        else
                        {
                            hi = mid;
                        }
                    }

                    return double.IsNaN(Engagement(hi)) ? Angle(previousT) : Angle(hi);
                }

                if (e > bestE)
                {
                    (bestT, bestE) = (t, e);
                }

                (previousT, previousE) = (t, e);
            }

            _ = previousE;
            return double.IsNaN(bestT) ? null : Angle(bestT);
        }

        private double Engaged(PointD q)
        {
            var hits = 0;
            foreach (var c in _circle)
            {
                if (IsMaterial(q.x + c.X, q.y + c.Y))
                {
                    hits++;
                }
            }

            return (double)hits / _circle.Length;
        }

        /// <summary>Cleared straight link: inside the region and no material under the swept tool.</summary>
        private bool LinkClear(PointD a, PointD b)
        {
            if (!Polygons.ContainsSegment(_region, a, b))
            {
                return false;
            }

            return !ForCapsule(a, b, _radius - _res, false);
        }

        private void FinishWalls()
        {
            // Wall contours: the region edge where it runs along the part / keep-outs.
            var walls = Polygons.Offset(_context.Forbidden(_z), 0.1 * _radius, _context.Tolerance);
            var lines = new PathsD(_region.Select(loop => new PathD(loop) { loop[0] }));
            var pieces = Polygons.ClipLines(lines, walls).Where(l => l.Count >= 2 && Polygons.Length(l, false) > 4 * _context.Tolerance).ToList();
            var conventional = _context.Settings.Direction == CutDirection.Conventional;
            foreach (var piece in pieces)
            {
                // Clipper may flip open paths: keep the region on the left (climb) by checking against the loop order.
                if (!RegionOnLeft(piece) ^ conventional)
                {
                    piece.Reverse();
                }
            }

            _builder.StayDownLink = (a, b) => Math.Abs(a.Z - b.Z) < 1e-9 && LinkClear(new PointD(a.X, a.Y), new PointD(b.X, b.Y)) ? [] : null;
            while (pieces.Count > 0)
            {
                var (index, _) = PassTools.Nearest(pieces, _builder.Current, l => new P3(l[0].x, l[0].y, _z), _ => default, false);
                var piece = pieces[index];
                pieces.RemoveAt(index);
                _builder.AddPass(new CutPass(PassTools.Line(piece, _z)) { EntryTop = _entryTop, InMaterial = true, Region = _region });
                for (var k = 1; k < piece.Count; k++)
                {
                    RemoveCapsule(piece[k - 1], piece[k], _radius + (0.5 * _res));
                }
            }
        }

        private bool RegionOnLeft(PathD line)
        {
            for (var k = 1; k < line.Count; k++)
            {
                var (a, b) = (line[k - 1], line[k]);
                var length = Polygons.Distance(a, b);
                if (length < 1e-3)
                {
                    continue;
                }

                const double probe = 2e-3;
                var mid = new PointD(0.5 * (a.x + b.x), 0.5 * (a.y + b.y));
                var left = new PointD(mid.x - ((b.y - a.y) / length * probe), mid.y + ((b.x - a.x) / length * probe));
                var right = new PointD(mid.x + ((b.y - a.y) / length * probe), mid.y - ((b.x - a.x) / length * probe));
                var (inLeft, inRight) = (Polygons.Contains(_region, left), Polygons.Contains(_region, right));
                if (inLeft != inRight)
                {
                    return inLeft;
                }
            }

            return true;
        }

        /// <summary>
        /// Recomputes the distance to material around the cells removed since the last refresh. Only distances up to
        /// the air-start window matter, so a window padded by twice that reach gives the same values as the full grid.
        /// </summary>
        private void RefreshDistance(float[] values)
        {
            if (_dirtyI1 < 0)
            {
                return;
            }

            var m = (int)Math.Ceiling((_radius + (2 * _res)) / _res) + 1;
            var (i0, j0) = (Math.Max(_dirtyI0 - (2 * m), 0), Math.Max(_dirtyJ0 - (2 * m), 0));
            var (i1, j1) = (Math.Min(_dirtyI1 + (2 * m), _nx - 1), Math.Min(_dirtyJ1 + (2 * m), _ny - 1));
            var (wx, wy) = (i1 - i0 + 1, j1 - j0 + 1);
            var sites = new bool[wx * wy];
            for (var j = j0; j <= j1; j++)
            {
                Array.Copy(_material, (j * _nx) + i0, sites, (j - j0) * wx, wx);
            }

            var window = DistanceField2D.FromSites(_context.Setup.Compute, sites, wx, wy, (float)_res).Values;
            for (var j = Math.Max(_dirtyJ0 - m, 0); j <= Math.Min(_dirtyJ1 + m, _ny - 1); j++)
            {
                for (var i = Math.Max(_dirtyI0 - m, 0); i <= Math.Min(_dirtyI1 + m, _nx - 1); i++)
                {
                    values[(j * _nx) + i] = window[((j - j0) * wx) + (i - i0)];
                }
            }

            (_dirtyI0, _dirtyJ0, _dirtyI1, _dirtyJ1) = (int.MaxValue, int.MaxValue, -1, -1);
        }

        private PointD Center(int k) => new(_origin.x + ((k % _nx + 0.5) * _res), _origin.y + ((k / _nx + 0.5) * _res));

        private bool Allowed(PointD p) => Cell(p.x, p.y) is var k and >= 0 && _allowed[k];

        private bool IsMaterial(double x, double y) => Cell(x, y) is var k and >= 0 && _material[k];

        private int Cell(double x, double y)
        {
            var i = (int)Math.Floor((x - _origin.x) / _res);
            var j = (int)Math.Floor((y - _origin.y) / _res);
            return i < 0 || j < 0 || i >= _nx || j >= _ny ? -1 : (j * _nx) + i;
        }

        private int RemoveDisk(PointD c, double radius) => RemoveCapsule(c, c, radius);

        private int RemoveCapsule(PointD a, PointD b, double radius)
        {
            var removed = 0;
            ForCapsule(a, b, radius, true, k =>
            {
                removed++;
                var (i, j) = (k % _nx, k / _nx);
                (_dirtyI0, _dirtyI1) = (Math.Min(_dirtyI0, i), Math.Max(_dirtyI1, i));
                (_dirtyJ0, _dirtyJ1) = (Math.Min(_dirtyJ0, j), Math.Max(_dirtyJ1, j));
            });
            return removed;
        }

        /// <summary>Visits material cells whose centres lie within <paramref name="radius"/> of segment ab; returns whether any exist.</summary>
        private bool ForCapsule(PointD a, PointD b, double radius, bool clear, Action<int>? removed = null)
        {
            var any = false;
            var r2 = radius * radius;
            var i0 = Math.Max((int)Math.Floor(((Math.Min(a.x, b.x) - radius - _origin.x) / _res) - 0.5), 0);
            var i1 = Math.Min((int)Math.Ceiling(((Math.Max(a.x, b.x) + radius - _origin.x) / _res) - 0.5), _nx - 1);
            var j0 = Math.Max((int)Math.Floor(((Math.Min(a.y, b.y) - radius - _origin.y) / _res) - 0.5), 0);
            var j1 = Math.Min((int)Math.Ceiling(((Math.Max(a.y, b.y) + radius - _origin.y) / _res) - 0.5), _ny - 1);
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var k = (j * _nx) + i;
                    if (!_material[k])
                    {
                        continue;
                    }

                    var c = new PointD(_origin.x + ((i + 0.5) * _res), _origin.y + ((j + 0.5) * _res));
                    if (Polygons.SegmentDistanceSquared(c, a, b) > r2)
                    {
                        continue;
                    }

                    any = true;
                    if (!clear)
                    {
                        return true;
                    }

                    _material[k] = false;
                    removed?.Invoke(k);
                }
            }

            return any;
        }
    }
}
