using LibreHardwareMonitor.Hardware;
using WattScope.Core.Sensors;
using WattScope.Hardware.LibreHardwareMonitor;
using WattScope.Hardware.Tests.TestSupport;

namespace WattScope.Hardware.Tests.LibreHardwareMonitor;

/// <summary>
/// Scoped sampling: after the energy channels are bound, a tick refreshes only
/// the hardware that owns a required sensor. The first group runs against a fake
/// hardware tree (so refreshes can be counted); the last against the real backend.
/// </summary>
public class ScopedSamplingTests
{
    private sealed class Tree
    {
        public FakeLhmHardware Motherboard { get; } = new("/motherboard", HardwareType.Motherboard);
        public FakeLhmHardware SuperIo { get; } = new("/lpc/nct6687d", HardwareType.SuperIO);
        public FakeLhmHardware Cpu { get; } = new("/intelcpu/0");
        public FakeLhmHardware Memory { get; } = new("/ram", HardwareType.Memory);
        public FakeLhmHardware Gpu { get; } = new("/gpu-nvidia/0", HardwareType.GpuNvidia);
        public FakeLhmSensor CpuPackage { get; }
        public FakeLhmSensor CpuCores { get; }
        public FakeLhmSensor GpuPackage { get; }
        public FakeLhmSensor BoardFan { get; }
        public LibreHardwareMonitorProvider Provider { get; }

        public Tree()
        {
            Motherboard.AddChild(SuperIo);
            BoardFan = SuperIo.AddSensor("Fan #1", 900, SensorType.Fan);
            CpuPackage = Cpu.AddSensor("CPU Package", 45.4f);
            CpuCores = Cpu.AddSensor("CPU Cores", 44.1f);
            Cpu.AddSensor("CPU Total", 12, SensorType.Load);
            Memory.AddSensor("Memory", 40, SensorType.Load);
            GpuPackage = Gpu.AddSensor("GPU Package", 43.0f);
            Gpu.AddSensor("GPU Core", 55, SensorType.Temperature);
            Provider = new LibreHardwareMonitorProvider([Motherboard, Cpu, Memory, Gpu]);
        }

        public int[] UpdateCounts => [Motherboard.UpdateCount, SuperIo.UpdateCount, Cpu.UpdateCount, Memory.UpdateCount, Gpu.UpdateCount];
    }

    private static string IdOf(ISensor sensor) => sensor.Identifier.ToString();

    [Fact]
    public void Unscoped_RefreshesEveryNode_AndReturnsEverySensor()
    {
        var tree = new Tree();

        var readings = tree.Provider.GetSensorReadings();

        Assert.Equal([1, 1, 1, 1, 1], tree.UpdateCounts);
        Assert.Equal(7, readings.Count);
    }

    [Fact]
    public void ScopedToCpuPackage_RefreshesOnlyTheCpuNode_AndReturnsOnlyThatSensor()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo([IdOf(tree.CpuPackage)]);

        var first = tree.Provider.GetSensorReadings();
        tree.CpuPackage.Value = 61.5f;
        var second = tree.Provider.GetSensorReadings();

