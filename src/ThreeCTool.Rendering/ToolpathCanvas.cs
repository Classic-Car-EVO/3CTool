using System.Numerics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace ThreeCTool.Rendering;

/// <summary>2D (Avalonia drawing) view of a <see cref="ToolpathScene"/> for displays without usable OpenGL.</summary>
public sealed class ToolpathCanvas : Control
{
    public static readonly StyledProperty<ToolpathScene?> SceneProperty =
        AvaloniaProperty.Register<ToolpathCanvas, ToolpathScene?>(nameof(Scene));

    private static readonly IBrush BackgroundBrush = Brush(ToolpathColors.Background);
    private static readonly IPen DonePen = new ImmutablePen(Brush(ToolpathColors.Done), 1.5);
    private static readonly IPen CutPen = new ImmutablePen(Brush(ToolpathColors.Cut), 1.5);
    private static readonly IPen RapidPen = new ImmutablePen(Brush(ToolpathColors.Rapid), 1, new ImmutableDashStyle([3, 3], 0));
    private static readonly IPen MarkerPen = new ImmutablePen(Brush(ToolpathColors.Marker), 2);

    private bool redrawQueued;

    public ToolpathScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        redrawQueued = false;
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);
        if (Scene is not { } scene || bounds.Width < 1 || bounds.Height < 1)
        {
            return;
        }

        using var clip = context.PushClip(bounds);
        var width = (float)bounds.Width;
        var height = (float)bounds.Height;
        foreach (var polyline in ToolpathProjection.Project(scene, width, height))
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ToPoint(polyline.Points[0]), false);
                for (var i = 1; i < polyline.Points.Count; i++)
                {
                    g.LineTo(ToPoint(polyline.Points[i]));
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, polyline.Style switch
            {
                PathStyle.Done => DonePen,
                PathStyle.Rapid => RapidPen,
                _ => CutPen,
            }, geometry);
        }

        if (scene.Marker is { } marker)
        {
            var viewProjection = scene.Camera.ViewProjection(width / height);
            for (var axis = 0; axis < 3; axis++)
            {
                var offset = Vector3.Zero;
                offset[axis] = scene.MarkerSize;
                if (OrbitCamera.Project(marker - offset, viewProjection, width, height) is { } a
                    && OrbitCamera.Project(marker + offset, viewProjection, width, height) is { } b)
                {
                    context.DrawLine(MarkerPen, ToPoint(a), ToPoint(b));
                }
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            if (change.OldValue is ToolpathScene old)
            {
                old.Changed -= OnSceneChanged;
            }

            if (change.NewValue is ToolpathScene scene)
            {
                scene.Changed += OnSceneChanged;
            }

            InvalidateVisual();
        }
    }

    private static Point ToPoint(Vector2 p) => new(p.X, p.Y);

    private static ImmutableSolidColorBrush Brush(Vector4 c) =>
        new(Color.FromArgb((byte)(c.W * 255), (byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)));

    private void OnSceneChanged(object? sender, EventArgs e)
    {
        // Coalesce bursts of status updates into one redraw.
        if (!redrawQueued)
        {
            redrawQueued = true;
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }
}
