using System.Globalization;
using System.Numerics;

using Clipper2Lib;

using ThreeCTool.Core.Projects;
using ThreeCTool.Geometry;

namespace ThreeCTool.Toolpaths;

/// <summary>Material kept so a part machined from two opposite faces stays held: frame and tabs, in the part frame.</summary>
public sealed record TabLayout(Mesh Mesh, IReadOnlyList<string> Warnings);

/// <summary>
/// Frame and tabs of a 3-axis setup with <see cref="Setup.TabCount"/> tabs: a border <see cref="Setup.FrameWidth"/>
/// wide all round the stock (its full height along the setup's tool axis) and bars at even angles around the part
/// section on the depth limit plane, from just inside the part outline out to the stock edge,
/// <see cref="Setup.TabThickness"/> thick and centred on the plane. Every setup on the same axis
/// (<see cref="StockTurn.SameAxis"/>) treats them like the part, so they are left standing.
/// </summary>
public static class TabSolids
{
    /// <summary>Frame and tabs of <paramref name="setup"/> cut down to <paramref name="depthLimit"/>; null without tabs.</summary>
    public static TabLayout? Create(Setup setup, double depthLimit, Stock stock, Mesh part)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(part);
        if (setup.TabCount <= 0 || setup.Orientation != SetupOrientation.ThreeAxis || stock.Type != StockType.Box)
        {
            return null;
        }

        var stockBox = SolidMeshes.Box(
            new Vector3((float)stock.OriginX, (float)stock.OriginY, (float)stock.OriginZ),
            new Vector3((float)(stock.OriginX + stock.SizeX), (float)(stock.OriginY + stock.SizeY), (float)(stock.OriginZ + stock.SizeZ)),
            "stock");
        var frame = MachiningSetup.CreateFrame(setup, stock, stockBox.Bounds);
        var toWcs = frame.PartToWcsMatrix(0);
        if (!Matrix4x4.Invert(toWcs, out var toPart))
        {
            return null;
        }

        var box = stockBox.Transform(toWcs).Bounds;
        double x0 = box.Min.X, y0 = box.Min.Y, z0 = box.Min.Z, x1 = box.Max.X, y1 = box.Max.Y, z1 = box.Max.Z;
        var f = setup.FrameWidth;
        var plane = z1 - depthLimit;
        var warnings = new List<string>();
        var meshes = new List<Mesh>
        {
            Box(x0, y0, z0, x0 + f, y1, z1),
            Box(x1 - f, y0, z0, x1, y1, z1),
            Box(x0 + f, y0, z0, x1 - f, y0 + f, z1),
            Box(x0 + f, y1 - f, z0, x1 - f, y1, z1),
        };
        if (2 * f >= Math.Min(x1 - x0, y1 - y0))
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Setup '{setup.Name}': the {f:0.###} mm frame covers the whole stock."));
        }

        var section = MeshSlicer.Slice(part.Transform(toWcs), plane);
        if (section.Count == 0)
        {
            warnings.Add($"Setup '{setup.Name}': the part does not cross the depth limit plane, so it gets no tabs.");
        }
        else
        {
            var bounds = Clipper.GetBounds(section);
            if (bounds.left < x0 + f || bounds.right > x1 - f || bounds.top < y0 + f || bounds.bottom > y1 - f)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Setup '{setup.Name}': the part reaches into the {f:0.###} mm frame."));
            }

            var center = new PointD(0.5 * (bounds.left + bounds.right), 0.5 * (bounds.top + bounds.bottom));
            var (w, t) = (setup.TabWidth, setup.TabThickness);
            for (var k = 0; k < setup.TabCount; k++)
            {
                var angle = 2 * Math.PI * k / setup.TabCount;
                var (dy, dx) = Math.SinCos(angle);
                var start = FarthestCrossing(section, center, dx, dy) - (w / 2);
                var end = Exit(center, dx, dy, x0, y0, x1, y1);
                if (start is not { } s || end <= s)
                {
                    continue;
                }

                var place = Matrix4x4.CreateRotationZ((float)angle) * Matrix4x4.CreateTranslation((float)center.x, (float)center.y, 0);
                meshes.Add(Box(Math.Max(s, 0), -w / 2, plane - (t / 2), end, w / 2, plane + (t / 2)).Transform(place));
            }
        }

        return new TabLayout(Mesh.Merge(meshes, "tabs").Transform(toPart), warnings);
    }

    private static Mesh Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        SolidMeshes.Box(new Vector3((float)x0, (float)y0, (float)z0), new Vector3((float)x1, (float)y1, (float)z1), "tab");

    /// <summary>Largest distance along the ray from <paramref name="c"/> at which it crosses the region outline; null when it misses.</summary>
    private static double? FarthestCrossing(PathsD region, PointD c, double dx, double dy)
    {
        double? farthest = null;
        foreach (var path in region)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var p = path[i];
                var q = path[(i + 1) % path.Count];
                var (ex, ey) = (q.x - p.x, q.y - p.y);
                var denominator = (dx * ey) - (dy * ex);
                if (Math.Abs(denominator) < 1e-12)
                {
                    continue;
                }

                var (wx, wy) = (p.x - c.x, p.y - c.y);
                var t = ((wx * ey) - (wy * ex)) / denominator;
                var s = ((wx * dy) - (wy * dx)) / denominator;
                if (t >= 0 && s >= 0 && s <= 1 && (farthest is null || t > farthest))
                {
                    farthest = t;
                }
            }
        }

        return farthest;
    }

    /// <summary>Distance along the ray from <paramref name="c"/> to the edge of the rectangle.</summary>
    private static double Exit(PointD c, double dx, double dy, double x0, double y0, double x1, double y1)
    {
        var tx = dx > 1e-12 ? (x1 - c.x) / dx : dx < -1e-12 ? (x0 - c.x) / dx : double.PositiveInfinity;
        var ty = dy > 1e-12 ? (y1 - c.y) / dy : dy < -1e-12 ? (y0 - c.y) / dy : double.PositiveInfinity;
        return Math.Min(tx, ty);
    }
}
