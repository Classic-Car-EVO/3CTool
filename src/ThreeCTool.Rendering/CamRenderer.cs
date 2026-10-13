using System.Numerics;

using Silk.NET.OpenGL;

namespace ThreeCTool.Rendering;

/// <summary>
/// OpenGL renderer of a <see cref="CamScene"/>: opaque meshes, simulation points, tool paths, then translucent meshes
/// and the tool back to front. GPU buffers are kept per scene item and only rebuilt for new items.
/// </summary>
/// <remarks>OpenGL ES 3.0 feature set only, so the same code runs on the Pi.</remarks>
public sealed class CamRenderer : IDisposable
{
    private const string MeshVertex = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in vec3 a_normal;
        uniform mat4 u_viewProjection;
        out vec3 v_normal;
        void main()
        {
            v_normal = a_normal;
            gl_Position = u_viewProjection * vec4(a_position, 1.0);
        }
        """;

    private const string MeshFragment = """
        in vec3 v_normal;
        uniform vec4 u_color;
        uniform vec3 u_toEye;
        out vec4 o_color;
        void main()
        {
            vec3 n = length(v_normal) > 0.0 ? normalize(v_normal) : vec3(0.0);
            float light = 0.3 + 0.7 * abs(dot(n, u_toEye));
            o_color = vec4(u_color.rgb * light, u_color.a);
        }
        """;

    private const string PointVertex = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in vec4 a_color;
        uniform mat4 u_viewProjection;
        uniform float u_size;
        out vec4 v_color;
        void main()
        {
            v_color = a_color;
            gl_Position = u_viewProjection * vec4(a_position, 1.0);
            gl_PointSize = clamp(u_size / max(gl_Position.w, 1e-6), 1.0, 32.0);
        }
        """;

    private const string PointFragment = """
        in vec4 v_color;
        out vec4 o_color;
        void main()
        {
            o_color = v_color;
        }
        """;

    private readonly GL gl;
    private readonly bool isGles;
    private readonly uint meshProgram;
    private readonly uint pointProgram;
    private readonly uint lineProgram;
    private readonly Dictionary<object, Buffer> buffers = new(ReferenceEqualityComparer.Instance);
    private Buffer? toolBuffer;
    private ToolMarker? toolPose;
    private CamScene? uploadedScene;
    private int uploadedVersion = -1;

    public CamRenderer(GL gl, bool isGles)
    {
        ArgumentNullException.ThrowIfNull(gl);
        this.gl = gl;
        this.isGles = isGles;
        meshProgram = GlProgram.Link(gl, isGles, MeshVertex, MeshFragment);
        try
        {
            pointProgram = GlProgram.Link(gl, isGles, PointVertex, PointFragment);
            lineProgram = GlProgram.Link(gl, isGles, LineRenderer.VertexBody, LineRenderer.FragmentBody);
        }
        catch
        {
            gl.DeleteProgram(meshProgram);
            if (pointProgram != 0)
            {
                gl.DeleteProgram(pointProgram);
            }

            throw;
        }
    }

    public float LineWidth { get; set; } = 1.5f;

    public void Draw(CamScene scene, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Sync(scene);
        var camera = scene.Camera;
        var viewProjection = camera.ViewProjection((float)width / height);
        var toEye = camera.Direction;
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthMask(true);

        // Opaque meshes are pushed back slightly so tool paths on their surface stay visible.
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1, 1);
        foreach (var mesh in scene.Meshes.Where(m => m.Color.W >= 0.999f && scene.IsVisible(m.Layer)))
        {
            DrawMesh(buffers[mesh], mesh.Color, viewProjection, toEye);
        }

        gl.Disable(EnableCap.PolygonOffsetFill);
        DrawPoints(scene, viewProjection, height);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        DrawToolpaths(scene, viewProjection);

        // Translucent surfaces last, far to near, without hiding each other.
        gl.DepthMask(false);
        var eye = camera.Eye;
        var translucent = scene.Meshes
            .Where(m => m.Color.W < 0.999f && scene.IsVisible(m.Layer))
            .Select(m => (Buffer: buffers[m], m.Color, Distance: Vector3.Distance(eye, m.Mesh.Bounds.Center)))
            .ToList();
        if (scene.Tool is { } pose && scene.IsVisible(SceneLayer.Tool))
        {
            translucent.Add((ToolBuffer(pose), SceneColors.Tool, Vector3.Distance(eye, pose.Tip)));
        }

        foreach (var (buffer, color, _) in translucent.OrderByDescending(t => t.Distance))
        {
            DrawMesh(buffer, color, viewProjection, toEye);
        }

        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        foreach (var buffer in buffers.Values)
        {
            Delete(buffer);
        }

        buffers.Clear();
        if (toolBuffer is { } tool)
        {
            Delete(tool);
        }

