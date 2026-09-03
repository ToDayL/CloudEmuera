using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using System.Text.Json;
using System.Reflection;
using CloudEmuera.Api.Bootstrap;
using CloudEmuera.Api.Configuration;
using CloudEmuera.Api.Security;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Administration;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using CloudEmuera.Contracts;
using CloudEmuera.Contracts.Identity;
using CloudEmuera.Contracts.Games;
using CloudEmuera.Contracts.Sessions;
using CloudEmuera.Contracts.Realtime;
using CloudEmuera.Contracts.Saves;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.Saves;
using CloudEmuera.Application.Assets;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Identity;
using CloudEmuera.Infrastructure.Authorization;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Infrastructure.GamePackages;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.Infrastructure.Saves;
using CloudEmuera.Infrastructure.Assets;
using CloudEmuera.Infrastructure.Fonts;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Fonts;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Ipc;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Workers;
using CloudEmuera.Api.Administration;
using CloudEmuera.Api.Health;
using CloudEmuera.Contracts.Administration;
using CloudEmuera.Infrastructure.Administration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = WorkerShutdownDefaults.HostShutdownTimeout);
string dataRoot = builder.Configuration["CloudEmuera:DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data");
string workerAssemblyPath = builder.Configuration["CloudEmuera:WorkerAssemblyPath"]
    ?? ValidatorAssemblyResolver.ResolveSiblingAssembly(builder.Environment.ContentRootPath, "CloudEmuera.Worker", "CloudEmuera.Worker.dll");
string runtimeFontRoot = builder.Configuration["CloudEmuera:RuntimeFontRoot"]
    ?? Environment.GetEnvironmentVariable("CLOUDEMUERA_RUNTIME_FONT_ROOT")
    ?? FileRuntimeFontCatalog.ResolveDefaultRoot();
string? runtimeDebugTraceSwitch = Environment.GetEnvironmentVariable("CLOUDEMUERA_RUNTIME_DEBUG_TRACE");
bool runtimeDebugTraceEnabled = string.Equals(runtimeDebugTraceSwitch, "1", StringComparison.Ordinal) ||
    bool.TryParse(runtimeDebugTraceSwitch, out bool parsedRuntimeDebugTrace) && parsedRuntimeDebugTrace;
RealtimeOutputOptions realtimeOutputOptions = DeploymentOptionsBinder.BindRealtimeOutput(builder.Configuration);
RealtimeGatewayOptions realtimeGatewayOptions = DeploymentOptionsBinder.BindRealtimeGateway(builder.Configuration);
var workerOptions = new WorkerManagerOptions(dataRoot, workerAssemblyPath, runtimeFontRoot)
{
    RealtimeOutput = realtimeOutputOptions,
    PendingEventMaxMessages = DeploymentOptionsBinder.ReadInt(builder.Configuration, "CloudEmuera:Worker:PendingEventMaxMessages") ?? 256,
    PendingEventMaxBytes = DeploymentOptionsBinder.ReadInt(builder.Configuration, "CloudEmuera:Worker:PendingEventMaxBytes") ?? 1 * 1024 * 1024,
    PendingInputMaxMessages = realtimeGatewayOptions.MaxPendingInputsPerWorker,
    DebugInputTraceEnabled = runtimeDebugTraceEnabled || builder.Configuration.GetValue<bool>("CloudEmuera:Debugger:TraceEnabled"),
    DebugTraceMaxBytes = DeploymentOptionsBinder.ReadInt(builder.Configuration, "CloudEmuera:Debugger:TraceMaxBytes") ?? 32 * 1024 * 1024,
};
InstanceCapacityOptions capacityOptions = DeploymentOptionsBinder.BindCapacity(
    builder.Configuration,
    out bool usedLegacyArchiveKey,
    out bool usedLegacyFreeSpaceKey);
