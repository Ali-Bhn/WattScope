namespace WattScope.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error
}

public sealed record DiagnosticMessage
{
    public required DiagnosticSeverity Severity { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
