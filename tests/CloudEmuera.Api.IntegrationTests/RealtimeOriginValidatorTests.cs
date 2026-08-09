using System.Net.WebSockets;
using System.Security.Claims;
using CloudEmuera.Api.Security;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class RealtimeOriginValidatorTests
{
    [Theory]
    [InlineData(null, true, true, true, false)]
    [InlineData("https://evil.example", true, true, true, false)]
    [InlineData("https://cloudemuera.example", false, true, true, false)]
    [InlineData("https://cloudemuera.example", true, false, true, false)]
    [InlineData("https://cloudemuera.example", true, true, false, false)]
    [InlineData("https://cloudemuera.example", true, true, true, true)]
    [Trait("Category", "Authorization")]
    public async Task UpgradeRequiresWebSocketExactOriginPrincipalAndLiveSession(string? origin, bool webSocket, bool liveSession, bool authenticated, bool expected)
    {
        SessionIdentity identities = new(liveSession);
        RealtimeOriginValidator validator = new(Configuration(), identities, new RecordingAuthorizer());
        DefaultHttpContext context = Context(origin, webSocket, authenticated);

        Assert.Equal(expected, await validator.IsUpgradeAllowedAsync(context));
    }

    [Fact]
    [Trait("Category", "Authorization")]
    public async Task EveryResumeAttemptCallsTheCentralAuthorizer()
    {
        RecordingAuthorizer authorizer = new();
        RealtimeOriginValidator validator = new(Configuration(), new SessionIdentity(true), authorizer);
        CurrentActor actor = new("usr_player", "PLAYER", "auths_live");

        Assert.Equal(ResourceAccessDecision.Allowed, await validator.AuthorizeResumeAsync(actor, "sess_one", false));
        Assert.Equal(ResourceAccessDecision.Allowed, await validator.AuthorizeResumeAsync(actor, "sess_one", false));
        Assert.Equal(2, authorizer.Calls);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["CloudEmuera:PublicOrigin"] = "https://cloudemuera.example" }).Build();

    private static DefaultHttpContext Context(string? origin, bool webSocket, bool authenticated)
    {
        DefaultHttpContext context = new();
        context.Features.Set<IHttpWebSocketFeature>(new WebSocketFeature(webSocket));
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (authenticated) context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "usr_player"),
            new Claim("auth_session_id", "auths_live"),
            new Claim("security_stamp", "live-stamp"),
        ], "test"));
        return context;
    }

    private sealed class WebSocketFeature(bool isWebSocketRequest) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest { get; } = isWebSocketRequest;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }

    private sealed class SessionIdentity(bool live) : ILocalIdentityService
    {
        public Task<bool> ValidateSessionAsync(string userId, string sessionId, string securityStamp, CancellationToken cancellationToken = default) => Task.FromResult(live);
        public Task<LoginResult?> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CurrentUser?> GetCurrentUserAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task LogoutAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginResult?> ChangePasswordAsync(CurrentActor actor, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CurrentUser>> ListUsersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CurrentUser> CreateUserAsync(CreateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CurrentUser> UpdateUserAsync(string id, UpdateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResetPasswordAsync(string id, string temporaryPassword, int expectedStateVersion, CurrentActor actor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAuthorizer : IResourceAuthorizer
    {
        public int Calls { get; private set; }
        public Task<ResourceAccessDecision> AuthorizeAsync(CurrentActor actor, ResourceKind kind, string resourceId, ResourceAction action, bool mustChangePassword = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(ResourceAccessDecision.Allowed);
        }
    }
}
