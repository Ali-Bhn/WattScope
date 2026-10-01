using WattScope.Core.Gaming;
using WattScope.Storage;

namespace WattScope.Probe.Serve;

/// <summary>
/// The gaming half of the --serve request protocol. Reads go through the query
/// store (its own connection); everything that changes the detector's
/// behaviour (mappings, manual sessions) goes through the tracker, which owns
/// the live state. Inputs are typed and validated; nothing named by a request
/// is ever executed.
/// </summary>
internal static class GamingHandlers
{
    /// <summary>Most executables returned to the "mark a running program as a game" picker.</summary>
    private const int MaxRunningExecutables = 200;

    public static object State(GamingService? gaming, MonitoringController controller) => new
    {
        available = gaming is not null,
        monitoring = controller.IsRunning,
        live = gaming?.Tracker.Live(),
        pollIntervalSeconds = gaming?.Options.PollInterval.TotalSeconds
    };

    public static object Sessions(SqliteEnergyStore store, ServeRequest request)
    {
        var prices = store.GetPriceSchedule();
        var now = DateTimeOffset.UtcNow;
        return store.ListGamingSessions(request.Limit ?? 50, request.GameKey)
            .Select(record => GamingAnalytics.Summarize(record, prices, now))
            .ToList();
    }

    public static object Session(SqliteEnergyStore store, ServeRequest request)
    {
        if (request.GamingSessionId is not { } id)
            throw new ArgumentException("gamingSessionId is required.");
        var record = store.GetGamingSession(id) ?? throw new ArgumentException($"Gaming session {id} does not exist.");
        var prices = store.GetPriceSchedule();
        var now = DateTimeOffset.UtcNow;
        var summary = GamingAnalytics.Summarize(record, prices, now);

        // How much of the session's wall-clock span was not monitored (sleep, stalls, a restarted monitor).
        var end = GamingAnalytics.EffectiveEnd(record, now);
        var gaps = store.GetGapsOverlapping(record.MonitoringSessionId, record.StartedUtc, end)
            .Select(g => new
            {
                g.Reason,
                StartUtc = g.StartUtc < record.StartedUtc ? record.StartedUtc : g.StartUtc,
                EndUtc = g.EndUtc > end ? end : g.EndUtc
            })
            .Select(g => new { g.Reason, g.StartUtc, g.EndUtc, Seconds = Math.Max(0, (g.EndUtc - g.StartUtc).TotalSeconds) })
            .ToList();

        return new
        {
            summary,
            exePath = record.ExePath,
            lastObservationUtc = record.LastObservationUtc,
            gaps = new { count = gaps.Count, seconds = gaps.Sum(g => g.Seconds), items = gaps },
            cost = GamingAnalytics.ComputeCost(record, prices, end)
        };
    }

    public static object Aggregates(SqliteEnergyStore store)
    {
        var prices = store.GetPriceSchedule();
        var now = DateTimeOffset.UtcNow;
        var summaries = store.ListAllGamingSessions().Select(record => GamingAnalytics.Summarize(record, prices, now)).ToList();
        return GamingAnalytics.AggregateByGame(summaries, prices.Currency);
    }

    public static object SetMapping(SqliteEnergyStore store, GamingService gaming, ServeRequest request)
    {
        if (request.ExeName is null)
            throw new ArgumentException("exeName is required.");
        var kind = request.MappingKind ?? throw new ArgumentException("mappingKind is required ('game' or 'excluded').");
        var saved = store.SetGameMapping(new GameMapping
        {
            ExeName = request.ExeName,
            ExePath = request.ExePath ?? "",
            Kind = kind,
            DisplayName = request.DisplayName
        }, DateTimeOffset.UtcNow);

        // A rename applies to the game everywhere: past sessions (history, totals) as well as the running one.
        if (saved.Kind == GameMappingKind.Game && !string.IsNullOrEmpty(saved.DisplayName))
            store.RenameGame(saved.ExeName, saved.DisplayName);

        var all = store.GetGameMappings();
        gaming.Tracker.ApplyMappings(all);
        return new { mapping = saved, mappings = all, live = gaming.Tracker.Live() };
    }

    public static object RemoveMapping(SqliteEnergyStore store, GamingService gaming, ServeRequest request)
    {
        if (request.ExeName is null)
            throw new ArgumentException("exeName is required.");
        var removed = store.RemoveGameMapping(request.ExeName, request.ExePath);
        var all = store.GetGameMappings();
        gaming.Tracker.ApplyMappings(all);
        return new { removed, mappings = all };
    }

    public static object StartManual(SqliteEnergyStore store, GamingService gaming, MonitoringController controller, ServeRequest request)
    {
        if (!controller.IsRunning)
            throw new InvalidOperationException("Monitoring is not running. Start monitoring first; a gaming session is energy attributed to monitored time.");
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("displayName is required.");
        if (name.Length > 80 || name.Any(char.IsControl))
            throw new ArgumentException("The game name must be at most 80 characters and contain no control characters.");

        string? exe = null;
        if (!string.IsNullOrWhiteSpace(request.ExeName))
            exe = SqliteEnergyStore.NormalizeExecutable(request.ExeName, request.ExePath).Name;

        var live = gaming.Tracker.StartManual(name, exe);

        if (request.RememberAsGame == true && exe is not null)
        {
            store.SetGameMapping(new GameMapping { ExeName = exe, Kind = GameMappingKind.Game, DisplayName = name }, DateTimeOffset.UtcNow);
            gaming.Tracker.ApplyMappings(store.GetGameMappings());
        }
        return new { live };
    }

    /// <summary>
    /// Executable names that could plausibly be games, for the user to pick from. The built-in
    /// never-a-game list and operating-system folders are left out. On demand only.
    /// </summary>
    public static object RunningExecutables(GamingService gaming)
    {
        return gaming.RunningProcesses()
            .Where(p => !GameCatalog.NeverGame.Contains(p.ExeName) && !GameCatalog.Launchers.Contains(p.ExeName))
            .Where(p => p.ExePath is null || !GameCatalog.SystemPathPattern.IsMatch(p.ExePath))
            .GroupBy(p => p.ExeName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { exeName = g.First().ExeName, exePath = g.Select(p => p.ExePath).FirstOrDefault(path => path is not null), instances = g.Count() })
            .OrderBy(x => x.exeName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRunningExecutables)
            .ToList();
    }
}
