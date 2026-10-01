using WattScope.Core.Energy;
using WattScope.Core.Estimation;

namespace WattScope.Probe.Serve;

/// <summary>
/// The one thing sent to the desktop UI every sample: only what the
/// dashboard/chart need (Part E: "transfer only the information required by
/// the dashboard", never the full discovered sensor set). Never a
/// substituted zero -- <see cref="CpuWatts"/>/<see cref="GpuWatts"/> are null
/// exactly when <see cref="CpuState"/>/<see cref="GpuState"/> say why.
/// </summary>
public sealed record TickMessage
{
    public string Type => "tick";
    public required long SessionId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset Utc { get; init; }

    public required double? CpuWatts { get; init; }
    public required ReadingState CpuState { get; init; }
    public required double? GpuWatts { get; init; }
    public required ReadingState GpuState { get; init; }

    /// <summary>The assumed constant draw of everything not measured (a user setting, not a reading) -- shown separately so the UI never conflates it with the measured channels.</summary>
    public required double OtherComponentsWatts { get; init; }

    /// <summary>ESTIMATED, never a measurement of wall power -- null when no included input is currently valid.</summary>
    public required double? EstimatedTotalWatts { get; init; }
    public required EstimateCompleteness EstimateCompleteness { get; init; }

    /// <summary>Completeness of the session's estimate so far (not just this sample): only a Complete session has a cost shown.</summary>
    public required EstimateCompleteness SessionCompleteness { get; init; }

    public required double SessionSeconds { get; init; }
    /// <summary>Complete estimated energy for the session so far, in kWh.</summary>
    public required double SessionKwh { get; init; }
    /// <summary>
    /// A live, approximate running cost at whatever price is in effect right
    /// now -- for display only. The exact, reconciled cost (which correctly
    /// splits any price change mid-session) comes from a "get_summary"
    /// request once the relevant bucket is flushed; see docs/ENERGY_MODEL.md
    /// section 4.
    /// </summary>
    public decimal? SessionCostApprox { get; init; }
    public string? Currency { get; init; }

    public required bool Elevated { get; init; }
}
