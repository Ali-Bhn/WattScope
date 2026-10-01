using Microsoft.Data.Sqlite;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Gaming;
using WattScope.Core.Monitoring;

namespace WattScope.Storage.Tests;

public class GamingStorageTests
{
    private static readonly DateTimeOffset BaseUtc = TestDatabase.BaseUtc;

    private static MonitoringSession NewMonitoring(SqliteEnergyStore store, DateTimeOffset? start = null) =>
        store.CreateSession(new NewSessionRequest { StartedUtc = start ?? BaseUtc, Estimation = new EstimationSettings(), Sensors = [] });

    private static NewGamingSession NewGame(long monitoringId, string key = "cod.exe", DateTimeOffset? start = null, bool manual = false) => new()
    {
        MonitoringSessionId = monitoringId,
        IsManual = manual,
        StartedUtc = start ?? BaseUtc,
        Game = new GameIdentity { Key = key, ExeName = key, ExePath = @"D:\Games\" + key, DisplayName = "Call of Duty", Source = GameDetectionSource.KnownCatalog, Tier = DetectionTier.Mapped }
    };

    private static EnergyTotals Totals(double seconds, double cpuW, double gpuW) => new()
    {
        MonitoredSeconds = seconds,
        CpuPackageJoules = cpuW * seconds, CpuPackageSeconds = seconds,
        GpuBoardJoules = gpuW * seconds, GpuBoardSeconds = seconds,
        EstimatedCompleteJoules = (cpuW + gpuW + 50) / 0.9 * seconds, EstimatedCompleteSeconds = seconds
    };

    private static void Exec(string path, params string[] statements)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public void MigrationFromAnExistingM004Database_KeepsAllHistory_AndAddsTheGamingTables()
    {
        using var db = new TestDatabase();
        long monitoringId;
        using (var store = db.Open())
        {
            var session = NewMonitoring(store);
            monitoringId = session.Id;
            store.WriteBatch(new EnergyWriteBatch
            {
                SessionId = session.Id,
                Samples = [TestDatabase.Sample(0, 100, 200), TestDatabase.Sample(1, 100, 200)],
                BucketDeltas = [new EnergyBucket { SessionId = session.Id, BucketStartUtc = BaseUtc, Totals = Totals(60, 100, 200) }],
                Gaps = [],
                AvailabilityChanges = [],
                LastObservationUtc = BaseUtc.AddSeconds(60)
            });
            store.EndSession(session.Id, BaseUtc.AddSeconds(61), SessionStatus.Stopped, "done");
            store.SetRawRetentionDays(5);
        }

        // Rewind to exactly what a Milestone 004 build left on disk: schema version 3, no gaming tables.
        Exec(db.FilePath, "DROP TABLE gaming_session_energy;", "DROP TABLE gaming_session;", "DROP TABLE game_mapping;", "PRAGMA user_version = 3;");

        using var migrated = db.Open();

        Assert.Equal(4, migrated.SchemaVersion);
        var kept = Assert.Single(migrated.ListSessions(10));
        Assert.Equal(monitoringId, kept.Id);
        Assert.Equal(SessionStatus.Stopped, kept.Status);
        Assert.Equal(60, migrated.GetSessionBuckets(kept.Id).Single().Totals.MonitoredSeconds, 9);   // M002 energy untouched
        Assert.Equal(2, migrated.GetSamples(kept.Id).Count);                                          // raw samples untouched
        Assert.Equal(5, migrated.GetRawRetentionDays());                                              // settings untouched
        Assert.Empty(migrated.ListGamingSessions());
        Assert.Empty(migrated.GetGameMappings());
        Assert.Equal("ok", migrated.CheckIntegrity());

        // and the new tables are usable immediately
        var id = migrated.CreateGamingSession(NewGame(kept.Id));
        Assert.NotNull(migrated.GetGamingSession(id));
    }

    [Fact]
    public void FreshDatabase_HasTheGamingSchema_WithTheExpectedIndexes()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        using var connection = new SqliteConnection($"Data Source={db.FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE '%gaming%' OR name LIKE '%game_mapping%';";
        var names = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                names.Add(reader.GetString(0));

        Assert.Contains("ux_gaming_single_active", names);
        Assert.Contains("ix_gaming_session_game", names);
        Assert.Contains("ix_gaming_session_started", names);
        Assert.Contains("ix_gaming_session_monitoring", names);
    }

    // ---------------------------------------------------------------- round trip

    [Fact]
    public void GamingSession_RoundTrips_WithPeaksAndPerPriceEnergyRows()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var monitoring = NewMonitoring(store);
        var id = store.CreateGamingSession(NewGame(monitoring.Id));
        var price = BaseUtc.AddHours(-1);

        store.SaveGamingProgress(id, BaseUtc.AddSeconds(90), new GamingPeaks { CpuWatts = 150.5, GpuWatts = 280, SystemCompleteWatts = 520.25, SystemPartialWatts = null },
        [
            new GamingEnergyRow(null, Totals(30, 100, 200), BaseUtc, BaseUtc.AddSeconds(30)),
            new GamingEnergyRow(price, Totals(60, 110, 210), BaseUtc.AddSeconds(30), BaseUtc.AddSeconds(90))
        ]);
        store.EndGamingSession(id, BaseUtc.AddSeconds(95), GamingSessionStatus.Ended, GamingEndReason.ProcessExited);

        var loaded = store.GetGamingSession(id)!;
        Assert.Equal("cod.exe", loaded.GameKey);
        Assert.Equal("Call of Duty", loaded.DisplayName);
        Assert.Equal(GameDetectionSource.KnownCatalog, loaded.Source);
        Assert.Equal(GamingSessionStatus.Ended, loaded.Status);
        Assert.Equal(GamingEndReason.ProcessExited, loaded.EndReason);
        Assert.Equal(BaseUtc.AddSeconds(95), loaded.EndedUtc);
        Assert.Equal(BaseUtc.AddSeconds(90), loaded.LastObservationUtc);
        Assert.Equal(150.5, loaded.Peaks.CpuWatts);
        Assert.Null(loaded.Peaks.SystemPartialWatts);                                   // a missing peak stays missing, not 0
        Assert.Equal(2, loaded.Energy.Count);
        Assert.Null(loaded.Energy[0].PriceEffectiveUtc);                                // the "unpriced" row round-trips as null
        Assert.Equal(price, loaded.Energy[1].PriceEffectiveUtc);
        Assert.Equal(60 + 30, GamingAnalytics.TotalsOf(loaded.Energy).MonitoredSeconds, 9);
        Assert.Equal(BaseUtc.AddSeconds(30), loaded.Energy[1].FirstUtc);
    }

