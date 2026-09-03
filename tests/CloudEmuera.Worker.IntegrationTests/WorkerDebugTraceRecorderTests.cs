using CloudEmuera.Debugging.Contracts;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Worker;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "TraceReplay")]
public sealed class WorkerDebugTraceRecorderTests
{
    [Fact]
    public void RightPointerMessageSkipRoundTripsThroughTracePayload()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-trace-pointer-" + Guid.NewGuid().ToString("N"));
        string sessionRoot = Path.Combine(root, "root");
        string metadata = Path.Combine(root, "metadata");
        Directory.CreateDirectory(sessionRoot);
        Directory.CreateDirectory(metadata);
        string tracePath = Path.Combine(metadata, "debug-input-trace.jsonl");
        try
        {
            var bootstrap = new WorkerBootstrapDocument
            {
                SessionId = "session_pointer",
                WorkerId = "worker_pointer",
                WorkerEpoch = 7,
                SessionRoot = sessionRoot,
                CompatibilityProfile = "v18-compatible",
                DebugInputTraceEnabled = true,
                DebugInputTracePath = tracePath,
                DebugCaptureId = "cap_pointer",
                SaveLayout = 1,
                FontSize = 18,
                LineHeight = 19,
                RandomSeed = 42,
            };
            using (var recorder = new WorkerDebugTraceRecorder(bootstrap))
            {
                var prompt = new ConsolePrompt("prompt_pointer", ConsoleInputType.AnyKey);
                var attempt = new ConsoleInputAttempt(
                    "right_skip", string.Empty, ConsoleInputSource.Pointer,
                    pointer: new ConsolePointerPayload(12, 34, button: 2, pressed: true));
                var input = new GameConsoleInput(prompt.PromptId, prompt.InputType, string.Empty, skipMessage: true, pointer: attempt.Pointer);
                recorder.PromptOpened(prompt);
                recorder.PromptResolved(prompt, ConsoleInputResult.Accepted(attempt, input), attempt);
                recorder.Terminal("completed", 1);
            }

            DebugTraceDocument trace = DebugTraceReader.Read(tracePath);
            DebugPromptResponse response = Assert.Single(trace.Prompts).Response;
            Assert.Equal("POINTER", response.Source);
            Assert.Equal(2, response.PointerData!.Value.GetProperty("button").GetInt32());
            Assert.True(response.PointerData.Value.GetProperty("pressed").GetBoolean());
            Assert.Equal(12, response.PointerData.Value.GetProperty("x").GetInt32());
            Assert.Equal(34, response.PointerData.Value.GetProperty("y").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
