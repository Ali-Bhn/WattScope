using WattScope.Core.Energy;

namespace WattScope.Core.Gaming;

/// <summary>Persisted as a small integer; never renumber.</summary>
public enum GamingSessionStatus : byte
{
    /// <summary>Being recorded -- or, if no process holds the monitor lock, orphaned by a crash and awaiting recovery.</summary>
    Active = 0,

    /// <summary>Ended in an orderly way (see <see cref="GamingEndReason"/>).</summary>
    Ended = 1,

    /// <summary>The recording process died while it was active; recovered later and ended at its last persisted observation.</summary>
    Interrupted = 2
}

/// <summary>Why a gaming session ended. Persisted as a small integer; never renumber.</summary>
public enum GamingEndReason : byte
{
    /// <summary>The game's process was gone for longer than the end grace period.</summary>
    ProcessExited = 0,

    /// <summary>The user stopped a manually started session.</summary>
    ManualStop = 1,

    /// <summary>Monitoring was stopped (or WattScope exited) while the game was running.</summary>
    MonitoringStopped = 2,

    /// <summary>Recovered after the recording process died.</summary>
    Interrupted = 3,

    /// <summary>The user excluded the executable while its session was running.</summary>
    Excluded = 4
}

/// <summary>How a game was recognised. Persisted as a small integer; never renumber. Ordered strongest-first for tie-breaking.</summary>
public enum GameDetectionSource : byte
{
    /// <summary>The user marked the executable as a game.</summary>
    UserMapping = 0,

    /// <summary>The executable name is in the built-in catalog of known games.</summary>
    KnownCatalog = 1,

    /// <summary>A non-helper executable started by a known game launcher (Steam, Battle.net, ...), outside the launcher's own folder.</summary>
    LauncherChild = 2,

    /// <summary>The executable lives in a game-library folder (steamapps\common, Epic Games, ...).</summary>
    LibraryPath = 3,

    /// <summary>The user started the session by hand; no process was involved in the decision.</summary>
    Manual = 4
}

/// <summary>Persisted as a small integer; never renumber.</summary>
public enum GameMappingKind : byte
{
    Game = 1,
    Excluded = 2
}

/// <summary>
/// A user decision about one executable. <see cref="ExePath"/> empty means
/// "this executable name, wherever it is"; a full path is more specific and wins.
/// </summary>
public sealed record GameMapping
{
    public long Id { get; init; }

    /// <summary>File name only, e.g. <c>cod.exe</c>. Compared case-insensitively.</summary>
    public required string ExeName { get; init; }

    /// <summary>Full executable path, or empty for any location. Compared case-insensitively.</summary>
    public string ExePath { get; init; } = "";

    public required GameMappingKind Kind { get; init; }

