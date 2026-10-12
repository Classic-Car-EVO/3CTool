using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;

namespace ThreeCTool.Feeds.Tests;

public class FeedsEngineTests
{
    private static Material MaterialFor(string key) => BuiltInLibrary.Materials().Single(m => m.Id == BuiltInLibrary.IdFor("material:" + key));

    private static Tool EndMill(double d = 6, int flutes = 2, double stickout = 18, double flute = 15) => new()
    {
        Name = $"{d} mm end mill",
        Type = ToolType.FlatEnd,
        Diameter = d,
        ShankDiameter = d,
        FluteCount = flutes,
        FluteLength = flute,
        Stickout = stickout,
    };

    private static FeedsEngine Engine(string material = "al-6061", OptimizationGoal goal = OptimizationGoal.Quality, MachineProfile? machine = null, IReadOnlyList<FeedCorrection>? corrections = null) =>
        new(new FeedContext
        {
            Machine = machine ?? BuiltInLibrary.DefaultMachine(),
            Material = MaterialFor(material),
            Goal = goal,
            Corrections = corrections ?? [],
        });

    private static FeedRequest Adaptive(Tool tool, double stepover = 0.6, double stepdown = 6) => new()
    {
        Strategy = StrategyType.AdaptiveRoughing,
        Tool = tool,
        Stepover = stepover,
        Stepdown = stepdown,
        Tolerance = 0.05,
        RadialStock = 0.5,
    };

    [Fact]
    public void ChipThinning_AtTenthDiameter_Is5Over3()
    {
        Assert.Equal(5.0 / 3, 1 / CuttingPhysics.RadialThinning(0.6, 6), 9);
        Assert.Equal(1, CuttingPhysics.RadialThinning(3, 6), 9);
        Assert.Equal(1, CuttingPhysics.RadialThinning(6, 6), 9);
    }

    [Fact]
    public void BallEnd_EffectiveDiameter_FollowsTheContactCircle()
    {
        var ball = new Tool { Type = ToolType.BallEnd, Diameter = 6, CornerRadius = 3 };
        Assert.Equal(2 * Math.Sqrt(0.5 * 5.5), CuttingPhysics.EffectiveDiameter(ball, 0.5, sideCutting: false), 9);
        Assert.Equal(6, CuttingPhysics.EffectiveDiameter(ball, 0.5, sideCutting: true), 9);
        Assert.Equal(6, CuttingPhysics.EffectiveDiameter(ball, 4, sideCutting: false), 9);
    }

    [Fact]
    public void Adaptive_UsesThinnedChipLoad_AndIsChipLoadLimited()
    {
        var engine = Engine();
        var request = Adaptive(EndMill());
        var e = engine.Evaluate(request, 18000, engine.NominalEngagement(request));

        Assert.Equal(FeedLimit.ChipLoad, e.LimitedBy);
        Assert.Equal(0.04, e.TableChipLoad, 9);
        Assert.Equal(0.04 * 0.8 * 5 / 3, e.ChipLoad, 9);
        Assert.Equal(0.04 * 0.8, e.MaxChipThickness, 9);
        Assert.Equal(e.ChipLoad * 2 * 18000, e.FeedRate, 6);
        Assert.Equal(e.FeedRate * 0.3, e.PlungeRate, 6);
        Assert.True(e.PowerW < e.AvailablePowerW);
        Assert.True(e.Deflection < e.AllowedDeflection);
    }

    [Fact]
    public void Slot_InWeakSpindle_IsPowerLimited()
    {
        var machine = BuiltInLibrary.DefaultMachine();
        machine.Spindle.PowerCurve = SpindleConfig.ConstantTorqueCurve(1500, 30000, 1000, 24000);
        var engine = Engine("al-7075", machine: machine);
        var request = Adaptive(EndMill(d: 10, flutes: 3, stickout: 30, flute: 22), stepover: 10, stepdown: 5);
        var e = engine.Evaluate(request, 12000, engine.NominalEngagement(request));

        Assert.Equal(FeedLimit.Power, e.LimitedBy);
        Assert.Equal(1000 * 0.5 * 0.8, e.AvailablePowerW, 6);
        Assert.InRange(e.PowerW, 0.97 * e.AvailablePowerW, 1.01 * e.AvailablePowerW);
        Assert.True(e.ChipLoad < e.TargetChipLoad);
    }

    [Fact]
    public void LongThinTool_Finishing_IsDeflectionLimited()
    {
        var engine = Engine();
        var tool = EndMill(d: 3, stickout: 30, flute: 12);
        var request = new FeedRequest
        {
            Strategy = StrategyType.Waterline,
            Tool = tool,
            Stepover = 0.3,
            Stepdown = 3,
            Tolerance = 0.01,
        };
        var e = engine.Evaluate(request, 20000, engine.NominalEngagement(request));

        Assert.Contains(e.LimitedBy, new[] { FeedLimit.Deflection, FeedLimit.ToolStrength });
        Assert.True(e.Deflection <= e.AllowedDeflection * 1.01, $"{e.Deflection} > {e.AllowedDeflection}");
        Assert.Equal(0.01, e.AllowedDeflection, 9);
    }

    [Fact]
    public void Softwood_IsCappedByTheMachineFeed()
    {
        var engine = Engine("softwood");
        var request = Adaptive(EndMill(flutes: 3));
        var e = engine.Evaluate(request, 24000, engine.NominalEngagement(request));

        Assert.Equal(FeedLimit.MachineFeed, e.LimitedBy);
        Assert.Equal(6000, e.FeedRate, 6);
    }

