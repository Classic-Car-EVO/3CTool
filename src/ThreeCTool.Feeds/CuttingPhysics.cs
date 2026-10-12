using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Feeds;

/// <summary>Cutting mechanics used by the engine (mm, N, N/mm², radians).</summary>
public static class CuttingPhysics
{
    /// <summary>Engagement angle of a radial width <paramref name="ae"/> on cutting diameter <paramref name="d"/>.</summary>
    public static double EngagementAngle(double ae, double d) =>
        d <= 0 ? 0 : Math.Acos(1 - (2 * Math.Clamp(ae / d, 0, 1)));

    /// <summary>Maximum chip thickness over feed per tooth from the engagement angle: sin φ below half the diameter.</summary>
    public static double RadialThinning(double ae, double d)
    {
        var phi = EngagementAngle(ae, d);
        return phi < Math.PI / 2 ? Math.Sin(phi) : 1;
    }

    /// <summary>Mean uncut chip thickness over the engagement arc: fz·sin κ·(1 − cos φ)/φ.</summary>
    public static double MeanChipThickness(double fz, double ae, double d, double leadSine)
    {
        var phi = EngagementAngle(ae, d);
        return phi <= 0 ? 0 : fz * leadSine * (1 - Math.Cos(phi)) / phi;
    }

    /// <summary>Kienzle specific cutting force kc = kc1.1·h^−mc (h floored at 1 µm).</summary>
    public static double SpecificCuttingForce(Material material, double chipThickness)
    {
        ArgumentNullException.ThrowIfNull(material);
        return material.SpecificCuttingForce * Math.Pow(Math.Max(chipThickness, 1e-3), -material.KienzleExponent);
    }

    /// <summary>
    /// Diameter that cuts at axial depth <paramref name="ap"/>: the full diameter for flank cuts, otherwise the
    /// contact circle on the ball, corner radius or cone.
    /// </summary>
    public static double EffectiveDiameter(Tool tool, double ap, bool sideCutting)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var d = tool.Diameter;
        ap = Math.Max(ap, 0);
        switch (tool.Type)
        {
            case ToolType.BallEnd when !sideCutting && ap < d / 2:
                return 2 * Math.Sqrt(ap * (d - ap));
            case ToolType.BullNose when !sideCutting:
            {
                var r = Math.Clamp(tool.CornerRadius, 0, d / 2);
                return ap < r ? d - (2 * r) + (2 * Math.Sqrt(ap * ((2 * r) - ap))) : d;
            }

            case ToolType.TaperedBall when !sideCutting:
            {
                var rt = tool.TipDiameter / 2;
                var taper = tool.TaperAngle * Math.PI / 180;
                var ballTop = rt * (1 - Math.Sin(taper));
                return ap <= ballTop
                    ? 2 * Math.Sqrt(ap * ((2 * rt) - ap))
                    : Math.Min(d, 2 * ((rt * Math.Cos(taper)) + ((ap - ballTop) * Math.Tan(taper))));
            }

            case ToolType.VBit or ToolType.Chamfer when tool.TipAngle > 0:
                return Math.Min(d, tool.TipDiameter + (2 * ap * Math.Tan(tool.TipAngle * Math.PI / 360)));
            default:
                return d;
        }
    }

    /// <summary>Sine of the lead angle κ between the cutting edge at the contact and the feed plane (1 for a flank).</summary>
    public static double LeadSine(Tool tool, double ap, bool sideCutting)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var d = tool.Diameter;
        switch (tool.Type)
        {
            case ToolType.BallEnd when !sideCutting && ap < d / 2:
                return Math.Max(EffectiveDiameter(tool, ap, false) / d, 1e-3);
            case ToolType.BullNose when !sideCutting && ap < tool.CornerRadius:
                return Math.Max(Math.Sqrt(ap * ((2 * tool.CornerRadius) - ap)) / tool.CornerRadius, 1e-3);
            case ToolType.TaperedBall:
            {
                var rt = tool.TipDiameter / 2;
                var taper = tool.TaperAngle * Math.PI / 180;
                return !sideCutting && ap < rt * (1 - Math.Sin(taper))
                    ? Math.Max(Math.Sqrt(ap * ((2 * rt) - ap)) / rt, 1e-3)
                    : Math.Cos(taper);
            }

            case ToolType.VBit or ToolType.Chamfer when tool.TipAngle > 0:
                return Math.Cos(tool.TipAngle * Math.PI / 360);
            default:
                return 1;
        }
    }

    /// <summary>
    /// Tip deflection of a stepped cantilever (shank clamped at the collet, fluted section at the free end) under a
    /// tip load: F/(3E)·((L³ − Lf³)/Is + Lf³/If).
    /// </summary>
    public static double TipDeflection(double force, double stickout, double fluteLength, double fluteDiameter, double shankDiameter, double modulus)
    {
        var lf = Math.Min(fluteLength > 0 ? fluteLength : stickout, stickout);
        var shank = Inertia(shankDiameter);
        var flute = Inertia(fluteDiameter);
        return force / (3 * modulus) * ((Cube(stickout) - Cube(lf)) / shank + (Cube(lf) / flute));
    }

    /// <summary>Largest bending stress: at the collet (shank) or where the flutes start.</summary>
    public static double BendingStress(double force, double stickout, double fluteLength, double fluteDiameter, double shankDiameter)
    {
        var lf = Math.Min(fluteLength > 0 ? fluteLength : stickout, stickout);
        return Math.Max(force * stickout / Section(shankDiameter), force * lf / Section(fluteDiameter));
    }

    private static double Inertia(double d) => Math.PI * Math.Pow(d, 4) / 64;

    private static double Section(double d) => Math.PI * d * d * d / 32;

    private static double Cube(double x) => x * x * x;
}
