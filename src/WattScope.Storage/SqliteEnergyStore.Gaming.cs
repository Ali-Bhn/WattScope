using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WattScope.Core.Energy;
using WattScope.Core.Gaming;

namespace WattScope.Storage;

/// <summary>Gaming sessions and game mappings (Milestone 005, schema v4). See docs/GAMING.md.</summary>
public sealed partial class SqliteEnergyStore
{
    private const int MaxDisplayNameLength = 80;
    private const int MaxExeNameLength = 260;
    private const int MaxExePathLength = 520;

    // ------------------------------------------------------------ gaming sessions

    public long CreateGamingSession(NewGamingSession session)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = Command(transaction, """
                INSERT INTO gaming_session (monitoring_session_id, game_key, exe_name, exe_path, display_name, detection_source, is_manual, started_utc_ms, last_observation_utc_ms, status)
                VALUES ($monitoring, $key, $exe, $path, $name, $source, $manual, $started, $started, 0)
                RETURNING id;
                """);
            command.Parameters.AddWithValue("$monitoring", session.MonitoringSessionId);
            command.Parameters.AddWithValue("$key", session.Game.Key);
            command.Parameters.AddWithValue("$exe", session.Game.ExeName);
            command.Parameters.AddWithValue("$path", (object?)session.Game.ExePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", session.Game.DisplayName);
            command.Parameters.AddWithValue("$source", (int)(session.IsManual ? GameDetectionSource.Manual : session.Game.Source));
            command.Parameters.AddWithValue("$manual", session.IsManual ? 1 : 0);
            command.Parameters.AddWithValue("$started", session.StartedUtc.ToUnixTimeMilliseconds());
            var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            transaction.Commit();
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("gaming_session.status", StringComparison.OrdinalIgnoreCase))
        {
            throw new GamingSessionExistsException(ex);
        }
    }

    public void SaveGamingProgress(long gamingSessionId, DateTimeOffset lastObservationUtc, GamingPeaks peaks, IReadOnlyList<GamingEnergyRow> energy)
    {
        using var transaction = _connection.BeginTransaction();
        using (var command = Command(transaction, """
            UPDATE gaming_session
            SET last_observation_utc_ms = $last,
                peak_cpu_w = $cpu, peak_gpu_w = $gpu, peak_system_complete_w = $sysc, peak_system_partial_w = $sysp
            WHERE id = $id AND status = 0;
            """))
        {
            command.Parameters.AddWithValue("$last", lastObservationUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$cpu", (object?)peaks.CpuWatts ?? DBNull.Value);
            command.Parameters.AddWithValue("$gpu", (object?)peaks.GpuWatts ?? DBNull.Value);
            command.Parameters.AddWithValue("$sysc", (object?)peaks.SystemCompleteWatts ?? DBNull.Value);
            command.Parameters.AddWithValue("$sysp", (object?)peaks.SystemPartialWatts ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", gamingSessionId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Gaming session {gamingSessionId} is not active.");
        }

        foreach (var row in energy)
        {
            // Absolute, not a delta: the tracker holds the whole session's ledger, so rewriting it is idempotent.
            using var command = Command(transaction, """
                INSERT INTO gaming_session_energy
                    (gaming_session_id, price_effective_utc_ms, first_utc_ms, last_utc_ms, monitored_s, cpu_j, cpu_s, gpu_j, gpu_s, est_complete_j, est_complete_s, est_partial_j, est_partial_s)
                VALUES ($id, $price, $first, $last, $mon, $cj, $cs, $gj, $gs, $ecj, $ecs, $epj, $eps)
                ON CONFLICT (gaming_session_id, price_effective_utc_ms) DO UPDATE SET
                    first_utc_ms = excluded.first_utc_ms, last_utc_ms = excluded.last_utc_ms, monitored_s = excluded.monitored_s,
                    cpu_j = excluded.cpu_j, cpu_s = excluded.cpu_s, gpu_j = excluded.gpu_j, gpu_s = excluded.gpu_s,
                    est_complete_j = excluded.est_complete_j, est_complete_s = excluded.est_complete_s,
                    est_partial_j = excluded.est_partial_j, est_partial_s = excluded.est_partial_s;
                """);
            var t = row.Totals;
            command.Parameters.AddWithValue("$id", gamingSessionId);
            command.Parameters.AddWithValue("$price", row.PriceEffectiveUtc is { } p ? p.ToUnixTimeMilliseconds() : UnpricedSentinel);
            command.Parameters.AddWithValue("$first", row.FirstUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$last", row.LastUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$mon", t.MonitoredSeconds);
            command.Parameters.AddWithValue("$cj", t.CpuPackageJoules);
            command.Parameters.AddWithValue("$cs", t.CpuPackageSeconds);
            command.Parameters.AddWithValue("$gj", t.GpuBoardJoules);
            command.Parameters.AddWithValue("$gs", t.GpuBoardSeconds);
            command.Parameters.AddWithValue("$ecj", t.EstimatedCompleteJoules);
            command.Parameters.AddWithValue("$ecs", t.EstimatedCompleteSeconds);
            command.Parameters.AddWithValue("$epj", t.EstimatedPartialJoules);
            command.Parameters.AddWithValue("$eps", t.EstimatedPartialSeconds);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void EndGamingSession(long gamingSessionId, DateTimeOffset endedUtc, GamingSessionStatus status, GamingEndReason reason)
    {
        if (status == GamingSessionStatus.Active)
            throw new ArgumentException("A session cannot be ended with status Active.", nameof(status));
        using var command = _connection.CreateCommand();
        // GREATEST-style clamp so a clock quirk can never produce ended < started (the table forbids it).
        command.CommandText = """
            UPDATE gaming_session
            SET status = $status, end_reason = $reason, ended_utc_ms = max($ended, started_utc_ms)
            WHERE id = $id AND status = 0;
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$reason", (int)reason);
        command.Parameters.AddWithValue("$ended", endedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", gamingSessionId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Gaming session {gamingSessionId} is not active.");
    }

    public void RenameGamingSession(long gamingSessionId, string displayName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE gaming_session SET display_name = $name WHERE id = $id;";
        command.Parameters.AddWithValue("$name", ValidateDisplayName(displayName) ?? throw new ArgumentException("A name is required.", nameof(displayName)));
        command.Parameters.AddWithValue("$id", gamingSessionId);
        command.ExecuteNonQuery();
    }

    /// <summary>Renames every stored session of one game (<paramref name="gameKey"/> is the lower-cased executable name), so history and per-game totals show one name. Returns how many sessions changed.</summary>
    public int RenameGame(string gameKey, string displayName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE gaming_session SET display_name = $name WHERE game_key = $key AND display_name <> $name;";
        command.Parameters.AddWithValue("$name", ValidateDisplayName(displayName) ?? throw new ArgumentException("A name is required.", nameof(displayName)));
        command.Parameters.AddWithValue("$key", gameKey.ToLowerInvariant());
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Ends gaming sessions left Active by a process that died, as Interrupted at
    /// their last persisted observation (nothing after it is assumed). The caller
    /// must hold the <see cref="MonitorLock"/>. Called by <see cref="RecoverInterruptedSessions"/>.
    /// </summary>
    public int RecoverInterruptedGamingSessions()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE gaming_session
            SET status = 2, end_reason = $reason,
                ended_utc_ms = max(started_utc_ms, COALESCE(last_observation_utc_ms, started_utc_ms))
            WHERE status = 0;
            """;
        command.Parameters.AddWithValue("$reason", (int)GamingEndReason.Interrupted);
        var count = command.ExecuteNonQuery();
        if (count > 0)
            _logger?.LogWarning("Recovered {Count} interrupted gaming session(s)", count);
        return count;
    }

    public GamingSessionRecord? GetGamingSession(long id) =>
        QueryGamingSessions("WHERE id = $id", c => c.Parameters.AddWithValue("$id", id)).SingleOrDefault();

    public GamingSessionRecord? GetActiveGamingSession() =>
        QueryGamingSessions("WHERE status = 0", _ => { }).SingleOrDefault();

    /// <summary>Newest first. <paramref name="gameKey"/> restricts to one game; <paramref name="limit"/> bounds the result.</summary>
    public IReadOnlyList<GamingSessionRecord> ListGamingSessions(int limit = 50, string? gameKey = null)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return QueryGamingSessions(
            gameKey is null ? "ORDER BY started_utc_ms DESC, id DESC LIMIT $limit" : "WHERE game_key = $key ORDER BY started_utc_ms DESC, id DESC LIMIT $limit",
            c =>
            {
                c.Parameters.AddWithValue("$limit", limit);
                if (gameKey is not null)
                    c.Parameters.AddWithValue("$key", gameKey);
            });
    }

    /// <summary>Gaming sessions that started in [from, to) -- a local day's worth, newest first. Uses the started-time index.</summary>
    public IReadOnlyList<GamingSessionRecord> ListGamingSessionsBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return QueryGamingSessions(
            "WHERE started_utc_ms >= $from AND started_utc_ms < $to ORDER BY started_utc_ms DESC, id DESC LIMIT $limit",
            c =>
            {
                c.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());
                c.Parameters.AddWithValue("$to", toUtc.ToUnixTimeMilliseconds());
                c.Parameters.AddWithValue("$limit", limit);
            });
    }

    /// <summary>Every gaming session (used for per-game aggregates). Bounded by what a person can play, not by sampling rate.</summary>
    public IReadOnlyList<GamingSessionRecord> ListAllGamingSessions() =>
        QueryGamingSessions("ORDER BY started_utc_ms", _ => { });

    private List<GamingSessionRecord> QueryGamingSessions(string clause, Action<SqliteCommand> bind)
    {
        var sessions = new List<GamingSessionRecord>();
        var energy = new Dictionary<long, List<GamingEnergyRow>>();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, monitoring_session_id, game_key, exe_name, exe_path, display_name, detection_source, is_manual,
                       started_utc_ms, ended_utc_ms, last_observation_utc_ms, status, end_reason,
                       peak_cpu_w, peak_gpu_w, peak_system_complete_w, peak_system_partial_w
                FROM gaming_session {clause};
                """;
            bind(command);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new GamingSessionRecord
                {
                    Id = reader.GetInt64(0),
                    MonitoringSessionId = reader.GetInt64(1),
                    GameKey = reader.GetString(2),
                    ExeName = reader.GetString(3),
                    ExePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    DisplayName = reader.GetString(5),
                    Source = (GameDetectionSource)reader.GetInt32(6),
                    IsManual = reader.GetInt32(7) == 1,
                    StartedUtc = FromMs(reader.GetInt64(8)),
                    EndedUtc = reader.IsDBNull(9) ? null : FromMs(reader.GetInt64(9)),
                    LastObservationUtc = reader.IsDBNull(10) ? null : FromMs(reader.GetInt64(10)),
                    Status = (GamingSessionStatus)reader.GetInt32(11),
                    EndReason = reader.IsDBNull(12) ? null : (GamingEndReason)reader.GetInt32(12),
                    Peaks = new GamingPeaks
                    {
                        CpuWatts = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                        GpuWatts = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                        SystemCompleteWatts = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                        SystemPartialWatts = reader.IsDBNull(16) ? null : reader.GetDouble(16)
                    },
                    Energy = []
                });
            }
        }

        if (sessions.Count == 0)
            return sessions;

        using (var command = _connection.CreateCommand())
        {
            // Ids come from the database (integers); no user text is concatenated into the statement.
            command.CommandText = $"""
                SELECT gaming_session_id, price_effective_utc_ms, first_utc_ms, last_utc_ms, monitored_s, cpu_j, cpu_s, gpu_j, gpu_s,
                       est_complete_j, est_complete_s, est_partial_j, est_partial_s
                FROM gaming_session_energy
                WHERE gaming_session_id IN ({string.Join(",", sessions.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)))})
                ORDER BY gaming_session_id, price_effective_utc_ms;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!energy.TryGetValue(id, out var list))
                    energy[id] = list = [];
                var price = reader.GetInt64(1);
                list.Add(new GamingEnergyRow(
                    price == UnpricedSentinel ? null : FromMs(price),
                    new EnergyTotals
                    {
                        MonitoredSeconds = reader.GetDouble(4),
                        CpuPackageJoules = reader.GetDouble(5),
                        CpuPackageSeconds = reader.GetDouble(6),
                        GpuBoardJoules = reader.GetDouble(7),
                        GpuBoardSeconds = reader.GetDouble(8),
                        EstimatedCompleteJoules = reader.GetDouble(9),
                        EstimatedCompleteSeconds = reader.GetDouble(10),
                        EstimatedPartialJoules = reader.GetDouble(11),
                        EstimatedPartialSeconds = reader.GetDouble(12)
                    },
                    FromMs(reader.GetInt64(2)),
                    FromMs(reader.GetInt64(3))));
            }
        }

        return sessions.Select(s => energy.TryGetValue(s.Id, out var rows) ? s with { Energy = rows } : s).ToList();
    }

    /// <summary>Monitoring gaps that overlap a time span of a monitoring session (for showing how much of a gaming session was not monitored).</summary>
    public IReadOnlyList<MonitoringGap> GetGapsOverlapping(long monitoringSessionId, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        QueryGaps("WHERE session_id = $sid AND end_utc_ms > $from AND start_utc_ms < $to", c =>
        {
            c.Parameters.AddWithValue("$sid", monitoringSessionId);
            c.Parameters.AddWithValue("$from", fromUtc.ToUnixTimeMilliseconds());
            c.Parameters.AddWithValue("$to", toUtc.ToUnixTimeMilliseconds());
        });

    // ------------------------------------------------------------ game mappings

    public IReadOnlyList<GameMapping> GetGameMappings()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, exe_name, exe_path, kind, display_name, updated_utc_ms FROM game_mapping ORDER BY exe_name, exe_path;";
        using var reader = command.ExecuteReader();
        var result = new List<GameMapping>();
        while (reader.Read())
        {
            result.Add(new GameMapping
            {
                Id = reader.GetInt64(0),
                ExeName = reader.GetString(1),
                ExePath = reader.GetString(2),
                Kind = (GameMappingKind)reader.GetInt32(3),
                DisplayName = reader.IsDBNull(4) ? null : reader.GetString(4),
                UpdatedUtc = FromMs(reader.GetInt64(5))
            });
        }
        return result;
    }

    /// <summary>Adds or replaces the user's decision for one executable (name, optionally a specific path). Validated before writing.</summary>
    public GameMapping SetGameMapping(GameMapping mapping, DateTimeOffset nowUtc)
    {
        var (name, path) = NormalizeExecutable(mapping.ExeName, mapping.ExePath);
        if (!Enum.IsDefined(mapping.Kind))
            throw new ArgumentException("Unknown mapping kind.", nameof(mapping));
        var display = mapping.Kind == GameMappingKind.Game ? ValidateDisplayName(mapping.DisplayName) : null;

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_mapping (exe_name, exe_path, kind, display_name, created_utc_ms, updated_utc_ms)
            VALUES ($name, $path, $kind, $display, $now, $now)
            ON CONFLICT (exe_name, exe_path) DO UPDATE SET kind = excluded.kind, display_name = excluded.display_name, updated_utc_ms = excluded.updated_utc_ms
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$kind", (int)mapping.Kind);
        command.Parameters.AddWithValue("$display", (object?)display ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return mapping with { Id = id, ExeName = name, ExePath = path, DisplayName = display, UpdatedUtc = nowUtc };
    }

    public bool RemoveGameMapping(string exeName, string? exePath)
    {
        var (name, path) = NormalizeExecutable(exeName, exePath);
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM game_mapping WHERE exe_name = $name AND exe_path = $path;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Accepts either a bare name (<c>cod.exe</c>) or a full path in
    /// <paramref name="exeName"/>; a path is split into name + path. Rejects
    /// anything that is not a plausible executable file name.
    /// </summary>
    public static (string Name, string Path) NormalizeExecutable(string? exeName, string? exePath)
    {
        var name = (exeName ?? "").Trim().Trim('"');
        var path = (exePath ?? "").Trim().Trim('"');
        if (name.Contains('\\') || name.Contains('/'))
        {
            path = path.Length > 0 ? path : name;
            name = System.IO.Path.GetFileName(name.Replace('/', '\\'));
        }
        if (path.Length > 0)
            name = name.Length > 0 ? name : System.IO.Path.GetFileName(path.Replace('/', '\\'));

        if (name.Length == 0 || name.Length > MaxExeNameLength)
            throw new ArgumentException("An executable name is required (for example 'game.exe').");
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' is not a valid executable file name.");
        if (path.Length > MaxExePathLength || (path.Length > 0 && path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0))
            throw new ArgumentException("The executable path is not valid.");
        return (name, path);
    }

    private static string? ValidateDisplayName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length > MaxDisplayNameLength)
            throw new ArgumentException($"A game name may be at most {MaxDisplayNameLength} characters.");
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("A game name cannot contain control characters.");
        return trimmed;
    }
}
