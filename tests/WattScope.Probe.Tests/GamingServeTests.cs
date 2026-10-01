using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WattScope.Core.Gaming;
using WattScope.Core.Monitoring;
using WattScope.Hardware.Processes;
using WattScope.Probe.Serve;
using WattScope.Storage;
using static WattScope.Probe.Tests.Samples;

namespace WattScope.Probe.Tests;

internal sealed class ScriptedProcessSource : IProcessSource
{
    private readonly object _gate = new();
    private List<ProcessInfo> _processes = [];

    public void Set(params ProcessInfo[] processes)
    {
        lock (_gate) _processes = [.. processes];
    }

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        lock (_gate) return [.. _processes];
    }
}

internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = BaseUtc;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The sidecar with game detection wired in: a real controller, real SQLite (three connections like
/// production), a hand-driven sample source and a scripted process source. The poller is parked
/// (hour-long interval) and the tests drive observations at synthetic times, so nothing depends on a
/// wall clock or a real game.
/// </summary>
internal sealed class GamingFixture : IAsyncDisposable
{
    public static readonly ProcessInfo Cod = new(100, 1, "cod.exe", @"D:\Games\Call of Duty\cod.exe", null);

    public GamingFixture(TempDb db, GameDetectorOptions? options = null, TimeProvider? time = null, ISampleSource? source = null)
    {
        Db = db;
        Record = db.Open();
        GamingStore = db.Open();
        Query = db.Open();
        Source = (source as ChannelSource) ?? new ChannelSource();
        Clock = time as ManualClock ?? new ManualClock();
        Processes = new ScriptedProcessSource();
        Gaming = new GamingService(GamingStore, Processes, null, options ?? new GameDetectorOptions { PollInterval = TimeSpan.FromHours(1) }, NullLogger.Instance, time ?? Clock);
        Controller = new MonitoringController(Record, Source, [],
            new MonitoringControllerOptions { Elevated = false, CpuAvailable = true, GpuAvailable = true },
            message => { lock (Emitted) Emitted.Add(message); }, NullLogger.Instance, Gaming);
        Processor = new ServeRequestProcessor(Query, Controller, () => { }, NullLogger.Instance, Gaming);
    }

    public TempDb Db { get; }
    public SqliteEnergyStore Record { get; }
    public SqliteEnergyStore GamingStore { get; }
    public SqliteEnergyStore Query { get; }
    public ChannelSource Source { get; }
    public ManualClock Clock { get; }
    public ScriptedProcessSource Processes { get; }
    public GamingService Gaming { get; }
    public MonitoringController Controller { get; }
    public ServeRequestProcessor Processor { get; }
    public List<object> Emitted { get; } = [];
    private long _sequence;

    public List<TickMessage> Ticks() { lock (Emitted) return Emitted.OfType<TickMessage>().ToList(); }

    /// <summary>Pushes one sample at synthetic second <paramref name="t"/>, waits until it was processed, then reports the process list at that moment.</summary>
    public async Task StepAsync(double t, double? cpu, double? gpu, params ProcessInfo[] processes)
    {
        var before = Ticks().Count;
        Source.Push(At(_sequence++, t, cpu, gpu));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Ticks().Count == before)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Sample at {t} s was not processed.");
            await Task.Delay(2);
        }
        Clock.Now = BaseUtc + TimeSpan.FromSeconds(t);
        Gaming.Tracker.ObserveProcesses(BaseUtc + TimeSpan.FromSeconds(t), processes);
    }

    public async Task RunAsync(double from, double to, double? cpu, double? gpu, params ProcessInfo[] processes)
    {
        for (var t = from; t <= to; t++)
            await StepAsync(t, cpu, gpu, processes);
    }

    public async Task<JsonElement> SendAsync(string json)
    {
        var response = await Processor.ProcessLineAsync(json);
        Assert.NotNull(response);
        return JsonSerializer.SerializeToElement(response, ServeProtocol.JsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        await Controller.DisposeAsync();
        Gaming.Dispose();
        Record.Dispose();
        GamingStore.Dispose();
        Query.Dispose();
    }
}

