namespace WattScope.Core.Energy;

/// <summary>
/// The measured power inputs WattScope integrates into energy. Each channel is
/// fed only by the classifier's selected sensor(s) for exactly one role, so
/// overlapping sensors (CPU Cores vs. CPU Package, LibreHardwareMonitor vs.
/// NVML board power) can never both contribute. See docs/ENERGY_MODEL.md.
/// </summary>
public enum EnergyChannel
{
    /// <summary>Fed by <see cref="Classification.PowerSensorRole.CpuPackage"/>.</summary>
    CpuPackage = 0,

    /// <summary>Fed by <see cref="Classification.PowerSensorRole.GpuBoard"/>.</summary>
    GpuBoard = 1
}
