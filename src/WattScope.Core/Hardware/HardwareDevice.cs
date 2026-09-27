namespace WattScope.Core.Hardware;

/// <summary>
/// A physically identified device. Discovered independently of whether any
/// sensor for it is available -- a device can exist with zero sensors.
/// </summary>
public sealed record HardwareDevice
{
    /// <summary>Stable identifier for this device, unique within its discovery source.</summary>
    public required string Id { get; init; }

    public required HardwareCategory Category { get; init; }

    public required string Name { get; init; }

    public string? Vendor { get; init; }

    /// <summary>Identifier of the parent device, if this device is nested (e.g. sub-hardware).</summary>
    public string? ParentId { get; init; }

    /// <summary>Source that produced this device record (e.g. "Wmi", "LibreHardwareMonitor").</summary>
    public required string Source { get; init; }
}
