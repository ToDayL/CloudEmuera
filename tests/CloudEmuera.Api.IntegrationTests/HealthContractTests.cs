using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CloudEmuera.Api.Bootstrap;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class HealthContractTests
{
    [Fact]
    [Trait("Category", "Bootstrap")]
    public async Task LiveEndpointRemainsAvailableWhenBootstrapIsNotReady()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"cloudemuera-health-{Guid.NewGuid():N}");
        using TestConfigurationOverride configuration = new(dataRoot);
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        });
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health/live");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains(factory.Services.GetServices<IHostedService>(), service => service is GamePackageIngestionReaperService);
        Directory.Delete(dataRoot, recursive: true);
    }
}
