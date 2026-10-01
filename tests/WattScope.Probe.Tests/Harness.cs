using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WattScope.Core.Energy;
using WattScope.Probe.Serve;
using WattScope.Storage;

namespace WattScope.Probe.Tests;

/// <summary>A throwaway database in a unique temp directory, deleted on dispose.</summary>
public sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Directory = Path.Combine(Path.GetTempPath(), "wattscope-probe-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        FilePath = Path.Combine(Directory, "test.db");
    }

    public string Directory { get; }
    public string FilePath { get; }
    public SqliteEnergyStore Open() => SqliteEnergyStore.Open(FilePath);

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}

/// <summary>Synthetic samples: UTC and monotonic time advance together unless a test says otherwise.</summary>
public static class Samples
{
    public static readonly DateTimeOffset BaseUtc = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public static SampleEnvelope At(long sequence, double seconds, double? cpu, double? gpu, double? utcSeconds = null) => new(sequence, new PowerSample
    {
        UtcTime = BaseUtc + TimeSpan.FromSeconds(utcSeconds ?? seconds),
        MonotonicTime = TimeSpan.FromSeconds(seconds),
        CpuPackage = cpu is { } c ? ChannelReading.FromWatts(c) : ChannelReading.Rejected(ReadingState.ImplausibleZero),
        GpuBoard = gpu is { } g ? ChannelReading.FromWatts(g) : ChannelReading.Rejected(ReadingState.Unavailable)
    });
}

/// <summary>A sample source the test drives by hand; each RunAsync is a new run reading the same channel.</summary>
public sealed class ChannelSource : ISampleSource
{
    private readonly Channel<SampleEnvelope> _channel = Channel.CreateUnbounded<SampleEnvelope>();
    public Exception? FailWith { get; set; }
    public int RunsStarted { get; private set; }

    public void Push(SampleEnvelope sample) => _channel.Writer.TryWrite(sample);

    public async IAsyncEnumerable<SampleEnvelope> RunAsync(TimeSpan interval, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RunsStarted++;
        while (true)
        {
            if (FailWith is { } ex)
                throw ex;
            SampleEnvelope item;
            try
            {
                item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return item;
        }
    }
}

/// <summary>A controller wired to a real SQLite database and a hand-driven sample source, capturing everything it emits.</summary>
public sealed class ControllerHarness : IAsyncDisposable
{
    public ControllerHarness(TempDb db, bool cpuAvailable = true, TimeSpan? flushInterval = null)
    {
        Db = db;
        Store = db.Open();
        Source = new ChannelSource();
        Controller = new MonitoringController(Store, Source, [],
            new MonitoringControllerOptions { Elevated = false, CpuAvailable = cpuAvailable, GpuAvailable = true, FlushInterval = flushInterval ?? TimeSpan.FromSeconds(10) },
            Emit, NullLogger.Instance);
    }

    public TempDb Db { get; }
    public SqliteEnergyStore Store { get; }
    public ChannelSource Source { get; }
    public MonitoringController Controller { get; }
    public List<object> Emitted { get; } = [];
    private readonly object _lock = new();

    private void Emit(object message)
    {
        lock (_lock) Emitted.Add(message);
    }

    public List<T> Of<T>() { lock (_lock) return Emitted.OfType<T>().ToList(); }

    /// <summary>Waits (bounded) until the controller has emitted <paramref name="count"/> ticks.</summary>
    public async Task WaitForTicksAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Of<TickMessage>().Count < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Only {Of<TickMessage>().Count} of {count} ticks were emitted.");
            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Controller.DisposeAsync();
        Store.Dispose();
    }
}
