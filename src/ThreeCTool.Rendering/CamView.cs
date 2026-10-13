using Avalonia;
using Avalonia.Controls;

namespace ThreeCTool.Rendering;

/// <summary>CAM workspace 3D view: OpenGL with the software rasterizer as fallback.</summary>
public sealed class CamView : SceneViewBase
{
    public static readonly StyledProperty<CamScene?> SceneProperty =
        AvaloniaProperty.Register<CamView, CamScene?>(nameof(Scene));

    private readonly CamCanvas canvas;

    public CamView()
        : this(new CamCanvas())
    {
    }

    private CamView(CamCanvas canvas)
        : base(canvas)
    {
        this.canvas = canvas;
    }

    public CamScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    protected override IOrbitScene? OrbitScene => Scene;

    protected override Control CreateGlView() => new CamGlView { Scene = Scene };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            canvas.Scene = Scene;
            if (GlView is CamGlView gl)
            {
                gl.Scene = Scene;
            }

            OnSceneReplaced();
        }
    }
}
