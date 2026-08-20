using System.Text.Json;
using CloudEmuera.EmueraRuntime.UpstreamHeadless;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
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
