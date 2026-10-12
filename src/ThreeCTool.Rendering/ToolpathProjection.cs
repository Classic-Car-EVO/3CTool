using System.Numerics;

namespace ThreeCTool.Rendering;

public enum PathStyle
{
    Done,
    Cut,
    Rapid,
}

/// <summary>A run of projected segments drawn with one style.</summary>
public sealed record ProjectedPolyline(PathStyle Style, IReadOnlyList<Vector2> Points);

/// <summary>Projects a scene to screen polylines for the 2D (no GL) fallback, styled like the GL view.</summary>
public static class ToolpathProjection
{
    public static IReadOnlyList<ProjectedPolyline> Project(ToolpathScene scene, float width, float height)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var result = new List<ProjectedPolyline>();
        var path = scene.Path;
        if (path.VertexCount == 0 || width <= 0 || height <= 0)
        {
            return result;
        }

        var viewProjection = scene.Camera.ViewProjection(width / height);
        for (var strip = 0; strip < path.StripStarts.Length; strip++)
        {
            var rapid = path.StripRapid[strip];
            if (rapid && !scene.ShowRapids)
            {
                continue;
            }

            var (start, count) = path.Strip(strip);
            List<Vector2>? points = null;
            var style = PathStyle.Cut;
            var previous = count > 0 ? OrbitCamera.Project(path.Vertex(start), viewProjection, width, height) : null;
            for (var i = start + 1; i < start + count; i++)
            {
                var current = OrbitCamera.Project(path.Vertex(i), viewProjection, width, height);
                if (previous is not { } from || current is not { } to)
                {
                    Flush();
                    previous = current;
                    continue;
                }

                var segmentStyle = path.Lines[i] <= scene.ProgressLine ? PathStyle.Done : rapid ? PathStyle.Rapid : PathStyle.Cut;
                if (points is null || segmentStyle != style)
                {
                    Flush();
                    points = [from];
                    style = segmentStyle;
                }

                points.Add(to);
                previous = current;
            }

            Flush();

            void Flush()
            {
                if (points is { Count: >= 2 })
                {
                    result.Add(new ProjectedPolyline(style, points));
                }

                points = null;
            }
        }

        return result;
    }
}
