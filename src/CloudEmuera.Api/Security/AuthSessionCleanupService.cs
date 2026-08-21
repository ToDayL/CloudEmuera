using CloudEmuera.Application.Identity;

namespace CloudEmuera.Api.Security;

public sealed partial class AuthSessionCleanupService(IServiceScopeFactory scopes, ILogger<AuthSessionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupUntilBoundedAsync(stoppingToken).ConfigureAwait(false);
        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await CleanupUntilBoundedAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task CleanupUntilBoundedAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Cap every maintenance pass as well as every SQL statement so a
            // large stale table cannot monopolize SQLite's single writer.
            const int batchSize = 200;
            const int maximumBatches = 5;
            for (int batch = 0; batch < maximumBatches; batch++)
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                int removed = await scope.ServiceProvider.GetRequiredService<IAuthSessionMaintenance>()
                    .CleanupAsync(batchSize, cancellationToken).ConfigureAwait(false);
                if (removed < batchSize) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { LogCleanupFailure(logger); }
    }

    [LoggerMessage(LogLevel.Warning, "Bounded authentication session cleanup failed; the next pass will retry.")]
    private static partial void LogCleanupFailure(ILogger logger);
}
