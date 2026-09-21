using System.Text.Json;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace CloudEmuera.RuntimeCompatibility.Tests;

[Collection("RuntimeDebugTraceEnvironment")]
public sealed class RuntimeDebugTraceTests
{
    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void EnabledTraceWritesOutsideGameRootAndRecordsRuntimeBoundaries()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-debug", Guid.NewGuid().ToString("N"));
        string sessionRoot = Path.Combine(root, "root");
        string? previous = Environment.GetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable);
        Directory.CreateDirectory(sessionRoot);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, "1");
            using RuntimeDebugTrace trace = Assert.IsType<RuntimeDebugTrace>(RuntimeDebugTrace.CreateWhenEnabled(sessionRoot));
            trace.Activate();
            RuntimeDebugTrace.RecordErbOutput(null, "PRINTFORMW", "waiting output", waitForInput: true);
            RuntimeDebugTrace.RecordErbWait(null, ConsoleInputType.EnterKey, stopMessageSkip: false);
            HeadlessKeyState.Reset();
            Assert.Equal(0, HeadlessKeyState.GetKeyState(87));
            Assert.Equal(0, HeadlessKeyState.GetKeyState(65));
            trace.RecordTransaction(new SequencedConsoleTransaction(7, new ConsoleTransaction(
            [
                ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("waiting output")])),
                ConsoleOperation.Open(new ConsolePrompt(ConsoleInputType.EnterKey))
            ])));
            trace.RecordLayoutDecision(
                "logical-1",
                "accept_overflow",
                atomIndex: 0,
                atomKind: "Button",
                text: "[800]",
                width: 120,
                canDivide: false,
                hasAction: true,
                lockedX: null,
                lockedXIsRelative: false,
                position: 0,
                availableWidth: 100,
                fittingCharacters: 0,
                currentSegmentCount: 0,
                currentContentWidth: 0,
                layoutWidth: 100,
                noWrap: false,
                buttonWrap: false,
                truncate: false);
            trace.RecordLayoutResult(
                "logical-1",
                layoutWidth: 100,
                lineHeight: 14,
                ConsoleLineAlignment.Left,
                temporary: false,
                requestedNoWrap: false,
                effectiveNoWrap: false,
                buttonWrap: false,
                truncate: false,
                atomCount: 1,
                [new ConsoleLine(
                    "logical-1",
                    [new PositionedInlineSegmentNode(0, 120, [new TextNode("overflow")])],
                    layoutWidth: 100,
                    lineHeight: 14)]);

            string tracePath = Path.Combine(root, "metadata", "runtime-debug.jsonl");
            Assert.True(File.Exists(tracePath));
            Assert.False(File.Exists(Path.Combine(sessionRoot, "runtime-debug.jsonl")));
            JsonElement[] entries = File.ReadLines(tracePath)
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .ToArray();
            Assert.Contains(entries, entry => entry.GetProperty("eventType").GetString() == "erb_output" &&
                entry.GetProperty("instruction").GetString() == "PRINTFORMW" &&
                entry.GetProperty("waitForInput").GetBoolean());
            Assert.Contains(entries, entry => entry.GetProperty("eventType").GetString() == "erb_wait" &&
                entry.GetProperty("inputType").GetString() == "EnterKey");
            JsonElement hostCompatibility = Assert.Single(entries, entry =>
                entry.GetProperty("eventType").GetString() == "host_compatibility");
            Assert.Equal("getkey", hostCompatibility.GetProperty("capability").GetString());
            Assert.Equal("neutral_released_state", hostCompatibility.GetProperty("behavior").GetString());
            Assert.Equal(87, hostCompatibility.GetProperty("value").GetInt32());
            Assert.Contains(entries, entry => entry.GetProperty("eventType").GetString() == "console_operation" &&
                entry.GetProperty("sequence").GetInt64() == 7 &&
                entry.GetProperty("operation").GetString() == "OpenPrompt" &&
                entry.GetProperty("detail").GetProperty("inputType").GetString() == "EnterKey");
            JsonElement decision = Assert.Single(entries, entry => entry.GetProperty("eventType").GetString() == "layout_decision");
            Assert.Equal("accept_overflow", decision.GetProperty("decision").GetString());
            Assert.False(decision.GetProperty("canDivide").GetBoolean());
            Assert.False(decision.GetProperty("buttonWrap").GetBoolean());
            JsonElement layout = Assert.Single(entries, entry => entry.GetProperty("eventType").GetString() == "layout_result");
            Assert.Equal(100, layout.GetProperty("layoutWidth").GetInt32());
            Assert.False(layout.GetProperty("buttonWrap").GetBoolean());
            Assert.Equal(1, layout.GetProperty("physicalLineCount").GetInt32());
            JsonElement physicalLine = layout.GetProperty("physicalLines")[0];
            Assert.True(physicalLine.GetProperty("overflow").GetBoolean());
            Assert.Equal(120, physicalLine.GetProperty("contentWidth").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void DisabledTraceDoesNotCreateTraceFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-debug", Guid.NewGuid().ToString("N"));
        string sessionRoot = Path.Combine(root, "root");
        string? previous = Environment.GetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable);
        Directory.CreateDirectory(sessionRoot);
        try
        {
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, null);
            Assert.Null(RuntimeDebugTrace.CreateWhenEnabled(sessionRoot));
            Assert.False(File.Exists(Path.Combine(root, "metadata", "runtime-debug.jsonl")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

[CollectionDefinition("RuntimeDebugTraceEnvironment", DisableParallelization = true)]
public sealed class RuntimeDebugTraceEnvironmentFixture
{
}
