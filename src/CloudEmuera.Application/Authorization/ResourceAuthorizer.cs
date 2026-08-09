using CloudEmuera.Application.Identity;

namespace CloudEmuera.Application.Authorization;

public enum ResourceKind { Game, Session, Save, Worker, User, Audit }

public enum ResourceAction
{
    GameRead, GameMutate, GameValidate, GameActivate, GameBlock,
    SessionRead, SessionControl, SessionForceStop, SessionResume,
    SaveList, SaveDownload, SaveMutate, UserAdminister, AuditRead,
}

public enum ResourceAccessDecision { Allowed, NotFoundOrHidden, Forbidden, PasswordChangeRequired }

public sealed class ResourceDescriptor(ResourceKind kind, string id, string ownerUserId, bool isServerShared = false, bool exists = true)
{
    public ResourceKind Kind { get; } = kind; public string Id { get; } = id; public string OwnerUserId { get; } = ownerUserId; public bool IsServerShared { get; } = isServerShared; public bool Exists { get; } = exists;
}

public interface IResourceAccessReader
{
    Task<ResourceDescriptor?> FindAsync(ResourceKind kind, string resourceId, CancellationToken cancellationToken = default);
}

public interface IResourceAuthorizer
{
    Task<ResourceAccessDecision> AuthorizeAsync(CurrentActor actor, ResourceKind kind, string resourceId, ResourceAction action, bool mustChangePassword = false, CancellationToken cancellationToken = default);
}

public sealed class ResourceAuthorizer(IResourceAccessReader reader) : IResourceAuthorizer
{
    public async Task<ResourceAccessDecision> AuthorizeAsync(CurrentActor actor, ResourceKind kind, string resourceId, ResourceAction action, bool mustChangePassword = false, CancellationToken cancellationToken = default)
    {
        if (mustChangePassword) return ResourceAccessDecision.PasswordChangeRequired;
        if (!ActionMatchesKind(kind, action)) return ResourceAccessDecision.NotFoundOrHidden;
        // Instance-wide capabilities are not tied to a private row.  Resolve
        // them before descriptor lookup so every API boundary can use the same
        // authorizer for administration and audit access.
        if (action is ResourceAction.UserAdminister or ResourceAction.AuditRead)
            return actor.IsAdmin ? ResourceAccessDecision.Allowed : ResourceAccessDecision.Forbidden;
        ResourceDescriptor? resource = await reader.FindAsync(kind, resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !resource.Exists || resource.Kind != kind || !string.Equals(resource.Id, resourceId, StringComparison.Ordinal))
            return ResourceAccessDecision.NotFoundOrHidden;

        if (action == ResourceAction.SessionForceStop)
            return actor.IsAdmin ? ResourceAccessDecision.Allowed : ResourceAccessDecision.NotFoundOrHidden;
        if (action == ResourceAction.GameBlock)
            return actor.IsAdmin ? ResourceAccessDecision.Allowed : ResourceAccessDecision.NotFoundOrHidden;

        bool owner = string.Equals(resource.OwnerUserId, actor.UserId, StringComparison.Ordinal);
        bool sharedRead = resource.IsServerShared && action == ResourceAction.GameRead;
        return owner || sharedRead ? ResourceAccessDecision.Allowed : ResourceAccessDecision.NotFoundOrHidden;
    }

    private static bool ActionMatchesKind(ResourceKind kind, ResourceAction action) => action switch
    {
        ResourceAction.GameRead or ResourceAction.GameMutate or ResourceAction.GameValidate or ResourceAction.GameActivate or ResourceAction.GameBlock => kind == ResourceKind.Game,
        ResourceAction.SessionRead or ResourceAction.SessionControl or ResourceAction.SessionForceStop or ResourceAction.SessionResume => kind == ResourceKind.Session,
        ResourceAction.SaveList or ResourceAction.SaveDownload or ResourceAction.SaveMutate => kind == ResourceKind.Save,
        ResourceAction.UserAdminister => kind == ResourceKind.User,
        ResourceAction.AuditRead => kind == ResourceKind.Audit,
        _ => false,
    };
}
