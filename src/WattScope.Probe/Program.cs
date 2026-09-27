using Microsoft.Extensions.Logging;
using WattScope.Core.Sampling;
using WattScope.Core.Sensors;
using WattScope.Hardware;
using WattScope.Hardware.LibreHardwareMonitor;
using WattScope.Hardware.Nvml;
using WattScope.Probe;
using WattScope.Probe.Commands;

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

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Warning));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Ctrl+C received, stopping...");
    cts.Cancel();
};

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

    // options.Monitor
    var availableProviderIds = snapshot.Providers.Where(p => p.IsAvailable).Select(p => p.ProviderId).ToHashSet();
    var availableProviders = providers.Where(p => availableProviderIds.Contains(p.ProviderId)).ToList();

    if (availableProviders.Count == 0)
    {
        Console.Error.WriteLine("No sensor provider is available; nothing to monitor.");
        return 2;
    }

    var samplingEngine = new SamplingEngine(availableProviders, loggerFactory.CreateLogger<SamplingEngine>());
    return await MonitorCommand.RunAsync(snapshot, samplingEngine, options, cts.Token);
}
finally
{
    foreach (var provider in providers)
        provider.Dispose();
}
