namespace WattScope.Core.Energy;

/// <summary>Power assumed to vary linearly between two valid samples (the trapezoidal-rule model).</summary>
public readonly record struct LinearPower(double StartWatts, double EndWatts)
{
    /// <summary>Trapezoidal rule: exact for power that varies linearly between the two samples.</summary>
    public double Joules(double seconds) => (StartWatts + EndWatts) * 0.5 * seconds;

    /// <summary>Power at a fraction (0..1) of the way through the interval.</summary>
    public double At(double fraction) => StartWatts + (EndWatts - StartWatts) * fraction;
}

/// <summary>
/// A verified interval between two consecutive samples of the same monitoring
/// chain. A channel is null when either endpoint was not valid -- that
/// channel then contributes no energy (and no coverage) for this interval.
/// </summary>
public sealed record PowerInterval
{
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>Elapsed seconds, from the monotonic clock (authoritative for energy).</summary>
    public required double Seconds { get; init; }

    public LinearPower? CpuPackage { get; init; }
    public LinearPower? GpuBoard { get; init; }

    /// <summary>
    /// Splits this interval at every multiple of <paramref name="bucketSize"/>
    /// (UTC, aligned to the Unix epoch). Power at a split point is linearly
    /// interpolated <em>within</em> this already-verified interval, so the
    /// pieces' energies sum exactly to the whole interval's energy.
    /// </summary>
    public IEnumerable<PowerInterval> SplitAtBoundaries(TimeSpan bucketSize)
    {
        var utcSpanTicks = (EndUtc - StartUtc).Ticks;
        var boundary = EnergyBuckets.BucketStart(StartUtc, bucketSize) + bucketSize;
        if (utcSpanTicks <= 0 || boundary >= EndUtc)
        {
            yield return this;
            yield break;
        }

        var pieceStart = StartUtc;
        var startFraction = 0.0;
        while (pieceStart < EndUtc)
        {
            var pieceEnd = boundary < EndUtc ? boundary : EndUtc;
            var endFraction = pieceEnd == EndUtc ? 1.0 : (pieceEnd - StartUtc).Ticks / (double)utcSpanTicks;

            yield return new PowerInterval
            {
                StartUtc = pieceStart,
                EndUtc = pieceEnd,
                Seconds = Seconds * (endFraction - startFraction),
                CpuPackage = Slice(CpuPackage, startFraction, endFraction),
                GpuBoard = Slice(GpuBoard, startFraction, endFraction)
            };

            pieceStart = pieceEnd;
            startFraction = endFraction;
            boundary += bucketSize;
        }
    }

    private static LinearPower? Slice(LinearPower? power, double from, double to) =>
        power is { } p ? new LinearPower(p.At(from), p.At(to)) : null;
}
