using System.Numerics;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Toolpaths;

/// <summary>
/// Part frame ↔ WCS of a setup. The WCS at rotary angle A is w = Rx(A)·(p − Origin) for indexed and rotary setups
/// (Origin Y/Z on the rotary axis, positive A turns +Y towards +Z) and w = p − Origin for 3-axis setups.
/// </summary>
public sealed record SetupFrame(SetupOrientation Orientation, double OriginX, double OriginY, double OriginZ, double IndexAngle)
{
    public static SetupFrame Identity { get; } = new(SetupOrientation.ThreeAxis, 0, 0, 0, 0);

    public P3 Origin => new(OriginX, OriginY, OriginZ);

    /// <summary>A angle of the working frame strategies generate in: the index angle for indexed setups, else 0.</summary>
    public double BaseAngle => Orientation == SetupOrientation.Indexed ? IndexAngle : 0;

    public bool IsRotary => Orientation != SetupOrientation.ThreeAxis;

    public P3 PartToWcs(P3 p, double a) => IsRotary ? RotateX(p - Origin, a) : p - Origin;

    public P3 WcsToPart(P3 w, double a) => (IsRotary ? RotateX(w, -a) : w) + Origin;

    /// <summary>Part-frame matrix of <see cref="PartToWcs"/> (System.Numerics row-vector convention).</summary>
    public Matrix4x4 PartToWcsMatrix(double a)
    {
        var translate = Matrix4x4.CreateTranslation((float)-OriginX, (float)-OriginY, (float)-OriginZ);
        return IsRotary ? translate * Matrix4x4.CreateRotationX((float)(a * Math.PI / 180)) : translate;
    }

    /// <summary>Tool tip and axis (unit, pointing away from the tip) of a move target in the part frame.</summary>
    public (P3 Tip, P3 Axis) ToolInPart(ToolpathMove move)
    {
        var a = IsRotary ? move.A : 0;
        return (WcsToPart(move.Position, a), RotateX(new P3(0, 0, 1), -a));
    }

    public static P3 RotateX(P3 v, double degrees)
    {
        if (degrees == 0)
        {
            return v;
        }

        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return new P3(v.X, (v.Y * cos) - (v.Z * sin), (v.Y * sin) + (v.Z * cos));
    }
}
