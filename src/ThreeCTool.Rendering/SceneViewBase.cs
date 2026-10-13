using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ThreeCTool.Rendering;

public enum PreviewRenderer
{
    /// <summary>OpenGL when it initialises, otherwise the software view.</summary>
    Auto,
    OpenGl,
    Software,
}

/// <summary>Scene a <see cref="SceneViewBase"/> navigates: camera, view aspect and redraw requests.</summary>
public interface IOrbitScene
{
    event EventHandler? Changed;

    OrbitCamera Camera { get; }

    /// <summary>Last aspect ratio a view reported; used to fit the camera.</summary>
    float Aspect { get; set; }

    void Invalidate();
}

/// <summary>OpenGL control of a scene view; reports whether GL came up.</summary>
public interface ISceneGlControl
{
    /// <summary>Raised on the UI thread once GL is running.</summary>
    event EventHandler? GlReady;

    /// <summary>Raised on the UI thread when GL could not be initialised or was lost.</summary>
    event EventHandler<string>? GlFailed;

    bool IsGlReady { get; }
}

/// <summary>
/// 3D view with touch/mouse navigation: one-finger (left) drag orbits (pans in <see cref="PanMode"/> or with the
/// right/middle button), two fingers pinch-zoom and pan, the wheel zooms. Renders with OpenGL, falling back to a
/// software control when GL is unavailable or does not start within two seconds.
/// </summary>
public abstract class SceneViewBase : Panel
{
    public static readonly StyledProperty<PreviewRenderer> RendererProperty =
        AvaloniaProperty.Register<SceneViewBase, PreviewRenderer>(nameof(Renderer));

    public static readonly StyledProperty<bool> PanModeProperty =
        AvaloniaProperty.Register<SceneViewBase, bool>(nameof(PanMode));

    private const float OrbitRadiansPerPixel = 0.008f;
    private static readonly TimeSpan GlTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<IPointer, Point> pointers = [];
    private readonly Control software;
    private Control? glView;
    private DispatcherTimer? glTimer;
    private bool panDrag;

    protected SceneViewBase(Control software)
    {
        ArgumentNullException.ThrowIfNull(software);
        this.software = software;
        ClipToBounds = true;
        Background = Avalonia.Media.Brushes.Transparent;
        Children.Add(software);
        Focusable = true;
    }

    public PreviewRenderer Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    public bool PanMode
    {
        get => GetValue(PanModeProperty);
        set => SetValue(PanModeProperty, value);
    }

    /// <summary>True while the OpenGL view is showing; false for the software view.</summary>
    public bool UsingOpenGl => glView is { IsVisible: true } and ISceneGlControl { IsGlReady: true };

    /// <summary>The OpenGL control while one exists.</summary>
    protected Control? GlView => glView;

    protected abstract IOrbitScene? OrbitScene { get; }

    /// <summary>Creates the OpenGL control (a <see cref="Control"/> implementing <see cref="ISceneGlControl"/>).</summary>
    protected abstract Control CreateGlView();

    /// <summary>Call when the scene was replaced, after handing it to the child controls.</summary>
    protected void OnSceneReplaced() => UpdateAspect(Bounds.Size);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyRenderer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopGlTimer();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == RendererProperty && VisualRoot is not null)
        {
            ApplyRenderer();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateAspect(Bounds.Size);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        pointers[e.Pointer] = point.Position;
        panDrag = PanMode || point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (OrbitScene is not { } scene || !pointers.TryGetValue(e.Pointer, out var last))
        {
            return;
        }

        var position = e.GetPosition(this);
        if (pointers.Count >= 2)
        {
            // Two fingers: the distance between them zooms, their midpoint pans.
            var other = pointers.First(p => p.Key != e.Pointer).Value;
            var before = Point.Distance(last, other);
            var after = Point.Distance(position, other);
            if (before > 1 && after > 1)
            {
                scene.Camera.Zoom((float)(before / after));
            }

            var delta = (position - last) / 2;
            scene.Camera.Pan((float)delta.X, (float)delta.Y, (float)Bounds.Height);
        }
        else
        {
            var delta = position - last;
            if (panDrag)
            {
                scene.Camera.Pan((float)delta.X, (float)delta.Y, (float)Bounds.Height);
            }
            else
            {
                scene.Camera.Orbit((float)-delta.X * OrbitRadiansPerPixel, (float)delta.Y * OrbitRadiansPerPixel);
            }
        }

        pointers[e.Pointer] = position;
        scene.Invalidate();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        pointers.Remove(e.Pointer);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerCaptureLost(e);
        pointers.Remove(e.Pointer);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (OrbitScene is { } scene)
        {
            scene.Camera.Zoom(MathF.Pow(0.85f, (float)e.Delta.Y));
            scene.Invalidate();
            e.Handled = true;
        }
    }

    private void UpdateAspect(Size size)
    {
        if (OrbitScene is { } scene && size.Width > 0 && size.Height > 0)
        {
            var aspect = (float)(size.Width / size.Height);
            if (Math.Abs(aspect - scene.Aspect) > 1e-3)
            {
                scene.Aspect = aspect;
                scene.Invalidate();
            }
        }
    }

    private void ApplyRenderer()
    {
        StopGlTimer();
        if (Renderer == PreviewRenderer.Software)
        {
            RemoveGl();
            software.IsVisible = true;
            return;
        }

        if (glView is null)
        {
            glView = CreateGlView();
            glView.IsHitTestVisible = false;
            var gl = (ISceneGlControl)glView;
            gl.GlReady += OnGlReady;
            gl.GlFailed += OnGlFailed;
            Children.Add(glView);
        }

        // The software view stays visible underneath until GL has drawn its first frame.
        software.IsVisible = true;
        var ready = ((ISceneGlControl)glView).IsGlReady;
        if (!ready && Renderer == PreviewRenderer.Auto)
        {
            glTimer = new DispatcherTimer(GlTimeout, DispatcherPriority.Background, (_, _) =>
            {
                StopGlTimer();
                if (glView is ISceneGlControl { IsGlReady: false })
                {
                    RemoveGl();
                }
            });
            glTimer.Start();
        }
        else if (ready)
        {
            software.IsVisible = false;
        }
    }

    private void OnGlReady(object? sender, EventArgs e)
    {
        StopGlTimer();
        software.IsVisible = false;
    }

    private void OnGlFailed(object? sender, string reason)
    {
        StopGlTimer();
        RemoveGl();
        software.IsVisible = true;
    }

    private void RemoveGl()
    {
        if (glView is null)
        {
            return;
        }

        var gl = (ISceneGlControl)glView;
        gl.GlReady -= OnGlReady;
        gl.GlFailed -= OnGlFailed;
        Children.Remove(glView);
        glView = null;
    }

    private void StopGlTimer()
    {
        glTimer?.Stop();
        glTimer = null;
    }
}
