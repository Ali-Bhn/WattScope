using Microsoft.Data.Sqlite;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using static WattScope.Storage.Tests.TestDatabase;

namespace WattScope.Storage.Tests;

/// <summary>
/// Session ownership is enforced by the database itself (migration v3), so it
/// holds no matter which process or code path tries to open a session.
/// </summary>
public class SessionOwnershipTests
{
    private static NewSessionRequest NewSession(DateTimeOffset? at = null) => new()
    {
        StartedUtc = at ?? BaseUtc,
        Estimation = new EstimationSettings(),
        Sensors = []
    };

    [Fact]
    public void SecondActiveSession_IsRefusedByTheDatabase()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var first = store.CreateSession(NewSession());

        var ex = Assert.Throws<ActiveSessionExistsException>(() => store.CreateSession(NewSession(BaseUtc.AddMinutes(1))));

        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Single(store.ListSessions(10));
        Assert.Equal(SessionStatus.Active, store.GetSession(first.Id)!.Status);
    }

    [Fact]
    public void TwoConnections_RacingToOpenASession_YieldExactlyOne()
    {
        using var db = new TestDatabase();
        using var a = db.Open();
        using var b = db.Open();

        var results = new[] { a, b }
            .Select(store => Task.Run(() =>
            {
                try { return store.CreateSession(NewSession()).Id; }
                catch (InvalidOperationException) { return (long?)null; }
            }))
            .ToArray();
        Task.WaitAll(results);

        Assert.Equal(1, results.Count(r => r.Result is not null));
        using var check = db.Open();
        Assert.Single(check.ListSessions(10), s => s.Status == SessionStatus.Active);
    }

    [Fact]
    public void NewSession_IsAllowedOnceThePreviousOneEnds()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var first = store.CreateSession(NewSession());
        store.EndSession(first.Id, BaseUtc.AddMinutes(1), SessionStatus.Stopped, "done");

        var second = store.CreateSession(NewSession(BaseUtc.AddMinutes(2)));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, store.ListSessions(10).Count);
    }

    [Fact]
    public void ReopeningASession_WhileAnotherIsActive_IsRefused()
    {
        using var db = new TestDatabase();
        using var store = db.Open();
        var old = store.CreateSession(NewSession());
        store.EndSession(old.Id, BaseUtc.AddMinutes(1), SessionStatus.Stopped, "done");
        store.CreateSession(NewSession(BaseUtc.AddMinutes(2)));

        Assert.Throws<ActiveSessionExistsException>(() => store.ReopenSession(old.Id, []));

        Assert.Equal(SessionStatus.Stopped, store.GetSession(old.Id)!.Status);   // the failed reopen changed nothing
    }

    [Fact]
    public void Migration_RepairsADatabaseThatAlreadyHasSeveralActiveSessions()
    {
        using var db = new TestDatabase();
        long newest;
        using (var store = db.Open())
        {
            var oldest = store.CreateSession(NewSession(BaseUtc));
            store.EndSession(oldest.Id, BaseUtc.AddMinutes(1), SessionStatus.Stopped, "done");
            store.CreateSession(NewSession(BaseUtc.AddMinutes(10)));
            newest = store.ListSessions(1)[0].Id;
        }

        // Rewind to a Milestone 003 database: no unique index, version 2, and
        // two orphaned Active sessions left behind by crashed recorders.
        using (var connection = new SqliteConnection($"Data Source={db.FilePath};Pooling=False"))
        {
            connection.Open();
            foreach (var sql in new[]
            {
                "DROP INDEX ux_session_single_active;",
                "INSERT INTO session (id, started_utc_ms, last_observation_utc_ms, status, est_other_watts, est_psu_efficiency, est_include_cpu, est_include_gpu) VALUES (0, 1000, 5000, 0, 50, 0.9, 1, 1);",
                "INSERT INTO sensor_availability (session_id, channel, state, from_utc_ms) VALUES (0, 0, 0, 1000);",
                "PRAGMA user_version = 2;"
            })
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        using var migrated = db.Open();

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Single(migrated.ListSessions(10), s => s.Status == SessionStatus.Active);
        Assert.Equal(newest, migrated.ListSessions(10).Single(s => s.Status == SessionStatus.Active).Id);   // the newest Active survives
        var closed = migrated.ListSessions(10).Single(s => s.StartedUtc == DateTimeOffset.FromUnixTimeMilliseconds(1000));
        Assert.Equal(SessionStatus.Interrupted, closed.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(5000), closed.EndedUtc);                     // ended at its last observation, nothing assumed after
        Assert.All(migrated.GetAvailability(closed.Id), a => Assert.NotNull(a.ToUtc));
        Assert.Equal("ok", migrated.CheckIntegrity());
        Assert.Throws<ActiveSessionExistsException>(() => migrated.CreateSession(NewSession()));          // and the guard is in force again
    }

    [Fact]
    public void MonitoringInterval_IsValidatedAndPersisted()
    {
        using var db = new TestDatabase();
        using (var store = db.Open())
        {
            Assert.Equal(SqliteEnergyStore.DefaultMonitoringIntervalMs, store.GetMonitoringIntervalMs());
            store.SetMonitoringIntervalMs(2500);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.SetMonitoringIntervalMs(249));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.SetMonitoringIntervalMs(60_001));
        }
        using var reopened = db.Open();
        Assert.Equal(2500, reopened.GetMonitoringIntervalMs());
    }
}
