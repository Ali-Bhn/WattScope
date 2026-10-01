using WattScope.Core.Energy;

namespace WattScope.Core.Tests.Energy;

public class PowerIntervalSplitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static PowerInterval Ramp(double seconds, double startWatts, double endWatts) => new()
    {
        StartUtc = T0,
        EndUtc = T0.AddSeconds(seconds),
        Seconds = seconds,
        CpuPackage = new LinearPower(startWatts, endWatts),
        GpuBoard = null
    };

    [Fact]
    public void SplitAt_ArbitraryInstants_PiecesSumExactlyToTheWholeInterval()
    {
        var interval = Ramp(20, 100, 300); // linear ramp 100W -> 300W over 20s
        var wholeJoules = interval.CpuPackage!.Value.Joules(interval.Seconds);

        var pieces = interval.SplitAt([T0.AddSeconds(5), T0.AddSeconds(15)]).ToList();

        Assert.Equal(3, pieces.Count);
        Assert.Equal(T0, pieces[0].StartUtc);
        Assert.Equal(T0.AddSeconds(5), pieces[0].EndUtc);
        Assert.Equal(T0.AddSeconds(15), pieces[1].EndUtc);
        Assert.Equal(T0.AddSeconds(20), pieces[2].EndUtc);

        var summedJoules = pieces.Sum(p => p.CpuPackage!.Value.Joules(p.Seconds));
        Assert.Equal(wholeJoules, summedJoules, 9);
    }

    [Fact]
    public void SplitAt_InterpolatesPowerLinearly_AtEachSplitPoint()
    {
        var interval = Ramp(20, 100, 300); // 10 W/s ramp

        var pieces = interval.SplitAt([T0.AddSeconds(5)]).ToList();

        Assert.Equal(2, pieces.Count);
        Assert.Equal(150, pieces[0].CpuPackage!.Value.EndWatts, 9);  // at t=5: 100 + 10*5
        Assert.Equal(150, pieces[1].CpuPackage!.Value.StartWatts, 9);
        Assert.Equal(300, pieces[1].CpuPackage!.Value.EndWatts, 9);
    }

    [Fact]
    public void SplitAt_InstantsOutsideTheOpenInterval_AreIgnored()
    {
        var interval = Ramp(20, 100, 300);

        var pieces = interval.SplitAt([T0, T0.AddSeconds(20), T0.AddSeconds(-5), T0.AddSeconds(25)]).ToList();

        var only = Assert.Single(pieces); // start/end themselves and out-of-range points never split anything
        Assert.Equal(interval, only);
    }

    [Fact]
    public void SplitAt_DuplicateAndUnsortedInstants_ProduceTheSamePiecesAsSortedUnique()
    {
        var interval = Ramp(20, 100, 300);

        var messy = interval.SplitAt([T0.AddSeconds(15), T0.AddSeconds(5), T0.AddSeconds(15), T0.AddSeconds(5)]).ToList();
        var clean = interval.SplitAt([T0.AddSeconds(5), T0.AddSeconds(15)]).ToList();

        Assert.Equal(clean, messy);
    }

    [Fact]
    public void SplitAt_NoInstants_ReturnsTheIntervalUnchanged()
    {
        var interval = Ramp(20, 100, 300);

        var pieces = interval.SplitAt([]).ToList();

        var only = Assert.Single(pieces);
        Assert.Equal(interval, only);
    }

    [Fact]
    public void SplitAtBoundaries_StillAgreesWithSplitAt_ForFifteenMinuteBoundaries()
    {
        // SplitAtBoundaries is now a thin wrapper over SplitAt; this locks the equivalence in.
        var start = EnergyBuckets.BucketStart(T0, EnergyBuckets.DefaultBucketSize) + TimeSpan.FromMinutes(10);
        var interval = new PowerInterval
        {
            StartUtc = start,
            EndUtc = start.AddMinutes(10),
            Seconds = 600,
            CpuPackage = new LinearPower(100, 200),
            GpuBoard = null
        };

        var boundary = EnergyBuckets.BucketStart(start, EnergyBuckets.DefaultBucketSize) + EnergyBuckets.DefaultBucketSize;
        var viaBoundaries = interval.SplitAtBoundaries(EnergyBuckets.DefaultBucketSize).ToList();
        var viaSplitAt = interval.SplitAt([boundary]).ToList();

        Assert.Equal(viaSplitAt, viaBoundaries);
    }
}