    /// <summary>The name the user wants shown; null/empty keeps the detected one.</summary>
    public string? DisplayName { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>One running process, as seen by the passive OS enumeration. Nothing here comes from inside the process.</summary>
/// <param name="ExePath">Full image path when the OS would tell us (a minimal query-only right); null otherwise (protected processes).</param>
/// <param name="StartUtc">Process creation time when known; used to reject a reused parent PID.</param>
public sealed record ProcessInfo(int Pid, int ParentPid, string ExeName, string? ExePath, DateTimeOffset? StartUtc);

/// <summary>Where processes come from. Production: the Windows toolhelp snapshot. Tests: scripted lists.</summary>
public interface IProcessSource
{
    IReadOnlyList<ProcessInfo> Snapshot();
}

/// <summary>Reads a human-friendly product name from an executable's version resource (cached by the implementation).</summary>
public interface IExecutableMetadata
{
    string? GetProductName(string exePath);
}

public enum DetectionTier
{
    /// <summary>User mapping or known catalog: recognised by explicit knowledge. Confirmed quickly.</summary>
    Mapped,

    /// <summary>Recognised by a conservative heuristic. Needs longer sustained presence.</summary>
    Heuristic
}

/// <summary>Who or what a game is. <see cref="Key"/> (the lower-cased executable name) is the identity of a game across sessions.</summary>
public sealed record GameIdentity
{
    public required string Key { get; init; }
    public required string ExeName { get; init; }
    public string? ExePath { get; init; }
    public required string DisplayName { get; init; }
    public required GameDetectionSource Source { get; init; }
    public required DetectionTier Tier { get; init; }
}

public enum RejectionReason
{
    None,
    UserExcluded,
    NeverGame,
    HelperName,
    SystemPath,
    LauncherOwnFolder,
    NoSignal
}

/// <summary>The identifier's verdict on one process. <see cref="Identity"/> is set only when <see cref="Reason"/> is <see cref="RejectionReason.None"/>.</summary>
public sealed record IdentificationResult(GameIdentity? Identity, RejectionReason Reason)
{
    public bool IsGame => Identity is not null;
}

/// <summary>Peak power seen while a game session was running. Estimated-system peaks are split by completeness so a partial peak is never read as a complete one.</summary>
public sealed record GamingPeaks
{
    public double? CpuWatts { get; init; }
    public double? GpuWatts { get; init; }
    public double? SystemCompleteWatts { get; init; }

    /// <summary>A lower bound: at least one included input was missing for the sample that produced it.</summary>
    public double? SystemPartialWatts { get; init; }
}

/// <summary>
/// Energy of one gaming session attributed to one electricity price (or to none,
/// when it predates the first price). <see cref="FirstUtc"/>/<see cref="LastUtc"/>
/// bound the time of the energy in the row; they let a later reader notice that
/// a price was added retroactively inside the session (the row would then
/// straddle a price boundary it was never split at).
/// </summary>
public sealed record GamingEnergyRow(DateTimeOffset? PriceEffectiveUtc, EnergyTotals Totals, DateTimeOffset FirstUtc, DateTimeOffset LastUtc);

public sealed record NewGamingSession
{
    public required long MonitoringSessionId { get; init; }
    public required GameIdentity Game { get; init; }
    public required bool IsManual { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
}

/// <summary>A gaming session as stored: identity, time span, status, peaks and per-price energy rows. Cost and averages are derived from it (<see cref="GamingAnalytics"/>).</summary>
public sealed record GamingSessionRecord
{
    public required long Id { get; init; }
    public required long MonitoringSessionId { get; init; }
    public required string GameKey { get; init; }
    public required string ExeName { get; init; }
    public string? ExePath { get; init; }
    public required string DisplayName { get; init; }
    public required GameDetectionSource Source { get; init; }
    public required bool IsManual { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public DateTimeOffset? LastObservationUtc { get; init; }
    public required GamingSessionStatus Status { get; init; }
    public GamingEndReason? EndReason { get; init; }
    public required GamingPeaks Peaks { get; init; }
    public required IReadOnlyList<GamingEnergyRow> Energy { get; init; }
}

/// <summary>Raised when a second Active gaming session is created; the database allows at most one.</summary>
public sealed class GamingSessionExistsException(Exception? inner = null)
    : InvalidOperationException("A gaming session is already active. At most one gaming session may be active at a time.", inner);

/// <summary>What the tracker needs from persistence. Implemented by WattScope.Storage; faked in tests.</summary>
public interface IGamingStore
{
    /// <exception cref="GamingSessionExistsException">Another gaming session is already active.</exception>
    long CreateGamingSession(NewGamingSession session);

    /// <summary>Writes the session's current totals (absolute, not deltas) and peaks in one transaction.</summary>
    void SaveGamingProgress(long gamingSessionId, DateTimeOffset lastObservationUtc, GamingPeaks peaks, IReadOnlyList<GamingEnergyRow> energy);

    void EndGamingSession(long gamingSessionId, DateTimeOffset endedUtc, GamingSessionStatus status, GamingEndReason reason);

    void RenameGamingSession(long gamingSessionId, string displayName);
}
