using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Application.Games;
using CloudEmuera.Contracts.Games;
using CloudEmuera.Contracts.Identity;
using CloudEmuera.Contracts.Saves;
using CloudEmuera.Contracts.Sessions;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

[Trait("Category", "SessionSaves")]
public sealed class SessionSaveApiContractTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), $"ce-sv-{Guid.NewGuid():N}"[..23]);
    private IdentityFactory? factory;

    [Theory]
    [InlineData(false, "ROOT")]
    [InlineData(true, "SAV_DIRECTORY")]
    public async Task ClosedSessionSupportsListDownloadReplaceRenameAndConfirmedDelete(bool useSavFolder, string expectedLayout)
    {
        await CreateDatabaseAsync();
        using TestConfigurationOverride configuration = new(dataRoot, includeBootstrap: true);
        factory = new IdentityFactory(dataRoot);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

        await LoginAsync(client);
        string csrf = await GetCsrfAsync(client);
        GameLibraryItem game = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/games",
            new CreateGameRequest("Session Save Fixture", "PRIVATE"), csrf))
            .Content.ReadFromJsonAsync<GameLibraryItem>()
            ?? throw new Xunit.Sdk.XunitException("Game create response was missing.");

        csrf = await GetCsrfAsync(client);
        using MemoryStream archive = CreateArchive(useSavFolder);
        IngestedGamePackage package = await (await SendRawAsync(client, HttpMethod.Post,
            "/api/v1/game-package-ingestions", archive.ToArray(), "application/zip", csrf, "save-ingest"))
            .Content.ReadFromJsonAsync<IngestedGamePackage>()
            ?? throw new Xunit.Sdk.XunitException("Package ingestion response was missing.");

        csrf = await GetCsrfAsync(client);
        GameLibraryItem draft = await (await SendJsonAsync(client, HttpMethod.Put,
            $"/api/v1/games/{game.Id}/package",
            new BindGamePackageRequest(package.IngestionId, package.Manifest.ContentDigest), csrf,
            game.StateVersion, "save-bind"))
            .Content.ReadFromJsonAsync<GameLibraryItem>()
            ?? throw new Xunit.Sdk.XunitException("Game bind response was missing.");

        csrf = await GetCsrfAsync(client);
        GameValidationResult validation = await (await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/games/{game.Id}:validate", new { }, csrf, draft.StateVersion, "save-validate"))
            .Content.ReadFromJsonAsync<GameValidationResult>()
            ?? throw new Xunit.Sdk.XunitException("Validation response was missing.");
        Assert.True(validation.CanActivate, string.Join(',', validation.Diagnostics.Select(item => item.Code)));

        csrf = await GetCsrfAsync(client);
        GameLibraryItem current = await (await SendJsonAsync(client, HttpMethod.Post,
            $"/api/v1/games/{game.Id}:activate", new { }, csrf, validation.StateVersion, "save-activate"))
            .Content.ReadFromJsonAsync<GameLibraryItem>()
            ?? throw new Xunit.Sdk.XunitException("Activation response was missing.");

        csrf = await GetCsrfAsync(client);
        SessionResponse session = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/sessions",
            new CreateSessionRequest(current.Id, "Save Session"), csrf, idempotencyKey: "save-session"))
            .Content.ReadFromJsonAsync<SessionResponse>()
        ?? throw new Xunit.Sdk.XunitException("Session create response was missing.");
        Assert.Equal("CLOSED", session.State);

        HttpResponseMessage manifestResponse = await client.GetAsync($"/api/v1/sessions/{session.Id}/presentation-manifest");
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        PresentationManifestResponse manifest = await manifestResponse.Content.ReadFromJsonAsync<PresentationManifestResponse>()
            ?? throw new Xunit.Sdk.XunitException("Presentation manifest response was missing.");
        byte[] expectedPng = FixturePng();
        string expectedDigest = Convert.ToHexString(SHA256.HashData(expectedPng)).ToLowerInvariant();
        PresentationAssetResponse asset = Assert.Single(manifest.Assets, item => item.MediaType == "image/png");
        Assert.Equal($"sha256-{expectedDigest}", asset.AssetId);
        PresentationFontResponse[] fonts = manifest.Fonts;
        Assert.Equal(2, fonts.Length);
        Assert.Contains(fonts, item => item.Family == "default");
        Assert.Equal(fonts.Length, fonts.Select(item => item.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(fonts.Length, fonts.Select(item => item.CssFamily).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("FONT_MULTIPLE_ASSETS_ISOLATED", manifest.FontDiagnostics);
        using HttpResponseMessage assetResponse = await client.GetAsync($"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}");
        Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
        Assert.Equal("image/png", assetResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedPng, await assetResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains("private", assetResponse.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("immutable", assetResponse.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bytes", assetResponse.Headers.AcceptRanges.ToString());
        Assert.False(string.IsNullOrWhiteSpace(assetResponse.Headers.ETag?.ToString()));
        using HttpRequestMessage cachedRequest = new(HttpMethod.Get, $"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}");
        cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", assetResponse.Headers.ETag?.ToString());
        using HttpResponseMessage cachedResponse = await client.SendAsync(cachedRequest);
        Assert.Equal(HttpStatusCode.NotModified, cachedResponse.StatusCode);
        using HttpRequestMessage rangeRequest = new(HttpMethod.Get, $"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 7);
        using HttpResponseMessage rangeResponse = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal(expectedPng[..8], await rangeResponse.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage multiRangeRequest = new(HttpMethod.Get, $"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}");
        multiRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-1,4-5");
        using HttpResponseMessage multiRangeResponse = await client.SendAsync(multiRangeRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, multiRangeResponse.StatusCode);
        using HttpRequestMessage suffixRangeRequest = new(HttpMethod.Get, $"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}");
        suffixRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=-8");
        using HttpResponseMessage suffixRangeResponse = await client.SendAsync(suffixRangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, suffixRangeResponse.StatusCode);
        Assert.Equal(expectedPng[^8..], await suffixRangeResponse.Content.ReadAsByteArrayAsync());
        using HttpRequestMessage invalidRangeRequest = new(HttpMethod.Get, $"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}");
        invalidRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=999-");
        using HttpResponseMessage invalidRangeResponse = await client.SendAsync(invalidRangeRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalidRangeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/sessions/{session.Id}/assets/not-a-digest")).StatusCode);

        // SEC-008: a frozen entry is fail-closed when the opened file is
        // replaced with bytes that no longer match its MIME or digest.
        string assetPath = Path.Combine(dataRoot, "sessions", session.Id, "root", "IMAGE", "hero.png");
        File.WriteAllBytes(assetPath, Encoding.ASCII.GetBytes("not-a-png"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}")).StatusCode);
        byte[] digestMismatch = expectedPng.ToArray();
        digestMismatch[^1] ^= 0x01;
        File.WriteAllBytes(assetPath, digestMismatch);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}")).StatusCode);
        File.WriteAllBytes(assetPath, expectedPng);
        if (OperatingSystem.IsLinux())
        {
            string linkTarget = Path.Combine(dataRoot, "sessions", session.Id, "root", "IMAGE", "other.png");
            File.WriteAllBytes(linkTarget, expectedPng);
            File.Delete(assetPath);
            File.CreateSymbolicLink(assetPath, linkTarget);
            HttpStatusCode symlinkStatus = (await client.GetAsync($"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}")).StatusCode;
            Assert.Contains(symlinkStatus, new[] { HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable });
            File.Delete(assetPath);
            File.WriteAllBytes(assetPath, expectedPng);
            File.Delete(linkTarget);

            string hardlinkTarget = Path.Combine(dataRoot, "sessions", session.Id, "root", "IMAGE", "hardlink.png");
            Assert.Equal(0, Link(assetPath, hardlinkTarget));
            HttpStatusCode hardlinkStatus = (await client.GetAsync($"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}")).StatusCode;
            Assert.Contains(hardlinkStatus, new[] { HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable });
            File.Delete(hardlinkTarget);

            // SEC-008/TOCTOU: repeatedly replace the published name while
            // requests are opening it. A secure descriptor may yield the
            // original bytes, but it must never follow the decoy link/file.
            string decoyPath = Path.Combine(dataRoot, "outside-asset.png");
            byte[] decoyPng = expectedPng.ToArray();
            decoyPng[^1] ^= 0x01;
            File.WriteAllBytes(decoyPath, decoyPng);
            using CancellationTokenSource raceCancellation = new();
            Task swapper = Task.Run(() =>
            {
                while (!raceCancellation.IsCancellationRequested)
                {
                    try
                    {
                        File.Delete(assetPath);
                        File.CreateSymbolicLink(assetPath, decoyPath);
                        File.Delete(assetPath);
                        File.WriteAllBytes(assetPath, expectedPng);
                    }
                    catch (IOException)
                    {
                        // The request may have the name open; retry the next
                        // replacement rather than weakening the assertion.
                    }
                }
            });
            try
            {
                HttpResponseMessage[] raceResponses = await Task.WhenAll(
                    Enumerable.Range(0, 12).Select(_ => client.GetAsync($"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}")));
                foreach (HttpResponseMessage response in raceResponses)
                {
                    using (response)
                    {
                        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable });
                        if (response.StatusCode == HttpStatusCode.OK) Assert.Equal(expectedPng, await response.Content.ReadAsByteArrayAsync());
                    }
                }
            }
            finally
            {
                raceCancellation.Cancel();
                await swapper;
                File.Delete(assetPath);
                File.WriteAllBytes(assetPath, expectedPng);
                File.Delete(decoyPath);
            }
        }

        // SEC-008/AUTH-003: a different authenticated user must not be able
        // to enumerate either the presentation manifest or an asset digest.
        csrf = await GetCsrfAsync(client);
        CurrentUserResponse player = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/admin/users",
            new CreateUserRequest("session-save-player", "session-save-player@example.test", "session-save-player-temporary", "PLAYER"), csrf))
            .Content.ReadFromJsonAsync<CurrentUserResponse>()
            ?? throw new Xunit.Sdk.XunitException("Cross-user fixture response was missing.");
        using HttpClient playerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        string playerCsrf = await GetCsrfAsync(playerClient);
        Assert.True((await SendJsonAsync(playerClient, HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest("session-save-player@example.test", "session-save-player-temporary", false), playerCsrf)).IsSuccessStatusCode);
        playerCsrf = await GetCsrfAsync(playerClient);
        Assert.Equal(HttpStatusCode.NoContent, (await SendJsonAsync(playerClient, HttpMethod.Post, "/api/v1/auth/change-password",
            new ChangePasswordRequest("session-save-player-temporary", "session-save-player-permanent"), playerCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await playerClient.GetAsync($"/api/v1/sessions/{session.Id}/presentation-manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await playerClient.GetAsync($"/api/v1/sessions/{session.Id}/assets/{asset.AssetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await playerClient.GetAsync($"/api/v1/sessions/{session.Id}/saves")).StatusCode);
        Assert.True(player.MustChangePassword);

        await AddStartingWorkerLeaseAsync(session.Id);
        csrf = await GetCsrfAsync(client);
        HttpResponseMessage activeWorker = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", Encoding.UTF8.GetBytes("0\n0\n"), "application/octet-stream", csrf, "save-active-worker");
        Assert.Equal(HttpStatusCode.Conflict, activeWorker.StatusCode);
        await RemoveWorkerLeaseAsync(session.Id);

        byte[] save = Encoding.UTF8.GetBytes("0\n0\n");
        csrf = await GetCsrfAsync(client);
        HttpResponseMessage missingIdempotency = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", save, "application/octet-stream", csrf, null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingIdempotency.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage imported = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", save, "application/octet-stream", csrf, "save-import");
        Assert.True(imported.StatusCode == HttpStatusCode.Created, $"Save import failed: {(int)imported.StatusCode} {await imported.Content.ReadAsStringAsync()}");
        SaveItemResponse importedItem = await imported.Content.ReadFromJsonAsync<SaveItemResponse>()
            ?? throw new Xunit.Sdk.XunitException("Save import response was missing.");
        Assert.Equal("global.sav", importedItem.Path);

        // The same client key is scoped to the target Session, not just the
        // actor and save-operation scope.
        csrf = await GetCsrfAsync(client);
        SessionResponse secondSession = await (await SendJsonAsync(client, HttpMethod.Post, "/api/v1/sessions",
            new CreateSessionRequest(current.Id, "Save Session 2"), csrf, idempotencyKey: "save-session-second"))
            .Content.ReadFromJsonAsync<SessionResponse>()
            ?? throw new Xunit.Sdk.XunitException("Second session create response was missing.");
        csrf = await GetCsrfAsync(client);
        HttpResponseMessage secondSessionImport = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{secondSession.Id}/saves/global.sav", save, "application/octet-stream", csrf, "save-import");
        Assert.Equal(HttpStatusCode.Created, secondSessionImport.StatusCode);

        csrf = await GetCsrfAsync(client);
        Task<HttpResponseMessage>[] concurrentImports =
        [
            SendRawAsync(client, HttpMethod.Put, $"/api/v1/sessions/{session.Id}/saves/save3.sav", save, "application/octet-stream", csrf, "save-concurrent"),
            SendRawAsync(client, HttpMethod.Put, $"/api/v1/sessions/{session.Id}/saves/save3.sav", save, "application/octet-stream", csrf, "save-concurrent"),
        ];
        HttpResponseMessage[] concurrentResults = await Task.WhenAll(concurrentImports);
        Assert.Contains(concurrentResults, response => response.StatusCode == HttpStatusCode.Created);
        Assert.DoesNotContain(concurrentResults, response => response.StatusCode == HttpStatusCode.InternalServerError);

        // A replay must be resolved before the stopped-state check. The
        // original operation is already durable, so an active Worker cannot
        // turn a safe idempotent replay into a false 409.
        await AddStartingWorkerLeaseAsync(session.Id);
        csrf = await GetCsrfAsync(client);
        HttpResponseMessage replayWhileWorkerActive = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", save, "application/octet-stream", csrf, "save-import");
        Assert.Equal(HttpStatusCode.Created, replayWhileWorkerActive.StatusCode);
        await RemoveWorkerLeaseAsync(session.Id);

        SaveListResponse listing = await (await client.GetAsync($"/api/v1/sessions/{session.Id}/saves"))
            .Content.ReadFromJsonAsync<SaveListResponse>()
            ?? throw new Xunit.Sdk.XunitException("Save list response was missing.");
        Assert.Equal(expectedLayout, listing.Layout);
        Assert.Contains(listing.Items, item => item.Path == "global.sav" && item.Kind == "GLOBAL");

        HttpResponseMessage missingDownload = await client.GetAsync($"/api/v1/sessions/{session.Id}/saves/save99.sav");
        Assert.Equal(HttpStatusCode.NotFound, missingDownload.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage secondSave = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/save2.sav", save, "application/octet-stream", csrf, "save-import-second");
        Assert.Equal(HttpStatusCode.Created, secondSave.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage targetExists = await SendJsonAsync(client, HttpMethod.Patch,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", new RenameSaveRequest("save2.sav"), csrf,
            idempotencyKey: "save-rename-conflict");
        Assert.Equal(HttpStatusCode.Conflict, targetExists.StatusCode);

        HttpResponseMessage download = await client.GetAsync($"/api/v1/sessions/{session.Id}/saves/global.sav");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(save, await download.Content.ReadAsByteArrayAsync());
        string cacheControl = download.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("private", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage renamed = await SendJsonAsync(client, HttpMethod.Patch,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", new RenameSaveRequest("save1.sav"), csrf,
            idempotencyKey: "save-rename");
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);

        // A replay must be resolved from the durable operation before looking
        // up the old source path; a successful rename has intentionally
        // removed that path.
        csrf = await GetCsrfAsync(client);
        HttpResponseMessage renamedReplay = await SendJsonAsync(client, HttpMethod.Patch,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", new RenameSaveRequest("save1.sav"), csrf,
            idempotencyKey: "save-rename");
        Assert.Equal(HttpStatusCode.NoContent, renamedReplay.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage rejectedDelete = await SendDeleteAsync(client,
            $"/api/v1/sessions/{session.Id}/saves/save1.sav", csrf, "save-delete-no-confirm", confirmed: false);
        Assert.Equal(HttpStatusCode.PreconditionRequired, rejectedDelete.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage deleted = await SendDeleteAsync(client,
            $"/api/v1/sessions/{session.Id}/saves/save1.sav", csrf, "save-delete", confirmed: true);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage deletedReplay = await SendDeleteAsync(client,
            $"/api/v1/sessions/{session.Id}/saves/save1.sav", csrf, "save-delete", confirmed: true);
        Assert.Equal(HttpStatusCode.NoContent, deletedReplay.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage invalidFormat = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", Encoding.UTF8.GetBytes("not an Emuera save"), "application/octet-stream", csrf, "save-invalid-format");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, invalidFormat.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage invalidFormatReplay = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/global.sav", Encoding.UTF8.GetBytes("not an Emuera save"), "application/octet-stream", csrf, "save-invalid-format");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, invalidFormatReplay.StatusCode);

        csrf = await GetCsrfAsync(client);
        HttpResponseMessage invalidPhysicalPrefix = await SendRawAsync(client, HttpMethod.Put,
            $"/api/v1/sessions/{session.Id}/saves/sav/save2.sav", save, "application/octet-stream", csrf, "save-invalid-path");
        Assert.True(invalidPhysicalPrefix.StatusCode == HttpStatusCode.BadRequest,
            $"Invalid save path failed unexpectedly: {(int)invalidPhysicalPrefix.StatusCode} {await invalidPhysicalPrefix.Content.ReadAsStringAsync()}");
    }

    private static async Task LoginAsync(HttpClient client)
    {
        string csrf = await GetCsrfAsync(client);
        HttpResponseMessage login = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest("admin@example.test", "temporary-password", false), csrf);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        csrf = await GetCsrfAsync(client);
        HttpResponseMessage password = await SendJsonAsync(client, HttpMethod.Post, "/api/v1/auth/change-password",
            new ChangePasswordRequest("temporary-password", "administrator-password"), csrf);
        Assert.Equal(HttpStatusCode.NoContent, password.StatusCode);
    }

    private async Task CreateDatabaseAsync()
    {
        DatabaseMigrationRunner runner = new(new SqliteDatabaseOptions { DataRoot = dataRoot });
        MigrationResult result = await runner.MigrateAsync();
        Assert.True(result.Succeeded, result.ErrorCode);
    }

    private async Task AddStartingWorkerLeaseAsync(string sessionId)
    {
        SqliteDatabaseOptions options = new() { DataRoot = dataRoot };
        await using SqliteConnection connection = new SqliteConnectionFactory(options, createDataRoot: false).OpenConnection(SqliteConnectionAccess.ReadWrite);
        DbContextOptions<CloudEmueraDbContext> contextOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options;
        await using CloudEmueraDbContext db = new(contextOptions);
        await db.Sessions.Where(item => item.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.WorkerEpoch, 1L));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.WorkerLeases.Add(new WorkerLeaseRow
        {
            SessionId = sessionId,
            WorkerId = "wrk_fixture",
            Epoch = 1,
            Status = WorkerLeaseStatus.Starting,
            ControlPlaneInstanceId = "ctl_fixture",
            IpcEndpoint = "worker.sock",
            RuntimeVersion = "test-runtime",
            ProtocolVersion = 1,
            AcquiredAt = now,
            HeartbeatAt = now,
            ExpiresAt = now.AddMinutes(1),
        });
        await db.SaveChangesAsync();
    }

    private async Task RemoveWorkerLeaseAsync(string sessionId)
    {
        SqliteDatabaseOptions options = new() { DataRoot = dataRoot };
        await using SqliteConnection connection = new SqliteConnectionFactory(options, createDataRoot: false).OpenConnection(SqliteConnectionAccess.ReadWrite);
        DbContextOptions<CloudEmueraDbContext> contextOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options;
        await using CloudEmueraDbContext db = new(contextOptions);
        WorkerLeaseRow? lease = await db.WorkerLeases.SingleOrDefaultAsync(item => item.SessionId == sessionId);
        if (lease is not null)
            db.WorkerLeases.Remove(lease);
        await db.SaveChangesAsync();
    }

    private static async Task<string> GetCsrfAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfResponse>()
            ?? throw new Xunit.Sdk.XunitException("CSRF response was missing.")).Token;

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(HttpClient client, HttpMethod method, string path,
        T body, string csrf, int? stateVersion = null, string? idempotencyKey = null)
    {
        using HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (stateVersion is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{stateVersion}\"");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path,
        byte[] body, string contentType, string csrf, string? idempotencyKey)
    {
        using HttpRequestMessage request = new(method, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendDeleteAsync(HttpClient client, string path, string csrf,
        string idempotencyKey, bool confirmed)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (confirmed) request.Headers.Add("X-Confirm-Delete", "true");
        return await client.SendAsync(request);
    }

    private static MemoryStream CreateArchive(bool useSavFolder)
    {
        MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry csv = archive.CreateEntry("CSV/GAMEBASE.CSV");
            using (Stream writer = csv.Open())
            using (StreamWriter text = new(writer)) text.Write("title,save-test\n");
            ZipArchiveEntry erb = archive.CreateEntry("ERB/START.ERB");
            using (Stream writer = erb.Open())
            using (StreamWriter text = new(writer)) text.Write("@SYSTEM_TITLE\nINPUT\nQUIT\n");
            ZipArchiveEntry config = archive.CreateEntry("emuera.config");
            using (Stream writer = config.Open())
            using (StreamWriter text = new(writer)) text.Write(useSavFolder ? "Use sav folder:YES\n" : "Use sav folder:NO\n");
            ZipArchiveEntry image = archive.CreateEntry("IMAGE/hero.png");
            using (Stream writer = image.Open()) writer.Write(FixturePng());
            ZipArchiveEntry defaultFont = archive.CreateEntry("FONT/default.woff2");
            using (Stream writer = defaultFont.Open()) writer.Write(FixtureWoff2(0x01));
            ZipArchiveEntry secondFont = archive.CreateEntry("FONT/second.woff2");
            using (Stream writer = secondFont.Open()) writer.Write(FixtureWoff2(0x02));
        }
        stream.Position = 0;
        return stream;
    }

    private static byte[] FixturePng() => Convert.FromHexString("89504E470D0A1A0A0000000D49484452000000010000000108060000001F15C4890000000D49444154789C6360F8CFC0000003010100C9FE92EF0000000049454E44AE426082");

    private static byte[] FixtureWoff2(byte marker) => [0x77, 0x4F, 0x46, 0x32, marker, 0x00, 0x00, 0x00];

    public void Dispose()
    {
        factory?.Dispose();
        try
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
        catch
        {
            // Test cleanup must not hide the assertion that failed.
        }
    }

    private sealed class IdentityFactory(string dataRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudEmuera:DataPath"] = dataRoot,
                ["CloudEmuera:PublicOrigin"] = "http://localhost:5173",
            }));
        }
    }

    private sealed record CsrfResponse(string Token);
    private sealed record PresentationManifestResponse(int SchemaVersion, PresentationAssetResponse[] Assets, PresentationFontResponse[] Fonts, string[] FontDiagnostics);
    private sealed record PresentationAssetResponse(string AssetId, string MediaType, long ByteLength, string ContentDigest, string? ETag);
    private sealed record PresentationFontResponse(string Family, string AssetId, string Fallback, string CssFamily, string[] Aliases);

    [SuppressMessage("Security", "CA2101", Justification = "The test P/Invoke explicitly marshals both paths as UTF-8.")]
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
