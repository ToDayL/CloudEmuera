using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Workers;
using CloudEmuera.Application.Administration;

namespace CloudEmuera.Api.Administration;

/// <summary>
/// Bridges transient API-owned runtime facts into the application diagnostic
/// port. It snapshots the Worker collection once and never exposes its
/// process diagnostics, SessionRoot or control socket values.
/// </summary>
public sealed class ApiAdminRuntimeDiagnostics(
    WorkerManager manager,
    WorkerRuntimeReadiness readiness,
    RealtimeConnectionRegistry connections) : IAdminRuntimeDiagnostics
{
    public async Task<AdminRuntimeHostSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApiWorkerSession[] workers = manager.Workers.ToArray();
        var facts = new List<AdminHostWorker>(workers.Length);
        foreach (ApiWorkerSession worker in workers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RealtimeHubDiagnostics hub = await worker.OutputHub.ReadDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            bool registered = worker.IsRegistered;
            facts.Add(new AdminHostWorker(
                worker.Binding.SessionId,
                worker.Binding.WorkerId,
                checked((long)worker.Binding.WorkerEpoch),
                worker.ProcessId > 0 ? worker.ProcessId : null,
                registered,
                worker.ReadyConfirmed,
                worker.HasExited,
                registered ? worker.LastHeartbeatAt : null,
                worker.LastOutputSequence,
                worker.DroppedPendingEventCount,
                new AdminRealtimeHostSnapshot(
                    ToHubState(hub.State),
                    hub.SnapshotSequence,
                    hub.SnapshotBytes,
                    hub.SnapshotSizeStatus,
                    hub.SubscriptionCount,
                    hub.ResyncCount,
                    hub.SoftOverflowCount,
                    hub.HardOverflowCount,
                    hub.FaultCount,
                    worker.DroppedPendingEventCount)));
        }

        RealtimeRegistryDiagnostics registry = connections.ReadDiagnostics();
        string controlPlaneState = manager.IsDraining
            ? "DRAINING"
            : readiness.IsReady ? "READY" : "NOT_READY";
        return new AdminRuntimeHostSnapshot(
            manager.ControlPlaneInstanceId,
            controlPlaneState,
            facts,
            registry.ConnectionCount,
            registry.SubscriptionCount,
            readiness.WriteFenceUnconfirmedSessionIds);
    }

    private static string ToHubState(SessionOutputHubState state) => state switch
    {
        SessionOutputHubState.AwaitingInitialSnapshot => "AWAITING_INITIAL_SNAPSHOT",
        SessionOutputHubState.Live => "LIVE",
        SessionOutputHubState.Faulted => "FAULTED",
        SessionOutputHubState.Disposed => "DISPOSED",
        _ => "UNKNOWN",
    };
}
