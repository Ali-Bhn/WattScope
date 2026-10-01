using WattScope.Core.Energy;
using WattScope.Core.Estimation;
using WattScope.Core.Pricing;
using WattScope.Core.Reporting;

namespace WattScope.Core.Gaming;

/// <summary>Why a gaming session has (or has not) a cost.</summary>
public enum GamingCostStatus
{
    /// <summary>The estimate is Complete and every joule was priced: the cost is real.</summary>
    Priced,

    /// <summary>The estimate is Partial: only a lower bound exists, so no complete cost is shown (a separate lower-bound cost may be).</summary>
    WithheldPartial,

    /// <summary>The estimate is Complete but no price covered (all of) it.</summary>
    WithheldUnpriced,

    /// <summary>No estimate at all (no included sensor was measured).</summary>
    NoEstimate
}

/// <summary>Everything the UI shows about one gaming session, derived from the stored record -- nothing here is stored twice.</summary>
public sealed record GamingSessionSummary
{
    public required long Id { get; init; }
    public required long MonitoringSessionId { get; init; }
    public required string GameKey { get; init; }
    public required string ExeName { get; init; }
    public required string DisplayName { get; init; }
    public required GameDetectionSource Source { get; init; }
    public required bool IsManual { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public required GamingSessionStatus Status { get; init; }
    public GamingEndReason? EndReason { get; init; }

    /// <summary>Wall-clock length of the session (until its end, or until now while it is running).</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>The part of the session that was actually monitored (excludes gaps such as sleep). Energy covers only this.</summary>
    public required double MonitoredSeconds { get; init; }

    public required EnergyTotals Totals { get; init; }

    /// <summary>Complete only if the complete estimate covers all monitored time; None if there is no estimate at all.</summary>
    public required EstimateCompleteness Completeness { get; init; }

    public double? AvgCpuWatts { get; init; }
    public double? AvgGpuWatts { get; init; }

    /// <summary>Estimated, never measured wall power. A lower bound when <see cref="Completeness"/> is not Complete.</summary>
    public double? AvgSystemWatts { get; init; }

    public double? PeakCpuWatts { get; init; }
    public double? PeakGpuWatts { get; init; }
    public double? PeakSystemWatts { get; init; }

    /// <summary>Estimated system energy: the complete estimate when Complete, otherwise the lower bound (complete + partial intervals).</summary>
    public required double SystemJoules { get; init; }

    /// <summary>True whenever <see cref="Completeness"/> is not Complete: system average/peak/energy are lower bounds, not estimates of the whole session.</summary>
    public required bool SystemIsLowerBound { get; init; }

    public required GamingCostStatus CostStatus { get; init; }

    /// <summary>Unrounded cost of the complete estimate. Set only when <see cref="CostStatus"/> is Priced.</summary>
    public decimal? Cost { get; init; }

    /// <summary>Unrounded cost of the lower-bound estimate when Partial; shown separately, never as the session's cost.</summary>
    public decimal? LowerBoundCost { get; init; }

    public string? Currency { get; init; }
    public PriceAllocationPrecision CostPrecision { get; init; }
}

/// <summary>Totals over some sessions of one game that all share a completeness class (never mixed across classes).</summary>
public sealed record GameAggregateGroup
{
    public int Sessions { get; init; }
    public double MonitoredSeconds { get; init; }

    /// <summary>Complete group: complete estimated energy. Partial group: lower-bound energy.</summary>
    public double SystemJoules { get; init; }

    /// <summary>Energy-weighted: total joules / total covered seconds -- never an average of per-session averages.</summary>
    public double? AvgSystemWatts { get; init; }

    /// <summary>Complete group only: unrounded sum of the sessions that were fully priced.</summary>
    public decimal? Cost { get; init; }

    /// <summary>Complete group only: sessions whose estimate is complete but could not be fully priced (not included in <see cref="Cost"/>).</summary>
    public int UnpricedSessions { get; init; }
}

/// <summary>Per-game totals recorded by WattScope. Complete and Partial sessions are kept in separate groups on purpose.</summary>
public sealed record GameAggregate
{
    public required string GameKey { get; init; }
    public required string DisplayName { get; init; }
    public required int SessionCount { get; init; }
    public required double TotalDurationSeconds { get; init; }
    public required double TotalMonitoredSeconds { get; init; }
    public required double AverageSessionDurationSeconds { get; init; }

