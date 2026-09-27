using WattScope.Core.Classification;
using WattScope.Core.Energy;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Core.Tests.Energy;

public class PowerSampleBuilderTests
{
    private const string Lhm = "LibreHardwareMonitor";
    private const string Nvml = "Nvml";

    private static SensorReading Power(string provider, string hardwareId, string sensorId, string name, double? value,
        SensorAvailability availability = SensorAvailability.Available) => new()
    {
        ProviderId = provider,
        HardwareId = hardwareId,
        HardwareName = hardwareId,
        SensorId = sensorId,
        SensorName = name,
        SensorType = "Power",
        Unit = "W",
        Value = availability == SensorAvailability.Available ? value : null,
        Availability = availability,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static HardwareDevice Device(string source, string id, HardwareCategory category) => new()
    {
        Id = id,
        Category = category,
        Name = id,
        Source = source
    };

    /// <summary>The elevated reading set reported from the development machine (docs/SENSOR_CONTRACT.md section 4).</summary>
    private static (HardwareDevice[] Devices, SensorReading[] Readings) DevMachineElevated() =>
    (
        [
            Device(Lhm, "/intelcpu/0", HardwareCategory.Cpu),
            Device(Lhm, "/gpu-nvidia/0", HardwareCategory.GpuNvidia),
            Device(Nvml, "GPU-uuid", HardwareCategory.GpuNvidia)
        ],
        [
            Power(Lhm, "/intelcpu/0", "/intelcpu/0/power/0", "CPU Package", 45.40),
            Power(Lhm, "/intelcpu/0", "/intelcpu/0/power/1", "CPU Cores", 44.14),
            Power(Lhm, "/intelcpu/0", "/intelcpu/0/power/3", "CPU Memory", 1.5),
            Power(Lhm, "/gpu-nvidia/0", "/gpu-nvidia/0/power/0", "GPU Package", 43.01),
            Power(Lhm, "/gpu-nvidia/0", "/gpu-nvidia/0/power/1", "GPU Power Limit", 300),
            Power(Nvml, "GPU-uuid", "GPU-uuid/power", "GPU Board Power", 43.01)
        ]
    );

    private static PowerSampleBuilder BuilderFor(HardwareDevice[] devices, SensorReading[] readings) =>
        new(new PowerSensorClassifier().Classify(devices, readings));

    [Fact]
    public void DevMachine_CpuCoresAreNeverAddedToCpuPackage_AndGpuIsCountedOnce()
    {
        var (devices, readings) = DevMachineElevated();
        var builder = BuilderFor(devices, readings);

        var sample = builder.Build(readings, DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(45.40, sample.CpuPackage.Watts);          // not 45.40 + 44.14
        Assert.Equal(43.01, sample.GpuBoard.Watts);            // not 43.01 + 43.01
        Assert.Equal(2, builder.Bindings.Count);
        var gpu = Assert.Single(builder.Bindings, b => b.Channel == EnergyChannel.GpuBoard);
        Assert.Equal(Nvml, gpu.ProviderId);
        var cpu = Assert.Single(builder.Bindings, b => b.Channel == EnergyChannel.CpuPackage);
        Assert.Equal("CPU Package", cpu.SensorName);
    }

    [Fact]
    public void NonElevatedZeroCpuPackage_IsRejected_NotTreatedAsZeroWatts()
    {
        var (devices, readings) = DevMachineElevated();
        readings[0] = readings[0] with { Value = 0.0 };
        var builder = BuilderFor(devices, readings);

        var sample = builder.Build(readings, DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(ReadingState.ImplausibleZero, sample.CpuPackage.State);
        Assert.Null(sample.CpuPackage.Watts);
        Assert.True(sample.GpuBoard.IsValid);
    }

    [Fact]
    public void MultipleGpusAcrossProviders_AreAmbiguous_NotSummed()
    {
        HardwareDevice[] devices =
        [
            Device(Lhm, "/gpu-nvidia/0", HardwareCategory.GpuNvidia),
            Device(Lhm, "/gpu-nvidia/1", HardwareCategory.GpuNvidia),
            Device(Nvml, "GPU-a", HardwareCategory.GpuNvidia),
            Device(Nvml, "GPU-b", HardwareCategory.GpuNvidia)
        ];
        SensorReading[] readings =
        [
            Power(Lhm, "/gpu-nvidia/0", "l0", "GPU Package", 100),
            Power(Lhm, "/gpu-nvidia/1", "l1", "GPU Package", 150),
            Power(Nvml, "GPU-a", "n0", "GPU Board Power", 100),
            Power(Nvml, "GPU-b", "n1", "GPU Board Power", 150)
        ];
        var builder = BuilderFor(devices, readings);

        var sample = builder.Build(readings, DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(ReadingState.Ambiguous, sample.GpuBoard.State);
        Assert.Null(sample.GpuBoard.Watts);
        Assert.DoesNotContain(builder.Bindings, b => b.Channel == EnergyChannel.GpuBoard);
    }

    [Fact]
    public void DistinctCpuSockets_FromOneProvider_AreSummed()
    {
        HardwareDevice[] devices = [Device(Lhm, "/intelcpu/0", HardwareCategory.Cpu), Device(Lhm, "/intelcpu/1", HardwareCategory.Cpu)];
        SensorReading[] readings =
        [
            Power(Lhm, "/intelcpu/0", "p0", "CPU Package", 60),
            Power(Lhm, "/intelcpu/1", "p1", "CPU Package", 70),
            Power(Lhm, "/intelcpu/0", "c0", "CPU Cores", 55),
            Power(Lhm, "/intelcpu/1", "c1", "CPU Cores", 65)
        ];
        var builder = BuilderFor(devices, readings);

        var sample = builder.Build(readings, DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(130, sample.CpuPackage.Watts);
    }

    [Fact]
    public void SelectedSensorMissingFromTick_IsMissing_NotZero()
    {
        var (devices, readings) = DevMachineElevated();
        var builder = BuilderFor(devices, readings);

        var sample = builder.Build(readings.Where(r => r.ProviderId != Nvml).ToList(), DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(ReadingState.Missing, sample.GpuBoard.State);
        Assert.Null(sample.GpuBoard.Watts);
    }

    [Fact]
    public void UnavailableSensor_IsUnavailable()
    {
        var (devices, readings) = DevMachineElevated();
        var builder = BuilderFor(devices, readings);
        readings[5] = readings[5] with { Value = null, Availability = SensorAvailability.UpdateFailed };

        var sample = builder.Build(readings, DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(ReadingState.Unavailable, sample.GpuBoard.State);
    }

    [Fact]
    public void NoGpuSensorAtAll_IsNotPresent()
    {
        HardwareDevice[] devices = [Device(Lhm, "/intelcpu/0", HardwareCategory.Cpu)];
        SensorReading[] readings = [Power(Lhm, "/intelcpu/0", "p0", "CPU Package", 20)];
        var builder = BuilderFor(devices, readings);

        var sample = builder.Build(readings, DateTimeOffset.UtcNow, TimeSpan.Zero);

        Assert.Equal(ReadingState.NotPresent, sample.GpuBoard.State);
        Assert.Equal(ReadingState.NotPresent, builder.FixedState(EnergyChannel.GpuBoard));
        Assert.Equal(20, sample.CpuPackage.Watts);
    }

    [Theory]
    [InlineData(double.NaN, ReadingState.NonFinite)]
    [InlineData(double.PositiveInfinity, ReadingState.NonFinite)]
    [InlineData(double.NegativeInfinity, ReadingState.NonFinite)]
    [InlineData(-5.0, ReadingState.Negative)]
    [InlineData(5000.0, ReadingState.AboveMaximum)]
    [InlineData(0.0, ReadingState.ImplausibleZero)]
    [InlineData(45.4, ReadingState.Valid)]
    public void CpuPackageValidation(double watts, ReadingState expected)
    {
        var result = new PowerValidationRules().Validate(EnergyChannel.CpuPackage, watts);

        Assert.Equal(expected, result.State);
        Assert.Equal(expected == ReadingState.Valid, result.Watts is not null);
    }

    [Fact]
    public void ZeroGpuPower_IsValid()
    {
        Assert.True(new PowerValidationRules().Validate(EnergyChannel.GpuBoard, 0.0).IsValid);
    }
}
