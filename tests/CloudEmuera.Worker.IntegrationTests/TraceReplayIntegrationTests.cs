using CloudEmuera.Debugger;
using CloudEmuera.Debugging.Contracts;
using CloudEmuera.Application.Fonts;
using CloudEmuera.RuntimeAdapter;
using CloudEmuera.Worker;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "TraceReplay")]
public sealed class TraceReplayIntegrationTests
{
    [Fact]
    public async Task FormalWorkerReplaysAcceptedInputToTerminal()
    {
        await using WorkerProcessIsolationTests.FixtureWorkspace fixture =
            WorkerProcessIsolationTests.FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        string capture = Path.Combine(fixture.Root, "capture");
        string snapshot = Path.Combine(capture, "debug-save-snapshot");
        string snapshotRoot = Path.Combine(snapshot, "root");
        string tracePath = Path.Combine(capture, "debug-input-trace.jsonl");
        string workspace = Path.Combine(fixture.Root, "debug-workspace");
        Directory.CreateDirectory(snapshotRoot);
        File.WriteAllText(Path.Combine(snapshot, ".capture-id"), "cap_integration");
        using (var writer = new DebugTraceWriter(tracePath, new DebugTraceHeader
        {
            CaptureId = "cap_integration",
            CreatedAt = DateTimeOffset.UtcNow,
            SessionId = "session_replay",
            OriginalWorkerEpoch = 1,
            CompatibilityProfile = "v18-compatible",
            SaveLayout = "root",
            SessionRootManifestDigest = fixture.Manifest.ManifestDigest,
            FontFaceId = RuntimeFontDefaults.DefaultFaceId,
            FontSize = 18,
            LineHeight = 19,
            RandomAlgorithm = "SFMT",
            RandomSeed = 1234,
            StartupWallClock = DateTimeOffset.UtcNow,
            SaveSnapshotComplete = true,
        }))
        {
            writer.Write(DebugTraceEventTypes.PromptOpen, new DebugPromptOpen
            {
                Ordinal = 1,
                PromptId = "captured_prompt",
                InputType = "Integer",
                AllowedSources = ["KEYBOARD", "BUTTON"],
            });
            writer.Write(DebugTraceEventTypes.PromptResponse, new DebugPromptResponse
            {
                Ordinal = 1,
                Result = DebugPromptResolutionKinds.Accepted,
                Source = "KEYBOARD",
                Value = "7",
                NormalizedValue = "7",
            });
            writer.Write(DebugTraceEventTypes.Terminal, new { status = "completed" }, flush: true, terminal: true);
        }
        string repositoryRoot = FindRepositoryRoot();
        var options = new ReplayOptions(
            tracePath,
            snapshot,
            fixture.SessionRoot,
            workspace,
            Output: null,
            typeof(ConsoleWireMapper).Assembly.Location,
            Path.Combine(repositoryRoot, "assets", "runtime-fonts"),
            "auto",
            "strict",
            ResetWorkspace: false,
            AllowCaptureMismatch: false,
            AllowTruncated: false,
            TimeSpan.FromSeconds(30));

        (int exitCode, DebugReplayResult result) = await ReplayEngine.RunAsync(options, CancellationToken.None);

        Assert.True(exitCode == 0, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(DebugReplayStatuses.TerminalReached, result.Status);
        Assert.Equal(1, result.LastMatchedPromptOrdinal);
        Assert.Contains("V18-END", File.ReadAllText(Path.Combine(workspace, "output", "console.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RightPointerReplaySkipsFollowingAnyKeyWait()
    {
        await using WorkerProcessIsolationTests.FixtureWorkspace fixture =
            WorkerProcessIsolationTests.FixtureWorkspace.Create("v18-core", RuntimeSaveLayout.Root);
        File.WriteAllText(Path.Combine(fixture.SessionRoot, "ERB", "START.ERB"),
            "@SYSTEM_TITLE\nPRINTFORMW FIRST-WAIT\nPRINTL BETWEEN-WAITS\nWAITANYKEY\nPRINTL AFTER-WAITS\nINPUT\nPRINTL DONE\nQUIT\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        string capture = Path.Combine(fixture.Root, "pointer-capture");
        string snapshot = Path.Combine(capture, "debug-save-snapshot");
        string tracePath = Path.Combine(capture, "debug-input-trace.jsonl");
        string workspace = Path.Combine(fixture.Root, "pointer-workspace");
        Directory.CreateDirectory(Path.Combine(snapshot, "root"));
        File.WriteAllText(Path.Combine(snapshot, ".capture-id"), "cap_pointer_replay");
        using (var writer = new DebugTraceWriter(tracePath, Header(fixture, "cap_pointer_replay", "session_pointer_replay")))
        {
            writer.Write(DebugTraceEventTypes.PromptOpen, Prompt(1, "EnterKey", ["KEYBOARD", "BUTTON", "POINTER", "SYSTEM"]));
            writer.Write(DebugTraceEventTypes.PromptResponse, new DebugPromptResponse
            {
                Ordinal = 1,
                Result = DebugPromptResolutionKinds.Accepted,
                Source = "POINTER",
                Value = string.Empty,
                NormalizedValue = string.Empty,
                PointerData = JsonSerializer.SerializeToElement(new { x = 5, y = 6, button = 2, pressed = true }),
            });
            writer.Write(DebugTraceEventTypes.PromptOpen, Prompt(2, "Integer", ["KEYBOARD", "BUTTON"]));
            writer.Write(DebugTraceEventTypes.PromptResponse, new DebugPromptResponse
            {
                Ordinal = 2,
                Result = DebugPromptResolutionKinds.Accepted,
                Source = "KEYBOARD",
                Value = "7",
                NormalizedValue = "7",
            });
            writer.Write(DebugTraceEventTypes.Terminal, new { status = "completed" }, flush: true, terminal: true);
        }
        string repositoryRoot = FindRepositoryRoot();
        var options = new ReplayOptions(
            tracePath, snapshot, fixture.SessionRoot, workspace, Output: null,
            typeof(ConsoleWireMapper).Assembly.Location, Path.Combine(repositoryRoot, "assets", "runtime-fonts"),
            "auto", "strict", false, false, false, TimeSpan.FromSeconds(30));

        (int exitCode, DebugReplayResult result) = await ReplayEngine.RunAsync(options, CancellationToken.None);

        Assert.True(exitCode == 0, JsonSerializer.Serialize(result));
        Assert.Equal(2, result.LastMatchedPromptOrdinal);
        string html = File.ReadAllText(Path.Combine(workspace, "output", "console.html"));
        Assert.Contains("FIRST-WAIT", html, StringComparison.Ordinal);
        Assert.Contains("BETWEEN-WAITS", html, StringComparison.Ordinal);
        Assert.Contains("AFTER-WAITS", html, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT_MISMATCH", html, StringComparison.Ordinal);
    }

    private static DebugTraceHeader Header(
        WorkerProcessIsolationTests.FixtureWorkspace fixture, string captureId, string sessionId) => new()
    {
        CaptureId = captureId,
        CreatedAt = DateTimeOffset.UtcNow,
        SessionId = sessionId,
        OriginalWorkerEpoch = 1,
        CompatibilityProfile = "v18-compatible",
        SaveLayout = "root",
        SessionRootManifestDigest = fixture.Manifest.ManifestDigest,
        FontFaceId = RuntimeFontDefaults.DefaultFaceId,
        FontSize = 18,
        LineHeight = 19,
        RandomAlgorithm = "SFMT",
        RandomSeed = 1234,
        StartupWallClock = DateTimeOffset.UtcNow,
        SaveSnapshotComplete = true,
    };

    private static DebugPromptOpen Prompt(long ordinal, string inputType, string[] allowedSources) => new()
    {
        Ordinal = ordinal,
        PromptId = "captured_" + ordinal,
        InputType = inputType,
        AllowedSources = allowedSources,
    };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "tests", "fixtures", "runtime", "manifest.json")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
