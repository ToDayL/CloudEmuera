using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Contracts.Games;
using CloudEmuera.Contracts.Identity;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

/// <summary>GAME-004~007/009/010: exercise the P1-04 single-Game HTTP boundary
/// end to end, including the one-shot parser Validator process that the browser
/// UI drives through validate/activate.</summary>
public sealed class GameLibraryApiContractTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"cloudemuera-game-api-{Guid.NewGuid():N}");
    private IdentityFactory? _factory;

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task CreateIngestBindValidateActivateFlowWorksOverHttp()
    {
        await CreateDatabaseAsync();
        using TestConfigurationOverride configuration = new(_dataRoot, includeBootstrap: true);
        _factory = new IdentityFactory(_dataRoot);
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

        string csrf = await GetCsrfAsync(client);
        HttpResponseMessage login = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/login", new LoginRequest("admin@example.test", "temporary-password", false), csrf);
        Assert.True(login.IsSuccessStatusCode);
        csrf = await GetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/change-password", new ChangePasswordRequest("temporary-password", "administrator-password"), csrf)).StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage created = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("HTTP Fixture", "PRIVATE"), csrf);
        GameLibraryItem game = await created.Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Create game response was missing.");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("NONE", game.WorkspaceStatus);
        Assert.False(game.HasCurrentContent);

        csrf = await GetCsrfAsync(client);
        using MemoryStream archive = CreateArchive();
        HttpResponseMessage ingested = await SendRawAsync(client, HttpMethod.Post, "/api/v1/game-package-ingestions", archive.ToArray(), "application/zip", csrf, $"ingest-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, ingested.StatusCode);
        IngestedGamePackage package = await ingested.Content.ReadFromJsonAsync<IngestedGamePackage>() ?? throw new Xunit.Sdk.XunitException("Ingestion response was missing.");
        Assert.Equal(3, package.Manifest.FileCount);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage bound = await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/games/{game.Id}/package",
            new BindGamePackageRequest(package.IngestionId, package.Manifest.ContentDigest), csrf, game.StateVersion, $"bind-{Guid.NewGuid():N}");
        GameLibraryItem draft = await bound.Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Bind response was missing.");
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
        Assert.Equal("DRAFT", draft.WorkspaceStatus);

        HttpResponseMessage files = await client.GetAsync($"/api/v1/games/{game.Id}/files?scope=WORKSPACE");
        GameFileListResponse listing = await files.Content.ReadFromJsonAsync<GameFileListResponse>() ?? throw new Xunit.Sdk.XunitException("File list was missing.");
        Assert.Contains(listing.Items, item => item.Path == "CSV" && item.IsDirectory);
        Assert.Contains(listing.Items, item => item.Path == "ERB" && item.IsDirectory);
        HttpResponseMessage erbListingResponse = await client.GetAsync($"/api/v1/games/{game.Id}/files?scope=WORKSPACE&path=ERB");
        GameFileListResponse erbListing = await erbListingResponse.Content.ReadFromJsonAsync<GameFileListResponse>() ?? throw new Xunit.Sdk.XunitException("ERB file list was missing.");
        Assert.Contains(erbListing.Items, item => item.Path == "ERB/START.ERB");

        HttpResponseMessage text = await client.GetAsync($"/api/v1/games/{game.Id}/file?scope=WORKSPACE&path=ERB%2FSTART.ERB");
        GameTextFile startFile = await text.Content.ReadFromJsonAsync<GameTextFile>() ?? throw new Xunit.Sdk.XunitException("Text file was missing.");
        Assert.Equal("@SYSTEM_TITLE\nINPUT\nQUIT\n", startFile.Content);
        Assert.NotNull(text.Headers.ETag);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage validated = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:validate", new { }, csrf, draft.StateVersion, $"validate-{Guid.NewGuid():N}");
        GameValidationResult validation = await validated.Content.ReadFromJsonAsync<GameValidationResult>() ?? throw new Xunit.Sdk.XunitException("Validation response was missing.");
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        Assert.True(validation.CanActivate, string.Join(',', validation.Diagnostics.Select(item => item.Code)));

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage activated = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:activate", new { }, csrf, validation.StateVersion, $"activate-{Guid.NewGuid():N}");
        GameLibraryItem current = await activated.Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Activate response was missing.");
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.True(current.HasCurrentContent);
        Assert.Equal("NONE", current.WorkspaceStatus);
        Assert.Equal(1, current.ContentRevision);

        HttpResponseMessage currentFiles = await client.GetAsync($"/api/v1/games/{game.Id}/files?scope=CURRENT");
        GameFileListResponse currentListing = await currentFiles.Content.ReadFromJsonAsync<GameFileListResponse>() ?? throw new Xunit.Sdk.XunitException("Current file list was missing.");
        Assert.Contains(currentListing.Items, item => item.Path == "ERB" && item.IsDirectory);
        HttpResponseMessage currentErbResponse = await client.GetAsync($"/api/v1/games/{game.Id}/files?scope=CURRENT&path=ERB");
        GameFileListResponse currentErb = await currentErbResponse.Content.ReadFromJsonAsync<GameFileListResponse>() ?? throw new Xunit.Sdk.XunitException("Current ERB file list was missing.");
        Assert.Contains(currentErb.Items, item => item.Path == "ERB/START.ERB");

        HttpResponseMessage search = await client.GetAsync($"/api/v1/games/{game.Id}/search?scope=CURRENT&q=INPUT&limit=10");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        GameSearchPageResponse searchPage = await search.Content.ReadFromJsonAsync<GameSearchPageResponse>() ?? throw new Xunit.Sdk.XunitException("Search response was missing.");
        Assert.NotNull(searchPage.Items);
        Assert.NotEmpty(searchPage.Items);

        HttpResponseMessage download = await client.GetAsync($"/api/v1/games/{game.Id}/download?scope=CURRENT&path=ERB%2FSTART.ERB");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.NotNull(download.Content.Headers.ContentDisposition);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task FailedActivationPersistsReadableBlockingDiagnostics()
    {
        await CreateDatabaseAsync();
        using TestConfigurationOverride configuration = new(_dataRoot, includeBootstrap: true);
        _factory = new IdentityFactory(_dataRoot);
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

        string csrf = await GetCsrfAsync(client);
        Assert.True((await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/login", new LoginRequest("admin@example.test", "temporary-password", false), csrf)).IsSuccessStatusCode);
        csrf = await GetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/change-password", new ChangePasswordRequest("temporary-password", "administrator-password"), csrf)).StatusCode);

        csrf = await GetCsrfAsync(client);
        GameLibraryItem game = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("Nested Folder Fixture", "PRIVATE"), csrf))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Create game response was missing.");
        csrf = await GetCsrfAsync(client);
        using MemoryStream nestedArchive = CreateNestedFolderArchive();
        IngestedGamePackage package = await (await SendRawAsync(client, HttpMethod.Post, "/api/v1/game-package-ingestions", nestedArchive.ToArray(), "application/zip", csrf, $"ingest-{Guid.NewGuid():N}"))
            .Content.ReadFromJsonAsync<IngestedGamePackage>() ?? throw new Xunit.Sdk.XunitException("Ingestion response was missing.");
        csrf = await GetCsrfAsync(client);
        GameLibraryItem draft = await (await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/games/{game.Id}/package",
            new BindGamePackageRequest(package.IngestionId, package.Manifest.ContentDigest), csrf, game.StateVersion, $"bind-{Guid.NewGuid():N}"))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Bind response was missing.");
        Assert.Equal("DRAFT", draft.WorkspaceStatus);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage validated = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:validate", new { }, csrf, draft.StateVersion, $"validate-{Guid.NewGuid():N}");
        GameValidationResult validation = await validated.Content.ReadFromJsonAsync<GameValidationResult>() ?? throw new Xunit.Sdk.XunitException("Validation response was missing.");
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        Assert.False(validation.CanActivate);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "ERB_ENTRYPOINT_MISSING" && diagnostic.ActivationBlocking);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage activated = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:activate", new { }, csrf, validation.StateVersion, $"activate-{Guid.NewGuid():N}");
        ApiError error = await activated.Content.ReadFromJsonAsync<ApiError>() ?? throw new Xunit.Sdk.XunitException("Activation error was missing.");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, activated.StatusCode);
        Assert.Equal("ACTIVATION_VALIDATION_FAILED", error.Code);

        HttpResponseMessage diagnostics = await client.GetAsync($"/api/v1/games/{game.Id}/diagnostics");
        GameDiagnosticListResponse listing = await diagnostics.Content.ReadFromJsonAsync<GameDiagnosticListResponse>() ?? throw new Xunit.Sdk.XunitException("Diagnostics response was missing.");
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        GameDiagnosticItem entrypoint = Assert.Single(listing.Items, item => item.Code == "ERB_ENTRYPOINT_MISSING");
        Assert.True(entrypoint.ActivationBlocking);
        Assert.False(string.IsNullOrWhiteSpace(entrypoint.Message));
    }

    private static MemoryStream CreateNestedFolderArchive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // A single top-level wrapper folder is a common distribution layout; the
            // controlled structure check rejects it because ERB/CSV are not at the root.
            ZipArchiveEntry entry = archive.CreateEntry("game-folder/CSV/GAMEBASE.CSV");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("title,validator-test\n");
            entry = archive.CreateEntry("game-folder/ERB/START.ERB");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("@SYSTEM_TITLE\nINPUT\nQUIT\n");
            entry = archive.CreateEntry("game-folder/emuera.config");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("Use sav folder:NO\n");
        }
        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        try
        {
            // Activated current content is read-only; allow cleanup to rewrite it.
            if (Directory.Exists(_dataRoot))
            {
                MakeWritable(_dataRoot);
                Directory.Delete(_dataRoot, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not hide the assertion or migration failure.
        }
    }

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        if (File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path)) MakeWritable(entry);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead);
    }

    private static MemoryStream CreateArchive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("CSV/GAMEBASE.CSV");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("title,validator-test\n");
            entry = archive.CreateEntry("ERB/START.ERB");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("@SYSTEM_TITLE\nINPUT\nQUIT\n");
            entry = archive.CreateEntry("emuera.config");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("Use sav folder:NO\n");
        }
        stream.Position = 0;
        return stream;
    }

    private async Task CreateDatabaseAsync()
    {
        DatabaseMigrationRunner runner = new(new SqliteDatabaseOptions { DataRoot = _dataRoot });
        MigrationResult result = await runner.MigrateAsync();
        Assert.Equal(MigrationExitCodes.Success, result.ExitCode);
    }

    private static async Task<string> GetCsrfAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfResponse>() ?? throw new Xunit.Sdk.XunitException("CSRF response was missing.")).Token;

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        T body,
        string csrf,
        int? stateVersion = null,
        string? idempotencyKey = null)
    {
        using HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (stateVersion is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{stateVersion}\"");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        byte[] body,
        string contentType,
        string csrf,
        string idempotencyKey)
    {
        using HttpRequestMessage request = new(method, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private sealed class IdentityFactory(string dataRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        }
    }

    private sealed record GameFileListResponse(IReadOnlyList<GameFileItem> Items);
    private sealed record GameSearchPageResponse(IReadOnlyList<GameSearchMatch> Items, string? NextCursor);
    private sealed record GameDiagnosticListResponse(IReadOnlyList<GameDiagnosticItem> Items);
    private sealed record CsrfResponse(string Token);
}
