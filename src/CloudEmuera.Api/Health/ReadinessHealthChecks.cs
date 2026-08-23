using System.Data;
using CloudEmuera.Application.Fonts;
using CloudEmuera.Api.Bootstrap;
using CloudEmuera.Api.Workers;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Infrastructure.Fonts;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudEmuera.Api.Health;

public static class ReadinessHealthCheckNames
{
    public static readonly string[] Ordered =
    [
        "identity_bootstrap",
        "runtime_fonts",
        "database",
        "schema",
        "data_root",
        "data_root_space",
        "worker_runtime",
        "session_operation_recovery",
        "save_operation_recovery",
    ];
}

public sealed class RuntimeFontReadinessHealthCheck(FileRuntimeFontCatalog catalog) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            catalog.VerifyAllAssets();
            return Task.FromResult(HealthCheckResult.Healthy("READY"));
        }
        catch (RuntimeFontCatalogException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("RUNTIME_FONTS_UNAVAILABLE"));
        }
        catch (IOException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("RUNTIME_FONTS_UNAVAILABLE"));
        }
    }
}

public sealed partial class DatabaseReadinessHealthCheck(
    SqliteDatabaseOptions options,
    ILogger<DatabaseReadinessHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string lastStage = "open";
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                lastStage = "open";
                try
                {
                    await using SqliteConnection connection = new SqliteConnectionFactory(options, createDataRoot: false)
                        .OpenConnection(SqliteConnectionAccess.ReadWrite);
                    lastStage = "foreign_keys";
                    await using SqliteCommand foreignKeys = connection.CreateCommand();
                    foreignKeys.CommandText = "PRAGMA foreign_keys;";
                    object? value = await foreignKeys.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), "1", StringComparison.Ordinal))
                        return HealthCheckResult.Unhealthy("DATABASE_FOREIGN_KEYS_DISABLED");

                    lastStage = "begin";
                    await using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                    lastStage = "update";
                    await using SqliteCommand probe = connection.CreateCommand();
                    probe.Transaction = transaction;
                    probe.CommandText = "UPDATE instance_state SET state_version = state_version WHERE id = 1;";
                    await probe.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    lastStage = "rollback";
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return HealthCheckResult.Healthy("READY");
                }
                catch (SqliteException exception) when (IsTransient(exception) && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100L * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (exception is SqliteException sqlite)
                LogSqliteProbeFailed(logger, sqlite.SqliteErrorCode, lastStage);
            else
                LogOtherProbeFailed(logger, exception.GetType().Name, lastStage);
            return HealthCheckResult.Unhealthy("DATABASE_UNAVAILABLE");
        }
    }

    private static bool IsTransient(SqliteException exception) => exception.SqliteErrorCode is 5 or 6 or 8;

    [LoggerMessage(EventId = 2801, Level = LogLevel.Warning, Message = "database_probe_failed; sqliteErrorCode={SqliteErrorCode} stage={Stage}")]
    private static partial void LogSqliteProbeFailed(ILogger logger, int sqliteErrorCode, string stage);

    [LoggerMessage(EventId = 2802, Level = LogLevel.Warning, Message = "database_probe_failed; exceptionType={ExceptionType} stage={Stage}")]
    private static partial void LogOtherProbeFailed(ILogger logger, string exceptionType, string stage);
}

public sealed class SchemaReadinessHealthCheck(SqliteDatabaseOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = new SqliteConnectionFactory(options, createDataRoot: false)
                .OpenConnection(SqliteConnectionAccess.ReadOnly);
            await using CloudEmueraDbContext db = new(new DbContextOptionsBuilder<CloudEmueraDbContext>()
                .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
                .Options);
            string[] known = db.Database.GetMigrations().ToArray();
            string[] applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            if (applied.Length > known.Length || applied.Where((migration, index) => !string.Equals(migration, known[index], StringComparison.Ordinal)).Any())
                return HealthCheckResult.Unhealthy("SCHEMA_NEWER_THAN_BINARY");
            if (applied.Length != known.Length)
                return HealthCheckResult.Unhealthy("SCHEMA_MIGRATION_REQUIRED");
            return HealthCheckResult.Healthy("READY");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("SCHEMA_UNAVAILABLE");
        }
    }
}

public sealed class DataRootReadinessHealthCheck(SqliteDatabaseOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            SqliteDatabasePaths paths = options.ResolvePaths(createDataRoot: false);
            DirectoryInfo root = new(paths.DataRoot);
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return HealthCheckResult.Unhealthy("DATA_ROOT_UNAVAILABLE");

            string probeDirectory = Path.Combine(paths.DataRoot, ".cloudemuera-health-probe");
            DirectoryInfo probeInfo = Directory.CreateDirectory(probeDirectory);
            if (probeInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return HealthCheckResult.Unhealthy("DATA_ROOT_UNAVAILABLE");
            string probePath = Path.Combine(probeDirectory, $"probe-{Guid.CreateVersion7():N}.tmp");
            try
            {
                await using FileStream stream = new(probePath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                });
                await stream.WriteAsync(new byte[] { 0x43 }, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                TryDeleteProbe(probePath);
            }
            TryDeleteProbeDirectory(probeDirectory);
            return HealthCheckResult.Healthy("READY");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("DATA_ROOT_UNAVAILABLE");
        }
    }

    private static void TryDeleteProbe(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteProbeDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch { }
    }
}

public sealed class DataRootSpaceReadinessHealthCheck(
    SqliteDatabaseOptions options,
    InstanceCapacityOptions capacity) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            DriveInfo drive = new(Path.GetFullPath(options.DataRoot));
            return Task.FromResult(drive.AvailableFreeSpace >= capacity.MinDataRootFreeBytes
                ? HealthCheckResult.Healthy("READY")
                : HealthCheckResult.Unhealthy("DATA_ROOT_SPACE_LOW"));
        }
        catch (Exception)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("DATA_ROOT_SPACE_UNAVAILABLE"));
        }
    }
}

public sealed class ReadinessResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        var checks = new List<object>(ReadinessHealthCheckNames.Ordered.Length);
        string reason = "READY";
        foreach (string name in ReadinessHealthCheckNames.Ordered)
        {
            if (!report.Entries.TryGetValue(name, out HealthReportEntry entry))
                continue;
            string checkReason = entry.Status == HealthStatus.Healthy ? "READY" : entry.Description ?? "NOT_READY";
            checks.Add(new { name, status = entry.Status == HealthStatus.Healthy ? "READY" : "NOT_READY", reason = checkReason });
            if (reason == "READY" && entry.Status != HealthStatus.Healthy)
                reason = checkReason;
        }
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status == HealthStatus.Healthy ? "READY" : "NOT_READY",
            reason,
            checks,
        }).ConfigureAwait(false);
    }
}
