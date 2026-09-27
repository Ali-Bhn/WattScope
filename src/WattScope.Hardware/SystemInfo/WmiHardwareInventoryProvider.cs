using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WattScope.Core.Hardware;

namespace WattScope.Hardware.SystemInfo;

/// <summary>
/// Identifies hardware via WMI, independently of any sensor provider. A
/// device discovered here exists in <see cref="HardwareInventory"/> even if
/// no sensor provider can ever read a value for it. Never queries or exposes
/// serial numbers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiHardwareInventoryProvider
{
    public const string SourceId = "Wmi";

    private readonly ILogger<WmiHardwareInventoryProvider>? _logger;

    public WmiHardwareInventoryProvider(ILogger<WmiHardwareInventoryProvider>? logger = null)
    {
        _logger = logger;
    }

    public HardwareInventory Discover(OsInfo osInfo)
    {
        var devices = new List<HardwareDevice>();

        devices.AddRange(Query("SELECT Name FROM Win32_Processor", HardwareCategory.Cpu, "Name", null));
        devices.AddRange(Query("SELECT Name, AdapterCompatibility FROM Win32_VideoController", HardwareCategory.GpuOther, "Name", "AdapterCompatibility",
            classify: vendor => vendor switch
            {
                string v when v.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => HardwareCategory.GpuNvidia,
                string v when v.Contains("AMD", StringComparison.OrdinalIgnoreCase) || v.Contains("ATI", StringComparison.OrdinalIgnoreCase) => HardwareCategory.GpuAmd,
                string v when v.Contains("Intel", StringComparison.OrdinalIgnoreCase) => HardwareCategory.GpuIntel,
                _ => HardwareCategory.GpuOther
            }));
        devices.AddRange(Query("SELECT Product, Manufacturer FROM Win32_BaseBoard", HardwareCategory.Motherboard, "Product", "Manufacturer"));
        devices.AddRange(Query("SELECT Capacity, Manufacturer, DeviceLocator FROM Win32_PhysicalMemory", HardwareCategory.Memory, "DeviceLocator", "Manufacturer",
            nameFallback: "Memory Module"));
        devices.AddRange(Query("SELECT Model FROM Win32_DiskDrive", HardwareCategory.Storage, "Model", null));

        return new HardwareInventory
        {
            OperatingSystem = osInfo,
            Devices = devices
        };
    }

    private IEnumerable<HardwareDevice> Query(
        string wql,
        HardwareCategory defaultCategory,
        string nameProperty,
        string? vendorProperty,
        Func<string?, HardwareCategory>? classify = null,
        string? nameFallback = null)
    {
        var results = new List<HardwareDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            var index = 0;
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                using (obj)
                {
                    var name = obj[nameProperty]?.ToString();
                    var vendor = vendorProperty is null ? null : obj[vendorProperty]?.ToString();
                    var category = classify?.Invoke(vendor) ?? defaultCategory;

                    if (string.IsNullOrWhiteSpace(name))
                        name = nameFallback ?? category.ToString();

                    results.Add(new HardwareDevice
                    {
                        Id = $"wmi:{category}:{index}",
                        Category = category,
                        Name = name.Trim(),
                        Vendor = vendor?.Trim(),
                        Source = SourceId
                    });
                    index++;
                }
            }
        }
        catch (ManagementException ex)
        {
            _logger?.LogWarning(ex, "WMI query failed for {Category}: {Query}", defaultCategory, wql);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "WMI query denied (insufficient privileges) for {Category}: {Query}", defaultCategory, wql);
        }

        return results;
    }
}