    [Fact]
    public void SavingProgressTwice_ReplacesTheTotals_ItDoesNotAddThemUp()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var id = store.CreateGamingSession(NewGame(NewMonitoring(store).Id));

        store.SaveGamingProgress(id, BaseUtc.AddSeconds(10), new GamingPeaks(), [new GamingEnergyRow(null, Totals(10, 100, 100), BaseUtc, BaseUtc.AddSeconds(10))]);
        store.SaveGamingProgress(id, BaseUtc.AddSeconds(20), new GamingPeaks(), [new GamingEnergyRow(null, Totals(20, 100, 100), BaseUtc, BaseUtc.AddSeconds(20))]);

        var loaded = store.GetGamingSession(id)!;
        Assert.Single(loaded.Energy);
        Assert.Equal(20, loaded.Energy[0].Totals.MonitoredSeconds, 9);                  // absolute rewrite (idempotent), not 10 + 20
    }

    [Fact]
    public void ManualSessions_AreMarkedAsManual()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        var id = store.CreateGamingSession(NewGame(NewMonitoring(store).Id, "manual:retro", manual: true));

        var loaded = store.GetGamingSession(id)!;
        Assert.True(loaded.IsManual);
        Assert.Equal(GameDetectionSource.Manual, loaded.Source);
    }

    [Fact]
    public void Rename_ChangesTheStoredName()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var id = store.CreateGamingSession(NewGame(NewMonitoring(store).Id));

        store.RenameGamingSession(id, "Call of Duty: Warzone");

        Assert.Equal("Call of Duty: Warzone", store.GetGamingSession(id)!.DisplayName);
        Assert.Throws<ArgumentException>(() => store.RenameGamingSession(id, "   "));
    }

    // ---------------------------------------------------------------- one active session

    [Fact]
    public void AtMostOneGamingSession_CanBeActive_EnforcedByTheDatabase()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var monitoring = NewMonitoring(store);
        var first = store.CreateGamingSession(NewGame(monitoring.Id));

        Assert.Throws<GamingSessionExistsException>(() => store.CreateGamingSession(NewGame(monitoring.Id, "other.exe")));

        store.EndGamingSession(first, BaseUtc.AddSeconds(10), GamingSessionStatus.Ended, GamingEndReason.ProcessExited);
        var second = store.CreateGamingSession(NewGame(monitoring.Id, "other.exe", BaseUtc.AddSeconds(20)));   // fine once the first ended
        Assert.Equal(second, store.GetActiveGamingSession()!.Id);
    }

    [Fact]
    public void TwoConnectionsRacingToStartAGamingSession_ProduceExactlyOne()
    {
        using var db = new TestDatabase();
        using var a = db.Open();
        using var b = db.Open();
        var monitoring = NewMonitoring(a);

        a.CreateGamingSession(NewGame(monitoring.Id));
        Assert.Throws<GamingSessionExistsException>(() => b.CreateGamingSession(NewGame(monitoring.Id, "other.exe")));

        Assert.Single(a.ListGamingSessions());
    }

    [Fact]
    public void AGamingSession_MustReferenceAnExistingMonitoringSession()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        Assert.Throws<SqliteException>(() => store.CreateGamingSession(NewGame(monitoringId: 12345)));
    }

    [Fact]
    public void EndingAnAlreadyEndedSession_IsRefused_AndEndTimeNeverPrecedesStart()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var id = store.CreateGamingSession(NewGame(NewMonitoring(store).Id, start: BaseUtc.AddSeconds(100)));

        store.EndGamingSession(id, BaseUtc, GamingSessionStatus.Ended, GamingEndReason.ManualStop);   // a clock quirk: "before" the start

        Assert.Equal(BaseUtc.AddSeconds(100), store.GetGamingSession(id)!.EndedUtc);                   // clamped, the table's CHECK is never violated
        Assert.Throws<InvalidOperationException>(() => store.EndGamingSession(id, BaseUtc.AddSeconds(200), GamingSessionStatus.Ended, GamingEndReason.ManualStop));
        Assert.Throws<ArgumentException>(() => store.EndGamingSession(id, BaseUtc, GamingSessionStatus.Active, GamingEndReason.ManualStop));
    }

    // ---------------------------------------------------------------- crash recovery

    [Fact]
    public void CrashRecovery_EndsAnOrphanedGamingSession_AsInterrupted_AtItsLastPersistedObservation()
    {
        using var db = new TestDatabase();
        long gamingId;
        using (var store = db.Open())
        {
            NewMonitoring(store);
            gamingId = store.CreateGamingSession(NewGame(1));
            store.SaveGamingProgress(gamingId, BaseUtc.AddSeconds(42), new GamingPeaks { GpuWatts = 200 }, [new GamingEnergyRow(null, Totals(42, 100, 200), BaseUtc, BaseUtc.AddSeconds(42))]);
            // the process dies here: no EndGamingSession, no EndSession
        }

        using var restarted = db.Open();
        var recovered = restarted.RecoverInterruptedSessions();

        Assert.Single(recovered);                                                           // the monitoring session
        var gaming = restarted.GetGamingSession(gamingId)!;
        Assert.Equal(GamingSessionStatus.Interrupted, gaming.Status);
        Assert.Equal(GamingEndReason.Interrupted, gaming.EndReason);
        Assert.Equal(BaseUtc.AddSeconds(42), gaming.EndedUtc);                               // nothing after the last persisted observation is assumed
        Assert.Equal(42, GamingAnalytics.TotalsOf(gaming.Energy).MonitoredSeconds, 9);      // what was flushed is kept
        Assert.Null(restarted.GetActiveGamingSession());                                    // so a new gaming session may start
        restarted.CreateSession(new NewSessionRequest { StartedUtc = BaseUtc.AddHours(1), Estimation = new EstimationSettings(), Sensors = [] });
        restarted.CreateGamingSession(NewGame(2, start: BaseUtc.AddHours(1)));
    }

    [Fact]
    public void CrashRecovery_LeavesFinishedGamingSessionsAlone()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var monitoring = NewMonitoring(store);
        var id = store.CreateGamingSession(NewGame(monitoring.Id));
        store.EndGamingSession(id, BaseUtc.AddSeconds(30), GamingSessionStatus.Ended, GamingEndReason.ProcessExited);
        store.EndSession(monitoring.Id, BaseUtc.AddSeconds(31), SessionStatus.Stopped, "done");

        store.RecoverInterruptedSessions();

        Assert.Equal(GamingSessionStatus.Ended, store.GetGamingSession(id)!.Status);
        Assert.Equal(GamingEndReason.ProcessExited, store.GetGamingSession(id)!.EndReason);
    }

    // ---------------------------------------------------------------- queries

    [Fact]
    public void List_IsNewestFirst_Limited_AndFilterableByGame()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var monitoring = NewMonitoring(store);
        for (var i = 0; i < 4; i++)
        {
            var id = store.CreateGamingSession(NewGame(monitoring.Id, i % 2 == 0 ? "a.exe" : "b.exe", BaseUtc.AddHours(i)));
            store.EndGamingSession(id, BaseUtc.AddHours(i).AddMinutes(30), GamingSessionStatus.Ended, GamingEndReason.ProcessExited);
        }

        Assert.Equal(["b.exe", "a.exe", "b.exe", "a.exe"], store.ListGamingSessions().Select(s => s.GameKey));
        Assert.Equal(2, store.ListGamingSessions(limit: 2).Count);
        Assert.All(store.ListGamingSessions(gameKey: "a.exe"), s => Assert.Equal("a.exe", s.GameKey));
        Assert.Equal(4, store.ListAllGamingSessions().Count);
    }

    [Fact]
    public void GapsOverlappingAGamingSession_AreReturnedForTheMonitoringSession()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var monitoring = NewMonitoring(store);
        store.WriteBatch(new EnergyWriteBatch
        {
            SessionId = monitoring.Id,
            Samples = [],
            BucketDeltas = [],
            Gaps =
            [
                new MonitoringGap { StartUtc = BaseUtc.AddSeconds(100), EndUtc = BaseUtc.AddSeconds(200), Reason = GapReason.Suspend },
                new MonitoringGap { StartUtc = BaseUtc.AddSeconds(5000), EndUtc = BaseUtc.AddSeconds(5100), Reason = GapReason.SamplingStall }
            ],
            AvailabilityChanges = []
        });

        var overlapping = store.GetGapsOverlapping(monitoring.Id, BaseUtc, BaseUtc.AddSeconds(1000));

        Assert.Equal(GapReason.Suspend, Assert.Single(overlapping).Reason);
    }

    // ---------------------------------------------------------------- mappings

    [Fact]
    public void Mappings_AreStoredAndReplaced_PerExecutable_CaseInsensitively()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        store.SetGameMapping(new GameMapping { ExeName = "MyGame.exe", Kind = GameMappingKind.Game, DisplayName = "My Game" }, BaseUtc);
        store.SetGameMapping(new GameMapping { ExeName = "mygame.EXE", Kind = GameMappingKind.Game, DisplayName = "My Game 2" }, BaseUtc.AddMinutes(1));   // same executable
        store.SetGameMapping(new GameMapping { ExeName = "tool.exe", Kind = GameMappingKind.Excluded }, BaseUtc);

        var all = store.GetGameMappings();
        Assert.Equal(2, all.Count);
        Assert.Equal("My Game 2", all.Single(m => m.Kind == GameMappingKind.Game).DisplayName);
        Assert.Null(all.Single(m => m.Kind == GameMappingKind.Excluded).DisplayName);
    }

    [Fact]
    public void AMappingForANameAndAPath_CoexistsWithTheNameOnlyOne()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        store.SetGameMapping(new GameMapping { ExeName = "game.exe", Kind = GameMappingKind.Game }, BaseUtc);
        store.SetGameMapping(new GameMapping { ExeName = "game.exe", ExePath = @"E:\Other\game.exe", Kind = GameMappingKind.Excluded }, BaseUtc);

        Assert.Equal(2, store.GetGameMappings().Count);
        Assert.True(store.RemoveGameMapping("game.exe", @"E:\OTHER\GAME.EXE"));
        Assert.Single(store.GetGameMappings());
        Assert.False(store.RemoveGameMapping("nothing.exe", null));
    }

    [Fact]
    public void AFullPathGivenAsTheName_IsSplitIntoNameAndPath()
    {
        var (name, path) = SqliteEnergyStore.NormalizeExecutable(@"D:\Games\Call of Duty\cod.exe", null);

        Assert.Equal("cod.exe", name);
        Assert.Equal(@"D:\Games\Call of Duty\cod.exe", path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad|name.exe")]
    [InlineData("a<b.exe")]
    public void InvalidExecutableNames_AreRejected(string name)
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        Assert.Throws<ArgumentException>(() => store.SetGameMapping(new GameMapping { ExeName = name, Kind = GameMappingKind.Game }, BaseUtc));
    }

    [Fact]
    public void AnOverlongOrControlCharacterGameName_IsRejected()
    {
        using var db = new TestDatabase();
        using var store = db.Open();

        Assert.Throws<ArgumentException>(() => store.SetGameMapping(new GameMapping { ExeName = "a.exe", Kind = GameMappingKind.Game, DisplayName = new string('x', 81) }, BaseUtc));
        Assert.Throws<ArgumentException>(() => store.SetGameMapping(new GameMapping { ExeName = "a.exe", Kind = GameMappingKind.Game, DisplayName = "bad\nname" }, BaseUtc));
    }

    [Fact]
    public void Mappings_SurviveReopeningTheDatabase()
    {
        using var db = new TestDatabase();
        using (var store = db.Open())
            store.SetGameMapping(new GameMapping { ExeName = "keep.exe", Kind = GameMappingKind.Game, DisplayName = "Keep" }, BaseUtc);

        using var reopened = db.Open();

        Assert.Equal("Keep", Assert.Single(reopened.GetGameMappings()).DisplayName);
    }

    [Fact]
    public void Statistics_CountGamingRows()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        store.SetGameMapping(new GameMapping { ExeName = "a.exe", Kind = GameMappingKind.Excluded }, BaseUtc);
        store.CreateGamingSession(NewGame(NewMonitoring(store).Id));

        var stats = store.GetStatistics();

        Assert.Equal(1, stats.GamingSessions);
        Assert.Equal(1, stats.GameMappings);
        Assert.Equal(4, stats.SchemaVersion);
    }
}
