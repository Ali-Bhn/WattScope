using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WattScope.Core.Monitoring;
using WattScope.Probe.Serve;
using static WattScope.Probe.Tests.Samples;

namespace WattScope.Probe.Tests;

/// <summary>The --serve wire protocol: validation, error reporting and dispatch, never a crash.</summary>
public class ServeRequestProcessorTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(TempDb db)
        {
            Harness = new ControllerHarness(db);
            Query = db.Open();
            Processor = new ServeRequestProcessor(Query, Harness.Controller, () => ShutdownRequests++, NullLogger.Instance);
        }

        public ControllerHarness Harness { get; }
        public WattScope.Storage.SqliteEnergyStore Query { get; }
        public ServeRequestProcessor Processor { get; }
        public int ShutdownRequests { get; private set; }

        public Task<ServeResponse?> SendAsync(string line) => Processor.ProcessLineAsync(line);

        public async ValueTask DisposeAsync()
        {
            await Harness.DisposeAsync();
            Query.Dispose();
        }
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"id\":\"abc\",\"cmd\":\"get_prices\"}")]
    [InlineData("{\"cmd\":\"get_prices\"}")]            // id is required
    [InlineData("{\"id\":1}")]                          // cmd is required
    public async Task MalformedRequests_BecomeStructuredErrors_NotCrashes(string line)
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync(line);

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.NotNull(response.Error);
        Assert.Null(response.Id);
    }

    [Fact]
    public async Task OversizedRequest_IsRejectedBeforeParsing()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("{\"id\":1,\"cmd\":\"" + new string('x', ServeRequestProcessor.MaxLineLength) + "\"}");

        Assert.False(response!.Ok);
        Assert.Contains("too large", response.Error);
    }

    [Fact]
    public async Task BlankLines_AreIgnored()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        Assert.Null(await f.SendAsync(""));
        Assert.Null(await f.SendAsync("   "));
        Assert.Null(await f.SendAsync("﻿"));
    }

    [Fact]
    public async Task LeadingByteOrderMark_IsTolerated()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("﻿{\"id\":7,\"cmd\":\"get_prices\"}");

        Assert.True(response!.Ok);
        Assert.Equal(7, response.Id);
    }

    [Fact]
    public async Task UnknownCommand_IsRejected_AndNothingIsExecuted()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("{\"id\":3,\"cmd\":\"cmd.exe /c calc\"}");

        Assert.False(response!.Ok);
        Assert.Equal(3, response.Id);
        Assert.Contains("Unknown command", response.Error);
    }

    [Theory]
    [InlineData("{\"id\":1,\"cmd\":\"set_price\"}", "pricePerKwh is required")]
    [InlineData("{\"id\":1,\"cmd\":\"set_price\",\"pricePerKwh\":-0.1}", "Negative")]
    [InlineData("{\"id\":1,\"cmd\":\"set_price\",\"pricePerKwh\":0.3,\"currency\":\"euro\"}", "ISO 4217")]
    [InlineData("{\"id\":1,\"cmd\":\"set_retention\"}", "retentionDays is required")]
    [InlineData("{\"id\":1,\"cmd\":\"set_retention\",\"retentionDays\":99999}", "between 0 and 3650")]
    [InlineData("{\"id\":1,\"cmd\":\"set_estimation\",\"psuEfficiency\":5}", "")]
    [InlineData("{\"id\":1,\"cmd\":\"set_monitoring_interval\"}", "intervalMs is required")]
    [InlineData("{\"id\":1,\"cmd\":\"set_monitoring_interval\",\"intervalMs\":10}", "between")]
    [InlineData("{\"id\":1,\"cmd\":\"set_monitoring_interval\",\"intervalMs\":99999999}", "between")]
    [InlineData("{\"id\":1,\"cmd\":\"get_summary\",\"month\":\"2026-13\"}", "yyyy-MM")]
    [InlineData("{\"id\":1,\"cmd\":\"get_summary\",\"sessionId\":424242}", "does not exist")]
    public async Task InvalidParameters_AreRejectedWithAReason(string line, string expectedFragment)
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync(line);

        Assert.False(response!.Ok);
        Assert.Contains(expectedFragment, response.Error);
    }

    [Fact]
    public async Task StartStop_ThroughTheProtocol_IsIdempotent()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var a = await f.SendAsync("{\"id\":1,\"cmd\":\"start_monitoring\"}");
        var b = await f.SendAsync("{\"id\":2,\"cmd\":\"start_monitoring\"}");
        var state = await f.SendAsync("{\"id\":3,\"cmd\":\"get_monitoring_state\"}");
        var stop = await f.SendAsync("{\"id\":4,\"cmd\":\"stop_monitoring\"}");
        var stopAgain = await f.SendAsync("{\"id\":5,\"cmd\":\"stop_monitoring\"}");

        Assert.All(new[] { a, b, state, stop, stopAgain }, r => Assert.True(r!.Ok));
        Assert.False(((MonitoringSnapshot)a!.Data!).AlreadyRunning);
        Assert.True(((MonitoringSnapshot)b!.Data!).AlreadyRunning);
        Assert.Equal("recording", ((MonitoringSnapshot)state!.Data!).State);
        Assert.Equal("idle", ((MonitoringSnapshot)stop!.Data!).State);
        Assert.Single(f.Query.ListSessions(10));
    }

    [Fact]
    public async Task ChangingTheInterval_WhileRunning_ContinuesTheSameSessionAtTheNewInterval()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        var started = (MonitoringSnapshot)(await f.SendAsync("{\"id\":1,\"cmd\":\"start_monitoring\"}"))!.Data!;

        var response = await f.SendAsync("{\"id\":2,\"cmd\":\"set_monitoring_interval\",\"intervalMs\":2000}");

        Assert.True(response!.Ok);
        var after = f.Harness.Controller.Snapshot();
        Assert.Equal("recording", after.State);
        Assert.Equal(2000, after.IntervalMs);
        Assert.Equal(started.SessionId, after.SessionId);          // same session, not a new one
        Assert.Single(f.Query.ListSessions(10));
        Assert.Equal(2000, f.Query.GetMonitoringIntervalMs());     // and it persists for the next launch
    }

    [Fact]
    public async Task IntervalChange_WhileIdle_IsStoredWithoutStartingAnything()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("{\"id\":1,\"cmd\":\"set_monitoring_interval\",\"intervalMs\":5000}");

        Assert.True(response!.Ok);
        Assert.Equal("idle", f.Harness.Controller.Snapshot().State);
        Assert.Equal(5000, f.Query.GetMonitoringIntervalMs());
        Assert.Empty(f.Query.ListSessions(10));
    }

    [Fact]
    public async Task Shutdown_RequestsAProcessExit_ExactlyThroughTheCallback()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("{\"id\":9,\"cmd\":\"shutdown\"}");

        Assert.True(response!.Ok);
        Assert.Equal(1, f.ShutdownRequests);
    }

    [Fact]
    public async Task Responses_SerializeToTheDocumentedShape()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);

        var response = await f.SendAsync("{\"id\":5,\"cmd\":\"get_monitoring_state\"}");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, ServeProtocol.JsonOptions));

        Assert.Equal("response", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("id").GetInt64());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("idle", json.RootElement.GetProperty("data").GetProperty("state").GetString());
    }

    [Fact]
    public async Task EveryEmittedTick_CarriesTheSessionAndNoSubstitutedZero()
    {
        using var db = new TempDb();
        await using var f = new Fixture(db);
        await f.SendAsync("{\"id\":1,\"cmd\":\"start_monitoring\"}");
        for (var t = 0; t < 3; t++)
            f.Harness.Source.Push(At(t, t, cpu: null, gpu: 60));
        await f.Harness.WaitForTicksAsync(3);

        foreach (var tick in f.Harness.Of<TickMessage>())
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(tick, ServeProtocol.JsonOptions));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("cpuWatts").ValueKind);
            Assert.Equal("implausibleZero", json.RootElement.GetProperty("cpuState").GetString());
        }
    }
}