PresentationAssetOptions assetOptions = DeploymentOptionsBinder.BindAssets(builder.Configuration);
DeploymentOptionsValidator.Validate(capacityOptions, realtimeOutputOptions, realtimeGatewayOptions, workerOptions, assetOptions);
var workerSocketLifecycle = new WorkerSocketLifecycle(workerOptions);
workerSocketLifecycle.Prepare();
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false;
    serverOptions.ListenUnixSocket(workerOptions.ControlSocketPath, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    // The Unix socket is reserved for API/Worker gRPC. Keep the configured
    // HTTP listener for browser HTTP and WebSocket traffic as well. Since an
    // explicit Kestrel endpoint suppresses ASPNETCORE_URLS, resolve its port
    // first instead of silently forcing the development default.
    int httpPort = KestrelHttpPortResolver.Resolve(builder.Configuration);
    serverOptions.ListenAnyIP(httpPort, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2);
});
builder.Services.AddGrpc(grpcOptions =>
{
    grpcOptions.MaxReceiveMessageSize = CloudEmuera.Ipc.StructuredIpcLimits.MaxEnvelopeBytes;
    grpcOptions.MaxSendMessageSize = CloudEmuera.Ipc.StructuredIpcLimits.MaxEnvelopeBytes;
});
SqliteDatabaseOptions databaseOptions = new()
{
    DataRoot = dataRoot,
    MinDataRootFreeBytes = capacityOptions.MinDataRootFreeBytes,
};
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton(capacityOptions);
builder.Services.AddSingleton(assetOptions);
FileRuntimeFontCatalog runtimeFontCatalog = new(runtimeFontRoot);
runtimeFontCatalog.VerifyAllAssets();
builder.Services.AddSingleton<IRuntimeFontCatalog>(runtimeFontCatalog);
builder.Services.AddSingleton(runtimeFontCatalog);
builder.Services.AddSingleton<PresentationAssetReadGate>();
builder.Services.AddScoped<CloudEmueraDbContext>(serviceProvider =>
{
    SqliteConnectionFactory factory = new(databaseOptions, createDataRoot: true);
    var options = new DbContextOptionsBuilder<CloudEmueraDbContext>()
        .UseSqlite(factory.OpenConnection(SqliteConnectionAccess.ReadWriteCreate), sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
        .Options;
    return new CloudEmueraDbContext(options);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton(realtimeGatewayOptions);
builder.Services.AddSingleton<RealtimeEnvelopeCodec>();
builder.Services.AddSingleton<RealtimeConnectionRegistry>();
builder.Services.AddSingleton<RealtimeAuthorizationGate>();
builder.Services.AddSingleton(workerSocketLifecycle);
builder.Services.AddSingleton(new ApiControlPlaneIdentity(workerOptions.ControlPlaneInstanceId));
builder.Services.AddSingleton<ISessionRuntimeStore, SqliteSessionRuntimeStore>();
builder.Services.AddSingleton<WorkerManager>(serviceProvider => new WorkerManager(
    workerOptions,
    serviceProvider.GetRequiredService<ILoggerFactory>(),
    serviceProvider.GetRequiredService<ISessionRuntimeStore>(),
    serviceProvider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<WorkerRuntimeReadiness>();
builder.Services.AddHostedService<WorkerManagerHostedService>();
builder.Services.AddSingleton<ISessionRootRuntimeInspector, SessionRootRuntimeInspector>();
builder.Services.AddSingleton<ISessionWorkerControl>(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());
builder.Services.AddSingleton<SessionRuntimeCoordinator>();
builder.Services.AddSingleton<ICurrentWorkerRouter>(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());
builder.Services.AddSingleton<IWorkerOpenOptionsFactory, ApiWorkerOpenOptionsFactory>();
builder.Services.AddSingleton<SessionLifecycleExecutor>();
builder.Services.AddSingleton<ISessionLifecycleExecutor>(serviceProvider => serviceProvider.GetRequiredService<SessionLifecycleExecutor>());
builder.Services.AddSingleton<ISessionCommandGate>(serviceProvider => serviceProvider.GetRequiredService<SessionLifecycleExecutor>());
builder.Services.AddSingleton<IRealtimeSessionRegistry>(serviceProvider => serviceProvider.GetRequiredService<WorkerManager>());
builder.Services.AddSingleton<RealtimeEndpoint>();
builder.Services.AddSingleton<ISessionRootMutationLeaseStore, SqliteSessionRootMutationLeaseStore>();
builder.Services.AddSingleton<SqliteIdempotencyStore>();
builder.Services.AddSingleton<IAdminRuntimeStore, SqliteAdminRuntimeStore>();
builder.Services.AddSingleton<IAdminRuntimeDiagnostics, ApiAdminRuntimeDiagnostics>();
builder.Services.AddSingleton<IAdminRuntimeQuery, AdminRuntimeQuery>();
builder.Services.AddSingleton<SqliteAdminSessionCommandService>();
builder.Services.AddSingleton<IAdminSessionCommandService>(serviceProvider => serviceProvider.GetRequiredService<SqliteAdminSessionCommandService>());
builder.Services.AddSingleton<IAdminForceStopRecovery>(serviceProvider => serviceProvider.GetRequiredService<SqliteAdminSessionCommandService>());
builder.Services.AddSingleton<ISessionApplicationService, SqliteSessionApplicationService>();
builder.Services.AddSingleton<ISessionOperationRecovery>(serviceProvider =>
    (ISessionOperationRecovery)serviceProvider.GetRequiredService<ISessionApplicationService>());
builder.Services.AddSingleton<SessionOperationRecoveryReadiness>();
builder.Services.AddSingleton<SessionCommandReadiness>();
builder.Services.AddSingleton<SaveOperationRecoveryReadiness>();
builder.Services.AddHostedService<SessionOperationRecoveryHostedService>();
builder.Services.AddHostedService<SaveFileOperationRecoveryHostedService>();
builder.Services.AddSingleton(new GamePackageStorageOptions
{
    DataRoot = dataRoot,
    MaxStagingReservedBytes = capacityOptions.MaxStagingReservedBytes,
    MinDataRootFreeBytes = capacityOptions.MinDataRootFreeBytes,
});
builder.Services.AddScoped<IGamePackageIngestionService, GamePackageIngestionService>();
builder.Services.AddScoped<IGameLibraryService, GameLibraryService>();
string validatorAssembly = builder.Configuration["CloudEmuera:ValidatorAssembly"]
    ?? ValidatorAssemblyResolver.Resolve(builder.Environment.ContentRootPath, builder.Environment.IsDevelopment() ? "Debug" : "Release");
double validatorTimeoutSeconds = builder.Configuration.GetValue<double?>("CloudEmuera:ValidatorTimeoutSeconds") ?? 120;
builder.Services.AddSingleton(new GameValidatorProcessOptions
{
    ExecutablePath = "dotnet",
    AssemblyPath = validatorAssembly,
    Timeout = TimeSpan.FromSeconds(validatorTimeoutSeconds > 0 ? validatorTimeoutSeconds : 120),
});
builder.Services.AddSingleton<IGameContentValidator, GameValidatorProcessClient>();
builder.Services.AddScoped<IGameContentCopyLeaseStore, GameContentCopyLeaseStore>();
builder.Services.AddScoped<IGameContentOperationMaintenance, GameContentOperationMaintenance>();
builder.Services.AddScoped<ApiIdempotencyStore>();
builder.Services.AddScoped<IGamePackageIngestionMaintenance, GamePackageIngestionMaintenance>();
builder.Services.AddHostedService<GamePackageIngestionReaperService>();
builder.Services.AddHostedService<GameContentOperationReaperService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditContext, HttpAuditContext>();
builder.Services.AddScoped<ISessionSaveRootAccessor, LinuxSessionSaveRootAccessor>();
builder.Services.AddScoped<ISaveFileOperationStore, SqliteSaveFileOperationStore>();
builder.Services.AddSingleton<ISaveFileFormatValidator, EmueraSaveFileFormatValidator>();
builder.Services.AddScoped<ISessionSaveApplicationService, SessionSaveApplicationService>();
builder.Services.AddScoped<ISessionAssetService, SessionAssetService>();
builder.Services.AddScoped<ISaveFileOperationRecovery, SaveFileOperationRecovery>();
builder.Services.AddScoped<IPasswordHasher<CloudEmueraUser>, PasswordHasher<CloudEmueraUser>>();
builder.Services.AddScoped<CloudEmueraUserStore>();
builder.Services.AddScoped<IUserStore<CloudEmueraUser>>(serviceProvider => serviceProvider.GetRequiredService<CloudEmueraUserStore>());
builder.Services.AddScoped<LocalIdentityService>();
builder.Services.AddScoped<ILocalIdentityService>(serviceProvider => serviceProvider.GetRequiredService<LocalIdentityService>());
builder.Services.AddScoped<IAuthSessionMaintenance, AuthSessionMaintenance>();
builder.Services.AddScoped<CloudEmuera.Application.Authorization.IResourceAccessReader, SqliteResourceAccessReader>();
builder.Services.AddScoped<CloudEmuera.Application.Authorization.IResourceAuthorizer, CloudEmuera.Application.Authorization.ResourceAuthorizer>();
builder.Services.AddSingleton<BootstrapReadiness>();
builder.Services.AddHostedService<BootstrapAdminInitializer>();
builder.Services.AddHostedService<AuthSessionCleanupService>();
builder.Services.AddScoped<RealtimeUpgradeValidator>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(DataProtectionKeyRing.Prepare(dataRoot)).SetApplicationName("CloudEmuera");
bool development = builder.Environment.IsDevelopment();
bool secureCookies = builder.Configuration.GetValue("CloudEmuera:Security:SecureCookies", false);
string cookieName = development ? "CloudEmuera.Dev.Session" : secureCookies ? "__Host-CloudEmuera.Session" : "CloudEmuera.Session";
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = cookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.None;
    options.Cookie.Path = "/";
    options.SlidingExpiration = false;
    options.Events = new CookieAuthenticationEvents
    {
        OnRedirectToLogin = context => ApiIdentity.WriteErrorAsync(context.HttpContext, "UNAUTHENTICATED", "需要登录。", StatusCodes.Status401Unauthorized),
        OnRedirectToAccessDenied = context => ApiIdentity.WriteErrorAsync(context.HttpContext, "FORBIDDEN", "没有权限。", StatusCodes.Status403Forbidden),
        OnValidatePrincipal = async context =>
        {
            string? userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            string? sessionId = context.Principal?.FindFirstValue("auth_session_id");
            string? stamp = context.Principal?.FindFirstValue("security_stamp");
            if (userId is null || sessionId is null || stamp is null || !await context.HttpContext.RequestServices.GetRequiredService<ILocalIdentityService>().ValidateSessionAsync(userId, sessionId, stamp, context.HttpContext.RequestAborted).ConfigureAwait(false))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync().ConfigureAwait(false);
            }
        },
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("identity-login", context => RateLimitPartition.GetFixedWindowLimiter(LoginRateLimitPartitioner.GetKey(context), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new ApiError("TOO_MANY_ATTEMPTS", "请求过于频繁。", RequestCorrelation.Current ?? context.HttpContext.TraceIdentifier), cancellationToken).ConfigureAwait(false);
    };
    options.AddPolicy("game-read", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("game-write", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("game-validate", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("session-read", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("session-write", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<BootstrapHealthCheck>("identity_bootstrap", tags: ["ready"])
    .AddCheck<RuntimeFontReadinessHealthCheck>("runtime_fonts", tags: ["ready"])
    .AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"])
    .AddCheck<SchemaReadinessHealthCheck>("schema", tags: ["ready"])
    .AddCheck<DataRootReadinessHealthCheck>("data_root", tags: ["ready"])
    .AddCheck<DataRootSpaceReadinessHealthCheck>("data_root_space", tags: ["ready"])
    .AddCheck<WorkerRuntimeHealthCheck>("worker_runtime", tags: ["ready"])
    .AddCheck<SessionOperationRecoveryHealthCheck>("session_operation_recovery", tags: ["ready"])
    .AddCheck<SaveOperationRecoveryHealthCheck>("save_operation_recovery", tags: ["ready"]);

var app = builder.Build();
if (usedLegacyArchiveKey)
    ConfigurationWarnings.LegacyMaxGamePackageBytes(app.Logger);
if (usedLegacyFreeSpaceKey)
    ConfigurationWarnings.LegacyMinDataRootFreeBytes(app.Logger);
app.Use((context, next) => RequestCorrelationMiddleware.InvokeAsync(
    context,
    next,
    context.RequestServices.GetRequiredService<ILoggerFactory>()));
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});
app.Use((context, next) =>
{
    DataProtectionKeyRing.HardenExistingKeyFiles(dataRoot);
    return next(context);
});
app.Use(async (context, next) =>
{
    await LoginRateLimitPartitioner.CaptureAsync(context).ConfigureAwait(false);
    await next(context).ConfigureAwait(false);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/api/v1/realtime", StringComparison.Ordinal))
        context.Response.Headers.CacheControl = "no-store";
    await next(context).ConfigureAwait(false);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGrpcService<WorkerControlGrpcService>();

app.MapGet("/api/v1/realtime", (HttpContext context, RealtimeEndpoint endpoint) => endpoint.HandleAsync(context))
    .RequireAuthorization();

app.MapGet("/api/v1/version", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    Assembly assembly = typeof(Program).Assembly;
    string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    string? commit = informational.Split('+', 2) is [_, var suffix] && suffix.Length is > 0 and <= 64 && suffix.All(Uri.IsHexDigit)
        ? suffix.ToLowerInvariant()
        : null;
    return Results.Ok(new VersionResponse(
        "CloudEmuera",
        assembly.GetName().Version?.ToString() ?? "0.0.0-dev",
        commit,
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        1,
        RealtimeProtocol.Version,
        RealtimeProtocol.PayloadSchemaVersion,
        checked((int)StructuredIpcProtocol.CurrentVersion),
        RuntimeBaseline.CloudEmueraIntegrationVersion,
        RuntimeBaseline.UpstreamCommit,
        SqliteStorageConventions.CurrentSchemaCompatibilityVersion,
        runtimeFontCatalog.CatalogDigest));
});

app.MapGet("/api/v1/runtime-fonts", (FileRuntimeFontCatalog catalog) => Results.Ok(new
{
    schemaVersion = 1,
    catalogDigest = catalog.CatalogDigest,
    defaultFaceId = RuntimeFontDefaults.DefaultFaceId,
    items = catalog.ListAvailable().Select(face => new
    {
        faceId = face.FaceId,
        displayName = face.DisplayName,
        family = face.Family,
        sourceVersion = face.SourceVersion,
        weight = face.Weight,
        runtimeFamilyName = face.RuntimeFamilyName,
        webAssetDigest = face.WebWoff2Sha256,
        webAssetByteLength = face.WebWoff2ByteLength,
        webAssetUrl = $"/api/v1/runtime-fonts/assets/{face.WebWoff2Sha256}.woff2",
        licenseId = face.LicenseId,
    }),
})).Produces(StatusCodes.Status200OK);

app.MapGet("/api/v1/runtime-fonts/assets/{digest}.woff2", (string digest, HttpContext context, FileRuntimeFontCatalog catalog) =>
{
    try
    {
        FileStream stream = catalog.OpenWebWoff2(digest);
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers.ETag = $"\"sha256-{digest}\"";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Stream(stream, "font/woff2");
    }
    catch (RuntimeFontCatalogException)
    {
        return Results.NotFound();
    }
}).Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status404NotFound);

app.MapGet("/health/live", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { status = "LIVE" });
});
app.MapOpenApi();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = ReadinessResponseWriter.WriteAsync });

app.MapGet("/api/v1/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new CsrfResponse(tokens.RequestToken!));
});

var adminRuntime = app.MapGroup("/api/v1/admin").RequireAuthorization();
adminRuntime.MapGet("/workers", async (HttpContext context, int? recentFailureLimit, IResourceAuthorizer authorizer, IAdminRuntimeQuery query) =>
{
    if (await ApiIdentity.RequireAdminAsync(context, authorizer).ConfigureAwait(false) is IResult denied)
        return denied;
    if (recentFailureLimit is < 1 or > 100)
        return ApiIdentity.Error(AdminErrorCodes.ValidationFailed, "recentFailureLimit 必须是 1 到 100。", StatusCodes.Status400BadRequest);
    try
    {
        AdminRuntimeSnapshot snapshot = await query.ReadAsync(new AdminRuntimeQueryOptions(recentFailureLimit ?? 20), context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ApiIdentity.ToResponse(snapshot));
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception)
    {
        return ApiIdentity.Error(AdminErrorCodes.ServiceNotReady, "运行时诊断暂不可用。", StatusCodes.Status503ServiceUnavailable);
    }
}).Produces<AdminRuntimeResponse>(StatusCodes.Status200OK)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status401Unauthorized)
  .Produces<ApiError>(StatusCodes.Status403Forbidden)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

adminRuntime.MapPost("/sessions/{sessionId}:force-stop", async (
    string sessionId,
    HttpContext context,
    JsonElement body,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    IAntiforgery antiforgery,
    SessionCommandReadiness readiness,
    IAdminSessionCommandService service,
    IResourceAuthorizer authorizer) =>
{
    if (await ApiIdentity.RequireAdminAsync(context, authorizer).ConfigureAwait(false) is IResult denied)
        return denied;
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false))
        return ApiIdentity.Error(AdminErrorCodes.CsrfValidationFailed, "请求验证失败。", StatusCodes.Status400BadRequest);
    if (!ApiIdentity.TryAdminIdempotencyKey(idempotencyKey, out string key))
        return ApiIdentity.Error(AdminErrorCodes.IdempotencyKeyRequired, "需要有效的 Idempotency-Key。", StatusCodes.Status400BadRequest);
    if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("reason", out JsonElement reasonValue) || reasonValue.ValueKind != JsonValueKind.String)
        return ApiIdentity.Error(AdminErrorCodes.ValidationFailed, "需要提供强制停止原因。", StatusCodes.Status400BadRequest);
    if (!readiness.IsReady)
        return ApiIdentity.Error(AdminErrorCodes.ServiceNotReady, "Session 控制面尚未完成恢复。", StatusCodes.Status503ServiceUnavailable);
    try
    {
        AdminForceStopResult result = await service.ForceStopAsync(ApiIdentity.Actor(context)!, sessionId, key, reasonValue.GetString() ?? string.Empty, context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            AdminCommandFailure failure = result.Failure ?? new(AdminErrorCodes.ServiceNotReady, "强制停止失败。", StatusCodes.Status503ServiceUnavailable);
            return ApiIdentity.Error(failure.Code, failure.Message, failure.StatusCode, failure.Details);
        }
        ApiIdentity.SetSessionETag(context, result.Value!);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Location = $"/api/v1/sessions/{result.Value!.Id}";
        return Results.Ok(ApiIdentity.ToResponse(result.Value));
    }
    catch (AdminSessionCommandException exception)
    {
        return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode);
    }
}).RequireRateLimiting("session-write")
  .Accepts<JsonElement>("application/json")
  .Produces<SessionResponse>(StatusCodes.Status200OK)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status401Unauthorized)
  .Produces<ApiError>(StatusCodes.Status403Forbidden)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/v1/auth/login", async (HttpContext context, LoginRequest request, IAntiforgery antiforgery, BootstrapReadiness readiness, ILocalIdentityService identities) =>
{
    if (!readiness.IsReady) return ApiIdentity.Error("SERVICE_NOT_READY", "服务尚未完成初始化。", StatusCodes.Status503ServiceUnavailable);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", StatusCodes.Status400BadRequest);
    LoginResult? result = await identities.LoginAsync(new LoginCommand(request.Email, request.Password, request.RememberMe), context.RequestAborted).ConfigureAwait(false);
    if (result is null) return ApiIdentity.Error("INVALID_CREDENTIALS", "邮箱或密码不正确。", StatusCodes.Status401Unauthorized);
    await ApiIdentity.SignInAsync(context, result).ConfigureAwait(false);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(ApiIdentity.ToResponse(result.User));
}).RequireRateLimiting("identity-login");

