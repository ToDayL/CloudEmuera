// CloudEmuera opt-in runtime trace. It stays outside the game root so ERB
// file enumeration and native save behavior are unaffected.
using System;
using System.Collections.Generic;
using System.Text.Json;
using CloudEmuera.RuntimeAdapter;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

internal sealed class RuntimeDebugTrace : IDisposable
{
    internal const string EnvironmentVariable = "CLOUDEMUERA_RUNTIME_DEBUG_TRACE";
    private const int MaxTextLength = 4_096;
    private readonly object sync = new();
    private readonly System.IO.StreamWriter writer;
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

    internal void RecordRuntimeWidth(int configuredWidth, int browserWidth, int effectiveWidth, int drawableWidth)
    {
        Write(new
        {
            eventType = "runtime_width",
            configuredWidth,
            browserWidth,
            effectiveWidth,
            drawableWidth
        });
    }

    internal static void RecordErbOutput(ScriptPosition? position, string instruction, string text, bool waitForInput)
    {
        Current?.Write(new
        {
            eventType = "erb_output",
            sourceFile = position?.Filename,
            sourceLine = position?.LineNo,
            instruction,
            waitForInput,
            text = Truncate(text)
        });
    }

    internal static void RecordErbWait(ScriptPosition? position, ConsoleInputType inputType, bool stopMessageSkip)
    {
        Current?.Write(new
        {
            eventType = "erb_wait",
            sourceFile = position?.Filename,
            sourceLine = position?.LineNo,
            inputType = inputType.ToString(),
            stopMessageSkip
        });
    }

    internal void RecordTransaction(SequencedConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        foreach (ConsoleOperation operation in transaction.Transaction.Operations)
        {
            Write(new
            {
                eventType = "console_operation",
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
