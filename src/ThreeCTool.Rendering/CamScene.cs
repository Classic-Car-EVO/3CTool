using System.Numerics;

using ThreeCTool.Geometry;

namespace ThreeCTool.Rendering;

/// <summary>Groups of scene content the user can show or hide.</summary>
public enum SceneLayer
{
    Part,
    Stock,
    KeepOut,
    Toolpath,

    /// <summary>Simulated stock and the deviation heatmap.</summary>
    Simulation,
    Tool,
}

/// <summary>Triangle mesh in the scene (part frame), drawn with one colour; alpha &lt; 1 is translucent.</summary>
public sealed record SceneMesh(SceneLayer Layer, string Name, Mesh Mesh, Vector4 Color);

/// <summary>Coloured points (simulated stock surface, heatmap); <paramref name="Size"/> is their world size (mm).</summary>
public sealed record ScenePoints(SceneLayer Layer, string Name, Vector3[] Positions, Vector4[] Colors, float Size)
{
    public Vector3[] Positions { get; } = Positions.Length == Colors.Length
        ? Positions
        : throw new ArgumentException("Expected one colour per point.", nameof(Colors));
}

/// <summary>Tool path of one operation; <see cref="PolylinePath.Lines"/> holds 1-based move numbers.</summary>
public sealed record SceneToolpath(Guid Id, string Name, PolylinePath Path, Vector4 Color);

/// <summary>Cutter drawn as a cylinder from the tip along the (unit) tool axis.</summary>
public readonly record struct ToolMarker(Vector3 Tip, Vector3 Axis, float Diameter, float Length);

/// <summary>
/// What the CAM workspace shows: part, stock and keep-out meshes, the tool paths of the operations, simulation points
/// and the tool. Shared by the GL view and the software view; <see cref="Changed"/> asks views to redraw and
/// <see cref="Version"/> changes whenever content (not only display state) must be re-uploaded.
/// </summary>
public sealed class CamScene : IOrbitScene
{
    /// <summary>Alpha factor of tool paths other than the selected one.</summary>
    public const float DimFactor = 0.2f;

    private readonly HashSet<SceneLayer> hidden = [];
    private IReadOnlyList<SceneMesh> meshes = [];
    private IReadOnlyList<ScenePoints> points = [];
    private IReadOnlyList<SceneToolpath> toolpaths = [];
    private Guid? selected;
    private (int Path, int Line)? progress;
    private ToolMarker? tool;
    private bool showRapids = true;

    public event EventHandler? Changed;

    public OrbitCamera Camera { get; } = new();

    public float Aspect { get; set; } = 1;

    public int Version { get; private set; }

    public IReadOnlyList<SceneMesh> Meshes => meshes;

    public IReadOnlyList<ScenePoints> Points => points;

    public IReadOnlyList<SceneToolpath> Toolpaths => toolpaths;

    /// <summary>Highlighted tool path; the others are dimmed. Null shows all alike.</summary>
    public Guid? SelectedToolpath
    {
        get => selected;
        set => Set(ref selected, value);
    }

    /// <summary>Playback position: tool paths before <c>Path</c> are done, and moves up to <c>Line</c> of it.</summary>
    public (int Path, int Line)? Progress
    {
        get => progress;
        set => Set(ref progress, value);
    }

    public ToolMarker? Tool
    {
        get => tool;
        set => Set(ref tool, value);
    }

    public bool ShowRapids
    {
        get => showRapids;
        set => Set(ref showRapids, value);
    }

    public void SetMeshes(IEnumerable<SceneMesh> value) => Replace(ref meshes, value);

    public void SetPoints(IEnumerable<ScenePoints> value) => Replace(ref points, value);

    public void SetToolpaths(IEnumerable<SceneToolpath> value) => Replace(ref toolpaths, value);

    public bool IsVisible(SceneLayer layer) => !hidden.Contains(layer);

    public void SetVisible(SceneLayer layer, bool visible)
    {
        if (visible ? hidden.Remove(layer) : hidden.Add(layer))
        {
            Invalidate();
        }
    }

    /// <summary>Last executed move number of tool path <paramref name="index"/> (0 = nothing done).</summary>
    public int ProgressLine(int index) => progress switch
    {
        null => 0,
        var (path, _) when index < path => int.MaxValue,
        var (path, line) when index == path => line,
        _ => 0,
    };

    /// <summary>Draw colour of tool path <paramref name="index"/>: its own colour, dimmed when another is selected.</summary>
    public Vector4 ToolpathColor(int index) => Dim(toolpaths[index].Color, toolpaths[index].Id);

    public Vector4 RapidColor(int index) => Dim(ToolpathColors.Rapid, toolpaths[index].Id);

    /// <summary>Bounds of the visible content, or null when nothing is visible.</summary>
    public (Vector3 Min, Vector3 Max)? Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        void Add(Vector3 a, Vector3 b)
        {
            min = Vector3.Min(min, a);
            max = Vector3.Max(max, b);
            any = true;
        }

