using Clipper2Lib;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Toolpaths.Strategies;

/// <summary>Parallel (raster) finishing: lines across the boundary dropped onto the part.</summary>
internal static class ParallelFinishing
{
    public static void Run(StrategyContext context, ToolpathBuilder builder)
    {
        var settings = context.Settings;
        var lines = PassTools.Raster(context.Boundary, settings.RasterAngle, settings.Stepover);
        var step = SampleStep(context);
        var runs = SurfaceProjection.Project(context, lines.Select(l => PassTools.Resample(l.Path, step, false)).ToList(), step);
        context.Report(0.8);

        // Runs keep the raster order; zig-zag flips every other row.
        var rows = runs.GroupBy(r => lines[r.Source].Row).OrderBy(g => g.Key).ToList();
        builder.StayDownLink = (a, b) => context.SurfaceLink(a, b, Math.Max(2 * settings.Stepover, context.Radius));
        for (var r = 0; r < rows.Count; r++)
        {
            var reverse = settings.Direction switch
            {
                CutDirection.Conventional => true,
                CutDirection.Both => r % 2 == 1,
                _ => false,
            };
            var row = rows[r].Select(x => x.Points).ToList();
            if (reverse)
            {
                row.Reverse();
            }

            foreach (var points in row)
            {
                if (reverse)
                {
                    points.Reverse();
                }

                builder.AddPass(new CutPass(points));
            }
        }
    }

    /// <summary>Initial sampling step along passes before chord refinement.</summary>
    public static double SampleStep(StrategyContext context) =>
        Math.Clamp(Math.Sqrt(8 * context.Radius * context.Tolerance), 0.05, Math.Max(context.Radius / 2, 0.1));
}
