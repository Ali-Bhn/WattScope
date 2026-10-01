using System.Globalization;
using WattScope.Core.Estimation;
using WattScope.Core.Gaming;
using WattScope.Storage;

namespace WattScope.Probe.Commands;

/// <summary>Gaming sessions, per-game totals and mappings from the command line. Database only; no hardware, no processes.</summary>
public static class GamingCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int List(SqliteEnergyStore store)
    {
        var prices = store.GetPriceSchedule();
        var now = DateTimeOffset.UtcNow;
        var sessions = store.ListGamingSessions(50).Select(r => GamingAnalytics.Summarize(r, prices, now)).ToList();

        Console.WriteLine($"Gaming sessions ({store.DatabasePath}):");
        if (sessions.Count == 0)
            Console.WriteLine("  none recorded yet.");
        foreach (var s in sessions)
        {
            var lb = s.SystemIsLowerBound ? ">= " : "";
            Console.WriteLine(string.Format(Inv,
                "  #{0} {1}  {2} -> {3}  duration {4}  monitored {5}  [{6}{7}]  {8}",
                s.Id, s.DisplayName, EnergyConsoleReporter.Local(s.StartedUtc), s.EndedUtc is { } e ? EnergyConsoleReporter.Local(e) : "(running)",
                EnergyConsoleReporter.Duration(TimeSpan.FromSeconds(s.DurationSeconds)), EnergyConsoleReporter.Duration(TimeSpan.FromSeconds(s.MonitoredSeconds)),
                s.Status, s.EndReason is { } r ? $": {r}" : "", s.IsManual ? "manual" : $"detected: {s.Source}"));
            Console.WriteLine(string.Format(Inv,
                "      completeness {0}; energy {1}{2:F4} kWh ({3}); avg CPU {4}, GPU {5}, system {6}{7}; peak CPU {8}, GPU {9}; cost {10}",
                s.Completeness, lb, s.SystemJoules / 3_600_000.0, s.Completeness == EstimateCompleteness.Complete ? "ESTIMATED" : "ESTIMATED lower bound",
                W(s.AvgCpuWatts), W(s.AvgGpuWatts), s.SystemIsLowerBound && s.AvgSystemWatts is not null ? ">= " : "", W(s.AvgSystemWatts), W(s.PeakCpuWatts), W(s.PeakGpuWatts),
                s.CostStatus == GamingCostStatus.Priced ? $"{s.Cost:F4} {s.Currency}" : $"n/a ({s.CostStatus})"));
        }

        var games = GamingAnalytics.AggregateByGame(store.ListAllGamingSessions().Select(r => GamingAnalytics.Summarize(r, prices, now)), prices.Currency);
        if (games.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Per game (finished sessions; complete and partial are never added together):");
            foreach (var g in games)
            {
                Console.WriteLine(string.Format(Inv, "  {0}: {1} session(s), play time {2}, monitored {3}, average session {4}",
                    g.DisplayName, g.SessionCount, EnergyConsoleReporter.Duration(TimeSpan.FromSeconds(g.TotalDurationSeconds)),
                    EnergyConsoleReporter.Duration(TimeSpan.FromSeconds(g.TotalMonitoredSeconds)), EnergyConsoleReporter.Duration(TimeSpan.FromSeconds(g.AverageSessionDurationSeconds))));
                if (g.Complete.Sessions > 0)
                    Console.WriteLine(string.Format(Inv, "      complete: {0} session(s), {1:F4} kWh, avg system {2}, cost {3}",
                        g.Complete.Sessions, g.Complete.SystemJoules / 3_600_000.0, W(g.Complete.AvgSystemWatts), g.Complete.Cost is { } c ? $"{c:F4} {g.Currency}" : "n/a"));
                if (g.Partial.Sessions > 0)
                    Console.WriteLine(string.Format(Inv, "      partial:  {0} session(s), >= {1:F4} kWh (lower bound), no cost", g.Partial.Sessions, g.Partial.SystemJoules / 3_600_000.0));
            }
        }

        var mappings = store.GetGameMappings();
        Console.WriteLine();
        Console.WriteLine("Your game mappings:");
        if (mappings.Count == 0)
            Console.WriteLine("  none.");
        foreach (var m in mappings)
            Console.WriteLine($"  {m.ExeName}{(m.ExePath.Length > 0 ? $" ({m.ExePath})" : "")}: {(m.Kind == GameMappingKind.Game ? "game" : "excluded")}{(string.IsNullOrEmpty(m.DisplayName) ? "" : $" as \"{m.DisplayName}\"")}");
        return 0;
    }

    public static int Mapping(CliOptions options, SqliteEnergyStore store)
    {
        var exe = options.GameExecutable ?? "";
        try
        {
            if (options.Command == CliCommand.GameUnmark)
            {
                Console.WriteLine(store.RemoveGameMapping(exe, null) ? $"Removed the mapping for {exe}." : $"There was no mapping for {exe}.");
                return 0;
            }

            var kind = options.Command == CliCommand.GameMark ? GameMappingKind.Game : GameMappingKind.Excluded;
            var saved = store.SetGameMapping(new GameMapping { ExeName = exe, Kind = kind, DisplayName = options.GameName }, DateTimeOffset.UtcNow);
            if (kind == GameMappingKind.Game && !string.IsNullOrEmpty(saved.DisplayName))
                store.RenameGame(saved.ExeName, saved.DisplayName);
            Console.WriteLine(kind == GameMappingKind.Game
                ? $"{saved.ExeName} is now marked as a game{(string.IsNullOrEmpty(saved.DisplayName) ? "" : $" named \"{saved.DisplayName}\"")}."
                : $"{saved.ExeName} will never be detected as a game.");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string W(double? watts) => watts is { } w ? w.ToString("F1", Inv) + " W" : "n/a";
}
