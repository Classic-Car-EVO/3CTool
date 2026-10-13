using System.Numerics;

using ThreeCTool.Geometry;

namespace ThreeCTool.Rendering;

/// <summary>
/// CPU renderer of a <see cref="CamScene"/> for displays without usable OpenGL: depth-buffered flat-shaded
/// triangles, square points and one-pixel lines, with translucent meshes blended back to front like the GL view.
/// Pixels are 0xAARRGGBB (BGRA bytes in memory), always opaque.
/// </summary>
public sealed class SoftwareRasterizer
{
    private uint[] pixels = [];
    private float[] depth = [];
    private Matrix4x4 viewProjection;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ReadOnlySpan<uint> Pixels => pixels;

    public uint this[int x, int y] => pixels[(y * Width) + x];

    public static uint Pack(Vector4 color)
    {
        static uint Channel(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return 0xFF000000u | (Channel(color.X) << 16) | (Channel(color.Y) << 8) | Channel(color.Z);
    }

    public void Render(CamScene scene, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (width != Width || height != Height)
        {
            (Width, Height) = (width, height);
            pixels = new uint[width * height];
            depth = new float[width * height];
        }

        Array.Fill(pixels, Pack(ToolpathColors.Background));
        Array.Fill(depth, float.PositiveInfinity);
        var camera = scene.Camera;
        viewProjection = camera.ViewProjection((float)width / height);
        var toEye = camera.Direction;

        foreach (var mesh in scene.Meshes.Where(m => m.Color.W >= 0.999f && scene.IsVisible(m.Layer)))
        {
            DrawMesh(mesh.Mesh, mesh.Color, toEye, writeDepth: true);
        }

        var pointScale = height / 2f / MathF.Tan(camera.FieldOfView / 2);
        foreach (var cloud in scene.Points.Where(p => scene.IsVisible(p.Layer)))
        {
            for (var i = 0; i < cloud.Positions.Length; i++)
            {
                DrawPoint(cloud.Positions[i], cloud.Colors[i], cloud.Size * pointScale);
            }
        }

        if (scene.IsVisible(SceneLayer.Toolpath))
        {
            // Lines are pulled towards the eye so they win against the surface they lie on.
            var bias = toEye * (camera.Distance * 0.002f);
            for (var index = 0; index < scene.Toolpaths.Count; index++)
            {
                DrawToolpath(scene, index, bias);
            }
        }

        var eye = camera.Eye;
        var translucent = scene.Meshes
            .Where(m => m.Color.W < 0.999f && scene.IsVisible(m.Layer))
            .Select(m => (m.Mesh, m.Color))
            .ToList();
        if (scene.Tool is { } pose && scene.IsVisible(SceneLayer.Tool))
        {
            translucent.Add((SceneGeometry.ToolMesh(pose), SceneColors.Tool));
        }

        foreach (var (mesh, color) in translucent.OrderByDescending(t => Vector3.Distance(eye, t.Mesh.Bounds.Center)))
        {
            DrawMesh(mesh, color, toEye, writeDepth: false);
        }
    }

    private static uint Blend(uint destination, Vector4 color)
    {
        var a = color.W;
        var r = (((destination >> 16) & 0xFF) / 255f * (1 - a)) + (color.X * a);
        var g = (((destination >> 8) & 0xFF) / 255f * (1 - a)) + (color.Y * a);
        var b = ((destination & 0xFF) / 255f * (1 - a)) + (color.Z * a);
        return Pack(new Vector4(r, g, b, 1));
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => ((b.X - a.X) * (y - a.Y)) - ((b.Y - a.Y) * (x - a.X));

    /// <summary>Pixel x, y and NDC depth (0 near .. 1 far), or null when behind the near plane.</summary>
    private Vector3? Project(Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), viewProjection);
        if (clip.W <= 1e-6f || clip.Z < 0)
        {
            return null;
        }

        return new Vector3(((clip.X / clip.W) + 1) * 0.5f * Width, (1 - (clip.Y / clip.W)) * 0.5f * Height, clip.Z / clip.W);
    }

    private void Write(int index, float z, Vector4 color, bool writeDepth)
    {
        if (z >= depth[index] || z > 1)
        {
            return;
        }

        if (writeDepth)
        {
            depth[index] = z;
        }

        pixels[index] = color.W >= 0.999f ? Pack(color) : Blend(pixels[index], color);
    }

