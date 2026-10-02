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

    /// <summary>
    /// Runs the engine on a <see cref="VirtualClock"/>: the read of the tick numbered <paramref name="slowTick"/>
    /// takes <paramref name="readTime"/>, every other read takes no time. Returns each tick with its start time in
    /// milliseconds since the first tick -- exact values, independent of the real scheduler.
    /// </summary>
    private static async Task<List<(double StartMs, SamplingTick Tick)>> RunOnVirtualClock(TimeSpan interval, TimeSpan duration, int slowTick, TimeSpan readTime)
    {
        var clock = new VirtualClock();
        var call = 0;
        var provider = new FakeSensorProvider("Stall", readingsFactory: () =>
        {
            if (call++ == slowTick)
                clock.Advance(readTime);
            return [Reading("Stall", 1.0)];
        });
        var engine = new SamplingEngine([provider], timeProvider: clock);

        var ticks = new List<SamplingTick>();
        await foreach (var tick in engine.RunAsync(new SamplingOptions { Interval = interval, Duration = duration }))
            ticks.Add(tick);
        return ticks.Select(t => ((t.MonotonicTime - ticks[0].MonotonicTime).TotalMilliseconds, t)).ToList();
    }

    [Fact]
    public async Task AfterAStall_DoesNotBurstCatchUpTicks()
    {
        // A 200 ms stall with a 20 ms interval used to make the engine fire ~10
        // back-to-back ticks to "catch up" (the same happens after sleep/resume).
        var ticks = await RunOnVirtualClock(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(400), slowTick: 1, readTime: TimeSpan.FromMilliseconds(200));

        // Tick 1 starts on time at 20 ms and blocks until 220 ms; the engine re-anchors there.
        Assert.Equal([0, 20, 220, 240, 260, 280, 300, 320, 340, 360, 380], ticks.Select(t => t.StartMs));
        var spacings = ticks.Zip(ticks.Skip(1), (a, b) => b.StartMs - a.StartMs).ToList();
        Assert.All(spacings, s => Assert.True(s >= 20, $"Tick spacing {s:F1} ms indicates a catch-up burst."));

        var gap = Assert.Single(ticks, t => t.Tick.GapDetected);
        Assert.Equal(2, gap.Tick.Sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(180), gap.Tick.GapDuration);
    }

    [Theory]
    [InlineData(41)]      // just over one missed interval
    [InlineData(1_000)]   // 50 missed intervals
    [InlineData(60_000)]  // sleep/resume scale: 3000 missed intervals
    public async Task AfterAStallOfAnyLength_TheNextTickIsAFullIntervalLater_AndExactlyOneGapIsReported(int stallMs)
    {
        var interval = TimeSpan.FromMilliseconds(20);
        var ticks = await RunOnVirtualClock(interval, TimeSpan.FromMilliseconds(20 + stallMs + 100), slowTick: 1, readTime: TimeSpan.FromMilliseconds(stallMs));

        Assert.Equal(20 + stallMs, ticks[2].StartMs);
        Assert.All(ticks.Skip(2).Zip(ticks.Skip(3), (a, b) => b.StartMs - a.StartMs), s => Assert.Equal(20, s));
        Assert.Equal(1 + 1 + 5, ticks.Count);   // the two before the stall and five in the 100 ms after it -- not one per missed slot

        var gap = Assert.Single(ticks, t => t.Tick.GapDetected);
        Assert.Equal(TimeSpan.FromMilliseconds(stallMs) - interval, gap.Tick.GapDuration);
    }

    [Fact]
    public async Task ATickLateByLessThanOneInterval_KeepsTheFixedSchedule_OneShortSpacingAndNoGap()
    {
        // Not a burst, and why a wall-clock "every spacing >= half an interval" assertion is wrong for this engine:
        // a read that overruns by half an interval makes the next tick 10 ms late; the schedule is kept (no drift),
        // so exactly one following tick comes 10 ms after it, and the cadence is back to 20 ms from there.
        var ticks = await RunOnVirtualClock(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(120), slowTick: 1, readTime: TimeSpan.FromMilliseconds(30));

        Assert.Equal([0, 20, 50, 60, 80, 100], ticks.Select(t => t.StartMs));
        Assert.DoesNotContain(ticks, t => t.Tick.GapDetected);
    }

    [Fact]
    public async Task WithoutAStall_TicksStayOnTheFixedSchedule_WithoutDrift()
    {
        // Every read takes 7 ms; a schedule anchored on "previous tick end" would drift by 7 ms per tick.
        var clock = new VirtualClock();
        var provider = new FakeSensorProvider("Steady", readingsFactory: () => { clock.Advance(TimeSpan.FromMilliseconds(7)); return [Reading("Steady", 1.0)]; });
        var engine = new SamplingEngine([provider], timeProvider: clock);

        var ticks = new List<SamplingTick>();
        await foreach (var tick in engine.RunAsync(new SamplingOptions { Interval = TimeSpan.FromMilliseconds(20), Duration = TimeSpan.FromMilliseconds(100) }))
            ticks.Add(tick);

        Assert.Equal([0, 20, 40, 60, 80], ticks.Select(t => (t.MonotonicTime - ticks[0].MonotonicTime).TotalMilliseconds));
        Assert.All(ticks, t => Assert.Equal(TimeSpan.FromMilliseconds(7), t.ReadDuration));
        Assert.DoesNotContain(ticks, t => t.GapDetected);
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
