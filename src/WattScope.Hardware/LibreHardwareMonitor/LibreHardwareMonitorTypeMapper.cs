using LibreHardwareMonitor.Hardware;
using WattScope.Core.Hardware;

namespace WattScope.Hardware.LibreHardwareMonitor;

internal static class LibreHardwareMonitorTypeMapper
{
    public static HardwareCategory ToCategory(HardwareType type) => type switch
    {
        HardwareType.Cpu => HardwareCategory.Cpu,
        HardwareType.GpuNvidia => HardwareCategory.GpuNvidia,
        HardwareType.GpuAmd => HardwareCategory.GpuAmd,
        HardwareType.GpuIntel => HardwareCategory.GpuIntel,
        HardwareType.Motherboard => HardwareCategory.Motherboard,
        HardwareType.Memory => HardwareCategory.Memory,
        HardwareType.Storage => HardwareCategory.Storage,
        _ => HardwareCategory.Other
    };
}
