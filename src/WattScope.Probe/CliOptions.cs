using System.Globalization;

namespace WattScope.Probe;

public enum CliCommand
{
    None,
    Discover,
    Monitor,
    Record,
    Sessions,
    Summary,
    SetPrice,
    Prices,
    Estimation,
    Retention,
    DbInfo,
    SelfTest
}

public sealed record CliOptions
{
    public bool ShowHelp { get; init; }
    public CliCommand Command { get; init; }
    public bool Discover => Command == CliCommand.Discover;
    public bool Monitor => Command == CliCommand.Monitor;
    public int IntervalMilliseconds { get; init; } = 1000;
    public int? DurationSeconds { get; init; }
    public string? ExportPath { get; init; }
    public string? DatabasePath { get; init; }
    public bool Resume { get; init; }
    public long? ResumeSessionId { get; init; }
    public bool Quiet { get; init; }
    public long? SessionId { get; init; }
    public DateOnly? Day { get; init; }
    public (int Year, int Month)? Month { get; init; }
    public decimal? Price { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public string Currency { get; init; } = "EUR";
    public double? OtherWatts { get; init; }
    public double? PsuEfficiency { get; init; }
    public bool? IncludeCpu { get; init; }
    public bool? IncludeGpu { get; init; }
    public int? RetentionDays { get; init; }
    public string? ParseError { get; init; }

    public bool NeedsHardware => Command is CliCommand.Discover or CliCommand.Monitor or CliCommand.Record;

    private static readonly Dictionary<string, CliCommand> CommandFlags = new()
    {
        ["--discover"] = CliCommand.Discover,
        ["--monitor"] = CliCommand.Monitor,
        ["--record"] = CliCommand.Record,
        ["--sessions"] = CliCommand.Sessions,
        ["--summary"] = CliCommand.Summary,
        ["--set-price"] = CliCommand.SetPrice,
        ["--prices"] = CliCommand.Prices,
        ["--estimation"] = CliCommand.Estimation,
        ["--retention"] = CliCommand.Retention,
        ["--db-info"] = CliCommand.DbInfo,
        ["--selftest"] = CliCommand.SelfTest
    };

    /// <summary>Which options each command accepts; anything else is an error rather than silently ignored.</summary>
    private static readonly Dictionary<CliCommand, string[]> AllowedOptions = new()
    {
        // --interval/--duration were accepted (and ignored) by --discover in Milestone 001; kept for compatibility.
        [CliCommand.Discover] = ["--export", "--interval", "--duration"],
        [CliCommand.Monitor] = ["--interval", "--duration", "--export"],
        [CliCommand.Record] = ["--interval", "--duration", "--db", "--resume", "--quiet"],
        [CliCommand.Sessions] = ["--db"],
        [CliCommand.Summary] = ["--db", "--session", "--day", "--month"],
        [CliCommand.SetPrice] = ["--db", "--effective", "--currency"],
        [CliCommand.Prices] = ["--db"],
        [CliCommand.Estimation] = ["--db", "--other-watts", "--psu-efficiency", "--include-cpu", "--include-gpu"],
        [CliCommand.Retention] = ["--db"],
        [CliCommand.DbInfo] = ["--db"],
        [CliCommand.SelfTest] = []
    };

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new CliOptions { ShowHelp = true };

        var result = new CliOptions();
        var command = CliCommand.None;
        var seenOptions = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "-?")
                return new CliOptions { ShowHelp = true };

