namespace WattScope.Core.Hardware;

public sealed record OsInfo
{
    public required string Caption { get; init; }
    public required string Version { get; init; }
    public required string Architecture { get; init; }
    public required bool IsElevated { get; init; }
}

/// <summary>
/// Hardware inventory produced independently of sensor providers. This must
/// succeed (partially, if necessary) even when no power sensor is reachable.
/// </summary>
public sealed record HardwareInventory
{
    public required OsInfo OperatingSystem { get; init; }
    public required IReadOnlyList<HardwareDevice> Devices { get; init; }
}