        gl.DeleteProgram(lineProgram);
        gl.DeleteProgram(pointProgram);
        gl.DeleteProgram(meshProgram);
    }

    private void Sync(CamScene scene)
    {
        if (ReferenceEquals(uploadedScene, scene) && uploadedVersion == scene.Version)
        {
            return;
        }

        var items = new HashSet<object>(ReferenceEqualityComparer.Instance);
        items.UnionWith(scene.Meshes);
        items.UnionWith(scene.Points);
        items.UnionWith(scene.Toolpaths);
        foreach (var stale in buffers.Keys.Where(k => !items.Contains(k)).ToList())
        {
            Delete(buffers[stale]);
            buffers.Remove(stale);
        }

        foreach (var mesh in scene.Meshes.Where(m => !buffers.ContainsKey(m)))
        {
            buffers[mesh] = Upload(SceneGeometry.FlatVertices(mesh.Mesh), [3, 3]);
        }

        foreach (var cloud in scene.Points.Where(p => !buffers.ContainsKey(p)))
        {
            var data = new float[cloud.Positions.Length * 7];
            for (var i = 0; i < cloud.Positions.Length; i++)
            {
                var (p, c) = (cloud.Positions[i], cloud.Colors[i]);
                p.CopyTo(data, i * 7);
                c.CopyTo(data, (i * 7) + 3);
            }

            buffers[cloud] = Upload(data, [3, 4]);
        }

        foreach (var toolpath in scene.Toolpaths.Where(t => !buffers.ContainsKey(t)))
        {
            var path = toolpath.Path;
            var data = new float[path.VertexCount * 4];
            for (var i = 0; i < path.VertexCount; i++)
            {
                Array.Copy(path.Positions, 3 * i, data, 4 * i, 3);
                data[(4 * i) + 3] = path.Lines[i];
            }

            buffers[toolpath] = Upload(data, [3, 1]);
        }

        uploadedScene = scene;
        uploadedVersion = scene.Version;
    }

    private Buffer ToolBuffer(ToolMarker pose)
    {
        if (toolBuffer is not { } buffer || toolPose != pose)
        {
            if (toolBuffer is { } old)
            {
                Delete(old);
            }

            buffer = Upload(SceneGeometry.FlatVertices(SceneGeometry.ToolMesh(pose)), [3, 3]);
            toolBuffer = buffer;
            toolPose = pose;
        }

        return buffer;
    }

    private void DrawMesh(Buffer buffer, Vector4 color, Matrix4x4 viewProjection, Vector3 toEye)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        gl.UseProgram(meshProgram);
        gl.UniformMatrix4(gl.GetUniformLocation(meshProgram, "u_viewProjection"), 1, false, GlProgram.AsFloats(in viewProjection));
        gl.Uniform4(gl.GetUniformLocation(meshProgram, "u_color"), color.X, color.Y, color.Z, color.W);
        gl.Uniform3(gl.GetUniformLocation(meshProgram, "u_toEye"), toEye.X, toEye.Y, toEye.Z);
        if (color.W < 0.999f)
        {
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }

        gl.BindVertexArray(buffer.Vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)buffer.Count);
    }

    private void DrawPoints(CamScene scene, Matrix4x4 viewProjection, int height)
    {
        if (!isGles)
        {
            gl.Enable(EnableCap.ProgramPointSize);
        }

        var scale = height / 2f / MathF.Tan(scene.Camera.FieldOfView / 2);
        gl.UseProgram(pointProgram);
        gl.UniformMatrix4(gl.GetUniformLocation(pointProgram, "u_viewProjection"), 1, false, GlProgram.AsFloats(in viewProjection));
        foreach (var cloud in scene.Points.Where(p => scene.IsVisible(p.Layer)))
        {
            var buffer = buffers[cloud];
            gl.Uniform1(gl.GetUniformLocation(pointProgram, "u_size"), cloud.Size * scale);
            gl.BindVertexArray(buffer.Vao);
            gl.DrawArrays(PrimitiveType.Points, 0, (uint)buffer.Count);
        }
    }

    private void DrawToolpaths(CamScene scene, Matrix4x4 viewProjection)
    {
        if (!scene.IsVisible(SceneLayer.Toolpath))
        {
            return;
        }

        gl.UseProgram(lineProgram);
        gl.UniformMatrix4(gl.GetUniformLocation(lineProgram, "u_viewProjection"), 1, false, GlProgram.AsFloats(in viewProjection));
        gl.LineWidth(LineWidth);
        var progressLocation = gl.GetUniformLocation(lineProgram, "u_progress");
        var colorLocation = gl.GetUniformLocation(lineProgram, "u_color");
        var doneLocation = gl.GetUniformLocation(lineProgram, "u_doneColor");
        for (var index = 0; index < scene.Toolpaths.Count; index++)
        {
            var toolpath = scene.Toolpaths[index];
            var path = toolpath.Path;
            var progress = scene.ProgressLine(index);
            gl.Uniform1(progressLocation, progress == int.MaxValue ? float.MaxValue : progress);
            var cut = scene.ToolpathColor(index);
            var rapid = scene.RapidColor(index);
            var done = ToolpathColors.Done with { W = cut.W };
            gl.Uniform4(doneLocation, done.X, done.Y, done.Z, done.W);
            gl.BindVertexArray(buffers[toolpath].Vao);
            for (var strip = 0; strip < path.StripStarts.Length; strip++)
            {
                var isRapid = path.StripRapid[strip];
                var (start, count) = path.Strip(strip);
                if ((isRapid && !scene.ShowRapids) || count < 2)
                {
                    continue;
                }

                var color = isRapid ? rapid : cut;
                gl.Uniform4(colorLocation, color.X, color.Y, color.Z, color.W);
                gl.DrawArrays(PrimitiveType.LineStrip, start, (uint)count);
            }
        }
    }

    private Buffer Upload(float[] data, int[] attributes)
    {
        var stride = attributes.Sum();
        var vao = gl.GenVertexArray();
        var vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);
        var offset = 0;
        for (var i = 0; i < attributes.Length; i++)
        {
            unsafe
            {
                gl.VertexAttribPointer((uint)i, attributes[i], VertexAttribPointerType.Float, false, (uint)(stride * sizeof(float)), (void*)(offset * sizeof(float)));
            }

            gl.EnableVertexAttribArray((uint)i);
            offset += attributes[i];
        }

        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        return new Buffer(vao, vbo, data.Length / stride);
    }

    private void Delete(Buffer buffer)
    {
        gl.DeleteBuffer(buffer.Vbo);
        gl.DeleteVertexArray(buffer.Vao);
    }

    private readonly record struct Buffer(uint Vao, uint Vbo, int Count);
}