    private void DrawMesh(Mesh mesh, Vector4 color, Vector3 toEye, bool writeDepth)
    {
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.GetTriangle(t);
            if (Project(a) is not { } pa || Project(b) is not { } pb || Project(c) is not { } pc)
            {
                continue;
            }

            Triangle(pa, pb, pc, SceneColors.Shade(color, mesh.FaceNormal(t), toEye), writeDepth);
        }
    }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector4 color, bool writeDepth)
    {
        var area = Edge(a, b, c.X, c.Y);
        if (MathF.Abs(area) < 1e-9f)
        {
            return;
        }

        var x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var x1 = Math.Min(Width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var y1 = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        const float Inside = -1e-5f;
        for (var y = y0; y <= y1; y++)
        {
            var py = y + 0.5f;
            for (var x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                var w0 = Edge(b, c, px, py) / area;
                var w1 = Edge(c, a, px, py) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < Inside || w1 < Inside || w2 < Inside)
                {
                    continue;
                }

                Write((y * Width) + x, (w0 * a.Z) + (w1 * b.Z) + (w2 * c.Z), color, writeDepth);
            }
        }
    }

    private void DrawPoint(Vector3 position, Vector4 color, float worldToPixels)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), viewProjection);
        if (Project(position) is not { } p)
        {
            return;
        }

        var size = Math.Clamp(worldToPixels / clip.W, 1, 32);
        var x0 = Math.Max(0, (int)MathF.Round(p.X - (size / 2)));
        var y0 = Math.Max(0, (int)MathF.Round(p.Y - (size / 2)));
        var x1 = Math.Min(Width - 1, (int)MathF.Round(p.X + (size / 2)) - 1);
        var y1 = Math.Min(Height - 1, (int)MathF.Round(p.Y + (size / 2)) - 1);
        for (var y = y0; y <= Math.Max(y0, y1) && y < Height; y++)
        {
            for (var x = x0; x <= Math.Max(x0, x1) && x < Width; x++)
            {
                Write((y * Width) + x, p.Z, color, writeDepth: true);
            }
        }
    }

    private void DrawToolpath(CamScene scene, int index, Vector3 bias)
    {
        var path = scene.Toolpaths[index].Path;
        var progress = scene.ProgressLine(index);
        var cut = scene.ToolpathColor(index);
        var rapid = scene.RapidColor(index);
        var done = ToolpathColors.Done with { W = cut.W };
        for (var strip = 0; strip < path.StripStarts.Length; strip++)
        {
            var isRapid = path.StripRapid[strip];
            if (isRapid && !scene.ShowRapids)
            {
                continue;
            }

            var (start, count) = path.Strip(strip);
            var previous = count > 0 ? Project(path.Vertex(start) + bias) : null;
            for (var i = start + 1; i < start + count; i++)
            {
                var current = Project(path.Vertex(i) + bias);
                if (previous is { } from && current is { } to)
                {
                    Line(from, to, path.Lines[i] <= progress ? done : isRapid ? rapid : cut);
                }

                previous = current;
            }
        }
    }

    private void Line(Vector3 from, Vector3 to, Vector4 color)
    {
        // Liang–Barsky clip to the viewport, then step one pixel at a time along the major axis.
        var d = to - from;
        float t0 = 0, t1 = 1;
        if (!Clip(-d.X, from.X, ref t0, ref t1) || !Clip(d.X, Width - 1 - from.X, ref t0, ref t1)
            || !Clip(-d.Y, from.Y, ref t0, ref t1) || !Clip(d.Y, Height - 1 - from.Y, ref t0, ref t1))
        {
            return;
        }

        var a = from + (t0 * d);
        var b = from + (t1 * d);
        var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y)));
        for (var s = 0; s <= steps; s++)
        {
            var p = steps == 0 ? a : Vector3.Lerp(a, b, (float)s / steps);
            var x = (int)p.X;
            var y = (int)p.Y;
            if ((uint)x < (uint)Width && (uint)y < (uint)Height)
            {
                Write((y * Width) + x, p.Z, color, writeDepth: false);
            }
        }

        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (MathF.Abs(p) < 1e-12f)
            {
                return q >= 0;
            }

            var r = q / p;
            if (p < 0)
            {
                if (r > t1)
                {
                    return false;
                }

                t0 = MathF.Max(t0, r);
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }

                t1 = MathF.Min(t1, r);
            }

            return true;
        }
    }
}
