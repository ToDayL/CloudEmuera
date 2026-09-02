// CloudEmuera opt-in runtime trace. It stays outside the game root so ERB
// file enumeration and native save behavior are unaffected.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

internal sealed class RuntimeDebugTrace : IDisposable
{
    internal const string EnvironmentVariable = "CLOUDEMUERA_RUNTIME_DEBUG_TRACE";
    private const int MaxTextLength = 4_096;
    private readonly object sync = new();
    private readonly System.IO.StreamWriter writer;
    private readonly long traceStartedTimestamp = Stopwatch.GetTimestamp();
    private readonly string traceId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<CalledFunction, List<ActiveFunctionTiming>> activeFunctions = [];
    private readonly Dictionary<string, TimingAggregate> functionAggregates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimingAggregate> stageAggregates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptHotspotAggregate> scriptHotspots = new(StringComparer.Ordinal);
    private long turnId;
    private long? inputConsumedTimestamp;
    private int inputScriptLineCount;
    private long waitGeneration;
    private long? waitStartedTimestamp;
    private long? lastScriptSampleTimestamp;
    private ScriptPosition? lastScriptSamplePosition;
    private string lastScriptSampleFunctionName;
    private bool disposed;

    private RuntimeDebugTrace(string path)
    {
        writer = new System.IO.StreamWriter(new System.IO.FileStream(
            path,
            System.IO.FileMode.Append,
            System.IO.FileAccess.Write,
            System.IO.FileShare.Read))
        {
            AutoFlush = true
        };
        Write(new
        {
            eventType = "runtime_trace_started",
            traceId,
            processId = Environment.ProcessId,
            startedAtUtc = DateTimeOffset.UtcNow,
            stopwatchFrequency = Stopwatch.Frequency
        });
    }

    internal static RuntimeDebugTrace Current { get; private set; }

    internal static RuntimeDebugTrace CreateWhenEnabled(string sessionRoot)
    {
        string value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        bool enabled = string.Equals(value, "1", StringComparison.Ordinal) ||
            bool.TryParse(value, out bool parsed) && parsed;
        if (!enabled)
            return null;

        string metadataDirectory = System.IO.Path.Combine(
            System.IO.Directory.GetParent(System.IO.Path.GetFullPath(sessionRoot))!.FullName,
            "metadata");
        System.IO.Directory.CreateDirectory(metadataDirectory);
        return new RuntimeDebugTrace(System.IO.Path.Combine(metadataDirectory, "runtime-debug.jsonl"));
    }

    internal void Activate() => Current = this;

    internal static void RecordInputConsumed(
        GameConsoleInput input,
        int scriptLineCount,
        string functionName,
        int functionDepth)
    {
        Current?.RecordInputConsumedCore(input, scriptLineCount, functionName, functionDepth);
    }

    internal static void RecordFunctionEnter(CalledFunction function) => Current?.FunctionEnter(function);

    internal static void RecordFunctionExit(CalledFunction function) => Current?.FunctionExit(function);

    internal static void RecordFunctionsCleared() => Current?.FunctionsCleared();

    internal static void RecordScriptSample(
        ScriptPosition? position,
        string functionName) =>
        Current?.RecordScriptSampleCore(position, functionName);

    internal void RecordStage(string stage, TimeSpan elapsed, long units = 1, string detail = null) =>
        Stage(stage, elapsed, units, detail);

    internal void RecordStageDetail(
        string stage,
        TimeSpan elapsed,
        long units,
        IReadOnlyDictionary<string, object> detail,
        TimeSpan? cpuElapsed = null)
    {
        if (string.IsNullOrWhiteSpace(stage) || elapsed < TimeSpan.Zero || units < 0)
            return;
        if (cpuElapsed is { } cpu && cpu < TimeSpan.Zero)
            cpuElapsed = null;

        lock (sync)
        {
            if (disposed || inputConsumedTimestamp is not long inputStart)
                return;

            long now = Stopwatch.GetTimestamp();
            string slowestDetail = detail is null ? null : JsonSerializer.Serialize(detail);
            AddAggregate(stageAggregates, stage, stage, position: null, elapsed, units, slowestDetail);
            WriteLocked(new
            {
                eventType = "runtime_stage_detail",
                traceId,
                traceElapsedMilliseconds = TraceElapsedMilliseconds(now),
                sinceInputMilliseconds = ElapsedMilliseconds(inputStart, now),
                turnId,
                stage,
                elapsedMilliseconds = Milliseconds(elapsed),
                cpuMilliseconds = cpuElapsed is TimeSpan cpuTime ? Milliseconds(cpuTime) : (double?)null,
                units,
                detail
            });
        }
    }

