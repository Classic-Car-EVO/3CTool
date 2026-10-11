using System.Runtime.CompilerServices;

using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Compute;

/// <summary>
/// Rotationally symmetric, convex cutter profile usable on the GPU. The surface height above the tip at radius r is
/// <c>h(r)</c>: zero on the flat bottom [0, FlatRadius], a circular arc (corner radius) up to <see cref="ArcEndRadius"/>,
/// then a straight cone up to <see cref="Radius"/>. Above <see cref="ProfileHeight"/> the cutter is a cylinder of
/// <see cref="Radius"/> up to <see cref="Height"/>. All six tool types map onto this form.
/// </summary>
public readonly record struct CutterProfile
{
    private const float NoLimit = float.MaxValue;

    private CutterProfile(float radius, float flatRadius, float cornerRadius, float arcEndRadius, float arcEndHeight, float arcEndSlope, float coneSlope, float height)
    {
        Radius = radius;
        FlatRadius = flatRadius;
        CornerRadius = cornerRadius;
        ArcEndRadius = arcEndRadius;
        ArcEndHeight = arcEndHeight;
        ArcEndSlope = arcEndSlope;
        ConeSlope = coneSlope;
        ProfileHeight = arcEndHeight + ((radius - arcEndRadius) * coneSlope);
        Height = MathF.Max(height, ProfileHeight);
    }

    public float Radius { get; }

    public float FlatRadius { get; }

    public float CornerRadius { get; }

    public float ArcEndRadius { get; }

    public float ArcEndHeight { get; }

    /// <summary>tan of the arc's end angle (float.MaxValue for a full quarter arc).</summary>
    public float ArcEndSlope { get; }

    /// <summary>Cone rise per unit radius (0 when there is no cone).</summary>
    public float ConeSlope { get; }

    /// <summary>h(Radius): height of the end of the profile above the tip.</summary>
    public float ProfileHeight { get; }

    /// <summary>Cutting height above the tip (flute length) used for swept volumes.</summary>
    public float Height { get; }

    public static CutterProfile Flat(double diameter, double height = 0)
    {
        var r = Positive(diameter) / 2;
        return new(r, r, 0, r, 0, 0, 0, (float)height);
    }

    public static CutterProfile Ball(double diameter, double height = 0)
    {
        var r = Positive(diameter) / 2;
        return new(r, 0, r, r, r, NoLimit, 0, (float)height);
    }

    public static CutterProfile BullNose(double diameter, double cornerRadius, double height = 0)
    {
        var r = Positive(diameter) / 2;
        var rc = (float)cornerRadius;
        ArgumentOutOfRangeException.ThrowIfNegative(rc, nameof(cornerRadius));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rc, r, nameof(cornerRadius));
        return rc == 0 ? Flat(diameter, height) : new(r, r - rc, rc, r, rc, NoLimit, 0, (float)height);
    }

    /// <summary>V-bit or chamfer mill: cone with <paramref name="includedAngle"/> (degrees) and an optional flat tip.</summary>
    public static CutterProfile Cone(double diameter, double includedAngle, double tipDiameter = 0, double height = 0)
    {
        var r = Positive(diameter) / 2;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(includedAngle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(includedAngle, 180);
        var f = (float)tipDiameter / 2;
        ArgumentOutOfRangeException.ThrowIfNegative(f, nameof(tipDiameter));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(f, r, nameof(tipDiameter));
        var slope = (float)(1 / Math.Tan(includedAngle * Math.PI / 360));
        return new(r, f, 0, f, 0, 0, slope, (float)height);
    }

    /// <summary>Tapered ball: ball of <paramref name="tipDiameter"/> blending tangentially into a cone that opens by <paramref name="taperAngle"/> (degrees per side).</summary>
    public static CutterProfile TaperedBall(double diameter, double tipDiameter, double taperAngle, double height = 0)
    {
        var r = Positive(diameter) / 2;
        var rb = Positive(tipDiameter) / 2;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rb, r, nameof(tipDiameter));
        ArgumentOutOfRangeException.ThrowIfNegative(taperAngle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(taperAngle, 90);
        if (taperAngle < 1e-6 || r - rb < 1e-6f)
        {
            return Ball(2 * rb, height);
        }

        var alpha = taperAngle * Math.PI / 180;
        var slope = (float)(1 / Math.Tan(alpha));
        var arcEndRadius = (float)(rb * Math.Cos(alpha));
        var arcEndHeight = (float)(rb * (1 - Math.Sin(alpha)));
        return new(r, 0, rb, arcEndRadius, arcEndHeight, slope, slope, (float)height);
    }

    public static CutterProfile FromTool(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var height = tool.FluteLength > 0 ? tool.FluteLength : 2 * tool.Diameter;
        return tool.Type switch
        {
            ToolType.FlatEnd => Flat(tool.Diameter, height),
            ToolType.BallEnd => Ball(tool.Diameter, height),
            ToolType.BullNose => BullNose(tool.Diameter, tool.CornerRadius, height),
            ToolType.VBit or ToolType.Chamfer => Cone(tool.Diameter, tool.TipAngle, tool.TipDiameter, height),
            ToolType.TaperedBall => TaperedBall(tool.Diameter, tool.TipDiameter, tool.TaperAngle, height),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool.Type, "Unknown tool type."),
        };
    }

    /// <summary>Same cutter with a different cutting height (e.g. a deposited bead or a holder segment).</summary>
    public CutterProfile WithHeight(double height) =>
        new(Radius, FlatRadius, CornerRadius, ArcEndRadius, ArcEndHeight, ArcEndSlope, ConeSlope, (float)height);

    /// <summary>h(r) for 0 ≤ r ≤ Radius.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float HeightAt(float r)
    {
        if (r <= FlatRadius)
        {
            return 0;
        }

        if (r <= ArcEndRadius)
        {
            var dx = r - FlatRadius;
            return CornerRadius - MathF.Sqrt(MathF.Max((CornerRadius * CornerRadius) - (dx * dx), 0));
        }

        return ArcEndHeight + ((MathF.Min(r, Radius) - ArcEndRadius) * ConeSlope);
    }

    /// <summary>Inverse profile: cutter radius at height y above the tip; -1 below the tip.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float RadiusAt(float y)
    {
        if (y < 0)
        {
            return -1;
        }

        if (y <= ArcEndHeight && CornerRadius > 0)
        {
            var dy = CornerRadius - y;
            return FlatRadius + MathF.Sqrt(MathF.Max((CornerRadius * CornerRadius) - (dy * dy), 0));
        }

        if (y <= ArcEndHeight)
        {
            return FlatRadius;
        }

        if (y < ProfileHeight && ConeSlope > 0)
        {
            return ArcEndRadius + ((y - ArcEndHeight) / ConeSlope);
        }

        return Radius;
    }

    /// <summary>Radius at which the cutter touches a plane tilted by θ (given as tan θ).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ContactRadius(float tanTheta)
    {
        if (tanTheta <= 1e-7f)
        {
            return 0;
        }

        if (CornerRadius > 0 && tanTheta <= ArcEndSlope)
        {
            return FlatRadius + (CornerRadius * tanTheta / MathF.Sqrt(1 + (tanTheta * tanTheta)));
        }

        return ArcEndRadius < Radius && tanTheta <= ConeSlope ? ArcEndRadius : Radius;
    }

    private static float Positive(double value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return (float)value;
    }
}
