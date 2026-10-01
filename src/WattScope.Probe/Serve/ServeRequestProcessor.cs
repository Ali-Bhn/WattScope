using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WattScope.Core.Estimation;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using WattScope.Probe.Commands;
using WattScope.Storage;
using WattScope.Core.Energy;

namespace WattScope.Probe.Serve;

/// <summary>
/// Turns one stdin line into one response: parsing, validation and dispatch
/// for the whole --serve request protocol. Nothing here executes anything
/// the request names -- <c>cmd</c> is matched against a fixed set of
/// handlers, and every parameter is typed and range-checked. Never throws
/// past its boundary: malformed or invalid input becomes a structured error
/// response, not a crash of the monitoring process.
/// </summary>
public sealed class ServeRequestProcessor(SqliteEnergyStore queryStore, MonitoringController controller, Action requestShutdown, ILogger logger)
{
    /// <summary>The longest request line accepted; anything larger is rejected before parsing.</summary>
    public const int MaxLineLength = 16 * 1024;

    public async Task<ServeResponse?> ProcessLineAsync(string line)
    {
        // Defensive: some hosts/encodings prepend a UTF-8 BOM (U+FEFF) to the
        // very first write on a stream; strip it rather than fail that request.
        line = line.TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(line))
            return null;
        if (line.Length > MaxLineLength)
            return ServeResponse.Failure(null, $"Request too large (limit {MaxLineLength} characters).");

        ServeRequest request;
        try
        {
            request = JsonSerializer.Deserialize<ServeRequest>(line, ServeProtocol.JsonOptions)
                ?? throw new JsonException("empty request");
        }
        catch (JsonException ex)
        {
            return ServeResponse.Failure(null, $"Malformed request: {ex.Message}");
        }

        try
        {
            return ServeResponse.Success(request.Id, await HandleAsync(request).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // Expected, user-facing failures (bad input, unknown session, invalid setting): reported, not logged as bugs.
            return ServeResponse.Failure(request.Id, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing --serve request {Cmd}", request.Cmd);
            return ServeResponse.Failure(request.Id, "Internal error; see the log for detail.");
        }
    }

    private async Task<object?> HandleAsync(ServeRequest request)
    {
        switch (request.Cmd)
        {
            case "get_monitoring_state":
                return controller.Snapshot();
            case "start_monitoring":
                return await controller.StartAsync(request.Resume ?? false, request.SessionId, request.IntervalMs).ConfigureAwait(false);
            case "stop_monitoring":
                return await controller.StopAsync().ConfigureAwait(false);
            case "set_monitoring_interval":
                return await SetIntervalAsync(request).ConfigureAwait(false);
            case "get_monitoring_interval":
                return new { intervalMs = queryStore.GetMonitoringIntervalMs() };
            case "get_sessions":
                return queryStore.ListSessions(50)
                    .Select(s => new { s.Id, s.StartedUtc, s.EndedUtc, s.Status, s.EndReason, Totals = EnergyReportBuilder.Sum(queryStore.GetSessionBuckets(s.Id)) })
                    .ToList();
            case "get_summary":
                return BuildSummaryResponse(request);
            case "get_prices":
                return queryStore.GetPriceSchedule().Entries;
            case "set_price":
                return SetPrice(request);
            case "get_estimation":
                return queryStore.GetEstimationSettings();
            case "set_estimation":
                return SetEstimation(request);
            case "get_retention":
                return new { days = queryStore.GetRawRetentionDays() };
            case "set_retention":
                return SetRetention(request);
            case "get_db_info":
                return queryStore.GetStatistics();
            case "shutdown":
                requestShutdown();
                return new { stopping = true };
            default:
                throw new ArgumentException($"Unknown command '{request.Cmd}'.");
        }
    }

    /// <summary>
    /// Stores the interval; if monitoring is running, restarts the run at the
    /// new interval *in the same session* (the sampling loop has no live
    /// reconfigure path), which records an honest "Paused" gap, never energy.
    /// </summary>
    private async Task<object> SetIntervalAsync(ServeRequest request)
    {
        if (request.IntervalMs is not { } intervalMs)
            throw new ArgumentException("intervalMs is required.");
        queryStore.SetMonitoringIntervalMs(intervalMs);

        var current = controller.Snapshot();
        if (current.State == "recording" && current.IntervalMs != intervalMs)
        {
            await controller.StopAsync("Interval changed").ConfigureAwait(false);
            await controller.StartAsync(resume: true, resumeSessionId: current.SessionId, intervalMs: intervalMs).ConfigureAwait(false);
        }
        return new { intervalMs, state = controller.Snapshot() };
    }

    private object BuildSummaryResponse(ServeRequest request)
    {
        var prices = queryStore.GetPriceSchedule();
        var tz = TimeZoneInfo.Local;

        if (request.Day is { } day)
        {
            var (start, end) = ReportPeriods.LocalDay(day, tz);
            var (report, _) = DatabaseCommands.BuildPeriodReport($"Day {day:yyyy-MM-dd}", start, end, queryStore, prices, tz, daily: false);
            return new { report };
        }

        if (request.Month is { } monthText)
        {
            if (!DateOnly.TryParseExact(monthText + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new ArgumentException("month must be 'yyyy-MM'.");
            var (start, end) = ReportPeriods.LocalMonth(parsed.Year, parsed.Month, tz);
            var (report, daily) = DatabaseCommands.BuildPeriodReport($"Month {parsed.Year:0000}-{parsed.Month:00}", start, end, queryStore, prices, tz, daily: true);
            return new { report, daily };
        }

        var session = request.SessionId is { } id ? queryStore.GetSession(id) : queryStore.GetLatestSession();
        if (session is null)
            throw new ArgumentException(request.SessionId is { } missing ? $"Session {missing} does not exist." : "No sessions recorded yet.");
        return new { report = DatabaseCommands.BuildSessionReport(queryStore, session, prices), session };
    }

    private ElectricityPrice SetPrice(ServeRequest request)
    {
        if (request.PricePerKwh is not { } pricePerKwh)
            throw new ArgumentException("pricePerKwh is required.");
        var entry = new ElectricityPrice
        {
            EffectiveFromUtc = request.EffectiveFrom ?? DateTimeOffset.UtcNow,
            PricePerKwh = pricePerKwh,
            Currency = request.Currency ?? ElectricityPriceSchedule.DefaultCurrency
        };
        queryStore.AddPrice(entry, DateTimeOffset.UtcNow);
        return entry;
    }

    private EstimationSettings SetEstimation(ServeRequest request)
    {
        var current = queryStore.GetEstimationSettings();
        var updated = current with
        {
            OtherComponentsWatts = request.OtherWatts ?? current.OtherComponentsWatts,
            PsuEfficiency = request.PsuEfficiency ?? current.PsuEfficiency,
            IncludeCpuPackage = request.IncludeCpu ?? current.IncludeCpuPackage,
            IncludeGpuBoard = request.IncludeGpu ?? current.IncludeGpuBoard
        };
        queryStore.SaveEstimationSettings(updated, DateTimeOffset.UtcNow);
        return updated;
    }

    private object SetRetention(ServeRequest request)
    {
        if (request.RetentionDays is not { } days)
            throw new ArgumentException("retentionDays is required.");
        queryStore.SetRawRetentionDays(days);
        var pruned = queryStore.PruneRawSamples(DateTimeOffset.UtcNow);
        return new { days, pruned };
    }
}
