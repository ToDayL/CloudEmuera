using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Contracts.Games;
using CloudEmuera.Contracts.Identity;
using CloudEmuera.Contracts.Sessions;
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
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"ce-{Guid.NewGuid().ToString("N")[..16]}");
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
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

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/games/{game.Id}/search?scope=CURRENT&q=INPUT&limit=10")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/games/{game.Id}:edit", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/games/{game.Id}/workspace")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/api/v1/games/{game.Id}/file?path=ERB%2FNEW.ERB", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/games/{game.Id}/file?path=ERB%2FSTART.ERB")).StatusCode);

        HttpResponseMessage download = await client.GetAsync($"/api/v1/games/{game.Id}/download?scope=CURRENT&path=ERB%2FSTART.ERB");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.NotNull(download.Content.Headers.ContentDisposition);
    }

    [Fact]
    [Trait("Category", "SessionLifecycle")]
    public async Task SessionCreateOpenCloseAndReopenUseDurableHttpLifecycle()
    {
        await CreateDatabaseAsync();
        using TestConfigurationOverride configuration = new(_dataRoot, includeBootstrap: true);
        _factory = new IdentityFactory(_dataRoot, useKestrel: true);
        _factory.StartKestrel();
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = _factory.KestrelBaseAddress,
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

        string csrf = await GetCsrfAsync(client);
        Assert.True((await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/login", new LoginRequest("admin@example.test", "temporary-password", false), csrf)).IsSuccessStatusCode);
        csrf = await GetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/change-password", new ChangePasswordRequest("temporary-password", "administrator-password"), csrf)).StatusCode);

        csrf = await GetCsrfAsync(client);
        GameLibraryItem game = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("HTTP Session Fixture", "PRIVATE"), csrf))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Create game response was missing.");
        csrf = await GetCsrfAsync(client);
        using MemoryStream archive = CreateArchive();
        IngestedGamePackage package = await (await SendRawAsync(client, HttpMethod.Post, "/api/v1/game-package-ingestions", archive.ToArray(), "application/zip", csrf, "session-ingest"))
            .Content.ReadFromJsonAsync<IngestedGamePackage>() ?? throw new Xunit.Sdk.XunitException("Ingestion response was missing.");
        csrf = await GetCsrfAsync(client);
        GameLibraryItem draft = await (await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/games/{game.Id}/package",
            new BindGamePackageRequest(package.IngestionId, package.Manifest.ContentDigest), csrf, game.StateVersion, "session-bind"))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Bind response was missing.");
        csrf = await GetCsrfAsync(client);
        GameValidationResult validation = await (await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:validate", new { }, csrf, draft.StateVersion, "session-validate"))
            .Content.ReadFromJsonAsync<GameValidationResult>() ?? throw new Xunit.Sdk.XunitException("Validation response was missing.");
        Assert.True(validation.CanActivate, string.Join(',', validation.Diagnostics.Select(item => item.Code)));
        csrf = await GetCsrfAsync(client);
        GameLibraryItem current = await (await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:activate", new { }, csrf, validation.StateVersion, "session-activate"))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Activation response was missing.");

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage createdResponse = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/sessions",
            new CreateSessionRequest(current.Id, "HTTP Session"), csrf, idempotencyKey: "session-create");
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        SessionResponse created = await createdResponse.Content.ReadFromJsonAsync<SessionResponse>() ?? throw new Xunit.Sdk.XunitException("Session create response was missing.");
        Assert.Equal("CLOSED", created.State);
        Assert.NotNull(createdResponse.Headers.ETag);
        Assert.Equal($"/api/v1/sessions/{created.Id}", createdResponse.Headers.Location?.ToString());

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage createReplay = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/sessions",
            new CreateSessionRequest(current.Id, "HTTP Session"), csrf, idempotencyKey: "session-create");
        SessionResponse replayed = await createReplay.Content.ReadFromJsonAsync<SessionResponse>() ?? throw new Xunit.Sdk.XunitException("Session replay response was missing.");
        Assert.Equal(HttpStatusCode.Created, createReplay.StatusCode);
        Assert.Equal(created.Id, replayed.Id);

        // The same key is deliberately reused across create/open/close scopes;
        // scope separation must prevent a false idempotency conflict.
        (HttpResponseMessage openedResponse, SessionResponse opened) = await WaitForLifecycleAsync(client, created.Id, "open", "session-create");
        Assert.Equal("RUNNING", opened.State);
        Assert.Equal(1, opened.WorkerEpoch);

        (HttpResponseMessage closedResponse, SessionResponse closed) = await WaitForLifecycleAsync(client, created.Id, "close", "session-create");
        Assert.Equal("CLOSED", closed.State);

        csrf = await GetCsrfAsync(client);
        GameLibraryItem blockedGame = await (await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/admin/games/{current.Id}:block",
            new SetGameBlockedRequest(true), csrf, current.StateVersion))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Block response was missing.");
        Assert.Equal("BLOCKED", blockedGame.Status);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage blockedOpen = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/sessions/{created.Id}:open", new { }, csrf, idempotencyKey: "session-open-blocked");
        ApiError blockedOpenError = await blockedOpen.Content.ReadFromJsonAsync<ApiError>() ?? throw new Xunit.Sdk.XunitException("Blocked reopen error was missing.");
        Assert.Equal(HttpStatusCode.Conflict, blockedOpen.StatusCode);
        Assert.Equal("GAME_BLOCKED", blockedOpenError.Code);

        csrf = await GetCsrfAsync(client);
        GameLibraryItem unblockedGame = await (await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/admin/games/{current.Id}:block",
            new SetGameBlockedRequest(false), csrf, blockedGame.StateVersion))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Unblock response was missing.");
        Assert.Equal("ACTIVE", unblockedGame.Status);

        (HttpResponseMessage reopenedResponse, SessionResponse reopened) = await WaitForLifecycleAsync(client, created.Id, "open", "session-reopen");
        Assert.Equal("RUNNING", reopened.State);
        Assert.Equal(2, reopened.WorkerEpoch);

        (_, _) = await WaitForLifecycleAsync(client, created.Id, "close", "session-reclose");

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage secondCreateResponse = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/sessions",
            new CreateSessionRequest(current.Id, "HTTP Session 2"), csrf, idempotencyKey: "session-create-2");
        Assert.Equal(HttpStatusCode.Created, secondCreateResponse.StatusCode);

        HttpResponseMessage firstPageResponse = await client.GetAsync("/api/v1/sessions?limit=1");
        string firstPageBody = await firstPageResponse.Content.ReadAsStringAsync();
        Assert.True(firstPageResponse.StatusCode == HttpStatusCode.OK, $"Session list failed: {(int)firstPageResponse.StatusCode} {firstPageBody}");
        Assert.StartsWith("{", firstPageBody, StringComparison.Ordinal);
        SessionListResponse firstPage = JsonSerializer.Deserialize<SessionListResponse>(firstPageBody, WebJsonOptions) ?? throw new Xunit.Sdk.XunitException("Session list response was missing.");
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        HttpResponseMessage secondPageResponse = await client.GetAsync($"/api/v1/sessions?limit=1&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        SessionListResponse secondPage = await secondPageResponse.Content.ReadFromJsonAsync<SessionListResponse>() ?? throw new Xunit.Sdk.XunitException("Session cursor page was missing.");
        Assert.Single(secondPage.Items);
        Assert.NotEqual(firstPage.Items[0].Id, secondPage.Items[0].Id);

        async Task<(HttpResponseMessage Response, SessionResponse Value)> WaitForLifecycleAsync(
            HttpClient httpClient,
            string sessionId,
            string operation,
            string key)
        {
            string initialCsrf = await GetCsrfAsync(httpClient);
            HttpResponseMessage initial = await SendJsonAsync(httpClient, HttpMethod.Post,
                $"/api/v1/sessions/{sessionId}:{operation}", new { }, initialCsrf, idempotencyKey: key);
            SessionResponse initialValue = await initial.Content.ReadFromJsonAsync<SessionResponse>() ?? throw new Xunit.Sdk.XunitException($"Session {operation} response was missing.");
            if (initial.StatusCode == HttpStatusCode.OK)
                return (initial, initialValue);
            Assert.Equal(HttpStatusCode.Accepted, initial.StatusCode);

            string expectedState = operation == "open" ? "RUNNING" : "CLOSED";
            List<string> attempts = [$"{(int)initial.StatusCode}:{initialValue.State}:epoch={initialValue.WorkerEpoch}"];
            for (int attempt = 0; attempt < 20; attempt++)
            {
                HttpResponseMessage detailResponse = await httpClient.GetAsync($"/api/v1/sessions/{sessionId}");
                SessionResponse detail = await detailResponse.Content.ReadFromJsonAsync<SessionResponse>() ?? throw new Xunit.Sdk.XunitException($"Session detail response was missing.");
                attempts.Add($"detail:{detail.State}:epoch={detail.WorkerEpoch}");
                if (detail.State == expectedState)
                {
                    string requestCsrf = await GetCsrfAsync(httpClient);
                    HttpResponseMessage response = await SendJsonAsync(httpClient, HttpMethod.Post,
                        $"/api/v1/sessions/{sessionId}:{operation}", new { }, requestCsrf, idempotencyKey: key);
                    SessionResponse value = await response.Content.ReadFromJsonAsync<SessionResponse>() ?? throw new Xunit.Sdk.XunitException($"Session {operation} replay response was missing.");
                    if (response.StatusCode == HttpStatusCode.OK)
                        return (response, value);
                    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                }
                await Task.Delay(250);
            }

            throw new Xunit.Sdk.XunitException($"Session {operation} did not complete within the integration-test deadline: {string.Join(',', attempts)}");
        }
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
            // Multiple top-level entries are never flattened, so the structure
            // check still rejects this package because ERB/CSV are not at the root.
            ZipArchiveEntry entry = archive.CreateEntry("README.txt");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("readme\n");
            entry = archive.CreateEntry("game-folder/CSV/GAMEBASE.CSV");
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

    private static MemoryStream CreateSingleRootCaseVariantArchive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The user-reported distribution: a single top-level wrapper folder and
            // a case-variant GAMEBASE.CSV. Flattening + the fixed-case alias must
            // make the package validate and activate through the real parser.
            ZipArchiveEntry entry = archive.CreateEntry("eraJK-wrapper/CSV/GameBase.csv");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("title,validator-test\n");
            entry = archive.CreateEntry("eraJK-wrapper/ERB/START.ERB");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("@SYSTEM_TITLE\nINPUT\nQUIT\n");
            entry = archive.CreateEntry("eraJK-wrapper/emuera.config");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("Use sav folder:NO\n");
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task SingleRootFolderWithCaseVariantGameBaseFlattensAndActivates()
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
        GameLibraryItem game = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("Flattened Fixture", "PRIVATE"), csrf))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Create game response was missing.");
        csrf = await GetCsrfAsync(client);
        using MemoryStream archive = CreateSingleRootCaseVariantArchive();
        IngestedGamePackage package = await (await SendRawAsync(client, HttpMethod.Post, "/api/v1/game-package-ingestions", archive.ToArray(), "application/zip", csrf, $"ingest-{Guid.NewGuid():N}"))
            .Content.ReadFromJsonAsync<IngestedGamePackage>() ?? throw new Xunit.Sdk.XunitException("Ingestion response was missing.");
        Assert.Contains(package.Manifest.Files, file => file.Path == "ERB/START.ERB");
        Assert.Contains(package.Manifest.Files, file => file.Path == "CSV/GameBase.csv");
        Assert.DoesNotContain(package.Manifest.Files, file => file.Path.StartsWith("eraJK-wrapper/", StringComparison.Ordinal));

        csrf = await GetCsrfAsync(client);
        GameLibraryItem draft = await (await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/games/{game.Id}/package",
            new BindGamePackageRequest(package.IngestionId, package.Manifest.ContentDigest), csrf, game.StateVersion, $"bind-{Guid.NewGuid():N}"))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Bind response was missing.");
        Assert.Equal("DRAFT", draft.WorkspaceStatus);

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
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task CreateAfterDeleteReusesTheName()
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
        GameLibraryItem first = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("Reusable Name", "PRIVATE"), csrf))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Create game response was missing.");

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage duplicate = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("Reusable Name", "PRIVATE"), csrf);
        ApiError conflict = await duplicate.Content.ReadFromJsonAsync<ApiError>() ?? throw new Xunit.Sdk.XunitException("Conflict error was missing.");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("GAME_NAME_CONFLICT", conflict.Code);

        csrf = await GetCsrfAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await SendJsonAsync(client, HttpMethod.Delete, $"/api/v1/games/{first.Id}", new { }, csrf, first.StateVersion)).StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage recreated = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("Reusable Name", "PRIVATE"), csrf);
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
        GameLibraryItem second = await recreated.Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Recreated game response was missing.");
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task Utf16TextFileConvertsOnIngestionAndValidates()
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
        GameLibraryItem game = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games", new CreateGameRequest("Utf16 Fixture", "PRIVATE"), csrf))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Create game response was missing.");
        csrf = await GetCsrfAsync(client);
        using MemoryStream archive = CreateUtf16Archive();
        IngestedGamePackage package = await (await SendRawAsync(client, HttpMethod.Post, "/api/v1/game-package-ingestions", archive.ToArray(), "application/zip", csrf, $"ingest-{Guid.NewGuid():N}"))
            .Content.ReadFromJsonAsync<IngestedGamePackage>() ?? throw new Xunit.Sdk.XunitException("Ingestion response was missing.");
        Assert.DoesNotContain(package.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_UTF16_OR_UTF32_UNSUPPORTED");
        Assert.Contains(package.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_ENCODING_CONVERTED");
        Assert.Contains(package.Manifest.Files, file => file.Path == "ERB/START.ERB" && file.Encoding == GamePackageTextEncoding.Utf8);

        csrf = await GetCsrfAsync(client);
        GameLibraryItem draft = await (await SendJsonAsync(client, HttpMethod.Put, $"/api/v1/games/{game.Id}/package",
            new BindGamePackageRequest(package.IngestionId, package.Manifest.ContentDigest), csrf, game.StateVersion, $"bind-{Guid.NewGuid():N}"))
            .Content.ReadFromJsonAsync<GameLibraryItem>() ?? throw new Xunit.Sdk.XunitException("Bind response was missing.");
        csrf = await GetCsrfAsync(client);
        HttpResponseMessage validated = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:validate", new { }, csrf, draft.StateVersion, $"validate-{Guid.NewGuid():N}");
        GameValidationResult validation = await validated.Content.ReadFromJsonAsync<GameValidationResult>() ?? throw new Xunit.Sdk.XunitException("Validation response was missing.");
        Assert.True(validation.CanActivate, string.Join(',', validation.Diagnostics.Select(item => item.Code)));

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage activated = await SendJsonAsync(client, HttpMethod.Post, $"/api/v1/games/{game.Id}:activate", new { }, csrf, validation.StateVersion, $"activate-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
    }

    private static MemoryStream CreateUtf16Archive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            byte[] utf16Erb = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("@SYSTEM_TITLE\nINPUT\nQUIT\n")];
            ZipArchiveEntry entry = archive.CreateEntry("ERB/START.ERB");
            using (Stream writer = entry.Open()) writer.Write(utf16Erb, 0, utf16Erb.Length);
            entry = archive.CreateEntry("CSV/GAMEBASE.CSV");
            using (Stream writer = entry.Open())
            using (var text = new StreamWriter(writer))
                text.Write("title,validator-test\n");
            entry = archive.CreateEntry("emuera.config");
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

    private sealed class IdentityFactory(string dataRoot, bool useKestrel = false) : WebApplicationFactory<Program>
    {
        private readonly int _kestrelPort = GetFreeTcpPort();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["CloudEmuera:DataPath"] = dataRoot }));
        }

        public void StartKestrel()
        {
            if (!useKestrel)
                throw new InvalidOperationException("This factory was not configured for Kestrel.");
            ClientOptions.BaseAddress = new Uri($"http://127.0.0.1:{_kestrelPort}", UriKind.Absolute);
            UseKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, _kestrelPort);
            });
            StartServer();
        }

        public Uri KestrelBaseAddress => new($"http://127.0.0.1:{_kestrelPort}", UriKind.Absolute);

        private static int GetFreeTcpPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed record GameFileListResponse(IReadOnlyList<GameFileItem> Items);
    private sealed record GameDiagnosticListResponse(IReadOnlyList<GameDiagnosticItem> Items);
    private sealed record CsrfResponse(string Token);
}
