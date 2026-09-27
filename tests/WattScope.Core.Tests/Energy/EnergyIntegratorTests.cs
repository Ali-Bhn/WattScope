using WattScope.Core.Energy;
using WattScope.Core.Tests.TestSupport;
using static WattScope.Core.Tests.TestSupport.Synthetic;

namespace WattScope.Core.Tests.Energy;

public class EnergyIntegratorTests
{
    private const double Tolerance = 1e-9;

    private static void AssertClose(double expected, double actual, double relative = Tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= Math.Abs(expected) * relative + 1e-12,
            $"Expected {expected:R}, got {actual:R}");

    [Fact]
    public void ConstantPower_OneHour_IntegratesExactly()
    {
        var result = Integrate(Constant(0, 3600, 1, cpuWatts: 100, gpuWatts: 200));

        AssertClose(3600, result.Totals.MonitoredSeconds);
        AssertClose(100 * 3600, result.Totals.CpuPackageJoules);  // 100 Wh
        AssertClose(200 * 3600, result.Totals.GpuBoardJoules);    // 200 Wh
        AssertClose(3600, result.Totals.CpuPackageSeconds);
        Assert.Empty(result.Gaps);
    }

    [Fact]
    public void LinearlyVaryingPower_WithIrregularSpacing_TrapezoidIsExact()
    {
        // P(t) = 50 + 0.1 t; analytic energy over [0, 600] = 50*600 + 0.05*600^2 = 48000 J.
        var samples = new List<PowerSample>();
        var t = 0.0;
        var i = 0;
        while (t < 600)
        {
            samples.Add(Sample(t, 50 + 0.1 * t, 10));
            t += (i++ % 3) switch { 0 => 0.5, 1 => 1.5, _ => 1.0 };
        }
        samples.Add(Sample(600, 50 + 0.1 * 600, 10));

        var result = Integrate(samples);

        AssertClose(48000, result.Totals.CpuPackageJoules);
        AssertClose(600, result.Totals.MonitoredSeconds);
    }

    [Fact]
    public void SinusoidalPower_ConvergesToAnalyticIntegral()
    {
        // P(t) = 100 + 50 sin(2*pi*t/60) over 10 full periods: analytic = 100 * 600 = 60000 J.
        var samples = Enumerable.Range(0, 601).Select(s => Sample(s, 100 + 50 * Math.Sin(2 * Math.PI * s / 60.0), 1));

        var result = Integrate(samples);

        AssertClose(60000, result.Totals.CpuPackageJoules, relative: 1e-3);
    }

    [Fact]
    public void UsesActualElapsedTime_NotAnAssumedSamplingInterval()
    {
        // Samples 0 s, 1 s, 3 s (one tick late, still within the verified-gap limit):
        // 100 W for 3 s is 300 J; assuming a fixed 1 s interval would give 200 J.
        var result = Integrate([Sample(0, 100, 100), Sample(1, 100, 100), Sample(3, 100, 100)]);

        AssertClose(300, result.Totals.CpuPackageJoules);
        AssertClose(3, result.Totals.MonitoredSeconds);
    }

    [Fact]
    public void MonotonicElapsedTime_IsAuthoritativeForDuration()
    {
        // UTC says 1.0 s elapsed, the monotonic clock says 1.2 s (within tolerance): duration is 1.2 s.
        var result = Integrate([Sample(0, 100, 100, utcSeconds: 0), Sample(1.2, 100, 100, utcSeconds: 1.0)]);

        AssertClose(120, result.Totals.CpuPackageJoules);
    }

    [Fact]
    public void MissingReading_IsNeitherInterpolatedNorZeroFilled()
    {
        // CPU missing at t=2: intervals [1,2] and [2,3] have no CPU energy at all.
        var samples = new[]
        {
            Sample(0, 100, 50), Sample(1, 100, 50), Sample(2, null, 50), Sample(3, 100, 50), Sample(4, 100, 50)
        };

        var result = Integrate(samples);

        AssertClose(4, result.Totals.MonitoredSeconds);
        AssertClose(200, result.Totals.CpuPackageJoules);    // only [0,1] and [3,4]
        AssertClose(2, result.Totals.CpuPackageSeconds);
        AssertClose(200, result.Totals.GpuBoardJoules);      // GPU unaffected
        AssertClose(4, result.Totals.GpuBoardSeconds);
        AssertClose(2, result.Totals.EstimatedCompleteSeconds);
        AssertClose(2, result.Totals.EstimatedPartialSeconds);
    }

    [Fact]
    public void DuplicateTimestamp_IsDiscarded_AndNeverIntegratedTwice()
    {
        var samples = new[] { Sample(0, 100, 100), Sample(1, 100, 100), Sample(1, 1000, 1000), Sample(2, 100, 100) };

        var result = Integrate(samples);

        Assert.Equal(1, result.Duplicates);
        AssertClose(200, result.Totals.CpuPackageJoules);
        AssertClose(2, result.Totals.MonitoredSeconds);
    }