app.MapGet("/api/v1/auth/me", async (HttpContext context, ILocalIdentityService identities) =>
{
    CurrentActor? actor = ApiIdentity.Actor(context);
    if (actor is null) return ApiIdentity.Error("UNAUTHENTICATED", "需要登录。", StatusCodes.Status401Unauthorized);
    CurrentUser? user = await identities.GetCurrentUserAsync(actor.UserId, context.RequestAborted).ConfigureAwait(false);
    return user is null ? ApiIdentity.Error("UNAUTHENTICATED", "需要登录。", StatusCodes.Status401Unauthorized) : Results.Ok(ApiIdentity.ToResponse(user));
}).RequireAuthorization();

var preferences = app.MapGroup("/api/v1/preferences").RequireAuthorization();
preferences.MapGet("/session-startup-defaults", async (HttpContext context, ILocalIdentityService identities) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    SessionStartupDefaults defaults = await identities.GetSessionStartupDefaultsAsync(actor, context.RequestAborted).ConfigureAwait(false);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(ApiIdentity.ToResponse(defaults));
}).RequireRateLimiting("session-read");
preferences.MapPut("/session-startup-defaults", async (HttpContext context, UpdateSessionStartupDefaultsRequest? request, IAntiforgery antiforgery, ILocalIdentityService identities) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (request is null) return ApiIdentity.Error("VALIDATION_FAILED", "请求体无效。", 400);
    if (!ApiIdentity.TryFontSizeLineHeightMode(request.FontSizeLineHeightMode, out SessionFontSizeLineHeightMode fontSizeLineHeightMode)) return ApiIdentity.Error("INVALID_SESSION_STARTUP_DEFAULTS", "Session 启动默认值无效。", 400);
    if (!ApiIdentity.TryWidthConfiguration(request.WidthMode, request.CustomWidth, out SessionWidthMode widthMode)) return ApiIdentity.Error("INVALID_SESSION_STARTUP_DEFAULTS", "Session 启动默认值无效。", 400);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    try
    {
        SessionStartupDefaults defaults = await identities.UpdateSessionStartupDefaultsAsync(actor, new SessionStartupDefaultsCommand(request.FontFaceId, request.FontSize, request.LineHeight, widthMode, request.CustomWidth, request.ConvertBackslashToYen, fontSizeLineHeightMode), context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ApiIdentity.ToResponse(defaults));
    }
    catch (IdentityValidationException exception) { return ApiIdentity.Error(exception.Code, "Session 启动默认值无效。", 400); }
    catch (KeyNotFoundException) { return ApiIdentity.Error("UNAUTHENTICATED", "需要登录。", 401); }
}).RequireRateLimiting("session-write");

app.MapPost("/api/v1/auth/logout", async (HttpContext context, IAntiforgery antiforgery, ILocalIdentityService identities) =>
{
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    CurrentActor? actor = ApiIdentity.Actor(context);
    if (actor is not null) await identities.LogoutAsync(actor.AuthSessionId, context.RequestAborted).ConfigureAwait(false);
    await context.SignOutAsync().ConfigureAwait(false);
    return Results.NoContent();
});

app.MapPost("/api/v1/auth/change-password", async (HttpContext context, ChangePasswordRequest request, IAntiforgery antiforgery, ILocalIdentityService identities) =>
{
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    CurrentActor? actor = ApiIdentity.Actor(context);
    if (actor is null) return ApiIdentity.Error("UNAUTHENTICATED", "需要登录。", 401);
    try
    {
        LoginResult? result = await identities.ChangePasswordAsync(actor, request.CurrentPassword, request.NewPassword, context.RequestAborted).ConfigureAwait(false);
        if (result is null) return ApiIdentity.Error("INVALID_CREDENTIALS", "邮箱或密码不正确。", 401);
        await context.SignOutAsync().ConfigureAwait(false);
        await ApiIdentity.SignInAsync(context, result).ConfigureAwait(false);
        return Results.NoContent();
    }
    catch (IdentityValidationException exception) { return ApiIdentity.Error(exception.Code, "密码不符合要求。", 400); }
}).RequireAuthorization();

