using System.Globalization;

using Microsoft.Data.Sqlite;

namespace ThreeCTool.Streaming.Jobs;

/// <summary>Durable store for job progress and the job event log.</summary>
public interface IJobJournal : IDisposable
{
    /// <summary>Records progress; committed within the batching interval, or immediately when <paramref name="flush"/> is set.</summary>
    void Record(JobProgress progress, bool flush = false);

    /// <summary>Appends an event; committed with the next batch, or immediately when <paramref name="flush"/> is set.</summary>
    void AddEvent(string jobId, JobEvent jobEvent, bool flush = false);

    void Flush();

    JobProgress? Load(string jobId);

    IReadOnlyList<JobEvent> GetEvents(string jobId);
}

/// <summary>
/// SQLite journal (WAL, <c>synchronous=FULL</c> so every commit is fsynced). Commits are batched to at most one per
/// <see cref="CommitInterval"/>; state changes, alarms and errors are committed immediately.
/// </summary>
public sealed class SqliteJobJournal : IJobJournal
{
    private readonly Lock gate = new();
    private readonly SqliteConnection connection;
    private readonly Timer timer;
    private readonly Dictionary<string, JobProgress> dirty = new(StringComparer.Ordinal);
    private readonly List<(string JobId, JobEvent Event)> pendingEvents = [];
    private long lastCommitTicks;
    private bool disposed;

    public SqliteJobJournal(string path, TimeSpan? commitInterval = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        CommitInterval = commitInterval ?? TimeSpan.FromMilliseconds(500);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS job_progress (
                job_id TEXT PRIMARY KEY,
                file_sha256 TEXT NOT NULL,
                state TEXT NOT NULL,
                interruption TEXT NOT NULL,
                interruption_code INTEGER,
                last_sent INTEGER NOT NULL,
                last_acked INTEGER NOT NULL,
                executed INTEGER NOT NULL,
                executed_exact INTEGER NOT NULL,
                machine_position TEXT,
                work_offset TEXT,
                work_coordinates TEXT,
                tool_length_offset REAL,
                spindle_rpm REAL,
                feed_override INTEGER NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS job_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                at TEXT NOT NULL,
                kind TEXT NOT NULL,
                line INTEGER,
                message TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_job_events_job ON job_events(job_id, id);
            """);
        timer = new Timer(_ => FlushIfDirty(), null, CommitInterval, CommitInterval);
    }

    public TimeSpan CommitInterval { get; }

    /// <summary>Number of transactions committed (for tests and diagnostics).</summary>
    public int CommitCount { get; private set; }

    public void Record(JobProgress progress, bool flush = false)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            dirty[progress.JobId] = progress;
            CommitIfDue(flush);
        }
    }

    public void AddEvent(string jobId, JobEvent jobEvent, bool flush = false)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(jobEvent);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pendingEvents.Add((jobId, jobEvent));
            CommitIfDue(flush);
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            if (!disposed)
            {
                Commit();
            }
        }
    }

    public JobProgress? Load(string jobId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (dirty.TryGetValue(jobId, out var pending))
            {
                return pending;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT file_sha256, state, interruption, interruption_code, last_sent, last_acked, executed, executed_exact,
                       machine_position, work_offset, work_coordinates, tool_length_offset, spindle_rpm, feed_override, updated_at
                FROM job_progress WHERE job_id = $id
                """;
            command.Parameters.AddWithValue("$id", jobId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new JobProgress
            {
                JobId = jobId,
                FileSha256 = reader.GetString(0),
                State = Enum.Parse<JobState>(reader.GetString(1)),
                Interruption = Enum.Parse<InterruptionKind>(reader.GetString(2)),
                InterruptionCode = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                LastSent = reader.GetInt32(4),
                LastAcked = reader.GetInt32(5),
                Executed = reader.GetInt32(6),
                ExecutedExact = reader.GetInt32(7) != 0,
                MachinePosition = reader.IsDBNull(8) ? null : reader.GetString(8),
                WorkOffset = reader.IsDBNull(9) ? null : reader.GetString(9),
                WorkCoordinates = reader.IsDBNull(10) ? null : reader.GetString(10),
                ToolLengthOffset = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                SpindleRpm = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                FeedOverride = reader.GetInt32(13),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
            };
        }
    }

    public IReadOnlyList<JobEvent> GetEvents(string jobId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = new List<JobEvent>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT at, kind, line, message FROM job_events WHERE job_id = $id ORDER BY id";
                command.Parameters.AddWithValue("$id", jobId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new JobEvent(
                        DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        reader.GetString(3)));
                }
            }

