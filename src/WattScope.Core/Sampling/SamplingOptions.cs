namespace WattScope.Core.Sampling;

public sealed record SamplingOptions
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(1000);

    public TimeSpan Interval { get; init; } = DefaultInterval;

    /// <summary>Total run duration; null means run until cancelled.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// A tick that starts this many times later than scheduled is flagged as
    /// a gap rather than treated as normal jitter.
    /// </summary>
    public double GapDetectionFactor { get; init; } = 2.0;
}
