using CloudEmuera.Ipc.V5;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

public sealed class WorkerHeartbeatPayloadTests
{
    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public void HeartbeatFieldsAreDerivedFromOnePromptSnapshot()
    {
        // Regression (eraTW epoch 10): the heartbeat loop used to read
        // CurrentPrompt once for WaitingForInput and again for CurrentPromptId.
        // A prompt transition between those reads could emit
        // WaitingForInput=true with an empty CurrentPromptId, which the API
        // durable store rejects as an invalid heartbeat payload and previously
        // used to fence the Session as CRASHED.
        CloudEmuera.RuntimeAdapter.ConsolePrompt prompt = new(
            "prompt-1",
            ConsoleInputType.Integer,
            timeout: TimeSpan.FromSeconds(5),
            timeoutBehavior: ConsolePromptTimeoutBehavior.ReturnDefaultValue,
            openedAtUnixMilliseconds: 1_000,
            deadlineUnixMilliseconds: 6_000);

        WorkerHeartbeat heartbeat = WorkerRuntimeController.CreateHeartbeat(42, 42, 1, prompt);

        Assert.True(heartbeat.WaitingForInput);
        Assert.Equal("prompt-1", heartbeat.CurrentPromptId);
        Assert.NotNull(heartbeat.PromptTiming);
        Assert.Equal(42, heartbeat.OutputSequence);

        WorkerHeartbeat idle = WorkerRuntimeController.CreateHeartbeat(43, 42, 1, null);

        Assert.False(idle.WaitingForInput);
        Assert.Equal(string.Empty, idle.CurrentPromptId);
        Assert.Null(idle.PromptTiming);
        Assert.Equal(42, idle.OutputSequence);
    }
}
