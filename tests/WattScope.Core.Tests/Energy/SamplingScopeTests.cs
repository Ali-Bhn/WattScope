using WattScope.Core.Classification;
using WattScope.Core.Energy;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;
using WattScope.Core.Tests.TestSupport;

namespace WattScope.Core.Tests.Energy;

/// <summary>
/// Which sensors continuous sampling asks each provider for is derived from the
/// resolved bindings only -- never from a vendor or a hardware name.
/// </summary>
public class SamplingScopeTests
{
    private const string Lhm = "LibreHardwareMonitor";
    private const string Nvml = "Nvml";

    private sealed class ScopedProvider(string providerId) : IScopedSensorProvider
    {
        public string ProviderId { get; } = providerId;
        public IReadOnlyCollection<string>? Scope { get; private set; }
        public void LimitSamplingTo(IReadOnlyCollection<string> sensorIds) => Scope = sensorIds;
        public ProviderAvailability Initialize() => new() { ProviderId = ProviderId, IsAvailable = true };
        public IReadOnlyList<HardwareDevice> GetHardware() => [];
        public IReadOnlyList<SensorReading> GetSensorReadings() => [];
        public void Dispose() { }
    }

    private static SensorReading Power(string provider, string hardwareId, string sensorId, string name, double value) => new()
    {
        ProviderId = provider,
        HardwareId = hardwareId,
        HardwareName = hardwareId,
        SensorId = sensorId,
        SensorName = name,
        SensorType = "Power",
        Unit = "W",
        Value = value,
        Availability = SensorAvailability.Available,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static HardwareDevice Device(string source, string id, HardwareCategory category) => new() { Id = id, Category = category, Name = id, Source = source };

    private static PowerSampleBuilder BuilderFor(HardwareDevice[] devices, SensorReading[] readings) =>
        new(new PowerSensorClassifier().Classify(devices, readings));

    /// <summary>The development machine: CPU from LibreHardwareMonitor, one NVIDIA GPU seen by both providers.</summary>
    private static (HardwareDevice[] Devices, SensorReading[] Readings) NvidiaMachine() =>
    (
        [Device(Lhm, "/intelcpu/0", HardwareCategory.Cpu), Device(Lhm, "/gpu-nvidia/0", HardwareCategory.GpuNvidia), Device(Nvml, "GPU-uuid", HardwareCategory.GpuNvidia)],
        [
            Power(Lhm, "/intelcpu/0", "/intelcpu/0/power/0", "CPU Package", 45.40),
            Power(Lhm, "/intelcpu/0", "/intelcpu/0/power/1", "CPU Cores", 44.14),
            Power(Lhm, "/gpu-nvidia/0", "/gpu-nvidia/0/power/0", "GPU Package", 43.01),
            Power(Nvml, "GPU-uuid", "GPU-uuid/power", "GPU Board Power", 43.01)
        ]
    );

    private static (HardwareDevice[] Devices, SensorReading[] Readings) AmdGpuMachine() =>
    (
        [Device(Lhm, "/intelcpu/0", HardwareCategory.Cpu), Device(Lhm, "/gpu-amd/0", HardwareCategory.GpuAmd)],
        [
            Power(Lhm, "/intelcpu/0", "/intelcpu/0/power/0", "CPU Package", 45.40),
            Power(Lhm, "/gpu-amd/0", "/gpu-amd/0/power/0", "GPU Package", 120.0)
        ]
    );

    [Fact]
    public void NvmlSelectedGpu_DoesNotRequireTheLibreHardwareMonitorGpuSensor()
    {
        var (devices, readings) = NvidiaMachine();
        var lhm = new ScopedProvider(Lhm);
        var nvml = new FakeSensorProvider(Nvml); // not scoped: it is left alone

        BuilderFor(devices, readings).LimitSamplingToBindings([lhm, nvml]);

        Assert.Equal(["/intelcpu/0/power/0"], lhm.Scope);
    }

    [Fact]
    public void GpuBoundToLibreHardwareMonitor_StaysInItsScope()
    {
        var (devices, readings) = AmdGpuMachine();
        var lhm = new ScopedProvider(Lhm);

        BuilderFor(devices, readings).LimitSamplingToBindings([lhm]);

        Assert.Equal(["/gpu-amd/0/power/0", "/intelcpu/0/power/0"], lhm.Scope!.Order());
    }

    [Fact]
    public void EachProvider_IsAskedOnlyForItsOwnBoundSensors()
    {
        var (devices, readings) = NvidiaMachine();
        var lhm = new ScopedProvider(Lhm);
        var nvml = new ScopedProvider(Nvml);
        var unrelated = new ScopedProvider("Other");

        BuilderFor(devices, readings).LimitSamplingToBindings([lhm, nvml, unrelated]);

        Assert.Equal(["/intelcpu/0/power/0"], lhm.Scope);
        Assert.Equal(["GPU-uuid/power"], nvml.Scope);
        Assert.Empty(unrelated.Scope!);
    }

    [Fact]
    public void AmbiguousGpuChannel_BindsNoGpuSensor_SoNoneIsRequired()
    {
        HardwareDevice[] devices =
        [
            Device(Lhm, "/gpu-nvidia/0", HardwareCategory.GpuNvidia), Device(Lhm, "/gpu-nvidia/1", HardwareCategory.GpuNvidia),
            Device(Nvml, "GPU-a", HardwareCategory.GpuNvidia), Device(Nvml, "GPU-b", HardwareCategory.GpuNvidia)
        ];
        SensorReading[] readings =
        [
            Power(Lhm, "/gpu-nvidia/0", "l0", "GPU Package", 100), Power(Lhm, "/gpu-nvidia/1", "l1", "GPU Package", 150),
            Power(Nvml, "GPU-a", "n0", "GPU Board Power", 100), Power(Nvml, "GPU-b", "n1", "GPU Board Power", 150)
        ];
        var lhm = new ScopedProvider(Lhm);
        var builder = BuilderFor(devices, readings);

        builder.LimitSamplingToBindings([lhm]);

        Assert.Empty(lhm.Scope!);
        Assert.Equal(ReadingState.Ambiguous, builder.Build([], DateTimeOffset.UtcNow, TimeSpan.Zero).GpuBoard.State); // unchanged: still Ambiguous, not Missing
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SampleFromOnlyTheBoundReadings_EqualsTheSampleFromTheFullInventory(bool nvidia)
    {
        var (devices, readings) = nvidia ? NvidiaMachine() : AmdGpuMachine();
        var builder = BuilderFor(devices, readings);
        var bound = builder.Bindings.Select(b => (b.ProviderId, b.SensorId)).ToHashSet();
        var scopedTick = readings.Where(r => bound.Contains((r.ProviderId, r.SensorId))).ToList();
        var at = DateTimeOffset.UtcNow;

        var full = builder.Build(readings, at, TimeSpan.FromSeconds(1));
        var scoped = builder.Build(scopedTick, at, TimeSpan.FromSeconds(1));

        Assert.Equal(full, scoped);
        Assert.Equal(45.40, scoped.CpuPackage.Watts);
        Assert.Equal(nvidia ? 43.01 : 120.0, scoped.GpuBoard.Watts);
    }

    [Fact]
    public void BoundSensorAbsentFromAScopedTick_IsMissing_NeverZero()
    {
        var (devices, readings) = NvidiaMachine();
        var builder = BuilderFor(devices, readings);

        var sample = builder.Build(readings.Where(r => r.ProviderId == Nvml).ToList(), DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(ReadingState.Missing, sample.CpuPackage.State);
        Assert.Null(sample.CpuPackage.Watts);
        Assert.Equal(43.01, sample.GpuBoard.Watts);
    }
}
