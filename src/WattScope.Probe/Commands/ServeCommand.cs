using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WattScope.Core.Discovery;
using WattScope.Core.Energy;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;
using WattScope.Probe.Serve;
using WattScope.Storage;

namespace WattScope.Probe.Commands;

/// <summary>
/// Machine-readable counterpart of --record for the desktop app's native
/// sidecar (docs/ARCHITECTURE.md, "Process model"): the same discovery ->
/// PowerSampleBuilder -> EnergyMonitor -> SamplingEngine pipeline as
/// --record, but speaking newline-delimited JSON on stdin/stdout.
///
/// The process lifetime and the monitoring lifetime are separate things
/// (Milestone 004). The process is the sidecar's owner relationship with the
/// desktop host; <see cref="MonitoringController"/> decides whether a
/// session is being recorded right now. Exactly one process may record into
/// a database (<see cref="MonitorLock"/>), and exactly one session may be
/// Active (enforced by the database itself).
///
/// The process exits when: a "shutdown" request arrives, stdin closes (the
/// host died -- so a sidecar can never outlive its host as an orphan), or
/// Ctrl+C / SIGTERM is received.
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(
        DiscoverySnapshot snapshot,
        IReadOnlyList<ISensorProvider> providers,
        CliOptions options,
        ILoggerFactory loggerFactory,
        string appVersion,
        CancellationToken cancellationToken)
    {
        var dbPath = options.DatabasePath ?? SqliteEnergyStore.DefaultDatabasePath;
        // Other WattScope processes (e.g. a `--sessions` CLI call doing crash
        // recovery) hold the lock for a moment; a *starting* recorder must not
        // mistake that for "another recorder owns this database", because the
        // host treats exit code 3 as final. Retry briefly before concluding it.
        MonitorLock? monitorLock = null;
        for (var attempt = 0; attempt < 15 && monitorLock is null; attempt++)
        {
            monitorLock = MonitorLock.TryAcquire(dbPath);
            if (monitorLock is null)
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        using var lockHolder = monitorLock;
        if (monitorLock is null)
        {
            ServeProtocol.WriteLine(new StatusMessage { State = "error", Message = $"Another WattScope recording is already writing to {dbPath}." });
            return 3;
        }

        // Two connections to the same WAL-mode database: one owned exclusively
        // by the monitoring controller (writes every flush), one for the
        // request/response loop's reads and settings writes -- the already
        // supported pattern of a separate --summary/--set-price process
        // alongside --record (docs/STORAGE.md). SqliteEnergyStore is not
        // thread-safe, so each connection is used by exactly one logical owner.
        using var recordStore = SqliteEnergyStore.Open(dbPath, loggerFactory.CreateLogger<SqliteEnergyStore>());
        using var queryStore = SqliteEnergyStore.Open(dbPath, loggerFactory.CreateLogger<SqliteEnergyStore>());
        var logger = loggerFactory.CreateLogger("WattScope.Probe.Serve");

        foreach (var recovered in recordStore.RecoverInterruptedSessions())
        {
            ServeProtocol.WriteLine(new StatusMessage
            {
                State = "recovered",
                SessionId = recovered.Id,
                Message = $"Previous recording ended unexpectedly; kept up to {(recovered.LastObservationUtc ?? recovered.StartedUtc):O}."
            });
        }
        recordStore.PruneRawSamples(DateTimeOffset.UtcNow);

        var builder = new PowerSampleBuilder(snapshot.PowerClassification);
        var source = new ProviderSampleSource(providers, builder, loggerFactory);
        await using var controller = new MonitoringController(
            recordStore, source, builder.Bindings,
            new MonitoringControllerOptions
            {
                Elevated = snapshot.OperatingSystem.IsElevated,
                CpuAvailable = builder.Bindings.Any(b => b.Channel == EnergyChannel.CpuPackage),
                GpuAvailable = builder.Bindings.Any(b => b.Channel == EnergyChannel.GpuBoard),
                AppVersion = appVersion
            },
            ServeProtocol.WriteLine, logger);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var processor = new ServeRequestProcessor(queryStore, controller, () => runCts.Cancel(), logger);

        // Must run on its own thread, not as a plain un-awaited async call:
        // Console.In.ReadLineAsync does not reliably yield control back before
        // it has a line to return, which would starve everything else.
        var requestLoop = Task.Run(() => RunRequestLoopAsync(processor, runCts, logger), CancellationToken.None);

        // One process-level subscription, forwarded to whatever run exists.
        // Idle (not monitoring) has no session to protect, so nothing to do.
        PowerModeChangedEventHandler powerHandler = (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend)
                controller.OnSuspend();
            else if (e.Mode == PowerModes.Resume)
                controller.OnResume();
        };
        SystemEvents.PowerModeChanged += powerHandler;

        try
        {
            if (options.Paused)
            {
                ServeProtocol.WriteLine(new StatusMessage
                {
                    State = "idle",
                    Elevated = snapshot.OperatingSystem.IsElevated,
                    CpuAvailable = builder.Bindings.Any(b => b.Channel == EnergyChannel.CpuPackage),
                    GpuAvailable = builder.Bindings.Any(b => b.Channel == EnergyChannel.GpuBoard)
                });
            }
            else
            {
                try
                {
                    // An explicit --interval wins for this start; otherwise the stored setting is used.
                    int? interval = options.IntervalMilliseconds != SqliteEnergyStore.DefaultMonitoringIntervalMs ? options.IntervalMilliseconds : null;
                    await controller.StartAsync(options.Resume, options.ResumeSessionId, interval).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
                {
                    // Already reported as an "error" status by the controller; stay up, idle, so the UI can show why and retry.
                    logger.LogWarning(ex, "Initial monitoring start failed; staying idle.");
                }
            }

            await Task.Delay(Timeout.Infinite, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal exit: shutdown request, stdin closed, Ctrl+C or SIGTERM.
        }
        finally
        {
            SystemEvents.PowerModeChanged -= powerHandler;
            await controller.StopAsync("Stopped (--serve exiting)").ConfigureAwait(false);
            ServeProtocol.WriteLine(new StatusMessage { State = "exiting" });
        }

        runCts.Cancel();
        await Task.WhenAny(requestLoop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Reads one JSON request per stdin line until stdin closes or shutdown is requested; never throws past its own boundary.</summary>
    private static async Task RunRequestLoopAsync(ServeRequestProcessor processor, CancellationTokenSource runCts, ILogger logger)
    {
        try
        {
            string? line;
            while ((line = await Console.In.ReadLineAsync(runCts.Token).ConfigureAwait(false)) is not null)
            {
                var response = await processor.ProcessLineAsync(line).ConfigureAwait(false);
                if (response is not null)
                    ServeProtocol.WriteLine(response);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The --serve request loop failed.");
        }
        finally
        {
            // stdin closed (the host process is gone) is itself a reason to
            // stop: a sidecar must never outlive its host as an orphan.
            runCts.Cancel();
        }
    }

    private sealed class ProviderSampleSource(IReadOnlyList<ISensorProvider> providers, PowerSampleBuilder builder, ILoggerFactory loggerFactory) : ISampleSource
    {
        public async IAsyncEnumerable<SampleEnvelope> RunAsync(TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var engine = new SamplingEngine(providers, loggerFactory.CreateLogger<SamplingEngine>());
            await foreach (var tick in engine.RunAsync(new SamplingOptions { Interval = interval, Duration = null }, cancellationToken).ConfigureAwait(false))
                yield return new SampleEnvelope(tick.Sequence, builder.Build(tick), tick.ReadDuration);
        }
    }
}
