using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

/// <summary>ADR-0010: the live OpenAPI document exposes the single Game content
/// contract and never advertises GameVersion resources.</summary>
public sealed class OpenApiContractTests
{
    [Fact]
    [Trait("Category", "Bootstrap")]
    public async Task OpenApiDocumentExposesGameContractWithoutGameVersion()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"cloudemuera-openapi-{Guid.NewGuid():N}");
        using TestConfigurationOverride configuration = new(dataRoot);
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        });
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/games", out _), "OpenAPI must describe the games collection.");
        Assert.True(paths.TryGetProperty("/api/v1/games/{id}", out _), "OpenAPI must describe the game item route.");
        Assert.False(paths.TryGetProperty("/api/v1/game-versions", out _), "OpenAPI must not advertise game-version routes.");
        Assert.DoesNotContain("GameVersion", json);
        Assert.DoesNotContain("game_version", json);
        Directory.Delete(dataRoot, recursive: true);
    }
}