var admin = app.MapGroup("/api/v1/admin/users").RequireAuthorization();
admin.MapGet("", async (HttpContext context, ILocalIdentityService identities, IResourceAuthorizer authorizer) =>
{
    if (await ApiIdentity.RequireAdminAsync(context, authorizer).ConfigureAwait(false) is IResult denied) return denied;
    IReadOnlyList<CurrentUser> users = await identities.ListUsersAsync(context.RequestAborted).ConfigureAwait(false);
    return Results.Ok(new { items = users.Select(ApiIdentity.ToResponse) });
});
admin.MapPost("", async (HttpContext context, CreateUserRequest request, IAntiforgery antiforgery, ILocalIdentityService identities, IResourceAuthorizer authorizer) =>
{
    if (await ApiIdentity.RequireAdminAsync(context, authorizer).ConfigureAwait(false) is IResult denied) return denied;
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    try { CurrentUser user = await identities.CreateUserAsync(new CreateUserCommand(request.Username, request.Email, request.TemporaryPassword, request.Role), ApiIdentity.Actor(context)!, context.RequestAborted).ConfigureAwait(false); return Results.Created($"/api/v1/admin/users/{user.Id}", ApiIdentity.ToResponse(user)); }
    catch (IdentityConflictException exception) { return ApiIdentity.Error(exception.Code, "用户数据冲突。", 409); }
    catch (IdentityValidationException exception) { return ApiIdentity.Error(exception.Code, "用户数据无效。", 400); }
});
admin.MapPatch("/{id}", async (string id, HttpContext context, UpdateUserRequest request, IAntiforgery antiforgery, ILocalIdentityService identities, IResourceAuthorizer authorizer) =>
{
    if (await ApiIdentity.RequireAdminAsync(context, authorizer).ConfigureAwait(false) is IResult denied) return denied;
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    try { CurrentUser user = await identities.UpdateUserAsync(id, new UpdateUserCommand(request.Username, request.Email, request.Role, request.Status, version), ApiIdentity.Actor(context)!, context.RequestAborted).ConfigureAwait(false); return Results.Ok(ApiIdentity.ToResponse(user)); }
    catch (KeyNotFoundException) { return ApiIdentity.Error("NOT_FOUND", "资源不存在。", 404); }
    catch (IdentityConcurrencyException) { return ApiIdentity.Error("STATE_VERSION_CONFLICT", "资源已更新。", 412); }
    catch (IdentityConflictException exception) { return ApiIdentity.Error(exception.Code, "用户数据冲突。", 409); }
    catch (IdentityValidationException exception) { return ApiIdentity.Error(exception.Code, "用户数据无效。", 400); }
});
admin.MapPost("/{id}:reset-password", async (string id, HttpContext context, ResetPasswordRequest request, IAntiforgery antiforgery, ILocalIdentityService identities, IResourceAuthorizer authorizer) =>
{
    if (await ApiIdentity.RequireAdminAsync(context, authorizer).ConfigureAwait(false) is IResult denied) return denied;
    if (string.Equals(ApiIdentity.Actor(context)?.UserId, id, StringComparison.Ordinal)) return ApiIdentity.Error("SELF_PASSWORD_RESET_FORBIDDEN", "请使用修改密码。", 409);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    try { await identities.ResetPasswordAsync(id, request.TemporaryPassword, version, ApiIdentity.Actor(context)!, context.RequestAborted).ConfigureAwait(false); return Results.NoContent(); }
    catch (KeyNotFoundException) { return ApiIdentity.Error("NOT_FOUND", "资源不存在。", 404); }
    catch (IdentityConcurrencyException) { return ApiIdentity.Error("STATE_VERSION_CONFLICT", "资源已更新。", 412); }
    catch (IdentityValidationException exception) { return ApiIdentity.Error(exception.Code, "用户数据无效。", 400); }
});

var adminGames = app.MapGroup("/api/v1/admin/games").RequireAuthorization();
adminGames.MapPost("/{id}:block", async (string id, HttpContext context, SetGameBlockedRequest request, IAntiforgery antiforgery, IGameLibraryService library, IResourceAuthorizer authorizer) =>
{
    CurrentActor? actor = ApiIdentity.Actor(context);
    if (actor is null) return ApiIdentity.Error("UNAUTHENTICATED", "需要登录。", 401);
    ResourceAccessDecision access = await authorizer.AuthorizeAsync(actor, ResourceKind.Game, id, ResourceAction.GameBlock,
        string.Equals(context.User.FindFirstValue("must_change_password"), "true", StringComparison.Ordinal), context.RequestAborted).ConfigureAwait(false);
    if (access == ResourceAccessDecision.PasswordChangeRequired) return ApiIdentity.Error("PASSWORD_CHANGE_REQUIRED", "请先修改密码。", 403);
    if (access != ResourceAccessDecision.Allowed) return ApiIdentity.Error("GAME_NOT_FOUND", "游戏不存在。", 404);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(() => library.SetBlockedAsync(actor, id, request.Blocked, version, context.RequestAborted), Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-write");
adminGames.MapPost("/{id}/diagnostics/{diagnosticId}:override", async (string id, string diagnosticId, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library, IResourceAuthorizer authorizer) =>
{
    if (await ApiIdentity.RequireAdminAsync(context, authorizer).ConfigureAwait(false) is IResult denied) return denied;
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    CurrentActor actor = ApiIdentity.Actor(context)!;
    return await ApiIdentity.GameResultAsync(() => library.OverrideDiagnosticAsync(actor, id, diagnosticId, version, context.RequestAborted), Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-write");

var games = app.MapGroup("/api/v1/games").RequireAuthorization();
games.MapGet("", async (HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return Results.Ok(new { items = await library.ListAsync(actor, context.RequestAborted).ConfigureAwait(false) });
}).RequireRateLimiting("game-read");
games.MapGet("/uploads/{requestId}", async (string requestId, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryIdempotencyKey(requestId, out string key))
        return ApiIdentity.Error("UPLOAD_NOT_FOUND", "上传操作不存在。", StatusCodes.Status404NotFound);
    GameUploadProgressItem? progress = await library.GetUploadProgressAsync(actor, key, context.RequestAborted).ConfigureAwait(false);
    return progress is null
        ? ApiIdentity.Error("UPLOAD_NOT_FOUND", "上传操作不存在。", StatusCodes.Status404NotFound)
        : Results.Ok(progress);
}).RequireRateLimiting("game-read");
games.MapPost("", async (string name, string? visibility, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library, ApiIdempotencyStore idempotency) =>
{
    // Uploads may exceed Kestrel's default 30 MiB limit. The ingestion service
    // still enforces the configured archive limit while streaming.
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = null;
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error("IDEMPOTENCY_KEY_REQUIRED", "需要 Idempotency-Key。", 428);
    try
    {
        return await ApiIdentity.GameResultAsync(async () =>
        {
            IdempotencyExecution<GameLibraryItem> execution = await idempotency.ExecuteAsync(
                actor,
                "game-upload",
                key,
                new { name, visibility = visibility ?? "PRIVATE", context.Request.ContentLength, context.Request.ContentType },
                () => library.UploadAsync(actor, name, visibility ?? "PRIVATE", context.Request.Body, key, context.RequestAborted),
                statusCode: StatusCodes.Status201Created,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return execution.Value;
        }, item => Results.Created($"/api/v1/games/{item.Id}", item)).ConfigureAwait(false);
    }
    catch (GamePackageIngestionException exception)
    {
        return ApiIdentity.Error(exception.Code, GamePackageRejectionMessages.Resolve(exception.Code, exception.LogicalPath), 400);
    }
}).RequireRateLimiting("game-write");
games.MapGet("/{id}", async (string id, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    GameLibraryItem? item = await library.GetAsync(actor, id, context.RequestAborted).ConfigureAwait(false);
    return item is null ? ApiIdentity.Error("GAME_NOT_FOUND", "游戏不存在。", 404) : Results.Ok(item);
}).RequireRateLimiting("game-read");
games.MapPatch("/{id}", async (string id, HttpContext context, UpdateGameRequest request, IAntiforgery antiforgery, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(() => library.UpdateAsync(actor, id, request.Name, request.Visibility, version, context.RequestAborted), Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-write");
games.MapDelete("/{id}", async (string id, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(async () => { await library.DeleteAsync(actor, id, version, context.RequestAborted).ConfigureAwait(false); return true; }, _ => Results.NoContent()).ConfigureAwait(false);
}).RequireRateLimiting("game-write");
games.MapGet("/{id}/files", async (string id, string? scope, string? path, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return await ApiIdentity.GameResultAsync(() => library.ListFilesAsync(actor, id, scope, path, context.RequestAborted), items => Results.Ok(new { items })).ConfigureAwait(false);
}).RequireRateLimiting("game-read");
games.MapGet("/{id}/file", async (string id, string? scope, string path, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return await ApiIdentity.GameResultAsync(async () =>
    {
        GameTextFile file = await library.ReadTextFileAsync(actor, id, scope, path, context.RequestAborted).ConfigureAwait(false);
        if (file.ETag is not null)
            context.Response.Headers.ETag = ApiIdentity.QuoteETag(file.ETag);
        return file;
    }, file => Results.Ok(file)).ConfigureAwait(false);
}).RequireRateLimiting("game-read");
games.MapGet("/{id}/download", async (string id, string? scope, string path, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return await ApiIdentity.GameResultAsync(() => library.OpenDownloadAsync(actor, id, scope, path, context.RequestAborted),
        download => ApiIdentity.SetDownloadHeaders(context, download)).ConfigureAwait(false);
}).RequireRateLimiting("game-read");
games.MapGet("/{id}/diagnostics", async (string id, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return await ApiIdentity.GameResultAsync(() => library.ListDiagnosticsAsync(actor, id, context.RequestAborted), items => Results.Ok(new { items })).ConfigureAwait(false);
}).RequireRateLimiting("game-read");
var sessions = app.MapGroup("/api/v1/sessions").RequireAuthorization();
sessions.MapPost("", async (HttpContext context, CreateSessionRequest? request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IAntiforgery antiforgery, ISessionApplicationService service, SessionCommandReadiness readiness) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!readiness.IsReady) return ApiIdentity.Error(SessionErrorCodes.ServiceNotReady, "Session 控制面尚未完成恢复。", 503);
    if (request is null) return ApiIdentity.Error("VALIDATION_FAILED", "请求体无效。", 400);
    if (!ApiIdentity.TryFontSizeLineHeightMode(request.FontSizeLineHeightMode, out SessionFontSizeLineHeightMode fontSizeLineHeightMode)) return ApiIdentity.Error(SessionErrorCodes.ValidationFailed, "Session 字号/行高模式无效。", 400);
    if (!ApiIdentity.TryWidthConfiguration(request.WidthMode, request.CustomWidth, out SessionWidthMode widthMode)) return ApiIdentity.Error(SessionErrorCodes.ValidationFailed, "Session 宽度配置无效。", 400);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(idempotencyKey, out string key)) return ApiIdentity.Error(SessionErrorCodes.IdempotencyKeyRequired, "需要 Idempotency-Key。", 428);
    try
    {
        SessionCommandResult result = await service.CreateAsync(actor, new CreateSessionCommand(request.GameId, request.Name, key, request.FontSize, request.LineHeight, request.FontFaceId, widthMode, request.CustomWidth, request.ConvertBackslashToYen, fontSizeLineHeightMode), context.RequestAborted).ConfigureAwait(false);
        return ApiIdentity.SessionCommand(context, result);
    }
    catch (SessionApplicationException exception)
    {
        return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode);
    }
}).RequireRateLimiting("session-write")
  .Accepts<CreateSessionRequest>("application/json")
  .Produces<SessionResponse>(StatusCodes.Status201Created)
  .Produces<SessionResponse>(StatusCodes.Status202Accepted)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status413PayloadTooLarge)
  .Produces<ApiError>(StatusCodes.Status428PreconditionRequired)
  .Produces<ApiError>(StatusCodes.Status429TooManyRequests)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapGet("", async (string? gameId, string? state, string? cursor, int? limit, HttpContext context, ISessionApplicationService service) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    SessionState? parsedState = null;
    if (!string.IsNullOrWhiteSpace(state))
    {
        if (!Enum.TryParse(state, ignoreCase: true, out SessionState value) || !Enum.IsDefined(value))
            return ApiIdentity.Error(SessionErrorCodes.ValidationFailed, "Session state 无效。", 400);
        parsedState = value;
    }
    try
    {
        SessionListPage page = await service.ListAsync(actor, new SessionListQuery(gameId, parsedState, cursor, limit ?? 50), context.RequestAborted).ConfigureAwait(false);
        ApiIdentity.SetSessionPageETag(context, page);
        return Results.Ok(ApiIdentity.ToResponse(page));
    }
    catch (SessionApplicationException exception)
    {
        return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode);
    }
}).RequireRateLimiting("session-read")
  .Produces<SessionListResponse>(StatusCodes.Status200OK)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status401Unauthorized)
  .Produces<ApiError>(StatusCodes.Status429TooManyRequests);

sessions.MapPut("/{sessionId}/configuration", async (string sessionId, HttpContext context, UpdateSessionConfigurationRequest? request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IAntiforgery antiforgery, ISessionApplicationService service, SessionCommandReadiness readiness) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!readiness.IsReady) return ApiIdentity.Error(SessionErrorCodes.ServiceNotReady, "Session 控制面尚未完成恢复。", 503);
    if (request is null) return ApiIdentity.Error(SessionErrorCodes.ValidationFailed, "请求体无效。", 400);
    if (!ApiIdentity.TryFontSizeLineHeightMode(request.FontSizeLineHeightMode, out SessionFontSizeLineHeightMode fontSizeLineHeightMode)) return ApiIdentity.Error(SessionErrorCodes.ValidationFailed, "Session 字号/行高模式无效。", 400);
    if (!ApiIdentity.TryWidthConfiguration(request.WidthMode, request.CustomWidth, out SessionWidthMode widthMode)) return ApiIdentity.Error(SessionErrorCodes.ValidationFailed, "Session 宽度配置无效。", 400);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(idempotencyKey, out string key)) return ApiIdentity.Error(SessionErrorCodes.IdempotencyKeyRequired, "需要 Idempotency-Key。", 428);
    try { return ApiIdentity.SessionCommand(context, await service.UpdateConfigurationAsync(actor, new SessionConfigurationCommand(sessionId, request.Name, request.FontSize, request.LineHeight, key, request.FontFaceId, widthMode, request.CustomWidth, request.ConvertBackslashToYen, fontSizeLineHeightMode), context.RequestAborted).ConfigureAwait(false)); }
    catch (SessionApplicationException exception) { return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode); }
}).RequireRateLimiting("session-write");

