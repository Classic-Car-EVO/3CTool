using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Projects;

namespace ThreeCTool.Feeds.Tests;

public class FeedLearningTests
{
    private static readonly Guid MaterialId = Guid.NewGuid();
    private static readonly Guid ToolId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static LearningInput Input(JobFeedback feedback, IReadOnlyList<FileSection>? sections = null, IReadOnlyList<ToolWearLog>? wear = null) => new()
    {
        Feedback = feedback,
        MaterialId = MaterialId,
        ToolId = ToolId,
        Sections = sections ?? [new FileSection(1, 100, StrategyType.AdaptiveRoughing)],
        PlannedRpm = 16000,
        WearEvents = wear ?? [],
    };

    private static JobFeedback Overrides(params (int Line, int Percent)[] events) => new()
    {
        FeedOverrides = [.. events.Select(e => new FeedOverrideEvent { LineNumber = e.Line, Percent = e.Percent })],
    };

    [Fact]
    public void NoSignals_KeepsFactors_AndCountsTheSample()
    {
        var result = Assert.Single(FeedLearning.Update([], Input(new JobFeedback()), Now));

        Assert.Equal(1, result.ChipLoadFactor, 12);
        Assert.Equal(1, result.SurfaceSpeedFactor, 12);
        Assert.Equal(1, result.SampleCount);
        Assert.Equal(Now, result.UpdatedAt);
        Assert.Equal((MaterialId, ToolId, StrategyType.AdaptiveRoughing), (result.MaterialId, result.ToolId, result.Strategy));
    }

    [Fact]
    public void FeedOverride_MovesChipLoad_HalfwayInLogScale()
    {
        var result = Assert.Single(FeedLearning.Update([], Input(Overrides((1, 120))), Now));
        Assert.Equal(Math.Sqrt(1.2), result.ChipLoadFactor, 9);
        Assert.Equal(1, result.SurfaceSpeedFactor, 12);
    }

    [Fact]
    public void OneJob_ChangesAFactorByAtMostTenPercent()
    {
        Assert.Equal(1.1, Assert.Single(FeedLearning.Update([], Input(Overrides((1, 200))), Now)).ChipLoadFactor, 9);
        Assert.Equal(0.9, Assert.Single(FeedLearning.Update([], Input(Overrides((1, 10))), Now)).ChipLoadFactor, 9);
    }

    [Fact]
    public void Factors_StayWithinBounds_AndKeepTheirIdentity()
    {
        var existing = new FeedCorrection { MaterialId = MaterialId, ToolId = ToolId, Strategy = StrategyType.AdaptiveRoughing, ChipLoadFactor = 1.45, SurfaceSpeedFactor = 0.52, SampleCount = 7 };
        var feedback = Overrides((1, 200));
        feedback.WearRating = 1;
        var result = Assert.Single(FeedLearning.Update([existing], Input(feedback), Now));

        Assert.Equal(1.5, result.ChipLoadFactor, 12);
        Assert.Equal(0.5, result.SurfaceSpeedFactor, 12);
        Assert.Equal(existing.Id, result.Id);
        Assert.Equal(8, result.SampleCount);
        Assert.Equal(1.45, existing.ChipLoadFactor, 12);
    }

    [Fact]
    public void Overrides_AreAttributedToTheSectionsTheyWereActiveIn()
    {
        FileSection[] sections = [new(1, 100, StrategyType.AdaptiveRoughing), new(101, 200, StrategyType.ParallelFinishing)];
        var result = FeedLearning.Update([], Input(Overrides((80, 130)), sections), Now);

        var roughing = result.Single(c => c.Strategy == StrategyType.AdaptiveRoughing);
        var finishing = result.Single(c => c.Strategy == StrategyType.ParallelFinishing);

        // Lines 1–79 at 100 %, 80–100 at 130 %; the finishing section starts with 130 % carried in.
        Assert.Equal(Math.Sqrt(((79 * 100) + (21 * 130)) / 100.0 / 100), roughing.ChipLoadFactor, 9);
        Assert.Equal(1.1, finishing.ChipLoadFactor, 9);
    }

    [Fact]
    public void RpmChange_MovesSurfaceSpeed_AndKeepsTheFeedPerMinute()
    {
        var feedback = new JobFeedback { RpmChanges = [new RpmChangeEvent { LineNumber = 1, Rpm = 17600 }] };
        var result = Assert.Single(FeedLearning.Update([], Input(feedback), Now));

        Assert.Equal(Math.Sqrt(1.1), result.SurfaceSpeedFactor, 9);
        Assert.Equal(1 / Math.Sqrt(1.1), result.ChipLoadFactor, 9);
    }

    [Fact]
    public void PoorRatings_AndWearEvents_ReduceFactors()
    {
        var feedback = new JobFeedback { ChatterRating = 2, FinishRating = 4 };
        var wear = new[] { new ToolWearLog { ToolId = ToolId, Kind = WearEventKind.Wear } };
        var result = Assert.Single(FeedLearning.Update([], Input(feedback, wear: wear), Now));
        Assert.Equal(Math.Exp(-0.05), result.ChipLoadFactor, 9);
        Assert.Equal(Math.Exp(-0.05), result.SurfaceSpeedFactor, 9);

        var broken = new[] { new ToolWearLog { ToolId = ToolId, Kind = WearEventKind.Breakage } };
        result = Assert.Single(FeedLearning.Update([], Input(new JobFeedback(), wear: broken), Now));
        Assert.Equal(Math.Exp(-0.1), result.ChipLoadFactor, 9);
        Assert.Equal(1, result.SurfaceSpeedFactor, 12);
    }
}