    [Fact]
    public void LongSamplingGap_IsRecorded_AndNoEnergyIsAttributedToIt()
    {
        var samples = Constant(0, 10, 1, 100, 100).Concat(Constant(100, 110, 1, 100, 100));

        var result = Integrate(samples);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(GapReason.SamplingStall, gap.Reason);
        Assert.Equal(TimeSpan.FromSeconds(90), gap.Duration);
        AssertClose(20, result.Totals.MonitoredSeconds);
        AssertClose(2000, result.Totals.CpuPackageJoules);
    }

    [Fact]
    public void SleepWhereOnlyWallClockAdvanced_IsAClockDiscontinuityGap()
    {
        // Monotonic clock paused during sleep (+1 s) while UTC advanced one hour.
        var samples = new[]
        {
            Sample(0, 100, 100, utcSeconds: 0), Sample(1, 100, 100, utcSeconds: 1),
            Sample(2, 100, 100, utcSeconds: 3601), Sample(3, 100, 100, utcSeconds: 3602)
        };

        var result = Integrate(samples);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(GapReason.ClockDiscontinuity, gap.Reason);
        AssertClose(2, result.Totals.MonitoredSeconds);
        AssertClose(200, result.Totals.CpuPackageJoules);
    }

    [Fact]
    public void TimeGoingBackwards_IsAClockDiscontinuityGap()
    {
        var samples = new[] { Sample(0, 100, 100, utcSeconds: 10), Sample(1, 100, 100, utcSeconds: 5) };

        var result = Integrate(samples);

        Assert.Equal(GapReason.ClockDiscontinuity, Assert.Single(result.Gaps).Reason);
        Assert.Equal(0, result.Totals.MonitoredSeconds);
    }

    [Fact]
    public void BreakChain_PreventsIntegrationAcrossTheBreak()
    {
        var integrator = new EnergyIntegrator();
        integrator.Accept(Sample(0, 100, 100));
        integrator.Accept(Sample(1, 100, 100));
        integrator.BreakChain(GapReason.Suspend);

        var step = integrator.Accept(Sample(2, 100, 100));

        Assert.Equal(IntegrationOutcome.ChainStarted, step.Outcome);
        Assert.Null(step.Interval);
        Assert.Equal(GapReason.Suspend, step.Gap!.Reason);
        Assert.Equal(BaseUtc.AddSeconds(1), step.Gap.StartUtc);
    }

    [Fact]
    public void IntervalCrossingABucketBoundary_IsSplitExactly()
    {
        // 100 W -> 200 W over 1 s, bucket boundary 0.5 s in. Linear profile:
        // first half 100..150 W = 62.5 J, second half 150..200 W = 87.5 J.
        var start = BaseUtc - TimeSpan.FromSeconds(0.5);
        var samples = new[]
        {
            new PowerSample { UtcTime = start, MonotonicTime = TimeSpan.Zero, CpuPackage = ChannelReading.FromWatts(100), GpuBoard = ChannelReading.FromWatts(0) },
            new PowerSample { UtcTime = start.AddSeconds(1), MonotonicTime = TimeSpan.FromSeconds(1), CpuPackage = ChannelReading.FromWatts(200), GpuBoard = ChannelReading.FromWatts(0) }
        };

        var result = Integrate(samples);

        Assert.Equal(2, result.Buckets.Count);
        Assert.Equal(BaseUtc - EnergyBuckets.DefaultBucketSize, result.Buckets[0].BucketStartUtc);
        Assert.Equal(BaseUtc, result.Buckets[1].BucketStartUtc);
        AssertClose(62.5, result.Buckets[0].Totals.CpuPackageJoules);
        AssertClose(87.5, result.Buckets[1].Totals.CpuPackageJoules);
        AssertClose(0.5, result.Buckets[0].Totals.MonitoredSeconds);
        AssertClose(150, result.Totals.CpuPackageJoules);
    }

    [Fact]
    public void BucketTotals_SumToRunTotals_OverAnHour()
    {
        var result = Integrate(Constant(0, 3600, 1, 45.4, 43.01));

        Assert.Equal(4, result.Buckets.Count);
        var summed = result.Buckets.Aggregate(new EnergyTotals(), (acc, b) => acc + b.Totals);
        AssertClose(result.Totals.CpuPackageJoules, summed.CpuPackageJoules);
        AssertClose(result.Totals.EstimatedCompleteJoules, summed.EstimatedCompleteJoules);
        Assert.All(result.Buckets, b => AssertClose(900, b.Totals.MonitoredSeconds));
    }

    [Fact]
    public void IntegrationOptions_ScaleWithSamplingInterval()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), IntegrationOptions.ForSamplingInterval(TimeSpan.FromSeconds(1)).MaxSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), IntegrationOptions.ForSamplingInterval(TimeSpan.FromSeconds(5)).MaxSampleInterval);
    }
}
