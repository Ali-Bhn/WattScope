using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;

namespace WattScope.Storage;

public sealed record StoredPowerSample(DateTimeOffset UtcTime, double? CpuPackageWatts, ReadingState CpuPackageState, double? GpuBoardWatts, ReadingState GpuBoardState);

public sealed record AvailabilityPeriod(EnergyChannel Channel, ReadingState State, DateTimeOffset FromUtc, DateTimeOffset? ToUtc);

public sealed record StorageStatistics
{
    public required int SchemaVersion { get; init; }
    public required long DatabaseBytes { get; init; }
    public required long WalBytes { get; init; }
    public required long FreeBytes { get; init; }
    public required long Sessions { get; init; }
    public required long PowerSamples { get; init; }
    public required long EnergyBuckets { get; init; }
    public required long PriceSplitRows { get; init; }
    public required long Gaps { get; init; }
    public required long AvailabilityRows { get; init; }
    public required long Prices { get; init; }
    public required int RawRetentionDays { get; init; }
    public DateTimeOffset? OldestSampleUtc { get; init; }
    public DateTimeOffset? NewestSampleUtc { get; init; }
}

/// <summary>
/// SQLite persistence for sessions, selected power samples, energy
/// aggregates, gaps, sensor availability, prices and estimation settings.
/// One connection per instance; not thread-safe (callers serialize, as
/// <see cref="EnergyMonitor"/> does). WAL journaling with synchronous=NORMAL:
/// committed transactions survive a process crash; the most recent commits
/// may be lost on an OS crash or power loss. See docs/STORAGE.md.
/// </summary>
public sealed class SqliteEnergyStore : IEnergyStore, IDisposable
{
    public const int DefaultRawRetentionDays = 7;
    private const string RawRetentionKey = "raw_retention_days";

    /// <summary>Stored in place of NULL for an unpriced price-split segment; WITHOUT ROWID primary key columns cannot be NULL.</summary>
    private const long UnpricedSentinel = -1;

    /// <summary>In-memory equivalent of <see cref="UnpricedSentinel"/> for dictionaries keyed by price time (real price effective times are never this old).</summary>
    private static readonly DateTimeOffset UnpricedSentinelKey = DateTimeOffset.MinValue;

    private readonly SqliteConnection _connection;
    private readonly ILogger? _logger;

    private SqliteEnergyStore(string path, SqliteConnection connection, ILogger? logger)
    {
        DatabasePath = path;
        _connection = connection;
        _logger = logger;
    }

    public string DatabasePath { get; }

    public static string DefaultDatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattScope", "wattscope.db");

