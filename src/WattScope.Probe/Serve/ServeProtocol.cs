using System.Text.Json;
using System.Text.Json.Serialization;

namespace WattScope.Probe.Serve;

/// <summary>
/// The newline-delimited JSON protocol <c>--serve</c> speaks on stdin/stdout,
/// for the desktop app's native sidecar (docs/ARCHITECTURE.md, "Data flow
/// (--serve)"). Every line on stdout is exactly one JSON object; nothing else
/// is ever written there (all logging goes to stderr; see Program.cs). Every
/// line on stdin is exactly one JSON request object.
///
/// Domain objects (<c>EnergyReport</c>, <c>CostBreakdown</c>,
/// <c>MonitoringSession</c>, etc.) are serialized directly with these
/// options rather than mapped into a separate DTO layer -- they are already
/// plain, side-effect-free records, and duplicating their shape would be
/// pure risk (a field renamed in one place and not the other) for no benefit.
/// </summary>
public static class ServeProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    private static readonly object WriteLock = new();

    /// <summary>Writes one JSON value as a single stdout line, synchronized against concurrent writers (the tick loop and the request loop both write stdout).</summary>
    public static void WriteLine(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        lock (WriteLock)
        {
            // Explicit '\n', not Console.WriteLine's platform newline (CRLF on
            // Windows) -- the protocol is one JSON object per '\n', full stop.
            Console.Out.Write(json);
            Console.Out.Write('\n');
            Console.Out.Flush();
        }
    }
}

/// <summary>One line read from stdin. Unrecognized/absent fields are simply null for commands that don't use them.</summary>
public sealed record ServeRequest
{
    public required long Id { get; init; }
    public required string Cmd { get; init; }
    public long? SessionId { get; init; }
    public DateOnly? Day { get; init; }
    /// <summary>"yyyy-MM"</summary>
    public string? Month { get; init; }
    public decimal? PricePerKwh { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public string? Currency { get; init; }
    public double? OtherWatts { get; init; }
    public double? PsuEfficiency { get; init; }
    public bool? IncludeCpu { get; init; }
    public bool? IncludeGpu { get; init; }
    public int? RetentionDays { get; init; }
    public int? IntervalMs { get; init; }

    /// <summary>start_monitoring: continue an existing session (default: latest) instead of opening a new one.</summary>
    public bool? Resume { get; init; }

    // Gaming (Milestone 005)
    public long? GamingSessionId { get; init; }
    public string? GameKey { get; init; }
    public int? Limit { get; init; }

    /// <summary>File name (<c>cod.exe</c>) -- or a full path, which is split into name + path.</summary>
    public string? ExeName { get; init; }
    public string? ExePath { get; init; }

    /// <summary>"game" or "excluded".</summary>
    public WattScope.Core.Gaming.GameMappingKind? MappingKind { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>start_manual_gaming: also remember the executable as a game so it is detected automatically next time.</summary>
    public bool? RememberAsGame { get; init; }
}

public sealed record ServeResponse
{
    public string Type => "response";
    public required long? Id { get; init; }
    public required bool Ok { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }

    public static ServeResponse Success(long id, object? data) => new() { Id = id, Ok = true, Data = data };
    public static ServeResponse Failure(long? id, string error) => new() { Id = id, Ok = false, Error = error };
}

/// <summary>Sidecar lifecycle event, distinct from the per-sample tick stream.</summary>
public sealed record StatusMessage
{
    public string Type => "status";
    public required string State { get; init; }
    public long? SessionId { get; init; }
    public bool? Elevated { get; init; }
    public bool? CpuAvailable { get; init; }
    public bool? GpuAvailable { get; init; }
    public string? Message { get; init; }
}

