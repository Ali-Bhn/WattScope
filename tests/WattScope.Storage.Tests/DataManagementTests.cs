using Microsoft.Data.Sqlite;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using static WattScope.Storage.Tests.TestDatabase;

namespace WattScope.Storage.Tests;

/// <summary>Milestone 006: safe reads of damaged settings, migration backup/failure handling, maintenance, index use, and the guarded history deletion.</summary>
public class DataManagementTests
{
    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Set(DateTimeOffset value) => _now = value;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Records one finished (Stopped) session of <paramref name="seconds"/> seconds starting at <paramref name="start"/>.</summary>
    private static long SeedSession(SqliteEnergyStore store, DateTimeOffset start, int seconds, bool endIt = true)
    {
        var time = new SettableTime(start);
        var monitor = new EnergyMonitor(store, timeProvider: time);
        var id = monitor.Start(DevMachineBindings, new EstimationSettings()).Id;
        for (var t = 0; t <= seconds; t++)
            monitor.OnSample(Sample(t, 100, 100, start));
        time.Set(start + TimeSpan.FromSeconds(seconds));
        if (endIt)
            monitor.Stop();
        else
            monitor.OnSuspend(); // flush without ending
        return id;
    }

    private static void Raw(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string RawScalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    // ---------------------------------------------------------------- invalid persisted settings

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("99999")]
    [InlineData("")]
    public void AnInvalidPersistedRetention_FallsBackToTheDefault(string stored)
    {
        using var db = new TestDatabase();
        using (db.Open()) { }
        Raw(db.FilePath, $"INSERT INTO app_setting (key, value) VALUES ('raw_retention_days', '{stored}');");

        using var store = db.Open();

        Assert.Equal(SqliteEnergyStore.DefaultRawRetentionDays, store.GetRawRetentionDays());
    }

    [Fact]
    public void InvalidPersistedEstimationSettings_FallBackToTheDefaults_NeverFeedingTheEnergyMaths()
    {
        using var db = new TestDatabase();
        using (db.Open()) { }
        Raw(db.FilePath, "INSERT INTO estimation_settings (created_utc_ms, other_watts, psu_efficiency, include_cpu, include_gpu) VALUES (1, -40, 7.5, 1, 1);");

        using var store = db.Open();

        Assert.Equal(new EstimationSettings(), store.GetEstimationSettings());
    }

