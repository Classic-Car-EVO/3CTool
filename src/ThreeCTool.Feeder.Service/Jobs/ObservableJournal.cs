using ThreeCTool.Streaming.Jobs;

namespace ThreeCTool.Feeder.Service.Jobs;

public sealed record JournalEvent(string RunId, long Sequence, JobEvent Event);

/// <summary>
/// Journal decorator that numbers events per run (1-based, in journal order) and publishes them as they are added,
/// so clients can replay history and then follow live events without gaps.
/// </summary>
public sealed class ObservableJournal(IJobJournal inner) : IJobJournal
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, long> counts = new(StringComparer.Ordinal);

    /// <summary>Raised under the journal lock, in sequence order: handlers must be quick and must not call back in.</summary>
    public event EventHandler<JournalEvent>? EventAdded;

    public void Record(JobProgress progress, bool flush = false) => inner.Record(progress, flush);

    public void AddEvent(string jobId, JobEvent jobEvent, bool flush = false)
    {
        lock (gate)
        {
            if (!counts.TryGetValue(jobId, out var count))
            {
                count = inner.GetEvents(jobId).Count;
            }

            inner.AddEvent(jobId, jobEvent, flush);
            counts[jobId] = ++count;
            EventAdded?.Invoke(this, new JournalEvent(jobId, count, jobEvent));
        }
    }

    public void Flush() => inner.Flush();

    public JobProgress? Load(string jobId) => inner.Load(jobId);

    /// <summary>Events with their sequence numbers; taken under the same lock as <see cref="AddEvent"/>.</summary>
    public IReadOnlyList<JournalEvent> GetNumberedEvents(string runId)
    {
        lock (gate)
        {
            return [.. inner.GetEvents(runId).Select((e, i) => new JournalEvent(runId, i + 1, e))];
        }
    }

    public IReadOnlyList<JobEvent> GetEvents(string jobId) => inner.GetEvents(jobId);

    public void Dispose() => inner.Dispose();
}
