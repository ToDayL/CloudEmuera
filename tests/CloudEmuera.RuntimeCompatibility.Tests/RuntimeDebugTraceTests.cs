using System.Text.Json;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;

namespace CloudEmuera.RuntimeCompatibility.Tests;

[Collection("RuntimeDebugTraceEnvironment")]
public sealed class RuntimeDebugTraceTests
{
    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void EnabledTraceWritesOutsideGameRootAndSummarizesPromptNodes()
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
            trace.RecordTransaction(new SequencedConsoleTransaction(7, new ConsoleTransaction(
            [
                ConsoleOperation.AppendLine(new ConsoleLine("line-1", [new TextNode("waiting output")])),
                ConsoleOperation.Open(new ConsolePrompt(ConsoleInputType.EnterKey))
            ])));

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
            Assert.Contains(entries, entry => entry.GetProperty("eventType").GetString() == "console_operation" &&
                entry.GetProperty("sequence").GetInt64() == 7 &&
                entry.GetProperty("operation").GetString() == "OpenPrompt" &&
                entry.GetProperty("detail").GetProperty("inputType").GetString() == "EnterKey");
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
    public void EnabledTraceRecordsInputToNextWaitTimingWithoutInputValue()
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

            RuntimeDebugTrace.RecordInputConsumed(
                new GameConsoleInput("prompt-1", ConsoleInputType.IntegerButton, "secret-value"),
                scriptLineCount: 100,
                functionName: "USERCOM",
                functionDepth: 2);
            RuntimeDebugTrace.RecordScriptSample(new ScriptPosition("HOTSPOT.ERB", 9), "HOT_FUNCTION");
            RuntimeDebugTrace.RecordScriptSample(new ScriptPosition("NEXT.ERB", 19), "NEXT_FUNCTION");
            RuntimeDebugTrace.RecordErbOutput(
                null,
                "PRINTFORMW",
                "after input",
                waitForInput: true,
                scriptLineCount: 112,
                functionName: "EVENTCOMEND0_通常モード",
                functionDepth: 4);
            trace.RecordStage("test_stage", TimeSpan.FromMilliseconds(2), units: 3, detail: "sample");
            trace.RecordStageDetail(
                "detailed_stage",
                TimeSpan.FromMilliseconds(3),
                units: 2,
                new Dictionary<string, object>
                {
                    ["width"] = 16,
                    ["height"] = 12,
                    ["fileWritten"] = true,
                },
                cpuElapsed: TimeSpan.FromMilliseconds(2));
            RuntimeDebugTrace.RecordErbWait(
                null,
                ConsoleInputType.EnterKey,
                stopMessageSkip: false,
                actualWait: true,
                scriptLineCount: 120,
                functionName: "EVENTCOMEND0_通常モード",
                functionDepth: 4);

            string tracePath = Path.Combine(root, "metadata", "runtime-debug.jsonl");
            string[] lines = File.ReadAllLines(tracePath);
            JsonElement[] entries = lines
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .ToArray();
            JsonElement input = Assert.Single(entries, entry => entry.GetProperty("eventType").GetString() == "timing_input_consumed");
            JsonElement output = Assert.Single(entries, entry => entry.GetProperty("eventType").GetString() == "erb_output");
            JsonElement summary = Assert.Single(entries, entry => entry.GetProperty("eventType").GetString() == "runtime_timing_summary");
            JsonElement stage = Assert.Single(
                summary.GetProperty("stageTimings").EnumerateArray(),
                entry => entry.GetProperty("name").GetString() == "test_stage");
            JsonElement detailedStage = Assert.Single(
                entries,
                entry => entry.GetProperty("eventType").GetString() == "runtime_stage_detail" &&
                    entry.GetProperty("stage").GetString() == "detailed_stage");
            JsonElement hotspot = Assert.Single(
                summary.GetProperty("scriptHotspots").EnumerateArray(),
                entry => entry.GetProperty("functionName").GetString() == "HOT_FUNCTION");

            Assert.Equal(1, input.GetProperty("turnId").GetInt64());
            Assert.Equal("IntegerButton", input.GetProperty("inputType").GetString());
            Assert.Equal("secret-value".Length, input.GetProperty("valueLength").GetInt32());
            Assert.True(output.GetProperty("sinceInputMilliseconds").GetDouble() >= 0);
            Assert.Equal(12, output.GetProperty("scriptLineCountSinceInput").GetInt32());
            Assert.Equal(20, summary.GetProperty("scriptLineCountDelta").GetInt32());
            Assert.Equal(3, stage.GetProperty("units").GetInt64());
            Assert.Equal("sample", stage.GetProperty("slowestDetail").GetString());
            Assert.Equal(2, detailedStage.GetProperty("units").GetInt64());
            Assert.Equal(2, detailedStage.GetProperty("cpuMilliseconds").GetDouble());
            Assert.Equal(16, detailedStage.GetProperty("detail").GetProperty("width").GetInt32());
            Assert.True(detailedStage.GetProperty("detail").GetProperty("fileWritten").GetBoolean());
            Assert.Equal("HOTSPOT.ERB", hotspot.GetProperty("sourceFile").GetString());
            Assert.Equal(10, hotspot.GetProperty("sourceLine").GetInt32());
            Assert.Equal(1, hotspot.GetProperty("sampleCount").GetInt64());
            Assert.DoesNotContain("secret-value", lines);
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
    public void EnabledTraceSplitsHtmlAndRasterStagesWithoutRecordingPayloads()
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
            RuntimeDebugTrace.RecordInputConsumed(
                new GameConsoleInput("prompt-1", ConsoleInputType.EnterKey, "not-recorded"),
                scriptLineCount: 0,
                functionName: "TRACE_TEST",
                functionDepth: 0);