    /// <summary>Total measured CPU energy / total seconds CPU power was valid, over every session. Null if the CPU was never measured.</summary>
    public double? AvgCpuWatts { get; init; }
    public double? AvgGpuWatts { get; init; }
    public double? PeakCpuWatts { get; init; }
    public double? PeakGpuWatts { get; init; }

    public required GameAggregateGroup Complete { get; init; }
    public required GameAggregateGroup Partial { get; init; }

    /// <summary>Sessions with no estimate at all.</summary>
    public required int NoEstimateSessions { get; init; }
    public string? Currency { get; init; }
}

public static class GamingAnalytics
{
    /// <summary>The effective end of a session for duration purposes: its end, else its last persisted observation (running/orphaned), else <paramref name="now"/>.</summary>
    public static DateTimeOffset EffectiveEnd(GamingSessionRecord record, DateTimeOffset now) =>
        record.EndedUtc ?? (record.Status == GamingSessionStatus.Active ? now : record.LastObservationUtc ?? record.StartedUtc);

    public static EnergyTotals TotalsOf(IEnumerable<GamingEnergyRow> rows)
    {
        var totals = new EnergyTotals();
        foreach (var row in rows)
            totals += row.Totals;
        return totals;
    }

    public static GamingSessionSummary Summarize(GamingSessionRecord record, ElectricityPriceSchedule prices, DateTimeOffset now)
    {
        var totals = TotalsOf(record.Energy);
        var completeness = EnergyReportBuilder.CompletenessOf(totals);
        var end = EffectiveEnd(record, now);
        var cost = ComputeCost(record, prices, end);

        var lowerBoundSeconds = totals.EstimatedCompleteSeconds + totals.EstimatedPartialSeconds;
        var lowerBoundJoules = totals.EstimatedCompleteJoules + totals.EstimatedPartialJoules;
        var systemJoules = completeness == EstimateCompleteness.Complete ? totals.EstimatedCompleteJoules : lowerBoundJoules;
        double? avgSystem = completeness switch
        {
            EstimateCompleteness.Complete => totals.EstimatedCompleteJoules / totals.EstimatedCompleteSeconds,
            EstimateCompleteness.Partial when lowerBoundSeconds > 0 => lowerBoundJoules / lowerBoundSeconds,
            _ => null
        };

        double? peakSystem = null;
        if (record.Peaks.SystemCompleteWatts is { } c) peakSystem = c;
        if (record.Peaks.SystemPartialWatts is { } p) peakSystem = peakSystem is null ? p : Math.Max(peakSystem.Value, p);

        var status = completeness switch
        {
            EstimateCompleteness.None => GamingCostStatus.NoEstimate,
            EstimateCompleteness.Partial => GamingCostStatus.WithheldPartial,
            _ => cost.EstimatedCost is not null && cost.UnpricedJoules <= 0 ? GamingCostStatus.Priced : GamingCostStatus.WithheldUnpriced
        };

        return new GamingSessionSummary
        {
            Id = record.Id,
            MonitoringSessionId = record.MonitoringSessionId,
            GameKey = record.GameKey,
            ExeName = record.ExeName,
            DisplayName = record.DisplayName,
            Source = record.Source,
            IsManual = record.IsManual,
            StartedUtc = record.StartedUtc,
            EndedUtc = record.EndedUtc,
            Status = record.Status,
            EndReason = record.EndReason,
            DurationSeconds = Math.Max(0, (end - record.StartedUtc).TotalSeconds),
            MonitoredSeconds = totals.MonitoredSeconds,
            Totals = totals,
            Completeness = completeness,
            AvgCpuWatts = totals.CpuPackageSeconds > 0 ? totals.CpuPackageJoules / totals.CpuPackageSeconds : null,
            AvgGpuWatts = totals.GpuBoardSeconds > 0 ? totals.GpuBoardJoules / totals.GpuBoardSeconds : null,
            AvgSystemWatts = avgSystem,
            PeakCpuWatts = record.Peaks.CpuWatts,
            PeakGpuWatts = record.Peaks.GpuWatts,
            PeakSystemWatts = peakSystem,
            SystemJoules = systemJoules,
            SystemIsLowerBound = completeness != EstimateCompleteness.Complete,
            CostStatus = status,
            Cost = status == GamingCostStatus.Priced ? cost.EstimatedCost : null,
            LowerBoundCost = completeness == EstimateCompleteness.Partial ? SumNullable(cost.EstimatedCost, cost.PartialEstimateCost) : null,
            Currency = prices.Currency,
            CostPrecision = cost.Precision
        };
    }