        // The GPU node (bound to another provider), memory and the motherboard are never touched.
        Assert.Equal([0, 0, 2, 0, 0], tree.UpdateCounts);
        var reading = Assert.Single(first);
        Assert.Equal(LibreHardwareMonitorProvider.Id, reading.ProviderId);
        Assert.Equal("/intelcpu/0/power/0", reading.SensorId);
        Assert.Equal("/intelcpu/0", reading.HardwareId);
        Assert.Equal("CPU Package", reading.SensorName);
        Assert.Equal("Power", reading.SensorType);
        Assert.Equal("W", reading.Unit);
        Assert.Equal(SensorAvailability.Available, reading.Availability);
        Assert.Equal(45.4, reading.Value!.Value, 3);
        Assert.Equal(61.5, Assert.Single(second).Value!.Value, 3); // a fresh value each tick, not a cached one
    }

    [Fact]
    public void ScopedReading_IsIdenticalToTheUnscopedReadingOfTheSameSensor()
    {
        var tree = new Tree();
        var unscoped = tree.Provider.GetSensorReadings().Single(r => r.SensorId == IdOf(tree.CpuPackage));

        tree.Provider.LimitSamplingTo([IdOf(tree.CpuPackage)]);
        var scoped = Assert.Single(tree.Provider.GetSensorReadings());

        Assert.Equal(unscoped with { Timestamp = scoped.Timestamp }, scoped);
    }

    [Fact]
    public void GpuBoundToLibreHardwareMonitor_IsStillRefreshedAndReturned()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo([IdOf(tree.CpuPackage), IdOf(tree.GpuPackage)]);

        var readings = tree.Provider.GetSensorReadings();

        Assert.Equal([0, 0, 1, 0, 1], tree.UpdateCounts);
        Assert.Equal(["/intelcpu/0/power/0", "/gpu-nvidia/0/power/0"], readings.Select(r => r.SensorId));
        Assert.Equal(43.0, readings[1].Value!.Value, 3);
    }

    [Fact]
    public void RequiredSensorOnSubHardware_AlsoRefreshesItsParent_ButNotItsSiblings()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo([IdOf(tree.BoardFan)]);

        var readings = tree.Provider.GetSensorReadings();

        Assert.Equal([1, 1, 0, 0, 0], tree.UpdateCounts);
        Assert.Equal(IdOf(tree.BoardFan), Assert.Single(readings).SensorId);
    }

    [Fact]
    public void EmptyScope_RefreshesNothing_AndReturnsNothing()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo([]);

        Assert.Empty(tree.Provider.GetSensorReadings());
        Assert.Equal([0, 0, 0, 0, 0], tree.UpdateCounts);
    }

    [Fact]
    public void RequiredSensorThatDisappears_IsAbsent_NotStaleAndNotZero()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo([IdOf(tree.CpuPackage)]);
        Assert.Single(tree.Provider.GetSensorReadings());

        tree.Cpu.SensorList.Remove(tree.CpuPackage); // LibreHardwareMonitor deactivated it

        Assert.Empty(tree.Provider.GetSensorReadings()); // PowerSampleBuilder turns an absent sensor into Missing
    }

    [Fact]
    public void UnknownRequiredSensor_IsAbsent()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo(["/intelcpu/0/power/99"]);

        Assert.Empty(tree.Provider.GetSensorReadings());
        Assert.Equal([0, 0, 0, 0, 0], tree.UpdateCounts);
    }

    [Fact]
    public void RequiredSensorWithoutAValue_IsNullValue_NeverZero()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo([IdOf(tree.CpuPackage)]);
        tree.CpuPackage.Value = null;

        var reading = Assert.Single(tree.Provider.GetSensorReadings());

        Assert.Equal(SensorAvailability.NullValue, reading.Availability);
        Assert.Null(reading.Value);
    }

    [Fact]
    public void FailedRefreshOfABoundNode_IsUpdateFailed_NeverTheOldValue()
    {
        var tree = new Tree();
        tree.Provider.LimitSamplingTo([IdOf(tree.CpuPackage)]);
        tree.Cpu.ThrowOnUpdate = true;

        var reading = Assert.Single(tree.Provider.GetSensorReadings());

        Assert.Equal(SensorAvailability.UpdateFailed, reading.Availability);
        Assert.Null(reading.Value);
    }
}

/// <summary>
/// The one scoped-sampling test that opens the real backend. LibreHardwareMonitor instances opened at the same
/// time interfere with each other, so it shares a collection with every other real-backend test (xUnit runs
/// classes in parallel); the fake-tree tests above stay parallel.
/// </summary>
[Collection("LibreHardwareMonitor real backend")]
public class ScopedSamplingRealBackendTests
{
    [Fact]
    public void RealBackend_DiscoveryStaysComplete_BeforeAndAfterScopedSampling()
    {
        using var provider = new LibreHardwareMonitorProvider();
        var discovery = new DiscoveryService([provider]);

        var before = discovery.Discover().SensorInventory.Select(r => r.SensorId).ToList();
        Assert.NotEmpty(before);

        provider.LimitSamplingTo([before[0]]);
        var scoped = Assert.Single(provider.GetSensorReadings());
        Assert.Equal(before[0], scoped.SensorId);

        // A new discovery pass re-initializes the provider and must see the whole inventory again.
        var after = discovery.Discover().SensorInventory.Select(r => r.SensorId).ToList();
        Assert.Equal(before, after);
        Assert.Equal(before.Count, provider.GetSensorReadings().Count);
    }
}
