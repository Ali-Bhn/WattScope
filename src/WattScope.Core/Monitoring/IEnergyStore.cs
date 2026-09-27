using WattScope.Core.Energy;
using WattScope.Core.Estimation;

namespace WattScope.Core.Monitoring;

/// <summary>Persisted as a small integer; never renumber.</summary>
public enum SessionStatus : byte
{
    /// <summary>Being recorded -- or, if no process holds the monitor lock, orphaned by a crash and awaiting recovery.</summary>
    Active = 0,

    /// <summary>Ended normally (user stop, duration elapsed).</summary>
    Stopped = 1,

    /// <summary>The recording process ended without stopping the session; recovered at the next start.</summary>
    Interrupted = 2
}

public sealed record MonitoringSession
{
    public required long Id { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }

    /// <summary>UTC time of the last persisted sample. Energy is never extended beyond it.</summary>
    public DateTimeOffset? LastObservationUtc { get; init; }

    public required SessionStatus Status { get; init; }
    public string? EndReason { get; init; }

    /// <summary>The estimation assumptions snapshotted when the session was created; used for its whole lifetime.</summary>
    public required EstimationSettings Estimation { get; init; }

    public required IReadOnlyList<EnergySensorBinding> Sensors { get; init; }
}

public sealed record NewSessionRequest
{
    public required DateTimeOffset StartedUtc { get; init; }
    public required EstimationSettings Estimation { get; init; }
    public required IReadOnlyList<EnergySensorBinding> Sensors { get; init; }
    public bool? ProcessElevated { get; init; }
    public string? AppVersion { get; init; }
}

/// <summary>A channel's <see cref="ReadingState"/> changed at <see cref="AtUtc"/> (run-length encoded availability).</summary>
public sealed record AvailabilityChange(EnergyChannel Channel, ReadingState State, DateTimeOffset AtUtc);

/// <summary>Everything produced since the last flush. Must be persisted atomically (all or nothing).</summary>
public sealed record EnergyWriteBatch
{
    public required long SessionId { get; init; }
    public required IReadOnlyList<PowerSample> Samples { get; init; }

    /// <summary>Energy to <em>add</em> to each bucket (deltas, not absolute values).</summary>
    public required IReadOnlyList<EnergyBucket> BucketDeltas { get; init; }

    public required IReadOnlyList<MonitoringGap> Gaps { get; init; }
    public required IReadOnlyList<AvailabilityChange> AvailabilityChanges { get; init; }
    public DateTimeOffset? LastObservationUtc { get; init; }

    public bool IsEmpty => Samples.Count == 0 && BucketDeltas.Count == 0 && Gaps.Count == 0 && AvailabilityChanges.Count == 0;
}

/// <summary>Persistence needed by <see cref="EnergyMonitor"/>. Implemented by WattScope.Storage (SQLite).</summary>
public interface IEnergyStore
{
    MonitoringSession CreateSession(NewSessionRequest request);

    MonitoringSession? GetSession(long sessionId);

    /// <summary>Marks a Stopped/Interrupted session Active again and adds any new sensor bindings. Returns the session as it was <em>before</em> reopening.</summary>
    MonitoringSession ReopenSession(long sessionId, IReadOnlyList<EnergySensorBinding> sensors);

    /// <summary>Persists the batch in a single transaction.</summary>
    void WriteBatch(EnergyWriteBatch batch);

    void EndSession(long sessionId, DateTimeOffset endedUtc, SessionStatus status, string reason);
}
