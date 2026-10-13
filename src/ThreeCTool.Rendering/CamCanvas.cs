using System.Diagnostics.CodeAnalysis;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ThreeCTool.Rendering;

/// <summary>Software view of a <see cref="CamScene"/> (<see cref="SoftwareRasterizer"/> into a bitmap).</summary>
[SuppressMessage("Design", "CA1001", Justification = "The bitmap is released when the control leaves the visual tree.")]
public sealed class CamCanvas : Control
{
    public static readonly StyledProperty<CamScene?> SceneProperty =
        AvaloniaProperty.Register<CamCanvas, CamScene?>(nameof(Scene));

    private readonly SoftwareRasterizer rasterizer = new();
    private WriteableBitmap? bitmap;
    private bool redrawQueued;

    public CamScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        redrawQueued = false;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = (int)(Bounds.Width * scaling);
        var height = (int)(Bounds.Height * scaling);
        if (Scene is not { } scene || width < 1 || height < 1)
        {
            return;
        }

        rasterizer.Render(scene, width, height);
        if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using (var buffer = bitmap.Lock())
        {
            var source = rasterizer.Pixels;
            for (var y = 0; y < height; y++)
            {
                unsafe
                {
                    var row = new Span<uint>((byte*)buffer.Address + ((long)y * buffer.RowBytes), width);
                    source.Slice(y * width, width).CopyTo(row);
                }
            }
        }

        context.DrawImage(bitmap, new Rect(Bounds.Size));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            if (change.OldValue is CamScene old)
            {
                old.Changed -= OnSceneChanged;
            }

            if (change.NewValue is CamScene scene)
            {
                scene.Changed += OnSceneChanged;
            }

            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        bitmap?.Dispose();
        bitmap = null;
    }

    private void OnSceneChanged(object? sender, EventArgs e)
    {
        // Coalesce bursts of changes (camera drags, playback) into one redraw.
        if (!redrawQueued)
        {
            redrawQueued = true;
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }
}