            result.AddRange(pendingEvents.Where(p => p.JobId == jobId).Select(p => p.Event));
            return result;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            Commit();
            disposed = true;
        }

        timer.Dispose();
        connection.Dispose();
    }

    private void FlushIfDirty()
    {
        lock (gate)
        {
            if (!disposed && (dirty.Count > 0 || pendingEvents.Count > 0))
            {
                Commit();
            }
        }
    }

    private void CommitIfDue(bool force)
    {
        if (force || Environment.TickCount64 - lastCommitTicks >= CommitInterval.TotalMilliseconds)
        {
            Commit();
        }
    }

    private void Commit()
    {
        if (dirty.Count == 0 && pendingEvents.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var progress in dirty.Values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO job_progress (job_id, file_sha256, state, interruption, interruption_code, last_sent, last_acked, executed,
                    executed_exact, machine_position, work_offset, work_coordinates, tool_length_offset, spindle_rpm, feed_override, updated_at)
                VALUES ($id, $sha, $state, $interruption, $code, $sent, $acked, $executed, $exact, $mpos, $wco, $wcs, $tlo, $rpm, $feed, $at)
                ON CONFLICT(job_id) DO UPDATE SET file_sha256 = $sha, state = $state, interruption = $interruption,
                    interruption_code = $code, last_sent = $sent, last_acked = $acked, executed = $executed, executed_exact = $exact,
                    machine_position = $mpos, work_offset = $wco, work_coordinates = $wcs, tool_length_offset = $tlo,
                    spindle_rpm = $rpm, feed_override = $feed, updated_at = $at
                """;
            var p = command.Parameters;
            p.AddWithValue("$id", progress.JobId);
            p.AddWithValue("$sha", progress.FileSha256);
            p.AddWithValue("$state", progress.State.ToString());
            p.AddWithValue("$interruption", progress.Interruption.ToString());
            p.AddWithValue("$code", (object?)progress.InterruptionCode ?? DBNull.Value);
            p.AddWithValue("$sent", progress.LastSent);
            p.AddWithValue("$acked", progress.LastAcked);
            p.AddWithValue("$executed", progress.Executed);
            p.AddWithValue("$exact", progress.ExecutedExact ? 1 : 0);
            p.AddWithValue("$mpos", (object?)progress.MachinePosition ?? DBNull.Value);
            p.AddWithValue("$wco", (object?)progress.WorkOffset ?? DBNull.Value);
            p.AddWithValue("$wcs", (object?)progress.WorkCoordinates ?? DBNull.Value);
            p.AddWithValue("$tlo", (object?)progress.ToolLengthOffset ?? DBNull.Value);
            p.AddWithValue("$rpm", (object?)progress.SpindleRpm ?? DBNull.Value);
            p.AddWithValue("$feed", progress.FeedOverride);
            p.AddWithValue("$at", progress.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        foreach (var (jobId, jobEvent) in pendingEvents)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO job_events (job_id, at, kind, line, message) VALUES ($id, $at, $kind, $line, $message)";
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$at", jobEvent.At.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$kind", jobEvent.Kind);
            command.Parameters.AddWithValue("$line", (object?)jobEvent.Line ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", jobEvent.Message);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        dirty.Clear();
        pendingEvents.Clear();
        lastCommitTicks = Environment.TickCount64;
        CommitCount++;
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
