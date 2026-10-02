using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WattScope.Storage;

/// <summary>What deleting old history would remove -- shown to the user before anything is deleted.</summary>
public sealed record HistoryDeletionPreview
{
    public required DateTimeOffset CutoffUtc { get; init; }
    public required int Sessions { get; init; }
    public required long EnergyBuckets { get; init; }
    public required long PowerSamples { get; init; }
    public required long Gaps { get; init; }
    public required int GamingSessions { get; init; }
    public DateTimeOffset? OldestStartedUtc { get; init; }
    public DateTimeOffset? NewestEndedUtc { get; init; }
}

public sealed record MaintenanceResult
{
    public required int RawSamplesPruned { get; init; }
    public required long FreePagesBefore { get; init; }
    public required long FreePagesAfter { get; init; }
    public required long BytesBefore { get; init; }
    public required long BytesAfter { get; init; }
}

public sealed partial class SqliteEnergyStore
{
    /// <summary>Pages returned to the file system per incremental-vacuum step; small so a writer on another connection is never blocked for long.</summary>
    private const int VacuumStepPages = 2048;

    // ---------------------------------------------------------------- generic application settings

    public string? GetAppSetting(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    public void SetAppSetting(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO app_setting (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- maintenance

    /// <summary>Returns a bounded number of free pages to the file system (never a full VACUUM, which would rewrite the whole file).</summary>
    private void ReturnFreePages() => Execute(_connection, $"PRAGMA incremental_vacuum({VacuumStepPages});");

    /// <summary>
    /// Routine, user-triggered housekeeping: prune raw samples past retention,
    /// return free pages to the file system in small steps and checkpoint the
    /// WAL without blocking writers (PASSIVE). It never rewrites the whole file
    /// and never touches energy totals, prices, settings or mappings.
    /// </summary>
    public MaintenanceResult RunMaintenance(DateTimeOffset nowUtc)
    {
        var before = GetStatistics();
        var pruned = PruneRawSamples(nowUtc);
        var guard = 0;
        while (Scalar<long>(_connection, "PRAGMA freelist_count;") > 0 && guard++ < 4096)
            ReturnFreePages();
        Execute(_connection, "PRAGMA wal_checkpoint(PASSIVE);");
        var after = GetStatistics();
        return new MaintenanceResult
        {
            RawSamplesPruned = pruned,
            FreePagesBefore = before.FreeBytes / PageSize(),
            FreePagesAfter = after.FreeBytes / PageSize(),
            BytesBefore = before.DatabaseBytes + before.WalBytes,
            BytesAfter = after.DatabaseBytes + after.WalBytes
        };
    }

    private long PageSize() => Scalar<long>(_connection, "PRAGMA page_size;");

    /// <summary>Quick structural check (much cheaper than a full integrity check on a large file); "ok" when healthy.</summary>
    public string QuickCheck() => Scalar<string>(_connection, "PRAGMA quick_check;") ?? "no result";

    // ---------------------------------------------------------------- history deletion

    /// <summary>A session is eligible when it is not Active and ended before the cutoff.</summary>
    private const string EligibleSessionsSql = """
        SELECT id, started_utc_ms, COALESCE(ended_utc_ms, last_observation_utc_ms, started_utc_ms) AS end_ms
        FROM session
        WHERE status <> 0 AND COALESCE(ended_utc_ms, last_observation_utc_ms, started_utc_ms) < $cutoff
        """;

    private List<(long Id, long StartMs, long EndMs)> EligibleSessions(DateTimeOffset cutoffUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = EligibleSessionsSql + " ORDER BY id;";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var list = new List<(long, long, long)>();
        while (reader.Read())
            list.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        return list;
    }

    /// <summary>Counts what <see cref="DeleteHistoryBefore"/> would remove. Read-only.</summary>
    public HistoryDeletionPreview PreviewHistoryDeletion(DateTimeOffset cutoffUtc)
    {
        var sessions = EligibleSessions(cutoffUtc);
        long buckets = 0, samples = 0, gaps = 0, gaming = 0;
        foreach (var (id, startMs, endMs) in sessions)
        {
            buckets += CountFor("SELECT count(*) FROM energy_bucket WHERE session_id = $id;", id);
            gaps += CountFor("SELECT count(*) FROM monitoring_gap WHERE session_id = $id;", id);
            gaming += CountFor("SELECT count(*) FROM gaming_session WHERE monitoring_session_id = $id;", id);
            samples += CountFor("SELECT count(*) FROM power_sample WHERE session_id = $id AND utc_ms BETWEEN $a AND $b;", id, startMs, endMs);
        }
        return new HistoryDeletionPreview
        {
            CutoffUtc = cutoffUtc,
            Sessions = sessions.Count,
            EnergyBuckets = buckets,
            PowerSamples = samples,
            Gaps = gaps,
            GamingSessions = (int)gaming,
            OldestStartedUtc = sessions.Count > 0 ? FromMs(sessions.Min(s => s.StartMs)) : null,
            NewestEndedUtc = sessions.Count > 0 ? FromMs(sessions.Max(s => s.EndMs)) : null
        };
    }

    private long CountFor(string sql, long id, long? from = null, long? to = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        if (from is { } a) command.Parameters.AddWithValue("$a", a);
        if (to is { } b) command.Parameters.AddWithValue("$b", b);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Permanently deletes finished monitoring sessions that ended before the cutoff, with everything
    /// recorded for them (energy buckets, price splits, gaps, availability, raw samples, sensors and the
    /// gaming sessions belonging to them). Never touches an Active session, electricity prices,
    /// estimation or application settings, or game mappings. One transaction per session, so the writer
    /// lock is held only briefly and a failure part-way leaves every remaining session whole.
    /// </summary>
    public HistoryDeletionPreview DeleteHistoryBefore(DateTimeOffset cutoffUtc)
    {
        var deleted = PreviewHistoryDeletion(cutoffUtc);
        foreach (var (id, startMs, endMs) in EligibleSessions(cutoffUtc))
        {
            using var transaction = _connection.BeginTransaction();
            // Children first. Gaming energy rows go with their gaming session (ON DELETE CASCADE).
            Delete(transaction, "DELETE FROM gaming_session WHERE monitoring_session_id = $id AND status <> 0;", id);
            Delete(transaction, "DELETE FROM bucket_price_split WHERE session_id = $id;", id);
            Delete(transaction, "DELETE FROM energy_bucket WHERE session_id = $id;", id);
            Delete(transaction, "DELETE FROM monitoring_gap WHERE session_id = $id;", id);
            Delete(transaction, "DELETE FROM sensor_availability WHERE session_id = $id;", id);
            Delete(transaction, "DELETE FROM session_sensor WHERE session_id = $id;", id);
            Delete(transaction, "DELETE FROM power_sample WHERE session_id = $id AND utc_ms BETWEEN $a AND $b;", id, startMs, endMs);
            Delete(transaction, "DELETE FROM session WHERE id = $id AND status <> 0;", id);
            transaction.Commit();
        }
        ReturnFreePages();
        _logger?.LogInformation("Deleted {Sessions} finished session(s) that ended before {Cutoff:O}", deleted.Sessions, cutoffUtc);
        return deleted;
    }

    private void Delete(SqliteTransaction transaction, string sql, long id, long? from = null, long? to = null)
    {
        using var command = Command(transaction, sql);
        command.Parameters.AddWithValue("$id", id);
        if (from is { } a) command.Parameters.AddWithValue("$a", a);
        if (to is { } b) command.Parameters.AddWithValue("$b", b);
        command.ExecuteNonQuery();
    }
}
