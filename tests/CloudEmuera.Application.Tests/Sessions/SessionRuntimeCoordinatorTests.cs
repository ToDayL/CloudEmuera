using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Domain.Sessions;
using Xunit;

namespace CloudEmuera.Application.Tests.Sessions;

[Trait("Category", "SessionLifecycle")]
public sealed class SessionRuntimeCoordinatorTests
{
    [Fact]
    public async Task OpenOrdersLeaseInspectionLaunchIdentityReadyAndReturnsRunning()
    {
        var trace = new List<string>();
        RecordingStore store = new(trace);
        RecordingRootInspector inspector = new(trace);
        RecordingWorkerControl workerControl = new(trace);
        SessionRuntimeCoordinator coordinator = new(store, workerControl, inspector, TimeProvider.System);

        SessionRuntimeOpenResult result = await coordinator.OpenAsync(CreateOpenOptions());

        Assert.Equal(SessionState.Running, result.Lease.State);
        Assert.Equal(1, result.Lease.Binding.WorkerEpoch);
        Assert.Equal(
            ["acquire", "inspect", "start", "identity", "ready", "mark-ready", "dispose"],
            trace);
        Assert.NotNull(store.Ready);
        Assert.Equal(workerControl.Process.Identity, store.RecordedIdentity);
    }

    [Fact]
    public async Task OpenReadyFailureKillsWorkerAndCompletesCrashedLease()
    {
        var trace = new List<string>();
        RecordingStore store = new(trace);
        RecordingRootInspector inspector = new(trace);
        RecordingWorkerControl workerControl = new(trace)
        {
            ReadyException = new TimeoutException("ready timeout")
        };
        SessionRuntimeCoordinator coordinator = new(store, workerControl, inspector, TimeProvider.System);

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.OpenAsync(CreateOpenOptions()));