    [Fact]
    public void AnUnreadablePriceRow_IsSkipped_AndTheValidOnesStillApply()
    {
        using var db = new TestDatabase();
        using (var store = db.Open())
            store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc, PricePerKwh = 0.30m }, BaseUtc);
        Raw(db.FilePath, "INSERT INTO electricity_price (effective_from_utc_ms, price_per_kwh, currency, created_utc_ms) VALUES (5, 'not-a-number', 'EUR', 5);");

        using var reopened = db.Open();
        var schedule = reopened.GetPriceSchedule();

        Assert.Single(schedule.Entries);
        Assert.Equal(0.30m, schedule.PriceAt(BaseUtc.AddDays(1))!.PricePerKwh);
    }

    [Fact]
    public void AppSettings_RoundTrip_AndAreIndependentOfOtherKeys()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        Assert.Null(store.GetAppSetting("first_run_completed"));
        store.SetAppSetting("first_run_completed", "x");
        store.SetAppSetting("first_run_completed", "y");
        store.SetRawRetentionDays(3);

        Assert.Equal("y", store.GetAppSetting("first_run_completed"));
        Assert.Equal(3, store.GetRawRetentionDays());
    }

    // ---------------------------------------------------------------- migration safety

    [Fact]
    public void MigratingAnOlderDatabase_KeepsAConsistentBackup_AndSucceeds()
    {
        using var db = new TestDatabase();
        long id;
        using (var store = db.Open())
            id = SeedSession(store, BaseUtc, 120);
        // Turn it back into a schema v3 database (what an M004 user has): v4 tables are removed.
        Raw(db.FilePath, "DROP TABLE gaming_session_energy; DROP TABLE gaming_session; DROP TABLE game_mapping; PRAGMA user_version = 3;");

        using (var store = db.Open())
        {
            Assert.Equal(4, store.SchemaVersion);
            Assert.NotEmpty(store.GetSessionBuckets(id));
        }

        var backup = db.FilePath + ".pre-migration-v3.bak";
        Assert.True(File.Exists(backup));
        Assert.Equal("3", RawScalar(backup, "PRAGMA user_version;"));
        Assert.Equal("ok", RawScalar(backup, "PRAGMA integrity_check;"));
        Assert.Equal("1", RawScalar(backup, "SELECT count(*) FROM session;"));
    }

    [Fact]
    public void AFailedMigration_IsRolledBack_ReportsTheBackup_AndLeavesTheDataIntact()
    {
        using var db = new TestDatabase();
        long id;
        using (var store = db.Open())
            id = SeedSession(store, BaseUtc, 120);
        // Claim v3 while the v4 tables already exist: migration 4 must fail ("table already exists").
        Raw(db.FilePath, "PRAGMA user_version = 3;");

        var ex = Assert.Throws<DatabaseMigrationException>(db.Open);

        Assert.Equal(3, ex.FromVersion);
        Assert.Equal(4, ex.FailedVersion);
        Assert.NotNull(ex.BackupPath);
        Assert.True(File.Exists(ex.BackupPath));
        Assert.Equal("3", RawScalar(db.FilePath, "PRAGMA user_version;"));          // not advanced
        Assert.Equal("ok", RawScalar(db.FilePath, "PRAGMA integrity_check;"));
        Assert.Equal("1", RawScalar(db.FilePath, "SELECT count(*) FROM session WHERE id = " + id + ";")); // data untouched
    }

    [Fact]
    public void ANewDatabase_AndAnUpToDateOne_DoNotCreateBackups()
    {
        using var db = new TestDatabase();
        using (db.Open()) { }
        using (db.Open()) { }

        Assert.Empty(Directory.GetFiles(db.Directory, "*.bak"));
    }

    // ---------------------------------------------------------------- indexes (query plans)

    [Theory]
    [InlineData("SELECT * FROM energy_bucket WHERE bucket_start_utc_s >= 0 AND bucket_start_utc_s < 100 ORDER BY bucket_start_utc_s, session_id", "ix_energy_bucket_start")]
    [InlineData("SELECT * FROM energy_bucket WHERE session_id = 1", "energy_bucket")]
    [InlineData("SELECT * FROM monitoring_gap WHERE session_id = 1", "ix_monitoring_gap_session")]
    [InlineData("SELECT * FROM monitoring_gap WHERE start_utc_ms < 100 AND end_utc_ms > 1", "ix_monitoring_gap_start")]
    [InlineData("SELECT * FROM power_sample WHERE utc_ms >= 0 AND utc_ms <= 100", "power_sample")]
    [InlineData("SELECT * FROM gaming_session WHERE game_key = 'x' ORDER BY started_utc_ms", "ix_gaming_session_game")]
    [InlineData("SELECT * FROM gaming_session WHERE monitoring_session_id = 1", "ix_gaming_session_monitoring")]
    public void HotQueries_UseAnIndex_NotAFullScan(string sql, string expectedIndexOrTable)
    {
        using var db = new TestDatabase();
        using (db.Open()) { }
        using var connection = new SqliteConnection($"Data Source={db.FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var details = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                details.Add(reader.GetString(3));

        var plan = string.Join(" | ", details);
        Assert.Contains(expectedIndexOrTable, plan);
        Assert.StartsWith("SEARCH", plan); // a full-table SCAN is the failure
    }

    // ---------------------------------------------------------------- maintenance

    [Fact]
    public void Maintenance_PrunesAndReturnsFreePages_WithoutTouchingTotals()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var id = SeedSession(store, BaseUtc, 1800);
        var before = store.GetSessionBuckets(id);
        store.SetRawRetentionDays(0);

        var result = store.RunMaintenance(BaseUtc.AddDays(30));

        Assert.True(result.RawSamplesPruned > 1000);
        Assert.Equal(0, result.FreePagesAfter);
        Assert.Equal(0, store.GetStatistics().PowerSamples);
        Assert.Equal(before, store.GetSessionBuckets(id));
        Assert.Equal("ok", store.CheckIntegrity(quick: true));
    }

    // ---------------------------------------------------------------- history deletion

    [Fact]
    public void HistoryDeletion_Preview_CountsExactlyWhatWouldGo_AndChangesNothing()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var now = DateTimeOffset.UtcNow;
        SeedSession(store, now.AddDays(-100), 1000);
        SeedSession(store, now.AddDays(-10), 1000);

        var preview = store.PreviewHistoryDeletion(now.AddDays(-30));

        Assert.Equal(1, preview.Sessions);
        Assert.True(preview.EnergyBuckets >= 1);
        Assert.True(preview.PowerSamples > 900);
        Assert.Equal(2, store.GetStatistics().Sessions);
    }

    [Fact]
    public void HistoryDeletion_RemovesOnlyOldFinishedSessions_AndNeverActiveOnesPricesSettingsOrMappings()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var now = DateTimeOffset.UtcNow;
        store.AddPrice(new ElectricityPrice { EffectiveFromUtc = now.AddDays(-200), PricePerKwh = 0.30m }, now);
        store.SaveEstimationSettings(new EstimationSettings { OtherComponentsWatts = 42 }, now);
        var oldId = SeedSession(store, now.AddDays(-100), 1000);
        var recentId = SeedSession(store, now.AddDays(-5), 1000);
        // An old but still-Active session (an orphan from a long-dead recorder is not "finished"): never eligible.
        var activeId = SeedSession(store, now.AddDays(-90), 600, endIt: false);

        var deleted = store.DeleteHistoryBefore(now.AddDays(-30));

        Assert.Equal(1, deleted.Sessions);
        Assert.Null(store.GetSession(oldId));
        Assert.Empty(store.GetSessionBuckets(oldId));
        Assert.Empty(store.GetSamples(oldId));
        Assert.NotNull(store.GetSession(recentId));
        Assert.NotEmpty(store.GetSessionBuckets(recentId));
        Assert.NotNull(store.GetSession(activeId));
        Assert.Single(store.GetPriceSchedule().Entries);
        Assert.Equal(42, store.GetEstimationSettings().OtherComponentsWatts);
        Assert.Equal("ok", store.CheckIntegrity());
    }

    [Fact]
    public void HistoryDeletion_IsTransactionalPerSession_ASecondRunFindsNothingLeftToDelete()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var now = DateTimeOffset.UtcNow;
        SeedSession(store, now.AddDays(-100), 300);
        SeedSession(store, now.AddDays(-80), 300);

        Assert.Equal(2, store.DeleteHistoryBefore(now.AddDays(-30)).Sessions);
        Assert.Equal(0, store.PreviewHistoryDeletion(now.AddDays(-30)).Sessions);
        Assert.Equal(0, store.GetStatistics().Sessions);
        Assert.Equal(0, store.GetStatistics().EnergyBuckets);
    }
}
