using WattScope.Core.Classification;
using WattScope.Core.Hardware;
using WattScope.Core.Sensors;

namespace WattScope.Core.Tests.Classification;

public class PowerSensorClassifierTests
{
    private static SensorReading PowerReading(string providerId, string hardwareId, string sensorName, double? value,
        SensorAvailability availability = SensorAvailability.Available, string sensorId = "s1") => new()
    {
        ProviderId = providerId,
        HardwareId = hardwareId,
        HardwareName = "Test Hardware",
        SensorId = sensorId,
        SensorName = sensorName,
        SensorType = "Power",
        Unit = "W",
        Value = value,
        Availability = availability,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static HardwareDevice Device(string source, string id, HardwareCategory category) => new()
    {
        Id = id,
        Category = category,
        Name = "Test Device",
        Source = source
    };

    [Fact]
    public void CpuPackageAndCores_AreSelected_AndCpuMemory_IsNotAssignedARole()
    {
        var devices = new[] { Device("LibreHardwareMonitor", "cpu0", HardwareCategory.Cpu) };
        var readings = new[]
        {
            PowerReading("LibreHardwareMonitor", "cpu0", "CPU Package", 45.0, sensorId: "pkg"),
            PowerReading("LibreHardwareMonitor", "cpu0", "CPU Cores", 30.0, sensorId: "cores"),
            PowerReading("LibreHardwareMonitor", "cpu0", "CPU Memory", 2.0, sensorId: "mem"),
        };

        var result = new PowerSensorClassifier().Classify(devices, readings);

        var packageSelection = Assert.Single(result.Selections, s => s.Role == PowerSensorRole.CpuPackage);
        Assert.Equal(45.0, packageSelection.Selected!.Reading.Value);

        var coresSelection = Assert.Single(result.Selections, s => s.Role == PowerSensorRole.CpuCores);
        Assert.Equal(30.0, coresSelection.Selected!.Reading.Value);

        Assert.DoesNotContain(result.Selections, s => s.Role == PowerSensorRole.Other);
        var memoryCandidate = Assert.Single(result.AllCandidates, c => c.Reading.SensorName == "CPU Memory");
        Assert.Equal(PowerSensorRole.Other, memoryCandidate.Role);
    }

    [Fact]
    public void GpuPowerLimit_IsNeverSelectedAsAMeasuredReading()
    {
        var devices = new[] { Device("LibreHardwareMonitor", "gpu0", HardwareCategory.GpuNvidia) };
        var readings = new[]
        {
            PowerReading("LibreHardwareMonitor", "gpu0", "GPU Power Limit", 320.0, sensorId: "limit"),
        };

        var result = new PowerSensorClassifier().Classify(devices, readings);

        Assert.Empty(result.Selections);
        var candidate = Assert.Single(result.AllCandidates);
        Assert.Equal(PowerSensorRole.Other, candidate.Role);
        Assert.Equal(ClassificationSource.KnownNamePattern, candidate.Source);
    }

    [Fact]
    public void SingleNvidiaGpu_PrefersNvmlOverLibreHardwareMonitor_ForBoardPower()
    {
        var devices = new[]
        {
            Device("LibreHardwareMonitor", "lhm-gpu0", HardwareCategory.GpuNvidia),
            Device("Nvml", "nvml-gpu0", HardwareCategory.GpuNvidia),
        };
        var readings = new[]
        {
            PowerReading("LibreHardwareMonitor", "lhm-gpu0", "GPU Package", 200.0, sensorId: "lhm-power"),
            PowerReading("Nvml", "nvml-gpu0", "GPU Board Power", 205.0, sensorId: "nvml-power"),
        };

        var result = new PowerSensorClassifier().Classify(devices, readings);

        var selection = Assert.Single(result.Selections, s => s.Role == PowerSensorRole.GpuBoard);
        Assert.NotNull(selection.Selected);
        Assert.Equal("Nvml", selection.Selected!.Reading.ProviderId);
        Assert.Equal(2, selection.Candidates.Count);

        // Never summed: the selected value is exactly the NVML reading, not a combination.
        Assert.Equal(205.0, selection.Selected.Reading.Value);

        // Both candidates are marked as overlapping the same physical measurement.
        Assert.All(result.AllCandidates.Where(c => c.Role == PowerSensorRole.GpuBoard), c => Assert.True(c.HasOverlap));
    }

    [Fact]
    public void MultipleNvidiaGpus_DoesNotGuessCorrelation_AndSelectsNothing()
    {
        var devices = new[]
        {
            Device("LibreHardwareMonitor", "lhm-gpu0", HardwareCategory.GpuNvidia),
            Device("LibreHardwareMonitor", "lhm-gpu1", HardwareCategory.GpuNvidia),
            Device("Nvml", "nvml-gpu0", HardwareCategory.GpuNvidia),
            Device("Nvml", "nvml-gpu1", HardwareCategory.GpuNvidia),
        };
        var readings = new[]
        {
            PowerReading("LibreHardwareMonitor", "lhm-gpu0", "GPU Package", 150.0, sensorId: "p0"),
            PowerReading("LibreHardwareMonitor", "lhm-gpu1", "GPU Package", 160.0, sensorId: "p1"),
            PowerReading("Nvml", "nvml-gpu0", "GPU Board Power", 151.0, sensorId: "n0"),
            PowerReading("Nvml", "nvml-gpu1", "GPU Board Power", 161.0, sensorId: "n1"),
        };

        var result = new PowerSensorClassifier().Classify(devices, readings);

        var selection = Assert.Single(result.Selections, s => s.Role == PowerSensorRole.GpuBoard);
        Assert.Null(selection.Selected);
        Assert.Equal(4, selection.Candidates.Count);
    }

    [Fact]
    public void UnavailableReading_IsStillClassified_ButNotTreatedAsZero()
    {
        var devices = new[] { Device("LibreHardwareMonitor", "cpu0", HardwareCategory.Cpu) };
        var readings = new[]
        {
            PowerReading("LibreHardwareMonitor", "cpu0", "CPU Package", null, SensorAvailability.NullValue, sensorId: "pkg"),
        };

        var result = new PowerSensorClassifier().Classify(devices, readings);

        var selection = Assert.Single(result.Selections, s => s.Role == PowerSensorRole.CpuPackage);
        Assert.NotNull(selection.Selected);
        Assert.False(selection.Selected!.Reading.IsAvailable);
        Assert.Null(selection.Selected.Reading.Value);
    }

    [Fact]
    public void NonPowerSensors_AreIgnoredEntirely()
    {
        var devices = new[] { Device("LibreHardwareMonitor", "cpu0", HardwareCategory.Cpu) };
        var readings = new[]
        {
            new SensorReading
            {
                ProviderId = "LibreHardwareMonitor",
                HardwareId = "cpu0",
                HardwareName = "Test CPU",
                SensorId = "temp1",
                SensorName = "CPU Core #1",
                SensorType = "Temperature",
                Unit = "°C",
                Value = 55.0,
                Availability = SensorAvailability.Available,
                Timestamp = DateTimeOffset.UtcNow
            }
        };

        var result = new PowerSensorClassifier().Classify(devices, readings);

        Assert.Empty(result.AllCandidates);
        Assert.Empty(result.Selections);
    }
}
