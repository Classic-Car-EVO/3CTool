using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ThreeCTool.Rendering;

public enum PreviewRenderer
{
    /// <summary>OpenGL when it initialises, otherwise the 2D canvas.</summary>
    Auto,
    OpenGl,
    Software,
}

/// <summary>
/// Tool path view with touch/mouse navigation: one-finger (left) drag orbits (pans in <see cref="PanMode"/> or with the
/// right/middle button), two fingers pinch-zoom and pan, the wheel zooms. Renders with OpenGL, falling back to 2D.
/// </summary>
public sealed class ToolpathView : Panel
{
    public static readonly StyledProperty<ToolpathScene?> SceneProperty =
        AvaloniaProperty.Register<ToolpathView, ToolpathScene?>(nameof(Scene));

    public static readonly StyledProperty<PreviewRenderer> RendererProperty =
        AvaloniaProperty.Register<ToolpathView, PreviewRenderer>(nameof(Renderer));

    public static readonly StyledProperty<bool> PanModeProperty =
        AvaloniaProperty.Register<ToolpathView, bool>(nameof(PanMode));

    private const float OrbitRadiansPerPixel = 0.008f;
    private static readonly TimeSpan GlTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<IPointer, Point> pointers = [];
    private readonly ToolpathCanvas canvas = new();
    private ToolpathGlView? glView;
    private DispatcherTimer? glTimer;
    private bool panDrag;

    public ToolpathView()
    {
        ClipToBounds = true;
        Background = Avalonia.Media.Brushes.Transparent;
        Children.Add(canvas);
        Focusable = true;
    }

    public ToolpathScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
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

    /// <summary>True while the OpenGL view is showing; false for the 2D canvas.</summary>
    public bool UsingOpenGl => glView is { IsGlReady: true, IsVisible: true };

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
        if (change.Property == SceneProperty)
        {
            canvas.Scene = Scene;
            if (glView is not null)
            {
                glView.Scene = Scene;
            }

            UpdateAspect(Bounds.Size);
        }
        else if (change.Property == RendererProperty && VisualRoot is not null)
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
        if (Scene is not { } scene || !pointers.TryGetValue(e.Pointer, out var last))
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
        if (Scene is { } scene)
        {
            scene.Camera.Zoom(MathF.Pow(0.85f, (float)e.Delta.Y));
            scene.Invalidate();
            e.Handled = true;
        }
    }

    private void UpdateAspect(Size size)
    {
        if (Scene is { } scene && size.Width > 0 && size.Height > 0)
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
            canvas.IsVisible = true;
            return;
        }

        if (glView is null)
        {
            glView = new ToolpathGlView { Scene = Scene, IsHitTestVisible = false };
            glView.GlReady += OnGlReady;
            glView.GlFailed += OnGlFailed;
            Children.Add(glView);
        }

        // The canvas stays visible underneath until GL has drawn its first frame.
        canvas.IsVisible = true;
        if (!glView.IsGlReady && Renderer == PreviewRenderer.Auto)
        {
            glTimer = new DispatcherTimer(GlTimeout, DispatcherPriority.Background, (_, _) =>
            {
                StopGlTimer();
                if (glView is { IsGlReady: false })
                {
                    RemoveGl();
                }
            });
            glTimer.Start();
        }
        else if (glView.IsGlReady)
        {
            canvas.IsVisible = false;
        }
    }

    private void OnGlReady(object? sender, EventArgs e)
    {
        StopGlTimer();
        canvas.IsVisible = false;
    }

    private void OnGlFailed(object? sender, string reason)
    {
        StopGlTimer();
        RemoveGl();
        canvas.IsVisible = true;
    }

    private void RemoveGl()
    {
        if (glView is null)
        {
            return;
        }

        glView.GlReady -= OnGlReady;
        glView.GlFailed -= OnGlFailed;
        Children.Remove(glView);
        glView = null;
    }

    private void StopGlTimer()
    {
        glTimer?.Stop();
        glTimer = null;
    }
}
