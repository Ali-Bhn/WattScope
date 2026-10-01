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
        if ((EndUtc - StartUtc).Ticks <= 0)
        {
            yield return this;
            yield break;
        }

        var first = EnergyBuckets.BucketStart(StartUtc, bucketSize) + bucketSize;
        if (first >= EndUtc)
        {
            yield return this;
            yield break;
        }

        var boundaries = new List<DateTimeOffset>();
        for (var boundary = first; boundary < EndUtc; boundary += bucketSize)
            boundaries.Add(boundary);

        foreach (var piece in SplitAt(boundaries))
            yield return piece;
    }

    /// <summary>
    /// Splits this interval at an arbitrary ascending set of UTC instants
    /// strictly inside <c>(StartUtc, EndUtc)</c> (instants outside that open
    /// range are ignored). Power at each split point is linearly interpolated
    /// <em>within</em> this already-verified interval -- the same model
    /// <see cref="SplitAtBoundaries"/> uses -- so the pieces' energies sum
    /// exactly to the whole interval's energy regardless of how many split
    /// points are given or why (a 15-minute bucket boundary, an electricity
    /// price change, or both).
    /// </summary>
    public IEnumerable<PowerInterval> SplitAt(IEnumerable<DateTimeOffset> instants)
    {
        var utcSpanTicks = (EndUtc - StartUtc).Ticks;
        if (utcSpanTicks <= 0)
        {
            yield return this;
            yield break;
        }

        var points = instants.Where(i => i > StartUtc && i < EndUtc).Distinct().OrderBy(i => i).ToList();
        if (points.Count == 0)
        {
            yield return this;
            yield break;
        }

        var pieceStart = StartUtc;
        var startFraction = 0.0;
        foreach (var boundary in points)
        {
            var endFraction = (boundary - StartUtc).Ticks / (double)utcSpanTicks;
            yield return Piece(pieceStart, boundary, startFraction, endFraction);
            pieceStart = boundary;
            startFraction = endFraction;
        }
        yield return Piece(pieceStart, EndUtc, startFraction, 1.0);
    }

    private PowerInterval Piece(DateTimeOffset start, DateTimeOffset end, double startFraction, double endFraction) => new()
    {
        StartUtc = start,
        EndUtc = end,
        Seconds = Seconds * (endFraction - startFraction),
        CpuPackage = Slice(CpuPackage, startFraction, endFraction),
        GpuBoard = Slice(GpuBoard, startFraction, endFraction)
    };

    private static LinearPower? Slice(LinearPower? power, double from, double to) =>
        power is { } p ? new LinearPower(p.At(from), p.At(to)) : null;
}