sessions.MapGet("/{sessionId}", async (string sessionId, HttpContext context, ISessionApplicationService service) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    try
    {
        SessionView? view = await service.GetAsync(actor, sessionId, context.RequestAborted).ConfigureAwait(false);
        if (view is null) return ApiIdentity.Error(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404);
        ApiIdentity.SetSessionETag(context, view);
        return Results.Ok(ApiIdentity.ToResponse(view));
    }
    catch (SessionApplicationException exception)
    {
        return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode);
    }
}).RequireRateLimiting("session-read")
  .Produces<SessionResponse>(StatusCodes.Status200OK)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status401Unauthorized)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status429TooManyRequests);

sessions.MapDelete("/{sessionId}", async (string sessionId, HttpContext context, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IAntiforgery antiforgery, ISessionApplicationService service, SessionCommandReadiness readiness) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!readiness.IsReady) return ApiIdentity.Error(SessionErrorCodes.ServiceNotReady, "Session 控制面尚未完成恢复。", StatusCodes.Status503ServiceUnavailable);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false))
        return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", StatusCodes.Status400BadRequest);
    if (!ApiIdentity.TryIdempotencyKey(idempotencyKey, out string key))
        return ApiIdentity.Error(SessionErrorCodes.IdempotencyKeyRequired, "需要 Idempotency-Key。", StatusCodes.Status428PreconditionRequired);
    try
    {
        SessionDeleteResult result = await service.DeleteAsync(actor, new SessionDeleteCommand(sessionId, key), context.RequestAborted).ConfigureAwait(false);
        return ApiIdentity.SessionDelete(result);
    }
    catch (SessionApplicationException exception)
    {
        return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode);
    }
}).RequireRateLimiting("session-write")
  .Produces(StatusCodes.Status204NoContent)
  .Produces(StatusCodes.Status202Accepted)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status428PreconditionRequired)
  .Produces<ApiError>(StatusCodes.Status429TooManyRequests)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapGet("/{sessionId}/presentation-manifest", async (string sessionId, HttpContext context, ISessionAssetService service) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    try
    {
        SessionPresentationManifest manifest = await service.GetManifestAsync(actor, sessionId, context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Ok(manifest);
    }
    catch (SessionAssetException exception)
    {
        if (exception.Code == SessionAssetErrorCodes.CapacityExceeded)
        {
            context.Response.Headers.RetryAfter = "1";
        }
        return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode);
    }
}).RequireRateLimiting("session-read")
  .Produces<SessionPresentationManifest>(StatusCodes.Status200OK)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapGet("/{sessionId}/assets/{assetId}", async (string sessionId, string assetId, HttpContext context, ISessionAssetService service, PresentationAssetOptions assetOptions) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    try
    {
        SessionAssetRead asset = await service.OpenReadAsync(actor, sessionId, assetId, context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.AcceptRanges = "bytes";

        if (!ApiIdentity.TrySingleRange(context.Request.Headers.Range.ToString(), asset.ByteLength, out long start, out long length))
        {
            await asset.Content.DisposeAsync().ConfigureAwait(false);
            context.Response.Headers.ContentRange = $"bytes */{asset.ByteLength}";
            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }
        if (!string.IsNullOrWhiteSpace(context.Request.Headers.Range) && length > assetOptions.MaxRangeBytes)
        {
            await asset.Content.DisposeAsync().ConfigureAwait(false);
            return ApiIdentity.Error(SessionAssetErrorCodes.RangeTooLarge, "资源范围超过实例上限。", StatusCodes.Status416RangeNotSatisfiable);
        }
        if (start > 0 || length != asset.ByteLength)
        {
            asset.Content.Seek(start, SeekOrigin.Begin);
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {start}-{start + length - 1}/{asset.ByteLength}";
            context.Response.ContentLength = length;
            return Results.Stream(new CloudEmuera.Api.BoundedReadStream(asset.Content, length), asset.MediaType, enableRangeProcessing: false);
        }
        context.Response.ContentLength = asset.ByteLength;
        return Results.Stream(asset.Content, asset.MediaType, enableRangeProcessing: false);
    }
    catch (SessionAssetException exception)
    {
        if (exception.Code == SessionAssetErrorCodes.CapacityExceeded)
        {
            context.Response.Headers.RetryAfter = "1";
        }
        return ApiIdentity.Error(exception.Code, exception.Message, exception.StatusCode);
    }
}).RequireRateLimiting("session-read")
  .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
  .Produces(StatusCodes.Status206PartialContent, contentType: "application/octet-stream")
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status416RangeNotSatisfiable)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapPost("/{sessionId}:open", async (string sessionId, HttpContext context, JsonElement body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IAntiforgery antiforgery, ISessionApplicationService service, SessionCommandReadiness readiness) =>
{
    return await ApiIdentity.ExecuteSessionLifecycleAsync(context, sessionId, body, idempotencyKey, antiforgery, readiness, service.OpenAsync, requireBrowserWidth: true).ConfigureAwait(false);
}).RequireRateLimiting("session-write")
  .Accepts<JsonElement>("application/json")
  .Produces<SessionResponse>(StatusCodes.Status200OK)
  .Produces<SessionResponse>(StatusCodes.Status202Accepted)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status428PreconditionRequired)
  .Produces<ApiError>(StatusCodes.Status429TooManyRequests)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapPost("/{sessionId}:close", async (string sessionId, HttpContext context, JsonElement body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, IAntiforgery antiforgery, ISessionApplicationService service, SessionCommandReadiness readiness) =>
{
    return await ApiIdentity.ExecuteSessionLifecycleAsync(context, sessionId, body, idempotencyKey, antiforgery, readiness, service.CloseAsync).ConfigureAwait(false);
}).RequireRateLimiting("session-write")
  .Accepts<JsonElement>("application/json")
  .Produces<SessionResponse>(StatusCodes.Status200OK)
  .Produces<SessionResponse>(StatusCodes.Status202Accepted)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status428PreconditionRequired)
  .Produces<ApiError>(StatusCodes.Status429TooManyRequests)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapGet("/{sessionId}/saves", async (string sessionId, HttpContext context, ISessionSaveApplicationService service) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    try
    {
        SessionSaveList list = await service.ListAsync(actor, sessionId, context.RequestAborted).ConfigureAwait(false);
        return Results.Ok(ApiIdentity.ToResponse(list));
    }
    catch (SessionSaveException exception)
    {
        return ApiIdentity.SaveError(exception);
    }
}).RequireRateLimiting("session-read")
  .Produces<SaveListResponse>(StatusCodes.Status200OK)
  .Produces<ApiError>(StatusCodes.Status401Unauthorized)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapGet("/{sessionId}/saves/{**path}", async (string sessionId, string path, HttpContext context, ISessionSaveApplicationService service) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    try
    {
        SessionSaveDownload download = await service.OpenReadAsync(actor, sessionId, path, context.RequestAborted).ConfigureAwait(false);
        return ApiIdentity.SetSaveDownloadHeaders(context, download);
    }
    catch (SessionSaveException exception)
    {
        return ApiIdentity.SaveError(exception);
    }
}).RequireRateLimiting("session-read")
  .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapPut("/{sessionId}/saves/{**path}", async (string sessionId, string path, HttpContext context, IAntiforgery antiforgery, ISessionSaveApplicationService service, SaveOperationRecoveryReadiness readiness) =>
{
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = null;
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!readiness.IsReady) return ApiIdentity.Error(SaveErrorCodes.RecoveryRequired, "存档操作恢复尚未完成。", StatusCodes.Status503ServiceUnavailable);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error(SaveErrorCodes.IdempotencyKeyRequired, "需要 Idempotency-Key。", 428);
    try
    {
        SessionSaveMutationResult result = await service.ImportAsync(new SaveImportCommand(actor, sessionId, path, context.Request.Body, context.Request.ContentLength, key), context.RequestAborted).ConfigureAwait(false);
        return ApiIdentity.SaveMutation(context, sessionId, result);
    }
    catch (SessionSaveException exception)
    {
        return ApiIdentity.SaveError(exception);
    }
}).RequireRateLimiting("session-write")
  .Accepts<Stream>("application/octet-stream")
  .Produces<SaveItemResponse>(StatusCodes.Status201Created)
  .Produces(StatusCodes.Status204NoContent)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status413PayloadTooLarge)
  .Produces<ApiError>(StatusCodes.Status415UnsupportedMediaType)
  .Produces<ApiError>(StatusCodes.Status428PreconditionRequired)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapPatch("/{sessionId}/saves/{**path}", async (string sessionId, string path, HttpContext context, JsonElement body, IAntiforgery antiforgery, ISessionSaveApplicationService service, SaveOperationRecoveryReadiness readiness) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!readiness.IsReady) return ApiIdentity.Error(SaveErrorCodes.RecoveryRequired, "存档操作恢复尚未完成。", StatusCodes.Status503ServiceUnavailable);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error(SaveErrorCodes.IdempotencyKeyRequired, "需要 Idempotency-Key。", 428);
    if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("targetPath", out JsonElement targetProperty) || targetProperty.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(targetProperty.GetString()))
        return ApiIdentity.Error(SaveErrorCodes.PathInvalid, "存档路径无效。", 400);
    try
    {
        SessionSaveMutationResult result = await service.RenameAsync(new SaveRenameCommand(actor, sessionId, path, targetProperty.GetString()!, key), context.RequestAborted).ConfigureAwait(false);
        return ApiIdentity.SaveMutation(context, sessionId, result);
    }
    catch (SessionSaveException exception)
    {
        return ApiIdentity.SaveError(exception);
    }
}).RequireRateLimiting("session-write")
  .Accepts<RenameSaveRequest>("application/json")
  .Produces(StatusCodes.Status204NoContent)
  .Produces<ApiError>(StatusCodes.Status400BadRequest)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status428PreconditionRequired)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

