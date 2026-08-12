using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Api.Workers;

/// <summary>Translates API-owned worker host facts into the application port.</summary>
public sealed class ApiWorkerOpenOptionsFactory(
    WorkerManagerOptions options,
    TimeProvider timeProvider) : IWorkerOpenOptionsFactory
{
    public SessionRuntimeOpenOptions Create(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        string workerId = $"wrk_{Guid.CreateVersion7():N}";
        return new SessionRuntimeOpenOptions(
            sessionId,
            options.ControlPlaneInstanceId,
            workerId,
            RuntimeBaseline.CloudEmueraIntegrationVersion,
            checked((int)IpcProtocol.CurrentVersion),
            $"uds/{workerId}",
            options.LeaseDuration,
            timeProvider.GetUtcNow());
    }
}
