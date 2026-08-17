using CloudEmuera.Api.Workers;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;
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

    private static ApiWorkerSession CreateSession()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"cloudemuera-heartbeat-{Guid.NewGuid():N}");
        var options = new WorkerManagerOptions(dataRoot, Path.Combine(dataRoot, "CloudEmuera.Worker.dll"));
        var session = new ApiWorkerSession(
            new WorkerLaunchRequest(
                new WorkerBinding("sess_heartbeat", "wrk_heartbeat", 1),
                dataRoot,
                "v18-compatible",
                RuntimeSaveLayout.Root),
            options,
            NullLogger<ApiWorkerSession>.Instance);
        session.SetBootstrapPath(Path.Combine(options.BootstrapDirectory, "unused-bootstrap.json"));
        return session;
    }
}
