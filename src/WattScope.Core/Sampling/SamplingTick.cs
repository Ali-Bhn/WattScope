using WattScope.Core.Diagnostics;
using WattScope.Core.Sensors;

namespace WattScope.Core.Sampling;

public sealed record SamplingTick
{
    public required int Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Process-local monotonic clock value (<see cref="System.Diagnostics.Stopwatch"/>) captured
    /// together with <see cref="Timestamp"/>. Only differences between ticks of the
    /// same process are meaningful; used for energy interval durations.
    /// </summary>
    public TimeSpan MonotonicTime { get; init; }

    /// <summary>How long reading all providers took for this tick (sampling latency).</summary>
    public TimeSpan ReadDuration { get; init; }

    public required IReadOnlyList<SensorReading> Readings { get; init; }
    public required IReadOnlyList<DiagnosticMessage> Errors { get; init; }
    public required bool GapDetected { get; init; }
    public TimeSpan? GapDuration { get; init; }
}
