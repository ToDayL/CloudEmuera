namespace CloudEmuera.Application.Auditing;

/// <summary>Supplies request correlation without coupling application or infrastructure code to HTTP.</summary>
public interface IAuditContext
{
    string? RequestId { get; }
}

public sealed class NullAuditContext : IAuditContext
{
    public static NullAuditContext Instance { get; } = new();
    public string? RequestId => null;
    private NullAuditContext() { }
}
