using CloudEmuera.Application.GamePackages;
using CloudEmuera.Infrastructure.GamePackages;

namespace CloudEmuera.Api.Bootstrap;

public sealed partial class GamePackageIngestionReaperService(
    IServiceScopeFactory scopeFactory,
    GamePackageStorageOptions options,
    ILogger<GamePackageIngestionReaperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReapAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(options.ReaperInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await ReapAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task ReapAsync(CancellationToken token)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            int count = await scope.ServiceProvider.GetRequiredService<IGamePackageIngestionMaintenance>()
                .ReapExpiredAsync(cancellationToken: token).ConfigureAwait(false);
            if (count > 0) LogReaped(logger, count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            LogFailure(logger);
        }
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "Reaped {Count} expired game package ingestions.")]
    private static partial void LogReaped(ILogger logger, int count);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Error, Message = "Game package ingestion reaper failed.")]
    private static partial void LogFailure(ILogger logger);
}
