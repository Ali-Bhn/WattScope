using WattScope.Core.Classification;
using WattScope.Core.Diagnostics;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Core.Discovery;

/// <summary>
/// The full result of one discovery pass: hardware inventory (independent of
/// sensors), every provider's availability, every discovered sensor with its
/// current value, and the power-sensor classification/selection derived from
/// them. Used both to render console output and, unchanged, as the basis for
/// a JSON export.
/// </summary>
public sealed record DiscoverySnapshot
{
    public required OsInfo OperatingSystem { get; init; }
    public required IReadOnlyList<HardwareDevice> Hardware { get; init; }
    public required IReadOnlyList<ProviderAvailability> Providers { get; init; }
    public required IReadOnlyList<SensorReading> SensorInventory { get; init; }
    public required PowerClassificationResult PowerClassification { get; init; }
    public required IReadOnlyList<DiagnosticMessage> Diagnostics { get; init; }
    public required PermissionInfo Permissions { get; init; }
}
