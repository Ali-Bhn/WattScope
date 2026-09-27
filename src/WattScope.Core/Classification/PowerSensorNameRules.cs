using WattScope.Core.Hardware;

namespace WattScope.Core.Classification;

/// <summary>
/// Fixed, documented tables mapping a provider's own sensor-type filter plus
/// its display name to a <see cref="PowerSensorRole"/>. Neither
/// LibreHardwareMonitor nor NVML expose a role/semantic enum beyond
/// (hardware type, sensor type, name, index); these tables are the smallest
/// deterministic mapping that avoids fuzzy string heuristics. Every entry is
/// listed in docs/SENSOR_CONTRACT.md.
/// </summary>
internal static class PowerSensorNameRules
{
    public static (PowerSensorRole Role, ClassificationSource Source) ClassifyLibreHardwareMonitor(
        HardwareCategory category, string sensorName)
    {
        if (category == HardwareCategory.Cpu)
        {
            return sensorName switch
            {
                "CPU Package" => (PowerSensorRole.CpuPackage, ClassificationSource.KnownNamePattern),
                "CPU Cores" => (PowerSensorRole.CpuCores, ClassificationSource.KnownNamePattern),
                "CPU Graphics" => (PowerSensorRole.CpuGraphics, ClassificationSource.KnownNamePattern),
                // Known but intentionally not mapped to a role: DRAM power and
                // individual per-core sensors are never summed into CpuCores
                // by this application (see docs/SENSOR_CONTRACT.md, rule 7).
                "CPU Memory" => (PowerSensorRole.Other, ClassificationSource.KnownNamePattern),
                _ when sensorName.StartsWith("CPU Core #", StringComparison.Ordinal)
                    => (PowerSensorRole.Other, ClassificationSource.KnownNamePattern),
                _ => (PowerSensorRole.Other, ClassificationSource.Unclassified)
            };
        }

        if (category is HardwareCategory.GpuNvidia or HardwareCategory.GpuAmd or HardwareCategory.GpuIntel)
        {
            return sensorName switch
            {
                "GPU Power" => (PowerSensorRole.GpuBoard, ClassificationSource.KnownNamePattern),
                "GPU Package" => (PowerSensorRole.GpuBoard, ClassificationSource.KnownNamePattern),
                "GPU Chip Power" => (PowerSensorRole.GpuChip, ClassificationSource.KnownNamePattern),
                // A configured limit, not a measured draw -- must never be
                // treated as a reading.
                "GPU Power Limit" => (PowerSensorRole.Other, ClassificationSource.KnownNamePattern),
                _ => (PowerSensorRole.Other, ClassificationSource.Unclassified)
            };
        }

        return (PowerSensorRole.Other, ClassificationSource.Unclassified);
    }

    public static (PowerSensorRole Role, ClassificationSource Source) ClassifyNvml(string sensorName)
    {
        return sensorName switch
        {
            "GPU Board Power" => (PowerSensorRole.GpuBoard, ClassificationSource.ProviderMetadata),
            _ => (PowerSensorRole.Other, ClassificationSource.Unclassified)
        };
    }
}
