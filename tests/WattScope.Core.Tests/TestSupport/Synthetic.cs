using WattScope.Core.Energy;
using WattScope.Core.Estimation;

namespace WattScope.Core.Tests.TestSupport;

/// <summary>Deterministic synthetic power data for energy-calculation tests. No hardware, no wall clock.</summary>
public static class Synthetic
{
    /// <summary>Aligned to a 15-minute bucket boundary.</summary>
    public static readonly DateTimeOffset BaseUtc = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A sample at <paramref name="seconds"/> after <see cref="BaseUtc"/>, with UTC and monotonic time advancing identically.</summary>
    public static PowerSample Sample(double seconds, double? cpuWatts, double? gpuWatts, double? utcSeconds = null) => new()
    {
        UtcTime = BaseUtc + TimeSpan.FromSeconds(utcSeconds ?? seconds),
        MonotonicTime = TimeSpan.FromSeconds(seconds),
        CpuPackage = cpuWatts is { } c ? ChannelReading.FromWatts(c) : ChannelReading.Rejected(ReadingState.Unavailable),
        GpuBoard = gpuWatts is { } g ? ChannelReading.FromWatts(g) : ChannelReading.Rejected(ReadingState.Unavailable)
    };

    public static IEnumerable<PowerSample> Constant(double fromSeconds, double toSeconds, double stepSeconds, double? cpuWatts, double? gpuWatts)
    {
        var steps = (int)Math.Round((toSeconds - fromSeconds) / stepSeconds);
        for (var i = 0; i <= steps; i++)
            yield return Sample(fromSeconds + i * stepSeconds, cpuWatts, gpuWatts);
    }

    public sealed record Result(EnergyTotals Totals, IReadOnlyList<MonitoringGap> Gaps, IReadOnlyList<EnergyBucket> Buckets, int Duplicates);

    /// <summary>Runs samples through the real integrator and bucket accumulator.</summary>
    public static Result Integrate(IEnumerable<PowerSample> samples, EstimationSettings? settings = null, IntegrationOptions? options = null)
    {
        settings ??= new EstimationSettings();
        var integrator = new EnergyIntegrator(options);
        var accumulator = new EnergyAccumulator();
        var gaps = new List<MonitoringGap>();
        var duplicates = 0;

        foreach (var sample in samples)
        {
            var step = integrator.Accept(sample);
            if (step.Outcome == IntegrationOutcome.DuplicateDiscarded)
                duplicates++;
            if (step.Gap is { } gap)
                gaps.Add(gap);
            if (step.Interval is { } interval)
                accumulator.Add(interval, settings);
        }

        return new Result(accumulator.RunTotals, gaps, accumulator.PendingDeltas(sessionId: 1), duplicates);
    }
}
