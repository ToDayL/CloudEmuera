using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using CloudEmuera.Contracts.Identity;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

/// <summary>AUTH-001/006: exercise the HTTP boundary with a migrated, isolated SQLite instance.</summary>
public sealed class IdentityApiContractTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"cloudemuera-identity-api-{Guid.NewGuid():N}");
    private IdentityFactory? _factory;

    [Fact]
    [Trait("Category", "IdentityApi")]
    public async Task EmailLoginPasswordChangeAndAdminUserManagementUseRealCookieAndCsrfProtection()
    {
        await CreateDatabaseAsync();
        using TestConfigurationOverride configuration = new(_dataRoot, includeBootstrap: true);
        _factory = new IdentityFactory(_dataRoot);
        using HttpClient anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@example.test", "temporary-password", false))).StatusCode);

        string csrf = await GetCsrfAsync(anonymous);
        HttpResponseMessage login = await SendJsonAsync(anonymous, HttpMethod.Post, "/api/v1/auth/login", new LoginRequest("admin@example.test", "temporary-password", false), csrf);
        CurrentUserResponse bootstrapAdmin = await login.Content.ReadFromJsonAsync<CurrentUserResponse>() ?? throw new Xunit.Sdk.XunitException("Login response was missing.");
        Assert.True(login.IsSuccessStatusCode);
        Assert.True(bootstrapAdmin.MustChangePassword);
        Assert.Equal("ADMIN", bootstrapAdmin.Role);
        Assert.Equal("ACTIVE", bootstrapAdmin.Status);

        csrf = await GetCsrfAsync(anonymous);
        HttpResponseMessage changed = await SendJsonAsync(anonymous, HttpMethod.Post, "/api/v1/auth/change-password", new ChangePasswordRequest("temporary-password", "administrator-password"), csrf);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        CurrentUserResponse current = await (await anonymous.GetAsync("/api/v1/auth/me")).Content.ReadFromJsonAsync<CurrentUserResponse>() ?? throw new Xunit.Sdk.XunitException("Current user response was missing.");
        Assert.False(current.MustChangePassword);

        csrf = await GetCsrfAsync(anonymous);
        HttpResponseMessage created = await SendJsonAsync(anonymous, HttpMethod.Post, "/api/v1/admin/users", new CreateUserRequest("player-one", "player@example.test", "player-temporary-password", "PLAYER", null), csrf);
        CurrentUserResponse player = await created.Content.ReadFromJsonAsync<CurrentUserResponse>() ?? throw new Xunit.Sdk.XunitException("Create user response was missing.");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(player.MustChangePassword);

        using HttpClient playerClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        string playerCsrf = await GetCsrfAsync(playerClient);
        HttpResponseMessage playerLogin = await SendJsonAsync(playerClient, HttpMethod.Post, "/api/v1/auth/login", new LoginRequest("player@example.test", "player-temporary-password", false), playerCsrf);
        Assert.True(playerLogin.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await playerClient.GetAsync("/api/v1/admin/users")).StatusCode);

        csrf = await GetCsrfAsync(anonymous);
        HttpResponseMessage disabled = await SendJsonAsync(anonymous, HttpMethod.Patch, $"/api/v1/admin/users/{player.Id}", new UpdateUserRequest(null, null, null, "DISABLED"), csrf, player.StateVersion);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await playerClient.GetAsync("/api/v1/auth/me")).StatusCode);

        await using var auditConnection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_dataRoot, SqliteStorageConventions.DatabaseFileName) }.ToString());
        await auditConnection.OpenAsync();
        Assert.True(await ScalarLongAsync(auditConnection, "SELECT COUNT(*) FROM audit_events WHERE request_id IS NOT NULL;") >= 5);
    }

    [Fact]
    [Trait("Category", "Bootstrap")]
    public async Task ConcurrentHostsCreateOneBootstrapAdminAndBothBecomeReady()
    {
        await CreateDatabaseAsync();
        using TestConfigurationOverride configuration = new(_dataRoot, includeBootstrap: true);
        using IdentityFactory first = new(_dataRoot);
        using IdentityFactory second = new(_dataRoot);
        using HttpClient firstClient = first.CreateClient();
        using HttpClient secondClient = second.CreateClient();

        HttpResponseMessage[] responses = await Task.WhenAll(firstClient.GetAsync("/health/ready"), secondClient.GetAsync("/health/ready"));

        Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode));
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_dataRoot, SqliteStorageConventions.DatabaseFileName) }.ToString());
        await connection.OpenAsync();
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM users WHERE role = 'ADMIN';"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM quota_profiles WHERE name = 'Default';"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM audit_events WHERE action = 'SYSTEM_ADMIN_BOOTSTRAPPED';"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM instance_state WHERE id = 1 AND bootstrap_status = 'COMPLETED';"));
    }

    [Fact]
    [Trait("Category", "Bootstrap")]
    public async Task CompletedBootstrapIgnoresRemovedConfigurationAndDoesNotReopenWithoutAnActiveAdmin()
    {
        await CreateDatabaseAsync();
        using (TestConfigurationOverride configured = new(_dataRoot, includeBootstrap: true))
        using (IdentityFactory first = new(_dataRoot))
        using (HttpClient firstClient = first.CreateClient())
            Assert.True((await firstClient.GetAsync("/health/ready")).IsSuccessStatusCode);

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_dataRoot, SqliteStorageConventions.DatabaseFileName) }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET role = 'PLAYER', status = 'DISABLED', state_version = state_version + 1;";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        using TestConfigurationOverride removed = new(_dataRoot);
        using IdentityFactory restarted = new(_dataRoot);
        using HttpClient restartedClient = restarted.CreateClient();
        Assert.True((await restartedClient.GetAsync("/health/ready")).IsSuccessStatusCode);

        await using var verify = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_dataRoot, SqliteStorageConventions.DatabaseFileName) }.ToString());
        await verify.OpenAsync();
        Assert.Equal(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM users WHERE role = 'ADMIN' AND status = 'ACTIVE';"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM quota_profiles WHERE name = 'Default';"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM audit_events WHERE action = 'SYSTEM_ADMIN_BOOTSTRAPPED';"));
    }

    [Fact]
    [Trait("Category", "Authentication")]
    public async Task AuthenticationFailuresAreUniformAndLoginRateLimitIsBounded()
    {
        await CreateDatabaseAsync();
        using TestConfigurationOverride configuration = new(_dataRoot, includeBootstrap: true);
        using IdentityFactory factory = new(_dataRoot);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        string csrf = await GetCsrfAsync(client);

        ApiError wrong = await FailureAsync(client, "admin@example.test", "wrong-password", csrf, HttpStatusCode.Unauthorized);
        ApiError unknown = await FailureAsync(client, "unknown@example.test", "temporary-password", csrf, HttpStatusCode.Unauthorized);
        ApiError username = await FailureAsync(client, "identity-admin", "temporary-password", csrf, HttpStatusCode.Unauthorized);
        await ExecuteSqlAsync("UPDATE users SET status = 'DISABLED' WHERE normalized_email = 'ADMIN@EXAMPLE.TEST';");
        ApiError disabled = await FailureAsync(client, "admin@example.test", "temporary-password", csrf, HttpStatusCode.Unauthorized);
        await ExecuteSqlAsync("UPDATE users SET status = 'ACTIVE', lockout_end = 4102444800000 WHERE normalized_email = 'ADMIN@EXAMPLE.TEST';");
        ApiError locked = await FailureAsync(client, "admin@example.test", "temporary-password", csrf, HttpStatusCode.Unauthorized);

        Assert.All([unknown, username, disabled, locked], value =>
        {
            Assert.Equal(wrong.Code, value.Code);
            Assert.Equal(wrong.Message, value.Message);
        });

        for (int attempt = 0; attempt < 10; attempt++)
            await FailureAsync(client, "throttled@example.test", "wrong-password", csrf, HttpStatusCode.Unauthorized);
        ApiError limited = await FailureAsync(client, "throttled@example.test", "wrong-password", csrf, HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_ATTEMPTS", limited.Code);
    }

    [Fact]
    [Trait("Category", "Authentication")]
    public async Task CookieSessionSurvivesHostRestartAndLogoutRevocationRejectsReplay()
    {
        await CreateDatabaseAsync();
        string authenticationCookie;
        using (TestConfigurationOverride configuration = new(_dataRoot, includeBootstrap: true))
        using (IdentityFactory first = new(_dataRoot))
        using (HttpClient firstClient = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }))
        {
            (string csrf, string antiforgeryCookie) = await GetManualCsrfAsync(firstClient, null);
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new LoginRequest("admin@example.test", "temporary-password", true)) };
            request.Headers.Add("Cookie", antiforgeryCookie);
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            using HttpResponseMessage login = await firstClient.SendAsync(request);
            Assert.True(login.IsSuccessStatusCode);
            authenticationCookie = CookiePair(login, "CloudEmuera.Dev.Session");
            string setCookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("CloudEmuera.Dev.Session=", StringComparison.Ordinal));
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        }

        using TestConfigurationOverride removed = new(_dataRoot);
        using IdentityFactory restarted = new(_dataRoot);
        using HttpClient client = restarted.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        using HttpRequestMessage me = new(HttpMethod.Get, "/api/v1/auth/me");
        me.Headers.Add("Cookie", authenticationCookie);
        Assert.True((await client.SendAsync(me)).IsSuccessStatusCode);

        (string logoutCsrf, string antiforgery) = await GetManualCsrfAsync(client, authenticationCookie);
        using HttpRequestMessage logout = new(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", $"{authenticationCookie}; {antiforgery}");
        logout.Headers.Add("X-CSRF-TOKEN", logoutCsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);
        using HttpRequestMessage replay = new(HttpMethod.Get, "/api/v1/auth/me");
        replay.Headers.Add("Cookie", authenticationCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(replay)).StatusCode);

        (string repeatedCsrf, string repeatedAntiforgery) = await GetManualCsrfAsync(client, null);
        using HttpRequestMessage repeated = new(HttpMethod.Post, "/api/v1/auth/logout");
        repeated.Headers.Add("Cookie", repeatedAntiforgery);
        repeated.Headers.Add("X-CSRF-TOKEN", repeatedCsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(repeated)).StatusCode);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true);
    }

    private async Task CreateDatabaseAsync()
    {
        DatabaseMigrationRunner runner = new(new SqliteDatabaseOptions { DataRoot = _dataRoot });
        MigrationResult result = await runner.MigrateAsync();
        Assert.Equal(MigrationExitCodes.Success, result.ExitCode);
    }

    private static async Task<string> GetCsrfAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfResponse>() ?? throw new Xunit.Sdk.XunitException("CSRF response was missing.")).Token;

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(HttpClient client, HttpMethod method, string path, T body, string csrf, int? stateVersion = null)
    {
        using HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (stateVersion is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{stateVersion}\"");
        return await client.SendAsync(request);
    }

    private static async Task<ApiError> FailureAsync(HttpClient client, string email, string password, string csrf, HttpStatusCode expected)
    {
        using HttpResponseMessage response = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(email, password, false), csrf);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<ApiError>() ?? throw new Xunit.Sdk.XunitException("Error response was missing.");
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_dataRoot, SqliteStorageConventions.DatabaseFileName) }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string Token, string Cookie)> GetManualCsrfAsync(HttpClient client, string? authenticationCookie)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf");
        if (authenticationCookie is not null) request.Headers.Add("Cookie", authenticationCookie);
        using HttpResponseMessage response = await client.SendAsync(request);
        CsrfResponse csrf = await response.Content.ReadFromJsonAsync<CsrfResponse>() ?? throw new Xunit.Sdk.XunitException("CSRF response was missing.");
        return (csrf.Token, CookiePair(response, ".AspNetCore.Antiforgery"));
    }

    private static string CookiePair(HttpResponseMessage response, string prefix)
    {
        string value = response.Headers.GetValues("Set-Cookie").Single(header => header.StartsWith(prefix, StringComparison.Ordinal));
        return value[..value.IndexOf(';')];
    }

    private static async Task<long> ScalarLongAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed class IdentityFactory(string dataRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        }
    }
}
