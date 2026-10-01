using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using WattScope.Probe.Serve;
using static WattScope.Probe.Tests.Samples;

namespace WattScope.Probe.Tests;

/// <summary>
/// Monitoring lifecycle independent of any process/window/UI: start/stop
/// idempotency, single-session ownership, recovery, and the suspend/resume
/// state machine -- against a real SQLite database with hand-driven samples.
/// </summary>
public class MonitoringControllerTests
{
    private static async Task PushAsync(ControllerHarness h, int fromSeconds, int toSeconds, double? cpu = 100, double? gpu = 50, double utcOffset = 0, double monoOffset = 0)
    {
        for (var t = fromSeconds; t <= toSeconds; t++)
            h.Source.Push(At(t - fromSeconds, t + monoOffset, cpu, gpu, t + utcOffset));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Start_CreatesExactlyOneActiveSession_AndIsIdempotent()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);

        var first = await h.Controller.StartAsync();
        var second = await h.Controller.StartAsync();

        Assert.Equal("recording", first.State);
        Assert.False(first.AlreadyRunning);
        Assert.True(second.AlreadyRunning);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(h.Store.ListSessions(10));
        Assert.Single(h.Of<StatusMessage>(), s => s.State == "recording");
    }

    [Fact]
    public async Task ConcurrentStarts_ProduceOneSession()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => h.Controller.StartAsync()));

        Assert.Single(results.Select(r => r.SessionId).Distinct());
        Assert.Equal(1, results.Count(r => !r.AlreadyRunning));
        Assert.Single(h.Store.ListSessions(10));
    }

    [Fact]
    public async Task Stop_WhenIdle_IsANoOp()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);

        var snapshot = await h.Controller.StopAsync();

        Assert.Equal("idle", snapshot.State);
        Assert.Empty(h.Store.ListSessions(10));
        Assert.Empty(h.Of<StatusMessage>());
    }

    [Fact]
    public async Task Stop_FlushesEnergy_AndClosesTheSession()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        var started = await h.Controller.StartAsync();

        await PushAsync(h, 0, 11);
        await h.WaitForTicksAsync(12);
        var stopped = await h.Controller.StopAsync();

        Assert.Equal("idle", stopped.State);
        var session = h.Store.GetSession(started.SessionId!.Value)!;
        Assert.Equal(SessionStatus.Stopped, session.Status);
        Assert.Equal(11, EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(session.Id)).MonitoredSeconds, 6);
        Assert.Contains(h.Of<StatusMessage>(), s => s.State == "stopped");
    }

    [Fact]
    public async Task StartAfterStop_OpensANewSession_AndNeverTwoActive()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);

        var a = await h.Controller.StartAsync();
        await h.Controller.StopAsync();
        var b = await h.Controller.StartAsync();

        Assert.NotEqual(a.SessionId, b.SessionId);
        var sessions = h.Store.ListSessions(10);
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.Status == SessionStatus.Active);
    }

    [Fact]
    public async Task Resume_ContinuesTheSameSession_WithAPausedGap()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        var first = await h.Controller.StartAsync();
        await PushAsync(h, 0, 4);
        await h.WaitForTicksAsync(5);
        await h.Controller.StopAsync();

        // New run: the sampling clock restarts at 0, wall-clock time continues.
        var resumed = await h.Controller.StartAsync(resume: true);
        await PushAsync(h, 0, 4, utcOffset: 60);
        await h.WaitForTicksAsync(10);
        await h.Controller.StopAsync();

        Assert.Equal(first.SessionId, resumed.SessionId);
        Assert.Single(h.Store.ListSessions(10));
        var gap = Assert.Single(h.Store.GetSessionGaps(first.SessionId!.Value));
        Assert.Equal(GapReason.Paused, gap.Reason);
        Assert.Equal(8, EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(first.SessionId.Value)).MonitoredSeconds, 6); // 4 + 4, nothing for the pause
    }

    [Fact]
    public async Task Start_RecoversAnOrphanedActiveSession_BeforeOpeningItsOwn()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        // A recorder that died: its session is still marked Active.
        var orphan = h.Store.CreateSession(new NewSessionRequest { StartedUtc = BaseUtc.AddHours(-1), Estimation = new EstimationSettings(), Sensors = [] });

        var started = await h.Controller.StartAsync();

        Assert.NotEqual(orphan.Id, started.SessionId);
        Assert.Equal(SessionStatus.Interrupted, h.Store.GetSession(orphan.Id)!.Status);
        Assert.Single(h.Store.ListSessions(10), s => s.Status == SessionStatus.Active);
        Assert.Contains(h.Of<StatusMessage>(), s => s.State == "recovered" && s.SessionId == orphan.Id);
    }

    [Fact]
    public async Task Start_RejectsAnOutOfRangeInterval_WithoutCreatingASession()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Controller.StartAsync(intervalMs: 100));

        Assert.Empty(h.Store.ListSessions(10));
        Assert.Equal("idle", h.Controller.Snapshot().State);
    }

    [Fact]
    public async Task Snapshot_LetsAReopenedUiAttachToTheRunningSession()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        var started = await h.Controller.StartAsync();
        await PushAsync(h, 0, 2);
        await h.WaitForTicksAsync(3);

        var snapshot = h.Controller.Snapshot();

        Assert.Equal("recording", snapshot.State);
        Assert.Equal(started.SessionId, snapshot.SessionId);
        Assert.NotNull(snapshot.LastTick);
        Assert.Equal(started.SessionId, snapshot.LastTick!.SessionId);
    }

    // ---- suspend / resume -------------------------------------------------

    private static async Task<(MonitoringSession Session, IReadOnlyList<MonitoringGap> Gaps, EnergyTotals Totals)> FinishAsync(ControllerHarness h, long sessionId)
    {
        await h.Controller.StopAsync();
        return (h.Store.GetSession(sessionId)!, h.Store.GetSessionGaps(sessionId), EnergyReportBuilder.Sum(h.Store.GetSessionBuckets(sessionId)));
    }

    [Fact]
    public async Task SuspendResume_RecordsOneGap_AndIntegratesNothingAcrossTheSleep()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        var id = (await h.Controller.StartAsync()).SessionId!.Value;

        await PushAsync(h, 0, 4);
        await h.WaitForTicksAsync(5);
        h.Controller.OnSuspend();
        await PushAsync(h, 5, 8);                 // arrive while suspending: ignored
        await h.WaitForTicksAsync(9);
        h.Controller.OnResume();
        await PushAsync(h, 3600, 3604);           // an hour later
        await h.WaitForTicksAsync(14);

        var (_, gaps, totals) = await FinishAsync(h, id);

        var gap = Assert.Single(gaps);
        Assert.Equal(GapReason.Suspend, gap.Reason);
        Assert.Equal(BaseUtc.AddSeconds(4), gap.StartUtc);
        Assert.Equal(BaseUtc.AddSeconds(3600), gap.EndUtc);
        Assert.Equal(8, totals.MonitoredSeconds, 6);          // 4 s before + 4 s after; none of the hour
        Assert.Equal(8 * 100.0, totals.CpuPackageJoules, 6);
    }

    [Fact]
    public async Task DuplicateSuspendAndResumeEvents_StillRecordOneGap()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        var id = (await h.Controller.StartAsync()).SessionId!.Value;

        await PushAsync(h, 0, 4);
        await h.WaitForTicksAsync(5);
        h.Controller.OnSuspend();
        h.Controller.OnSuspend();
        h.Controller.OnSuspend();
        h.Controller.OnResume();
        h.Controller.OnResume();
        h.Controller.OnResume();
        await PushAsync(h, 500, 504);
        await h.WaitForTicksAsync(10);

        var (_, gaps, totals) = await FinishAsync(h, id);

        Assert.Equal(GapReason.Suspend, Assert.Single(gaps).Reason);
        Assert.Equal(8, totals.MonitoredSeconds, 6);
    }

    [Fact]
    public async Task ResumeEventWithoutASuspend_DoesNotCutAGapOutOfContinuousSampling()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        var id = (await h.Controller.StartAsync()).SessionId!.Value;

        await PushAsync(h, 0, 4);
        await h.WaitForTicksAsync(5);
        h.Controller.OnResume();                  // spurious
        await PushAsync(h, 5, 9);
        await h.WaitForTicksAsync(10);

        var (_, gaps, totals) = await FinishAsync(h, id);

        Assert.Empty(gaps);
        Assert.Equal(9, totals.MonitoredSeconds, 6);
    }

    [Theory]
    [InlineData(1.0)]   // wall clock jumped, the monotonic clock did not count the sleep -> clock discontinuity
    [InlineData(3600.0)] // both clocks counted the sleep -> sampling stall
    public async Task MissedSuspendNotification_ElapsedTimeFallbackStillRecordsTheGap(double monotonicAdvanceSeconds)
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        var id = (await h.Controller.StartAsync()).SessionId!.Value;

        await PushAsync(h, 0, 4);
        // No OnSuspend/OnResume at all: the sleep is only visible in the timestamps.
        for (var i = 0; i < 5; i++)
            h.Source.Push(At(5 + i, 4 + monotonicAdvanceSeconds + i, 100, 50, 3604 + i));
        await h.WaitForTicksAsync(10);

        var (_, gaps, totals) = await FinishAsync(h, id);

        var gap = Assert.Single(gaps);
        Assert.Contains(gap.Reason, new[] { GapReason.ClockDiscontinuity, GapReason.SamplingStall });
        // 4 s before the gap + 4 s after it (the first post-gap sample only starts a new chain); nothing for the sleep.
        Assert.Equal(8, totals.MonitoredSeconds, 6);
    }

    [Fact]
    public async Task SuspendWhileIdle_IsIgnored()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);

        h.Controller.OnSuspend();
        h.Controller.OnResume();

        Assert.Empty(h.Of<StatusMessage>());
        Assert.Empty(h.Store.ListSessions(10));
    }

    // ---- failure and honesty ----------------------------------------------

    [Fact]
    public async Task SamplingFailure_GoesIdle_ReportsTheError_AndClosesTheSession()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        h.Source.FailWith = new InvalidOperationException("sensor driver went away");

        var started = await h.Controller.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.Controller.Snapshot().State != "idle" && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal("idle", h.Controller.Snapshot().State);
        Assert.Contains(h.Of<StatusMessage>(), s => s.State == "error" && s.Message!.Contains("sensor driver went away"));
        Assert.Equal(SessionStatus.Stopped, h.Store.GetSession(started.SessionId!.Value)!.Status);
    }

    [Fact]
    public async Task PartialSession_NeverShowsACost_AndNeverShowsMissingCpuAsZero()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        h.Store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(-1), PricePerKwh = 0.30m }, BaseUtc);
        await h.Controller.StartAsync();

        await PushAsync(h, 0, 5, cpu: null, gpu: 80);    // CPU rejected as ImplausibleZero (unelevated)
        await h.WaitForTicksAsync(6);
        await h.Controller.StopAsync();

        var last = h.Of<TickMessage>().Last();
        Assert.Null(last.CpuWatts);
        Assert.Equal(ReadingState.ImplausibleZero, last.CpuState);
        Assert.Equal(80, last.GpuWatts);
        Assert.Equal(EstimateCompleteness.Partial, last.EstimateCompleteness);
        Assert.Equal(EstimateCompleteness.Partial, last.SessionCompleteness);
        Assert.Null(last.SessionCostApprox);
        Assert.All(h.Of<TickMessage>(), t => Assert.Null(t.SessionCostApprox));
    }

    [Fact]
    public async Task CompleteSession_ShowsACost()
    {
        using var db = new TempDb();
        await using var h = new ControllerHarness(db);
        h.Store.AddPrice(new ElectricityPrice { EffectiveFromUtc = BaseUtc.AddDays(-1), PricePerKwh = 0.30m }, BaseUtc);
        await h.Controller.StartAsync();

        await PushAsync(h, 0, 5, cpu: 100, gpu: 80);
        await h.WaitForTicksAsync(6);
        await h.Controller.StopAsync();

        var last = h.Of<TickMessage>().Last();
        Assert.Equal(EstimateCompleteness.Complete, last.SessionCompleteness);
        Assert.NotNull(last.SessionCostApprox);
        Assert.True(last.SessionCostApprox > 0);
    }
}
