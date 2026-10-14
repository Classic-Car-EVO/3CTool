using System.Numerics;

using ThreeCTool.Core.Projects;

namespace ThreeCTool.Toolpaths;

/// <summary>
/// Part frame ↔ WCS of a setup. The WCS at rotary angle A is w = Rx(A)·(p − Origin) for indexed and rotary setups
/// (Origin Y/Z on the rotary axis, positive A turns +Y towards +Z) and w = R·p − Origin for 3-axis setups, where R
/// turns the stock by hand (<see cref="StockTurn.Rotate"/>: <see cref="Face"/> up, then <see cref="Spin"/>) and
/// Origin is in turned coordinates (identity for the top face unspun).
/// </summary>
public sealed record SetupFrame(SetupOrientation Orientation, double OriginX, double OriginY, double OriginZ, double IndexAngle, StockFace Face = StockFace.Top, int Spin = 0)
{
    public static SetupFrame Identity { get; } = new(SetupOrientation.ThreeAxis, 0, 0, 0, 0);

    public P3 Origin => new(OriginX, OriginY, OriginZ);

    /// <summary>A angle of the working frame strategies generate in: the index angle for indexed setups, else 0.</summary>
    public double BaseAngle => Orientation == SetupOrientation.Indexed ? IndexAngle : 0;

    public bool IsRotary => Orientation != SetupOrientation.ThreeAxis;

    /// <summary>3-axis setup with the stock turned by hand (another face up or spun).</summary>
    public bool IsTurned => !IsRotary && (Face != StockFace.Top || Spin != 0);

    public P3 PartToWcs(P3 p, double a) => IsRotary ? RotateX(p - Origin, a) : Turn(p) - Origin;

    public P3 WcsToPart(P3 w, double a) => IsRotary ? RotateX(w, -a) + Origin : Unturn(w + Origin);

    /// <summary>A part-frame direction in the WCS at A.</summary>
    public P3 DirectionToWcs(P3 d, double a) => IsRotary ? RotateX(d, a) : Turn(d);

    /// <summary>Part-frame matrix of <see cref="PartToWcs"/> (System.Numerics row-vector convention).</summary>
    public Matrix4x4 PartToWcsMatrix(double a)
    {
        var translate = Matrix4x4.CreateTranslation((float)-OriginX, (float)-OriginY, (float)-OriginZ);
        if (IsRotary)
        {
            return translate * Matrix4x4.CreateRotationX((float)(a * Math.PI / 180));
        }

        if (!IsTurned)
        {
            return translate;
        }

        var (x, y, z) = (Turn(new P3(1, 0, 0)), Turn(new P3(0, 1, 0)), Turn(new P3(0, 0, 1)));
        var turn = new Matrix4x4(
            (float)x.X, (float)x.Y, (float)x.Z, 0,
            (float)y.X, (float)y.Y, (float)y.Z, 0,
            (float)z.X, (float)z.Y, (float)z.Z, 0,
            0, 0, 0, 1);
        return turn * translate;
    }

    /// <summary>Tool tip and axis (unit, pointing away from the tip) of a move target in the part frame.</summary>
    public (P3 Tip, P3 Axis) ToolInPart(ToolpathMove move)
    {
        var a = IsRotary ? move.A : 0;
        return (WcsToPart(move.Position, a), IsRotary ? RotateX(new P3(0, 0, 1), -a) : Unturn(new P3(0, 0, 1)));
    }

    /// <summary>R·v of a 3-axis frame (identity when the stock is not turned).</summary>
    public P3 Turn(P3 v)
    {
        if (!IsTurned)
        {
            return v;
        }

        var (x, y, z) = StockTurn.Rotate(Face, Spin, (v.X, v.Y, v.Z));
        return new P3(x, y, z);
    }

    /// <summary>Rᵀ·v, the inverse of <see cref="Turn"/>.</summary>
    public P3 Unturn(P3 v)
    {
        if (!IsTurned)
        {
            return v;
        }

        var (x, y, z) = StockTurn.Unrotate(Face, Spin, (v.X, v.Y, v.Z));
        return new P3(x, y, z);
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
