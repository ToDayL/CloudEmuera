using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Application.Games;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Api.Bootstrap;

internal sealed partial class GameContentOperationReaperService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<GameContentOperationReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapAsync(stoppingToken).ConfigureAwait(false);
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                LogReaperFailure(logger);
                await Task.Delay(TimeSpan.FromSeconds(10), timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReapAsync(CancellationToken token)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        DateTimeOffset now = timeProvider.GetUtcNow();
        await db.GameContentCopyLeases.Where(lease => lease.ExpiresAt < now).ExecuteDeleteAsync(token).ConfigureAwait(false);
        int count = await scope.ServiceProvider.GetRequiredService<IGameContentOperationMaintenance>().ReconcileAsync(cancellationToken: token).ConfigureAwait(false);
        if (count != 0) LogReaped(logger, count);

        var incompleteIngestions = await db.GameContentOperations.AsNoTracking()
            .Where(operation => operation.Status == GameContentOperationStatus.Committed && operation.IngestionId != null)
            .Join(db.Games.AsNoTracking(), operation => operation.GameId, game => game.Id,
                (operation, game) => new { IngestionId = operation.IngestionId!, game.OwnerUserId })
            .Join(db.GamePackageIngestions.AsNoTracking().Where(ingestion => ingestion.Status == GamePackageIngestionStatus.Consuming),
                value => value.IngestionId, ingestion => ingestion.Id, (value, _) => value)
            .Take(16)
            .ToArrayAsync(token).ConfigureAwait(false);
        IGamePackageIngestionService ingestionService = scope.ServiceProvider.GetRequiredService<IGamePackageIngestionService>();
        foreach (var item in incompleteIngestions)
        {
            try { await ingestionService.CompleteConsumeAsync(item.IngestionId, item.OwnerUserId, token).ConfigureAwait(false); }
            catch (GamePackageIngestionException exception) { LogIngestionReconcileFailure(logger, exception.Code); }
        }
    }

    [LoggerMessage(EventId = 1311, Level = LogLevel.Error, Message = "Game content operation reaper failed.")]
    private static partial void LogReaperFailure(ILogger logger);

    [LoggerMessage(EventId = 1312, Level = LogLevel.Warning, Message = "Reaped {Count} expired game content operations.")]
    private static partial void LogReaped(ILogger logger, int count);

    [LoggerMessage(EventId = 1313, Level = LogLevel.Warning, Message = "Game ingestion reconciliation failed with {Code}.")]
    private static partial void LogIngestionReconcileFailure(ILogger logger, string code);
}
