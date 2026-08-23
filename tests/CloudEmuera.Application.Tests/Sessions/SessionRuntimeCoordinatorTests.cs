using CloudEmuera.Application.Sessions;
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
    public async Task OpenPassesBrowserWidthToEveryNewWorker()
    {
        var trace = new List<string>();
        RecordingStore store = new(trace);
        RecordingRootInspector inspector = new(trace);
        RecordingWorkerControl workerControl = new(trace);
        SessionRuntimeCoordinator coordinator = new(store, workerControl, inspector, TimeProvider.System);

        await coordinator.OpenAsync(CreateOpenOptions(browserWidth: 390));

        Assert.NotNull(workerControl.LaunchSpec);
        Assert.Equal(390, workerControl.LaunchSpec!.BrowserWidth);
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
    public async Task OpenDoesNotReleaseLeaseWhenWorkerExitCannotBeConfirmed()
    {
        var trace = new List<string>();
        RecordingStore store = new(trace);
        RecordingRootInspector inspector = new(trace);
        RecordingWorkerControl workerControl = new(trace)
        {
            StartException = new SessionRuntimeException(
                SessionRuntimeResultCodes.WorkerExitUnconfirmed,
                "The Worker exit could not be confirmed.")
        };
        SessionRuntimeCoordinator coordinator = new(store, workerControl, inspector, TimeProvider.System);

        SessionRuntimeException exception = await Assert.ThrowsAsync<SessionRuntimeException>(() => coordinator.OpenAsync(CreateOpenOptions()));

        Assert.Equal(SessionRuntimeResultCodes.WorkerExitUnconfirmed, exception.Code);
        Assert.Null(store.CompletedTerminalState);
        Assert.DoesNotContain("complete", trace);
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
        Assert.Equal(opened.Lease.Binding.StateVersion + 1, store.CompletedBinding!.StateVersion);
    }

    [Fact]
    public async Task ForceCloseAlwaysCompletesAsCrashEquivalentEvenAfterCooperativeExit()
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
            new SessionRuntimeCloseRequest(opened.Lease.Binding.SessionId, Force: true, "admin_force_stopped"));

        Assert.True(result.Completion.Applied);
        Assert.Equal(SessionState.Crashed, result.Completion.State);
        Assert.Equal(SessionRuntimeTerminalState.Crashed, store.CompletedTerminalState);
        Assert.Equal("admin_force_stopped", store.CompletedReason);
        Assert.Equal(["stopping", "request-stop", "exit", "complete"], trace);
    }

    [Fact]
    public async Task CloseDoesNotCompleteWhenForcedExitCannotBeConfirmed()
    {
        var trace = new List<string>();
        RecordingStore store = new(trace);
        RecordingRootInspector inspector = new(trace);
        RecordingWorkerControl workerControl = new(trace);
        SessionRuntimeCoordinator coordinator = new(store, workerControl, inspector, TimeProvider.System);
        SessionRuntimeOpenResult opened = await coordinator.OpenAsync(CreateOpenOptions());
        workerControl.Process.ExitConfirmed = false;

        SessionRuntimeException exception = await Assert.ThrowsAsync<SessionRuntimeException>(() => coordinator.CloseAsync(
            opened.Lease.Binding,
            workerControl.Process,
            new SessionRuntimeCloseRequest(opened.Lease.Binding.SessionId, Force: false, "requested")));

        Assert.Equal(SessionRuntimeResultCodes.WorkerExitUnconfirmed, exception.Code);
        Assert.Null(store.CompletedTerminalState);
        Assert.Contains("kill", trace);
        Assert.DoesNotContain("complete", trace);
    }

    [Fact]
    public async Task CloseRefreshesBindingWhenHeartbeatAdvancesStateVersion()
    {
        var trace = new List<string>();
        RecordingStore store = new(trace) { ReturnStaleOnFirstStop = true };
        RecordingRootInspector inspector = new(trace);
        RecordingWorkerControl workerControl = new(trace);
        SessionRuntimeCoordinator coordinator = new(store, workerControl, inspector, TimeProvider.System);
        SessionRuntimeOpenResult opened = await coordinator.OpenAsync(CreateOpenOptions());
        trace.Clear();
        RefreshingWorkerRouter router = new(opened.Lease.Binding, workerControl.Process);
        SessionLifecycleExecutor executor = new(coordinator, router, new RecordingOpenOptionsFactory());

        SessionRuntimeCloseResult result = await executor.CloseAsync(opened.Lease.Binding.SessionId);

        Assert.True(result.Completion.Applied);
        Assert.Equal(SessionState.Closed, result.Completion.State);
        Assert.Equal(2, router.Calls);
        Assert.Equal(["stale-stopping", "stopping", "request-stop", "exit", "complete"], trace);
    }

    private static SessionRuntimeOpenOptions CreateOpenOptions(int browserWidth = 0) => new(
        "sess_coordinator",
        "ctl_coordinator",
        "wrk_coordinator",
        "headless-test",
        2,
        "uds/wrk_coordinator",
        TimeSpan.FromSeconds(30),
        DateTimeOffset.UtcNow,
        browserWidth);

    private sealed class RecordingStore(List<string> trace) : ISessionRuntimeStore
    {
        private bool staleStopReturned;

        public WorkerProcessIdentity? RecordedIdentity { get; private set; }
        public WorkerReadyInfo? Ready { get; private set; }
        public SessionRuntimeTerminalState? CompletedTerminalState { get; private set; }
        public string? CompletedReason { get; private set; }
        public SessionRuntimeBinding? CompletedBinding { get; private set; }
        public bool ReturnStaleOnFirstStop { get; init; }

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
                3);
            SessionRuntimeLease lease = new(
                binding,
                "usr_coordinator",
                SessionState.Starting,
                options.Now,
                options.Now.Add(options.LeaseDuration));
            return Task.FromResult(SessionRuntimeAcquireResult.Success(lease));
        }

        public Task<SessionRuntimeWriteResult> RecordProcessIdentityAsync(
            SessionRuntimeBinding binding,
            WorkerProcessIdentity identity,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            trace.Add("identity");
            RecordedIdentity = identity;
            return Task.FromResult(SessionRuntimeWriteResult.Accepted(binding));
        }

        public Task<SessionRuntimeWriteResult> MarkReadyAsync(
            SessionRuntimeBinding binding,
            WorkerReadyInfo ready,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            trace.Add("mark-ready");
            Ready = ready;
            return Task.FromResult(SessionRuntimeWriteResult.Accepted(binding with
            {
                StateVersion = checked(binding.StateVersion + 1),
                InitialOutputSequence = Math.Max(binding.InitialOutputSequence, ready.LastOutputSequence),
            }));
        }

        public Task<SessionRuntimeWriteResult> RecordHeartbeatAsync(
            SessionRuntimeBinding binding,
            WorkerHeartbeatInfo heartbeat,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) => Task.FromResult(SessionRuntimeWriteResult.Accepted(binding));

        public Task<SessionRuntimeWriteResult> BeginStoppingAsync(
            SessionRuntimeBinding binding,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            if (ReturnStaleOnFirstStop && !staleStopReturned)
            {
                staleStopReturned = true;
                trace.Add("stale-stopping");
                return Task.FromResult(SessionRuntimeWriteResult.Stale());
            }

            trace.Add("stopping");
            return Task.FromResult(SessionRuntimeWriteResult.Accepted(binding with { StateVersion = checked(binding.StateVersion + 1) }));
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
            CompletedBinding = binding;
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
        public WorkerLaunchSpec? LaunchSpec { get; private set; }
        public Exception? ReadyException { get; init; }
        public Exception? StartException { get; init; }

        public Task<IWorkerProcessHandle> StartAsync(
            WorkerLaunchSpec spec,
            CancellationToken cancellationToken = default)
        {
            trace.Add("start");
            LaunchSpec = spec;
            if (StartException is not null)
                return Task.FromException<IWorkerProcessHandle>(StartException);
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
        public bool ExitConfirmed { get; set; } = true;

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
            return Task.FromResult(new WorkerExitInfo(
                ExitConfirmed ? 0 : null,
                ExitConfirmed,
                ExitConfirmed,
                ExitConfirmed ? "worker_finished" : SessionRuntimeResultCodes.WorkerExitUnconfirmed,
                4,
                DateTimeOffset.UtcNow));
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

        public void UpdateRuntimeBinding(SessionRuntimeBinding binding, bool persistenceReady = false)
        {
        }
    }

    private sealed class RefreshingWorkerRouter(
        SessionRuntimeBinding binding,
        IWorkerProcessHandle process) : ICurrentWorkerRouter
    {
        public int Calls { get; private set; }

        public Task<CurrentWorkerRoute?> GetCurrentAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            SessionRuntimeBinding current = Calls == 1
                ? binding
                : binding with { StateVersion = checked(binding.StateVersion + 1) };
            return Task.FromResult<CurrentWorkerRoute?>(new CurrentWorkerRoute(current, process));
        }
    }

    private sealed class RecordingOpenOptionsFactory : IWorkerOpenOptionsFactory
    {
        public SessionRuntimeOpenOptions Create(string sessionId, int browserWidth = 0) =>
            CreateOpenOptionsForTest(sessionId, browserWidth);

        private static SessionRuntimeOpenOptions CreateOpenOptionsForTest(string sessionId, int browserWidth) => new(
            sessionId,
            "ctl_options",
            "wrk_options",
            "headless-test",
            2,
            "uds/wrk_options",
            TimeSpan.FromSeconds(30),
            DateTimeOffset.UtcNow,
            browserWidth);
    }
}
