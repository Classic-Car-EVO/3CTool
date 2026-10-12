using System.Numerics;

namespace ThreeCTool.Rendering;

/// <summary>
/// What a tool path view shows: path, camera, progress and tool marker. Shared by the GL view and the 2D fallback;
/// <see cref="Changed"/> asks views to redraw.
/// </summary>
public sealed class ToolpathScene
{
    private int progressLine;
    private Vector3? marker;
    private bool showRapids = true;

    public event EventHandler? Changed;

    public PolylinePath Path { get; private set; } = PolylinePath.Empty;

    /// <summary>Incremented whenever <see cref="Path"/> is replaced, so GL views know to re-upload.</summary>
    public int PathVersion { get; private set; }

    public OrbitCamera Camera { get; } = new();

    /// <summary>Last executed G-code line; 0 = nothing done yet.</summary>
    public int ProgressLine
    {
        get => progressLine;
        set => Set(ref progressLine, value);
    }

    /// <summary>Tool position in the path frame, or null to hide the marker.</summary>
    public Vector3? Marker
    {
        get => marker;
        set => Set(ref marker, value);
    }

    public bool ShowRapids
    {
        get => showRapids;
        set => Set(ref showRapids, value);
    }

    /// <summary>Size of the tool marker: 2 % of the path's diagonal, at least 1 mm.</summary>
    public float MarkerSize => Math.Max(Vector3.Distance(Path.Min, Path.Max) * 0.02f, 1f);

    /// <summary>Last aspect ratio a view reported; used to fit the camera.</summary>
    public float Aspect { get; set; } = 1;

    public void SetPath(PolylinePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
        PathVersion++;
        progressLine = 0;
        Fit();
    }

    /// <summary>Fits the camera to the path (or a 100 mm cube around the origin when empty).</summary>
    public void Fit()
    {
        if (Path.VertexCount > 0)
        {
            Camera.Fit(Path.Min, Path.Max, Aspect);
        }
        else
        {
            Camera.Fit(new Vector3(-50), new Vector3(50), Aspect);
        }

        Invalidate();
    }

    public void SetView(CameraView view)
    {
        Camera.SetView(view);
        Fit();
    }

    public void Invalidate() => Changed?.Invoke(this, EventArgs.Empty);

    private void Set<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            Invalidate();
        }
    }
}
