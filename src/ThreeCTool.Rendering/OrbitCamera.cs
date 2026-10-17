using System.Numerics;

namespace ThreeCTool.Rendering;

/// <summary>Standard views for touch buttons.</summary>
public enum CameraView
{
    Iso,
    Top,
    Front,
    Right,
}

/// <summary>
/// Z-up orbit camera around a target: yaw about Z, pitch above the XY plane, perspective projection.
/// Used for the GL views and for the 2D fallback (<see cref="Project"/>), so both show the same picture.
/// </summary>
public sealed class OrbitCamera
{
    private const float MaxPitch = (MathF.PI / 2) - 0.001f;

    private float distance = 100;
    private float pitch = 0.6f;

    public Vector3 Target { get; set; }

    /// <summary>Rotation of the eye about the Z axis through the target, radians (0 = looking from +X).</summary>
    public float Yaw { get; set; } = -MathF.PI / 3;

    /// <summary>Elevation of the eye above the XY plane, radians; limited short of straight up/down.</summary>
    public float Pitch
    {
        get => pitch;
        set => pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    public float Distance
    {
        get => distance;
        set => distance = Math.Clamp(value, MinDistance, MaxDistance);
    }

    public float MinDistance { get; set; } = 1;

    public float MaxDistance { get; set; } = 100_000;

    /// <summary>Vertical field of view, radians.</summary>
    public float FieldOfView { get; set; } = MathF.PI / 4;

    public Vector3 Eye => Target + (Distance * Direction);

    /// <summary>Unit vector from the target to the eye.</summary>
    public Vector3 Direction => new(MathF.Cos(Pitch) * MathF.Cos(Yaw), MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch));

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitZ);

    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect > 0 ? aspect : 1, Distance * 0.01f, Distance * 100);

    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);

    public void CopyFrom(OrbitCamera other)
    {
        ArgumentNullException.ThrowIfNull(other);
        (MinDistance, MaxDistance) = (other.MinDistance, other.MaxDistance);
        (Target, Yaw, Pitch, Distance, FieldOfView) = (other.Target, other.Yaw, other.Pitch, other.Distance, other.FieldOfView);
    }

    public void Orbit(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch += deltaPitch;
    }

    /// <summary>Moves the eye toward (<paramref name="factor"/> &lt; 1) or away from the target.</summary>
    public void Zoom(float factor)
    {
        if (factor > 0 && float.IsFinite(factor))
        {
            Distance *= factor;
        }
    }

    /// <summary>Drags the scene by a screen offset in pixels (content follows the finger).</summary>
    public void Pan(float deltaX, float deltaY, float viewportHeight)
    {
        if (viewportHeight <= 0)
        {
            return;
        }

        var worldPerPixel = 2 * Distance * MathF.Tan(FieldOfView / 2) / viewportHeight;
        var forward = -Direction;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);
        Target += ((-deltaX * right) + (deltaY * up)) * worldPerPixel;
    }

    public void SetView(CameraView view)
    {
        (Yaw, Pitch) = view switch
        {
            CameraView.Top => (-MathF.PI / 2, MaxPitch),
            CameraView.Front => (-MathF.PI / 2, 0f),
            CameraView.Right => (0f, 0f),
            _ => (-MathF.PI / 3, 0.6f),
        };
    }

    /// <summary>Centres the box and moves back until its bounding sphere fits the view.</summary>
    public void Fit(Vector3 min, Vector3 max, float aspect)
    {
        Target = (min + max) / 2;
        var radius = Math.Max(Vector3.Distance(min, max) / 2, MinDistance / 2);
        var horizontal = 2 * MathF.Atan(MathF.Tan(FieldOfView / 2) * (aspect > 0 ? aspect : 1));
        var limiting = Math.Min(FieldOfView, horizontal);
        Distance = radius / MathF.Sin(limiting / 2) * 1.05f;
    }

    /// <summary>Projects a point to pixel coordinates (origin top left), or null when it is behind the eye.</summary>
    public static Vector2? Project(Vector3 point, Matrix4x4 viewProjection, float width, float height)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), viewProjection);
        if (clip.W <= 1e-6f)
        {
            return null;
        }

        return new Vector2(((clip.X / clip.W) + 1) * 0.5f * width, (1 - (clip.Y / clip.W)) * 0.5f * height);
    }
}
