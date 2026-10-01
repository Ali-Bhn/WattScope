using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;
using WattScope.Hardware;
using WattScope.Hardware.LibreHardwareMonitor;
using WattScope.Hardware.Nvml;
using WattScope.Probe;
using WattScope.Probe.Commands;
using WattScope.Storage;

var options = CliOptions.Parse(args);

if (options.ParseError is not null)
{
    Console.Error.WriteLine($"Error: {options.ParseError}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CliOptions.HelpText);
    return 1;
}

if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
        .SetMinimumLevel(LogLevel.Warning);
    // All log output goes to stderr, never stdout -- --serve's stdout is a
    // strict newline-delimited JSON protocol (see docs/ARCHITECTURE.md) that
    // a stray log line would corrupt; every other command's stdout is
    // human-readable text that a log line would just be noise in anyway.
    builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
});

if (options.Command == CliCommand.SelfTest)
    return SelfTestCommand.Run();

if (!options.NeedsHardware)
{
    // Database-only commands never initialize sensor providers.
    try
    {
        using var store = SqliteEnergyStore.Open(options.DatabasePath ?? SqliteEnergyStore.DefaultDatabasePath,
            loggerFactory.CreateLogger<SqliteEnergyStore>());
        return DatabaseCommands.Run(options, store);
    }
    catch (DatabaseSchemaTooNewException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Ctrl+C received, stopping...");
    cts.Cancel();
};

// Console window closed (SIGHUP) or logoff/shutdown (SIGTERM): Windows ends the
// process as soon as the handler returns, so request a stop and wait briefly
// for the session to be flushed and closed. If this does not finish in time,
// the session is recovered as Interrupted on the next start.
var shutdownComplete = new ManualResetEventSlim(false); // not disposed: a late signal must never hit a disposed handle
void OnTerminationSignal(PosixSignalContext context)
{
    cts.Cancel();
    shutdownComplete.Wait(TimeSpan.FromSeconds(4));
}
using var closeRegistration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTerminationSignal);
using var terminateRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminationSignal);

var providers = new List<ISensorProvider>
{
    new LibreHardwareMonitorProvider(loggerFactory.CreateLogger<LibreHardwareMonitorProvider>()),
    new NvmlSensorProvider(loggerFactory.CreateLogger<NvmlSensorProvider>())
};

try
{
    var discoveryService = new DiscoveryService(providers, logger: loggerFactory.CreateLogger<DiscoveryService>());
    var snapshot = discoveryService.Discover();

    if (options.Discover)
    {
        return await DiscoverCommand.RunAsync(snapshot: snapshot, exportPath: options.ExportPath, cancellationToken: cts.Token);
    }

    // --monitor / --record
    var availableProviderIds = snapshot.Providers.Where(p => p.IsAvailable).Select(p => p.ProviderId).ToHashSet();
    var availableProviders = providers.Where(p => availableProviderIds.Contains(p.ProviderId)).ToList();

    if (availableProviders.Count == 0)
    {
        Console.Error.WriteLine("No sensor provider is available; nothing to monitor.");
        return 2;
    }

    if (options.Command == CliCommand.Record)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        return await RecordCommand.RunAsync(snapshot, availableProviders, options, loggerFactory, version, cts.Token);
    }

    if (options.Command == CliCommand.Serve)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        return await ServeCommand.RunAsync(snapshot, availableProviders, options, loggerFactory, version, cts.Token);
    }

    var samplingEngine = new SamplingEngine(availableProviders, loggerFactory.CreateLogger<SamplingEngine>());
    return await MonitorCommand.RunAsync(snapshot, samplingEngine, options, cts.Token);
}
catch (DatabaseSchemaTooNewException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
finally
{
    foreach (var provider in providers)
        provider.Dispose();
    shutdownComplete.Set();
}
