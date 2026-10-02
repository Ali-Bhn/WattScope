using System.Globalization;
using System.Runtime.InteropServices;
using WattScope.Core.Energy;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;
using WattScope.Core.Sensors;
using WattScope.Storage;

namespace WattScope.Probe.Serve;

/// <summary>
/// Static facts about this sidecar process for the Diagnostics panel. Deliberately small: versions, provider
/// availability and the two selected energy sensors. No machine name, user name, serial numbers or full
/// sensor inventory.
/// </summary>
public sealed record DiagnosticsInfo
{
    public required string AppVersion { get; init; }
    public required string OsDescription { get; init; }
    public required string Architecture { get; init; }
    public required bool Elevated { get; init; }
    public required IReadOnlyList<ProviderAvailability> Providers { get; init; }
    public required IReadOnlyList<EnergySensorBinding> Sensors { get; init; }
    public required string DatabasePath { get; init; }
}

/// <summary>Application-level requests of the --serve protocol that are neither monitoring control nor gaming: first run, pricing, diagnostics, data management.</summary>
internal static class AppHandlers
{
    public const string FirstRunKey = "first_run_completed";

    /// <summary>The highest price per kWh accepted (any currency). Far above any real tariff; it exists to catch a misplaced decimal separator.</summary>
    public const decimal MaxPricePerKwh = 100m;
    public const int MaxPriceDecimals = 6;

    /// <summary>Deleting history is only offered for data at least this old, and always needs an explicit confirmation of the exact scope.</summary>
    public const int MinHistoryDeletionDays = 30;

    // ---------------------------------------------------------------- first run

    public static object AppState(SqliteEnergyStore store, DiagnosticsInfo? info) => new
    {
        firstRunCompleted = store.GetAppSetting(FirstRunKey) is { Length: > 0 },
        appVersion = info?.AppVersion,
        schemaVersion = store.SchemaVersion
    };

    public static object CompleteFirstRun(SqliteEnergyStore store)
    {
        if (store.GetAppSetting(FirstRunKey) is not { Length: > 0 })
            store.SetAppSetting(FirstRunKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return AppState(store, null);
    }

    // ---------------------------------------------------------------- electricity price

    public static object PriceInfo(SqliteEnergyStore store)
    {
        var schedule = store.GetPriceSchedule();
        var now = DateTimeOffset.UtcNow;
        return new
        {
            currency = schedule.Entries.Count > 0 ? schedule.Currency : null,
            defaultCurrency = ElectricityPriceSchedule.DefaultCurrency,
            current = schedule.PriceAt(now),
            entries = schedule.Entries,
            maxPricePerKwh = MaxPricePerKwh,
            maxDecimals = MaxPriceDecimals
        };
    }

    /// <summary>
    /// What a new price would change: the energy already recorded between its effective time and the next
    /// price (or now). A price only ever *adds* an entry; existing entries are never edited or removed.
    /// </summary>
    public static object PreviewPrice(SqliteEnergyStore store, ServeRequest request)
    {
        var effective = request.EffectiveFrom ?? DateTimeOffset.UtcNow;
        var (affected, sessions, seconds) = AffectedEnergy(store, effective);
        return new
        {
            effectiveFromUtc = effective,
            affectsRecordedEnergy = affected,
            affectedSessions = sessions,
            affectedMonitoredSeconds = seconds
        };
    }

    private static (bool Affected, int Sessions, double Seconds) AffectedEnergy(SqliteEnergyStore store, DateTimeOffset effective)
    {
        var now = DateTimeOffset.UtcNow;
        if (effective >= now)
            return (false, 0, 0);
        var schedule = store.GetPriceSchedule();
        var next = schedule.Entries.Where(e => e.EffectiveFromUtc > effective).Select(e => (DateTimeOffset?)e.EffectiveFromUtc).FirstOrDefault();
        var buckets = store.GetBuckets(effective, next ?? now);
        var totals = EnergyReportBuilder.Sum(buckets);
        return (buckets.Count > 0, buckets.Select(b => b.SessionId).Distinct().Count(), totals.MonitoredSeconds);
    }