    internal void RecordConsoleTransactionTiming(
        SequencedConsoleTransaction transaction,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string detail = transaction.Transaction.Operations.Count == 1
            ? transaction.Transaction.Operations[0].Kind.ToString()
            : "batch";
        Stage(
            "console_apply_transaction",
            elapsed,
            transaction.Transaction.Operations.Count,
            detail);
    }

    internal void RecordRuntimeWidth(int configuredWidth, int browserWidth, int effectiveWidth, int drawableWidth)
    {
        Write(new
        {
            eventType = "runtime_width",
            traceId,
            traceElapsedMilliseconds = TraceElapsedMilliseconds(),
            configuredWidth,
            browserWidth,
            effectiveWidth,
            drawableWidth
        });
    }

    internal static void RecordErbOutput(
        ScriptPosition? position,
        string instruction,
        string text,
        bool waitForInput,
        int scriptLineCount = 0,
        string functionName = null,
        int functionDepth = 0)
    {
        Current?.RecordErbOutputCore(
            position,
            instruction,
            text,
            waitForInput,
            scriptLineCount,
            functionName,
            functionDepth);
    }

    internal static void RecordErbWait(
        ScriptPosition? position,
        ConsoleInputType inputType,
        bool stopMessageSkip,
        bool actualWait = true,
        int scriptLineCount = 0,
        string functionName = null,
        int functionDepth = 0)
    {
        Current?.RecordErbWaitCore(
            position,
            inputType,
            stopMessageSkip,
            actualWait,
            scriptLineCount,
            functionName,
            functionDepth);
    }

    internal void RecordTransaction(SequencedConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        foreach (ConsoleOperation operation in transaction.Transaction.Operations)
        {
            Write(new
            {
                eventType = "console_operation",
                traceId,
                traceElapsedMilliseconds = TraceElapsedMilliseconds(),
                sequence = transaction.Sequence,
                operation = operation.Kind.ToString(),
                detail = Describe(operation)
            });
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            if (ReferenceEquals(Current, this))
                Current = null;
            writer.Dispose();
        }
    }

    private void Write<T>(T payload)
    {
        lock (sync)
        {
            if (!disposed)
                writer.WriteLine(JsonSerializer.Serialize(payload));
        }
    }