public class GamingServeTests
{
    private static readonly ProcessInfo Cod = GamingFixture.Cod;

    // ---------------------------------------------------------------- controller + detection + energy

    [Fact]
    public async Task AGame_BecomesAGamingSession_InsideTheMonitoringSession_AndEndsWithIt()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        var monitoring = await f.Controller.StartAsync();

        await f.RunAsync(0, 9, 100, 200);
        await f.RunAsync(10, 60, 100, 200, Cod);

        var live = f.Ticks().Last().Gaming;
        Assert.NotNull(live);
        Assert.Equal("active", live!.State);
        Assert.Equal("Call of Duty", live.GameName);

        await f.Controller.StopAsync();

        var session = Assert.Single(f.Query.ListGamingSessions());
        Assert.Equal(monitoring.SessionId, session.MonitoringSessionId);              // a gaming session belongs to a monitoring session
        Assert.Equal(GamingSessionStatus.Ended, session.Status);
        Assert.Equal(GamingEndReason.MonitoringStopped, session.EndReason);
        var summary = GamingAnalytics.Summarize(session, f.Query.GetPriceSchedule(), DateTimeOffset.UtcNow);
        Assert.Equal(50, summary.MonitoredSeconds, 6);                                 // t = 10..60, from first sight
        Assert.Equal(100.0 * 50, summary.Totals.CpuPackageJoules, 6);
        Assert.Equal(200.0 * 50, summary.Totals.GpuBoardJoules, 6);
    }

    [Fact]
    public async Task TicksCarryNoGamingBlock_WhenNoGameIsRunning()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();

        await f.RunAsync(0, 30, 100, 200);                                             // nothing but "non-game applications"

        Assert.All(f.Ticks(), t => Assert.Null(t.Gaming));
        Assert.Empty(f.Query.ListGamingSessions());
    }

    [Fact]
    public async Task GamingEnergy_NeverExceedsMonitoringEnergy_AndSharesItsGaps()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        var monitoring = await f.Controller.StartAsync();

        await f.RunAsync(0, 40, 100, 200, Cod);
        await f.StepAsync(100, 100, 200, Cod);                                         // a 60 s stall: the monitor records a gap
        await f.RunAsync(101, 140, 100, 200, Cod);
        await f.Controller.StopAsync();

        var gaming = GamingAnalytics.Summarize(f.Query.ListGamingSessions().Single(), f.Query.GetPriceSchedule(), DateTimeOffset.UtcNow);
        var whole = f.Query.GetSessionBuckets(monitoring.SessionId!.Value).Aggregate(new WattScope.Core.Energy.EnergyTotals(), (a, b) => a + b.Totals);

        Assert.True(gaming.MonitoredSeconds <= whole.MonitoredSeconds + 1e-9);
        Assert.Equal(whole.MonitoredSeconds, gaming.MonitoredSeconds, 6);               // the game ran the whole time, so both see exactly the same verified time
        Assert.True(gaming.DurationSeconds > gaming.MonitoredSeconds + 50);             // and the 60 s stall is visible as unmonitored duration
    }

    [Fact]
    public async Task MonitoringStopped_WithNoGame_LeavesNoGamingState()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();
        await f.RunAsync(0, 5, 100, 200);

        await f.Controller.StopAsync();

        Assert.Empty(f.Query.ListGamingSessions());
        Assert.Null(f.Gaming.Tracker.Live());
        Assert.False(f.Gaming.Tracker.IsAttached);
    }

    [Fact]
    public async Task SuspendAndResume_DuringAGame_KeepsOneSession_WithNoEnergyForTheSleep()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();
        await f.RunAsync(0, 40, 100, 200, Cod);

        f.Controller.OnSuspend();
        f.Controller.OnResume();
        await f.StepAsync(3600, 100, 200, Cod);                                        // an hour later
        await f.RunAsync(3601, 3640, 100, 200, Cod);
        await f.Controller.StopAsync();

        var session = Assert.Single(f.Query.ListGamingSessions());
        var summary = GamingAnalytics.Summarize(session, f.Query.GetPriceSchedule(), DateTimeOffset.UtcNow);
        Assert.Equal(40 + 40, summary.MonitoredSeconds, 6);                            // [0..40] + [3600..3640]; not the hour in between
        Assert.True(summary.DurationSeconds > 3600);
    }

    [Fact]
    public async Task IntervalChange_RestartsMonitoringInTheSameSession_AndKeepsTheGamingSession()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();
        await f.RunAsync(0, 40, 100, 200, Cod);
        var gamingId = f.Gaming.Tracker.ActiveGamingSessionId;
        Assert.NotNull(gamingId);

        var response = await f.SendAsync("{\"id\":1,\"cmd\":\"set_monitoring_interval\",\"intervalMs\":2000}");
        Assert.True(response.GetProperty("ok").GetBoolean());
        await f.RunAsync(60, 90, 100, 200, Cod);
        await f.Controller.StopAsync();

        var session = Assert.Single(f.Query.ListGamingSessions());                     // one session, not two
        Assert.Equal(gamingId, session.Id);
    }

    // ---------------------------------------------------------------- sidecar restart / crash recovery

    [Fact]
    public async Task SidecarRestart_DuringAGame_RecoversTheOrphan_AndStartsAFreshSession()
    {
        using var db = new TempDb();
        long firstGaming;
        using (var crashed = db.Open())
        {
            var m = crashed.CreateSession(new NewSessionRequest { StartedUtc = BaseUtc, Estimation = new WattScope.Core.Estimation.EstimationSettings(), Sensors = [] });
            firstGaming = crashed.CreateGamingSession(new NewGamingSession
            {
                MonitoringSessionId = m.Id,
                IsManual = false,
                StartedUtc = BaseUtc,
                Game = new GameIdentity { Key = "cod.exe", ExeName = "cod.exe", DisplayName = "Call of Duty", Source = GameDetectionSource.KnownCatalog, Tier = DetectionTier.Mapped }
            });
            crashed.SaveGamingProgress(firstGaming, BaseUtc.AddSeconds(30), new GamingPeaks(), []);
            // ... and the sidecar process dies here: nothing is closed.
        }

        await using var f = new GamingFixture(db, time: new ManualClock { Now = BaseUtc.AddHours(1) });
        await f.Controller.StartAsync();                                                // the new sidecar: recovers, then monitors
        var first = f.Query.GetGamingSession(firstGaming)!;
        Assert.Equal(GamingSessionStatus.Interrupted, first.Status);                    // the orphan was closed, never left Active
        Assert.Equal(BaseUtc.AddSeconds(30), first.EndedUtc);

        // The game is still running: a *new* gaming session is detected, in the new monitoring session.
        var t0 = 3600.0;
        for (var t = t0; t <= t0 + 40; t++)
            await f.StepAsync(t, 100, 200, Cod);

        var sessions = f.Query.ListGamingSessions();
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.Status == GamingSessionStatus.Active);
        Assert.NotEqual(firstGaming, sessions.Single(s => s.Status == GamingSessionStatus.Active).Id);
    }

    [Fact]
    public async Task OnlyOneGamingSession_IsActive_AtAnyTime_EvenWithTwoGamesRunning()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();
        var other = new ProcessInfo(300, 1, "eldenring.exe", @"D:\Games\EldenRing\eldenring.exe", null);

        await f.RunAsync(0, 60, 100, 200, Cod, other);

        var all = f.Query.ListGamingSessions();
        Assert.Single(all);
        Assert.Equal("cod.exe", all[0].GameKey);
    }

    // ---------------------------------------------------------------- protocol: mappings, manual control

    [Fact]
    public async Task Mappings_CanBeAddedListedAndRemoved_ThroughTheProtocol()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);

        var set = await f.SendAsync("{\"id\":1,\"cmd\":\"set_game_mapping\",\"exeName\":\"MyGame.exe\",\"mappingKind\":\"game\",\"displayName\":\"My Game\"}");
        Assert.True(set.GetProperty("ok").GetBoolean());
        var list = await f.SendAsync("{\"id\":2,\"cmd\":\"get_game_mappings\"}");
        Assert.Equal("My Game", list.GetProperty("data")[0].GetProperty("displayName").GetString());
        Assert.Equal("game", list.GetProperty("data")[0].GetProperty("kind").GetString());

        var removed = await f.SendAsync("{\"id\":3,\"cmd\":\"remove_game_mapping\",\"exeName\":\"mygame.exe\"}");
        Assert.True(removed.GetProperty("data").GetProperty("removed").GetBoolean());
        Assert.Empty(f.Query.GetGameMappings());
    }

    [Theory]
    [InlineData("{\"id\":1,\"cmd\":\"set_game_mapping\",\"mappingKind\":\"game\"}")]                                   // no executable
    [InlineData("{\"id\":1,\"cmd\":\"set_game_mapping\",\"exeName\":\"a.exe\"}")]                                       // no kind
    [InlineData("{\"id\":1,\"cmd\":\"set_game_mapping\",\"exeName\":\"a|b.exe\",\"mappingKind\":\"game\"}")]            // invalid file name
    [InlineData("{\"id\":1,\"cmd\":\"set_game_mapping\",\"exeName\":\"a.exe\",\"mappingKind\":\"banana\"}")]            // unknown kind
    [InlineData("{\"id\":1,\"cmd\":\"get_gaming_session\"}")]                                                          // no id
    [InlineData("{\"id\":1,\"cmd\":\"get_gaming_session\",\"gamingSessionId\":99}")]                                    // unknown id
    public async Task InvalidGamingRequests_BecomeStructuredErrors(string line)
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);

        var response = await f.SendAsync(line);

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(response.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task UserMapping_AddedWhileMonitoring_MakesAnUnknownProgramDetectable()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();
        var indie = new ProcessInfo(50, 1, "indie.exe", @"C:\Tools\indie.exe", null);

        await f.RunAsync(0, 40, 100, 200, indie);
        Assert.Empty(f.Query.ListGamingSessions());                                     // unknown: not a game

        await f.SendAsync("{\"id\":1,\"cmd\":\"set_game_mapping\",\"exeName\":\"indie.exe\",\"mappingKind\":\"game\",\"displayName\":\"Indie\"}");
        await f.RunAsync(41, 90, 100, 200, indie);

        Assert.Equal("Indie", Assert.Single(f.Query.ListGamingSessions()).DisplayName);
    }

    [Fact]
    public async Task ExcludingTheRunningGame_ThroughTheProtocol_EndsItImmediately()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();
        await f.RunAsync(0, 40, 100, 200, Cod);

        await f.SendAsync("{\"id\":1,\"cmd\":\"set_game_mapping\",\"exeName\":\"cod.exe\",\"mappingKind\":\"excluded\"}");

        Assert.Equal(GamingEndReason.Excluded, Assert.Single(f.Query.ListGamingSessions()).EndReason);
        await f.RunAsync(41, 120, 100, 200, Cod);
        Assert.Single(f.Query.ListGamingSessions());                                    // and it is not detected again
    }

    [Fact]
    public async Task ManualGaming_RequiresMonitoring_ThenStartsAndStops_AndIsMarkedManual()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);

        var refused = await f.SendAsync("{\"id\":1,\"cmd\":\"start_manual_gaming\",\"displayName\":\"Retro\"}");
        Assert.False(refused.GetProperty("ok").GetBoolean());

        await f.Controller.StartAsync();
        await f.RunAsync(0, 10, 100, 200);
        var started = await f.SendAsync("{\"id\":2,\"cmd\":\"start_manual_gaming\",\"displayName\":\"Retro\",\"exeName\":\"retro.exe\",\"rememberAsGame\":true}");
        Assert.True(started.GetProperty("ok").GetBoolean());
        Assert.True(started.GetProperty("data").GetProperty("live").GetProperty("isManual").GetBoolean());
        Assert.Equal("manual", started.GetProperty("data").GetProperty("live").GetProperty("source").GetString());

        var second = await f.SendAsync("{\"id\":3,\"cmd\":\"start_manual_gaming\",\"displayName\":\"Another\"}");
        Assert.False(second.GetProperty("ok").GetBoolean());                            // one active gaming session at a time

        await f.RunAsync(11, 40, 100, 200);
        var stopped = await f.SendAsync("{\"id\":4,\"cmd\":\"stop_manual_gaming\"}");
        Assert.True(stopped.GetProperty("ok").GetBoolean());

        var session = Assert.Single(f.Query.ListGamingSessions());
        Assert.True(session.IsManual);
        Assert.Equal(GamingEndReason.ManualStop, session.EndReason);
        Assert.Equal("Retro", Assert.Single(f.Query.GetGameMappings()).DisplayName);    // "remember as game" stored a mapping
    }

    [Fact]
    public async Task GamingState_History_Detail_AndAggregates_AreServed()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        await f.Controller.StartAsync();
        await f.RunAsync(0, 60, 100, 200, Cod);

        var state = await f.SendAsync("{\"id\":1,\"cmd\":\"get_gaming_state\"}");
        Assert.True(state.GetProperty("data").GetProperty("monitoring").GetBoolean());
        Assert.Equal("active", state.GetProperty("data").GetProperty("live").GetProperty("state").GetString());

        await f.Controller.StopAsync();

        var list = await f.SendAsync("{\"id\":2,\"cmd\":\"get_gaming_sessions\"}");
        var first = list.GetProperty("data")[0];
        Assert.Equal("cod.exe", first.GetProperty("gameKey").GetString());
        var id = first.GetProperty("id").GetInt64();

        var detail = await f.SendAsync($"{{\"id\":3,\"cmd\":\"get_gaming_session\",\"gamingSessionId\":{id}}}");
        Assert.True(detail.GetProperty("ok").GetBoolean());
        Assert.Equal(0, detail.GetProperty("data").GetProperty("gaps").GetProperty("count").GetInt32());

        var aggregates = await f.SendAsync("{\"id\":4,\"cmd\":\"get_game_aggregates\"}");
        Assert.Equal(1, aggregates.GetProperty("data")[0].GetProperty("sessionCount").GetInt32());
    }

    [Fact]
    public async Task RunningExecutables_ListsOnlyPlausibleCandidates_OnDemand()
    {
        using var db = new TempDb();
        await using var f = new GamingFixture(db);
        f.Processes.Set(
            Cod,
            new ProcessInfo(2, 1, "chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe", null),
            new ProcessInfo(3, 1, "Battle.net.exe", @"C:\Program Files (x86)\Battle.net\Battle.net.exe", null),
            new ProcessInfo(4, 1, "svchost.exe", @"C:\Windows\System32\svchost.exe", null),
            new ProcessInfo(5, 1, "notepad_plus.exe", @"C:\Tools\notepad_plus.exe", null),
            new ProcessInfo(6, 1, "winhelper.exe", @"C:\Windows\winhelper.exe", null));

        var response = await f.SendAsync("{\"id\":1,\"cmd\":\"get_running_executables\"}");

        var names = response.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("exeName").GetString()).ToList();
        Assert.Contains("cod.exe", names);
        Assert.Contains("notepad_plus.exe", names);
        Assert.DoesNotContain("chrome.exe", names);
        Assert.DoesNotContain("Battle.net.exe", names);
        Assert.DoesNotContain("svchost.exe", names);
        Assert.DoesNotContain("winhelper.exe", names);                                  // an operating-system folder
    }

    // ---------------------------------------------------------------- the real poller (real clock, scripted processes)

    [Fact]
    public async Task ThePoller_DetectsAndEndsAGame_ByItself_WithNoUiInvolved()
    {
        using var db = new TempDb();
        var options = new GameDetectorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(25),
            ConfirmDelayMapped = TimeSpan.FromMilliseconds(150),
            EndGracePeriod = TimeSpan.FromMilliseconds(250)
        };
        await using var f = new GamingFixture(db, options, time: TimeProvider.System);
        await f.Controller.StartAsync();

        using var feeder = new CancellationTokenSource();
        var feed = Task.Run(async () =>                                                  // live samples at real times, ~100 Hz
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long seq = 0;
            while (!feeder.IsCancellationRequested)
            {
                f.Source.Push(new SampleEnvelope(seq++, new WattScope.Core.Energy.PowerSample
                {
                    UtcTime = DateTimeOffset.UtcNow,
                    MonotonicTime = sw.Elapsed,
                    CpuPackage = WattScope.Core.Energy.ChannelReading.FromWatts(100),
                    GpuBoard = WattScope.Core.Energy.ChannelReading.FromWatts(200)
                }));
                await Task.Delay(10);
            }
        });

        try
        {
            f.Processes.Set(Cod);
            await WaitUntilAsync(() => f.Query.ListGamingSessions().Any(s => s.Status == GamingSessionStatus.Active), "the poller to confirm the game");

            f.Processes.Set();                                                           // the game closes
            await WaitUntilAsync(() => f.Query.ListGamingSessions().All(s => s.Status != GamingSessionStatus.Active), "the session to end after the grace period");
        }
        finally
        {
            feeder.Cancel();
            await feed;
        }

        var session = Assert.Single(f.Query.ListGamingSessions());
        Assert.Equal(GamingEndReason.ProcessExited, session.EndReason);
        var summary = GamingAnalytics.Summarize(session, f.Query.GetPriceSchedule(), DateTimeOffset.UtcNow);
        Assert.True(summary.MonitoredSeconds > 0.1, $"expected some monitored gaming time, got {summary.MonitoredSeconds}");
        Assert.Equal(100.0, summary.AvgCpuWatts!.Value, 3);                              // constant 100 W CPU; no zero-filling anywhere
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    // ---------------------------------------------------------------- the real (Windows) process source

    [Fact]
    public void WindowsProcessSource_SeesThisProcess_WithItsPathAndParent_WithoutThrowing()
    {
        var source = new WindowsProcessSource();

        var first = source.Snapshot();
        var second = source.Snapshot();                                                  // the second poll is served from the cache

        var self = first.Single(p => p.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(self.ExeName));
        Assert.False(string.IsNullOrEmpty(self.ExePath));                                // the minimal query-only right is enough for our own processes
        Assert.True(self.StartUtc is not null);
        Assert.True(first.Count > 20);
        Assert.InRange(second.Count, first.Count - 30, first.Count + 30);                  // same machine, almost the same process list
        Assert.Contains(first, p => p.Pid == self.ParentPid || p.ParentPid == self.ParentPid);
    }

    [Fact]
    public void WindowsProcessSource_DoesNotOpenKnownSystemProcesses()
    {
        var source = new WindowsProcessSource();

        var snapshot = source.Snapshot();

        Assert.All(snapshot.Where(p => GameCatalog.NeverGame.Contains(p.ExeName) && !GameCatalog.Launchers.Contains(p.ExeName)),
            p => Assert.Null(p.ExePath));                                                // never opened, so no path (nor handle) was ever obtained
    }
}
