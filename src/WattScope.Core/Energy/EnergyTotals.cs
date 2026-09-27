using WattScope.Core.Estimation;

namespace WattScope.Core.Energy;

/// <summary>
/// Additive energy totals for some span of time (an interval, a bucket, a
/// session, a day). Joules are kept as unrounded doubles; "Seconds" fields are
/// coverage -- how much of <see cref="MonitoredSeconds"/> each quantity was
/// actually observed for. Measured and estimated quantities are kept in
/// separate fields and are never mixed.
/// </summary>
public readonly record struct EnergyTotals
{
    /// <summary>Time covered by verified intervals (excludes gaps).</summary>
    public double MonitoredSeconds { get; init; }

    public double CpuPackageJoules { get; init; }
    public double CpuPackageSeconds { get; init; }

    public double GpuBoardJoules { get; init; }
    public double GpuBoardSeconds { get; init; }

    /// <summary>Estimated wall energy over intervals where every included input was measured.</summary>
    public double EstimatedCompleteJoules { get; init; }
    public double EstimatedCompleteSeconds { get; init; }

    /// <summary>Lower-bound estimated wall energy over intervals where some (not all) included inputs were measured.</summary>
    public double EstimatedPartialJoules { get; init; }
    public double EstimatedPartialSeconds { get; init; }

    public static EnergyTotals FromInterval(PowerInterval interval, EstimationSettings settings)
    {
        var s = interval.Seconds;
        double? cpu = interval.CpuPackage?.Joules(s);
        double? gpu = interval.GpuBoard?.Joules(s);
        var estimate = WallPowerEstimator.EstimateEnergy(cpu, gpu, s, settings);

        return new EnergyTotals
        {
            MonitoredSeconds = s,
            CpuPackageJoules = cpu ?? 0,
            CpuPackageSeconds = cpu is null ? 0 : s,
            GpuBoardJoules = gpu ?? 0,
            GpuBoardSeconds = gpu is null ? 0 : s,
            EstimatedCompleteJoules = estimate.Completeness == EstimateCompleteness.Complete ? estimate.Value!.Value : 0,
            EstimatedCompleteSeconds = estimate.Completeness == EstimateCompleteness.Complete ? s : 0,
            EstimatedPartialJoules = estimate.Completeness == EstimateCompleteness.Partial ? estimate.Value!.Value : 0,
            EstimatedPartialSeconds = estimate.Completeness == EstimateCompleteness.Partial ? s : 0
        };
    }

    public static EnergyTotals operator +(EnergyTotals a, EnergyTotals b) => new()
    {
        MonitoredSeconds = a.MonitoredSeconds + b.MonitoredSeconds,
        CpuPackageJoules = a.CpuPackageJoules + b.CpuPackageJoules,
        CpuPackageSeconds = a.CpuPackageSeconds + b.CpuPackageSeconds,
        GpuBoardJoules = a.GpuBoardJoules + b.GpuBoardJoules,
        GpuBoardSeconds = a.GpuBoardSeconds + b.GpuBoardSeconds,
        EstimatedCompleteJoules = a.EstimatedCompleteJoules + b.EstimatedCompleteJoules,
        EstimatedCompleteSeconds = a.EstimatedCompleteSeconds + b.EstimatedCompleteSeconds,
        EstimatedPartialJoules = a.EstimatedPartialJoules + b.EstimatedPartialJoules,
        EstimatedPartialSeconds = a.EstimatedPartialSeconds + b.EstimatedPartialSeconds
    };

    public bool IsEmpty => MonitoredSeconds == 0;
}

/// <summary>Energy totals for one session within one fixed-size UTC time bucket.</summary>
public sealed record EnergyBucket
{
    public required long SessionId { get; init; }
    public required DateTimeOffset BucketStartUtc { get; init; }
    public required EnergyTotals Totals { get; init; }
}
