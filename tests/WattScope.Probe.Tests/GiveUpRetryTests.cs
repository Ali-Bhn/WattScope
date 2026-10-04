using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Reporting;
using WattScope.Probe.Serve;
using static WattScope.Probe.Tests.Samples;

namespace WattScope.Probe.Tests;

/// <summary>
/// The session a user gets from Retry (or tray Start) after the host gave up restarting a crashing
/// sidecar. The host's sequence is: <c>--serve</c>, three <c>--serve --resume</c>, give-up, and then --
/// only on the user's action -- a fresh <c>--serve</c>. Here each crashed process is a recorder that
/// wrote to the database and vanished without stopping its session; the retry is a new controller on
/// the same database. Nothing of the abandoned attempt may reach the fresh session.
/// </summary>
public class GiveUpRetryTests
{
    private const int CrashedRuns = 4; // the first launch and its three bounded restarts

    /// <summary>One sidecar process that records a few seconds (CPU unreadable, GPU 50 W) and then dies: no Stop, the session stays Active.</summary>
    private static long CrashedRun(TempDb db, bool resume, double utcStart)
    {
        using var store = db.Open();
        store.RecoverInterruptedSessions();
        var monitor = new EnergyMonitor(store, new EnergyMonitorOptions { Integration = IntegrationOptions.ForSamplingInterval(TimeSpan.FromSeconds(1)) });
        var session = resume
            ? monitor.Resume(store.GetLatestSession()!.Id, [])
            : monitor.Start([], store.GetEstimationSettings(), processElevated: false, appVersion: "crashed");
        for (var t = 0; t <= 4; t++)
            monitor.OnSample(At(t, t, cpu: null, gpu: 50, utcStart + t).Sample);
        monitor.Flush();
        return session.Id;
    }

    /// <summary>The database as the give-up leaves it: one session, still marked Active, with four crashed runs in it.</summary>
    private static long CrashLoopUntilGiveUp(TempDb db)
    {
        var id = CrashedRun(db, resume: false, utcStart: 0);
        for (var restart = 1; restart < CrashedRuns; restart++)
            Assert.Equal(id, CrashedRun(db, resume: true, utcStart: restart * 100));
        return id;
    }

    [Fact]
    public async Task Retry_AfterGiveUp_ClosesTheAbandonedSession_AndOpensExactlyOneCleanSession()
    {
        using var db = new TempDb();
        var abandonedId = CrashLoopUntilGiveUp(db);
        await using var h = new ControllerHarness(db);
        var abandonedTotals = EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(abandonedId));
        var abandonedGaps = h.Store.GetSessionGaps(abandonedId).Count;
        Assert.Equal(SessionStatus.Active, h.Store.GetSession(abandonedId)!.Status); // nothing closed it: the process died
        Assert.Equal(CrashedRuns * 4, abandonedTotals.MonitoredSeconds, 6);
        Assert.Equal(CrashedRuns - 1, abandonedGaps);                               // one Interrupted gap per restart

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var started = await h.Controller.StartAsync();                               // the user's Retry: a fresh --serve

