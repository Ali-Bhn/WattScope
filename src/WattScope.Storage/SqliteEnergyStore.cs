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
        InsertSensors(transaction, id, request.Sensors);
        transaction.Commit();
        return GetSession(id)!;
    }

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
        using (var command = Command(transaction, "UPDATE session SET status = $status, ended_utc_ms = NULL, end_reason = NULL WHERE id = $id;"))
        {
            command.Parameters.AddWithValue("$status", (int)SessionStatus.Active);
            command.Parameters.AddWithValue("$id", sessionId);
            command.ExecuteNonQuery();
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
