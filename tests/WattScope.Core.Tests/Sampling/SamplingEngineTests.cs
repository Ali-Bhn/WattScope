using WattScope.Core.Hardware;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;
using WattScope.Core.Tests.TestSupport;

namespace WattScope.Core.Tests.Sampling;

public class SamplingEngineTests
{
    private static SensorReading Reading(string providerId, double value) => new()
    {
        ProviderId = providerId,
        HardwareId = "hw0",
        HardwareName = "Test Hardware",
        SensorId = "s0",
        SensorName = "Test Sensor",
        SensorType = "Power",
        Unit = "W",
        Value = value,
        Availability = SensorAvailability.Available,
        Timestamp = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ProducesExpectedNumberOfTicks_ForGivenDuration()
    {
        var provider = new FakeSensorProvider("Fake", readingsFactory: () => [Reading("Fake", 10.0)]);
        var engine = new SamplingEngine([provider]);

        var options = new SamplingOptions { Interval = TimeSpan.FromMilliseconds(50), Duration = TimeSpan.FromMilliseconds(220) };
        var ticks = new List<SamplingTick>();
        await foreach (var tick in engine.RunAsync(options))
            ticks.Add(tick);

        // ~220ms / 50ms interval: tick 0 fires immediately, so 5 ticks (0..4) is the expected count.
        Assert.InRange(ticks.Count, 4, 6);
        Assert.Equal(Enumerable.Range(0, ticks.Count), ticks.Select(t => t.Sequence));
    }

    [Fact]
    public async Task ProviderException_ProducesDiagnosticError_AndDoesNotStopTheEngine()
    {
        var callCount = 0;
        var provider = new FakeSensorProvider("Flaky", readingsFactory: () =>
        {
            callCount++;
            if (callCount == 2)
                throw new InvalidOperationException("simulated failure");
            return [Reading("Flaky", 5.0)];
        });
        var engine = new SamplingEngine([provider]);

        var options = new SamplingOptions { Interval = TimeSpan.FromMilliseconds(20), Duration = TimeSpan.FromMilliseconds(90) };
        var ticks = new List<SamplingTick>();
        await foreach (var tick in engine.RunAsync(options))
            ticks.Add(tick);

        Assert.True(ticks.Count >= 3);
        Assert.Empty(ticks[0].Errors);
        Assert.Single(ticks[1].Errors);
        Assert.Contains("simulated failure", ticks[1].Errors[0].Message);
        // The engine kept going after the failing tick.
        Assert.Empty(ticks[2].Errors);
    }

    [Fact]
    public async Task Cancellation_StopsTheLoopPromptly()
    {
        var provider = new FakeSensorProvider("Fake", readingsFactory: () => [Reading("Fake", 1.0)]);
        var engine = new SamplingEngine([provider]);
        using var cts = new CancellationTokenSource();

        var options = new SamplingOptions { Interval = TimeSpan.FromMilliseconds(20) }; // no Duration: runs until cancelled
        var ticks = new List<SamplingTick>();

        await foreach (var tick in engine.RunAsync(options, cts.Token))
        {
            ticks.Add(tick);
            if (ticks.Count == 3)
                cts.Cancel();
        }

        Assert.Equal(3, ticks.Count);
    }

    [Fact]
    public async Task GapDetection_FlagsATickThatStartedMuchLaterThanScheduled()
    {
        var callCount = 0;
        var provider = new FakeSensorProvider("Slow", readingsFactory: () =>
        {
            callCount++;
            if (callCount == 1)
                Thread.Sleep(150); // Blocks this tick's own work well past the interval.
            return [Reading("Slow", 1.0)];
        });
        var engine = new SamplingEngine([provider]);

        var options = new SamplingOptions { Interval = TimeSpan.FromMilliseconds(30), Duration = TimeSpan.FromMilliseconds(250), GapDetectionFactor = 2.0 };
        var ticks = new List<SamplingTick>();
        await foreach (var tick in engine.RunAsync(options))
            ticks.Add(tick);

        Assert.False(ticks[0].GapDetected);
        Assert.Contains(ticks.Skip(1), t => t.GapDetected);
    }

    [Fact]
    public async Task AfterAStall_DoesNotBurstCatchUpTicks()
    {
        // A 200 ms stall with a 20 ms interval used to make the engine fire ~10
        // back-to-back ticks to "catch up" (the same happens after sleep/resume).
        var callCount = 0;
        var provider = new FakeSensorProvider("Stall", readingsFactory: () =>
        {
            if (++callCount == 2)
                Thread.Sleep(200);
            return [Reading("Stall", 1.0)];
        });
        var engine = new SamplingEngine([provider]);

        var options = new SamplingOptions { Interval = TimeSpan.FromMilliseconds(20), Duration = TimeSpan.FromMilliseconds(400) };
        var ticks = new List<SamplingTick>();
        await foreach (var tick in engine.RunAsync(options))
            ticks.Add(tick);

        var spacings = ticks.Zip(ticks.Skip(1), (a, b) => (b.MonotonicTime - a.MonotonicTime).TotalMilliseconds).ToList();
        Assert.All(spacings, s => Assert.True(s >= 10, $"Tick spacing {s:F1} ms indicates a catch-up burst."));
        Assert.Contains(ticks, t => t.GapDetected);
    }

    [Fact]
    public async Task Ticks_CarryMonotonicTime_AndReadDuration()
    {
        var provider = new FakeSensorProvider("Fake", readingsFactory: () => { Thread.Sleep(5); return [Reading("Fake", 1.0)]; });
        var engine = new SamplingEngine([provider]);

        var ticks = new List<SamplingTick>();
        await foreach (var tick in engine.RunAsync(new SamplingOptions { Interval = TimeSpan.FromMilliseconds(30), Duration = TimeSpan.FromMilliseconds(100) }))
            ticks.Add(tick);

        Assert.True(ticks.Count >= 2);
        Assert.All(ticks.Zip(ticks.Skip(1)), p => Assert.True(p.Second.MonotonicTime > p.First.MonotonicTime));
        Assert.All(ticks, t => Assert.True(t.ReadDuration >= TimeSpan.FromMilliseconds(4)));
    }

    [Fact]
    public async Task NeverCallsProviderConcurrently_TicksAreStrictlySequential()
    {
        var inFlight = 0;
        var maxConcurrent = 0;
        var provider = new FakeSensorProvider("Fake", readingsFactory: () =>
        {
            Interlocked.Increment(ref inFlight);
            maxConcurrent = Math.Max(maxConcurrent, inFlight);
            Thread.Sleep(15);
            Interlocked.Decrement(ref inFlight);
            return [Reading("Fake", 1.0)];
        });
        var engine = new SamplingEngine([provider]);

        var options = new SamplingOptions { Interval = TimeSpan.FromMilliseconds(5), Duration = TimeSpan.FromMilliseconds(120) };
        await foreach (var _ in engine.RunAsync(options)) { }

        Assert.Equal(1, maxConcurrent);
    }
}
