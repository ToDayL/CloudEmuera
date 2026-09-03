using CloudEmuera.Api.Workers;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Debugging.Contracts;
using CloudEmuera.Ipc;
using CloudEmuera.Ipc.V9;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Worker;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "TraceReplay")]
public sealed class TraceCaptureIntegrationTests
{
    [Fact]
    public async Task TracedWorkerCreatesMatchedSnapshotAndReplayablePromptTrace()
    {
        await using WorkerProcessIsolationTests.FixtureWorkspace fixture =
            WorkerProcessIsolationTests.FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        await using WorkerManagerHost manager = await WorkerManagerHost.StartAsync(new WorkerManagerOptions(
            fixture.ControlRuntimeRoot, typeof(ConsoleWireMapper).Assembly.Location)
        {
            DebugInputTraceEnabled = true,
        });
        ApiWorkerSession session = await manager.LaunchWorkerAsync(new WorkerLaunchRequest(
            new WorkerBinding("session_capture", "worker_capture", 4), fixture.SessionRoot,
            "v18-compatible", RuntimeSaveLayout.Root, fixture.Manifest.ManifestDigest));
        await session.SendStartRuntimeAsync();
        _ = await session.WaitForAsync(value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.Ready, TimeSpan.FromSeconds(15));
        Assert.True(SpinWait.SpinUntil(() => session.OutputHub.CurrentSnapshot?.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        SessionInputResult receipt = await session.SubmitInputAsync(new SessionInputCommand(
            "session_capture", 4, "capture_input", "7", SessionInputSource.Keyboard), TimeSpan.FromSeconds(5));
        Assert.Equal(SessionInputResultCodes.Accepted, receipt.Status);
        _ = await session.WaitForAsync(value => value.PayloadCase == WorkerEnvelope.PayloadOneofCase.RuntimeCompleted, TimeSpan.FromSeconds(15));
        _ = await session.WaitForExitAsync(TimeSpan.FromSeconds(15));

        string metadata = Path.Combine(Directory.GetParent(fixture.SessionRoot)!.FullName, "metadata");
        string snapshot = Path.Combine(metadata, "debug-save-snapshot");
        string captureId = File.ReadAllText(Path.Combine(snapshot, ".capture-id")).Trim();
        DebugTraceDocument trace = DebugTraceReader.Read(Path.Combine(metadata, "debug-input-trace.jsonl"));

        Assert.Equal(captureId, trace.Header.CaptureId);
        Assert.Equal(4UL, trace.Header.OriginalWorkerEpoch);
        Assert.Equal("7", Assert.Single(trace.Prompts).Response.Value);
        Assert.Equal("terminal", trace.DefaultTarget.Type);
    }
}
