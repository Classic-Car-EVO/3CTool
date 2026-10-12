using System.Globalization;

using Google.Protobuf;

using Microsoft.Data.Sqlite;

using ThreeCTool.Feeder.Contracts;

namespace ThreeCTool.Feeder.Service.Storage;

public sealed record JobRow(string Id, UploadHeader Header, int LineCount, DateTimeOffset UploadedAt);

/// <summary>
/// One execution of a job. <see cref="EndedAt"/> is set whenever streaming stops; <see cref="Recoverable"/> runs
/// (stopped, faulted, interrupted) can still be resumed, so they are not offered for feedback yet.
/// </summary>
public sealed record RunRow
{
    public required string Id { get; init; }

    public required string JobId { get; init; }

    public required UploadHeader Header { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string State { get; init; } = "Pending";

    public int LastLine { get; init; }

    public double RuntimeSeconds { get; init; }

    public bool Recoverable { get; init; }

    public bool FeedbackPending { get; init; }

    public FeedbackSubmission? Feedback { get; init; }

    public DateTimeOffset? FeedbackAt { get; init; }

    public bool Acknowledged { get; init; }
}

public sealed record PairingRow(string Id, string ClientName, string TokenHash, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt);

public sealed record RunChange(DateTimeOffset At, int Line, double Value);

/// <summary>Feeder state in SQLite: settings, uploaded jobs, runs with feedback, and paired desktops.</summary>
public sealed class FeederDatabase : IDisposable
{
    public const string FeedChange = "feed";
    public const string RpmChange = "rpm";

    private const string RunColumns =
        "id, job_id, header, started_at, ended_at, state, last_line, runtime_seconds, recoverable, feedback_pending, feedback, feedback_at, acknowledged";

    private readonly SqliteConnection connection;
    private readonly Lock gate = new();
    private bool disposed;

