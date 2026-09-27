using WattScope.Hardware.SystemInfo;

namespace WattScope.Hardware.Tests.SystemInfo;

/// <summary>
/// Exercises the real WMI backend -- these are integration tests that
/// require running on Windows, which is this application's only supported
/// platform.
/// </summary>
public class WmiHardwareInventoryProviderTests
{
    [Fact]
    public void Discover_ReturnsAtLeastACpuDevice_OnThisMachine()
    {
        var osInfo = OsInfoCollector.Collect();
        var inventory = new WmiHardwareInventoryProvider().Discover(osInfo);

        Assert.NotEmpty(inventory.Devices);
        Assert.Contains(inventory.Devices, d => d.Category == Core.Hardware.HardwareCategory.Cpu);
    }

    [Fact]
    public void Discover_NeverIncludesASerialNumberLikeField()
    {
        var osInfo = OsInfoCollector.Collect();
        var inventory = new WmiHardwareInventoryProvider().Discover(osInfo);

        // HardwareDevice has no serial-number property at all; this guards
        // against a future edit accidentally reintroducing one.
        var properties = typeof(Core.Hardware.HardwareDevice).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(properties, name => name.Contains("Serial", StringComparison.OrdinalIgnoreCase));
    }
}
