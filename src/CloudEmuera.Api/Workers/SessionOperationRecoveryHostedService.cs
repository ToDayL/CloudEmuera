using CloudEmuera.Application.Sessions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudEmuera.Api.Workers;

public sealed class SessionOperationRecoveryReadiness
{
    private int ready;
    private string reason = "session_operation_recovery_pending";

    public bool IsReady => Volatile.Read(ref ready) != 0;
    public string Reason => Volatile.Read(ref reason) ?? "session_operation_recovery_pending";

    public void MarkReady()
    {
        Volatile.Write(ref reason, "ready");
        Volatile.Write(ref ready, 1);
    }

    public void MarkFailed(string failureReason)
    {
        Volatile.Write(ref ready, 0);
        Volatile.Write(ref reason, string.IsNullOrWhiteSpace(failureReason) ? "session_operation_recovery_failed" : failureReason);
    }
}

public sealed class SessionOperationRecoveryHealthCheck(SessionOperationRecoveryReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy("READY")
            : HealthCheckResult.Unhealthy(readiness.Reason));
}

/// <summary>
/// A Session command may touch both the durable reconciliation state and the
/// Worker control plane.  Keep the HTTP write boundary closed until both
/// startup barriers have completed; the health endpoint alone is not enough
/// because a client can otherwise race the first recovery pass.
/// </summary>
public sealed class SessionCommandReadiness(
    WorkerRuntimeReadiness worker,
    SessionOperationRecoveryReadiness recovery)
{
    public bool IsReady => worker.IsReady && recovery.IsReady;

    public string Reason => !worker.IsReady
        ? worker.Reason
        : recovery.Reason;
}

/// <summary>
/// Runs the first durable Session reconciliation before readiness can become
/// healthy, then repeats it as a bounded reaper.  This is deliberately an
/// IHostedService rather than a fire-and-forget BackgroundService so the host
/// cannot expose a ready API while create/lifecycle operations are unresolved.
/// </summary>
public sealed partial class SessionOperationRecoveryHostedService(
    ISessionOperationRecovery recovery,
    SessionOperationRecoveryReadiness readiness,
    ILogger<SessionOperationRecoveryHostedService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private int disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
            readiness.MarkReady();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            readiness.MarkFailed("session_operation_recovery_cancelled");
            throw;
        }
        catch (Exception exception)
        {
            readiness.MarkFailed("session_operation_recovery_failed");
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

    private async Task RunPeriodicRecoveryAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await recovery.RecoverAsync(stoppingToken).ConfigureAwait(false);
                readiness.MarkReady();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                readiness.MarkFailed("session_operation_recovery_failed");
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

    [LoggerMessage(EventId = 2604, Level = LogLevel.Warning, Message = "session_operation_recovery_failed; retrying on the next pass")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception);
}
