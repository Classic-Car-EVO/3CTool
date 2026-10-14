using System.Numerics;

using Silk.NET.OpenGL;

namespace ThreeCTool.Rendering;

/// <summary>
/// Draws a <see cref="PolylinePath"/> as line strips, coloured by progress: segments up to
/// <see cref="ProgressLine"/> in <see cref="DoneColor"/>, the rest in <see cref="CutColor"/> or <see cref="RapidColor"/>.
/// Also draws a small axis cross as the tool position marker.
/// </summary>
/// <remarks>OpenGL ES 3.0 feature set only, so the same code runs on the Pi.</remarks>
public sealed class LineRenderer : IDisposable
{
    internal const string VertexBody = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in float a_line;
        uniform mat4 u_viewProjection;
        out float v_line;
        void main()
        {
            v_line = a_line;
            gl_Position = u_viewProjection * vec4(a_position, 1.0);
        }
        """;

    // A segment ending at line L interpolates (L-1, L]: it turns "done" once line L has executed.
    internal const string FragmentBody = """
        in float v_line;
        uniform float u_progress;
        uniform vec4 u_color;
        uniform vec4 u_doneColor;
        out vec4 o_color;
        void main()
        {
            o_color = v_line <= u_progress + 0.001 ? u_doneColor : u_color;
        }
        """;

    private const uint FloatsPerVertex = 4;

    private readonly GL gl;
    private readonly uint program;
    private readonly uint vao;
    private readonly uint vbo;
    private readonly uint markerVao;
    private readonly uint markerVbo;
    private readonly int viewProjectionLocation;
    private readonly int progressLocation;
    private readonly int colorLocation;
    private readonly int doneColorLocation;
    private PolylinePath path = PolylinePath.Empty;

    public LineRenderer(GL gl, bool isGles)
    {
        ArgumentNullException.ThrowIfNull(gl);
        this.gl = gl;
        program = GlProgram.Link(gl, isGles, VertexBody, FragmentBody);
        viewProjectionLocation = gl.GetUniformLocation(program, "u_viewProjection");
        progressLocation = gl.GetUniformLocation(program, "u_progress");
        colorLocation = gl.GetUniformLocation(program, "u_color");
        doneColorLocation = gl.GetUniformLocation(program, "u_doneColor");
        (vao, vbo) = CreateBuffer();
        (markerVao, markerVbo) = CreateBuffer();
    }

    /// <summary>Last executed G-code line; 0 = nothing done yet.</summary>
    public int ProgressLine { get; set; }

    public bool ShowRapids { get; set; } = true;

    public Vector4 CutColor { get; set; } = ToolpathColors.Cut;

    public Vector4 RapidColor { get; set; } = ToolpathColors.Rapid;

    public Vector4 DoneColor { get; set; } = ToolpathColors.Done;

    public Vector4 MarkerColor { get; set; } = ToolpathColors.Marker;

    public float LineWidth { get; set; } = 1.5f;

    public void SetPath(PolylinePath value)
    {
        ArgumentNullException.ThrowIfNull(value);
        path = value;
        var data = new float[value.VertexCount * FloatsPerVertex];
        for (var i = 0; i < value.VertexCount; i++)
        {
            data[(i * 4) + 0] = value.Positions[3 * i];
            data[(i * 4) + 1] = value.Positions[(3 * i) + 1];
            data[(i * 4) + 2] = value.Positions[(3 * i) + 2];
            data[(i * 4) + 3] = value.Lines[i];
        }

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    public void Draw(Matrix4x4 viewProjection)
    {
        if (path.VertexCount == 0)
        {
            return;
        }

        Begin(viewProjection, ProgressLine);
        SetColor(doneColorLocation, DoneColor);
        gl.BindVertexArray(vao);
        for (var i = 0; i < path.StripStarts.Length; i++)
        {
            var rapid = path.StripRapid[i];
            if (rapid && !ShowRapids)
            {
                continue;
            }

            var (start, count) = path.Strip(i);
            if (count < 2)
            {
                continue;
            }

            SetColor(colorLocation, rapid ? RapidColor : CutColor);
            gl.DrawArrays(PrimitiveType.LineStrip, start, (uint)count);
        }

        End();
    }

    /// <summary>Draws an axis cross of half-size <paramref name="size"/> at <paramref name="position"/>.</summary>
    public void DrawMarker(Vector3 position, float size, Matrix4x4 viewProjection)
    {
        Span<float> data = stackalloc float[6 * (int)FloatsPerVertex];
        for (var axis = 0; axis < 3; axis++)
        {
            var offset = Vector3.Zero;
            offset[axis] = size;
            Write(data[(axis * 8)..], position - offset);
            Write(data[((axis * 8) + 4)..], position + offset);
        }

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StreamDraw);
        Begin(viewProjection, int.MinValue);
        SetColor(colorLocation, MarkerColor);
        gl.BindVertexArray(markerVao);
        gl.DrawArrays(PrimitiveType.Lines, 0, 6);
        End();
    }

    public void Dispose()
    {
        gl.DeleteBuffer(markerVbo);
        gl.DeleteVertexArray(markerVao);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(program);
    }

    private static void Write(Span<float> target, Vector3 p)
    {
        target[0] = p.X;
        target[1] = p.Y;
        target[2] = p.Z;
        target[3] = 0;
    }

    private void SetColor(int location, Vector4 color) => gl.Uniform4(location, color.X, color.Y, color.Z, color.W);

    private (uint Vao, uint Vbo) CreateBuffer()
    {
        var array = gl.GenVertexArray();
        var buffer = gl.GenBuffer();
        gl.BindVertexArray(array);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), (void*)0);
            gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), (void*)(3 * sizeof(float)));
        }

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        return (array, buffer);
    }

    private void Begin(Matrix4x4 viewProjection, int progress)
    {
        gl.UseProgram(program);
        gl.UniformMatrix4(viewProjectionLocation, 1, false, GlProgram.AsFloats(in viewProjection));
        gl.Uniform1(progressLocation, (float)progress);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.LineWidth(LineWidth);
    }

    private void End()
    {
        gl.BindVertexArray(0);
        gl.Disable(EnableCap.Blend);
    }
}
