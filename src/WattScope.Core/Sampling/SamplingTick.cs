using WattScope.Core.Diagnostics;
using WattScope.Core.Sensors;

namespace WattScope.Core.Sampling;

public sealed record SamplingTick
{
    public required int Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required IReadOnlyList<SensorReading> Readings { get; init; }
    public required IReadOnlyList<DiagnosticMessage> Errors { get; init; }
    public required bool GapDetected { get; init; }
    public TimeSpan? GapDuration { get; init; }
}
