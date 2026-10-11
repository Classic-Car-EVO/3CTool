using System.Numerics;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;

namespace ThreeCTool.Geometry;

/// <summary>
/// Converts the editable <see cref="TransformStep"/> history into matrices.
/// System.Numerics uses row vectors (<c>v' = v * M</c>), so <c>A * B</c> applies A first.
/// </summary>
public static class MeshTransforms
{
    public static TransformStep Rotate(Vector3 axis, double angleDegrees) =>
        new() { Kind = TransformKind.Rotate, X = axis.X, Y = axis.Y, Z = axis.Z, AngleDegrees = angleDegrees };

    public static TransformStep Scale(double factor) => new() { Kind = TransformKind.Scale, Factor = factor };

    public static TransformStep Translate(Vector3 offset) =>
        new() { Kind = TransformKind.Translate, X = offset.X, Y = offset.Y, Z = offset.Z };

    public static Matrix4x4 ToMatrix(TransformStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        switch (step.Kind)
        {
            case TransformKind.Rotate:
                var axis = new Vector3((float)step.X, (float)step.Y, (float)step.Z);
                if (axis.LengthSquared() == 0)
                {
                    throw new ArgumentException("Rotation axis must be non-zero.", nameof(step));
                }

                return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), (float)(step.AngleDegrees * Math.PI / 180.0));
            case TransformKind.Scale:
                if (!(step.Factor > 0) || !double.IsFinite(step.Factor))
                {
                    throw new ArgumentException("Scale factor must be positive.", nameof(step));
                }

                return Matrix4x4.CreateScale((float)step.Factor);
            case TransformKind.Translate:
                return Matrix4x4.CreateTranslation((float)step.X, (float)step.Y, (float)step.Z);
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step.Kind, null);
        }
    }

    /// <summary>Import-unit conversion to mm followed by the transform history in order.</summary>
    public static Matrix4x4 Compose(LengthUnit importUnit, IEnumerable<TransformStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var matrix = Matrix4x4.CreateScale((float)Units.MillimetersPer(importUnit));
        foreach (var step in steps)
        {
            matrix *= ToMatrix(step);
        }

        return matrix;
    }

    /// <summary>Rotation steps for 90° snaps about a principal axis.</summary>
    public static TransformStep Snap90(Vector3 axis, int quarterTurns) => Rotate(axis, 90.0 * quarterTurns);
}

/// <summary>
/// A-axis kinematics: rotation about an X-parallel axis through (centerY, centerZ), right-hand rule
/// (positive A turns +Y towards +Z). Part coordinates are the part frame at A = 0.
/// </summary>
public static class RotaryKinematics
{
    public static Matrix4x4 PartToMachine(double aDegrees, double centerY, double centerZ) =>
        Matrix4x4.CreateTranslation(0, (float)-centerY, (float)-centerZ) *
        Matrix4x4.CreateRotationX((float)(aDegrees * Math.PI / 180.0)) *
        Matrix4x4.CreateTranslation(0, (float)centerY, (float)centerZ);

    public static Matrix4x4 MachineToPart(double aDegrees, double centerY, double centerZ) =>
        PartToMachine(-aDegrees, centerY, centerZ);

    /// <summary>Double-precision point rotation for toolpath maths.</summary>
    public static (double Y, double Z) Rotate(double y, double z, double aDegrees, double centerY, double centerZ)
    {
        var radians = aDegrees * Math.PI / 180.0;
        var (sin, cos) = Math.SinCos(radians);
        var dy = y - centerY;
        var dz = z - centerZ;
        return (centerY + (dy * cos) - (dz * sin), centerZ + (dy * sin) + (dz * cos));
    }
}
