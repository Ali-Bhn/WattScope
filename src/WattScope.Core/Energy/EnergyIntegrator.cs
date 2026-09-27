namespace WattScope.Core.Energy;

public sealed record IntegrationOptions
{
    public static readonly TimeSpan DefaultMaxSampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest spacing between consecutive samples that is still integrated.
    /// Anything longer is an unverified gap: no energy is interpolated across it.
    /// </summary>
    public TimeSpan MaxSampleInterval { get; init; } = DefaultMaxSampleInterval;

    /// <summary>
    /// Maximum allowed disagreement between wall-clock and monotonic elapsed
    /// time for one interval. Larger disagreement means one clock jumped or
    /// stopped (system time change, or a sleep only one clock counted).
    /// </summary>
    public TimeSpan ClockTolerance { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Default options for a given sampling interval: tolerate up to 3 missed ticks' worth of jitter, at least 5 s.</summary>
    public static IntegrationOptions ForSamplingInterval(TimeSpan samplingInterval) => new()
    {
        MaxSampleInterval = samplingInterval * 3 > DefaultMaxSampleInterval ? samplingInterval * 3 : DefaultMaxSampleInterval
    };
}

public enum IntegrationOutcome
{
    /// <summary>First sample of a new chain; nothing integrated yet. <see cref="IntegrationStep.Gap"/> may describe the gap that preceded it.</summary>
    ChainStarted,

    /// <summary>The interval from the previous sample was verified; see <see cref="IntegrationStep.Interval"/>.</summary>
    Integrated,

    /// <summary>Same timestamp as the previous sample; the sample was discarded (never integrated twice).</summary>
    DuplicateDiscarded
}

public readonly record struct IntegrationStep(IntegrationOutcome Outcome, PowerInterval? Interval, MonitoringGap? Gap);

/// <summary>
/// Builds verified intervals from a stream of samples. Interval duration is
/// the monotonic elapsed time between samples -- never an assumed nominal
/// sampling interval. An interval is only produced when both samples belong
/// to the same unbroken chain; otherwise the time between them is reported
/// as a <see cref="MonitoringGap"/> and no energy is attributed to it.
/// See docs/ENERGY_MODEL.md, "Integration".
/// </summary>
/// <remarks>Not thread-safe; the caller serializes access.</remarks>
public sealed class EnergyIntegrator
{
    private readonly IntegrationOptions _options;
    private PowerSample? _last;
    private GapReason? _pendingBreak;
    private DateTimeOffset? _pendingBreakStartUtc;
    private string? _pendingBreakDetail;

    public EnergyIntegrator(IntegrationOptions? options = null)
    {
        _options = options ?? new IntegrationOptions();
    }

    public PowerSample? LastSample => _last;

    /// <summary>
    /// Ends the current chain. The next accepted sample starts a new chain,
    /// and the time from <paramref name="gapStartUtc"/> (default: the last
    /// accepted sample) to it is reported as a gap with this reason. A later
    /// call before the next sample keeps the earliest start but updates the reason.
    /// </summary>
    public void BreakChain(GapReason reason, DateTimeOffset? gapStartUtc = null, string? detail = null)
    {
        _pendingBreakStartUtc ??= gapStartUtc ?? _last?.UtcTime;
        _pendingBreak = reason;
        _pendingBreakDetail = detail;
        _last = null;
    }

    public IntegrationStep Accept(PowerSample sample)
    {
        if (_last is null)
        {
            MonitoringGap? gap = null;
            if (_pendingBreak is { } reason && _pendingBreakStartUtc is { } start)
                gap = new MonitoringGap { StartUtc = start, EndUtc = sample.UtcTime, Reason = reason, Detail = _pendingBreakDetail };
            _pendingBreak = null;
            _pendingBreakStartUtc = null;
            _pendingBreakDetail = null;
            _last = sample;
            return new IntegrationStep(IntegrationOutcome.ChainStarted, null, gap);
        }

        var previous = _last;
        var monoElapsed = sample.MonotonicTime - previous.MonotonicTime;
        var utcElapsed = sample.UtcTime - previous.UtcTime;

        if (monoElapsed == TimeSpan.Zero || utcElapsed == TimeSpan.Zero)
            return new IntegrationStep(IntegrationOutcome.DuplicateDiscarded, null, null);

        string? gapDetail = null;
        var gapReason = (GapReason?)null;
        if (monoElapsed < TimeSpan.Zero || utcElapsed < TimeSpan.Zero)
        {
            gapReason = GapReason.ClockDiscontinuity;
            gapDetail = $"Time went backwards (monotonic {monoElapsed.TotalSeconds:F3} s, UTC {utcElapsed.TotalSeconds:F3} s).";
        }
        else if (monoElapsed > _options.MaxSampleInterval)
        {
            gapReason = GapReason.SamplingStall;
            gapDetail = $"No sample for {monoElapsed.TotalSeconds:F3} s (limit {_options.MaxSampleInterval.TotalSeconds:F3} s).";
        }
        else if ((utcElapsed - monoElapsed).Duration() > _options.ClockTolerance)
        {
            gapReason = GapReason.ClockDiscontinuity;
            gapDetail = $"UTC advanced {utcElapsed.TotalSeconds:F3} s but monotonic clock advanced {monoElapsed.TotalSeconds:F3} s.";
        }

        _last = sample;

        if (gapReason is { } r)
        {
            var gap = new MonitoringGap { StartUtc = previous.UtcTime, EndUtc = sample.UtcTime, Reason = r, Detail = gapDetail };
            return new IntegrationStep(IntegrationOutcome.ChainStarted, null, gap);
        }

        var interval = new PowerInterval
        {
            StartUtc = previous.UtcTime,
            EndUtc = sample.UtcTime,
            Seconds = monoElapsed.TotalSeconds,
            CpuPackage = Linear(previous.CpuPackage, sample.CpuPackage),
            GpuBoard = Linear(previous.GpuBoard, sample.GpuBoard)
        };
        return new IntegrationStep(IntegrationOutcome.Integrated, interval, null);
    }

    private static LinearPower? Linear(ChannelReading start, ChannelReading end) =>
        start.IsValid && end.IsValid ? new LinearPower(start.Watts!.Value, end.Watts!.Value) : null;
}
