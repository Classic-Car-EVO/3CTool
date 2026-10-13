using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core.Learning;
using ThreeCTool.Data;
using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeds;

using FeederClient = ThreeCTool.Feeder.Contracts.Feeder.FeederClient;

namespace ThreeCTool.Desktop.Services;

/// <summary>Outcome of one feedback sync.</summary>
/// <param name="Imported">Runs stored as job feedback (with their tool wear logs).</param>
/// <param name="Learned">Runs that updated feed corrections.</param>
/// <param name="AlreadyImported">Runs found in the library from an earlier, unacknowledged sync.</param>
/// <param name="Notes">Why individual runs were imported without learning.</param>
public sealed record FeederSyncResult(int Imported, int Learned, int AlreadyImported, IReadOnlyList<string> Notes);

/// <summary>
/// Downloads unacknowledged run feedback from a paired feeder, stores it as <see cref="JobFeedback"/> and
/// <see cref="ToolWearLog"/> rows, applies <see cref="FeedLearning"/> and then acknowledges the runs on the feeder.
/// Imports are idempotent (keyed by feeder run id), so a sync interrupted before the acknowledgement is safe to repeat.
/// </summary>
public sealed class FeederLearningSync(PairedFeederStore feeders, TimeProvider? time = null, FeedsOptions? options = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>Connects to a paired feeder (pinned certificate + token) and syncs its feedback.</summary>
    public async Task<FeederSyncResult> SyncAsync(Guid pairedFeederId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feeders);
        var feeder = await feeders.GetAsync(pairedFeederId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Paired feeder '{pairedFeederId}' not found.");
        using var channel = FeederChannels.ForPairedFeeder(feeder.Address, feeder.CertificateFingerprint, feeder.Token);
        var result = await SyncAsync(new FeederClient(channel), cancellationToken).ConfigureAwait(false);
        await feeders.MarkSyncedAsync(feeder.Id, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<FeederSyncResult> SyncAsync(FeederClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var list = await client.ListFeedbackAsync(new ListFeedbackRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (list.Records.Count == 0)
        {
            return new FeederSyncResult(0, 0, 0, []);
        }

        int imported = 0, learned = 0, already = 0;
        var notes = new List<string>();
        var done = new List<string>();
        await using (var db = await LibraryDatabase.OpenAsync(feeders.LibraryPath, cancellationToken).ConfigureAwait(false))
        {
            foreach (var record in list.Records.OrderBy(r => r.EndedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue))
            {
                if (await db.JobFeedback.AnyAsync(f => f.FeederRunId == record.RunId, cancellationToken).ConfigureAwait(false))
                {
                    already++;
                    done.Add(record.RunId);
                    continue;
                }

                var feedback = FeedbackConversion.ToJobFeedback(record);
                feedback.FeederRunId = record.RunId;
                var wear = FeedbackConversion.ToWearLogs(record, feedback.Id);
                db.JobFeedback.Add(feedback);
                db.ToolWearLogs.AddRange(wear);
                if (await LearnAsync(db, record, feedback, wear, cancellationToken).ConfigureAwait(false) is { } note)
                {
                    notes.Add($"{Describe(record)}: {note}");
                }
                else
                {
                    learned++;
                }

                // Saved per run so one bad record cannot hold back the others' acknowledgement.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                imported++;
                done.Add(record.RunId);
            }
        }

        var ack = new FeedbackAck();
        ack.RunIds.AddRange(done);
        await client.AcknowledgeFeedbackAsync(ack, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new FeederSyncResult(imported, learned, already, notes);
    }

    /// <summary>Applies the run to the feed corrections; returns why it could not, or null when it did.</summary>
    private async Task<string?> LearnAsync(
        LibraryDbContext db, FeedbackRecord record, JobFeedback feedback, IReadOnlyList<ToolWearLog> wear, CancellationToken cancellationToken)
    {
        if (record.FinalState is not ("Completed" or "Aborted"))
        {
            return $"not learned from a run that ended {record.FinalState}";
        }

        if (feedback.MaterialId is not { } materialId || feedback.ToolId is not { } toolId)
        {
            return "no material/tool tags; feedback stored without learning";
        }

        IReadOnlyList<(int FirstLine, int LastLine, Core.Projects.StrategyType Strategy)> sections;
        try
        {
            sections = FeederTags.ParseSections(record.Job?.Tags.GetValueOrDefault(FeederTags.Sections));
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }

        if (sections.Count == 0)
        {
            return "no strategy sections; feedback stored without learning";
        }

        var existing = await db.FeedCorrections.Where(c => c.MaterialId == materialId && c.ToolId == toolId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var input = new LearningInput
        {
            Feedback = feedback,
            MaterialId = materialId,
            ToolId = toolId,
            Sections = [.. sections.Select(s => new FileSection(s.FirstLine, s.LastLine, s.Strategy))],
            PlannedRpm = record.Job?.PlannedRpm ?? 0,
            WearEvents = wear,
        };
        foreach (var updated in FeedLearning.Update(existing, input, time.GetUtcNow(), options))
        {
            var tracked = existing.Find(c => c.Id == updated.Id);
            if (tracked is null)
            {
                db.FeedCorrections.Add(updated);
                existing.Add(updated);
            }
            else
            {
                tracked.ChipLoadFactor = updated.ChipLoadFactor;
                tracked.SurfaceSpeedFactor = updated.SurfaceSpeedFactor;
                tracked.SampleCount = updated.SampleCount;
                tracked.UpdatedAt = updated.UpdatedAt;
            }
        }

        return null;
    }

    private static string Describe(FeedbackRecord record) =>
        record.Job is { Name.Length: > 0 } job ? $"{job.Name} ({record.RunId})" : record.RunId;
}
