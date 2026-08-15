using CloudEmuera.Application.Saves;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudEmuera.Api.Workers;

public sealed class SaveOperationRecoveryReadiness
{
    private int ready;
    private string reason = "save_operation_recovery_pending";

    public bool IsReady => Volatile.Read(ref ready) != 0;

    public string Reason => Volatile.Read(ref reason) ?? "save_operation_recovery_pending";

    public void MarkReady()
    {
        Volatile.Write(ref reason, "ready");
        Volatile.Write(ref ready, 1);
    }

    public void MarkFailed(string failureReason)
    {
        Volatile.Write(ref ready, 0);
        Volatile.Write(ref reason, string.IsNullOrWhiteSpace(failureReason) ? "save_operation_recovery_failed" : failureReason);
    }
}

public sealed class SaveOperationRecoveryHealthCheck(SaveOperationRecoveryReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy("READY")
            : HealthCheckResult.Unhealthy(readiness.Reason));
}

/// <summary>
/// Runs the first save-operation reconciliation before save mutations are
/// admitted, then retries failed reconciliation passes periodically.
/// </summary>
public sealed partial class SaveFileOperationRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    SaveOperationRecoveryReadiness readiness,
    ILogger<SaveFileOperationRecoveryHostedService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private int disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RecoverOnceAsync(cancellationToken).ConfigureAwait(false);
            readiness.MarkReady();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            readiness.MarkFailed("save_operation_recovery_cancelled");
            throw;
        }
        catch (Exception exception)
        {
            readiness.MarkFailed("save_operation_recovery_failed");
            LogRecoveryFailed(logger, exception);
        }

        loop = RunPeriodicRecoveryAsync(stop.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (loop is null)
            return;
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
        stop.Dispose();
    }

    private async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISaveFileOperationRecovery>().RecoverAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPeriodicRecoveryAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
                readiness.MarkReady();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                readiness.MarkFailed("save_operation_recovery_failed");
                LogRecoveryFailed(logger, exception);
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 2605, Level = LogLevel.Warning, Message = "save_file_operation_recovery_failed; retrying on the next pass")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);
}
