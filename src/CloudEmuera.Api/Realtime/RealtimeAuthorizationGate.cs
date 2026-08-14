using System.Security.Claims;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;

namespace CloudEmuera.Api.Realtime;

public sealed record RealtimeConnectionIdentity(
    string UserId,
    string AuthSessionId,
    string SecurityStamp,
    string Role);

public enum RealtimeAuthorizationStatus
{
    Allowed,
    AuthenticationExpired,
    PasswordChangeRequired,
    NotFoundOrHidden,
    Forbidden,
}

public sealed record RealtimeAuthorizationResult(
    RealtimeAuthorizationStatus Status,
    CurrentActor? Actor = null,
    CurrentUser? User = null)
{
    public bool Allowed => Status == RealtimeAuthorizationStatus.Allowed;
}

/// <summary>
/// Performs live identity and resource checks without retaining a scoped
/// identity service for the lifetime of a WebSocket. Every operation creates
/// a short-lived scope, so revocation and password changes take effect on the
/// next resume/input and on periodic connection revalidation.
/// </summary>
public sealed class RealtimeAuthorizationGate(IServiceScopeFactory scopeFactory)
{
    public RealtimeConnectionIdentity? ReadIdentity(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string? userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        string? authSessionId = principal.FindFirstValue("auth_session_id");
        string? securityStamp = principal.FindFirstValue("security_stamp");
        string? role = principal.FindFirstValue(ClaimTypes.Role);
        return string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(authSessionId) ||
               string.IsNullOrWhiteSpace(securityStamp) || string.IsNullOrWhiteSpace(role)
            ? null
            : new RealtimeConnectionIdentity(userId, authSessionId, securityStamp, role);
    }

    public Task<RealtimeAuthorizationResult> AuthenticateConnectionAsync(
        RealtimeConnectionIdentity identity,
        CancellationToken cancellationToken = default) =>
        AuthorizeAsync(identity, action: null, sessionId: null, cancellationToken);

    public Task<RealtimeAuthorizationResult> AuthorizeResumeAsync(
        RealtimeConnectionIdentity identity,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        AuthorizeAsync(identity, ResourceAction.SessionResume, sessionId, cancellationToken);

    public Task<RealtimeAuthorizationResult> AuthorizeInputAsync(
        RealtimeConnectionIdentity identity,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        AuthorizeAsync(identity, ResourceAction.SessionControl, sessionId, cancellationToken);

    private async Task<RealtimeAuthorizationResult> AuthorizeAsync(
        RealtimeConnectionIdentity identity,
        ResourceAction? action,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (action is not null && string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A Session ID is required for a resource authorization check.", nameof(sessionId));

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ILocalIdentityService identities = scope.ServiceProvider.GetRequiredService<ILocalIdentityService>();
        if (!await identities.ValidateSessionAsync(identity.UserId, identity.AuthSessionId, identity.SecurityStamp, cancellationToken).ConfigureAwait(false))
            return new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.AuthenticationExpired);

        CurrentUser? user = await identities.GetCurrentUserAsync(identity.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || !string.Equals(user.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.AuthenticationExpired);
        if (user.MustChangePassword)
            return new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.PasswordChangeRequired, null, user);

        var actor = new CurrentActor(user.Id, user.Role, identity.AuthSessionId);
        if (action is null)
            return new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.Allowed, actor, user);

        IResourceAuthorizer authorizer = scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>();
        ResourceAccessDecision decision = await authorizer.AuthorizeAsync(
            actor,
            ResourceKind.Session,
            sessionId!,
            action.Value,
            mustChangePassword: user.MustChangePassword,
            cancellationToken).ConfigureAwait(false);
        return decision switch
        {
            ResourceAccessDecision.Allowed => new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.Allowed, actor, user),
            ResourceAccessDecision.PasswordChangeRequired => new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.PasswordChangeRequired, actor, user),
            ResourceAccessDecision.Forbidden => new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.Forbidden, actor, user),
            _ => new RealtimeAuthorizationResult(RealtimeAuthorizationStatus.NotFoundOrHidden, actor, user),
        };
    }
}
