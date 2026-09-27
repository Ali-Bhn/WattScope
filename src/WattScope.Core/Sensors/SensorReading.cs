namespace WattScope.Core.Sensors;

/// <summary>
/// A single sensor's identity and, optionally, a current value. Every field
/// required by the Milestone 001 sensor contract (see docs/SENSOR_CONTRACT.md)
/// is present so a reading is self-describing without needing a lookup back
/// into provider-specific state.
/// </summary>
public sealed record SensorReading
{
    /// <summary>Stable identifier for the provider that produced this reading (e.g. "LibreHardwareMonitor", "Nvml").</summary>
    public required string ProviderId { get; init; }

    /// <summary>Provider-native, stable identifier of the owning hardware device.</summary>
    public required string HardwareId { get; init; }

    public required string HardwareName { get; init; }

    /// <summary>Provider-native, stable identifier of this sensor. Preserved verbatim -- never derived from the display name.</summary>
    public required string SensorId { get; init; }

    public required string SensorName { get; init; }

    /// <summary>Provider-native sensor type/category string (e.g. "Power", "Temperature").</summary>
    public required string SensorType { get; init; }

    public required string Unit { get; init; }

    /// <summary>The numeric value, or null when <see cref="Availability"/> is not <see cref="SensorAvailability.Available"/>.</summary>
    public double? Value { get; init; }

    public required SensorAvailability Availability { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public bool IsAvailable => Availability == SensorAvailability.Available && Value is not null;
}