        foreach (var mesh in meshes.Where(m => IsVisible(m.Layer) && m.Mesh.VertexCount > 0))
        {
            var box = mesh.Mesh.Bounds;
            Add(box.Min, box.Max);
        }

        foreach (var cloud in points.Where(p => IsVisible(p.Layer)))
        {
            foreach (var p in cloud.Positions)
            {
                Add(p, p);
            }
        }

        if (IsVisible(SceneLayer.Toolpath))
        {
            foreach (var path in toolpaths.Where(t => t.Path.VertexCount > 0))
            {
                Add(path.Path.Min, path.Path.Max);
            }
        }

        return any ? (min, max) : null;
    }

    /// <summary>Fits the camera to the visible content (or a 100 mm cube around the origin when empty).</summary>
    public void Fit()
    {
        var (min, max) = Bounds() ?? (new Vector3(-50), new Vector3(50));
        Camera.Fit(min, max, Aspect);
        Invalidate();
    }

    /// <summary>Centres the camera on <paramref name="point"/> showing about <paramref name="radius"/> around it.</summary>
    public void Focus(Vector3 point, float radius)
    {
        Camera.Fit(point - new Vector3(radius), point + new Vector3(radius), Aspect);
        Invalidate();
    }

    public void SetView(CameraView view)
    {
        Camera.SetView(view);
        Fit();
    }

    public void Invalidate() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Independent copy of the content, display state and camera (for rendering off screen).</summary>
    public CamScene Clone()
    {
        var copy = new CamScene
        {
            Aspect = Aspect,
            meshes = meshes,
            points = points,
            toolpaths = toolpaths,
            selected = selected,
            progress = progress,
            tool = tool,
            showRapids = showRapids,
        };
        copy.hidden.UnionWith(hidden);
        copy.Camera.CopyFrom(Camera);
        return copy;
    }

    private Vector4 Dim(Vector4 color, Guid id) =>
        selected is { } s && s != id ? color with { W = color.W * DimFactor } : color;

    private void Replace<T>(ref IReadOnlyList<T> field, IEnumerable<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        field = [.. value];
        Version++;
        Invalidate();
    }

    private void Set<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Invalidate();
        }
    }
}

/// <summary>Colours of CAM scene content (RGBA 0..1).</summary>
public static class SceneColors
{
    private static readonly Vector4[] Palette =
    [
        new(0.35f, 0.65f, 1f, 1f),
        new(0.95f, 0.8f, 0.25f, 1f),
        new(0.75f, 0.45f, 1f, 1f),
        new(0.25f, 0.9f, 0.85f, 1f),
        new(1f, 0.5f, 0.75f, 1f),
        new(0.6f, 0.9f, 0.3f, 1f),
        new(1f, 0.65f, 0.4f, 1f),
        new(0.55f, 0.6f, 1f, 1f),
    ];

    public static Vector4 Part { get; } = new(0.72f, 0.75f, 0.8f, 1f);

    public static Vector4 Stock { get; } = new(0.85f, 0.65f, 0.4f, 0.22f);

    public static Vector4 KeepOut { get; } = new(0.95f, 0.25f, 0.2f, 0.3f);

    public static Vector4 SimulatedStock { get; } = new(0.82f, 0.68f, 0.48f, 1f);

    public static Vector4 Tool { get; } = new(1f, 0.3f, 0.3f, 0.55f);

    /// <summary>Distinct colour of the operation at <paramref name="index"/> in execution order.</summary>
    public static Vector4 Operation(int index) => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

    /// <summary>Headlight shading shared by both renderers: ambient plus two-sided diffuse towards the eye.</summary>
    public static Vector4 Shade(Vector4 color, Vector3 normal, Vector3 toEye)
    {
        var light = 0.3f + (0.7f * MathF.Abs(Vector3.Dot(normal, toEye)));
        return new Vector4(color.X * light, color.Y * light, color.Z * light, color.W);
    }
}

/// <summary>Deviation colours: green on size, shading to red for gouges and to blue for leftover material.</summary>
public static class Heatmap
{
    public static Vector4 OnSize { get; } = new(0.2f, 0.85f, 0.3f, 1f);

    public static Vector4 Gouge { get; } = new(0.95f, 0.15f, 0.15f, 1f);

    public static Vector4 Leftover { get; } = new(0.2f, 0.35f, 1f, 1f);

    /// <summary>
    /// Colour of a signed distance (negative = gouge): within ±<paramref name="tolerance"/> on size, beyond it
    /// blending to full gouge/leftover colour at ±<paramref name="range"/>.
    /// </summary>
    public static Vector4 Color(float distance, float tolerance, float range)
    {
        var magnitude = MathF.Abs(distance);
        if (!float.IsFinite(distance) || magnitude <= tolerance)
        {
            return OnSize;
        }

        var t = range > tolerance ? Math.Clamp((magnitude - tolerance) / (range - tolerance), 0, 1) : 1;
        return Vector4.Lerp(OnSize, distance < 0 ? Gouge : Leftover, MathF.Sqrt(t));
    }
}