    public static ElectricityPrice SetPrice(SqliteEnergyStore store, ServeRequest request)
    {
        if (request.PricePerKwh is not { } pricePerKwh)
            throw new ArgumentException("pricePerKwh is required.");
        if (pricePerKwh > MaxPricePerKwh)
            throw new ArgumentException($"The price per kWh must not exceed {MaxPricePerKwh.ToString(CultureInfo.InvariantCulture)}.");
        if (decimal.Round(pricePerKwh, MaxPriceDecimals) != pricePerKwh)
            throw new ArgumentException($"The price per kWh can have at most {MaxPriceDecimals} decimal places.");

        var schedule = store.GetPriceSchedule();
        var currency = request.Currency ?? (schedule.Entries.Count > 0 ? schedule.Currency : ElectricityPriceSchedule.DefaultCurrency);
        var now = DateTimeOffset.UtcNow;
        var effective = request.EffectiveFrom ?? now;

        // Never silently reprice history: a price that reaches back over energy that is already recorded
        // must be confirmed explicitly (the UI shows what it affects first).
        if (effective < now - TimeSpan.FromMinutes(5) && request.ConfirmHistorical != true)
        {
            var (affected, sessions, _) = AffectedEnergy(store, effective);
            if (affected)
                throw new ArgumentException($"This price takes effect in the past and would change the cost of energy already recorded in {sessions} session(s). Confirm explicitly to apply it; existing prices are never edited or removed.");
        }

        var entry = new ElectricityPrice { EffectiveFromUtc = effective, PricePerKwh = pricePerKwh, Currency = currency };
        store.AddPrice(entry, now);
        return entry;
    }

    // ---------------------------------------------------------------- diagnostics

    public static object Diagnostics(SqliteEnergyStore store, MonitoringController controller, DiagnosticsInfo? info)
    {
        var stats = store.GetStatistics();
        var snapshot = controller.Snapshot();
        return new
        {
            appVersion = info?.AppVersion,
            runtime = RuntimeInformation.FrameworkDescription,
            os = info?.OsDescription,
            architecture = info?.Architecture,
            elevated = info?.Elevated ?? snapshot.Elevated,
            databasePath = store.DatabasePath,
            schemaVersion = stats.SchemaVersion,
            databaseBytes = stats.DatabaseBytes + stats.WalBytes,
            monitoringState = snapshot.State,
            monitoringIntervalMs = snapshot.IntervalMs,
            sessionId = snapshot.SessionId,
            completeness = snapshot.LastTick?.SessionCompleteness,
            cpuAvailable = snapshot.CpuAvailable,
            gpuAvailable = snapshot.GpuAvailable,
            providers = info?.Providers.Select(p => new { id = p.ProviderId, available = p.IsAvailable, reason = p.UnavailableReason, requiresElevation = p.RequiresElevation }),
            sensors = info?.Sensors.Select(s => new { channel = s.Channel, provider = s.ProviderId, hardware = s.HardwareName, sensor = s.SensorName }),
            monitoringDiagnostics = snapshot.Diagnostics
        };
    }

    // ---------------------------------------------------------------- data management

    public static object CheckIntegrity(SqliteEnergyStore store) => new { result = store.CheckIntegrity(quick: true) };

    public static object Maintenance(SqliteEnergyStore store) => store.RunMaintenance(DateTimeOffset.UtcNow);

    private static DateTimeOffset CutoffFor(ServeRequest request)
    {
        if (request.OlderThanDays is not { } days)
            throw new ArgumentException("olderThanDays is required.");
        if (days is < MinHistoryDeletionDays or > 3650)
            throw new ArgumentException($"olderThanDays must be between {MinHistoryDeletionDays} and 3650.");
        return DateTimeOffset.UtcNow - TimeSpan.FromDays(days);
    }

    public static object PreviewHistoryDeletion(SqliteEnergyStore store, ServeRequest request) =>
        store.PreviewHistoryDeletion(CutoffFor(request));

    /// <summary>
    /// The only destructive request. It needs the age (at least 30 days), and a confirmation that names the
    /// exact number of sessions the user was shown -- a stale or guessed confirmation is refused.
    /// </summary>
    public static object DeleteHistory(SqliteEnergyStore store, ServeRequest request)
    {
        var cutoff = CutoffFor(request);
        var preview = store.PreviewHistoryDeletion(cutoff);
        if (request.ConfirmSessions is not { } confirmed || confirmed != preview.Sessions)
            throw new ArgumentException($"Deleting history needs confirmation of the exact scope ({preview.Sessions} session(s)); nothing was deleted.");
        // An Active session is never eligible (the store excludes it), so the running session is untouched.
        return preview.Sessions == 0 ? preview : store.DeleteHistoryBefore(cutoff);
    }
}
