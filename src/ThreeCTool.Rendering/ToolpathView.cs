using Avalonia;
using Avalonia.Controls;

namespace ThreeCTool.Rendering;

/// <summary>G-code tool path view (feeder kiosk): OpenGL with a 2D canvas fallback.</summary>
public sealed class ToolpathView : SceneViewBase
{
    public static readonly StyledProperty<ToolpathScene?> SceneProperty =
        AvaloniaProperty.Register<ToolpathView, ToolpathScene?>(nameof(Scene));

    private readonly ToolpathCanvas canvas;

    public ToolpathView()
        : this(new ToolpathCanvas())
    {
    }

    private ToolpathView(ToolpathCanvas canvas)
        : base(canvas)
    {
        this.canvas = canvas;
    }

    public ToolpathScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    protected override IOrbitScene? OrbitScene => Scene;

    protected override Control CreateGlView() => new ToolpathGlView { Scene = Scene };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty)
        {
            canvas.Scene = Scene;
            if (GlView is ToolpathGlView gl)
            {
                gl.Scene = Scene;
            }

            OnSceneReplaced();
        }
    }
}