        Assert.Equal(
            ["acquire", "inspect", "start", "identity", "ready", "kill", "exit", "complete", "dispose"],
            trace);
        Assert.Equal(SessionRuntimeTerminalState.Crashed, store.CompletedTerminalState);
        Assert.Equal(SessionRuntimeResultCodes.WorkerStartFailed, store.CompletedReason);
    }

    [Fact]
    public async Task CloseOrdersStoppingRequestExitAndClosedCompletion()
    {
        var trace = new List<string>();
        RecordingStore store = new(trace);
        RecordingRootInspector inspector = new(trace);
        RecordingWorkerControl workerControl = new(trace);
        SessionRuntimeCoordinator coordinator = new(store, workerControl, inspector, TimeProvider.System);
        SessionRuntimeOpenResult opened = await coordinator.OpenAsync(CreateOpenOptions());
        trace.Clear();

        SessionRuntimeCloseResult result = await coordinator.CloseAsync(
            opened.Lease.Binding,
            workerControl.Process,
            new SessionRuntimeCloseRequest(opened.Lease.Binding.SessionId, Force: false, "requested"));

        Assert.True(result.Completion.Applied);
        Assert.Equal(SessionState.Closed, result.Completion.State);
        Assert.Equal(["stopping", "request-stop", "exit", "complete"], trace);
        Assert.Equal(SessionRuntimeTerminalState.Closed, store.CompletedTerminalState);
    }

    private static SessionRuntimeOpenOptions CreateOpenOptions() => new(
        "sess_coordinator",
        "ctl_coordinator",
        "wrk_coordinator",
        "headless-test",
        2,
        "uds/wrk_coordinator",
        TimeSpan.FromSeconds(30),
        DateTimeOffset.UtcNow);

    private sealed class RecordingStore(List<string> trace) : ISessionRuntimeStore
    {
        public WorkerProcessIdentity? RecordedIdentity { get; private set; }
        public WorkerReadyInfo? Ready { get; private set; }
        public SessionRuntimeTerminalState? CompletedTerminalState { get; private set; }
        public string? CompletedReason { get; private set; }

        public Task<SessionRuntimeAcquireResult> TryAcquireOpenLeaseAsync(
            SessionRuntimeOpenOptions options,
            CancellationToken cancellationToken = default)
        {
            trace.Add("acquire");
            SessionRuntimeBinding binding = new(
                options.SessionId,
                options.WorkerId,
                1,
                4,
                options.ControlPlaneInstanceId,
                "sessions/sess_coordinator/root",
                "v18-compatible",
                0,
                "sha256:" + new string('a', 64),
                options.RuntimeVersion,
                3,
                "{\"compatibilityProfile\":\"v18-compatible\",\"saveLayout\":0}");
            SessionRuntimeLease lease = new(
                binding,
                "usr_coordinator",
                SessionState.Starting,
                options.Now,
                options.Now.Add(options.LeaseDuration));
            return Task.FromResult(SessionRuntimeAcquireResult.Success(lease));
        }

        public Task<bool> RecordProcessIdentityAsync(
            SessionRuntimeBinding binding,
            WorkerProcessIdentity identity,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            trace.Add("identity");
            RecordedIdentity = identity;
            return Task.FromResult(true);
        }

        public Task<bool> MarkReadyAsync(
            SessionRuntimeBinding binding,
            WorkerReadyInfo ready,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            trace.Add("mark-ready");
            Ready = ready;
            return Task.FromResult(true);
        }

        public Task<bool> RecordHeartbeatAsync(
            SessionRuntimeBinding binding,
            WorkerHeartbeatInfo heartbeat,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> BeginStoppingAsync(
            SessionRuntimeBinding binding,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            trace.Add("stopping");
            return Task.FromResult(true);
        }

        public Task<SessionRuntimeCompletionResult> CompleteAsync(
            SessionRuntimeBinding binding,
            SessionRuntimeTerminalState terminalState,
            string reasonCode,
            long lastOutputSequence,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            trace.Add("complete");
            CompletedTerminalState = terminalState;
            CompletedReason = reasonCode;
            SessionState state = terminalState == SessionRuntimeTerminalState.Closed
                ? SessionState.Closed
                : SessionState.Crashed;
            return Task.FromResult(new SessionRuntimeCompletionResult(true, state, reasonCode));
        }

        public Task<IReadOnlyList<PersistedWorkerLease>> ListPersistedLeasesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PersistedWorkerLease>>([]);

        public Task<bool> ReconcileAsync(
            PersistedWorkerLease lease,
            string reasonCode,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingRootInspector(List<string> trace) : ISessionRootRuntimeInspector
    {
        public Task<SessionRootRuntimeDescriptor> InspectAsync(
            SessionRuntimeLease lease,
            CancellationToken cancellationToken = default)
        {
            trace.Add("inspect");
            return Task.FromResult(new SessionRootRuntimeDescriptor(
                lease.Binding.SessionRootPath,
                lease.Binding.SaveLayout,
                lease.Binding.SessionRootManifestDigest,
                lease.Binding.CompatibilityProfile));
        }
    }

    private sealed class RecordingWorkerControl(List<string> trace) : ISessionWorkerControl
    {
        public RecordingProcess Process { get; } = new(trace);
        public Exception? ReadyException { get; init; }

        public Task<IWorkerProcessHandle> StartAsync(
            WorkerLaunchSpec spec,
            CancellationToken cancellationToken = default)
        {
            trace.Add("start");
            Process.ReadyException = ReadyException;
            return Task.FromResult<IWorkerProcessHandle>(Process);
        }
    }

    private sealed class RecordingProcess(List<string> trace) : IWorkerProcessHandle
    {
        public WorkerProcessIdentity Identity { get; } = new(
            43101,
            "00000000-0000-0000-0000-000000000001",
            1001);

        public Exception? ReadyException { get; set; }

        public Task<WorkerReadyInfo> WaitForReadyAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            trace.Add("ready");
            if (ReadyException is not null)
                return Task.FromException<WorkerReadyInfo>(ReadyException);
            return Task.FromResult(new WorkerReadyInfo(
                "runtime-test",
                "upstream-test",
                0,
                4,
                "v18-compatible",
                "sha256:" + new string('a', 64)));
        }

        public Task<WorkerExitInfo> WaitForExitAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            trace.Add("exit");
            return Task.FromResult(new WorkerExitInfo(0, true, true, "worker_finished", 4, DateTimeOffset.UtcNow));
        }

        public Task RequestStopAsync(
            string reasonCode,
            DateTimeOffset deadline,
            CancellationToken cancellationToken = default)
        {
            trace.Add("request-stop");
            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken cancellationToken = default)
        {
            trace.Add("kill");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            trace.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
