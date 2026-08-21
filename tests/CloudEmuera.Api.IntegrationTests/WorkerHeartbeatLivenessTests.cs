using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Workers;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V5;
using CloudEmuera.RuntimeAdapter;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class WorkerHeartbeatLivenessTests
{
    // SESS-010: a heartbeat already accepted by the API must not race the
    // watchdog merely because durable lease renewal is waiting on SQLite.
    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public async Task HeartbeatPersistenceAtLeaseBoundaryDoesNotClaimTimeout()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await using ApiWorkerSession session = CreateSession();

        Assert.True(session.TryBeginHeartbeatProcessing(startedAt));
        Assert.False(session.TryClaimHeartbeatTimeout(
            startedAt.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)));

        session.CompleteHeartbeatProcessing(startedAt.AddSeconds(5));

        Assert.False(session.TryClaimHeartbeatTimeout(
            startedAt.AddSeconds(9),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)));
        Assert.True(session.TryClaimHeartbeatTimeout(
            startedAt.AddSeconds(10),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public async Task StuckHeartbeatPersistenceStillHasBoundedTimeout()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await using ApiWorkerSession session = CreateSession();

        Assert.True(session.TryBeginHeartbeatProcessing(startedAt));
        Assert.False(session.TryClaimHeartbeatTimeout(
            startedAt.AddSeconds(9),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)));
        Assert.True(session.TryClaimHeartbeatTimeout(
            startedAt.AddSeconds(10),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)));
        Assert.False(session.TryBeginHeartbeatProcessing(startedAt.AddSeconds(10)));
    }

    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public async Task InvalidHeartbeatPayloadIsRejectedWithoutTearingDownTheWorker()
    {
        // Regression (eraTW epoch 10): the Worker could emit a
        // self-contradictory heartbeat (WaitingForInput=true with an empty
        // CurrentPromptId) when the prompt transitioned between reads.
        // RecordHeartbeatAsync rejects that payload with ArgumentException;
        // the API must reject the sample and keep the control stream alive
        // instead of misclassifying it as database_unavailable and fencing the
        // Session as CRASHED.
        string dataRoot = Path.Combine(Path.GetTempPath(), $"cloudemuera-heartbeat-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        var options = new WorkerManagerOptions(dataRoot, Path.Combine(dataRoot, "CloudEmuera.Worker.dll"));
        var store = new SqliteSessionRuntimeStore(
            new SqliteDatabaseOptions { DataRoot = dataRoot },
            TimeProvider.System);
        await using var manager = new WorkerManager(options, NullLoggerFactory.Instance, store, TimeProvider.System);
        await using ApiWorkerSession session = CreateSession(options);
        session.UpdateRuntimeBinding(
            new SessionRuntimeBinding(
                SessionId: session.Binding.SessionId,
                WorkerId: session.Binding.WorkerId,
                WorkerEpoch: (long)session.Binding.WorkerEpoch,
                StateVersion: 1,
                ControlPlaneInstanceId: options.ControlPlaneInstanceId,
                SessionRootPath: dataRoot,
                CompatibilityProfile: "v18-compatible",
                SaveLayout: (int)RuntimeSaveLayout.Root,
                SessionRootManifestDigest: "manifest-digest",
                RuntimeVersion: "headless-p0.5.1",
                InitialOutputSequence: 0),
            persistenceReady: true);
        using var connection = new ApiWorkerConnection(session);
        session.AttachConnection(connection);

        WorkerEnvelope invalidHeartbeat = new()
        {
            ProtocolVersion = StructuredIpcProtocol.CurrentVersion,
            MessageId = "hb-invalid-1",
            SessionId = session.Binding.SessionId,
            WorkerId = session.Binding.WorkerId,
            WorkerEpoch = session.Binding.WorkerEpoch,
            ControlPlaneInstanceId = options.ControlPlaneInstanceId,
            CapabilitySetDigest = StructuredIpcProtocol.CapabilitySetDigest,
            Heartbeat = new WorkerHeartbeat
            {
                MonotonicTimestampTicks = 1,
                OutputSequence = 10,
                WaitingForInput = true,
                CurrentPromptId = string.Empty,
                ResidentMemoryBytes = 1024,
            },
        };

        try
        {
            await manager.ReceiveAsync(connection, invalidHeartbeat);

            Assert.False(
                connection.CancellationToken.IsCancellationRequested,
                "An invalid heartbeat payload must not tear down the Worker control stream.");
            Assert.False(
                session.OutputHub.State is SessionOutputHubState.Faulted or SessionOutputHubState.Disposed,
                "An invalid heartbeat payload must not fault the output hub.");
        }
        finally
        {
            // Dispose the session before the connection so the session's own
            // teardown does not Cancel() an already-disposed token source.
            session.DetachConnection(connection);
        }
    }

    private static ApiWorkerSession CreateSession(WorkerManagerOptions? options = null)
    {
        options ??= new WorkerManagerOptions(
            Path.Combine(Path.GetTempPath(), $"cloudemuera-heartbeat-{Guid.NewGuid():N}"),
            Path.Combine(Path.GetTempPath(), "CloudEmuera.Worker.dll"));
        var session = new ApiWorkerSession(
            new WorkerLaunchRequest(
                new WorkerBinding("sess_heartbeat", "wrk_heartbeat", 1),
                options.DataRoot,
                "v18-compatible",
                RuntimeSaveLayout.Root),
            options,
            NullLogger<ApiWorkerSession>.Instance);
        session.SetBootstrapPath(Path.Combine(options.BootstrapDirectory, "unused-bootstrap.json"));
        return session;
    }
}
