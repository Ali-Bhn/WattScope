using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Sensors;
using WattScope.Probe.Serve;
using WattScope.Storage;
using static WattScope.Probe.Tests.Samples;

namespace WattScope.Probe.Tests;

/// <summary>Milestone 006 protocol additions: first run, guarded pricing, diagnostics, maintenance and the guarded history deletion.</summary>
public class AppProtocolTests
{
    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Set(DateTimeOffset value) => _now = value;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly DiagnosticsInfo Info = new()
    {
        AppVersion = "9.9.9",
        OsDescription = "Windows 11 test",
        Architecture = "X64",
        Elevated = false,
        Providers = [new ProviderAvailability { ProviderId = "Nvml", IsAvailable = true }, new ProviderAvailability { ProviderId = "LibreHardwareMonitor", IsAvailable = true, RequiresElevation = true, UnavailableReason = "needs elevation" }],
        Sensors = [new EnergySensorBinding { Channel = EnergyChannel.GpuBoard, ProviderId = "Nvml", HardwareId = "g", HardwareName = "Test GPU", SensorId = "s", SensorName = "GPU Board Power" }],
        DatabasePath = "ignored"
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(TempDb db, DiagnosticsInfo? info = null)
        {
            Harness = new ControllerHarness(db);
            Query = db.Open();
            Processor = new ServeRequestProcessor(Query, Harness.Controller, () => { }, NullLogger.Instance, diagnostics: info);
        }

        public ControllerHarness Harness { get; }
        public SqliteEnergyStore Query { get; }
        public ServeRequestProcessor Processor { get; }
        private int _id;

        public async Task<ServeResponse> SendAsync(string cmd, string extra = "")
        {
            var line = $"{{\"id\":{++_id},\"cmd\":\"{cmd}\"{(extra.Length > 0 ? "," + extra : "")}}}";
            return (await Processor.ProcessLineAsync(line))!;
        }

        /// <summary>One finished session of <paramref name="seconds"/> seconds that started <paramref name="daysAgo"/> days ago.</summary>
        public long Seed(int daysAgo, int seconds = 600)
        {
            var start = DateTimeOffset.UtcNow.AddDays(-daysAgo);
            var time = new SettableTime(start);
            var monitor = new EnergyMonitor(Query, timeProvider: time);
            var id = monitor.Start([], new EstimationSettings()).Id;
            for (var t = 0; t <= seconds; t++)
                monitor.OnSample(new PowerSample
                {
                    UtcTime = start + TimeSpan.FromSeconds(t),
                    MonotonicTime = TimeSpan.FromSeconds(t),
                    CpuPackage = ChannelReading.FromWatts(100),
                    GpuBoard = ChannelReading.FromWatts(100)
                });
            time.Set(start + TimeSpan.FromSeconds(seconds));
            monitor.Stop();
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            await Harness.DisposeAsync();
            Query.Dispose();
        }
    }

    private static JsonElement Data(ServeResponse response) =>
        JsonSerializer.SerializeToElement(response.Data, ServeProtocol.JsonOptions);

    // ---------------------------------------------------------------- first run

    [Fact]
    public async Task FirstRun_IsOffered_UntilCompleted_AndStaysCompletedAcrossRestarts()
    {
        using var db = new TempDb();
        await using (var f = new Fixture(db))
        {
            Assert.False(Data(await f.SendAsync("get_app_state")).GetProperty("firstRunCompleted").GetBoolean());
            Assert.True(Data(await f.SendAsync("complete_first_run")).GetProperty("firstRunCompleted").GetBoolean());
            Assert.True(Data(await f.SendAsync("complete_first_run")).GetProperty("firstRunCompleted").GetBoolean()); // idempotent
        }

        await using var again = new Fixture(db);
        Assert.True(Data(await again.SendAsync("get_app_state")).GetProperty("firstRunCompleted").GetBoolean());
    }

    // ---------------------------------------------------------------- pricing guards

    [Theory]
    [InlineData("\"pricePerKwh\":100.01", "must not exceed")]
    [InlineData("\"pricePerKwh\":0.1234567", "decimal places")]
    [InlineData("\"pricePerKwh\":-1", "Negative")]
    public async Task SetPrice_RejectsImplausibleValues(string body, string fragment)
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("set_price", body);

