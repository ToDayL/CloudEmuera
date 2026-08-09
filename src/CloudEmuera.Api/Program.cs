using System.Security.Claims;
using CloudEmuera.Api.Bootstrap;
using CloudEmuera.Api.Security;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using CloudEmuera.Contracts;
using CloudEmuera.Contracts.Identity;
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
builder.Services.AddScoped<IGamePackageIngestionMaintenance, GamePackageIngestionMaintenance>();
builder.Services.AddHostedService<GamePackageIngestionReaperService>();
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
    public static bool TryVersion(HttpRequest request, out int version) => int.TryParse(request.Headers.IfMatch.ToString().Trim('"'), out version);
}