    /// <summary>
    /// Prices a session's per-price energy rows. Exact when every row still
    /// sits inside the price period it was recorded under. If a price was
    /// added retroactively inside the session (a row now straddles a boundary
    /// it was never split at), the whole session's energy is re-split
    /// proportionally by wall-clock time and flagged
    /// <see cref="PriceAllocationPrecision.ApproximateProportional"/> -- never
    /// presented as exact (same philosophy as docs/ENERGY_MODEL.md section 4).
    /// </summary>
    public static CostBreakdown ComputeCost(GamingSessionRecord record, ElectricityPriceSchedule prices, DateTimeOffset endUtc)
    {
        var pieces = new List<(EnergyTotals Totals, ElectricityPrice? Price)>();
        var precision = PriceAllocationPrecision.Exact;

        if (record.Energy.All(row => RowStillValid(row, prices)))
        {
            foreach (var row in record.Energy)
                pieces.Add((row.Totals, row.PriceEffectiveUtc is { } from ? prices.Entries.FirstOrDefault(e => e.EffectiveFromUtc == from) : null));
        }
        else
        {
            precision = PriceAllocationPrecision.ApproximateProportional;
            var total = TotalsOf(record.Energy);
            var edges = new List<DateTimeOffset> { record.StartedUtc };
            edges.AddRange(prices.Entries.Select(e => e.EffectiveFromUtc).Where(t => t > record.StartedUtc && t < endUtc).OrderBy(t => t));
            edges.Add(endUtc);
            var span = (endUtc - record.StartedUtc).TotalSeconds;
            for (var i = 0; i < edges.Count - 1 && span > 0; i++)
                pieces.Add((Scale(total, (edges[i + 1] - edges[i]).TotalSeconds / span), prices.PriceAt(edges[i])));
        }

        decimal? estimated = null, partial = null;
        double priced = 0, unpriced = 0;
        var applied = new HashSet<ElectricityPrice>();
        foreach (var (totals, price) in pieces)
        {
            if (price is null)
            {
                unpriced += totals.EstimatedCompleteJoules;
                continue;
            }
            applied.Add(price);
            if (totals.EstimatedCompleteSeconds > 0)
            {
                estimated = (estimated ?? 0m) + CostCalculator.Cost(totals.EstimatedCompleteJoules, price.PricePerKwh);
                priced += totals.EstimatedCompleteJoules;
            }
            if (totals.EstimatedPartialSeconds > 0)
                partial = (partial ?? 0m) + CostCalculator.Cost(totals.EstimatedPartialJoules, price.PricePerKwh);
        }

        return new CostBreakdown
        {
            Currency = prices.Currency,
            EstimatedCost = estimated,
            PricedJoules = priced,
            UnpricedJoules = unpriced,
            PartialEstimateCost = partial,
            PricesApplied = applied.OrderBy(p => p.EffectiveFromUtc).ToList(),
            Precision = precision
        };
    }

    /// <summary>A row is trustworthy if no price boundary in the current schedule falls strictly inside the time its energy covers, and the price it was keyed to is still the one in effect.</summary>
    private static bool RowStillValid(GamingEnergyRow row, ElectricityPriceSchedule prices)
    {
        if (row.Totals.MonitoredSeconds <= 0)
            return true;
        if (prices.PriceAt(row.FirstUtc)?.EffectiveFromUtc != row.PriceEffectiveUtc)
            return false;
        return !prices.Entries.Any(e => e.EffectiveFromUtc > row.FirstUtc && e.EffectiveFromUtc < row.LastUtc);
    }

    private static EnergyTotals Scale(EnergyTotals t, double f) => new()
    {
        MonitoredSeconds = t.MonitoredSeconds * f,
        CpuPackageJoules = t.CpuPackageJoules * f,
        CpuPackageSeconds = t.CpuPackageSeconds * f,
        GpuBoardJoules = t.GpuBoardJoules * f,
        GpuBoardSeconds = t.GpuBoardSeconds * f,
        EstimatedCompleteJoules = t.EstimatedCompleteJoules * f,
        EstimatedCompleteSeconds = t.EstimatedCompleteSeconds * f,
        EstimatedPartialJoules = t.EstimatedPartialJoules * f,
        EstimatedPartialSeconds = t.EstimatedPartialSeconds * f
    };

