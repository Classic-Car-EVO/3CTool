using System.Numerics;
using System.Runtime.InteropServices;

using Silk.NET.OpenGL;

namespace ThreeCTool.Rendering;

/// <summary>Shader compilation shared by the renderers (GLSL ES 3.00 or GLSL 3.30 core from the same body).</summary>
internal static class GlProgram
{
    public static string Header(bool isGles) => isGles ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";

    public static uint Link(GL gl, bool isGles, string vertexBody, string fragmentBody)
    {
        var header = Header(isGles);
        var vertex = Compile(gl, ShaderType.VertexShader, header + vertexBody);
        uint fragment;
        try
        {
            fragment = Compile(gl, ShaderType.FragmentShader, header + fragmentBody);
        }
        catch
        {
            gl.DeleteShader(vertex);
            throw;
        }

        var program = gl.CreateProgram();
        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);
        gl.LinkProgram(program);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            var log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        return program;
    }

    /// <summary>System.Numerics row-major (v * M) uploaded untransposed equals GLSL column-major (M * v).</summary>
    public static ReadOnlySpan<float> AsFloats(in Matrix4x4 m) =>
        MemoryMarshal.Cast<Matrix4x4, float>(new ReadOnlySpan<Matrix4x4>(in m));

    private static uint Compile(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            var log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }

        return shader;
    }
}
