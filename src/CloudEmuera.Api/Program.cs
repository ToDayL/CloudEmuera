using System.Security.Claims;
using CloudEmuera.Api.Bootstrap;
using CloudEmuera.Api.Security;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using CloudEmuera.Contracts;
using CloudEmuera.Contracts.Identity;
using CloudEmuera.Contracts.Games;
using CloudEmuera.Application.Games;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Identity;
using CloudEmuera.Infrastructure.Authorization;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Infrastructure.GamePackages;
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
string dataRoot = builder.Configuration["CloudEmuera:DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data");
SqliteDatabaseOptions databaseOptions = new() { DataRoot = dataRoot };
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddScoped<CloudEmueraDbContext>(serviceProvider =>
{
    SqliteConnectionFactory factory = new(databaseOptions, createDataRoot: true);
    var options = new DbContextOptionsBuilder<CloudEmueraDbContext>()
        .UseSqlite(factory.OpenConnection(SqliteConnectionAccess.ReadWriteCreate), sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
        .Options;
    return new CloudEmueraDbContext(options);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new GamePackageStorageOptions { DataRoot = dataRoot });
builder.Services.AddScoped<IGamePackageIngestionService, GamePackageIngestionService>();
builder.Services.AddScoped<IGameLibraryService, GameLibraryService>();
string validatorAssembly = builder.Configuration["CloudEmuera:ValidatorAssembly"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "src", "CloudEmuera.Validator", "bin",
        builder.Environment.IsDevelopment() ? "Debug" : "Release", "net10.0", "CloudEmuera.Validator.dll");
builder.Services.AddSingleton(new GameValidatorProcessOptions
{
    ExecutablePath = "dotnet",
    AssemblyPath = validatorAssembly,
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
builder.Services.AddScoped<RealtimeOriginValidator>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(DataProtectionKeyRing.Prepare(dataRoot)).SetApplicationName("CloudEmuera");
bool development = builder.Environment.IsDevelopment();
string cookieName = development ? "CloudEmuera.Dev.Session" : "__Host-CloudEmuera.Session";
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = cookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
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
        await context.HttpContext.Response.WriteAsJsonAsync(new ApiError("TOO_MANY_ATTEMPTS", "请求过于频繁。", $"req_{Guid.CreateVersion7():N}"), cancellationToken).ConfigureAwait(false);
    };
    options.AddPolicy("game-read", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("game-write", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("game-search", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("game-validate", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHealthChecks().AddCheck<BootstrapHealthCheck>("identity_bootstrap", tags: ["ready"]);

var app = builder.Build();
if (!development)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
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
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/v1/version", () => Results.Ok(new BuildInfo("CloudEmuera", typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0-dev", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, 1, 1, 1)));
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = async (context, report) =>
{
    context.Response.ContentType = "application/json";
    string reason = report.Entries.Values.FirstOrDefault().Description ?? "READY";
    await context.Response.WriteAsJsonAsync(new { status = report.Status == HealthStatus.Healthy ? "READY" : "NOT_READY", reason });
}});

app.MapGet("/api/v1/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new CsrfResponse(tokens.RequestToken!));
});

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
    try { CurrentUser user = await identities.CreateUserAsync(new CreateUserCommand(request.Username, request.Email, request.TemporaryPassword, request.Role, request.QuotaProfileId), ApiIdentity.Actor(context)!, context.RequestAborted).ConfigureAwait(false); return Results.Created($"/api/v1/admin/users/{user.Id}", ApiIdentity.ToResponse(user)); }
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

app.MapPost("/api/v1/game-package-ingestions", async (HttpContext context, IAntiforgery antiforgery, IGamePackageIngestionService ingestion, ApiIdempotencyStore idempotency) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error("IDEMPOTENCY_KEY_REQUIRED", "需要 Idempotency-Key。", 428);
    try
    {
        IdempotencyExecution<IngestedGamePackage> execution = await idempotency.ExecuteAsync(actor, "game-package-ingestion", key,
            new { context.Request.ContentLength, context.Request.ContentType },
            () => ingestion.IngestAsync(new GamePackageIngestionRequest(actor.UserId, context.Request.Body, key), cancellationToken: context.RequestAborted),
            statusCode: StatusCodes.Status201Created, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        IngestedGamePackage result = execution.Value;
        return Results.Created($"/api/v1/game-package-ingestions/{result.IngestionId}", result);
    }
    catch (GameLibraryException exception) { return ApiIdentity.Error(exception.Code, exception.Message, exception.Code == GameLibraryErrorCodes.Conflict ? 409 : 400); }
    catch (GamePackageIngestionException exception) { return ApiIdentity.Error(exception.Code, "游戏包不安全或不受支持。", 400); }
}).RequireAuthorization().RequireRateLimiting("game-write");

var games = app.MapGroup("/api/v1/games").RequireAuthorization();
games.MapGet("", async (HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return Results.Ok(new { items = await library.ListAsync(actor, context.RequestAborted).ConfigureAwait(false) });
}).RequireRateLimiting("game-read");
games.MapPost("", async (HttpContext context, CreateGameRequest request, IAntiforgery antiforgery, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(() => library.CreateAsync(actor, request.Name, request.Visibility, context.RequestAborted), item => Results.Created($"/api/v1/games/{item.Id}", item)).ConfigureAwait(false);
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
games.MapPut("/{id}/package", async (string id, HttpContext context, BindGamePackageRequest request, IAntiforgery antiforgery, IGameLibraryService library, ApiIdempotencyStore idempotency) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error("IDEMPOTENCY_KEY_REQUIRED", "需要 Idempotency-Key。", 428);
    try
    {
        return await ApiIdentity.GameResultAsync(async () =>
        {
            IdempotencyExecution<GameLibraryItem> execution = await idempotency.ExecuteAsync(actor, $"game/{id}/package", key,
                new { id, request.IngestionId, request.ContentDigest, version },
                () => library.BindPackageAsync(actor, id, request.IngestionId, request.ContentDigest, version, context.RequestAborted), cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return execution.Value;
        }, Results.Ok).ConfigureAwait(false);
    }
    catch (GamePackageIngestionException exception)
    {
        return ApiIdentity.Error(exception.Code, "游戏包不安全或不受支持。", 400);
    }
}).RequireRateLimiting("game-write");
games.MapPost("/{id}:edit", async (string id, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(() => library.StartEditingAsync(actor, id, version, context.RequestAborted), Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-write");
games.MapDelete("/{id}/workspace", async (string id, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(() => library.DiscardWorkspaceAsync(actor, id, version, context.RequestAborted), Results.Ok).ConfigureAwait(false);
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
        context.Response.Headers.ETag = ApiIdentity.QuoteETag(file.ETag);
        return file;
    }, file => Results.Ok(file)).ConfigureAwait(false);
}).RequireRateLimiting("game-read");
games.MapGet("/{id}/search", async (string id, string? scope, string q, string? cursor, int? limit, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return await ApiIdentity.GameResultAsync(() => library.SearchAsync(actor, id, scope, q, cursor, limit ?? 100, context.RequestAborted), page => Results.Ok(new { items = page.Items, nextCursor = page.NextCursor })).ConfigureAwait(false);
}).RequireRateLimiting("game-search");
games.MapGet("/{id}/download", async (string id, string? scope, string path, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    return await ApiIdentity.GameResultAsync(() => library.OpenDownloadAsync(actor, id, scope, path, context.RequestAborted),
        download => ApiIdentity.SetDownloadHeaders(context, download)).ConfigureAwait(false);
}).RequireRateLimiting("game-read");
games.MapGet("/{id}/operations/{operationId}", async (string id, string operationId, HttpContext context, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    GameContentOperationItem? operation = await library.GetOperationAsync(actor, id, operationId, context.RequestAborted).ConfigureAwait(false);
    return operation is null ? ApiIdentity.Error("GAME_NOT_FOUND", "资源不存在。", 404) : Results.Ok(operation);
}).RequireRateLimiting("game-read");
games.MapPut("/{id}/file", async (string id, string path, WriteGameFileRequest request, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要游戏 If-Match 或 X-Game-State-Version。", 428);
    if (!ApiIdentity.TryFilePrecondition(context.Request, out string? fileETag, out bool requireAbsent)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要文件 If-Match 或 If-None-Match: *。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(() => library.WriteTextFileAsync(actor, id, path, request.Content, version, fileETag, requireAbsent, context.RequestAborted), Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-write");
games.MapDelete("/{id}/file", async (string id, string path, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    return await ApiIdentity.GameResultAsync(() => library.DeletePathAsync(actor, id, path, version, context.RequestAborted), Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-write");
games.MapPost("/{id}:validate", async (string id, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library, ApiIdempotencyStore idempotency) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error("IDEMPOTENCY_KEY_REQUIRED", "需要 Idempotency-Key。", 428);
    return await ApiIdentity.GameResultAsync(async () =>
    {
        IdempotencyExecution<GameValidationResult> execution = await idempotency.ExecuteAsync(actor, $"game/{id}:validate", key,
            new { id, version }, () => library.ValidateAsync(actor, id, version, context.RequestAborted), cancellationToken: context.RequestAborted).ConfigureAwait(false);
        return execution.Value;
    }, Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-validate");
games.MapPost("/{id}:activate", async (string id, HttpContext context, IAntiforgery antiforgery, IGameLibraryService library, ApiIdempotencyStore idempotency) =>
{
    if (ApiIdentity.GameActor(context) is not CurrentActor actor) return ApiIdentity.GameActorError(context);
    if (!ApiIdentity.TryVersion(context.Request, out int version)) return ApiIdentity.Error("PRECONDITION_REQUIRED", "需要 If-Match。", 428);
    if (!await ApiIdentity.ValidateCsrfAsync(context, antiforgery).ConfigureAwait(false)) return ApiIdentity.Error("CSRF_VALIDATION_FAILED", "请求验证失败。", 400);
    if (!ApiIdentity.TryIdempotencyKey(context.Request, out string key)) return ApiIdentity.Error("IDEMPOTENCY_KEY_REQUIRED", "需要 Idempotency-Key。", 428);
    return await ApiIdentity.GameResultAsync(async () =>
    {
        IdempotencyExecution<GameLibraryItem> execution = await idempotency.ExecuteAsync(actor, $"game/{id}:activate", key,
            new { id, version }, () => library.ActivateAsync(actor, id, version, context.RequestAborted), cancellationToken: context.RequestAborted).ConfigureAwait(false);
        return execution.Value;
    }, Results.Ok).ConfigureAwait(false);
}).RequireRateLimiting("game-validate");

app.MapFallback("/api/{**path}", () => ApiIdentity.Error("NOT_FOUND", "资源不存在。", StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;

internal static class ApiIdentity
{
    public static IResult Error(string code, string message, int status) => Results.Json(new ApiError(code, message, $"req_{Guid.CreateVersion7():N}"), statusCode: status);
    public static Task WriteErrorAsync(HttpContext context, string code, string message, int status)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new ApiError(code, message, $"req_{Guid.CreateVersion7():N}"));
    }
    public static CurrentUserResponse ToResponse(CurrentUser value) => new(value.Id, value.Username, value.Email, value.Role, value.Status, value.MustChangePassword, value.StateVersion);
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

    public static async Task<IResult> GameResultAsync<T>(Func<Task<T>> action, Func<T, IResult> success)
    {
        try { return success(await action().ConfigureAwait(false)); }
        catch (GameLibraryException exception)
        {
            int status = exception.Code switch
            {
                GameLibraryErrorCodes.NotFound or GameLibraryErrorCodes.FileNotFound => 404,
                GameLibraryErrorCodes.StateVersionConflict or GameLibraryErrorCodes.FileChanged => 412,
                GameLibraryErrorCodes.Conflict or GameLibraryErrorCodes.NameConflict or GameLibraryErrorCodes.InUse
                    or GameLibraryErrorCodes.HasNoCurrentContent or GameLibraryErrorCodes.WorkspaceNotFound
                    or GameLibraryErrorCodes.WorkspaceAlreadyExists or GameLibraryErrorCodes.ValidationInProgress
                    or GameLibraryErrorCodes.ActivationInProgress => 409,
                GameLibraryErrorCodes.ValidationFailed or GameLibraryErrorCodes.ActivationValidationFailed
                    or GameLibraryErrorCodes.FileTypeNotEditable or GameLibraryErrorCodes.FileTooLargeToEdit
                    or GameLibraryErrorCodes.TextEncodingUnsupported or GameLibraryErrorCodes.TextNotRepresentable => 422,
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
        key = request.Headers["Idempotency-Key"].ToString().Trim();
        return key.Length is > 0 and <= 256 && !key.Any(char.IsControl);
    }

    public static string QuoteETag(string etag) => etag.StartsWith('"') ? etag : $"\"{etag}\"";

    public static IResult SetDownloadHeaders(HttpContext context, GameFileDownload download)
    {
        context.Response.Headers.ETag = QuoteETag(download.ETag);
        return Results.Stream(download.Content, "application/octet-stream", download.FileName, enableRangeProcessing: false);
    }
}