    private static decimal? SumNullable(decimal? a, decimal? b) => a is null ? b : b is null ? a : a + b;

    /// <summary>
    /// Per-game totals over <em>finished</em> sessions (a running session has no end yet). Mathematically careful:
    /// averages are energy-weighted (total joules / total seconds), Complete and Partial sessions are never
    /// merged into one energy or cost figure, and a cost is only summed over fully priced Complete sessions.
    /// </summary>
    public static IReadOnlyList<GameAggregate> AggregateByGame(IEnumerable<GamingSessionSummary> sessions, string? currency = null)
    {
        return sessions
            .Where(s => s.Status != GamingSessionStatus.Active)
            .GroupBy(s => s.GameKey)
            .Select(group =>
            {
                var list = group.OrderBy(s => s.StartedUtc).ToList();
                var cpuJ = list.Sum(s => s.Totals.CpuPackageJoules);
                var cpuS = list.Sum(s => s.Totals.CpuPackageSeconds);
                var gpuJ = list.Sum(s => s.Totals.GpuBoardJoules);
                var gpuS = list.Sum(s => s.Totals.GpuBoardSeconds);
                var totalDuration = list.Sum(s => s.DurationSeconds);

                return new GameAggregate
                {
                    GameKey = group.Key,
                    DisplayName = list[^1].DisplayName, // the most recent name wins (a rename applies going forward)
                    SessionCount = list.Count,
                    TotalDurationSeconds = totalDuration,
                    TotalMonitoredSeconds = list.Sum(s => s.MonitoredSeconds),
                    AverageSessionDurationSeconds = totalDuration / list.Count,
                    AvgCpuWatts = cpuS > 0 ? cpuJ / cpuS : null,
                    AvgGpuWatts = gpuS > 0 ? gpuJ / gpuS : null,
                    PeakCpuWatts = MaxOf(list.Select(s => s.PeakCpuWatts)),
                    PeakGpuWatts = MaxOf(list.Select(s => s.PeakGpuWatts)),
                    Complete = CompleteGroup(list.Where(s => s.Completeness == EstimateCompleteness.Complete).ToList()),
                    Partial = PartialGroup(list.Where(s => s.Completeness == EstimateCompleteness.Partial).ToList()),
                    NoEstimateSessions = list.Count(s => s.Completeness == EstimateCompleteness.None),
                    Currency = currency
                };
            })
            .OrderByDescending(a => a.TotalMonitoredSeconds)
            .ThenBy(a => a.GameKey, StringComparer.Ordinal)
            .ToList();
    }

    private static GameAggregateGroup CompleteGroup(List<GamingSessionSummary> sessions)
    {
        var joules = sessions.Sum(s => s.Totals.EstimatedCompleteJoules);
        var seconds = sessions.Sum(s => s.Totals.EstimatedCompleteSeconds);
        var priced = sessions.Where(s => s.CostStatus == GamingCostStatus.Priced).ToList();
        return new GameAggregateGroup
        {
            Sessions = sessions.Count,
            MonitoredSeconds = sessions.Sum(s => s.MonitoredSeconds),
            SystemJoules = joules,
            AvgSystemWatts = seconds > 0 ? joules / seconds : null,
            Cost = priced.Count > 0 ? priced.Sum(s => s.Cost!.Value) : null,
            UnpricedSessions = sessions.Count - priced.Count
        };
    }

    private static GameAggregateGroup PartialGroup(List<GamingSessionSummary> sessions)
    {
        var joules = sessions.Sum(s => s.Totals.EstimatedCompleteJoules + s.Totals.EstimatedPartialJoules);
        var seconds = sessions.Sum(s => s.Totals.EstimatedCompleteSeconds + s.Totals.EstimatedPartialSeconds);
        return new GameAggregateGroup
        {
            Sessions = sessions.Count,
            MonitoredSeconds = sessions.Sum(s => s.MonitoredSeconds),
            SystemJoules = joules,
            AvgSystemWatts = seconds > 0 ? joules / seconds : null
        };
    }

    private static double? MaxOf(IEnumerable<double?> values)
    {
        double? max = null;
        foreach (var v in values)
            if (v is { } d && (max is null || d > max))
                max = d;
        return max;
    }
}
