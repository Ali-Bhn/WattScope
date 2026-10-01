namespace WattScope.Core.Gaming;

/// <summary>
/// The tunable thresholds of game detection. All of them trade responsiveness for
/// resistance to false sessions; the defaults are documented in docs/GAMING.md.
/// </summary>
public sealed record GameDetectorOptions
{
    /// <summary>How often the process list is examined. Not used by the state machine itself (it only sees timestamps); used by the poller.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A user-mapped or catalog game must be present this long before it counts as a gaming session.</summary>
    public TimeSpan ConfirmDelayMapped { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A heuristically recognised game (launcher child, library folder) must be present this long. Longer: it is a guess.</summary>
    public TimeSpan ConfirmDelayHeuristic { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a confirmed game may be absent before its session ends. Bridges a game restarting itself or a crash-and-relaunch.</summary>
    public TimeSpan EndGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
}

public enum DetectorPhase
{
    /// <summary>No game context.</summary>
    NoGame,

    /// <summary>A possible game was seen; it has not been present long enough to count.</summary>
    Candidate,

    /// <summary>A gaming session is running.</summary>
    Confirmed,

    /// <summary>The game process vanished; the session is kept open for the grace period in case it comes back.</summary>
    EndingGrace
}

/// <summary>The game the detector is currently tracking (a candidate, a session, or one in its grace period).</summary>
public sealed record GameContext(GameIdentity Identity, DateTimeOffset FirstSeenUtc, bool IsManual);

public enum DetectorEventKind
{
    CandidateStarted,
    CandidateRejected,
    SessionConfirmed,
    GraceStarted,
    GraceResumed,
    SessionEnded,
    NameChanged
}

/// <param name="At">When the transition was observed.</param>
/// <param name="EndedUtc">For <see cref="DetectorEventKind.SessionEnded"/>: the end of the session. For a process exit this is when the process was first found missing, not when the grace period ran out.</param>
public sealed record DetectorEvent(DetectorEventKind Kind, DateTimeOffset At, GameContext Context, GamingEndReason? EndReason = null, DateTimeOffset? EndedUtc = null);

/// <summary>
/// The explicit game-session state machine (docs/GAMING.md):
/// <code>
/// NoGame --candidate seen--> Candidate --present long enough--> Confirmed
///    ^                          |                                  |  ^
///    |        vanished first    |                       process     |  | process
///    +--------------------------+                       missing     v  | back
///    +----------- grace period elapsed ------------------------ EndingGrace
/// </code>
/// It sees only timestamps and process lists, never a clock or the OS, so it is
/// deterministic and fully testable. It owns exactly one context: while one game
/// is Candidate/Confirmed/Grace every other process is ignored (one authoritative
/// gaming context, never two sessions for one stretch of time).
/// </summary>
/// <remarks>Not thread-safe; the caller serializes access.</remarks>
public sealed class GameDetector
{
    private readonly GameDetectorOptions _options;
    private readonly GameIdentifier _identifier;

    private Dictionary<(int Pid, long StartTicks), (int MappingsVersion, IdentificationResult Result)> _cache = new();

    private GameContext? _context;
    private DateTimeOffset _graceStartedUtc;

    public GameDetector(GameDetectorOptions? options = null, GameIdentifier? identifier = null)
    {
        _options = options ?? new GameDetectorOptions();
        _identifier = identifier ?? new GameIdentifier();
    }

    public GameIdentifier Identifier => _identifier;
    public GameDetectorOptions Options => _options;
    public DetectorPhase Phase { get; private set; } = DetectorPhase.NoGame;
    public GameContext? Context => _context;

    /// <summary>When the process was first found missing, while in <see cref="DetectorPhase.EndingGrace"/>.</summary>
    public DateTimeOffset? GraceStartedUtc => Phase == DetectorPhase.EndingGrace ? _graceStartedUtc : null;

    /// <summary>Examines one process snapshot taken at <paramref name="now"/> and returns the transitions it caused (usually none).</summary>
    public IReadOnlyList<DetectorEvent> Observe(DateTimeOffset now, IReadOnlyList<ProcessInfo> processes)
    {
        var events = new List<DetectorEvent>();

        // A manual session is not decided by processes at all.
        if (_context is { IsManual: true })
            return events;

        var byPid = new Dictionary<int, ProcessInfo>(processes.Count);
        foreach (var process in processes)
            byPid[process.Pid] = process;

        var identified = IdentifyAll(processes, byPid);

        switch (Phase)
        {
            case DetectorPhase.Candidate:
            {
                var context = _context!;
                if (!identified.Any(i => i.Key == context.Identity.Key))
                {
                    Reject(now, events);
                }
                else if (BestCandidate(identified) is { } better && IsStrictlyBetterTier(better, context.Identity))
                {
                    // A user-mapped / catalog game appeared while a heuristic guess was still being confirmed: the guess yields.
                    Reject(now, events);
                    StartCandidate(now, better, events);
                }
                else if (now - context.FirstSeenUtc >= ConfirmDelay(context.Identity))
                {
                    Phase = DetectorPhase.Confirmed;
                    events.Add(new DetectorEvent(DetectorEventKind.SessionConfirmed, now, context));
                }
                break;
            }

            case DetectorPhase.Confirmed:
                if (!identified.Any(i => i.Key == _context!.Identity.Key))
                {
                    Phase = DetectorPhase.EndingGrace;
                    _graceStartedUtc = now;
                    events.Add(new DetectorEvent(DetectorEventKind.GraceStarted, now, _context!));
                    EndIfGraceElapsed(now, events); // a zero-length grace ends immediately
                }
                break;

            case DetectorPhase.EndingGrace:
                if (identified.Any(i => i.Key == _context!.Identity.Key))
                {
                    Phase = DetectorPhase.Confirmed;
                    events.Add(new DetectorEvent(DetectorEventKind.GraceResumed, now, _context!));
                }
                else
                {
                    EndIfGraceElapsed(now, events);
                }
                break;
        }

        // Whenever there is no context (nothing was running, a candidate vanished, or a session just
        // ended) the best remaining candidate starts right away -- never a poll later than necessary.
        if (Phase == DetectorPhase.NoGame)
            TryStartCandidate(now, identified, events);

        return events;
    }

    /// <summary>
    /// Starts a manually requested session. Refused (<see cref="InvalidOperationException"/>)
    /// while a game session is running: there is only ever one authoritative gaming
    /// session. A pending candidate is simply superseded.
    /// </summary>
    public IReadOnlyList<DetectorEvent> StartManual(DateTimeOffset now, string displayName, string? exeName = null)
    {
        if (Phase is DetectorPhase.Confirmed or DetectorPhase.EndingGrace)
            throw new InvalidOperationException("A gaming session is already active. Stop it before starting another.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A game name is required.", nameof(displayName));

        var events = new List<DetectorEvent>();
        if (Phase == DetectorPhase.Candidate)
            Reject(now, events);

        var exe = string.IsNullOrWhiteSpace(exeName) ? null : exeName.Trim();
        var identity = new GameIdentity
        {
            Key = exe is null ? "manual:" + displayName.Trim().ToLowerInvariant() : exe.ToLowerInvariant(),
            ExeName = exe ?? displayName.Trim(),
            DisplayName = displayName.Trim(),
            Source = GameDetectionSource.Manual,
            Tier = DetectionTier.Mapped
        };
        _context = new GameContext(identity, now, IsManual: true);
        Phase = DetectorPhase.Confirmed;
        events.Add(new DetectorEvent(DetectorEventKind.SessionConfirmed, now, _context));
        return events;
    }

    /// <summary>Ends a manual session. A no-op (empty result) when no manual session is running.</summary>
    public IReadOnlyList<DetectorEvent> StopManual(DateTimeOffset now)
    {
        if (_context is not { IsManual: true })
            return [];
        return End(now, GamingEndReason.ManualStop, now);
    }

    /// <summary>Abandons whatever is pending or running (monitoring stopped, host exiting). A confirmed session ends with <paramref name="reason"/>.</summary>
    public IReadOnlyList<DetectorEvent> Reset(DateTimeOffset now, GamingEndReason reason = GamingEndReason.MonitoringStopped)
    {
        var events = new List<DetectorEvent>();
        switch (Phase)
        {
            case DetectorPhase.Candidate:
                Reject(now, events);
                break;
            case DetectorPhase.Confirmed:
                events.AddRange(End(now, reason, now));
                break;
            case DetectorPhase.EndingGrace:
                // The process had already vanished; the session ended when it was first missed.
                events.AddRange(End(now, reason, _graceStartedUtc));
                break;
        }
        return events;
    }

    /// <summary>Drops a pending candidate without touching a running session (the machine slept: presence across the sleep proves nothing).</summary>
    public IReadOnlyList<DetectorEvent> DropCandidate(DateTimeOffset now)
    {
        var events = new List<DetectorEvent>();
        if (Phase == DetectorPhase.Candidate)
            Reject(now, events);
        return events;
    }

    /// <summary>
    /// Applies new user mappings. A running/pending game that is now excluded
    /// ends/disappears at once; a rename of the current game is reported so the
    /// stored session can follow.
    /// </summary>
    public IReadOnlyList<DetectorEvent> ApplyMappings(DateTimeOffset now, IEnumerable<GameMapping> mappings)
    {
        _identifier.SetMappings(mappings);
        _cache.Clear();

        var events = new List<DetectorEvent>();
        if (_context is not { IsManual: false } context)
            return events;

        var mapping = _identifier.FindMapping(context.Identity.ExeName, context.Identity.ExePath);
        if (mapping is { Kind: GameMappingKind.Excluded })
        {
            if (Phase == DetectorPhase.Candidate)
                Reject(now, events);
            else
                events.AddRange(End(now, GamingEndReason.Excluded, Phase == DetectorPhase.EndingGrace ? _graceStartedUtc : now));
        }
        else if (mapping is { Kind: GameMappingKind.Game, DisplayName: { Length: > 0 } name } && name != context.Identity.DisplayName)
        {
            _context = context with { Identity = context.Identity with { DisplayName = name } };
            events.Add(new DetectorEvent(DetectorEventKind.NameChanged, now, _context));
        }
        return events;
    }

    // ------------------------------------------------------------ internals

    private List<GameIdentity> IdentifyAll(IReadOnlyList<ProcessInfo> processes, Dictionary<int, ProcessInfo> byPid)
    {
        var next = new Dictionary<(int, long), (int, IdentificationResult)>(processes.Count);
        var found = new List<GameIdentity>();
        foreach (var process in processes)
        {
            var key = (process.Pid, process.StartUtc?.UtcTicks ?? 0L);
            if (!_cache.TryGetValue(key, out var cached) || cached.MappingsVersion != _identifier.MappingsVersion)
                cached = (_identifier.MappingsVersion, _identifier.Identify(process, byPid));
            next[key] = cached;
            if (cached.Result.Identity is { } identity)
                found.Add(identity);
        }
        _cache = next; // pruned to what is running now: bounded memory
        return found;
    }

    private GameIdentity? BestCandidate(List<GameIdentity> identified) =>
        identified
            .OrderBy(i => (int)i.Source)
            .ThenBy(i => i.ExePath is null ? 1 : 0)
            .ThenBy(i => i.Key, StringComparer.Ordinal)
            .ThenBy(i => i.ExePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static bool IsStrictlyBetterTier(GameIdentity candidate, GameIdentity current) =>
        candidate.Tier == DetectionTier.Mapped && current.Tier == DetectionTier.Heuristic && candidate.Key != current.Key;

    private TimeSpan ConfirmDelay(GameIdentity identity) =>
        identity.Tier == DetectionTier.Mapped ? _options.ConfirmDelayMapped : _options.ConfirmDelayHeuristic;

    private void TryStartCandidate(DateTimeOffset now, List<GameIdentity> identified, List<DetectorEvent> events)
    {
        if (BestCandidate(identified) is { } best)
            StartCandidate(now, best, events);
    }

    private void StartCandidate(DateTimeOffset now, GameIdentity identity, List<DetectorEvent> events)
    {
        _context = new GameContext(identity, now, IsManual: false);
        Phase = DetectorPhase.Candidate;
        events.Add(new DetectorEvent(DetectorEventKind.CandidateStarted, now, _context));
        // A zero confirm delay (tests, aggressive configs) confirms on first sight.
        if (ConfirmDelay(identity) <= TimeSpan.Zero)
        {
            Phase = DetectorPhase.Confirmed;
            events.Add(new DetectorEvent(DetectorEventKind.SessionConfirmed, now, _context));
        }
    }

    private void Reject(DateTimeOffset now, List<DetectorEvent> events)
    {
        if (_context is not { } context)
            return;
        _context = null;
        Phase = DetectorPhase.NoGame;
        events.Add(new DetectorEvent(DetectorEventKind.CandidateRejected, now, context));
    }

    private void EndIfGraceElapsed(DateTimeOffset now, List<DetectorEvent> events)
    {
        if (now - _graceStartedUtc >= _options.EndGracePeriod)
            events.AddRange(End(now, GamingEndReason.ProcessExited, _graceStartedUtc));
    }

    private IReadOnlyList<DetectorEvent> End(DateTimeOffset now, GamingEndReason reason, DateTimeOffset endedUtc)
    {
        if (_context is not { } context)
            return [];
        _context = null;
        Phase = DetectorPhase.NoGame;
        return [new DetectorEvent(DetectorEventKind.SessionEnded, now, context, reason, endedUtc)];
    }
}