            if (CommandFlags.TryGetValue(arg, out var flagCommand))
            {
                if (command != CliCommand.None)
                    return Error(command is CliCommand.Discover or CliCommand.Monitor && flagCommand is CliCommand.Discover or CliCommand.Monitor
                        ? "--discover and --monitor cannot be combined."
                        : $"Only one command may be given (found {arg} after another command).");
                command = flagCommand;

                // Commands that take an optional/required positional value.
                if (flagCommand == CliCommand.SetPrice)
                {
                    if (!TryNext(args, ref i, out var text) || !TryParseDecimal(text, out var price))
                        return Error("--set-price requires a price per kWh using '.' as decimal separator, e.g. --set-price 0.3175");
                    result = result with { Price = price };
                }
                else if (flagCommand == CliCommand.Retention && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var days))
                        return Error("--retention takes a whole number of days.");
                    result = result with { RetentionDays = days };
                }
                continue;
            }

            seenOptions.Add(arg);
            switch (arg)
            {
                case "--interval":
                    if (!TryReadInt(args, ref i, out var interval) || interval <= 0)
                        return Error("--interval requires a positive integer number of milliseconds.");
                    result = result with { IntervalMilliseconds = interval };
                    break;
                case "--duration":
                    if (!TryReadInt(args, ref i, out var duration) || duration <= 0)
                        return Error("--duration requires a positive integer number of seconds.");
                    result = result with { DurationSeconds = duration };
                    break;
                case "--export":
                    if (!TryNext(args, ref i, out var exportPath))
                        return Error("--export requires a file path.");
                    result = result with { ExportPath = exportPath };
                    break;
                case "--db":
                    if (!TryNext(args, ref i, out var dbPath))
                        return Error("--db requires a file path.");
                    result = result with { DatabasePath = dbPath };
                    break;
                case "--resume":
                    result = result with { Resume = true };
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        if (!long.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var resumeId))
                            return Error("--resume takes an optional numeric session ID.");
                        result = result with { ResumeSessionId = resumeId };
                    }
                    break;
                case "--quiet":
                    result = result with { Quiet = true };
                    break;
                case "--session":
                    if (!TryNext(args, ref i, out var sessionText) || !long.TryParse(sessionText, NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
                        return Error("--session requires a numeric session ID.");
                    result = result with { SessionId = sessionId };
                    break;
                case "--day":
                    if (!TryNext(args, ref i, out var dayText) || !DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        return Error("--day requires a date as yyyy-MM-dd.");
                    result = result with { Day = day };
                    break;
                case "--month":
                    if (!TryNext(args, ref i, out var monthText) || !DateOnly.TryParseExact(monthText + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                        return Error("--month requires a month as yyyy-MM.");
                    result = result with { Month = (month.Year, month.Month) };
                    break;
                case "--effective":
                    if (!TryNext(args, ref i, out var effectiveText) ||
                        !DateTimeOffset.TryParse(effectiveText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var effective))
                        return Error("--effective requires an ISO-8601 date/time, e.g. 2026-10-01 or 2026-10-01T00:00:00+02:00 (local time if no offset).");
                    result = result with { EffectiveFrom = effective.ToUniversalTime() };
                    break;
                case "--currency":
                    if (!TryNext(args, ref i, out var currency))
                        return Error("--currency requires a 3-letter currency code.");
                    result = result with { Currency = currency.ToUpperInvariant() };
                    break;
                case "--other-watts":
                    if (!TryNext(args, ref i, out var otherText) || !TryParseDouble(otherText, out var other))
                        return Error("--other-watts requires a number of watts using '.' as decimal separator.");
                    result = result with { OtherWatts = other };
                    break;
                case "--psu-efficiency":
                    if (!TryNext(args, ref i, out var effText) || !TryParseDouble(effText, out var eff))
                        return Error("--psu-efficiency requires a fraction such as 0.90.");
                    result = result with { PsuEfficiency = eff };
                    break;
                case "--include-cpu":
                case "--include-gpu":
                    if (!TryNext(args, ref i, out var boolText) || !bool.TryParse(boolText, out var include))
                        return Error($"{arg} requires true or false.");
                    result = arg == "--include-cpu" ? result with { IncludeCpu = include } : result with { IncludeGpu = include };
                    break;
                default:
                    return Error($"Unrecognized argument: {arg}");
            }
        }

        if (command == CliCommand.None)
            return Error("A command is required (e.g. --discover, --monitor, --record, --summary). See --help.");

        var allowed = AllowedOptions[command];
        var invalid = seenOptions.FirstOrDefault(o => !allowed.Contains(o));
        if (invalid is not null)
            return Error($"{invalid} cannot be used with {CommandFlags.First(kv => kv.Value == command).Key}.");

        if (command == CliCommand.Summary && new object?[] { result.SessionId, result.Day, result.Month }.Count(v => v is not null) > 1)
            return Error("--summary accepts at most one of --session, --day, --month.");

        return result with { Command = command };
    }

    private static CliOptions Error(string message) => new() { ParseError = message };

    private static bool TryNext(string[] args, ref int i, out string value)
    {
        if (i + 1 >= args.Length)
        {
            value = "";
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool TryReadInt(string[] args, ref int i, out int value)
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out value))
        {
            value = 0;
            return false;
        }
        i++;
        return true;
    }

    // Deliberately strict: no thousands separators, so "0,30" is rejected
    // instead of being read as 30 under the invariant culture.
    private const NumberStyles StrictNumber = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;

    public static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text, StrictNumber, CultureInfo.InvariantCulture, out value);

    public static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, StrictNumber, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public const string HelpText = """
        WattScope.Probe -- hardware, power sensor, energy and cost diagnostic CLI

        Usage:
          WattScope.Probe.exe --discover [--export <path>]
          WattScope.Probe.exe --monitor [--interval <ms>] [--duration <seconds>] [--export <path>]
          WattScope.Probe.exe --record [--interval <ms>] [--duration <seconds>] [--resume [<id>]] [--quiet] [--db <path>]
          WattScope.Probe.exe --sessions [--db <path>]
          WattScope.Probe.exe --summary [--session <id> | --day <yyyy-MM-dd> | --month <yyyy-MM>] [--db <path>]
          WattScope.Probe.exe --set-price <EUR/kWh> [--effective <date/time>] [--currency <code>] [--db <path>]
          WattScope.Probe.exe --prices [--db <path>]
          WattScope.Probe.exe --estimation [--other-watts <W>] [--psu-efficiency <0-1>] [--include-cpu true|false] [--include-gpu true|false] [--db <path>]
          WattScope.Probe.exe --retention [<days>] [--db <path>]
          WattScope.Probe.exe --db-info [--db <path>]
          WattScope.Probe.exe --selftest
          WattScope.Probe.exe --help

        Milestone 001 diagnostics (unchanged):
          --discover            Run one hardware and sensor discovery pass and print a summary.
          --monitor             Sample sensors repeatedly until Ctrl+C or --duration elapses.
                                With --export, writes every sensor for every tick (diagnostic only;
                                large files). Never writes to the database.

        Energy and cost (Milestone 002):
          --record              Record a monitoring session into the database: integrates selected
                                CPU package / GPU board power into energy, estimates wall energy,
                                stores compact samples and 15-minute aggregates. Ctrl+C stops cleanly.
          --resume [<id>]       With --record: continue a stopped/interrupted session (default: latest).
          --quiet               With --record: do not print a line per sample.
          --sessions            List recent sessions with their energy totals.
          --summary             Energy and cost report. Default: the latest session.
          --set-price <price>   Add an electricity price (per kWh, '.' decimal separator). Applies from
                                --effective (default: now) until the next price; past energy keeps its price.
          --prices              Show the price history.
          --estimation          Show or change wall-power estimation assumptions (applies to new sessions).
          --retention [<days>]  Show or set how many days raw samples are kept (aggregates are kept).
          --db-info             Database statistics and integrity check.
          --selftest            Validate energy integration, pricing, persistence and recovery using
                                synthetic data and a temporary database (no hardware needed).

        Options:
          --interval <ms>       Sampling interval in milliseconds. Default: 1000.
          --duration <seconds>  Stop automatically after this many seconds.
          --export <path>       Write a structured JSON report to this file.
          --db <path>           Database file. Default: %LOCALAPPDATA%\WattScope\wattscope.db
          --help                Show this help text.

        Examples:
          WattScope.Probe.exe --discover
          WattScope.Probe.exe --monitor --duration 60 --export report.json
          WattScope.Probe.exe --set-price 0.3175
          WattScope.Probe.exe --record --duration 600
          WattScope.Probe.exe --summary --month 2026-09
        """;
}
