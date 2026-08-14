using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Application.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class RealtimeEndpointContractTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ce-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program>? factory;

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
                ["CloudEmuera:PublicOrigin"] = "http://localhost:5173",
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