sessions.MapDelete("/{sessionId}/saves/{**path}", async (string sessionId, string path, HttpContext context, IAntiforgery antiforgery, ISessionSaveApplicationService service, SaveOperationRecoveryReadiness readiness) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!readiness.IsReady) return ApiIdentity.Error(SaveErrorCodes.RecoveryRequired, "存档操作恢复尚未完成。", StatusCodes.Status503ServiceUnavailable);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error(SaveErrorCodes.IdempotencyKeyRequired, "需要 Idempotency-Key。", 428);
    bool confirmed = string.Equals(context.Request.Headers["X-Confirm-Delete"].ToString(), "true", StringComparison.Ordinal);
    try
    {
        SessionSaveMutationResult result = await service.DeleteAsync(new SaveDeleteCommand(actor, sessionId, path, key, confirmed), context.RequestAborted).ConfigureAwait(false);
        return ApiIdentity.SaveMutation(context, sessionId, result);
    }
    catch (SessionSaveException exception)
    {
        return ApiIdentity.SaveError(exception);
    }
}).RequireRateLimiting("session-write")
  .Produces(StatusCodes.Status204NoContent)
  .Produces<ApiError>(StatusCodes.Status404NotFound)
  .Produces<ApiError>(StatusCodes.Status409Conflict)
  .Produces<ApiError>(StatusCodes.Status428PreconditionRequired)
  .Produces<ApiError>(StatusCodes.Status503ServiceUnavailable);

