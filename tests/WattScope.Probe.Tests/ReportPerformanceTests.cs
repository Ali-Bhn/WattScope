using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WattScope.Core.Reporting;
using WattScope.Probe.Commands;
using Xunit.Abstractions;

namespace WattScope.Probe.Tests;

/// <summary>
/// Milestone 006 performance pass: the read queries behind Reports, run against a year of synthetic 15-minute
/// buckets in a scratch database. The bounds are deliberately generous (they catch an accidental full scan or
/// an N+1, not machine noise); the measured timings are written to the test output.
/// </summary>
public class ReportPerformanceTests(ITestOutputHelper output)
{
    private const int Days = 365;

    private static void SeedYear(string path)
    {
        using var store = SqliteEnergyStoreOpen(path);
        // Ten sessions across the year; one bucket per 15 minutes, in raw SQL (this measures reads, not the recorder).
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        var start = DateTimeOffset.UtcNow.AddDays(-Days).ToUnixTimeSeconds() / 900 * 900;
        using var tx = connection.BeginTransaction();
        for (var s = 1; s <= 10; s++)
        {
            Exec(connection, tx, $"INSERT INTO session (id, started_utc_ms, ended_utc_ms, last_observation_utc_ms, status, end_reason, est_other_watts, est_psu_efficiency, est_include_cpu, est_include_gpu) VALUES ({s}, {(start + (s - 1) * 36 * 86400L) * 1000}, {(start + s * 36 * 86400L) * 1000}, {(start + s * 36 * 86400L) * 1000}, 1, 'x', 50, 0.9, 1, 1);");
        }
        Exec(connection, tx, $"""
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {Days * 96 - 1})
            INSERT INTO energy_bucket (session_id, bucket_start_utc_s, monitored_s, cpu_j, cpu_s, gpu_j, gpu_s, est_complete_j, est_complete_s, est_partial_j, est_partial_s)
            SELECT 1 + (i / {Days * 96 / 10 + 1}), {start} + i * 900, 900, 90000, 900, 150000, 900, 300000, 900, 0, 0 FROM n;
            """);
        tx.Commit();
    }

    private static WattScope.Storage.SqliteEnergyStore SqliteEnergyStoreOpen(string path) => WattScope.Storage.SqliteEnergyStore.Open(path);

    private static void Exec(SqliteConnection c, SqliteTransaction t, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ReportQueries_OverAYearOfBuckets_StayFast()
    {
        using var db = new TempDb();
        SeedYear(db.FilePath);
        using var store = db.Open();
        var prices = store.GetPriceSchedule();
        var tz = TimeZoneInfo.Local;

        T("get_db_info (statistics)", () => store.GetStatistics(), 1500, out var stats);
        Assert.Equal(Days * 96, stats.EnergyBuckets);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var (dayStart, dayEnd) = ReportPeriods.LocalDay(today.AddDays(-30), tz);
        T("day report", () => DatabaseCommands.BuildPeriodReport("d", dayStart, dayEnd, store, prices, tz, daily: false), 500, out _);

        var (monthStart, monthEnd) = ReportPeriods.LocalMonth(today.AddMonths(-2).Year, today.AddMonths(-2).Month, tz);
        T("month report + daily breakdown", () => DatabaseCommands.BuildPeriodReport("m", monthStart, monthEnd, store, prices, tz, daily: true), 1000, out var month);
        Assert.NotNull(month.Daily);

        T("session list (50) with totals", () => store.ListSessions(50).Select(s => EnergyReportBuilder.Sum(store.GetSessionBuckets(s.Id))).ToList(), 1500, out var sessions);
        Assert.Equal(10, sessions.Count);

        T("quick_check", () => store.CheckIntegrity(quick: true), 3000, out var check);
        Assert.Equal("ok", check);
    }

    private void T<R>(string name, Func<R> work, long budgetMs, out R result)
    {
        var sw = Stopwatch.StartNew();
        result = work();
        sw.Stop();
        output.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms (budget {budgetMs} ms)");
        Assert.True(sw.ElapsedMilliseconds < budgetMs, $"{name} took {sw.ElapsedMilliseconds} ms (budget {budgetMs} ms)");
    }
}
