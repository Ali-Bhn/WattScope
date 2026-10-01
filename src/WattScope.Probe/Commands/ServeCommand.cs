using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WattScope.Core.Discovery;
using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Monitoring;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;
using WattScope.Probe.Serve;
using WattScope.Storage;

namespace WattScope.Probe.Commands;

/// <summary>
/// Machine-readable counterpart of --record for the desktop app's native
/// sidecar (Part I, docs/ARCHITECTURE.md "Data flow (--serve)"): the same
/// discovery -> PowerSampleBuilder -> EnergyMonitor -> SamplingEngine
/// pipeline as --record, reused rather than duplicated, but speaking
/// newline-delimited JSON on stdin/stdout instead of printing text.
/// Exactly one process may record at a time (the same MonitorLock --record
/// uses), so a --serve and a --record can never both write.
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
        using var monitorLock = MonitorLock.TryAcquire(dbPath);
        if (monitorLock is null)
        {
            ServeProtocol.WriteLine(new StatusMessage { State = "error", Message = $"Another WattScope recording is already writing to {dbPath}." });
            return 3;
        }

        // Two connections to the same WAL-mode database: one owned exclusively
        // by the recording monitor (writes every flush), one for the
        // request/response loop's reads and settings writes. This is exactly
        // the already-supported pattern of a separate --summary/--set-price
        // process reading/writing while --record holds the lock
        // (docs/STORAGE.md) -- just both ends alive in one process instead of
        // two. SqliteEnergyStore is documented as not thread-safe, so each
        // connection is touched by exactly one loop/thread.
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
        var interval = TimeSpan.FromMilliseconds(options.IntervalMilliseconds);
        var monitor = new EnergyMonitor(recordStore,
            new EnergyMonitorOptions { Integration = IntegrationOptions.ForSamplingInterval(interval) },
            logger: loggerFactory.CreateLogger<EnergyMonitor>(),
            priceScheduleProvider: recordStore.GetPriceSchedule);

        MonitoringSession session;
        try
        {
            session = options.Resume
                ? monitor.Resume(options.ResumeSessionId ?? recordStore.GetLatestSession()?.Id
                    ?? throw new InvalidOperationException("There is no session to resume."), builder.Bindings)
                : monitor.Start(builder.Bindings, recordStore.GetEstimationSettings(), snapshot.OperatingSystem.IsElevated, appVersion);
        }
        catch (InvalidOperationException ex)
        {
            ServeProtocol.WriteLine(new StatusMessage { State = "error", Message = ex.Message });
            return 1;
        }

        ServeProtocol.WriteLine(new StatusMessage
        {
            State = "recording",
            SessionId = session.Id,
            Elevated = snapshot.OperatingSystem.IsElevated,
            CpuAvailable = builder.Bindings.Any(b => b.Channel == EnergyChannel.CpuPackage),
            GpuAvailable = builder.Bindings.Any(b => b.Channel == EnergyChannel.GpuBoard)
        });

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Must run on its own thread, not just be an un-awaited async call:
        // Console.In.ReadLineAsync does not reliably yield control back to
        // this method before it has a line to return, which would otherwise
        // starve the sampling loop below of any chance to run at all.
        var requestLoop = Task.Run(() => RunRequestLoopAsync(queryStore, runCts, logger), CancellationToken.None);

        PowerModeChangedEventHandler powerHandler = (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend)
            {
                monitor.OnSuspend();
                ServeProtocol.WriteLine(new StatusMessage { State = "suspended", SessionId = session.Id });
            }
            else if (e.Mode == PowerModes.Resume)
            {
                monitor.OnResume();
                ServeProtocol.WriteLine(new StatusMessage { State = "resumed", SessionId = session.Id });
            }
        };
        SystemEvents.PowerModeChanged += powerHandler;

        // Fetched once, like --record's own tick display: the tick stream's
        // cost figure is explicitly an approximate live display (see
        // TickMessage.SessionCostApprox), not a source of new DB polling.
        var displayPrices = recordStore.GetPriceSchedule();
        var engine = new SamplingEngine(providers, loggerFactory.CreateLogger<SamplingEngine>());
        var samplingOptions = new SamplingOptions { Interval = interval, Duration = null };

        try
        {
            await foreach (var tick in engine.RunAsync(samplingOptions, runCts.Token))
            {
                var sample = builder.Build(tick);
                monitor.OnSample(sample);
                EmitTick(session, tick, sample, monitor.RunTotals, displayPrices, snapshot.OperatingSystem.IsElevated);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop: Ctrl+C, SIGTERM, or a "shutdown" request.
        }
        finally
        {
            SystemEvents.PowerModeChanged -= powerHandler;
            var stopped = monitor.Stop("Stopped (--serve)");
            ServeProtocol.WriteLine(new StatusMessage
            {
                State = "stopped",
                SessionId = session.Id,
                Message = stopped ? null : "The session could not be fully saved/closed; it will be recovered as interrupted on the next start."
            });
        }

        runCts.Cancel();
        await requestLoop;
        return 0;
    }

    private static void EmitTick(MonitoringSession session, SamplingTick tick, PowerSample sample, EnergyTotals runTotals, ElectricityPriceSchedule prices, bool elevated)
    {
        var estimation = session.Estimation;
        var wall = WallPowerEstimator.EstimateWatts(sample, estimation);
        var price = prices.PriceAt(sample.UtcTime);

        ServeProtocol.WriteLine(new TickMessage
        {
            SessionId = session.Id,
            Sequence = tick.Sequence,
            Utc = sample.UtcTime,
            CpuWatts = sample.CpuPackage.IsValid ? sample.CpuPackage.Watts : null,
            CpuState = sample.CpuPackage.State,
            GpuWatts = sample.GpuBoard.IsValid ? sample.GpuBoard.Watts : null,
            GpuState = sample.GpuBoard.State,
            OtherComponentsWatts = estimation.OtherComponentsWatts,
            EstimatedTotalWatts = wall.Value,
            EstimateCompleteness = wall.Completeness,
            SessionSeconds = runTotals.MonitoredSeconds,
            SessionKwh = (double)EnergyUnits.ToKwh(runTotals.EstimatedCompleteJoules),
            SessionCostApprox = price is not null ? CostCalculator.Cost(runTotals.EstimatedCompleteJoules, price.PricePerKwh) : null,
            Currency = prices.Currency,
            Elevated = elevated
        });
    }

    /// <summary>Reads one JSON request per stdin line until it closes or "shutdown" is received; never throws past its own boundary.</summary>
    private static async Task RunRequestLoopAsync(SqliteEnergyStore store, CancellationTokenSource runCts, ILogger logger)
    {
        string? line;
        try
        {
            while ((line = await Console.In.ReadLineAsync(runCts.Token).ConfigureAwait(false)) is not null)
            {
                // Defensive: some hosts/encodings prepend a UTF-8 BOM (U+FEFF)
                // to the very first write on a stream; strip it rather than
                // fail the first request a sidecar host happens to send.
                line = line.TrimStart('﻿');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ServeRequest request;
                try
                {
                    request = JsonSerializer.Deserialize<ServeRequest>(line, ServeProtocol.JsonOptions)
                        ?? throw new JsonException("empty request");
                }
                catch (JsonException ex)
                {
                    ServeProtocol.WriteLine(ServeResponse.Failure(null, $"Malformed request: {ex.Message}"));
                    continue;
                }

                try
                {
                    var data = HandleRequest(request, store, runCts);
                    ServeProtocol.WriteLine(ServeResponse.Success(request.Id, data));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArgumentOutOfRangeException)
                {
                    // Expected, user-facing failures (bad input, unknown session, invalid setting): reported, not logged as bugs.
                    ServeProtocol.WriteLine(ServeResponse.Failure(request.Id, ex.Message));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error processing --serve request {Cmd}", request.Cmd);
                    ServeProtocol.WriteLine(ServeResponse.Failure(request.Id, "Internal error; see the log for detail."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            // stdin closed (the parent process disappeared) is itself a
            // reason to stop recording, not just the request loop.
            runCts.Cancel();
        }
    }

    private static object? HandleRequest(ServeRequest request, SqliteEnergyStore store, CancellationTokenSource runCts) => request.Cmd switch
    {
        "get_sessions" => store.ListSessions(50)
            .Select(s => new { s.Id, s.StartedUtc, s.EndedUtc, s.Status, s.EndReason, Totals = EnergyReportBuilder.Sum(store.GetSessionBuckets(s.Id)) })
            .ToList(),

        "get_summary" => BuildSummaryResponse(request, store),

        "get_prices" => store.GetPriceSchedule().Entries,

        "set_price" => SetPrice(request, store),

        "get_estimation" => store.GetEstimationSettings(),

        "set_estimation" => SetEstimation(request, store),

        "get_retention" => new { days = store.GetRawRetentionDays() },

        "set_retention" => SetRetention(request, store),

        "get_db_info" => store.GetStatistics(),

        "shutdown" => Shutdown(runCts),

        _ => throw new ArgumentException($"Unknown command '{request.Cmd}'.")
    };

    private static object BuildSummaryResponse(ServeRequest request, SqliteEnergyStore store)
    {
        var prices = store.GetPriceSchedule();
        var tz = TimeZoneInfo.Local;

        if (request.Day is { } day)
        {
            var (start, end) = ReportPeriods.LocalDay(day, tz);
            var (report, _) = DatabaseCommands.BuildPeriodReport($"Day {day:yyyy-MM-dd}", start, end, store, prices, tz, daily: false);
            return new { report };
        }

        if (request.Month is { } monthText)
        {
            if (!DateOnly.TryParseExact(monthText + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new ArgumentException("month must be 'yyyy-MM'.");
            var (start, end) = ReportPeriods.LocalMonth(parsed.Year, parsed.Month, tz);
            var (report, daily) = DatabaseCommands.BuildPeriodReport($"Month {parsed.Year:0000}-{parsed.Month:00}", start, end, store, prices, tz, daily: true);
            return new { report, daily };
        }

        var session = request.SessionId is { } id ? store.GetSession(id) : store.GetLatestSession();
        if (session is null)
            throw new ArgumentException(request.SessionId is { } missing ? $"Session {missing} does not exist." : "No sessions recorded yet.");
        return new { report = DatabaseCommands.BuildSessionReport(store, session, prices), session };
    }

    private static ElectricityPrice SetPrice(ServeRequest request, SqliteEnergyStore store)
    {
        if (request.PricePerKwh is not { } pricePerKwh)
            throw new ArgumentException("pricePerKwh is required.");
        var entry = new ElectricityPrice
        {
            EffectiveFromUtc = request.EffectiveFrom ?? DateTimeOffset.UtcNow,
            PricePerKwh = pricePerKwh,
            Currency = request.Currency ?? ElectricityPriceSchedule.DefaultCurrency
        };
        store.AddPrice(entry, DateTimeOffset.UtcNow);
        return entry;
    }

    private static EstimationSettings SetEstimation(ServeRequest request, SqliteEnergyStore store)
    {
        var current = store.GetEstimationSettings();
        var updated = current with
        {
            OtherComponentsWatts = request.OtherWatts ?? current.OtherComponentsWatts,
            PsuEfficiency = request.PsuEfficiency ?? current.PsuEfficiency,
            IncludeCpuPackage = request.IncludeCpu ?? current.IncludeCpuPackage,
            IncludeGpuBoard = request.IncludeGpu ?? current.IncludeGpuBoard
        };
        store.SaveEstimationSettings(updated, DateTimeOffset.UtcNow);
        return updated;
    }

    private static object SetRetention(ServeRequest request, SqliteEnergyStore store)
    {
        if (request.RetentionDays is not { } days)
            throw new ArgumentException("retentionDays is required.");
        store.SetRawRetentionDays(days);
        var pruned = store.PruneRawSamples(DateTimeOffset.UtcNow);
        return new { days, pruned };
    }

    private static object Shutdown(CancellationTokenSource runCts)
    {
        runCts.Cancel();
        return new { stopping = true };
    }
}
