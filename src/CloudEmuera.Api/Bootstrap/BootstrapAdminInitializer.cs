using CloudEmuera.Application.Auditing;
using CloudEmuera.Infrastructure.Identity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Capacity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Api.Bootstrap;

public sealed class BootstrapReadiness
{
    public bool IsReady { get; private set; }
    public string Reason { get; private set; } = "STARTING";
    public void Ready() { IsReady = true; Reason = "READY"; }
    public void Fail(string reason) { IsReady = false; Reason = reason; }
}

public sealed partial class BootstrapAdminInitializer(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    BootstrapReadiness readiness,
    ILogger<BootstrapAdminInitializer> logger,
    InstanceCapacityOptions? capacityOptions = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int attempts = 4;
        try
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    BootstrapAttemptResult result = await TryBootstrapAsync(
                        configuration["CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME"],
                        configuration["CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL"],
                        configuration["CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD"],
                        cancellationToken).ConfigureAwait(false);
                    if (result == BootstrapAttemptResult.Ready) { readiness.Ready(); return; }
                    if (result == BootstrapAttemptResult.ConfigurationInvalid)
                    {
                        await RecordFailureAsync("BOOTSTRAP_CONFIGURATION_INVALID", cancellationToken).ConfigureAwait(false);
                        readiness.Fail("BOOTSTRAP_CONFIGURATION_INVALID");
                        return;
                    }
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 or 19 && attempt + 1 < attempts)
                {
                    // A competing API may own the writer lock or have just won the
                    // unique/CAS race.  A new scope must re-read COMPLETED.
                    await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
            }
            readiness.Fail("BOOTSTRAP_INITIALIZATION_FAILED");
        }
        catch (IdentityValidationException)
        {
            await RecordFailureAsync("BOOTSTRAP_CONFIGURATION_INVALID", cancellationToken).ConfigureAwait(false);
            readiness.Fail("BOOTSTRAP_CONFIGURATION_INVALID");
        }
        catch (Exception exception)
        {
            LogBootstrapFailure(logger, exception);
            await RecordFailureAsync("BOOTSTRAP_INITIALIZATION_FAILED", cancellationToken).ConfigureAwait(false);
            readiness.Fail("BOOTSTRAP_INITIALIZATION_FAILED");
        }
    }

    private async Task<BootstrapAttemptResult> TryBootstrapAsync(string? username, string? email, string? password, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        InstanceStateRow? state = await db.InstanceStates.SingleOrDefaultAsync(row => row.Id == 1, cancellationToken).ConfigureAwait(false);
        if (state is null) return BootstrapAttemptResult.SchemaInvalid;
        if (state.BootstrapStatus == InstanceStateRow.Completed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BootstrapAttemptResult.Ready;
        }
        // Bootstrap configuration is deliberately inspected only after the
        // persisted one-way state says it is still required. A completed
        // instance must remain ready when operators remove or rotate .env.
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return BootstrapAttemptResult.ConfigurationInvalid;
        string normalizedUsername = IdentityValidation.NormalizeUsername(username);
        string normalizedEmail = IdentityValidation.NormalizeEmail(email);
        IdentityValidation.ValidatePassword(password);
        if (await db.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail || user.NormalizedLoginName == normalizedUsername, cancellationToken).ConfigureAwait(false))
            return BootstrapAttemptResult.ConfigurationInvalid;

        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        InstanceCapacityOptions capacity = capacityOptions ?? InstanceCapacityOptions.Default;
        capacity.Validate();
        // These columns are retained only to satisfy the legacy user FK and
        // old database readers. Runtime admission uses InstanceCapacityOptions;
        // the output column has no runtime consumer and keeps a fixed legacy value.
        QuotaProfileRow quota = new() { Id = $"qtp_{Guid.CreateVersion7():N}", Name = "Default", MaxActiveSessions = capacity.MaxActiveWorkers, MaxGamePackageBytes = capacity.MaxGamePackageBytes, MaxSessionBytes = capacity.MaxSessionRootBytes, MaxOutputBytesPerSecond = 1_048_576, CreatedAt = now, UpdatedAt = now, StateVersion = 0 };
        db.QuotaProfiles.Add(quota);
        LocalIdentityService identities = scope.ServiceProvider.GetRequiredService<LocalIdentityService>();
        CloudEmueraUser admin = identities.NewUser(username, normalizedUsername, email.Trim(), normalizedEmail, quota.Id, UserRole.Admin, password, now);
        db.Users.Add(admin);
        identities.AddAudit(AuditActions.SystemAdminBootstrapped, "INSTANCE", "1", "SUCCEEDED", "SYSTEM", admin.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        int transitioned = await db.InstanceStates.Where(row => row.Id == 1 && row.BootstrapStatus == InstanceStateRow.Required && row.StateVersion == state.StateVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.BootstrapStatus, InstanceStateRow.Completed)
                .SetProperty(row => row.InitializedAt, now)
                .SetProperty(row => row.InitialAdminUserId, admin.Id)
                .SetProperty(row => row.StateVersion, row => row.StateVersion + 1), cancellationToken).ConfigureAwait(false);
        if (transitioned != 1) throw new SqliteException("bootstrap state transition lost", 19);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return BootstrapAttemptResult.Ready;
    }

    private async Task RecordFailureAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
            await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
            if (await db.InstanceStates.AnyAsync(row => row.Id == 1 && row.BootstrapStatus == InstanceStateRow.Required, cancellationToken).ConfigureAwait(false))
            {
                LocalIdentityService identities = scope.ServiceProvider.GetRequiredService<LocalIdentityService>();
                identities.AddAudit(AuditActions.SystemAdminBootstrapFailed, "INSTANCE", "1", "FAILED", "SYSTEM", reason: reason);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) { /* readiness remains fail-closed when the audit store itself is unavailable. */ }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(LogLevel.Error, "Bootstrap initialization failed without recording configuration values.")]
    private static partial void LogBootstrapFailure(ILogger logger, Exception exception);
}

internal enum BootstrapAttemptResult { Ready, ConfigurationInvalid, SchemaInvalid }

public sealed class BootstrapHealthCheck(BootstrapReadiness readiness) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.IsReady ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy() : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(readiness.Reason));
}
