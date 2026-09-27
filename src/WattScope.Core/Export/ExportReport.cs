using WattScope.Core.Classification;
using WattScope.Core.Diagnostics;
using WattScope.Core.Hardware;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;

namespace WattScope.Core.Export;

/// <summary>
/// The complete, self-contained JSON export produced by WattScope.Probe.
/// Deliberately excludes usernames, file paths, and hardware serial numbers
/// -- see docs/SENSOR_CONTRACT.md, "Exported data".
/// </summary>
public sealed record ExportReport
{
    public required string AppVersion { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required OsInfo OperatingSystem { get; init; }
    public required IReadOnlyList<HardwareDevice> Hardware { get; init; }
    public required IReadOnlyList<ProviderAvailability> Providers { get; init; }
    public required IReadOnlyList<SensorReading> SensorInventory { get; init; }
    public required IReadOnlyList<PowerSensorSelection> SelectedPowerSensors { get; init; }
    public SamplingConfigExport? Sampling { get; init; }
    public required IReadOnlyList<SamplingTick> Readings { get; init; }
    public required IReadOnlyList<DiagnosticMessage> Diagnostics { get; init; }
    public required PermissionInfo Permissions { get; init; }
}

public sealed record SamplingConfigExport
{
    public required int IntervalMilliseconds { get; init; }
    public int? DurationSeconds { get; init; }
}
