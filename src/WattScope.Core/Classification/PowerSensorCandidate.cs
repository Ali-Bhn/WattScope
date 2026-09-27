using WattScope.Core.Sensors;

namespace WattScope.Core.Classification;

/// <summary>
/// A discovered power sensor tagged with its classified role. Every power
/// sensor found by any provider becomes a candidate; only a subset are ever
/// <see cref="PowerSensorSelection.Selected"/>. Keeping the two separate is
/// the explicit selection metadata required by the sensor contract.
/// </summary>
public sealed record PowerSensorCandidate
{
    public required SensorReading Reading { get; init; }
    public required PowerSensorRole Role { get; init; }
    public required ClassificationSource Source { get; init; }

    /// <summary>True when another candidate reports an overlapping physical measurement for the same role.</summary>
    public bool HasOverlap { get; init; }
}

/// <summary>
/// The outcome of applying the precedence policy for a single role: which
/// candidate (if any) was selected, and why the others were not.
/// </summary>
public sealed record PowerSensorSelection
{
    public required PowerSensorRole Role { get; init; }
    public PowerSensorCandidate? Selected { get; init; }
    public required IReadOnlyList<PowerSensorCandidate> Candidates { get; init; }
    public required string PrecedenceExplanation { get; init; }
}
