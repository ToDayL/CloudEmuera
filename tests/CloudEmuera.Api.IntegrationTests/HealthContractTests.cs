using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CloudEmuera.Api.Bootstrap;
using CloudEmuera.Api.Health;
using CloudEmuera.Contracts;
using CloudEmuera.Infrastructure.Persistence;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class HealthContractTests
{
    [Fact]
    [Trait("Category", "Bootstrap")]
    public async Task LiveEndpointRemainsAvailableWhenBootstrapIsNotReady()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"ce-{Guid.NewGuid():N}");
        using TestConfigurationOverride configuration = new(dataRoot);
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        });
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health/live");
        Assert.True(response.IsSuccessStatusCode);
        Assert.StartsWith("req_", response.Headers.GetValues("X-Request-ID").Single(), StringComparison.Ordinal);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("LIVE", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(["status"], body.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Contains(factory.Services.GetServices<IHostedService>(), service => service is GamePackageIngestionReaperService);
        Directory.Delete(dataRoot, recursive: true);
    }

    [Fact]
    [Trait("Category", "Health")]
    public async Task VersionEndpointExposesFrozenCompatibilityFactsWithoutHostDetails()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"ce-{Guid.NewGuid():N}");
        using TestConfigurationOverride configuration = new(dataRoot);
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/version");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        VersionResponse version = await response.Content.ReadFromJsonAsync<VersionResponse>() ?? throw new Xunit.Sdk.XunitException("Version response was missing.");
        Assert.Equal("CloudEmuera", version.Product);
        Assert.Equal(1, version.HttpApiSchemaVersion);
        Assert.True(version.RealtimeEnvelopeVersion > 0);
        Assert.True(version.WorkerIpcMajor > 0);
        Assert.False(string.IsNullOrWhiteSpace(version.RuntimeIntegrationVersion));
        Assert.Equal(40, version.UpstreamCommit.Length);
        Assert.DoesNotContain(dataRoot, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("SessionRoot", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Directory.Delete(dataRoot, recursive: true);
    }

    [Fact]
    [Trait("Category", "Health")]
    public async Task ReadyEndpointReturnsStableOrderedChecksAfterBootstrapAndRecovery()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"ce-{Guid.NewGuid():N}");
        DatabaseMigrationRunner runner = new(new SqliteDatabaseOptions { DataRoot = dataRoot });
        Assert.Equal(MigrationExitCodes.Success, (await runner.MigrateAsync()).ExitCode);
        using TestConfigurationOverride configuration = new(dataRoot, includeBootstrap: true);
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("READY", body.RootElement.GetProperty("status").GetString());
        string[] checks = body.RootElement.GetProperty("checks").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(ReadinessHealthCheckNames.Ordered, checks);
        Assert.All(body.RootElement.GetProperty("checks").EnumerateArray(), item => Assert.Equal("READY", item.GetProperty("status").GetString()));
        Directory.Delete(dataRoot, recursive: true);
    }
}
