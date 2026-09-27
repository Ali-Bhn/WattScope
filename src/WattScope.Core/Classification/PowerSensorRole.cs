namespace WattScope.Core.Classification;

/// <summary>
/// The physical measurement a power sensor represents. Roles are mutually
/// exclusive by design: a sensor is assigned at most one role, and the
/// classifier never combines readings across roles (see docs/SENSOR_CONTRACT.md
/// for the precedence policy that prevents double-counting).
/// </summary>
public enum PowerSensorRole
{
    CpuPackage,
    CpuCores,
    CpuGraphics,
    GpuBoard,
    GpuChip,
    Other
}