    public FeederDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY, sha256 TEXT NOT NULL, header BLOB NOT NULL, line_count INTEGER NOT NULL, uploaded_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_jobs_sha ON jobs(sha256);
            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY, job_id TEXT NOT NULL, header BLOB NOT NULL, started_at TEXT NOT NULL, ended_at TEXT,
                state TEXT NOT NULL, last_line INTEGER NOT NULL, runtime_seconds REAL NOT NULL, recoverable INTEGER NOT NULL,
                feedback_pending INTEGER NOT NULL, feedback BLOB, feedback_at TEXT, acknowledged INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_runs_job ON runs(job_id, started_at);
            CREATE TABLE IF NOT EXISTS run_changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL, kind TEXT NOT NULL, at TEXT NOT NULL, line INTEGER NOT NULL, value REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_run_changes_run ON run_changes(run_id, id);
            CREATE TABLE IF NOT EXISTS pairings (
                id TEXT PRIMARY KEY, client_name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, paired_at TEXT NOT NULL, last_seen_at TEXT);
            """);
    }

    public string? GetMeta(string key) =>
        Query("SELECT value FROM meta WHERE key = $key", p => p.AddWithValue("$key", key), r => r.IsDBNull(0) ? null : r.GetString(0)).FirstOrDefault();

    public void SetMeta(string key, string? value) =>
        Execute("INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value", p =>
        {
            p.AddWithValue("$key", key);
            p.AddWithValue("$value", (object?)value ?? DBNull.Value);
        });

    public void InsertJob(JobRow job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Execute("INSERT INTO jobs (id, sha256, header, line_count, uploaded_at) VALUES ($id, $sha, $header, $lines, $at)", p =>
        {
            p.AddWithValue("$id", job.Id);
            p.AddWithValue("$sha", job.Header.Sha256);
            p.AddWithValue("$header", job.Header.ToByteArray());
            p.AddWithValue("$lines", job.LineCount);
            p.AddWithValue("$at", Text(job.UploadedAt));
        });
    }

    public void UpdateJobHeader(string id, UploadHeader header) =>
        Execute("UPDATE jobs SET header = $header WHERE id = $id", p =>
        {
            p.AddWithValue("$id", id);
            p.AddWithValue("$header", header.ToByteArray());
        });

    public JobRow? GetJob(string id) =>
        Query("SELECT id, header, line_count, uploaded_at FROM jobs WHERE id = $id", p => p.AddWithValue("$id", id), ReadJob).FirstOrDefault();

    public JobRow? FindJobBySha256(string sha256) =>
        Query("SELECT id, header, line_count, uploaded_at FROM jobs WHERE sha256 = $sha ORDER BY uploaded_at DESC LIMIT 1",
            p => p.AddWithValue("$sha", sha256), ReadJob).FirstOrDefault();

    public IReadOnlyList<JobRow> ListJobs() =>
        Query("SELECT id, header, line_count, uploaded_at FROM jobs ORDER BY uploaded_at DESC", null, ReadJob);

    public void DeleteJob(string id) => Execute("DELETE FROM jobs WHERE id = $id", p => p.AddWithValue("$id", id));

    public void InsertRun(RunRow run) => WriteRun(run, insert: true);

    public void UpdateRun(RunRow run) => WriteRun(run, insert: false);

    public RunRow? GetRun(string id) =>
        Query($"SELECT {RunColumns} FROM runs WHERE id = $id", p => p.AddWithValue("$id", id), ReadRun).FirstOrDefault();

    public IReadOnlyList<RunRow> ListRuns(string? jobId = null, int limit = 500) =>
        Query($"SELECT {RunColumns} FROM runs WHERE ($job IS NULL OR job_id = $job) ORDER BY started_at DESC LIMIT $limit", p =>
        {
            p.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
            p.AddWithValue("$limit", limit);
        }, ReadRun);

    /// <summary>Most recent run of each job (for the job list).</summary>
    public IReadOnlyDictionary<string, RunRow> LatestRunPerJob() =>
        Query($"SELECT {RunColumns} FROM runs r WHERE started_at = (SELECT MAX(started_at) FROM runs WHERE job_id = r.job_id)", null, ReadRun)
            .GroupBy(r => r.JobId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Runs that never reached an end state (the daemon stopped while they ran).</summary>
    public IReadOnlyList<RunRow> UnfinishedRuns() =>
        Query($"SELECT {RunColumns} FROM runs WHERE ended_at IS NULL", null, ReadRun);

    public RunRow? LatestRecoverableRun() =>
        Query($"SELECT {RunColumns} FROM runs WHERE recoverable = 1 ORDER BY started_at DESC LIMIT 1", null, ReadRun).FirstOrDefault();

    public RunRow? LatestFeedbackPendingRun() =>
        Query($"SELECT {RunColumns} FROM runs WHERE feedback_pending = 1 ORDER BY ended_at DESC LIMIT 1", null, ReadRun).FirstOrDefault();

    /// <summary>Finished runs whose feedback was submitted or skipped.</summary>
    public IReadOnlyList<RunRow> ListFeedback(bool includeAcknowledged) =>
        Query($"SELECT {RunColumns} FROM runs WHERE ended_at IS NOT NULL AND recoverable = 0 AND feedback_pending = 0 AND feedback IS NOT NULL"
            + " AND ($all = 1 OR acknowledged = 0) ORDER BY ended_at", p => p.AddWithValue("$all", includeAcknowledged ? 1 : 0), ReadRun);

    public int Acknowledge(IEnumerable<string> runIds)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        var count = 0;
        foreach (var id in runIds)
        {
            count += Execute("UPDATE runs SET acknowledged = 1 WHERE id = $id AND feedback IS NOT NULL", p => p.AddWithValue("$id", id));
        }

        return count;
    }

    public void AddChange(string runId, string kind, RunChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Execute("INSERT INTO run_changes (run_id, kind, at, line, value) VALUES ($run, $kind, $at, $line, $value)", p =>
        {
            p.AddWithValue("$run", runId);
            p.AddWithValue("$kind", kind);
            p.AddWithValue("$at", Text(change.At));
            p.AddWithValue("$line", change.Line);
            p.AddWithValue("$value", change.Value);
        });
    }

    public IReadOnlyList<RunChange> GetChanges(string runId, string kind) =>
        Query("SELECT at, line, value FROM run_changes WHERE run_id = $run AND kind = $kind ORDER BY id", p =>
        {
            p.AddWithValue("$run", runId);
            p.AddWithValue("$kind", kind);
        }, r => new RunChange(Time(r.GetString(0)), r.GetInt32(1), r.GetDouble(2)));

    public void InsertPairing(PairingRow pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        Execute("INSERT INTO pairings (id, client_name, token_hash, paired_at, last_seen_at) VALUES ($id, $name, $hash, $at, NULL)", p =>
        {
            p.AddWithValue("$id", pairing.Id);
            p.AddWithValue("$name", pairing.ClientName);
            p.AddWithValue("$hash", pairing.TokenHash);
            p.AddWithValue("$at", Text(pairing.PairedAt));
        });
    }

    public PairingRow? FindPairingByTokenHash(string tokenHash) =>
        Query("SELECT id, client_name, token_hash, paired_at, last_seen_at FROM pairings WHERE token_hash = $hash",
            p => p.AddWithValue("$hash", tokenHash), ReadPairing).FirstOrDefault();

    public IReadOnlyList<PairingRow> ListPairings() =>
        Query("SELECT id, client_name, token_hash, paired_at, last_seen_at FROM pairings ORDER BY paired_at", null, ReadPairing);

    public bool DeletePairing(string id) => Execute("DELETE FROM pairings WHERE id = $id", p => p.AddWithValue("$id", id)) > 0;

    public void TouchPairing(string id, DateTimeOffset at) =>
        Execute("UPDATE pairings SET last_seen_at = $at WHERE id = $id", p =>
        {
            p.AddWithValue("$id", id);
            p.AddWithValue("$at", Text(at));
        });

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            connection.Dispose();
        }
    }

    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? OptionalTime(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Time(reader.GetString(ordinal));

    private static JobRow ReadJob(SqliteDataReader r) =>
        new(r.GetString(0), UploadHeader.Parser.ParseFrom((byte[])r[1]), r.GetInt32(2), Time(r.GetString(3)));

    private static RunRow ReadRun(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        JobId = r.GetString(1),
        Header = UploadHeader.Parser.ParseFrom((byte[])r[2]),
        StartedAt = Time(r.GetString(3)),
        EndedAt = OptionalTime(r, 4),
        State = r.GetString(5),
        LastLine = r.GetInt32(6),
        RuntimeSeconds = r.GetDouble(7),
        Recoverable = r.GetInt32(8) != 0,
        FeedbackPending = r.GetInt32(9) != 0,
        Feedback = r.IsDBNull(10) ? null : FeedbackSubmission.Parser.ParseFrom((byte[])r[10]),
        FeedbackAt = OptionalTime(r, 11),
        Acknowledged = r.GetInt32(12) != 0,
    };

    private static PairingRow ReadPairing(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), Time(r.GetString(3)), OptionalTime(r, 4));

    private void WriteRun(RunRow run, bool insert)
    {
        ArgumentNullException.ThrowIfNull(run);
        var sql = insert
            ? $"INSERT INTO runs ({RunColumns}) VALUES ($id, $job, $header, $started, $ended, $state, $line, $runtime, $recoverable, $pending, $feedback, $feedbackAt, $ack)"
            : "UPDATE runs SET job_id = $job, header = $header, started_at = $started, ended_at = $ended, state = $state, last_line = $line,"
                + " runtime_seconds = $runtime, recoverable = $recoverable, feedback_pending = $pending, feedback = $feedback,"
                + " feedback_at = $feedbackAt, acknowledged = $ack WHERE id = $id";
        Execute(sql, p =>
        {
            p.AddWithValue("$id", run.Id);
            p.AddWithValue("$job", run.JobId);
            p.AddWithValue("$header", run.Header.ToByteArray());
            p.AddWithValue("$started", Text(run.StartedAt));
            p.AddWithValue("$ended", run.EndedAt is { } ended ? Text(ended) : DBNull.Value);
            p.AddWithValue("$state", run.State);
            p.AddWithValue("$line", run.LastLine);
            p.AddWithValue("$runtime", run.RuntimeSeconds);
            p.AddWithValue("$recoverable", run.Recoverable ? 1 : 0);
            p.AddWithValue("$pending", run.FeedbackPending ? 1 : 0);
            p.AddWithValue("$feedback", run.Feedback is { } feedback ? feedback.ToByteArray() : DBNull.Value);
            p.AddWithValue("$feedbackAt", run.FeedbackAt is { } at ? Text(at) : DBNull.Value);
            p.AddWithValue("$ack", run.Acknowledged ? 1 : 0);
        });
    }

    private int Execute(string sql, Action<SqliteParameterCollection>? parameters = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            parameters?.Invoke(command.Parameters);
            return command.ExecuteNonQuery();
        }
    }

    private List<T> Query<T>(string sql, Action<SqliteParameterCollection>? parameters, Func<SqliteDataReader, T> read)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            parameters?.Invoke(command.Parameters);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read())
            {
                result.Add(read(reader));
            }

            return result;
        }
    }
}