        // The abandoned session: Interrupted at its last persisted observation, otherwise untouched.
        var abandoned = h.Store.GetSession(abandonedId)!;
        Assert.Equal(SessionStatus.Interrupted, abandoned.Status);
        Assert.Equal(abandoned.LastObservationUtc, abandoned.EndedUtc);
        Assert.Equal(BaseUtc.AddSeconds(304), abandoned.EndedUtc);
        Assert.Equal(abandonedTotals, EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(abandonedId)));
        Assert.Equal(abandonedGaps, h.Store.GetSessionGaps(abandonedId).Count);
        Assert.Contains(h.Of<StatusMessage>(), s => s.State == "recovered" && s.SessionId == abandonedId);

        // The fresh session: a new row, the only Active one, starting now and starting empty.
        Assert.False(started.AlreadyRunning);
        Assert.NotEqual(abandonedId, started.SessionId);
        var freshId = started.SessionId!.Value;
        var sessions = h.Store.ListSessions(10);
        Assert.Equal(2, sessions.Count);
        Assert.Equal(freshId, Assert.Single(sessions, s => s.Status == SessionStatus.Active).Id);
        var fresh = h.Store.GetSession(freshId)!;
        Assert.True(fresh.StartedUtc >= before, $"fresh session starts at {fresh.StartedUtc:O}, before the retry at {before:O}");
        Assert.Null(fresh.LastObservationUtc);
        Assert.Null(fresh.EndedUtc);
        Assert.Empty(h.Store.GetSessionBuckets(freshId));
        Assert.Empty(h.Store.GetSessionGaps(freshId));
        Assert.Empty(h.Store.GetSamples(freshId));
        Assert.Empty(h.Store.GetAvailability(freshId));
        Assert.Null(started.LastTick);
        Assert.Equal(0, started.Diagnostics!.Samples);
        Assert.Equal(0, started.Diagnostics.GapsRecorded);
    }

    [Fact]
    public async Task FreshSession_AfterGiveUp_AccumulatesOnlyItsOwnSamples_AndItsOwnSensorState()
    {
        using var db = new TempDb();
        var abandonedId = CrashLoopUntilGiveUp(db);
        await using var h = new ControllerHarness(db);
        var abandonedTotals = EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(abandonedId));
        var freshId = (await h.Controller.StartAsync()).SessionId!.Value;

        // The abandoned attempt never read the CPU; the fresh one does (100 W) and the GPU at 80 W, an hour later.
        for (var t = 0; t <= 4; t++)
            h.Source.Push(At(t, t, cpu: 100, gpu: 80, 3600 + t));
        await h.WaitForTicksAsync(5);
        var last = h.Of<TickMessage>().Last();
        await h.Controller.StopAsync();

        // No duration, energy, gap or unavailable-CPU state leaked in from the abandoned session.
        Assert.All(h.Of<TickMessage>(), t => Assert.Equal(freshId, t.SessionId));
        Assert.Equal(4, last.SessionSeconds, 6);
        Assert.Equal(100, last.CpuWatts);
        Assert.Equal(ReadingState.Valid, last.CpuState);
        Assert.Equal(EstimateCompleteness.Complete, last.SessionCompleteness);
        var totals = EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(freshId));
        Assert.Equal(4, totals.MonitoredSeconds, 6);
        Assert.Equal(4 * 100.0, totals.CpuPackageJoules, 6);
        Assert.Equal(4 * 80.0, totals.GpuBoardJoules, 6);
        Assert.Empty(h.Store.GetSessionGaps(freshId));
        Assert.All(h.Store.GetSamples(freshId), s => Assert.True(s.UtcTime >= BaseUtc.AddSeconds(3600)));
        Assert.Equal(SessionStatus.Stopped, h.Store.GetSession(freshId)!.Status);

        // And nothing of the fresh session went back into the abandoned one.
        Assert.Equal(SessionStatus.Interrupted, h.Store.GetSession(abandonedId)!.Status);
        Assert.Equal(abandonedTotals, EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(abandonedId)));
        Assert.Equal(0, abandonedTotals.CpuPackageJoules);
        Assert.DoesNotContain(h.Store.ListSessions(10), s => s.Status == SessionStatus.Active);
    }

    [Fact]
    public async Task RepeatedRetry_AfterGiveUp_NeverCreatesASecondFreshSession()
    {
        using var db = new TempDb();
        var abandonedId = CrashLoopUntilGiveUp(db);
        await using var h = new ControllerHarness(db);

        // A double click on Retry, or Retry and tray Start together: every start reaches the same controller.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Controller.StartAsync()));

        var freshId = Assert.Single(results.Select(r => r.SessionId).Distinct());
        Assert.NotEqual(abandonedId, freshId);
        Assert.Equal(1, results.Count(r => !r.AlreadyRunning));
        Assert.Equal(2, h.Store.ListSessions(10).Count);
        Assert.Single(h.Store.ListSessions(10), s => s.Status == SessionStatus.Active);
        Assert.Single(h.Of<StatusMessage>(), s => s.State == "recovered");
    }
}
