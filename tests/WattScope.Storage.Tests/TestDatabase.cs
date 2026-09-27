using WattScope.Core.Energy;

namespace WattScope.Storage.Tests;

/// <summary>A throwaway database file in a unique temp directory, deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    public static readonly DateTimeOffset BaseUtc = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public TestDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "wattscope-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        FilePath = Path.Combine(Directory, "test.db");
    }

    public string Directory { get; }
    public string FilePath { get; }

    public SqliteEnergyStore Open() => SqliteEnergyStore.Open(FilePath);

    public static PowerSample Sample(double seconds, double? cpu, double? gpu, DateTimeOffset? baseUtc = null) => new()
    {
        UtcTime = (baseUtc ?? BaseUtc) + TimeSpan.FromSeconds(seconds),
        MonotonicTime = TimeSpan.FromSeconds(seconds),
        CpuPackage = cpu is { } c ? ChannelReading.FromWatts(c) : ChannelReading.Rejected(ReadingState.ImplausibleZero),
        GpuBoard = gpu is { } g ? ChannelReading.FromWatts(g) : ChannelReading.Rejected(ReadingState.Unavailable)
    };

    public static IEnumerable<PowerSample> Constant(double from, double to, double? cpu, double? gpu, DateTimeOffset? baseUtc = null)
    {
        for (var t = from; t <= to; t++)
            yield return Sample(t, cpu, gpu, baseUtc);
    }

    public static IReadOnlyList<EnergySensorBinding> DevMachineBindings { get; } =
    [
        new() { Channel = EnergyChannel.CpuPackage, ProviderId = "LibreHardwareMonitor", HardwareId = "/intelcpu/0", HardwareName = "Intel Core i9-14900KF", SensorId = "/intelcpu/0/power/0", SensorName = "CPU Package" },
        new() { Channel = EnergyChannel.GpuBoard, ProviderId = "Nvml", HardwareId = "GPU-uuid", HardwareName = "NVIDIA GeForce RTX 5070 Ti", SensorId = "GPU-uuid/power", SensorName = "GPU Board Power" }
    ];

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* best effort: a still-open handle must not fail the test */ }
    }
}
