using Microsoft.Data.Sqlite;
using Squeue.Core.FileSystem;

namespace Squeue.Core.State;

/// The operation journal. Every method commits before it returns (synchronous = FULL),
/// so anything the copier does after a call can rely on that state surviving a crash.
/// Not thread-safe: use one Journal from one thread at a time.
public sealed class Journal : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS jobs (
          id         INTEGER PRIMARY KEY,
          hash_algo  TEXT,
          name       TEXT,
          source     TEXT,
          dest_root  TEXT,
          state      TEXT NOT NULL DEFAULT 'Queued',
          created_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS entries (
          id              INTEGER PRIMARY KEY,
          job_id          INTEGER NOT NULL REFERENCES jobs(id),
          src_path        TEXT NOT NULL,
          dest_path       TEXT NOT NULL,
          planned_size    INTEGER NOT NULL,
          conflict_action TEXT NOT NULL,
          seen_dest       BLOB,
          src_version     BLOB,
          state           TEXT NOT NULL,
          src_hash        TEXT,
          dest_hash       TEXT,
          verify_failures INTEGER NOT NULL DEFAULT 0,
          error           TEXT
        );
        CREATE TABLE IF NOT EXISTS attempts (
          id                INTEGER PRIMARY KEY,
          entry_id          INTEGER NOT NULL REFERENCES entries(id),
          phase             TEXT NOT NULL,
          temp_name         TEXT NOT NULL,
          temp_file_id      BLOB,
          replaced_file_id  BLOB,
          published_file_id BLOB,
          started_at        INTEGER NOT NULL,
          ended_at          INTEGER
        );
        CREATE INDEX IF NOT EXISTS attempts_open ON attempts(phase) WHERE phase NOT IN ('Finished', 'Abandoned');
        CREATE INDEX IF NOT EXISTS entries_job ON entries(job_id);
        """;

    private readonly SqliteConnection _db;
    private SqliteTransaction? _transaction;

    /// Commits made on this connection; each one is a disk sync (synchronous = FULL). Used by tests and benchmarks.
    internal long CommitCount { get; private set; }

    private Journal(SqliteConnection db) => _db = db;

    public static Journal Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        var journal = new Journal(db);
        journal.Exec("PRAGMA journal_mode = WAL;");
        journal.Exec("PRAGMA synchronous = FULL;");
        journal.Exec("PRAGMA foreign_keys = ON;");
        journal.Exec(Schema);
        journal.Migrate();
        return journal;
    }

    /// Runs several journal writes as one transaction: all commit together or none do. Nested calls join the outer one.
    public void Atomically(Action writes)
    {
        if (_transaction is not null) { writes(); return; }
        _transaction = _db.BeginTransaction();
        try
        {
            writes();
            _transaction.Commit();
            CommitCount++;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public long CreateJob(string? hashAlgorithm, string? name = null, string? source = null, string? destRoot = null) =>
        Insert("""
            INSERT INTO jobs (hash_algo, name, source, dest_root, state, created_at)
            VALUES ($hash, $name, $source, $dest, 'Queued', $now)
            """,
            ("$hash", hashAlgorithm), ("$name", name), ("$source", source), ("$dest", destRoot), ("$now", Now()));

    public void SetJobState(long jobId, JobState state) =>
        Exec("UPDATE jobs SET state = $s WHERE id = $id", ("$s", state.ToString()), ("$id", jobId));

    public JobRow GetJob(long jobId) =>
        ReadJobs("WHERE id = $id", ("$id", jobId)).SingleOrDefault() ?? throw new KeyNotFoundException($"No job {jobId}.");

    public IReadOnlyList<JobRow> Jobs() => ReadJobs("ORDER BY id");

    public IReadOnlyList<EntryRow> EntriesOf(long jobId)
    {
        using var cmd = Command("SELECT id, src_path, planned_size, state, error FROM entries WHERE job_id = $job ORDER BY id",
            ("$job", jobId));
        using var r = cmd.ExecuteReader();
        var rows = new List<EntryRow>();
        while (r.Read())
            rows.Add(new EntryRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), Enum.Parse<EntryState>(r.GetString(3)), Text(r, 4)));
        return rows;
    }

    public long AddEntry(long jobId, string srcPath, string destPath, long plannedSize, ConflictAction action, FileIdentity? seenDest) =>
        Insert("""
            INSERT INTO entries (job_id, src_path, dest_path, planned_size, conflict_action, seen_dest, state)
            VALUES ($job, $src, $dest, $size, $action, $seen, 'Pending')
            """,
            ("$job", jobId), ("$src", srcPath), ("$dest", destPath), ("$size", plannedSize),
            ("$action", action.ToString()), ("$seen", IdentityBlob.Encode(seenDest)));

    public Entry GetEntry(long entryId)
    {
        using var cmd = Command("""
            SELECT e.id, e.job_id, e.src_path, e.dest_path, e.planned_size, e.conflict_action, e.seen_dest, e.state,
                   j.hash_algo, e.src_version, e.src_hash, e.dest_hash, e.verify_failures, e.error
            FROM entries e JOIN jobs j ON j.id = e.job_id
            WHERE e.id = $id
            """, ("$id", entryId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException($"No entry {entryId}.");
        return new Entry(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            Enum.Parse<ConflictAction>(r.GetString(5)), IdentityBlob.Decode(Blob(r, 6)), Enum.Parse<EntryState>(r.GetString(7)),
            Text(r, 8), IdentityBlob.Decode(Blob(r, 9)), Text(r, 10), Text(r, 11), r.GetInt32(12), Text(r, 13));
    }

    public void RecordSourceVersion(long entryId, FileIdentity version) =>
        Exec("UPDATE entries SET src_version = $v WHERE id = $id", ("$v", IdentityBlob.Encode(version)), ("$id", entryId));

    public void SetEntryState(long entryId, EntryState state, string? error = null) =>
        Exec("UPDATE entries SET state = $s, error = $e WHERE id = $id", ("$s", state.ToString()), ("$e", error), ("$id", entryId));

    public void ClearHashes(long entryId) =>
        Exec("UPDATE entries SET src_hash = NULL, dest_hash = NULL WHERE id = $id", ("$id", entryId));

    public void SetHashes(long entryId, string? srcHash, string? destHash) =>
        Exec("UPDATE entries SET src_hash = COALESCE($src, src_hash), dest_hash = COALESCE($dest, dest_hash) WHERE id = $id",
            ("$src", srcHash), ("$dest", destHash), ("$id", entryId));

    /// Counts a verification failure and clears both hashes. With a target, the next attempt replaces that
    /// (already published, but unverified) file instead of creating a new one.
    public int RecordVerifyFailure(long entryId, FileIdentity? newReplaceTarget)
    {
        Exec("""
            UPDATE entries
            SET verify_failures = verify_failures + 1, src_hash = NULL, dest_hash = NULL,
                conflict_action = CASE WHEN $target IS NULL THEN conflict_action ELSE 'Replace' END,
                seen_dest = COALESCE($target, seen_dest)
            WHERE id = $id
            """, ("$target", IdentityBlob.Encode(newReplaceTarget)), ("$id", entryId));
        return GetEntry(entryId).VerifyFailures;
    }

    public long BeginAttempt(long entryId, string tempName, UInt128? replacedFileId) =>
        Insert("""
            INSERT INTO attempts (entry_id, phase, temp_name, replaced_file_id, started_at)
            VALUES ($entry, 'Intent', $temp, $replaced, $now)
            """, ("$entry", entryId), ("$temp", tempName), ("$replaced", IdBytes(replacedFileId)), ("$now", Now()));

    public void SetPhase(long attemptId, AttemptPhase phase, UInt128? tempFileId = null, UInt128? publishedFileId = null) =>
        Exec("""
            UPDATE attempts
            SET phase = $phase,
                temp_file_id = COALESCE($temp, temp_file_id),
                published_file_id = COALESCE($pub, published_file_id),
                ended_at = CASE WHEN $phase IN ('Finished', 'Abandoned') THEN $now ELSE ended_at END
            WHERE id = $id
            """, ("$phase", phase.ToString()), ("$temp", IdBytes(tempFileId)), ("$pub", IdBytes(publishedFileId)),
            ("$now", Now()), ("$id", attemptId));

    public Attempt? GetOpenAttempt(long entryId) =>
        ReadAttempts("WHERE entry_id = $entry AND phase NOT IN ('Finished', 'Abandoned') ORDER BY id DESC LIMIT 1",
            ("$entry", entryId)).FirstOrDefault();

    public IReadOnlyList<Attempt> OpenAttempts() =>
        ReadAttempts("WHERE phase NOT IN ('Finished', 'Abandoned') ORDER BY id");

    public IReadOnlyList<Attempt> AttemptsFor(long entryId) =>
        ReadAttempts("WHERE entry_id = $entry ORDER BY id", ("$entry", entryId));

    internal string Pragma(string name)
    {
        using var cmd = Command($"PRAGMA {name}");
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    public void Dispose() => _db.Dispose();

    private List<JobRow> ReadJobs(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command("SELECT id, name, source, dest_root, state, hash_algo, created_at FROM jobs " + where, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<JobRow>();
        while (r.Read())
        {
            rows.Add(new JobRow(r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3),
                Enum.Parse<JobState>(r.GetString(4)), Text(r, 5), r.GetInt64(6)));
        }
        return rows;
    }

    /// Adds columns that databases created by earlier builds don't have yet.
    private void Migrate()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Command("PRAGMA table_info(jobs)"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) columns.Add(r.GetString(1));
        }
        if (!columns.Contains("name")) Exec("ALTER TABLE jobs ADD COLUMN name TEXT");
        if (!columns.Contains("source")) Exec("ALTER TABLE jobs ADD COLUMN source TEXT");
        if (!columns.Contains("dest_root")) Exec("ALTER TABLE jobs ADD COLUMN dest_root TEXT");
        if (!columns.Contains("state")) Exec("ALTER TABLE jobs ADD COLUMN state TEXT NOT NULL DEFAULT 'Queued'");
    }

    private List<Attempt> ReadAttempts(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(
            "SELECT id, entry_id, phase, temp_name, temp_file_id, replaced_file_id, published_file_id, started_at FROM attempts " + where,
            args);
        using var r = cmd.ExecuteReader();
        var attempts = new List<Attempt>();
        while (r.Read())
        {
            attempts.Add(new Attempt(r.GetInt64(0), r.GetInt64(1), Enum.Parse<AttemptPhase>(r.GetString(2)), r.GetString(3),
                Id(Blob(r, 4)), Id(Blob(r, 5)), Id(Blob(r, 6)), r.GetInt64(7)));
        }
        return attempts;
    }

    private static long Now() => DateTime.UtcNow.ToFileTimeUtc();
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static byte[]? Blob(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i);
    private static UInt128? Id(byte[]? bytes) => bytes is null ? null : FileIdentity.FileIdFromBytes(bytes);
    private static byte[]? IdBytes(UInt128? id) => id is null ? null : FileIdentity.FileIdToBytes(id.Value);

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
        if (_transaction is null) CommitCount++;
    }

    private long Insert(string sql, params (string Name, object? Value)[] args)
    {
        Exec(sql, args);
        using var cmd = Command("SELECT last_insert_rowid()");
        return (long)cmd.ExecuteScalar()!;
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _transaction;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
