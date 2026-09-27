namespace WattScope.Probe;

public sealed record CliOptions
{
    public bool ShowHelp { get; init; }
    public bool Discover { get; init; }
    public bool Monitor { get; init; }
    public int IntervalMilliseconds { get; init; } = 1000;
    public int? DurationSeconds { get; init; }
    public string? ExportPath { get; init; }
    public string? ParseError { get; init; }

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new CliOptions { ShowHelp = true };

        bool discover = false, monitor = false, showHelp = false;
        int interval = 1000;
        int? duration = null;
        string? exportPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h" or "-?":
                    showHelp = true;
                    break;
                case "--discover":
                    discover = true;
                    break;
                case "--monitor":
                    monitor = true;
                    break;
                case "--interval":
                    if (!TryReadInt(args, ref i, out interval) || interval <= 0)
                        return new CliOptions { ParseError = "--interval requires a positive integer number of milliseconds." };
                    break;
                case "--duration":
                    if (!TryReadInt(args, ref i, out var durationValue) || durationValue <= 0)
                        return new CliOptions { ParseError = "--duration requires a positive integer number of seconds." };
                    duration = durationValue;
                    break;
                case "--export":
                    if (i + 1 >= args.Length)
                        return new CliOptions { ParseError = "--export requires a file path." };
                    exportPath = args[++i];
                    break;
                default:
                    return new CliOptions { ParseError = $"Unrecognized argument: {args[i]}" };
            }
        }

        if (showHelp)
            return new CliOptions { ShowHelp = true };

        if (!discover && !monitor)
            return new CliOptions { ParseError = "One of --discover or --monitor is required." };

        if (discover && monitor)
            return new CliOptions { ParseError = "--discover and --monitor cannot be combined." };

        return new CliOptions
        {
            Discover = discover,
            Monitor = monitor,
            IntervalMilliseconds = interval,
            DurationSeconds = duration,
            ExportPath = exportPath
        };
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

    public const string HelpText = """
        WattScope.Probe -- hardware and power sensor diagnostic CLI

        Usage:
          WattScope.Probe.exe --discover [--export <path>]
          WattScope.Probe.exe --monitor [--interval <ms>] [--duration <seconds>] [--export <path>]
          WattScope.Probe.exe --help

        Commands:
          --discover            Run one hardware and sensor discovery pass and print a summary.
          --monitor             Sample sensors repeatedly until Ctrl+C or --duration elapses.

        Options:
          --interval <ms>       Sampling interval in milliseconds for --monitor. Default: 1000.
          --duration <seconds>  Stop --monitor automatically after this many seconds.
          --export <path>       Write a structured JSON report to this file.
          --help                Show this help text.

        Examples:
          WattScope.Probe.exe --discover
          WattScope.Probe.exe --monitor --interval 1000
          WattScope.Probe.exe --monitor --duration 60 --export report.json
        """;
}
