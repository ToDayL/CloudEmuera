using CloudEmuera.Application.Auditing;

namespace CloudEmuera.Api.Security;

public sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    public string? RequestId => accessor.HttpContext?.TraceIdentifier;
}