    private void RecordInputConsumedCore(
        GameConsoleInput input,
        int scriptLineCount,
        string functionName,
        int functionDepth)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (sync)
        {
            if (disposed)
                return;

            long now = Stopwatch.GetTimestamp();
            turnId = checked(turnId + 1);
            inputConsumedTimestamp = now;
            inputScriptLineCount = scriptLineCount;
            // Calls which opened the prompt remain on the interpreter stack
            // while adapter.Read waits. Keep those frames, but restart their
            // stopwatch at the input boundary so their inclusive timing does
            // not contain the player's think time.
            foreach (List<ActiveFunctionTiming> timings in activeFunctions.Values)
            {
                foreach (ActiveFunctionTiming timing in timings)
                    timing.Restart(now, turnId, waitGeneration);
            }
            functionAggregates.Clear();
            stageAggregates.Clear();
            scriptHotspots.Clear();
            lastScriptSampleTimestamp = now;
            lastScriptSamplePosition = null;
            lastScriptSampleFunctionName = null;
            WriteLocked(new
            {
                eventType = "timing_input_consumed",
                traceId,
                traceElapsedMilliseconds = TraceElapsedMilliseconds(now),
                turnId,
                waitMilliseconds = waitStartedTimestamp is long waitStart
                    ? ElapsedMilliseconds(waitStart, now)
                    : (double?)null,
                inputType = input.InputType.ToString(),
                valueLength = input.Value.Length,
                isDefaultValue = input.IsDefaultValue,
                skipMessage = input.SkipMessage,
                scriptLineCount,
                functionName,
                functionDepth
            });
            waitStartedTimestamp = null;
        }
    }

    private void RecordErbOutputCore(
        ScriptPosition? position,
        string instruction,
        string text,
        bool waitForInput,
        int scriptLineCount,
        string functionName,
        int functionDepth)
    {
        lock (sync)
        {
            if (disposed)
                return;

            long now = Stopwatch.GetTimestamp();
            WriteLocked(new
            {
                eventType = "erb_output",
                traceId,
                traceElapsedMilliseconds = TraceElapsedMilliseconds(now),
                sinceInputMilliseconds = inputConsumedTimestamp is long inputStart
                    ? ElapsedMilliseconds(inputStart, now)
                    : (double?)null,
                sourceFile = position?.Filename,
                sourceLine = position?.LineNo,
                instruction,
                waitForInput,
                scriptLineCount,
                scriptLineCountSinceInput = inputConsumedTimestamp is not null
                    ? scriptLineCount - inputScriptLineCount
                    : (int?)null,
                functionName,
                functionDepth,
                text = Truncate(text)
            });
        }
    }

    private void RecordErbWaitCore(
        ScriptPosition? position,
        ConsoleInputType inputType,
        bool stopMessageSkip,
        bool actualWait,
        int scriptLineCount,
        string functionName,
        int functionDepth)
    {
        lock (sync)
        {
            if (disposed)
                return;

            long now = Stopwatch.GetTimestamp();
            if (actualWait)
            {
                RecordScriptSampleLocked(position, functionName, now);
                WriteTimingSummaryLocked(now, scriptLineCount);
                waitGeneration = checked(waitGeneration + 1);
                waitStartedTimestamp = now;
            }

            WriteLocked(new
            {
                eventType = "erb_wait",
                traceId,
                traceElapsedMilliseconds = TraceElapsedMilliseconds(now),
                sinceInputMilliseconds = inputConsumedTimestamp is long inputStart
                    ? ElapsedMilliseconds(inputStart, now)
                    : (double?)null,
                sourceFile = position?.Filename,
                sourceLine = position?.LineNo,
                inputType = inputType.ToString(),
                stopMessageSkip,
                actualWait,
                scriptLineCount,
                scriptLineCountSinceInput = inputConsumedTimestamp is not null
                    ? scriptLineCount - inputScriptLineCount
                    : (int?)null,
                functionName,
                functionDepth
            });
        }
    }

    private void FunctionEnter(CalledFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        lock (sync)
        {
            if (disposed || inputConsumedTimestamp is null)
                return;

            if (!activeFunctions.TryGetValue(function, out List<ActiveFunctionTiming> timings))
            {
                timings = [];
                activeFunctions.Add(function, timings);
            }

            timings.Add(new ActiveFunctionTiming(
                function.FunctionName,
                function.TopLabel?.Position,
                Stopwatch.GetTimestamp(),
                turnId,
                waitGeneration));
        }
    }

    private void FunctionExit(CalledFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        lock (sync)
        {
            if (disposed || !activeFunctions.TryGetValue(function, out List<ActiveFunctionTiming> timings) || timings.Count == 0)
                return;

            ActiveFunctionTiming timing = timings[^1];
            timings.RemoveAt(timings.Count - 1);
            if (timings.Count == 0)
                activeFunctions.Remove(function);

            // Frames are restarted at each input boundary, so a function that
            // crosses an input wait contributes only its post-input segment.
            if (timing.TurnId != turnId || timing.WaitGeneration != waitGeneration)
                return;

            AddAggregate(
                functionAggregates,
                FunctionKey(timing.FunctionName, timing.Position),
                timing.FunctionName,
                timing.Position,
                Stopwatch.GetElapsedTime(timing.StartTimestamp),
                units: 1,
                detail: null);
        }
    }

    private void FunctionsCleared()
    {
        lock (sync)
        {
            activeFunctions.Clear();
            functionAggregates.Clear();
            scriptHotspots.Clear();
            lastScriptSampleTimestamp = null;
            lastScriptSamplePosition = null;
            lastScriptSampleFunctionName = null;
        }
    }

    private void RecordScriptSampleCore(
        ScriptPosition? position,
        string functionName)
    {
        lock (sync)
        {
            if (disposed || inputConsumedTimestamp is null)
                return;

            RecordScriptSampleLocked(position, functionName, Stopwatch.GetTimestamp());
        }
    }

    private void RecordScriptSampleLocked(
        ScriptPosition? position,
        string functionName,
        long now)
    {
        if (lastScriptSampleTimestamp is long start && lastScriptSamplePosition is ScriptPosition previousPosition)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start, now);
            string key = string.Concat(
                previousPosition.Filename,
                "|",
                previousPosition.LineNo,
                "|",
                lastScriptSampleFunctionName);
            if (!scriptHotspots.TryGetValue(key, out ScriptHotspotAggregate aggregate))
            {
                aggregate = new ScriptHotspotAggregate(
                    previousPosition.Filename,
                    previousPosition.LineNo,
                    lastScriptSampleFunctionName);
                scriptHotspots.Add(key, aggregate);
            }

            aggregate.SampleCount = checked(aggregate.SampleCount + 1);
            aggregate.TotalTicks = checked(aggregate.TotalTicks + elapsed.Ticks);
            aggregate.MaximumTicks = Math.Max(aggregate.MaximumTicks, elapsed.Ticks);
        }

        lastScriptSampleTimestamp = now;
        lastScriptSamplePosition = position;
        lastScriptSampleFunctionName = functionName;
    }

    private void Stage(string stage, TimeSpan elapsed, long units, string detail)
    {
        if (string.IsNullOrWhiteSpace(stage) || elapsed < TimeSpan.Zero || units < 0)
            return;

        lock (sync)
        {
            if (disposed || inputConsumedTimestamp is null)
                return;
            AddAggregate(stageAggregates, stage, stage, position: null, elapsed, units, detail);
        }
    }

    private void WriteTimingSummaryLocked(long now, int scriptLineCount)
    {
        if (inputConsumedTimestamp is not long inputStart)
            return;

        WriteLocked(new
        {
            eventType = "runtime_timing_summary",
            traceId,
            traceElapsedMilliseconds = TraceElapsedMilliseconds(now),
            turnId,
            scriptElapsedMilliseconds = ElapsedMilliseconds(inputStart, now),
            scriptLineCount,
            scriptLineCountDelta = scriptLineCount - inputScriptLineCount,
            functionTimings = functionAggregates.Values
                .Where(value => IsInterestingFunction(value.Name) ||
                    value.TotalTicks >= TimeSpan.FromMilliseconds(0.5).Ticks)
                .OrderByDescending(value => value.TotalTicks)
                .Take(100)
                .Select(value => new
                {
                    name = value.Name,
                    sourceFile = value.Position?.Filename,
                    sourceLine = value.Position?.LineNo,
                    count = value.Count,
                    totalMilliseconds = Milliseconds(value.TotalTicks),
                    maxMilliseconds = Milliseconds(value.MaximumTicks)
                })
                .ToArray(),
            scriptHotspots = scriptHotspots.Values
                .OrderByDescending(value => value.TotalTicks)
                .Take(20)
                .Select(value => new
                {
                    sourceFile = value.SourceFile,
                    sourceLine = value.SourceLine,
                    functionName = value.FunctionName,
                    sampleCount = value.SampleCount,
                    totalMilliseconds = Milliseconds(value.TotalTicks),
                    maxMilliseconds = Milliseconds(value.MaximumTicks)
                })
                .ToArray(),
            stageTimings = stageAggregates.Values
                .OrderByDescending(value => value.TotalTicks)
                .Select(value => new
                {
                    name = value.Name,
                    count = value.Count,
                    units = value.Units,
                    totalMilliseconds = Milliseconds(value.TotalTicks),
                    maxMilliseconds = Milliseconds(value.MaximumTicks),
                    slowestDetail = value.SlowestDetail
                })
                .ToArray()
        });
        functionAggregates.Clear();
        stageAggregates.Clear();
        scriptHotspots.Clear();
        lastScriptSampleTimestamp = null;
        lastScriptSamplePosition = null;
        lastScriptSampleFunctionName = null;
    }

    private void WriteLocked<T>(T payload)
    {
        if (!disposed)
            writer.WriteLine(JsonSerializer.Serialize(payload));
    }

    private static void AddAggregate(
        Dictionary<string, TimingAggregate> aggregates,
        string key,
        string name,
        ScriptPosition? position,
        TimeSpan elapsed,
        long units,
        string detail)
    {
        if (!aggregates.TryGetValue(key, out TimingAggregate aggregate))
        {
            aggregate = new TimingAggregate(name, position);
            aggregates.Add(key, aggregate);
        }

        long ticks = elapsed.Ticks;
        aggregate.Count = checked(aggregate.Count + 1);
        aggregate.Units = checked(aggregate.Units + units);
        aggregate.TotalTicks = checked(aggregate.TotalTicks + ticks);
        if (ticks >= aggregate.MaximumTicks)
        {
            aggregate.MaximumTicks = ticks;
            aggregate.SlowestDetail = detail;
        }
    }

    private double TraceElapsedMilliseconds() => TraceElapsedMilliseconds(Stopwatch.GetTimestamp());

    private double TraceElapsedMilliseconds(long timestamp) =>
        ElapsedMilliseconds(traceStartedTimestamp, timestamp);

    private static double ElapsedMilliseconds(long start, long end) =>
        Math.Round(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, 3);

    private static double Milliseconds(long ticks) =>
        Math.Round(TimeSpan.FromTicks(ticks).TotalMilliseconds, 3);

    private static double Milliseconds(TimeSpan value) =>
        Math.Round(value.TotalMilliseconds, 3);

    private static string FunctionKey(string name, ScriptPosition? position) =>
        $"{name}|{position?.Filename}|{position?.LineNo}";

    private static bool IsInterestingFunction(string name) =>
        !string.IsNullOrEmpty(name) &&
        (name.StartsWith("BEFORE_AUTOCOM", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("TURN_RESET", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("CHARA_MOVEMENT", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("COMF400", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("EVENTCOMEND", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("SHOW_STATUS", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("SHOW_USERCOM", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("TARGET_SET", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("RUN_SCHEDULE", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("SCHEDULE_", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("DRAW_MAP", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("SORT_CHARALIST", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("画像描画終了", StringComparison.Ordinal));

    private sealed class ActiveFunctionTiming(
        string functionName,
        ScriptPosition? position,
        long startTimestamp,
        long turnId,
        long waitGeneration)
    {
        public string FunctionName { get; } = functionName;
        public ScriptPosition? Position { get; } = position;
        public long StartTimestamp { get; private set; } = startTimestamp;
        public long TurnId { get; private set; } = turnId;
        public long WaitGeneration { get; private set; } = waitGeneration;

        public void Restart(long startTimestamp, long turnId, long waitGeneration)
        {
            StartTimestamp = startTimestamp;
            TurnId = turnId;
            WaitGeneration = waitGeneration;
        }
    }

    private sealed class TimingAggregate(string name, ScriptPosition? position)
    {
        public string Name { get; } = name;
        public ScriptPosition? Position { get; } = position;
        public long Count { get; set; }
        public long Units { get; set; }
        public long TotalTicks { get; set; }
        public long MaximumTicks { get; set; }
        public string SlowestDetail { get; set; }
    }

    private sealed class ScriptHotspotAggregate(
        string sourceFile,
        int? sourceLine,
        string functionName)
    {
        public string SourceFile { get; } = sourceFile;
        public int? SourceLine { get; } = sourceLine;
        public string FunctionName { get; } = functionName;
        public long SampleCount { get; set; }
        public long TotalTicks { get; set; }
        public long MaximumTicks { get; set; }
    }

    private static object Describe(ConsoleOperation operation) => operation switch
    {
        AppendNodesOperation value => new { nodes = DescribeNodes(value.Nodes) },
        AppendLineOperation value => new { lineId = value.Line.LineId, nodes = DescribeNodes(value.Line.Nodes) },
        AppendInlineOperation value => new { lineId = value.LineId, nodes = DescribeNodes(value.Nodes) },
        ReplaceLineOperation value => new { lineId = value.Line.LineId, nodes = DescribeNodes(value.Line.Nodes) },
        OpenPromptOperation value => new { promptId = value.Prompt.PromptId, inputType = value.Prompt.InputType.ToString(), stopMessageSkip = value.Prompt.StopMessageSkip },
        ClosePromptOperation value => new { promptId = value.PromptId, reason = value.Reason.ToString() },
        DeleteLinesOperation value => new { lineIds = value.LineIds },
        _ => new { }
    };

    private static IReadOnlyList<object> DescribeNodes(IReadOnlyList<ConsoleNode> nodes)
    {
        var result = new List<object>(nodes.Count);
        foreach (ConsoleNode node in nodes)
        {
            result.Add(node switch
            {
                TextNode value => new { kind = node.Kind.ToString(), text = Truncate(value.Text) },
                ButtonNode value => new { kind = node.Kind.ToString(), value = Truncate(value.Value), children = DescribeNodes(value.Children) },
                _ => new { kind = node.Kind.ToString() }
            });
        }
        return result;
    }

    private static string Truncate(string value) => value.Length <= MaxTextLength ? value : value[..MaxTextLength] + "...<truncated>";
}
