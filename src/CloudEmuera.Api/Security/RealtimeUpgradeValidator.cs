using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Authorization;

namespace CloudEmuera.Api.Security;

/// <summary>Reusable authenticated WebSocket upgrade gate; it owns no WebSocket protocol state.</summary>
public sealed class RealtimeUpgradeValidator(ILocalIdentityService identities, IResourceAuthorizer authorizer)
{
    public async Task<bool> IsUpgradeAllowedAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (!context.WebSockets.IsWebSocketRequest) return false;

        string? userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        string? sessionId = context.User.FindFirst("auth_session_id")?.Value;
        string? stamp = context.User.FindFirst("security_stamp")?.Value;
        return userId is not null && sessionId is not null && stamp is not null && await identities.ValidateSessionAsync(userId, sessionId, stamp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Called by the P1-08 resume envelope handler for every resume attempt.</summary>
    public Task<ResourceAccessDecision> AuthorizeResumeAsync(CurrentActor actor, string sessionId, bool mustChangePassword, CancellationToken cancellationToken = default) =>
        authorizer.AuthorizeAsync(actor, ResourceKind.Session, sessionId, ResourceAction.SessionResume, mustChangePassword, cancellationToken);
}
