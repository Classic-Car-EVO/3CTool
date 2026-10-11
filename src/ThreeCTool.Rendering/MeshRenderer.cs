using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace ThreeCTool.Rendering;

/// <summary>Draws an indexed triangle mesh (interleaved position + normal) with directional lighting.</summary>
/// <remarks>Shaders use only the OpenGL ES 3.0 feature set so the same code runs on the Pi.</remarks>
public sealed class MeshRenderer : IDisposable
{
    private const string VertexBody = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in vec3 a_normal;
        uniform mat4 u_model;
        uniform mat4 u_viewProjection;
        out vec3 v_normal;
        void main()
        {
            v_normal = mat3(u_model) * a_normal;
            gl_Position = u_viewProjection * u_model * vec4(a_position, 1.0);
        }
        """;

    private const string FragmentBody = """
        in vec3 v_normal;
        uniform vec3 u_color;
        uniform vec3 u_lightDirection;
        out vec4 o_color;
        void main()
        {
            float diffuse = max(dot(normalize(v_normal), -u_lightDirection), 0.0);
            o_color = vec4(u_color * (0.25 + 0.75 * diffuse), 1.0);
        }
        """;

    private const uint FloatsPerVertex = 6;

    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;
    private readonly int _modelLocation;
    private readonly int _viewProjectionLocation;
    private readonly int _colorLocation;
    private readonly int _lightDirectionLocation;
    private uint _indexCount;

    public MeshRenderer(GL gl, bool isGles)
    {
        _gl = gl;
        var header = isGles ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        _program = LinkProgram(header + VertexBody, header + FragmentBody);
        _modelLocation = gl.GetUniformLocation(_program, "u_model");
        _viewProjectionLocation = gl.GetUniformLocation(_program, "u_viewProjection");
        _colorLocation = gl.GetUniformLocation(_program, "u_color");
        _lightDirectionLocation = gl.GetUniformLocation(_program, "u_lightDirection");

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        unsafe
        {
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), (void*)0);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), (void*)(3 * sizeof(float)));
        }

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    public Vector3 Color { get; set; } = new(0.7f, 0.75f, 0.8f);

    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -0.6f, -1f));

    /// <param name="vertices">Interleaved x, y, z, nx, ny, nz per vertex.</param>
    public void SetMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices)
    {
        _gl.BindVertexArray(_vao);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
        _gl.BufferData(BufferTargetARB.ElementArrayBuffer, indices, BufferUsageARB.StaticDraw);
        _gl.BindVertexArray(0);
        _indexCount = (uint)indices.Length;
    }

    public void Draw(Matrix4x4 model, Matrix4x4 viewProjection)
    {
        if (_indexCount == 0)
        {
            return;
        }

        // System.Numerics row-major (v * M) uploaded untransposed equals GLSL column-major (M * v).
        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_modelLocation, 1, false, AsFloats(in model));
        _gl.UniformMatrix4(_viewProjectionLocation, 1, false, AsFloats(in viewProjection));
        _gl.Uniform3(_colorLocation, Color.X, Color.Y, Color.Z);
        var light = Vector3.Normalize(LightDirection);
        _gl.Uniform3(_lightDirectionLocation, light.X, light.Y, light.Z);
        _gl.BindVertexArray(_vao);
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, null);
        }

        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }

    private static ReadOnlySpan<float> AsFloats(in Matrix4x4 m) =>
        MemoryMarshal.Cast<Matrix4x4, float>(new ReadOnlySpan<Matrix4x4>(in m));

    private uint LinkProgram(string vertexSource, string fragmentSource)
    {
        var vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vertex);
        _gl.AttachShader(program, fragment);
        _gl.LinkProgram(program);
        _gl.DeleteShader(vertex);
        _gl.DeleteShader(fragment);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            var log = _gl.GetProgramInfoLog(program);
            _gl.DeleteProgram(program);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        return program;
    }

    private uint CompileShader(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }

        return shader;
    }
}
