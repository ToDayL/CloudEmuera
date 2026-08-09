namespace CloudEmuera.Infrastructure.Persistence;

public sealed class CompatibilityDiagnosticRow
{
    public string Id { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public int WorkspaceRevision { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? LogicalPath { get; set; }
    public int? LineNumber { get; set; }
    public string MessageKey { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public bool ActivationBlocking { get; set; }
    public string OverridePolicy { get; set; } = "NEVER";
    public string? OverriddenBy { get; set; }
    public DateTimeOffset? OverriddenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public GameRow? Game { get; set; }
}
