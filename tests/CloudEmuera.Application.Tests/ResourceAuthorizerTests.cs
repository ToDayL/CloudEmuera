using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using Xunit;

namespace CloudEmuera.Application.Tests;

public sealed class ResourceAuthorizerTests
{
    [Fact]
    [Trait("Category", "Authorization")]
    public async Task AdminCannotReadAnotherPlayersPrivateSaveButCanForceStop()
    {
        ResourceAuthorizer authorizer = new(new Reader(new(ResourceKind.Save, "sess_1", "usr_owner")));
        ResourceAuthorizer sessionAuthorizer = new(new Reader(new(ResourceKind.Session, "sess_1", "usr_owner")));
        CurrentActor admin = new("usr_admin", "ADMIN", "auths_admin");
        Assert.Equal(ResourceAccessDecision.NotFoundOrHidden, await authorizer.AuthorizeAsync(admin, ResourceKind.Save, "sess_1", ResourceAction.SaveDownload));
        Assert.Equal(ResourceAccessDecision.NotFoundOrHidden, await authorizer.AuthorizeAsync(admin, ResourceKind.Save, "sess_1", ResourceAction.SessionForceStop));
        Assert.Equal(ResourceAccessDecision.Allowed, await sessionAuthorizer.AuthorizeAsync(admin, ResourceKind.Session, "sess_1", ResourceAction.SessionForceStop));
    }

    [Fact]
    [Trait("Category", "Authorization")]
    public async Task DescriptorKindAndIdMustMatchTheRequestedBoundary()
    {
        CurrentActor owner = new("usr_owner", "PLAYER", "auths_owner");
        ResourceAuthorizer wrongKind = new(new Reader(new(ResourceKind.Save, "sess_1", "usr_owner")));
        ResourceAuthorizer wrongId = new(new Reader(new(ResourceKind.Session, "sess_other", "usr_owner")));

        Assert.Equal(ResourceAccessDecision.NotFoundOrHidden, await wrongKind.AuthorizeAsync(owner, ResourceKind.Session, "sess_1", ResourceAction.SessionRead));
        Assert.Equal(ResourceAccessDecision.NotFoundOrHidden, await wrongId.AuthorizeAsync(owner, ResourceKind.Session, "sess_1", ResourceAction.SessionRead));
    }

    [Theory]
    [InlineData("usr_owner", ResourceKind.Game, ResourceAction.GameRead, ResourceAccessDecision.Allowed)]
    [InlineData("usr_other", ResourceKind.Game, ResourceAction.GameRead, ResourceAccessDecision.Allowed)]
    [InlineData("usr_other", ResourceKind.Game, ResourceAction.GameMutate, ResourceAccessDecision.NotFoundOrHidden)]
    [InlineData("usr_admin", ResourceKind.Save, ResourceAction.SaveDownload, ResourceAccessDecision.NotFoundOrHidden)]
    [InlineData("usr_admin", ResourceKind.Session, ResourceAction.SessionForceStop, ResourceAccessDecision.Allowed)]
    [Trait("Category", "Authorization")]
    public async Task MatrixKeepsPrivateResourcesHiddenAndGrantsOnlyExplicitAdminActions(string actorId, ResourceKind kind, ResourceAction action, ResourceAccessDecision expected)
    {
        ResourceDescriptor descriptor = kind == ResourceKind.Game
            ? new(ResourceKind.Game, "game_1", "usr_owner", isServerShared: true)
            : new(kind, "sess_1", "usr_owner");
        ResourceAuthorizer authorizer = new(new Reader(descriptor));
        CurrentActor actor = new(actorId, actorId == "usr_admin" ? "ADMIN" : "PLAYER", "auths_test");

        Assert.Equal(expected, await authorizer.AuthorizeAsync(actor, kind, descriptor.Id, action));
    }

    [Fact]
    [Trait("Category", "Authorization")]
    public async Task GlobalAdministrationAndAuditDoNotDependOnAResourceDescriptor()
    {
        ResourceAuthorizer authorizer = new(new NullReader());
        CurrentActor admin = new("usr_admin", "ADMIN", "auths_admin");
        CurrentActor player = new("usr_player", "PLAYER", "auths_player");

        Assert.Equal(ResourceAccessDecision.Allowed, await authorizer.AuthorizeAsync(admin, ResourceKind.User, "usr_admin", ResourceAction.UserAdminister));
        Assert.Equal(ResourceAccessDecision.Allowed, await authorizer.AuthorizeAsync(admin, ResourceKind.Audit, "instance", ResourceAction.AuditRead));
        Assert.Equal(ResourceAccessDecision.Forbidden, await authorizer.AuthorizeAsync(player, ResourceKind.User, "usr_player", ResourceAction.UserAdminister));
        Assert.Equal(ResourceAccessDecision.PasswordChangeRequired, await authorizer.AuthorizeAsync(admin, ResourceKind.User, "usr_admin", ResourceAction.UserAdminister, mustChangePassword: true));
    }

    private sealed class Reader(ResourceDescriptor descriptor) : IResourceAccessReader
    {
        public Task<ResourceDescriptor?> FindAsync(ResourceKind kind, string resourceId, CancellationToken cancellationToken = default) => Task.FromResult<ResourceDescriptor?>(descriptor);
    }

    private sealed class NullReader : IResourceAccessReader
    {
        public Task<ResourceDescriptor?> FindAsync(ResourceKind kind, string resourceId, CancellationToken cancellationToken = default) => Task.FromResult<ResourceDescriptor?>(null);
    }
}
