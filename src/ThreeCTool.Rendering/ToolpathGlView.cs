using System.Diagnostics.CodeAnalysis;
using System.Numerics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

using Silk.NET.OpenGL;

namespace ThreeCTool.Rendering;

/// <summary>OpenGL (ES 3.0 / 3.3 core) view of a <see cref="ToolpathScene"/>.</summary>
[SuppressMessage("Design", "CA1001", Justification = "GL resources are released in OnOpenGlDeinit, while the context is current.")]
public sealed class ToolpathGlView : OpenGlControlBase, ISceneGlControl
{
    public static readonly StyledProperty<ToolpathScene?> SceneProperty =
        AvaloniaProperty.Register<ToolpathGlView, ToolpathScene?>(nameof(Scene));

    private GL? gl;
    private LineRenderer? lines;
    private int uploadedVersion = -1;
    private ToolpathScene? uploadedScene;

    /// <summary>Raised on the UI thread once GL is running.</summary>
    public event EventHandler? GlReady;

    /// <summary>Raised on the UI thread when GL could not be initialised or was lost.</summary>
    public event EventHandler<string>? GlFailed;

    public ToolpathScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public bool IsGlReady => lines is not null;

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

            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        try
        {
            this.gl = GL.GetApi(gl.GetProcAddress);
            lines = new LineRenderer(this.gl, GlVersion.Type == GlProfileType.OpenGLES);
            uploadedVersion = -1;
            Dispatcher.UIThread.Post(() => GlReady?.Invoke(this, EventArgs.Empty));
        }
        catch (InvalidOperationException ex)
        {
            lines = null;
            this.gl = null;
            var message = ex.Message;
            Dispatcher.UIThread.Post(() => GlFailed?.Invoke(this, message));
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        lines?.Dispose();
        lines = null;
        this.gl = null;
    }

    protected override void OnOpenGlLost()
    {
        base.OnOpenGlLost();
        lines = null;
        gl = null;
        Dispatcher.UIThread.Post(() => GlFailed?.Invoke(this, "OpenGL context lost"));
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (this.gl is not { } api || lines is null)
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
        api.Disable(EnableCap.DepthTest);
        api.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (Scene is not { } scene)
        {
            return;
        }

        if (!ReferenceEquals(uploadedScene, scene) || uploadedVersion != scene.PathVersion)
        {
            lines.SetPath(scene.Path);
            uploadedScene = scene;
            uploadedVersion = scene.PathVersion;
        }

        var viewProjection = scene.Camera.ViewProjection((float)width / height);
        lines.ProgressLine = scene.ProgressLine;
        lines.ShowRapids = scene.ShowRapids;
        lines.LineWidth = (float)Math.Max(1, 1.5 * scaling);
        lines.Draw(viewProjection);
        if (scene.Marker is { } marker)
        {
            lines.DrawMarker(marker, scene.MarkerSize, viewProjection);
        }
    }

    private void OnSceneChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RequestNextFrameRendering);
}

/// <summary>Colours shared by the GL view and the 2D fallback (RGBA 0..1).</summary>
public static class ToolpathColors
{
    public static Vector4 Background { get; } = new(0.11f, 0.12f, 0.14f, 1f);

    public static Vector4 Cut { get; } = new(0.35f, 0.65f, 1f, 1f);

    public static Vector4 Rapid { get; } = new(1f, 0.55f, 0.15f, 0.7f);

    public static Vector4 Done { get; } = new(0.3f, 0.85f, 0.4f, 1f);

    public static Vector4 Marker { get; } = new(1f, 0.25f, 0.25f, 1f);
}
