using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WattScope.Storage;

public sealed class DatabaseSchemaTooNewException(int found, int supported)
    : Exception($"Database schema version {found} is newer than this build supports ({supported}). Refusing to open it to avoid corrupting data written by a newer version.")
{
    public int FoundVersion { get; } = found;
    public int SupportedVersion { get; } = supported;
}

/// <summary>
/// Raised when a second Active session is created/reopened in a database that
/// already has one. The database enforces this (<c>ux_session_single_active</c>),
/// so it holds even if two processes race past their own in-memory checks.
/// Derives from <see cref="InvalidOperationException"/> so existing callers
/// that already handle "session cannot be started" keep working.
/// </summary>
public sealed class ActiveSessionExistsException(Exception inner)
    : InvalidOperationException("Another monitoring session is already active in this database. At most one session may be active at a time.", inner);

/// <summary>
/// Forward-only schema migrations tracked in <c>PRAGMA user_version</c>. Each
/// migration runs in its own transaction together with the version bump, so a
/// failed migration leaves the database at the previous version. Never edit a
/// released migration; append a new one.
/// </summary>
internal static class SchemaMigrations
{
    private sealed record Migration(int Version, string Description, string Sql);

    private static readonly Migration[] All =
    [
        new(1, "Initial energy/session schema", """
            CREATE TABLE session (
                id                      INTEGER PRIMARY KEY,
                started_utc_ms          INTEGER NOT NULL,
                ended_utc_ms            INTEGER,
                last_observation_utc_ms INTEGER,
                status                  INTEGER NOT NULL,
                end_reason              TEXT,
                est_other_watts         REAL    NOT NULL,
                est_psu_efficiency      REAL    NOT NULL,
                est_include_cpu         INTEGER NOT NULL,
                est_include_gpu         INTEGER NOT NULL,
                process_elevated        INTEGER,
                app_version             TEXT
            );

            CREATE TABLE session_sensor (
                session_id    INTEGER NOT NULL REFERENCES session(id),
                channel       INTEGER NOT NULL,
                provider_id   TEXT    NOT NULL,
                sensor_id     TEXT    NOT NULL,
                hardware_id   TEXT    NOT NULL,
                hardware_name TEXT    NOT NULL,
                sensor_name   TEXT    NOT NULL,
                PRIMARY KEY (session_id, channel, provider_id, sensor_id)
            ) WITHOUT ROWID;

            -- Raw samples: only the two energy channels, milliwatts as integers
            -- (SQLite varints: ~3 bytes each). Keyed by time first so retention
            -- pruning is a range delete on the primary key.
            CREATE TABLE power_sample (
                utc_ms     INTEGER NOT NULL,
                session_id INTEGER NOT NULL REFERENCES session(id),
                cpu_mw     INTEGER,
                gpu_mw     INTEGER,
                cpu_state  INTEGER NOT NULL,
                gpu_state  INTEGER NOT NULL,
                PRIMARY KEY (utc_ms, session_id)
            ) WITHOUT ROWID;

            -- 15-minute aggregates, kept after raw samples are pruned.
            CREATE TABLE energy_bucket (
                session_id         INTEGER NOT NULL REFERENCES session(id),
                bucket_start_utc_s INTEGER NOT NULL,
                monitored_s        REAL NOT NULL,
                cpu_j              REAL NOT NULL,
                cpu_s              REAL NOT NULL,
                gpu_j              REAL NOT NULL,
                gpu_s              REAL NOT NULL,
                est_complete_j     REAL NOT NULL,
                est_complete_s     REAL NOT NULL,
                est_partial_j      REAL NOT NULL,
                est_partial_s      REAL NOT NULL,
                PRIMARY KEY (session_id, bucket_start_utc_s)
            ) WITHOUT ROWID;
            CREATE INDEX ix_energy_bucket_start ON energy_bucket(bucket_start_utc_s);

            CREATE TABLE monitoring_gap (
                id           INTEGER PRIMARY KEY,
                session_id   INTEGER NOT NULL REFERENCES session(id),
                start_utc_ms INTEGER NOT NULL,
                end_utc_ms   INTEGER NOT NULL,
                reason       INTEGER NOT NULL,
                detail       TEXT
            );
            CREATE INDEX ix_monitoring_gap_session ON monitoring_gap(session_id);
            CREATE INDEX ix_monitoring_gap_start ON monitoring_gap(start_utc_ms);

            -- Run-length encoded channel state: one row per state change, not per sample.
            CREATE TABLE sensor_availability (
                id           INTEGER PRIMARY KEY,
                session_id   INTEGER NOT NULL REFERENCES session(id),
                channel      INTEGER NOT NULL,
                state        INTEGER NOT NULL,
                from_utc_ms  INTEGER NOT NULL,
                to_utc_ms    INTEGER
            );
            CREATE INDEX ix_sensor_availability_open ON sensor_availability(session_id, channel, to_utc_ms);

            -- Prices as invariant decimal text: exact, no binary floating point.
            CREATE TABLE electricity_price (
                id                    INTEGER PRIMARY KEY,
                effective_from_utc_ms INTEGER NOT NULL UNIQUE,
                price_per_kwh         TEXT    NOT NULL,
                currency              TEXT    NOT NULL,
                created_utc_ms        INTEGER NOT NULL
            );

            -- History of estimation settings; the latest row is current.
            CREATE TABLE estimation_settings (
                id             INTEGER PRIMARY KEY,
                created_utc_ms INTEGER NOT NULL,
                other_watts    REAL    NOT NULL,
                psu_efficiency REAL    NOT NULL,
                include_cpu    INTEGER NOT NULL,
                include_gpu    INTEGER NOT NULL
            );

            CREATE TABLE app_setting (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;
            """),

        // Additive: energy_bucket and every Milestone 002 table/row are
        // untouched. One row per (bucket, price) actually seen while
        // recording -- one row for the common case of a single price
        // throughout a bucket, more only when a price genuinely changed
        // mid-bucket. Comparable in size to energy_bucket itself, not
        // "kept indefinitely" in the sense of growing per-flush -- rows for
        // the same (session, bucket, price) coalesce via upsert. See
        // docs/ENERGY_MODEL.md section 4.
        new(2, "Sub-bucket price allocation", """
            CREATE TABLE bucket_price_split (
                session_id             INTEGER NOT NULL REFERENCES session(id),
                bucket_start_utc_s     INTEGER NOT NULL,
                -- The price entry's effective_from, as UTC ms; -1 means this
                -- segment's energy was consumed before any price existed
                -- (unpriced -- NULL is avoided here because WITHOUT ROWID
                -- primary key columns cannot be NULL).
                price_effective_utc_ms INTEGER NOT NULL,
                complete_j REAL NOT NULL,
                complete_s REAL NOT NULL,
                partial_j  REAL NOT NULL,
                partial_s  REAL NOT NULL,
                PRIMARY KEY (session_id, bucket_start_utc_s, price_effective_utc_ms)
            ) WITHOUT ROWID;
            """),

        // Session ownership (Milestone 004): at most one Active session per
        // database, enforced by the database itself rather than by UI or
        // process state. A database that already has several Active rows
        // (orphans of crashed recorders) is repaired first: every Active
        // session except the newest is closed as Interrupted at its last
        // persisted observation -- the same rule crash recovery applies.
        new(3, "Single active session", """
            UPDATE session
            SET status = 2,
                ended_utc_ms = COALESCE(last_observation_utc_ms, started_utc_ms),
                end_reason = 'Closed by migration: more than one session was marked active.'
            WHERE status = 0 AND id <> (SELECT max(id) FROM session WHERE status = 0);

            UPDATE sensor_availability
            SET to_utc_ms = COALESCE((SELECT last_observation_utc_ms FROM session WHERE session.id = sensor_availability.session_id), from_utc_ms)
            WHERE to_utc_ms IS NULL
              AND session_id IN (SELECT id FROM session WHERE status <> 0);

            CREATE UNIQUE INDEX ux_session_single_active ON session(status) WHERE status = 0;
            """)
    ];

    public static int LatestVersion => All[^1].Version;

    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static void Apply(SqliteConnection connection, ILogger? logger)
    {
        var current = GetVersion(connection);
        if (current > LatestVersion)
            throw new DatabaseSchemaTooNewException(current, LatestVersion);

        foreach (var migration in All.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql + $"\nPRAGMA user_version = {migration.Version};";
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            logger?.LogInformation("Applied database migration {Version}: {Description}", migration.Version, migration.Description);
        }
    }
}
