using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Projects;
using ThreeCTool.Data;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Feeder.Tests;

public sealed class FeederLearningSyncTests
{
    [Fact]
    public async Task Pairing_IsStoredAndReplacedOnRepair()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var store = new PairedFeederStore(Path.Combine(feeder.Folder, "library.db"));
        var (_, first) = await feeder.PairAsync();
        var machine = Guid.NewGuid();

        var saved = await store.SaveAsync(first, machine);
        Assert.Equal(first.FeederId, saved.FeederId);
        Assert.Equal(first.Fingerprint, saved.CertificateFingerprint);
        Assert.Equal(machine, saved.MachineProfileId);

        var (_, second) = await feeder.PairAsync();
        var again = await store.SaveAsync(second);
        Assert.Equal(saved.Id, again.Id);
        var listed = Assert.Single(await store.ListAsync());
        Assert.Equal(second.Token, listed.Token);
        Assert.Equal(machine, listed.MachineProfileId);

        await store.RemoveAsync(saved.Id);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Sync_ImportsFeedbackLearnsAndAcknowledges()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var store = new PairedFeederStore(Path.Combine(feeder.Folder, "library.db"));
        var (client, pairing) = await feeder.PairAsync();
        var paired = await store.SaveAsync(pairing);
        var sync = new FeederLearningSync(store);
        var material = Guid.NewGuid();
        var tool = Guid.NewGuid();

        // Tagged run with a 120 % override from the M0 on, rated and with a wear event.
        var (lines, _) = TestJobs.Zigzag(40, pauseAt: 2);
        var job = await FeederHarness.UploadAsync(client, lines, header: h =>
        {
            h.Tags[FeederTags.MaterialId] = material.ToString();
            h.Tags[FeederTags.ToolId] = tool.ToString();
            h.Tags[FeederTags.Sections] = FeederTags.FormatSections([(1, lines.Count, StrategyType.AdaptiveRoughing)]);
        });
        var tagged = await client.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        await feeder.WaitStatusAsync(s => s.Prompt is not null);
        await client.SetFeedOverrideAsync(new FeedOverrideRequest { Percent = 120 });
        Assert.Equal("Completed", (await feeder.RunToEndAsync(tagged.RunId)).Job.State);
        await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission
        {
            RunId = tagged.RunId, FinishRating = 4, ChatterRating = 5, WearRating = 3, WearEvent = WearEvent.Wear,
        });

        // Untagged run: stored, not learned from.
        var (plain, _) = TestJobs.Zigzag(5);
        var untaggedJob = await FeederHarness.UploadAsync(client, plain, "plain.nc");
        var untagged = await client.StartJobAsync(new StartJobRequest { JobId = untaggedJob.JobId });
        Assert.Equal("Completed", (await feeder.RunToEndAsync(untagged.RunId)).Job.State);
        await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission { RunId = untagged.RunId, Skipped = true });

        var result = await sync.SyncAsync(paired.Id);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Learned);
        Assert.Equal(0, result.AlreadyImported);
        Assert.Contains(result.Notes, n => n.Contains("plain.nc", StringComparison.Ordinal) && n.Contains("tags", StringComparison.Ordinal));
        Assert.Empty((await client.ListFeedbackAsync(new ListFeedbackRequest())).Records);
        Assert.NotNull((await store.GetAsync(paired.Id))!.LastSyncAt);

        await using (var db = await LibraryDatabase.OpenAsync(store.LibraryPath))
        {
            var feedback = await db.JobFeedback.SingleAsync(f => f.FeederRunId == tagged.RunId);
            Assert.Equal(material, feedback.MaterialId);
            Assert.Equal(4, feedback.FinishRating);
            Assert.Contains(feedback.FeedOverrides, o => o.Percent == 120);
            Assert.Null((await db.JobFeedback.SingleAsync(f => f.FeederRunId == untagged.RunId)).FinishRating);

            var wear = await db.ToolWearLogs.Where(w => w.JobFeedbackId == feedback.Id).ToListAsync();
            Assert.Contains(wear, w => w.Kind == WearEventKind.Usage && w.RuntimeMinutes > 0);
            Assert.Contains(wear, w => w.Kind == WearEventKind.Wear && w.ToolId == tool);

            var correction = await db.FeedCorrections.SingleAsync();
            Assert.Equal((material, tool, StrategyType.AdaptiveRoughing), (correction.MaterialId, correction.ToolId, correction.Strategy));
            Assert.Equal(1, correction.SampleCount);
            Assert.True(correction.ChipLoadFactor > 1, $"chip load {correction.ChipLoadFactor}");
            Assert.True(correction.SurfaceSpeedFactor < 1, $"surface speed {correction.SurfaceSpeedFactor}");
        }

        var second = await sync.SyncAsync(paired.Id);
        Assert.Equal((0, 0), (second.Imported, second.AlreadyImported));
    }

    [Fact]
    public async Task Sync_InterruptedBeforeAcknowledgement_DoesNotImportTwice()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var store = new PairedFeederStore(Path.Combine(feeder.Folder, "library.db"));
        var (client, _) = await feeder.PairAsync();
        var (lines, _) = TestJobs.Zigzag(5);
        var job = await FeederHarness.UploadAsync(client, lines);
        var run = await client.StartJobAsync(new StartJobRequest { JobId = job.JobId });
        await feeder.RunToEndAsync(run.RunId);
        await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission { RunId = run.RunId, FinishRating = 2 });

        // A previous sync stored the run but never acknowledged it.
        await using (var db = await LibraryDatabase.OpenAsync(store.LibraryPath))
        {
            db.JobFeedback.Add(new JobFeedback { FeederRunId = run.RunId, RecordedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var result = await new FeederLearningSync(store).SyncAsync(client);

        Assert.Equal((0, 1), (result.Imported, result.AlreadyImported));
        Assert.Empty((await client.ListFeedbackAsync(new ListFeedbackRequest())).Records);
        await using var check = await LibraryDatabase.OpenAsync(store.LibraryPath);
        Assert.Equal(1, await check.JobFeedback.CountAsync());
        Assert.Empty(await check.ToolWearLogs.ToListAsync());
    }
}
