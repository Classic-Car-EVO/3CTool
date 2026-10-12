using Google.Protobuf.WellKnownTypes;

using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Projects;
using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Feeder.Tests;

public sealed class FeedbackConversionTests
{
    private static readonly DateTimeOffset Ended = new(2025, 3, 4, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Sections_RoundTrip()
    {
        var sections = new[] { (1, 120, StrategyType.AdaptiveRoughing), (121, 900, StrategyType.ParallelFinishing) };

        var text = FeederTags.FormatSections(sections);

        Assert.Equal("1-120:AdaptiveRoughing;121-900:ParallelFinishing", text);
        Assert.Equal(sections, FeederTags.ParseSections(text));
        Assert.Empty(FeederTags.ParseSections(null));
        Assert.Throws<FormatException>(() => FeederTags.ParseSections("1-x:Waterline"));
        Assert.Throws<FormatException>(() => FeederTags.ParseSections("1-5:NotAStrategy"));
    }

    [Fact]
    public void ToJobFeedback_MapsRatingsAndEvents()
    {
        var record = Record(new FeedbackSubmission { FinishRating = 4, ChatterRating = 0, WearRating = 2, Notes = "ok", WearEvent = WearEvent.Breakage });

        var feedback = FeedbackConversion.ToJobFeedback(record);

        Assert.Equal("Bracket", feedback.ProjectName);
        Assert.Equal(new string('a', 64), feedback.GcodeSha256);
        Assert.Equal(MaterialId, feedback.MaterialId);
        Assert.Equal(ToolId, feedback.ToolId);
        Assert.Equal(4, feedback.FinishRating);
        Assert.Null(feedback.ChatterRating);
        Assert.Equal(2, feedback.WearRating);
        Assert.Equal("ok", feedback.Notes);
        Assert.Equal(Ended.AddMinutes(1), feedback.RecordedAt);
        var feed = Assert.Single(feedback.FeedOverrides);
        Assert.Equal((120, 80), (feed.LineNumber, feed.Percent));
        var rpm = Assert.Single(feedback.RpmChanges);
        Assert.Equal((300, 15000.0), (rpm.LineNumber, rpm.Rpm));
    }

    [Fact]
    public void ToJobFeedback_SkippedForm_HasNoRatings()
    {
        var feedback = FeedbackConversion.ToJobFeedback(Record(new FeedbackSubmission { Skipped = true, FinishRating = 5 }));

        Assert.Null(feedback.FinishRating);
        Assert.Null(feedback.WearRating);
        Assert.Single(feedback.RpmChanges);
    }

    [Fact]
    public void ToWearLogs_AddsUsageAndReportedEvent()
    {
        var id = Guid.NewGuid();
        var logs = FeedbackConversion.ToWearLogs(Record(new FeedbackSubmission { WearEvent = WearEvent.Breakage, Notes = "snapped in slot" }), id);

        Assert.Equal(2, logs.Count);
        Assert.Equal(WearEventKind.Usage, logs[0].Kind);
        Assert.Equal(15, logs[0].RuntimeMinutes, 6);
        Assert.Equal(WearEventKind.Breakage, logs[1].Kind);
        Assert.Equal("snapped in slot", logs[1].Notes);
        Assert.All(logs, l => Assert.Equal((ToolId, id, Ended), (l.ToolId, l.JobFeedbackId!.Value, l.LoggedAt)));

        var noTool = Record(new FeedbackSubmission());
        noTool.Job.Tags.Remove(FeederTags.ToolId);
        Assert.Empty(FeedbackConversion.ToWearLogs(noTool, id));
        Assert.Single(FeedbackConversion.ToWearLogs(Record(new FeedbackSubmission()), id));
    }

    private static readonly Guid MaterialId = Guid.NewGuid();
    private static readonly Guid ToolId = Guid.NewGuid();

    private static FeedbackRecord Record(FeedbackSubmission feedback) => new()
    {
        RunId = "run",
        JobId = "job",
        Job = new UploadHeader
        {
            Name = "bracket.nc",
            ProjectName = "Bracket",
            Sha256 = new string('a', 64),
            Tags = { [FeederTags.MaterialId] = MaterialId.ToString(), [FeederTags.ToolId] = ToolId.ToString() },
        },
        FinalState = "Completed",
        StartedAt = Timestamp.FromDateTimeOffset(Ended.AddMinutes(-15)),
        EndedAt = Timestamp.FromDateTimeOffset(Ended),
        RecordedAt = Timestamp.FromDateTimeOffset(Ended.AddMinutes(1)),
        Feedback = feedback,
        FeedOverrides = { new FeedOverrideChange { At = Timestamp.FromDateTimeOffset(Ended.AddMinutes(-10)), Line = 120, Percent = 80 } },
        RpmChanges = { new Contracts.RpmChange { At = Timestamp.FromDateTimeOffset(Ended.AddMinutes(-5)), Line = 300, Rpm = 15000 } },
        RuntimeSeconds = 900,
    };
}