        Assert.False(response.Ok);
        Assert.Contains(fragment, response.Error);
        Assert.Empty(f.Query.GetPriceSchedule().Entries);
    }

    [Fact]
    public async Task SetPrice_AcceptsAnExactDecimalString_WithoutBinaryFloatingPointDrift()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("set_price", "\"pricePerKwh\":\"0.3050\"");

        Assert.True(response.Ok);
        Assert.Equal(0.3050m, f.Query.GetPriceSchedule().Entries.Single().PricePerKwh);
    }

    [Fact]
    public async Task SetPrice_ThatReachesIntoRecordedEnergy_NeedsExplicitConfirmation_AndNeverEditsExistingPrices()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        f.Seed(daysAgo: 3);
        var past = DateTimeOffset.UtcNow.AddDays(-4).ToString("O");

        var refused = await f.SendAsync("set_price", $"\"pricePerKwh\":\"0.40\",\"effectiveFrom\":\"{past}\"");

        Assert.False(refused.Ok);
        Assert.Contains("already recorded", refused.Error);
        Assert.Empty(f.Query.GetPriceSchedule().Entries);

        var accepted = await f.SendAsync("set_price", $"\"pricePerKwh\":\"0.40\",\"effectiveFrom\":\"{past}\",\"confirmHistorical\":true");
        Assert.True(accepted.Ok);
        Assert.Single(f.Query.GetPriceSchedule().Entries);

        // A later price is an additional entry, not an edit: the earlier one is untouched.
        var later = await f.SendAsync("set_price", "\"pricePerKwh\":\"0.50\"");
        Assert.True(later.Ok);
        var entries = f.Query.GetPriceSchedule().Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal(0.40m, entries[0].PricePerKwh);
    }

    [Fact]
    public async Task SetPrice_InThePast_WithNothingRecordedThere_NeedsNoConfirmation()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        var past = DateTimeOffset.UtcNow.AddDays(-4).ToString("O");

        var response = await f.SendAsync("set_price", $"\"pricePerKwh\":\"0.40\",\"effectiveFrom\":\"{past}\"");

        Assert.True(response.Ok);
    }

    [Fact]
    public async Task SetPrice_KeepsTheScheduleCurrency_AndRefusesAMixedOne()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        await f.SendAsync("set_price", "\"pricePerKwh\":\"0.30\",\"currency\":\"USD\"");

        var later = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        var implicitCurrency = await f.SendAsync("set_price", $"\"pricePerKwh\":\"0.31\",\"effectiveFrom\":\"{later}\"");
        var mixed = await f.SendAsync("set_price", $"\"pricePerKwh\":\"0.32\",\"currency\":\"EUR\",\"effectiveFrom\":\"{DateTimeOffset.UtcNow.AddMinutes(20):O}\"");

        Assert.True(implicitCurrency.Ok);
        Assert.Equal("USD", f.Query.GetPriceSchedule().Entries.Last().Currency);
        Assert.False(mixed.Ok);
    }

    [Fact]
    public async Task PreviewPrice_ReportsWhatAPastPriceWouldAffect_AndChangesNothing()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        f.Seed(daysAgo: 3, seconds: 600);

        var past = Data(await f.SendAsync("preview_price", $"\"effectiveFrom\":\"{DateTimeOffset.UtcNow.AddDays(-4):O}\""));
        var future = Data(await f.SendAsync("preview_price", $"\"effectiveFrom\":\"{DateTimeOffset.UtcNow.AddDays(1):O}\""));

        Assert.True(past.GetProperty("affectsRecordedEnergy").GetBoolean());
        Assert.Equal(1, past.GetProperty("affectedSessions").GetInt32());
        Assert.True(past.GetProperty("affectedMonitoredSeconds").GetDouble() >= 590);
        Assert.False(future.GetProperty("affectsRecordedEnergy").GetBoolean());
        Assert.Empty(f.Query.GetPriceSchedule().Entries);
    }

    // ---------------------------------------------------------------- diagnostics

    [Fact]
    public async Task Diagnostics_AreSmall_AndCarryNoMachineNameUserNameOrInventory()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db, Info);

        var response = await f.SendAsync("get_diagnostics");
        var data = Data(response);
        // The database location is shown on purpose (it is where the user's data lives; the UI redacts the profile folder when copying).
        var withoutPath = JsonSerializer.Serialize(response.Data, ServeProtocol.JsonOptions).Replace(JsonSerializer.Serialize(db.FilePath).Trim('"'), "<db>");
        var raw = withoutPath;

        Assert.True(response.Ok);
        Assert.Equal("9.9.9", data.GetProperty("appVersion").GetString());
        Assert.Equal(4, data.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("idle", data.GetProperty("monitoringState").GetString());
        Assert.Equal(2, data.GetProperty("providers").GetArrayLength());
        Assert.Equal("Test GPU", data.GetProperty("sensors")[0].GetProperty("hardware").GetString());
        Assert.DoesNotContain(Environment.MachineName, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, raw.Replace(db.Directory, ""), StringComparison.OrdinalIgnoreCase);
        Assert.True(raw.Length < 4000, $"diagnostics should be small, was {raw.Length} chars");
    }

    [Fact]
    public async Task Diagnostics_WorkWithoutProcessInfo_AndFlagTheLiveStateAfterAStart()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        await f.SendAsync("start_monitoring");

        var data = Data(await f.SendAsync("get_diagnostics"));

        Assert.Equal("recording", data.GetProperty("monitoringState").GetString());
    }

    // ---------------------------------------------------------------- data management

    [Fact]
    public async Task CheckIntegrity_And_Maintenance_RunOnAHealthyDatabase()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        f.Seed(daysAgo: 1, seconds: 300);

        Assert.Equal("ok", Data(await f.SendAsync("check_integrity")).GetProperty("result").GetString());
        var maintenance = await f.SendAsync("run_maintenance");
        Assert.True(maintenance.Ok);
        Assert.Equal(0, Data(maintenance).GetProperty("freePagesAfter").GetInt64());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"olderThanDays\":7")]
    [InlineData("\"olderThanDays\":29")]
    [InlineData("\"olderThanDays\":99999")]
    public async Task HistoryDeletion_RequiresAnAgeOfAtLeast30Days(string body)
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        Assert.False((await f.SendAsync("preview_history_deletion", body)).Ok);
        Assert.False((await f.SendAsync("delete_history", body.Length > 0 ? body + ",\"confirmSessions\":0" : "\"confirmSessions\":0")).Ok);
    }

    [Fact]
    public async Task HistoryDeletion_WithoutOrWithAMismatchedConfirmation_DeletesNothing()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        var old = f.Seed(daysAgo: 100);
        var preview = Data(await f.SendAsync("preview_history_deletion", "\"olderThanDays\":30"));
        Assert.Equal(1, preview.GetProperty("sessions").GetInt32());

        var none = await f.SendAsync("delete_history", "\"olderThanDays\":30");
        var wrong = await f.SendAsync("delete_history", "\"olderThanDays\":30,\"confirmSessions\":5");

        Assert.False(none.Ok);
        Assert.False(wrong.Ok);
        Assert.NotNull(f.Query.GetSession(old));
    }

    [Fact]
    public async Task HistoryDeletion_WithTheExactConfirmation_RemovesOnlyOldFinishedHistory_AndTheRunningSessionSurvives()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        var old = f.Seed(daysAgo: 100);
        var recent = f.Seed(daysAgo: 5);
        await f.SendAsync("start_monitoring");
        var running = f.Harness.Controller.Snapshot().SessionId!.Value;

        var response = await f.SendAsync("delete_history", "\"olderThanDays\":30,\"confirmSessions\":1");

        Assert.True(response.Ok);
        Assert.Null(f.Query.GetSession(old));
        Assert.NotNull(f.Query.GetSession(recent));
        Assert.NotNull(f.Query.GetSession(running));
        Assert.Equal("recording", f.Harness.Controller.Snapshot().State);
    }

    [Fact]
    public async Task GamingSessions_CanBeListedForALocalDay()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var none = await f.SendAsync("get_gaming_sessions", $"\"day\":\"{DateOnly.FromDateTime(DateTime.Now):yyyy-MM-dd}\"");
        var bad = await f.SendAsync("get_gaming_sessions", "\"day\":\"not-a-day\"");

        Assert.True(none.Ok);
        Assert.Equal(0, Data(none).GetArrayLength());
        Assert.False(bad.Ok);
    }

    // ---------------------------------------------------------------- partial energy in ticks

    [Fact]
    public async Task Ticks_CarryPartialEnergySeparately_NeverMergedIntoCompleteEnergy()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        await f.SendAsync("start_monitoring");
        for (var t = 0; t < 5; t++)
            f.Harness.Source.Push(At(t, t, cpu: null, gpu: 60)); // CPU missing: every interval is Partial
        await f.Harness.WaitForTicksAsync(5);

        var last = f.Harness.Of<TickMessage>().Last();

        Assert.Equal(0, last.SessionKwh);
        Assert.True(last.SessionPartialKwh > 0);
        Assert.Equal(EstimateCompleteness.Partial, last.SessionCompleteness);
        Assert.Null(last.SessionCostApprox);
    }
}
