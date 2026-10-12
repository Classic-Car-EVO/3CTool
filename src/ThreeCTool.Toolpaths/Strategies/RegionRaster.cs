using Clipper2Lib;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>Scanline rasterization of regions to row-major masks (non-zero winding).</summary>
internal static class RegionRaster
{
    /// <summary>Mask of the samples at origin + (i + <paramref name="offset"/>, j + <paramref name="offset"/>)·spacing inside the region.</summary>
    public static bool[] Rasterize(PathsD region, PointD origin, double spacing, int nx, int ny, double offset = 0)
    {
        var mask = new bool[nx * ny];
        Fill(region, origin, spacing, nx, ny, offset, mask, true);
        return mask;
    }

    /// <summary>Sets mask samples inside the region to <paramref name="value"/>.</summary>
    public static void Fill(PathsD region, PointD origin, double spacing, int nx, int ny, double offset, bool[] mask, bool value)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(mask);
        var edges = new List<(double X0, double Y0, double X1, double Y1, int Winding)>();
        foreach (var path in region)
        {
            for (var k = 0; k < path.Count; k++)
            {
                var a = path[k];
                var b = path[(k + 1) % path.Count];
                if (a.y != b.y)
                {
                    edges.Add(a.y < b.y ? (a.x, a.y, b.x, b.y, 1) : (b.x, b.y, a.x, a.y, -1));
                }
            }
        }

        if (edges.Count == 0)
        {
            return;
        }

        edges.Sort((p, q) => p.Y0.CompareTo(q.Y0));
        Parallel.For(0, ny, j =>
        {
            var y = origin.y + ((j + offset) * spacing);
            var crossings = new List<(double X, int W)>();
            foreach (var e in edges)
            {
                if (e.Y0 > y)
                {
                    break;
                }

                if (y < e.Y1)
                {
                    crossings.Add((e.X0 + ((e.X1 - e.X0) * (y - e.Y0) / (e.Y1 - e.Y0)), e.Winding));
                }
            }

            if (crossings.Count == 0)
            {
                return;
            }

            crossings.Sort((p, q) => p.X.CompareTo(q.X));
            var winding = 0;
            for (var c = 0; c < crossings.Count - 1; c++)
            {
                winding += crossings[c].W;
                if (winding == 0)
                {
                    continue;
                }

                var i0 = Math.Max((int)Math.Ceiling(((crossings[c].X - origin.x) / spacing) - offset), 0);
                var i1 = Math.Min((int)Math.Ceiling(((crossings[c + 1].X - origin.x) / spacing) - offset) - 1, nx - 1);
                for (var i = i0; i <= i1; i++)
                {
                    mask[(j * nx) + i] = value;
                }
            }
        });
    }
}
