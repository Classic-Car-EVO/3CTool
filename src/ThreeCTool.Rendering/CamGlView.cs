using System.Diagnostics.CodeAnalysis;

using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

using Silk.NET.OpenGL;

namespace ThreeCTool.Rendering;

/// <summary>OpenGL (ES 3.0 / 3.3 core) view of a <see cref="CamScene"/>.</summary>
[SuppressMessage("Design", "CA1001", Justification = "GL resources are released in OnOpenGlDeinit, while the context is current.")]
public sealed class CamGlView : OpenGlControlBase, ISceneGlControl
{
    public static readonly StyledProperty<CamScene?> SceneProperty =
        AvaloniaProperty.Register<CamGlView, CamScene?>(nameof(Scene));

    private GL? gl;
    private CamRenderer? renderer;

    public event EventHandler? GlReady;

    public event EventHandler<string>? GlFailed;

    public CamScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public bool IsGlReady => renderer is not null;

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

            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        try
        {
            this.gl = GL.GetApi(gl.GetProcAddress);
            renderer = new CamRenderer(this.gl, GlVersion.Type == GlProfileType.OpenGLES);
            Dispatcher.UIThread.Post(() => GlReady?.Invoke(this, EventArgs.Empty));
        }
        catch (InvalidOperationException ex)
        {
            renderer = null;
            this.gl = null;
            var message = ex.Message;
            Dispatcher.UIThread.Post(() => GlFailed?.Invoke(this, message));
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        renderer?.Dispose();
        renderer = null;
        this.gl = null;
    }

    protected override void OnOpenGlLost()
    {
        base.OnOpenGlLost();
        renderer = null;
        gl = null;
        Dispatcher.UIThread.Post(() => GlFailed?.Invoke(this, "OpenGL context lost"));
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (this.gl is not { } api || renderer is null)
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));
        api.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        api.Viewport(0, 0, (uint)width, (uint)height);
        var background = ToolpathColors.Background;
        api.ClearColor(background.X, background.Y, background.Z, 1f);
        api.DepthMask(true);
        api.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (Scene is { } scene)
        {
            renderer.LineWidth = (float)Math.Max(1, 1.5 * scaling);
            renderer.Draw(scene, width, height);
        }
    }

    private void OnSceneChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RequestNextFrameRendering);
}
