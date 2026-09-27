namespace WattScope.Core.Energy;

/// <summary>Why no energy was integrated for a period. Persisted as a small integer; never renumber.</summary>
public enum GapReason : byte
{
    /// <summary>Consecutive samples were further apart than <see cref="IntegrationOptions.MaxSampleInterval"/>.</summary>
    SamplingStall = 0,

    /// <summary>Wall-clock and monotonic elapsed time disagreed (clock change, or a sleep only one clock observed), or time went backwards.</summary>
    ClockDiscontinuity = 1,

    /// <summary>The OS reported suspend/resume (sleep, hibernate).</summary>
    Suspend = 2,

    /// <summary>The session was stopped and later resumed.</summary>
    Paused = 3,

    /// <summary>The monitoring process terminated unexpectedly; the session was recovered and later resumed.</summary>
    Interrupted = 4
}

/// <summary>A period inside a session with no valid observations, for which no energy was recorded.</summary>
public sealed record MonitoringGap
{
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public required GapReason Reason { get; init; }
    public string? Detail { get; init; }

    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}
