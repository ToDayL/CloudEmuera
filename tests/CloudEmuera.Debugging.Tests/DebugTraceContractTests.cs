using System.Text;
using System.Text.Json;
using CloudEmuera.Debugging.Contracts;
using CloudEmuera.Debugger;
using CloudEmuera.Application.Sessions;
using Xunit;

namespace CloudEmuera.Debugging.Tests;

[Trait("Category", "TraceReplay")]
public sealed class DebugTraceContractTests
{
    [Fact]
    public void WriterProducesOneJsonObjectPerLineAndReaderSelectsTerminal()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        using (var writer = new DebugTraceWriter(path, Header()))
        {
            writer.Write(DebugTraceEventTypes.PromptOpen, Open(1, "prompt-1"));
            writer.Write(DebugTraceEventTypes.PromptResponse, Response(1), flush: true);
            writer.Write(DebugTraceEventTypes.Terminal, new { status = "completed" }, flush: true, terminal: true);
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, line => Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(line).RootElement.ValueKind));
        DebugTraceDocument trace = DebugTraceReader.Read(path);
        Assert.Single(trace.Prompts);
        Assert.Equal("terminal", trace.DefaultTarget.Type);
    }

    [Fact]
    public void ReaderIgnoresOnlyMalformedUnterminatedTail()
    {
        string valid = Event(1, DebugTraceEventTypes.Header, Header()) + "\n" +
            Event(2, DebugTraceEventTypes.Terminal, new { status = "completed" }) + "\n";
        using var partial = new MemoryStream(Encoding.UTF8.GetBytes(valid + "{\"version\":"));
        Assert.Equal("terminal", DebugTraceReader.Read(partial).DefaultTarget.Type);

        using var complete = new MemoryStream(Encoding.UTF8.GetBytes(valid + "{\"version\":\n"));
        DebugTraceException exception = Assert.Throws<DebugTraceException>(() => DebugTraceReader.Read(complete));
        Assert.Equal(DebugReplayStatuses.TraceInvalid, exception.Code);
    }

    [Fact]
    public void TruncationUsesReservedTailAndRequiresExplicitOptIn()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        using (var writer = new DebugTraceWriter(path, Header(), 16 * 1024))
        {
            for (int index = 0; index < 8; index++)
                writer.Write(DebugTraceEventTypes.ClockValue, new { value = new string('x', 3_000) });
            Assert.True(writer.IsTruncated);
            writer.Write(DebugTraceEventTypes.Terminal, new { status = "completed" }, flush: true, terminal: true);
        }

        DebugTraceException exception = Assert.Throws<DebugTraceException>(() => DebugTraceReader.Read(path));
        Assert.Equal(DebugReplayStatuses.TraceTruncated, exception.Code);
        Assert.True(DebugTraceReader.Read(path, allowTruncated: true).IsTruncated);
    }

    [Fact]
    public void OversizedEventTruncatesWithoutThrowingOrLosingMarker()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        using (var writer = new DebugTraceWriter(path, Header()))
        {
            writer.Write(DebugTraceEventTypes.ClockValue,
                new { value = new string('x', DebugTraceContract.MaxLineBytes) });
            Assert.True(writer.IsTruncated);
            writer.Write(DebugTraceEventTypes.Terminal, new { status = "completed" }, flush: true, terminal: true);
        }

        DebugTraceDocument trace = DebugTraceReader.Read(path, allowTruncated: true);
        Assert.Contains(trace.Events, value => value.Type == DebugTraceEventTypes.TraceTruncated);
    }

    [Fact]
    public void RightPointerMessageSkipMapsBackToFormalReplayInput()
    {
        JsonElement pointer = JsonSerializer.SerializeToElement(new { x = 12, y = 34, button = 2, pressed = true });
        var response = new DebugPromptResponse
        {
            Ordinal = 1,
            Result = DebugPromptResolutionKinds.Accepted,
            Source = "POINTER",
            Value = string.Empty,
            NormalizedValue = string.Empty,
            PointerData = pointer,
        };

        SessionInputCommand command = ReplayEngine.ToInput("session_test", 2, response);

        Assert.Equal(SessionInputSource.PointerDevice, command.Source);
        Assert.Equal(new SessionPointerInput(12, 34, 2, true), command.PointerData);
        Assert.Equal(string.Empty, command.Value);
    }

    private static DebugTraceHeader Header() => new()
    {
        CaptureId = "cap_test",
        CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
        SessionId = "session_test",
        OriginalWorkerEpoch = 1,
        CompatibilityProfile = "v18-compatible",
        SaveLayout = "sav",
        FontSize = 18,
        LineHeight = 19,
        StartupWallClock = DateTimeOffset.UnixEpoch.AddSeconds(1),
        SaveSnapshotComplete = true,
    };

    private static DebugPromptOpen Open(long ordinal, string promptId) => new()
    {
        Ordinal = ordinal,
        PromptId = promptId,
        InputType = "Text",
        AllowedSources = ["KEYBOARD"],
    };

    private static DebugPromptResponse Response(long ordinal) => new()
    {
        Ordinal = ordinal,
        Result = DebugPromptResolutionKinds.Accepted,
        Source = "KEYBOARD",
        Value = "ok",
        NormalizedValue = "ok",
    };

    private static string Event(long index, string type, object data)
    {
        JsonElement element = JsonSerializer.SerializeToElement(data, DebugTraceJson.CompactOptions);
        return JsonSerializer.Serialize(new DebugTraceEvent(1, index, type, 0, element), DebugTraceJson.CompactOptions);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudemuera-debug-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
