using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Security;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Contracts.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class RealtimeEndpointContractTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ce-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? factory;

    // SEC-011: a configured nonmatching Origin must fail before socket acceptance.
    [Fact]
    [Trait("Category", "Realtime")]
    public async Task ConfiguredOriginPolicyRejectsUpgradeBeforeAcceptingSocket()
    {
        RealtimeOriginPolicy policy = RealtimeOriginPolicy.Parse("https://allowed.example");
        ServiceCollection services = new();
        services.AddScoped(_ => new RealtimeUpgradeValidator(null!, null!, policy));
        using ServiceProvider provider = services.BuildServiceProvider();
        RealtimeEndpoint endpoint = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        DefaultHttpContext context = new();
        context.RequestServices = provider;
        context.Features.Set<IHttpWebSocketFeature>(new IncomingWebSocketFeature());
        context.Request.Headers[HeaderNames.SecWebSocketProtocol] = RealtimeProtocol.Subprotocol;
        context.Request.Headers[HeaderNames.Origin] = "https://evil.example";
        using MemoryStream responseBody = new();
        context.Response.Body = responseBody;

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        responseBody.Position = 0;
        using JsonDocument body = await JsonDocument.ParseAsync(responseBody);
        Assert.Equal("ORIGIN_NOT_ALLOWED", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Category", "Realtime")]
    public async Task AuthenticatedNonWebSocketRequestResolvesTheRealEndpointWithoutA500()
    {
        using TestConfigurationOverride configuration = new(dataRoot);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudEmuera:DataPath"] = dataRoot,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
            });
        });

        SessionLifecycleExecutor concrete = factory.Services.GetRequiredService<SessionLifecycleExecutor>();
        Assert.Same(concrete, factory.Services.GetRequiredService<ISessionLifecycleExecutor>());
        Assert.Same(concrete, factory.Services.GetRequiredService<ISessionCommandGate>());
        _ = factory.Services.GetRequiredService<RealtimeEndpoint>();

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/realtime");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("InvalidOperationException", await response.Content.ReadAsStringAsync());
    }

    public void Dispose()
    {
        factory?.Dispose();
        if (Directory.Exists(dataRoot))
            Directory.Delete(dataRoot, recursive: true);
    }

    private sealed class IncomingWebSocketFeature : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "RealtimeIntegrationTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.NameIdentifier, "usr_realtime_test"),
                new Claim(ClaimTypes.Role, "PLAYER"),
                new Claim("auth_session_id", "auths_realtime_test"),
                new Claim("security_stamp", "stamp_realtime_test"),
            ],
            SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