    /// <summary>Opens (creating if needed) and migrates the database.</summary>
    public static SqliteEnergyStore Open(string path, ILogger? logger = null)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());

        try
        {
            connection.Open();
            var isNew = SchemaMigrations.GetVersion(connection) == 0 && Scalar<long>(connection, "SELECT count(*) FROM sqlite_master;") == 0;
            // auto_vacuum can only be switched before the first table exists; INCREMENTAL
            // lets retention pruning actually return space to the file system.
            if (isNew)
                Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL;");
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA synchronous = NORMAL;");
            Execute(connection, "PRAGMA foreign_keys = ON;");
            SchemaMigrations.Apply(connection, logger);
            return new SqliteEnergyStore(fullPath, connection, logger);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int SchemaVersion => SchemaMigrations.GetVersion(_connection);

    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- sessions

    public MonitoringSession CreateSession(NewSessionRequest request)
    {
        request.Estimation.Validate();
        using var transaction = _connection.BeginTransaction();
        long id;
        try
        {
            using (var command = Command(transaction, """
                INSERT INTO session (started_utc_ms, status, est_other_watts, est_psu_efficiency, est_include_cpu, est_include_gpu, process_elevated, app_version)
                VALUES ($started, $status, $other, $eff, $cpu, $gpu, $elevated, $version)
                RETURNING id;
                """))
            {
                command.Parameters.AddWithValue("$started", request.StartedUtc.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$status", (int)SessionStatus.Active);
                command.Parameters.AddWithValue("$other", request.Estimation.OtherComponentsWatts);
                command.Parameters.AddWithValue("$eff", request.Estimation.PsuEfficiency);
                command.Parameters.AddWithValue("$cpu", request.Estimation.IncludeCpuPackage);
                command.Parameters.AddWithValue("$gpu", request.Estimation.IncludeGpuBoard);
                command.Parameters.AddWithValue("$elevated", (object?)request.ProcessElevated ?? DBNull.Value);
                command.Parameters.AddWithValue("$version", (object?)request.AppVersion ?? DBNull.Value);
                id = Convert.ToInt64(command.ExecuteScalar());
            }
        }
        catch (SqliteException ex) when (IsSingleActiveViolation(ex))
        {
            throw new ActiveSessionExistsException(ex);
        }
        InsertSensors(transaction, id, request.Sensors);
        transaction.Commit();
        return GetSession(id)!;
    }

    private static bool IsSingleActiveViolation(SqliteException ex) =>
        ex.SqliteErrorCode == 19 && ex.Message.Contains("session.status", StringComparison.OrdinalIgnoreCase);

    public MonitoringSession? GetSession(long sessionId) =>
        QuerySessions("WHERE id = $id", c => c.Parameters.AddWithValue("$id", sessionId)).SingleOrDefault();

    public MonitoringSession? GetLatestSession() =>
        QuerySessions("ORDER BY id DESC LIMIT 1", _ => { }).SingleOrDefault();

    public IReadOnlyList<MonitoringSession> ListSessions(int limit = 20) =>
        QuerySessions("ORDER BY id DESC LIMIT $limit", c => c.Parameters.AddWithValue("$limit", limit));

    public IReadOnlyList<MonitoringSession> GetSessions(IEnumerable<long> ids) =>
        ids.Distinct().Select(GetSession).Where(s => s is not null).Select(s => s!).ToList();

    public MonitoringSession ReopenSession(long sessionId, IReadOnlyList<EnergySensorBinding> sensors)
    {
        var before = GetSession(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
        if (before.Status == SessionStatus.Active)
            throw new InvalidOperationException($"Session {sessionId} is already active.");

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = Command(transaction, "UPDATE session SET status = $status, ended_utc_ms = NULL, end_reason = NULL WHERE id = $id;");
            command.Parameters.AddWithValue("$status", (int)SessionStatus.Active);
            command.Parameters.AddWithValue("$id", sessionId);
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsSingleActiveViolation(ex))
        {
            throw new ActiveSessionExistsException(ex);
        }
        InsertSensors(transaction, sessionId, sensors);
        transaction.Commit();
        return before;
    }

    public void EndSession(long sessionId, DateTimeOffset endedUtc, SessionStatus status, string reason)
    {
        using var transaction = _connection.BeginTransaction();
        EndSessionCore(transaction, sessionId, endedUtc.ToUnixTimeMilliseconds(), status, reason);
        transaction.Commit();
    }

    /// <summary>
    /// Marks sessions left Active by a process that died as Interrupted. The
    /// session ends at its last persisted observation -- nothing after that is
    /// assumed. The caller must hold the <see cref="MonitorLock"/>, otherwise
    /// a live recording would be mistaken for a crashed one.
    /// </summary>
    public IReadOnlyList<MonitoringSession> RecoverInterruptedSessions()
    {
        var orphaned = QuerySessions("WHERE status = $status", c => c.Parameters.AddWithValue("$status", (int)SessionStatus.Active));
        if (orphaned.Count == 0)
            return [];

        using var transaction = _connection.BeginTransaction();
        foreach (var session in orphaned)
        {
            var end = (session.LastObservationUtc ?? session.StartedUtc).ToUnixTimeMilliseconds();
            EndSessionCore(transaction, session.Id, end, SessionStatus.Interrupted, "Recovered: the recording process ended without stopping the session.");
            _logger?.LogWarning("Recovered interrupted session {SessionId}", session.Id);
        }
        transaction.Commit();
        return orphaned.Select(s => GetSession(s.Id)!).ToList();
    }

    private void EndSessionCore(SqliteTransaction transaction, long sessionId, long endedMs, SessionStatus status, string reason)
    {
        using (var command = Command(transaction, "UPDATE session SET status = $status, ended_utc_ms = $ended, end_reason = $reason WHERE id = $id;"))
        {
            command.Parameters.AddWithValue("$status", (int)status);
            command.Parameters.AddWithValue("$ended", endedMs);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$id", sessionId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Session {sessionId} does not exist.");
        }

        // Close availability runs at the last observation: states are not asserted beyond it.
        using (var command = Command(transaction, """
            UPDATE sensor_availability
            SET to_utc_ms = COALESCE((SELECT last_observation_utc_ms FROM session WHERE id = $id), $ended)
            WHERE session_id = $id AND to_utc_ms IS NULL;
            """))
        {
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$ended", endedMs);
            command.ExecuteNonQuery();
        }
    }

    private void InsertSensors(SqliteTransaction transaction, long sessionId, IReadOnlyList<EnergySensorBinding> sensors)
    {
        using var command = Command(transaction, """
            INSERT OR IGNORE INTO session_sensor (session_id, channel, provider_id, sensor_id, hardware_id, hardware_name, sensor_name)
            VALUES ($session, $channel, $provider, $sensor, $hardware, $hardwareName, $sensorName);
            """);
        var session = command.Parameters.Add("$session", SqliteType.Integer);
        var channel = command.Parameters.Add("$channel", SqliteType.Integer);
        var provider = command.Parameters.Add("$provider", SqliteType.Text);
        var sensor = command.Parameters.Add("$sensor", SqliteType.Text);
        var hardware = command.Parameters.Add("$hardware", SqliteType.Text);
        var hardwareName = command.Parameters.Add("$hardwareName", SqliteType.Text);
        var sensorName = command.Parameters.Add("$sensorName", SqliteType.Text);
        foreach (var binding in sensors)
        {
            session.Value = sessionId;
            channel.Value = (int)binding.Channel;
            provider.Value = binding.ProviderId;
            sensor.Value = binding.SensorId;
            hardware.Value = binding.HardwareId;
            hardwareName.Value = binding.HardwareName;
            sensorName.Value = binding.SensorName;
            command.ExecuteNonQuery();
        }
    }

    private List<MonitoringSession> QuerySessions(string clause, Action<SqliteCommand> bind)
    {
        var sessions = new List<MonitoringSession>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, started_utc_ms, ended_utc_ms, last_observation_utc_ms, status, end_reason,
                       est_other_watts, est_psu_efficiency, est_include_cpu, est_include_gpu
                FROM session {clause};
                """;
            bind(command);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new MonitoringSession
                {
                    Id = reader.GetInt64(0),
                    StartedUtc = FromMs(reader.GetInt64(1)),
                    EndedUtc = reader.IsDBNull(2) ? null : FromMs(reader.GetInt64(2)),
                    LastObservationUtc = reader.IsDBNull(3) ? null : FromMs(reader.GetInt64(3)),
                    Status = (SessionStatus)reader.GetInt32(4),
                    EndReason = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Estimation = new EstimationSettings
                    {
                        OtherComponentsWatts = reader.GetDouble(6),
                        PsuEfficiency = reader.GetDouble(7),
                        IncludeCpuPackage = reader.GetBoolean(8),
                        IncludeGpuBoard = reader.GetBoolean(9)
                    },
                    Sensors = []
                });
            }
        }

        return sessions.Select(s => s with { Sensors = GetSensors(s.Id) }).ToList();
    }

    private List<EnergySensorBinding> GetSensors(long sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT channel, provider_id, sensor_id, hardware_id, hardware_name, sensor_name
            FROM session_sensor WHERE session_id = $id ORDER BY channel, provider_id, sensor_id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var result = new List<EnergySensorBinding>();
        while (reader.Read())
        {
            result.Add(new EnergySensorBinding
            {
                Channel = (EnergyChannel)reader.GetInt32(0),
                ProviderId = reader.GetString(1),
                SensorId = reader.GetString(2),
                HardwareId = reader.GetString(3),
                HardwareName = reader.GetString(4),
                SensorName = reader.GetString(5)
            });
        }
        return result;
    }

    // ---------------------------------------------------------------- monitoring data

    public void WriteBatch(EnergyWriteBatch batch)
    {
        using var transaction = _connection.BeginTransaction();

        if (batch.Samples.Count > 0)
        {
            using var command = Command(transaction, """
                INSERT OR IGNORE INTO power_sample (utc_ms, session_id, cpu_mw, gpu_mw, cpu_state, gpu_state)
                VALUES ($t, $s, $cpu, $gpu, $cpuState, $gpuState);
                """);
            var t = command.Parameters.Add("$t", SqliteType.Integer);
            command.Parameters.AddWithValue("$s", batch.SessionId);
            var cpu = command.Parameters.Add("$cpu", SqliteType.Integer);
            var gpu = command.Parameters.Add("$gpu", SqliteType.Integer);
            var cpuState = command.Parameters.Add("$cpuState", SqliteType.Integer);
            var gpuState = command.Parameters.Add("$gpuState", SqliteType.Integer);
            foreach (var sample in batch.Samples)
            {
                t.Value = sample.UtcTime.ToUnixTimeMilliseconds();
                cpu.Value = ToMilliwatts(sample.CpuPackage);
                gpu.Value = ToMilliwatts(sample.GpuBoard);
                cpuState.Value = (int)sample.CpuPackage.State;
                gpuState.Value = (int)sample.GpuBoard.State;
                command.ExecuteNonQuery();
            }
        }

        if (batch.BucketDeltas.Count > 0)
        {
            using var command = Command(transaction, """
                INSERT INTO energy_bucket (session_id, bucket_start_utc_s, monitored_s, cpu_j, cpu_s, gpu_j, gpu_s,
                                           est_complete_j, est_complete_s, est_partial_j, est_partial_s)
                VALUES ($s, $b, $m, $cj, $cs, $gj, $gs, $ecj, $ecs, $epj, $eps)
                ON CONFLICT (session_id, bucket_start_utc_s) DO UPDATE SET
                    monitored_s    = monitored_s    + excluded.monitored_s,
                    cpu_j          = cpu_j          + excluded.cpu_j,
                    cpu_s          = cpu_s          + excluded.cpu_s,
                    gpu_j          = gpu_j          + excluded.gpu_j,
                    gpu_s          = gpu_s          + excluded.gpu_s,
                    est_complete_j = est_complete_j + excluded.est_complete_j,
                    est_complete_s = est_complete_s + excluded.est_complete_s,
                    est_partial_j  = est_partial_j  + excluded.est_partial_j,
                    est_partial_s  = est_partial_s  + excluded.est_partial_s;
                """);
            var p = command.Parameters;
            p.AddWithValue("$s", batch.SessionId);
            var b = p.Add("$b", SqliteType.Integer);
            var m = p.Add("$m", SqliteType.Real);
            var cj = p.Add("$cj", SqliteType.Real);
            var cs = p.Add("$cs", SqliteType.Real);
            var gj = p.Add("$gj", SqliteType.Real);
            var gs = p.Add("$gs", SqliteType.Real);
            var ecj = p.Add("$ecj", SqliteType.Real);
            var ecs = p.Add("$ecs", SqliteType.Real);
            var epj = p.Add("$epj", SqliteType.Real);
            var eps = p.Add("$eps", SqliteType.Real);
            foreach (var bucket in batch.BucketDeltas)
            {
                if (bucket.SessionId != batch.SessionId)
                    throw new ArgumentException("All bucket deltas in a batch must belong to the batch's session.", nameof(batch));
                var x = bucket.Totals;
                b.Value = bucket.BucketStartUtc.ToUnixTimeSeconds();
                m.Value = x.MonitoredSeconds;
                cj.Value = x.CpuPackageJoules;
                cs.Value = x.CpuPackageSeconds;
                gj.Value = x.GpuBoardJoules;
                gs.Value = x.GpuBoardSeconds;
                ecj.Value = x.EstimatedCompleteJoules;
                ecs.Value = x.EstimatedCompleteSeconds;
                epj.Value = x.EstimatedPartialJoules;
                eps.Value = x.EstimatedPartialSeconds;
                command.ExecuteNonQuery();
            }
        }

        if (batch.PriceSplits.Count > 0)
        {
            using var command = Command(transaction, """
                INSERT INTO bucket_price_split (session_id, bucket_start_utc_s, price_effective_utc_ms, complete_j, complete_s, partial_j, partial_s)
                VALUES ($s, $b, $p, $cj, $cs, $pj, $ps)
                ON CONFLICT (session_id, bucket_start_utc_s, price_effective_utc_ms) DO UPDATE SET
                    complete_j = complete_j + excluded.complete_j,
                    complete_s = complete_s + excluded.complete_s,
                    partial_j  = partial_j  + excluded.partial_j,
                    partial_s  = partial_s  + excluded.partial_s;
                """);
            var p = command.Parameters;
            p.AddWithValue("$s", batch.SessionId);
            var b = p.Add("$b", SqliteType.Integer);
            var price = p.Add("$p", SqliteType.Integer);
            var cj = p.Add("$cj", SqliteType.Real);
            var cs = p.Add("$cs", SqliteType.Real);
            var pj = p.Add("$pj", SqliteType.Real);
            var ps = p.Add("$ps", SqliteType.Real);
            foreach (var segment in batch.PriceSplits)
            {
                if (segment.SessionId != batch.SessionId)
                    throw new ArgumentException("All price splits in a batch must belong to the batch's session.", nameof(batch));
                b.Value = segment.BucketStartUtc.ToUnixTimeSeconds();
                price.Value = segment.PriceEffectiveUtc?.ToUnixTimeMilliseconds() ?? UnpricedSentinel;
                cj.Value = segment.Totals.EstimatedCompleteJoules;
                cs.Value = segment.Totals.EstimatedCompleteSeconds;
                pj.Value = segment.Totals.EstimatedPartialJoules;
                ps.Value = segment.Totals.EstimatedPartialSeconds;
                command.ExecuteNonQuery();
            }
        }

        foreach (var gap in batch.Gaps)
        {
            using var command = Command(transaction, """
                INSERT INTO monitoring_gap (session_id, start_utc_ms, end_utc_ms, reason, detail)
                VALUES ($s, $start, $end, $reason, $detail);
                """);
            command.Parameters.AddWithValue("$s", batch.SessionId);
            command.Parameters.AddWithValue("$start", gap.StartUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$end", gap.EndUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$reason", (int)gap.Reason);
            command.Parameters.AddWithValue("$detail", (object?)gap.Detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        foreach (var change in batch.AvailabilityChanges)
        {
            var at = change.AtUtc.ToUnixTimeMilliseconds();
            using (var close = Command(transaction, """
                UPDATE sensor_availability SET to_utc_ms = $at
                WHERE session_id = $s AND channel = $c AND to_utc_ms IS NULL;
                """))
            {
                close.Parameters.AddWithValue("$at", at);
                close.Parameters.AddWithValue("$s", batch.SessionId);
                close.Parameters.AddWithValue("$c", (int)change.Channel);
                close.ExecuteNonQuery();
            }
            using var open = Command(transaction, """
                INSERT INTO sensor_availability (session_id, channel, state, from_utc_ms) VALUES ($s, $c, $state, $at);
                """);
            open.Parameters.AddWithValue("$s", batch.SessionId);
            open.Parameters.AddWithValue("$c", (int)change.Channel);
            open.Parameters.AddWithValue("$state", (int)change.State);
            open.Parameters.AddWithValue("$at", at);
            open.ExecuteNonQuery();
        }

        if (batch.LastObservationUtc is { } last)
        {
            using var command = Command(transaction, "UPDATE session SET last_observation_utc_ms = $t WHERE id = $s;");
            command.Parameters.AddWithValue("$t", last.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$s", batch.SessionId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Session {batch.SessionId} does not exist.");
        }

        transaction.Commit();
    }

    public IReadOnlyList<EnergyBucket> GetSessionBuckets(long sessionId) =>
        QueryBuckets("WHERE session_id = $s", c => c.Parameters.AddWithValue("$s", sessionId));

    /// <summary>All sessions' buckets whose start lies in [fromUtc, toUtc).</summary>
    public IReadOnlyList<EnergyBucket> GetBuckets(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        QueryBuckets("WHERE bucket_start_utc_s >= $from AND bucket_start_utc_s < $to", c =>
        {
            c.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeSeconds());
            c.Parameters.AddWithValue("$to", toUtc.ToUnixTimeSeconds());
        });

    private List<EnergyBucket> QueryBuckets(string where, Action<SqliteCommand> bind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT session_id, bucket_start_utc_s, monitored_s, cpu_j, cpu_s, gpu_j, gpu_s,
                   est_complete_j, est_complete_s, est_partial_j, est_partial_s
            FROM energy_bucket {where} ORDER BY bucket_start_utc_s, session_id;
            """;
        bind(command);
        using var reader = command.ExecuteReader();
        var result = new List<EnergyBucket>();
        while (reader.Read())
        {
            result.Add(new EnergyBucket
            {
                SessionId = reader.GetInt64(0),
                BucketStartUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                Totals = new EnergyTotals
                {
                    MonitoredSeconds = reader.GetDouble(2),
                    CpuPackageJoules = reader.GetDouble(3),
                    CpuPackageSeconds = reader.GetDouble(4),
                    GpuBoardJoules = reader.GetDouble(5),
                    GpuBoardSeconds = reader.GetDouble(6),
                    EstimatedCompleteJoules = reader.GetDouble(7),
                    EstimatedCompleteSeconds = reader.GetDouble(8),
                    EstimatedPartialJoules = reader.GetDouble(9),
                    EstimatedPartialSeconds = reader.GetDouble(10)
                }
            });
        }
        return result;
    }

    /// <summary>Live-recorded price splits for one session, keyed by (session, bucket start). Empty for a bucket priced by a single entry throughout (the common case).</summary>
    public IReadOnlyDictionary<(long SessionId, DateTimeOffset BucketStartUtc), IReadOnlyList<PricedEnergySegment>> GetPriceSplits(long sessionId) =>
        QueryPriceSplits("WHERE session_id = $s", c => c.Parameters.AddWithValue("$s", sessionId));

    /// <summary>Live-recorded price splits for every bucket starting in [fromUtc, toUtc), across sessions.</summary>
    public IReadOnlyDictionary<(long SessionId, DateTimeOffset BucketStartUtc), IReadOnlyList<PricedEnergySegment>> GetPriceSplits(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        QueryPriceSplits("WHERE bucket_start_utc_s >= $from AND bucket_start_utc_s < $to", c =>
        {
            c.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeSeconds());
            c.Parameters.AddWithValue("$to", toUtc.ToUnixTimeSeconds());
        });

    private IReadOnlyDictionary<(long SessionId, DateTimeOffset BucketStartUtc), IReadOnlyList<PricedEnergySegment>> QueryPriceSplits(string where, Action<SqliteCommand> bind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT session_id, bucket_start_utc_s, price_effective_utc_ms, complete_j, complete_s, partial_j, partial_s
            FROM bucket_price_split {where} ORDER BY session_id, bucket_start_utc_s, price_effective_utc_ms;
            """;
        bind(command);
        using var reader = command.ExecuteReader();
        var byBucket = new Dictionary<(long, DateTimeOffset), List<PricedEnergySegment>>();
        while (reader.Read())
        {
            var sessionId = reader.GetInt64(0);
            var bucketStart = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1));
            var priceMs = reader.GetInt64(2);
            var key = (sessionId, bucketStart);
            if (!byBucket.TryGetValue(key, out var list))
                byBucket[key] = list = [];
            list.Add(new PricedEnergySegment
            {
                SessionId = sessionId,
                BucketStartUtc = bucketStart,
                PriceEffectiveUtc = priceMs == UnpricedSentinel ? null : FromMs(priceMs),
                Totals = new EnergyTotals
                {
                    EstimatedCompleteJoules = reader.GetDouble(3),
                    EstimatedCompleteSeconds = reader.GetDouble(4),
                    EstimatedPartialJoules = reader.GetDouble(5),
                    EstimatedPartialSeconds = reader.GetDouble(6)
                }
            });
        }
        return byBucket.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PricedEnergySegment>)kv.Value);
    }

    /// <summary>
    /// Attempts an exact replay of one bucket's price allocation from raw
    /// <c>power_sample</c> rows -- only meaningful for a bucket with no live
    /// split tracking whose *current* price schedule shows a price change
    /// falling inside it (i.e. a price added retroactively after the bucket
    /// was already persisted; see docs/ENERGY_MODEL.md section 4 and
    /// <see cref="Pricing.CostCalculator.AllocateBucket"/>). Returns null if
    /// the raw samples needed to bracket the bucket exactly are no longer
    /// available (pruned past retention) or don't fully account for the
    /// bucket's already-persisted monitored time -- callers must then fall
    /// back to an explicitly approximate allocation rather than trust a
    /// partial replay.
    /// </summary>
    public IReadOnlyList<PricedEnergySegment>? TryRepriceBucket(EnergyBucket bucket, ElectricityPriceSchedule schedule, EstimationSettings estimation)
    {
        var bucketEnd = bucket.BucketStartUtc + EnergyBuckets.DefaultBucketSize;
        var startMs = bucket.BucketStartUtc.ToUnixTimeMilliseconds();
        var endMs = bucketEnd.ToUnixTimeMilliseconds();

        var boundStartMs = NearestSampleMs(bucket.SessionId, startMs, before: true);
        var boundEndMs = NearestSampleMs(bucket.SessionId, endMs, before: false);
        if (boundStartMs is null || boundEndMs is null)
            return null; // raw data doesn't bracket this bucket on at least one side -- can't replay exactly

        var samples = GetSamples(bucket.SessionId, FromMs(boundStartMs.Value), FromMs(boundEndMs.Value));
        if (samples.Count < 2)
            return null;

        var gaps = GetSessionGaps(bucket.SessionId);
        var segments = new Dictionary<DateTimeOffset, EnergyTotals>();
        double coveredSeconds = 0;

        for (var i = 0; i < samples.Count - 1; i++)
        {
            var s0 = samples[i];
            var s1 = samples[i + 1];
            if (gaps.Any(g => g.StartUtc < s1.UtcTime && g.EndUtc > s0.UtcTime))
                continue; // a recorded gap covers (part of) this span -- never bridge it, same rule as live integration

            var interval = new PowerInterval
            {
                StartUtc = s0.UtcTime,
                EndUtc = s1.UtcTime,
                Seconds = (s1.UtcTime - s0.UtcTime).TotalSeconds,
                CpuPackage = Linear(s0.CpuPackageState, s0.CpuPackageWatts, s1.CpuPackageState, s1.CpuPackageWatts),
                GpuBoard = Linear(s0.GpuBoardState, s0.GpuBoardWatts, s1.GpuBoardState, s1.GpuBoardWatts)
            };

            var boundaries = new[] { bucket.BucketStartUtc, bucketEnd }
                .Where(t => t > interval.StartUtc && t < interval.EndUtc)
                .Concat(schedule.Entries.Select(p => p.EffectiveFromUtc).Where(t => t > interval.StartUtc && t < interval.EndUtc));

            foreach (var piece in interval.SplitAt(boundaries))
            {
                if (piece.StartUtc < bucket.BucketStartUtc || piece.EndUtc > bucketEnd)
                    continue; // outside this bucket's window (the bracketing samples reach slightly beyond it)

                var totals = EnergyTotals.FromInterval(piece, estimation);
                var priceKey = schedule.PriceAt(piece.StartUtc)?.EffectiveFromUtc ?? UnpricedSentinelKey;
                segments[priceKey] = segments.TryGetValue(priceKey, out var existing) ? existing + totals : totals;
                coveredSeconds += totals.MonitoredSeconds;
            }
        }

        // Sanity gate: only trust the replay if it accounts for exactly the
        // monitored time the live recording already measured for this
        // bucket. If not -- e.g. a sample inside the bucket was itself
        // pruned even though the bracketing ones survived -- refuse rather
        // than emit a silently wrong split.
        if (Math.Abs(coveredSeconds - bucket.Totals.MonitoredSeconds) > 0.001)
            return null;

        return segments.Select(kv => new PricedEnergySegment
        {
            SessionId = bucket.SessionId,
            BucketStartUtc = bucket.BucketStartUtc,
            PriceEffectiveUtc = kv.Key == UnpricedSentinelKey ? null : kv.Key,
            Totals = kv.Value
        }).ToList();
    }

    private static LinearPower? Linear(ReadingState s0, double? w0, ReadingState s1, double? w1) =>
        s0 == ReadingState.Valid && s1 == ReadingState.Valid && w0 is not null && w1 is not null
            ? new LinearPower(w0.Value, w1.Value)
            : null;

    /// <summary>The nearest raw sample's utc_ms at-or-before (or at-or-after) <paramref name="utcMs"/> for a session, or null if none exists (e.g. pruned).</summary>
    private long? NearestSampleMs(long sessionId, long utcMs, bool before)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = before
            ? "SELECT MAX(utc_ms) FROM power_sample WHERE session_id = $s AND utc_ms <= $t;"
            : "SELECT MIN(utc_ms) FROM power_sample WHERE session_id = $s AND utc_ms >= $t;";
        command.Parameters.AddWithValue("$s", sessionId);
        command.Parameters.AddWithValue("$t", utcMs);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>Raw samples for one session within [fromUtc, toUtc] (inclusive both ends), for repricing replay. Empty once pruned past retention.</summary>
    public IReadOnlyList<StoredPowerSample> GetSamples(long sessionId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT utc_ms, cpu_mw, cpu_state, gpu_mw, gpu_state FROM power_sample
            WHERE session_id = $s AND utc_ms >= $from AND utc_ms <= $to ORDER BY utc_ms;
            """;
        command.Parameters.AddWithValue("$s", sessionId);
        command.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", toUtc.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var result = new List<StoredPowerSample>();
        while (reader.Read())
        {
            result.Add(new StoredPowerSample(
                FromMs(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : reader.GetInt64(1) / 1000.0,
                (ReadingState)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3) / 1000.0,
                (ReadingState)reader.GetInt32(4)));
        }
        return result;
    }

    public IReadOnlyList<MonitoringGap> GetSessionGaps(long sessionId) =>
        QueryGaps("WHERE session_id = $s", c => c.Parameters.AddWithValue("$s", sessionId));

    /// <summary>Gaps overlapping [fromUtc, toUtc).</summary>
    public IReadOnlyList<MonitoringGap> GetGaps(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        QueryGaps("WHERE start_utc_ms < $to AND end_utc_ms > $from", c =>
        {
            c.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());
            c.Parameters.AddWithValue("$to", toUtc.ToUnixTimeMilliseconds());
        });

    private List<MonitoringGap> QueryGaps(string where, Action<SqliteCommand> bind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT start_utc_ms, end_utc_ms, reason, detail FROM monitoring_gap {where} ORDER BY start_utc_ms;";
        bind(command);
        using var reader = command.ExecuteReader();
        var result = new List<MonitoringGap>();
        while (reader.Read())
        {
            result.Add(new MonitoringGap
            {
                StartUtc = FromMs(reader.GetInt64(0)),
                EndUtc = FromMs(reader.GetInt64(1)),
                Reason = (GapReason)reader.GetInt32(2),
                Detail = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }
        return result;
    }

    public IReadOnlyList<StoredPowerSample> GetSamples(long sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT utc_ms, cpu_mw, cpu_state, gpu_mw, gpu_state FROM power_sample
            WHERE session_id = $s ORDER BY utc_ms;
            """;
        command.Parameters.AddWithValue("$s", sessionId);
        using var reader = command.ExecuteReader();
        var result = new List<StoredPowerSample>();
        while (reader.Read())
        {
            result.Add(new StoredPowerSample(
                FromMs(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : reader.GetInt64(1) / 1000.0,
                (ReadingState)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3) / 1000.0,
                (ReadingState)reader.GetInt32(4)));
        }
        return result;
    }

    public IReadOnlyList<AvailabilityPeriod> GetAvailability(long sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT channel, state, from_utc_ms, to_utc_ms FROM sensor_availability
            WHERE session_id = $s ORDER BY channel, from_utc_ms, id;
            """;
        command.Parameters.AddWithValue("$s", sessionId);
        using var reader = command.ExecuteReader();
        var result = new List<AvailabilityPeriod>();
        while (reader.Read())
        {
            result.Add(new AvailabilityPeriod(
                (EnergyChannel)reader.GetInt32(0),
                (ReadingState)reader.GetInt32(1),
                FromMs(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : FromMs(reader.GetInt64(3))));
        }
        return result;
    }

    // ---------------------------------------------------------------- configuration

    public ElectricityPriceSchedule GetPriceSchedule()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT effective_from_utc_ms, price_per_kwh, currency FROM electricity_price ORDER BY effective_from_utc_ms;";
        using var reader = command.ExecuteReader();
        var prices = new List<ElectricityPrice>();
        while (reader.Read())
        {
            prices.Add(new ElectricityPrice
            {
                EffectiveFromUtc = FromMs(reader.GetInt64(0)),
                PricePerKwh = decimal.Parse(reader.GetString(1), NumberStyles.Number, CultureInfo.InvariantCulture),
                Currency = reader.GetString(2)
            });
        }
        return new ElectricityPriceSchedule(prices);
    }

    /// <summary>Adds a price entry. Validated against the existing schedule (single currency, unique effective time) before writing.</summary>
    public void AddPrice(ElectricityPrice price, DateTimeOffset nowUtc)
    {
        _ = new ElectricityPriceSchedule([.. GetPriceSchedule().Entries, price]);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO electricity_price (effective_from_utc_ms, price_per_kwh, currency, created_utc_ms)
            VALUES ($from, $price, $currency, $created);
            """;
        command.Parameters.AddWithValue("$from", price.EffectiveFromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$price", price.PricePerKwh.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$currency", price.Currency);
        command.Parameters.AddWithValue("$created", nowUtc.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    /// <summary>The current estimation settings, or the documented defaults if none were ever saved.</summary>
    public EstimationSettings GetEstimationSettings()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT other_watts, psu_efficiency, include_cpu, include_gpu FROM estimation_settings ORDER BY id DESC LIMIT 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new EstimationSettings();
        return new EstimationSettings
        {
            OtherComponentsWatts = reader.GetDouble(0),
            PsuEfficiency = reader.GetDouble(1),
            IncludeCpuPackage = reader.GetBoolean(2),
            IncludeGpuBoard = reader.GetBoolean(3)
        };
    }

    public void SaveEstimationSettings(EstimationSettings settings, DateTimeOffset nowUtc)
    {
        settings.Validate();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO estimation_settings (created_utc_ms, other_watts, psu_efficiency, include_cpu, include_gpu)
            VALUES ($t, $o, $e, $c, $g);
            """;
        command.Parameters.AddWithValue("$t", nowUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$o", settings.OtherComponentsWatts);
        command.Parameters.AddWithValue("$e", settings.PsuEfficiency);
        command.Parameters.AddWithValue("$c", settings.IncludeCpuPackage);
        command.Parameters.AddWithValue("$g", settings.IncludeGpuBoard);
        command.ExecuteNonQuery();
    }

    public const int MinMonitoringIntervalMs = 250;
    public const int MaxMonitoringIntervalMs = 60_000;
    public const int DefaultMonitoringIntervalMs = 1000;
    private const string MonitoringIntervalKey = "monitoring_interval_ms";

    /// <summary>The user's chosen sampling interval, or <see cref="DefaultMonitoringIntervalMs"/> if never set.</summary>
    public int GetMonitoringIntervalMs()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $k;";
        command.Parameters.AddWithValue("$k", MonitoringIntervalKey);
        return command.ExecuteScalar() is string s
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            && ms is >= MinMonitoringIntervalMs and <= MaxMonitoringIntervalMs
            ? ms
            : DefaultMonitoringIntervalMs;
    }

    public void SetMonitoringIntervalMs(int intervalMs)
    {
        if (intervalMs is < MinMonitoringIntervalMs or > MaxMonitoringIntervalMs)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), intervalMs,
                $"Monitoring interval must be between {MinMonitoringIntervalMs} and {MaxMonitoringIntervalMs} ms.");
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO app_setting (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", MonitoringIntervalKey);
        command.Parameters.AddWithValue("$v", intervalMs.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public int GetRawRetentionDays()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $k;";
        command.Parameters.AddWithValue("$k", RawRetentionKey);
        return command.ExecuteScalar() is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            ? days
            : DefaultRawRetentionDays;
    }

    public void SetRawRetentionDays(int days)
    {
        if (days is < 0 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(days), days, "Raw sample retention must be between 0 and 3650 days.");
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO app_setting (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", RawRetentionKey);
        command.Parameters.AddWithValue("$v", days.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes raw samples older than the retention period. Energy buckets,
    /// gaps and availability are kept, so historical totals are unaffected.
    /// Freed pages are returned to the file system (incremental auto-vacuum).
    /// </summary>
    public int PruneRawSamples(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromDays(GetRawRetentionDays());
        int deleted;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM power_sample WHERE utc_ms < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            deleted = command.ExecuteNonQuery();
        }
        if (deleted > 0)
        {
            Execute(_connection, "PRAGMA incremental_vacuum;");
            _logger?.LogInformation("Pruned {Count} raw samples older than {Cutoff:O}", deleted, cutoff);
        }
        return deleted;
    }

    public StorageStatistics GetStatistics()
    {
        var pageSize = Scalar<long>(_connection, "PRAGMA page_size;");
        var walPath = DatabasePath + "-wal";
        long? oldest = Scalar<long?>(_connection, "SELECT min(utc_ms) FROM power_sample;");
        long? newest = Scalar<long?>(_connection, "SELECT max(utc_ms) FROM power_sample;");
        return new StorageStatistics
        {
            SchemaVersion = SchemaVersion,
            DatabaseBytes = Scalar<long>(_connection, "PRAGMA page_count;") * pageSize,
            WalBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            FreeBytes = Scalar<long>(_connection, "PRAGMA freelist_count;") * pageSize,
            Sessions = Scalar<long>(_connection, "SELECT count(*) FROM session;"),
            PowerSamples = Scalar<long>(_connection, "SELECT count(*) FROM power_sample;"),
            EnergyBuckets = Scalar<long>(_connection, "SELECT count(*) FROM energy_bucket;"),
            PriceSplitRows = Scalar<long>(_connection, "SELECT count(*) FROM bucket_price_split;"),
            Gaps = Scalar<long>(_connection, "SELECT count(*) FROM monitoring_gap;"),
            AvailabilityRows = Scalar<long>(_connection, "SELECT count(*) FROM sensor_availability;"),
            Prices = Scalar<long>(_connection, "SELECT count(*) FROM electricity_price;"),
            RawRetentionDays = GetRawRetentionDays(),
            OldestSampleUtc = oldest is { } o ? FromMs(o) : null,
            NewestSampleUtc = newest is { } n ? FromMs(n) : null
        };
    }

    /// <summary>Runs SQLite's integrity and foreign-key checks. Returns "ok" when both pass.</summary>
    public string CheckIntegrity()
    {
        var integrity = Scalar<string>(_connection, "PRAGMA integrity_check;") ?? "no result";
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        var fkViolations = 0;
        while (reader.Read())
            fkViolations++;
        return fkViolations == 0 ? integrity : $"{integrity}; {fkViolations} foreign key violation(s)";
    }

    /// <summary>Checkpoints the WAL into the main database file (e.g. before measuring file size).</summary>
    public void Checkpoint() => Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");

    // ---------------------------------------------------------------- helpers

    private SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static object ToMilliwatts(ChannelReading reading) =>
        reading.IsValid ? (long)Math.Round(reading.Watts!.Value * 1000.0, MidpointRounding.AwayFromZero) : DBNull.Value;

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
