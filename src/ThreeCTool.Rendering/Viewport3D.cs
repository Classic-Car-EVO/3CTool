using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;

namespace ThreeCTool.Rendering;

/// <summary>Avalonia OpenGL viewport; currently renders a rotating cube to validate the GL pipeline.</summary>
[SuppressMessage("Design", "CA1001", Justification = "GL resources are released in OnOpenGlDeinit, while the context is current.")]
public sealed class Viewport3D : OpenGlControlBase
{
    private readonly Stopwatch _clock = new();
    private GL? _gl;
    private MeshRenderer? _mesh;
    private long _frame;

    public string RendererInfo { get; private set; } = string.Empty;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        _mesh = new MeshRenderer(_gl, GlVersion.Type == GlProfileType.OpenGLES);
        var (vertices, indices) = Primitives.Cube(1f);
        _mesh.SetMesh(vertices, indices);

        RendererInfo = $"{GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor} | {_gl.GetStringS(StringName.Version)} | {_gl.GetStringS(StringName.Renderer)}";
        Console.WriteLine($"[Viewport3D] {RendererInfo}");
        _clock.Start();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _mesh?.Dispose();
        _mesh = null;
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || _mesh is null)
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.ClearColor(0.12f, 0.13f, 0.15f, 1f);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var t = (float)_clock.Elapsed.TotalSeconds;
        var model = Matrix4x4.CreateRotationZ(t * 0.8f) * Matrix4x4.CreateRotationX(0.3f);
        var eye = new Vector3(2.2f, -2.8f, 2f);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, (float)width / height, 0.1f, 100f);
        _mesh.LightDirection = Vector3.Normalize(-eye - (0.5f * Vector3.UnitX));
        _mesh.Draw(model, view * projection);

        if (++_frame == 30)
        {
            LogCenterPixel(width, height);
        }

        RequestNextFrameRendering();
    }

    private unsafe void LogCenterPixel(int width, int height)
    {
        var rgba = stackalloc byte[4];
        _gl!.ReadPixels(width / 2, height / 2, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        Console.WriteLine($"[Viewport3D] frame {_frame}: {width}x{height}, centre pixel RGB {rgba[0]},{rgba[1]},{rgba[2]}, GL error {_gl.GetError()}");
    }
}
