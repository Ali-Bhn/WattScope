namespace WattScope.Core.Classification;

public sealed record PowerClassificationResult
{
    public required IReadOnlyList<PowerSensorCandidate> AllCandidates { get; init; }
    public required IReadOnlyList<PowerSensorSelection> Selections { get; init; }
}
