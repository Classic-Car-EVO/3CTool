using System.Globalization;

using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Feeds;

/// <summary>
/// Feeds and speeds: chip load from the material table (goal, learned correction, stickout and chip thinning),
/// capped by spindle power, tool deflection, tool bending strength and the machine feed rate; one RPM per tool group
/// chosen by the optimization goal.
/// </summary>
public sealed class FeedsEngine
{
    /// <summary>Weight of the squared surface speed overshoot (share of the range) when no RPM keeps every operation in range.</summary>
    private const double OutOfRangeWeight = 100;

    private readonly FeedContext _context;
    private readonly FeedsOptions _options;

    public FeedsEngine(FeedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _options = context.Options;
    }

    public FeedContext Context => _context;

    /// <summary>
    /// Picks the RPM for all <paramref name="requests"/> (same tool) and computes their feeds; a given
    /// <paramref name="rpm"/> skips the optimization.
    /// </summary>
    public ToolGroupPlan Plan(IReadOnlyList<FeedRequest> requests, double? rpm = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("No operations to plan.", nameof(requests));
        }

        if (requests.Any(r => r.Tool.Id != requests[0].Tool.Id))
        {
            throw new ArgumentException("All operations of a group must use the same tool.", nameof(requests));
        }

        var engagements = requests.Select(NominalEngagement).ToArray();
        var chosen = rpm ?? ChooseRpm(requests, engagements);
        var results = new List<FeedResult>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            var evaluation = Evaluate(requests[i], chosen, engagements[i]);
            results.Add(new FeedResult(requests[i], evaluation, Warnings(requests[i], evaluation), new EngagementLimiter(this, requests[i], evaluation)));
        }

        return new ToolGroupPlan(chosen, results);
    }

    /// <summary>Engagement the strategy cuts with in the bulk of its moves.</summary>
    public Engagement NominalEngagement(FeedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var d = request.Tool.Diameter;
        var incoming = request.IncomingStock ?? _context.Tolerances.RoughingStockToLeave;
        var finish = Math.Max(incoming - request.RadialStock, _options.MinEngagement);
        var stepover = Math.Clamp(request.Stepover, _options.MinEngagement, d);
        return request.Strategy switch
        {
            _ when FeedRequest.IsRoughing(request.Strategy) => new Engagement(Math.Max(request.Stepdown, _options.MinEngagement), stepover),
            Core.Projects.StrategyType.Waterline => new Engagement(Math.Max(request.Stepdown, _options.MinEngagement), Math.Min(finish, d)),
            Core.Projects.StrategyType.Pencil => new Engagement(finish, d / 2),
            _ => new Engagement(finish, stepover),
        };
    }

    /// <summary>Cutting state at <paramref name="rpm"/> with the given engagement, all limits applied.</summary>
    public FeedEvaluation Evaluate(FeedRequest request, double rpm, Engagement engagement)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tool = request.Tool;
        var side = FeedRequest.IsSideCutting(request.Strategy);
        var ap = Math.Max(engagement.AxialDepth, 1e-6);
        var ae = Math.Max(engagement.RadialWidth, 1e-6);
        var deff = Math.Max(CuttingPhysics.EffectiveDiameter(tool, ap, side), 1e-3);
        var lead = CuttingPhysics.LeadSine(tool, ap, side);
        var teeth = Math.Max(tool.FluteCount, 1);
        var cutting = Cutting(tool);
        var correction = CorrectionFor(request);
        var goal = FeedsOptions.ProfileFor(_context.Goal);

        var vcMin = cutting.SurfaceSpeedMin * correction.SurfaceSpeed;
        var vcMax = cutting.SurfaceSpeedMax * correction.SurfaceSpeed;
        var target = cutting.ChipLoad * goal.ChipLoadFactor * correction.ChipLoad * StickoutFactor(tool);
        var thinning = CuttingPhysics.RadialThinning(ae, deff) * lead;
        var fzTarget = target * Math.Min(_options.MaxChipThinning, 1 / Math.Max(thinning, 1e-6));

        var feed = fzTarget * teeth * rpm;
        var limit = FeedLimit.ChipLoad;
        var available = _context.Machine.Spindle.PowerAt(rpm) * _options.PowerUtilization;
        var allowedDeflection = AllowedDeflection(request);
        var machineFeed = MachineFeed();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var loads = Loads(request, rpm, feed, ap, ae, deff, lead);
            var exponent = 1 / Math.Max(1 - _context.Material.KienzleExponent, 0.1);
            var scale = 1.0;
            var limitHere = FeedLimit.ChipLoad;
            Consider(ref scale, ref limitHere, loads.Power > available ? Math.Pow(available / loads.Power, exponent) : 1, FeedLimit.Power);
            if (!double.IsNaN(loads.Deflection) && loads.Deflection > allowedDeflection)
            {
                Consider(ref scale, ref limitHere, Math.Pow(allowedDeflection / loads.Deflection, exponent), FeedLimit.Deflection);
            }

            var stress = _options.BendingStressOf(tool.Material);
            if (loads.Stress > stress)
            {
                Consider(ref scale, ref limitHere, Math.Pow(stress / loads.Stress, exponent), FeedLimit.ToolStrength);
            }

            if (feed > machineFeed)
            {
                Consider(ref scale, ref limitHere, machineFeed / feed, FeedLimit.MachineFeed);
            }

            if (scale >= 1 - 1e-9)
            {
                break;
            }

            feed *= scale;
            limit = limitHere;
        }

        var final = Loads(request, rpm, feed, ap, ae, deff, lead);
        var fz = feed / (teeth * rpm);
        return new FeedEvaluation
        {
            Rpm = rpm,
            Engagement = new Engagement(ap, ae),
            EffectiveDiameter = deff,
            FeedRate = feed,
            PlungeRate = Math.Min(feed * _options.PlungeFeedShare, AxisRate(AxisName.Z)),
            ChipLoad = fz,
            TargetChipLoad = fzTarget,
            MaxChipThickness = fz * thinning,
            TableChipLoad = cutting.ChipLoad,
            SurfaceSpeed = Math.PI * deff * rpm / 1000,
            SurfaceSpeedMin = vcMin,
            SurfaceSpeedMax = vcMax,
            SurfaceSpeedTarget = vcMin + (goal.SurfaceSpeedPosition * (vcMax - vcMin)),
            PowerW = final.Power,
            AvailablePowerW = available,
            Force = final.Force,
            Deflection = final.Deflection,
            AllowedDeflection = allowedDeflection,
            BendingStress = final.Stress,
            MaterialRemovalRate = ap * ae * feed,
            LimitedBy = limit,
        };
    }

    // ---- RPM choice ----

    private double ChooseRpm(IReadOnlyList<FeedRequest> requests, Engagement[] engagements)
    {
        var spindle = _context.Machine.Spindle;
        var step = _options.RpmStep;
        var first = Math.Ceiling(spindle.MinRpm / step) * step;
        var last = Math.Floor(spindle.MaxRpm / step) * step;
        if (last < first)
        {
            return spindle.MaxRpm;
        }

        var goal = FeedsOptions.ProfileFor(_context.Goal);
        var weights = requests.Select(r => r.CutLength > 0 ? r.CutLength : 1).ToArray();
        var total = weights.Sum();
        var candidates = new List<(double Rpm, double Time, double Speed, double Outside, double Chip)>();
        for (var rpm = first; rpm <= last + 1e-9; rpm += step)
        {
            double time = 0, speed = 0, outside = 0, chip = 0;
            for (var i = 0; i < requests.Count; i++)
            {
                var e = Evaluate(requests[i], rpm, engagements[i]);
                var share = weights[i] / total;
                time += weights[i] / Math.Max(e.FeedRate, 1e-6);
                var range = Math.Max(e.SurfaceSpeedMax - e.SurfaceSpeedMin, 1e-6);
                var deviation = (e.SurfaceSpeed - e.SurfaceSpeedTarget) / range;
                var beyond = (Math.Max(e.SurfaceSpeedMin - e.SurfaceSpeed, 0) + Math.Max(e.SurfaceSpeed - e.SurfaceSpeedMax, 0)) / range;
                speed += share * deviation * deviation;
                outside += share * beyond * beyond;
                chip += share * Math.Pow(Math.Max(0, 1 - (e.ChipLoad / e.TargetChipLoad)), 2);
            }

            candidates.Add((rpm, time, speed, outside, chip));
        }

        // Stay inside the material's surface speed range whenever some RPM allows it for every operation.
        var inRange = candidates.Where(c => c.Outside <= 1e-12).ToList();
        if (inRange.Count > 0)
        {
            candidates = inRange;
        }

        var fastest = candidates.Min(c => c.Time);
        return candidates
            .OrderBy(c => (goal.TimeWeight * ((c.Time / fastest) - 1)) + (goal.SurfaceSpeedWeight * c.Speed) + (OutOfRangeWeight * c.Outside) + (goal.ChipLoadWeight * c.Chip))
            .ThenBy(c => c.Rpm)
            .First().Rpm;
    }

    // ---- Loads and limits ----

    private (double Power, double Force, double Deflection, double Stress) Loads(FeedRequest request, double rpm, double feed, double ap, double ae, double deff, double lead)
    {
        var tool = request.Tool;
        var fz = feed / (Math.Max(tool.FluteCount, 1) * rpm);
        var hm = CuttingPhysics.MeanChipThickness(fz, ae, deff, lead);
        var kc = CuttingPhysics.SpecificCuttingForce(_context.Material, hm);
        var power = ap * ae * feed * kc / 60_000;
        var speed = Math.PI * deff * rpm / 60_000;
        var phi = CuttingPhysics.EngagementAngle(ae, deff);
        var helix = tool.HelixAngle * Math.PI / 180;
        var contact = helix > 0.01 ? Math.Min(ap, phi * deff / 2 / Math.Tan(helix)) : ap;
        var force = _options.ResultantForceFactor * Math.Max(power / Math.Max(speed, 1e-9), kc * hm * contact);
        var flute = _options.FluteCoreRatio * tool.Diameter;
        var shank = tool.ShankDiameter > 0 ? tool.ShankDiameter : tool.Diameter;
        if (tool.Stickout <= 0)
        {
            return (power, force, double.NaN, 0);
        }

        return (
            power,
            force,
            CuttingPhysics.TipDeflection(force, tool.Stickout, tool.FluteLength, flute, shank, _options.ModulusOf(tool.Material)),
            CuttingPhysics.BendingStress(force, tool.Stickout, tool.FluteLength, flute, shank));
    }

    private static void Consider(ref double scale, ref FeedLimit limit, double candidate, FeedLimit kind)
    {
        if (candidate < scale)
        {
            scale = candidate;
            limit = kind;
        }
    }

    private double AllowedDeflection(FeedRequest request) => FeedRequest.IsRoughing(request.Strategy)
        ? Math.Max(_options.RoughingDeflectionShare * request.RadialStock, request.Tolerance)
        : _options.FinishingDeflectionFactor * request.Tolerance;

    private double MachineFeed() => Math.Min(AxisRate(AxisName.X), AxisRate(AxisName.Y));

    private double AxisRate(AxisName axis) =>
        _context.Machine.GetAxis(axis) is { MaxRatePerMinute: > 0 } config ? config.MaxRatePerMinute : double.PositiveInfinity;

    private double StickoutFactor(Tool tool)
    {
        if (tool.Stickout <= 0 || tool.Diameter <= 0)
        {
            return 1;
        }

        var ratio = tool.Stickout / tool.Diameter;
        return Math.Clamp(1 - (0.1 * (ratio - _options.StickoutRatioFree)), 0.5, 1);
    }

    // ---- Material data and corrections ----

    internal readonly record struct CuttingTable(double SurfaceSpeedMin, double SurfaceSpeedMax, double ChipLoad, string? Note, bool Missing);

    internal CuttingTable Cutting(Tool tool)
    {
        var data = _context.Material.CuttingData;
        var own = data.Find(c => c.ToolMaterial == tool.Material && c.ChipLoad.Count > 0);
        if (own is not null)
        {
            return new CuttingTable(own.SurfaceSpeedMin, own.SurfaceSpeedMax, ChipLoadAt(own, tool.Diameter), null, false);
        }

        // Convert between carbide and HSS with the same ratios the built-in library uses.
        var other = data.Find(c => c.ChipLoad.Count > 0);
        if (other is not null)
        {
            var (speed, chip) = tool.Material == ToolMaterial.Hss ? (0.35, 0.8) : (1 / 0.35, 1 / 0.8);
            return new CuttingTable(
                other.SurfaceSpeedMin * speed,
                other.SurfaceSpeedMax * speed,
                ChipLoadAt(other, tool.Diameter) * chip,
                $"'{_context.Material.Name}' has no {tool.Material} cutting data; converted from {other.ToolMaterial}.",
                false);
        }

        return new CuttingTable(50, 150, 0.005 * tool.Diameter, $"'{_context.Material.Name}' has no cutting data; using a conservative fallback.", true);
    }

    /// <summary>Chip load vs. diameter: linear between table points, proportional below the first, flat above the last.</summary>
    internal static double ChipLoadAt(CuttingData data, double diameter)
    {
        var points = data.ChipLoad.OrderBy(p => p.Diameter).ToList();
        if (diameter <= points[0].Diameter)
        {
            return points[0].ChipLoad * diameter / points[0].Diameter;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (diameter <= points[i].Diameter)
            {
                var t = (diameter - points[i - 1].Diameter) / (points[i].Diameter - points[i - 1].Diameter);
                return points[i - 1].ChipLoad + (t * (points[i].ChipLoad - points[i - 1].ChipLoad));
            }
        }

        return points[^1].ChipLoad;
    }

    private (double ChipLoad, double SurfaceSpeed) CorrectionFor(FeedRequest request)
    {
        var correction = _context.Corrections.FirstOrDefault(c =>
            c.MaterialId == _context.Material.Id && c.ToolId == request.Tool.Id && c.Strategy == request.Strategy);
        return correction is null ? (1, 1) : (
            Math.Clamp(correction.ChipLoadFactor, _options.CorrectionMin, _options.CorrectionMax),
            Math.Clamp(correction.SurfaceSpeedFactor, _options.CorrectionMin, _options.CorrectionMax));
    }

    // ---- Warnings ----

    private List<FeedWarning> Warnings(FeedRequest request, FeedEvaluation e)
    {
        var tool = request.Tool;
        var material = _context.Material;
        var d = tool.Diameter;
        var list = new List<FeedWarning>();
        var cutting = Cutting(tool);
        if (cutting.Note is not null)
        {
            list.Add(new FeedWarning(cutting.Missing ? FeedWarningLevel.Error : FeedWarningLevel.Info, cutting.Note));
        }

        if (e.SurfaceSpeed < e.SurfaceSpeedMin * 0.999 || e.SurfaceSpeed > e.SurfaceSpeedMax * 1.001)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Warning, Format(
                $"Surface speed {e.SurfaceSpeed:0} m/min at {e.Rpm:0} RPM is outside {e.SurfaceSpeedMin:0}–{e.SurfaceSpeedMax:0} m/min for {material.Name}.")));
        }

        if (e.MaxChipThickness < _options.MinChipLoadShare * e.TableChipLoad)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Warning, Format(
                $"Chip thickness {e.MaxChipThickness:0.####} mm is below {_options.MinChipLoadShare:P0} of the table chip load ({e.TableChipLoad:0.####} mm; limited by {e.LimitedBy}): risk of rubbing. Reduce the engagement or stickout, or lower the RPM.")));
        }

        if (FeedRequest.IsRoughing(request.Strategy) && material.MaxDocFactor > 0 && e.Engagement.AxialDepth > material.MaxDocFactor * d * 1.001)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Warning, Format(
                $"Depth of cut {e.Engagement.AxialDepth:0.###} mm exceeds {material.MaxDocFactor:0.##}×D for {material.Name}.")));
        }

        if (material.MaxWocFactor > 0 && e.Engagement.RadialWidth > material.MaxWocFactor * d * 1.001)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Warning, Format(
                $"Width of cut {e.Engagement.RadialWidth:0.###} mm exceeds {material.MaxWocFactor:0.##}×D for {material.Name}.")));
        }

        if (tool.FluteLength > 0 && e.Engagement.AxialDepth > tool.FluteLength)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Error, Format(
                $"Depth of cut {e.Engagement.AxialDepth:0.###} mm exceeds the flute length of '{tool.Name}' ({tool.FluteLength:0.###} mm).")));
        }
        else if (tool.MaxDepthOfCut is > 0 && e.Engagement.AxialDepth > tool.MaxDepthOfCut.Value)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Warning, Format(
                $"Depth of cut {e.Engagement.AxialDepth:0.###} mm exceeds the maximum of '{tool.Name}' ({tool.MaxDepthOfCut.Value:0.###} mm).")));
        }

        if (tool.Stickout <= 0)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Warning, $"'{tool.Name}' has no stickout; deflection and tool strength are not checked."));
        }

        if (e.LimitedBy is FeedLimit.Power or FeedLimit.Deflection or FeedLimit.ToolStrength)
        {
            list.Add(new FeedWarning(FeedWarningLevel.Info, Format(
                $"Feed limited by {e.LimitedBy}: {e.FeedRate:0} mm/min (chip load {e.ChipLoad:0.####} of {e.TargetChipLoad:0.####} mm).")));
        }

        return list;
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