app.MapFallback("/api/{**path}", () => ApiIdentity.Error("NOT_FOUND", "资源不存在。", StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;

internal static class ValidatorAssemblyResolver
{
    public static string ResolveSiblingAssembly(string contentRoot, string projectName, string assemblyName)
    {
        string? currentConfiguration = GetCurrentConfiguration(typeof(Program).Assembly.Location);
        string[] configurations = currentConfiguration switch
        {
            "Debug" => ["Debug", "Release"],
            "Release" => ["Release", "Debug"],
            _ => ["Debug", "Release"]
        };
        DirectoryInfo? current = new DirectoryInfo(contentRoot);
        while (current is not null)
        {
            foreach (string configuration in configurations)
            {
                string candidate = Path.Combine(current.FullName, "src", projectName, "bin", configuration, "net10.0", assemblyName);
                if (File.Exists(candidate)) return candidate;
            }
            current = current.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, assemblyName);
    }

    private static string? GetCurrentConfiguration(string assemblyPath)
    {
        DirectoryInfo? netTarget = new FileInfo(assemblyPath).Directory;
        string? configuration = netTarget?.Parent?.Name;
        return configuration is "Debug" or "Release" ? configuration : null;
    }

    /// <summary>
    /// Dev/test containers run with the API project directory as the content root;
    /// walk up to the repository root to locate the pinned Validator project. Prefer
    /// the build configuration matching the host environment and fall back to the
    /// other configuration so tests can run against whichever DLL exists. Published
    /// production layouts place the validator side-by-side with the API.
    /// </summary>
    public static string Resolve(string contentRoot, string preferredConfiguration)
    {
        string[] configurations = preferredConfiguration == "Release" ? ["Release", "Debug"] : ["Debug", "Release"];
        DirectoryInfo? current = new DirectoryInfo(contentRoot);
        while (current is not null)
        {
            foreach (string configuration in configurations)
            {
                string candidate = Path.Combine(current.FullName, "src", "CloudEmuera.Validator", "bin", configuration, "net10.0", "CloudEmuera.Validator.dll");
                if (File.Exists(candidate)) return candidate;
            }
            current = current.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "CloudEmuera.Validator.dll");
    }
}

internal static class ApiIdentity
{
    public static IResult Error(string code, string message, int status) => Error(code, message, status, null);
    public static IResult Error(string code, string message, int status, object? details) => Results.Json(new ApiError(code, message, RequestCorrelation.Current ?? $"req_{Guid.CreateVersion7():N}", details), statusCode: status);
    public static Task WriteErrorAsync(HttpContext context, string code, string message, int status)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new ApiError(code, message, RequestCorrelation.Current ?? context.TraceIdentifier));
    }
    public static CurrentUserResponse ToResponse(CurrentUser value) => new(value.Id, value.Username, value.Email, value.Role, value.Status, value.MustChangePassword, value.StateVersion);
    public static SessionStartupDefaultsResponse ToResponse(SessionStartupDefaults value) => new(value.FontFaceId, value.FontSize, value.LineHeight, WidthModeName(value.WidthMode), value.CustomWidth, value.ConvertBackslashToYen, FontSizeLineHeightModeName(value.FontSizeLineHeightMode));
    public static bool TryFontSizeLineHeightMode(string? value, out SessionFontSizeLineHeightMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim().ToUpperInvariant();
        mode = normalized switch
        {
            "OVERRIDE" => SessionFontSizeLineHeightMode.Override,
            "CONFIG" => SessionFontSizeLineHeightMode.Config,
            _ => default,
        };
        return normalized is "OVERRIDE" or "CONFIG";
    }

    public static string FontSizeLineHeightModeName(SessionFontSizeLineHeightMode mode) => mode switch
    {
        SessionFontSizeLineHeightMode.Override => "OVERRIDE",
        SessionFontSizeLineHeightMode.Config => "CONFIG",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    public static bool TryWidthConfiguration(string? value, int? customWidth, out SessionWidthMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim().ToUpperInvariant();
        mode = normalized switch
        {
            "ORIGINAL" => SessionWidthMode.Original,
            "MAX" => SessionWidthMode.Max,
            "ADAPTIVE" or "ORIGIN" => SessionWidthMode.Adaptive,
            "CUSTOM" => SessionWidthMode.Custom,
            _ => default,
        };
        return (normalized is "ORIGINAL" or "MAX" or "ADAPTIVE" or "ORIGIN" or "CUSTOM") &&
            SessionWidthConfiguration.IsValid(mode, customWidth);
    }

    public static string WidthModeName(SessionWidthMode mode) => mode switch
    {
        SessionWidthMode.Original => "ORIGINAL",
        SessionWidthMode.Max => "MAX",
        SessionWidthMode.Adaptive => "ADAPTIVE",
        SessionWidthMode.Custom => "CUSTOM",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    public static CurrentActor? Actor(HttpContext context)
    {
        string? id = context.User.FindFirstValue(ClaimTypes.NameIdentifier); string? role = context.User.FindFirstValue(ClaimTypes.Role); string? session = context.User.FindFirstValue("auth_session_id");
        return id is null || role is null || session is null ? null : new CurrentActor(id, role, session);
    }
    public static CurrentActor? GameActor(HttpContext context) =>
        string.Equals(context.User.FindFirstValue("must_change_password"), "true", StringComparison.Ordinal) ? null : Actor(context);

    public static IResult GameActorError(HttpContext context) => Actor(context) is null
        ? Error("UNAUTHENTICATED", "需要登录。", StatusCodes.Status401Unauthorized)
        : Error("PASSWORD_CHANGE_REQUIRED", "请先修改密码。", StatusCodes.Status403Forbidden);

    public static SessionResponse ToResponse(SessionView value) => new(
        value.SchemaVersion,
        value.Id,
        value.Name,
        new SessionGameResponse(value.Game.Id, value.Game.Name),
        value.SourceContentDigest,
        value.SourceContentRevision,
        value.RuntimeVersion,
        value.FontFaceId,
        value.FontSize,
        value.LineHeight,
        FontSizeLineHeightModeName(value.FontSizeLineHeightMode),
        WidthModeName(value.WidthMode),
        value.CustomWidth,
        value.ConvertBackslashToYen,
        value.State.ToString().ToUpperInvariant(),
        value.StateVersion,
        value.WorkerEpoch,
        value.WaitingForInput,
        value.CreatedAt,
        value.StartedAt,
        value.LastActivityAt,
        value.ClosedAt,
        value.CloseReason);

    public static SessionListResponse ToResponse(SessionListPage value) =>
        new(value.Items.Select(ToResponse).ToArray(), value.NextCursor);

    public static AdminRuntimeResponse ToResponse(AdminRuntimeSnapshot value) => new(
        value.SchemaVersion,
        value.ObservedAt,
        new AdminInstanceResponse(
            value.Instance.ControlPlaneState,
            value.Instance.ActiveWorkerCount,
            value.Instance.WebSocketConnectionCount,
            value.Instance.SubscriptionCount),
        value.Workers.Select(worker => new AdminWorkerResponse(
            new AdminSessionResponse(
                worker.Session.Id,
                worker.Session.Name,
                worker.Session.OwnerUsername,
                worker.Session.GameId,
                worker.Session.GameName,
                worker.Session.State,
                worker.Session.StateVersion,
                worker.Session.LastActivityAt),
            new AdminWorkerProcessResponse(
                worker.Worker.WorkerId,
                worker.Worker.Pid,
                worker.Worker.WorkerEpoch,
                worker.Worker.LeaseStatus,
                worker.Worker.HeartbeatAt,
                worker.Worker.HeartbeatAgeMilliseconds,
                worker.Worker.Registered,
                worker.Worker.Ready,
                worker.Worker.ProcessExited,
                worker.Worker.LastOutputSequence),
            new AdminRealtimeResponse(
                worker.Realtime.HubState,
                worker.Realtime.SnapshotSequence,
                worker.Realtime.SnapshotBytes,
                worker.Realtime.SnapshotSizeStatus,
                worker.Realtime.SubscriptionCount,
                worker.Realtime.ResyncCount,
                worker.Realtime.SoftOverflowCount,
                worker.Realtime.HardOverflowCount,
                worker.Realtime.FaultCount,
                worker.Realtime.DroppedPendingEventCount),
            worker.RuntimeConsistency)).ToArray(),
        value.RecentFailures.Select(failure => new AdminFailureResponse(
            failure.SessionId,
            failure.SessionName,
            failure.OwnerUsername,
            failure.GameId,
            failure.GameName,
            failure.WorkerEpoch,
            failure.FailedAt,
            failure.ReasonCode)).ToArray());

    public static void SetSessionETag(HttpContext context, SessionView value) =>
        context.Response.Headers.ETag = QuoteETag(value.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static void SetSessionPageETag(HttpContext context, SessionListPage value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value.Items.Select(item => new { item.Id, item.StateVersion, item.LastActivityAt }));
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        context.Response.Headers.ETag = QuoteETag($"sha256:{digest}");
    }

    public static IResult SessionCommand(HttpContext context, SessionCommandResult result)
    {
        if (!result.Succeeded)
        {
            SessionCommandFailure failure = result.Failure ?? new("SESSION_COMMAND_FAILED", "Session 操作失败。", 503);
            return Error(failure.Code, failure.Message, failure.StatusCode, failure.Details);
        }

        SessionView value = result.Value!;
        SetSessionETag(context, value);
        context.Response.Headers.Location = $"/api/v1/sessions/{value.Id}";
        SessionResponse response = ToResponse(value);
        return result.StatusCode == StatusCodes.Status201Created
            ? Results.Created($"/api/v1/sessions/{value.Id}", response)
            : Results.Json(response, statusCode: result.StatusCode);
    }

    public static IResult SessionDelete(SessionDeleteResult result)
    {
        if (result.Failure is SessionCommandFailure failure)
            return Error(failure.Code, failure.Message, failure.StatusCode, failure.Details);
        return result.Pending
            ? Results.Json(new { pending = true }, statusCode: StatusCodes.Status202Accepted)
            : Results.NoContent();
    }

    public static async Task<IResult> ExecuteSessionLifecycleAsync(
        HttpContext context,
        string sessionId,
        JsonElement body,
        string? idempotencyKey,
        IAntiforgery antiforgery,
        SessionCommandReadiness readiness,
        Func<CurrentActor, SessionLifecycleCommand, CancellationToken, Task<SessionCommandResult>> operation,
        bool requireBrowserWidth = false)
    {
        if (GameActor(context) is not CurrentActor actor) return GameActorError(context);
        if (!readiness.IsReady) return Error(SessionErrorCodes.ServiceNotReady, "Session 控制面尚未完成恢复。", StatusCodes.Status503ServiceUnavailable);
        int browserWidth = 0;
        if (body.ValueKind != JsonValueKind.Object)
            return Error(SessionErrorCodes.ValidationFailed, "生命周期请求体必须是 JSON 对象。", 400);
        foreach (JsonProperty property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, "browserWidth", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out browserWidth) && browserWidth is >= 240 and <= 16_384)
                continue;
            return Error(SessionErrorCodes.ValidationFailed, "生命周期请求只能包含 240 到 16384 之间的 browserWidth。", 400);
        }
        if (body.EnumerateObject().Any() && !body.TryGetProperty("browserWidth", out _))
            return Error(SessionErrorCodes.ValidationFailed, "生命周期请求体字段无效。", 400);
        if (requireBrowserWidth && browserWidth == 0)
            return Error(SessionErrorCodes.ValidationFailed, "启动 Session 必须提供浏览器宽度。", 400);
        if (!await ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false))
            return Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
        if (!TryIdempotencyKey(idempotencyKey, out string key))
            return Error(SessionErrorCodes.IdempotencyKeyRequired, "需要 Idempotency-Key。", 428);
        try
        {
            SessionCommandResult result = await operation(actor, new SessionLifecycleCommand(sessionId, key, browserWidth), context.RequestAborted).ConfigureAwait(false);
            return SessionCommand(context, result);
        }
        catch (SessionApplicationException exception)
        {
            return Error(exception.Code, exception.Message, exception.StatusCode);
        }
    }

    public static async Task<IResult> GameResultAsync<T>(Func<Task<T>> action, Func<T, IResult> success)
    {
        try { return success(await action().ConfigureAwait(false)); }
        catch (GameLibraryException exception)
        {
            int status = exception.Code switch
            {
                GameLibraryErrorCodes.NotFound or GameLibraryErrorCodes.FileNotFound => 404,
                GameLibraryErrorCodes.StateVersionConflict => 412,
                GameLibraryErrorCodes.Conflict or GameLibraryErrorCodes.NameConflict or GameLibraryErrorCodes.InUse
                    or GameLibraryErrorCodes.HasNoCurrentContent or GameLibraryErrorCodes.ValidationInProgress
                    or GameLibraryErrorCodes.ActivationInProgress => 409,
                GameLibraryErrorCodes.ValidationFailed or GameLibraryErrorCodes.ActivationValidationFailed
                    or GameLibraryErrorCodes.FileTooLargeToRead or GameLibraryErrorCodes.TextEncodingUnsupported => 422,
                GameLibraryErrorCodes.IdempotencyConflict => 409,
                GameLibraryErrorCodes.DiagnosticOverrideNotAllowed => 409,
                _ => 400,
            };
            return Error(exception.Code, exception.Message, status);
        }
    }
    public static async Task<IResult?> RequireAdminAsync(HttpContext context, IResourceAuthorizer authorizer)
    {
        CurrentActor? actor = Actor(context);
        if (actor is null) return Error("UNAUTHENTICATED", "需要登录。", StatusCodes.Status401Unauthorized);
        ResourceAccessDecision decision = await authorizer.AuthorizeAsync(actor, ResourceKind.User, actor.UserId, ResourceAction.UserAdminister,
            string.Equals(context.User.FindFirstValue("must_change_password"), "true", StringComparison.Ordinal), context.RequestAborted).ConfigureAwait(false);
        return decision switch
        {
            ResourceAccessDecision.Allowed => null,
            ResourceAccessDecision.PasswordChangeRequired => Error("PASSWORD_CHANGE_REQUIRED", "请先修改密码。", StatusCodes.Status403Forbidden),
            _ => Error("FORBIDDEN", "没有权限。", StatusCodes.Status403Forbidden),
        };
    }
    public static async Task SignInAsync(HttpContext context, LoginResult result)
    {
        Claim[] claims = [new(ClaimTypes.NameIdentifier, result.User.Id), new(ClaimTypes.Role, result.User.Role), new("must_change_password", result.User.MustChangePassword ? "true" : "false"), new("auth_session_id", result.AuthSessionId), new("security_stamp", context.RequestServices.GetRequiredService<CloudEmueraDbContext>().AuthSessions.Local.FirstOrDefault(x => x.Id == result.AuthSessionId)?.SecurityStamp ?? string.Empty)];
        // The session is tracked in the current DbContext during login; load it when a different service created it.
        if (string.IsNullOrEmpty(claims[^1].Value))
        {
            AuthSessionRow session = await context.RequestServices.GetRequiredService<CloudEmueraDbContext>().AuthSessions.SingleAsync(row => row.Id == result.AuthSessionId, context.RequestAborted).ConfigureAwait(false);
            claims[^1] = new Claim("security_stamp", session.SecurityStamp);
        }
        ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = result.AuthSessionId.Length > 0 && result.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1), ExpiresUtc = result.ExpiresAt }).ConfigureAwait(false);
    }
    public static async Task<bool> ValidateCsrfAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
    public static bool TryVersion(HttpRequest request, out int version)
    {
        string stateHeader = request.Headers["X-Game-State-Version"].ToString();
        if (int.TryParse(stateHeader, out version)) return true;
        return int.TryParse(request.Headers.IfMatch.ToString().Trim('"'), out version);
    }

    public static bool TryFilePrecondition(HttpRequest request, out string? etag, out bool requireAbsent)
    {
        string ifNoneMatch = request.Headers.IfNoneMatch.ToString().Trim();
        requireAbsent = string.Equals(ifNoneMatch, "*", StringComparison.Ordinal);
        string ifMatch = request.Headers["X-File-If-Match"].ToString();
        if (string.IsNullOrWhiteSpace(ifMatch)) ifMatch = request.Headers.IfMatch.ToString();
        etag = int.TryParse(ifMatch.Trim('"'), out _) ? null : ifMatch.Trim();
        if (etag is not null && !(etag.StartsWith("\"sha256:", StringComparison.Ordinal) && etag.EndsWith('"')))
            etag = null;
        return requireAbsent ^ etag is not null;
    }

    public static bool TryIdempotencyKey(HttpRequest request, out string key)
    {
        return TryIdempotencyKey(request.Headers["Idempotency-Key"].ToString(), out key);
    }

    public static bool TryIdempotencyKey(string? value, out string key)
    {
        key = (value ?? string.Empty).Trim();
        return key.Length is > 0 and <= 256 && !key.Any(char.IsControl);
    }

    public static bool TryAdminIdempotencyKey(string? value, out string key)
    {
        key = (value ?? string.Empty).Trim();
        return key.Length is >= 8 and <= 128 && !key.Any(char.IsControl);
    }

    public static string QuoteETag(string etag) => etag.StartsWith('"') ? etag : $"\"{etag}\"";

    public static IResult SetDownloadHeaders(HttpContext context, GameFileDownload download)
    {
        if (download.ETag is not null)
            context.Response.Headers.ETag = QuoteETag(download.ETag);
        return Results.Stream(download.Content, "application/octet-stream", download.FileName, enableRangeProcessing: false);
    }

    public static SaveListResponse ToResponse(SessionSaveList value) => new(
        value.SchemaVersion,
        value.Layout switch
        {
            SessionSaveLayout.Root => "ROOT",
            SessionSaveLayout.SavDirectory => "SAV_DIRECTORY",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        },
        value.Items.Select(item => new SaveItemResponse(
            item.Path,
            item.Kind.ToString().ToUpperInvariant(),
            item.SizeBytes,
            item.ModifiedAt)).ToArray());

    public static IResult SaveError(SessionSaveException exception) =>
        Error(exception.Code, SaveErrorMessage(exception.Code), exception.StatusCode);

    public static IResult SaveMutation(HttpContext context, string sessionId, SessionSaveMutationResult result)
    {
        if (result.StatusCode == StatusCodes.Status204NoContent)
            return Results.NoContent();
        if (result.Item is not SessionSaveItem item)
            return Results.StatusCode(result.StatusCode);
        string locationPath = string.Join('/', item.Path.Split('/', StringSplitOptions.None).Select(Uri.EscapeDataString));
        return Results.Created($"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/saves/{locationPath}", new SaveItemResponse(
            item.Path,
            item.Kind.ToString().ToUpperInvariant(),
            item.SizeBytes,
            item.ModifiedAt));
    }

    public static IResult SetSaveDownloadHeaders(HttpContext context, SessionSaveDownload download)
    {
        string fileName = download.Path.Split('/', StringSplitOptions.None)[^1];
        if (fileName.Any(char.IsControl) || fileName.Contains('\r') || fileName.Contains('\n'))
            fileName = "download.bin";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(download.Content, "application/octet-stream", fileName, enableRangeProcessing: false);
    }

    public static bool TrySingleRange(string header, long totalLength, out long start, out long length)
    {
        start = 0;
        length = totalLength;
        if (string.IsNullOrWhiteSpace(header)) return totalLength >= 0;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header[6..].Contains(',')) return false;
        string value = header[6..].Trim();
        int dash = value.IndexOf('-');
        if (dash < 0 || totalLength <= 0) return false;
        string first = value[..dash].Trim();
        string last = value[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!long.TryParse(last, out long suffix) || suffix <= 0) return false;
            length = Math.Min(suffix, totalLength);
            start = totalLength - length;
            return true;
        }
        if (!long.TryParse(first, out start) || start < 0 || start >= totalLength) return false;
        long end = totalLength - 1;
        if (last.Length > 0 && (!long.TryParse(last, out end) || end < start)) return false;
        end = Math.Min(end, totalLength - 1);
        length = end - start + 1;
        return length > 0;
    }

    private static string SaveErrorMessage(string code) => code switch
    {
        SaveErrorCodes.PathInvalid => "存档路径无效。",
        SaveErrorCodes.NotFound or SaveErrorCodes.SessionNotFound => "资源不存在。",
        SaveErrorCodes.SessionNotQuiescent => "Session 必须处于静止状态。",
        SaveErrorCodes.SessionHasActiveWorker => "Session 仍有活动 Worker。",
        SaveErrorCodes.MutationInProgress => "Session 的另一个存档操作正在执行。",
        SaveErrorCodes.IdempotencyKeyRequired => "需要 Idempotency-Key。",
        SaveErrorCodes.IdempotencyKeyReused => "Idempotency-Key 已用于其他存档请求。",
        SaveErrorCodes.FileTooLarge => "存档文件超过大小上限。",
        SaveErrorCodes.ListLimitExceeded => "存档列表超过实例容量限制。",
        SaveErrorCodes.FormatInvalid => "存档文件格式无效。",
        SaveErrorCodes.DeleteConfirmationRequired => "删除存档需要显式确认。",
        SaveErrorCodes.TargetExists => "重命名目标已存在。",
        SaveErrorCodes.DataRootSpaceLow => "数据目录可用空间不足。",
        SaveErrorCodes.RecoveryRequired => "存档操作等待恢复。",
        _ => "存档服务暂时不可用。",
    };
}