    [Fact]
    public void SpeedGoal_WithMachineCap_PicksTheLowestRpmInsideTheSurfaceSpeedRange()
    {
        var engine = Engine("softwood", OptimizationGoal.Speed);
        var plan = engine.Plan([Adaptive(EndMill(flutes: 3))]);

        // 300 m/min on 6 mm is 15915 RPM; any higher RPM only thins the chip at the capped feed.
        Assert.Equal(16000, plan.Rpm);
        Assert.Equal(6000, plan.Operations[0].Evaluation.FeedRate, 6);
        Assert.Equal(0, plan.Rpm % 100);
    }

    [Fact]
    public void Goals_TradeRemovalRateForSurfaceSpeed()
    {
        var request = Adaptive(EndMill());
        var speed = Engine(goal: OptimizationGoal.Speed).Plan([request]).Operations[0].Evaluation;
        var quality = Engine(goal: OptimizationGoal.Quality).Plan([request]).Operations[0].Evaluation;
        var life = Engine(goal: OptimizationGoal.ToolLife).Plan([request]).Operations[0].Evaluation;

        Assert.True(speed.MaterialRemovalRate > quality.MaterialRemovalRate);
        Assert.True(quality.MaterialRemovalRate > life.MaterialRemovalRate);
        Assert.True(life.SurfaceSpeed < quality.SurfaceSpeed);
        Assert.All(new[] { speed, quality, life }, e => Assert.InRange(e.SurfaceSpeed, e.SurfaceSpeedMin, e.SurfaceSpeedMax));
    }

    [Fact]
    public void Plan_UsesOneRpmForAllOperationsOfTheTool()
    {
        var tool = EndMill();
        var finishing = new FeedRequest { Strategy = StrategyType.ParallelFinishing, Tool = tool, Stepover = 1, Stepdown = 0, Tolerance = 0.01, IncomingStock = 0.5 };
        var plan = Engine().Plan([Adaptive(tool), finishing]);

        Assert.All(plan.Operations, o => Assert.Equal(plan.Rpm, o.Evaluation.Rpm));
        Assert.Equal(0.5, plan.Operations[1].Evaluation.Engagement.AxialDepth, 9);
        Assert.Throws<ArgumentException>(() => Engine().Plan([Adaptive(tool), Adaptive(EndMill())]));
    }

    [Fact]
    public void FixedRpm_SkipsTheOptimization()
    {
        var plan = Engine().Plan([Adaptive(EndMill())], rpm: 12345);
        Assert.Equal(12345, plan.Rpm);
        Assert.Equal(12345, plan.Operations[0].ToConditions().Rpm);
    }

    [Fact]
    public void Correction_ScalesTheChipLoad()
    {
        var tool = EndMill();
        var correction = new FeedCorrection { MaterialId = BuiltInLibrary.IdFor("material:al-6061"), ToolId = tool.Id, Strategy = StrategyType.AdaptiveRoughing, ChipLoadFactor = 1.2 };
        var request = Adaptive(tool);
        var plain = Engine().Evaluate(request, 15000, new Engagement(6, 0.6));
        var corrected = Engine(corrections: [correction]).Evaluate(request, 15000, new Engagement(6, 0.6));

        Assert.Equal(plain.ChipLoad * 1.2, corrected.ChipLoad, 9);
    }

    [Fact]
    public void MissingCuttingData_FallsBack_WithAnError()
    {
        var material = MaterialFor("al-6061");
        material.CuttingData = [];
        var engine = new FeedsEngine(new FeedContext { Machine = BuiltInLibrary.DefaultMachine(), Material = material });
        var result = engine.Plan([Adaptive(EndMill())]).Operations[0];

        Assert.Equal(0.03, result.Evaluation.TableChipLoad, 9);
        Assert.Contains(result.Warnings, w => w.Level == FeedWarningLevel.Error && w.Message.Contains("no cutting data", StringComparison.Ordinal));
    }

    [Fact]
    public void HssTool_WithoutHssData_ConvertsFromCarbide()
    {
        var engine = Engine("cf-g10");
        var tool = EndMill();
        tool.Material = ToolMaterial.Hss;
        var result = engine.Plan([Adaptive(tool, stepdown: 3)]).Operations[0];

        Assert.Equal(100 * 0.35, result.Evaluation.SurfaceSpeedMin, 9);
        Assert.Equal(0.04 * 0.8, result.Evaluation.TableChipLoad, 9);
        Assert.Contains(result.Warnings, w => w.Level == FeedWarningLevel.Info && w.Message.Contains("converted", StringComparison.Ordinal));
    }

    [Fact]
    public void DepthBeyondFluteLength_IsAnError()
    {
        var result = Engine().Plan([Adaptive(EndMill(flute: 4))]).Operations[0];
        Assert.Contains(result.Warnings, w => w.Level == FeedWarningLevel.Error && w.Message.Contains("flute length", StringComparison.Ordinal));
    }

    [Fact]
    public void Limiter_SlowsHeavierCuts_AndKeepsNominal()
    {
        var result = Engine().Plan([Adaptive(EndMill())]).Operations[0];
        var limiter = result.Limiter;

        Assert.Equal(3.6, limiter.NominalArea, 9);
        Assert.Equal(1, limiter.Factor(3.6));
        Assert.Equal(1, limiter.Factor(4));
        var slot = limiter.Factor(36);
        Assert.InRange(slot, 0.1, 0.95);
        Assert.Equal(0, Math.Round(slot / 0.05, 9) % 1, 9);
        Assert.True(limiter.Factor(12) >= slot);
    }
}
