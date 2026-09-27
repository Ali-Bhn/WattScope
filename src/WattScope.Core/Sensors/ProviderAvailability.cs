namespace WattScope.Core.Sensors;

/// <summary>Result of attempting to initialize a sensor provider.</summary>
public sealed record ProviderAvailability
{
    public required string ProviderId { get; init; }
    public required bool IsAvailable { get; init; }
    public string? UnavailableReason { get; init; }
    public bool RequiresElevation { get; init; }
}