            var adapter = new StructuredGameConsole();
            var headless = new EmueraConsole(adapter, adapter.Clock, CancellationToken.None);
            headless.BeginExecutionOutput();
            headless.PrintHtml("<p align='left'>trace payload</p>", toPrintBuffer: false);
            using var graphics = new GraphicsImage(1);
            graphics.GCreate(8, 6, useGDI: false);
            Assert.True(headless.CBG_SetGraphics(graphics, 0, 0, 1));

            RuntimeDebugTrace.RecordErbWait(
                null,
                ConsoleInputType.EnterKey,
                stopMessageSkip: false,
                actualWait: true,
                scriptLineCount: 1,
                functionName: "TRACE_TEST",
                functionDepth: 0);

            string tracePath = Path.Combine(root, "metadata", "runtime-debug.jsonl");
            string[] lines = File.ReadAllLines(tracePath);
            JsonElement[] entries = lines
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .ToArray();
            JsonElement raster = Assert.Single(
                entries,
                entry => entry.GetProperty("eventType").GetString() == "runtime_stage_detail" &&
                    entry.GetProperty("stage").GetString() == "raster_png_encode");
            JsonElement html = Assert.Single(
                entries,
                entry => entry.GetProperty("eventType").GetString() == "runtime_stage_detail" &&
                    entry.GetProperty("stage").GetString() == "html_print");
            JsonElement translation = Assert.Single(
                entries,
                entry => entry.GetProperty("eventType").GetString() == "runtime_stage_detail" &&
                    entry.GetProperty("stage").GetString() == "html_translate");

            Assert.Equal("cbg_set_graphics", raster.GetProperty("detail").GetProperty("operation").GetString());
            Assert.Equal(8, raster.GetProperty("detail").GetProperty("width").GetInt32());
            Assert.Equal(6, raster.GetProperty("detail").GetProperty("height").GetInt32());
            Assert.Equal(1, html.GetProperty("detail").GetProperty("textPartCount").GetInt32());
            Assert.Equal(1, translation.GetProperty("detail").GetProperty("textPartCount").GetInt32());
            Assert.DoesNotContain("not-recorded", lines);
            Assert.Contains(
                entries,
                entry => entry.GetProperty("eventType").GetString() == "runtime_stage_detail" &&
                    entry.GetProperty("stage").GetString() == "html_parse");
            Assert.Contains(
                entries,
                entry => entry.GetProperty("eventType").GetString() == "runtime_stage_detail" &&
                    entry.GetProperty("stage").GetString() == "html_flush_layout");
        }
        finally
        {
            GlobalStatic.Reset();
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void DisabledTraceDoesNotCreateTraceFileInProduction()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-debug", Guid.NewGuid().ToString("N"));
        string sessionRoot = Path.Combine(root, "root");
        string? previousTrace = Environment.GetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable);
        string? previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Directory.CreateDirectory(sessionRoot);
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, null);
            Assert.Null(RuntimeDebugTrace.CreateWhenEnabled(sessionRoot));
            Assert.False(File.Exists(Path.Combine(root, "metadata", "runtime-debug.jsonl")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, previousTrace);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public void TrueEnablesTraceInProduction()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-runtime-debug", Guid.NewGuid().ToString("N"));
        string sessionRoot = Path.Combine(root, "root");
        string? previousTrace = Environment.GetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable);
        string? previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Directory.CreateDirectory(sessionRoot);
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, "true");
            using RuntimeDebugTrace trace = Assert.IsType<RuntimeDebugTrace>(RuntimeDebugTrace.CreateWhenEnabled(sessionRoot));
            trace.Activate();
            RuntimeDebugTrace.RecordErbWait(null, ConsoleInputType.EnterKey, stopMessageSkip: false);

            Assert.True(File.Exists(Path.Combine(root, "metadata", "runtime-debug.jsonl")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeDebugTrace.EnvironmentVariable, previousTrace);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RuntimeBridge")]
    public async Task ReadAnyKeySuppressesTheEventCommandEndFallbackWait()
    {
        GlobalStatic.Reset();
        var adapter = new StructuredGameConsole();
        var headless = new EmueraConsole(adapter, adapter.Clock, CancellationToken.None);
        var process = new MinorShift.Emuera.GameProc.Process(headless);
        GlobalStatic.Process = process;
        process.NeedWaitToEventComEnd = true;
        try
        {
            Task wait = Task.Run(() => headless.ReadAnyKey());
            Assert.True(SpinWait.SpinUntil(() => adapter.CurrentPrompt is not null, TimeSpan.FromSeconds(2)));
            Assert.False(process.NeedWaitToEventComEnd);
            ConsolePrompt prompt = adapter.CurrentPrompt!;
            Assert.Equal(ConsoleInputResultKind.Accepted, adapter.SubmitCurrentInput(
                new ConsoleInputAttempt("event-command-end", string.Empty)).Kind);
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            GlobalStatic.Reset();
        }
    }
}

[CollectionDefinition("RuntimeDebugTraceEnvironment", DisableParallelization = true)]
public sealed class RuntimeDebugTraceEnvironmentFixture
{
}
