// CloudEmuera opt-in runtime trace. It stays outside the game root so ERB
// file enumeration and native save behavior are unaffected.
using System;
using System.Collections.Generic;
using System.Linq;
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

    internal static void RecordHostCompatibility(string capability, string behavior, int value)
    {
        Current?.Write(new
        {
            eventType = "host_compatibility",
            capability,
            behavior,
            value
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

    internal void RecordLayoutDecision(
        string logicalLineId,
        string decision,
        int atomIndex,
        string atomKind,
        string? text,
        int width,
        bool canDivide,
        bool hasAction,
        int? lockedX,
        bool lockedXIsRelative,
        int position,
        int availableWidth,
        int fittingCharacters,
        int currentSegmentCount,
        int currentContentWidth,
        int layoutWidth,
        bool noWrap,
        bool buttonWrap,
        bool truncate)
    {
        Write(new
        {
            eventType = "layout_decision",
            logicalLineId,
            decision,
            atomIndex,
            atomKind,
            text = text is null ? null : Truncate(text),
            width,
            canDivide,
            hasAction,
            lockedX,
            lockedXIsRelative,
            position,
            availableWidth,
            fittingCharacters,
            currentSegmentCount,
            currentContentWidth,
            layoutWidth,
            noWrap,
            buttonWrap,
            truncate
        });
    }

    internal void RecordLayoutResult(
        string logicalLineId,
        int layoutWidth,
        int lineHeight,
        ConsoleLineAlignment alignment,
        bool temporary,
        bool requestedNoWrap,
        bool effectiveNoWrap,
        bool buttonWrap,
        bool truncate,
        int atomCount,
        IReadOnlyList<ConsoleLine> physicalLines)
    {
        ArgumentNullException.ThrowIfNull(physicalLines);
        Write(new
        {
            eventType = "layout_result",
            logicalLineId,
            layoutWidth,
            lineHeight,
            alignment = alignment.ToString(),
            temporary,
            requestedNoWrap,
            effectiveNoWrap,
            noWrap = effectiveNoWrap,
            buttonWrap,
            truncate,
            atomCount,
            physicalLineCount = physicalLines.Count,
            physicalLines = physicalLines.Select(DescribeLineOperation).ToArray()
        });
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
        AppendLineOperation value => DescribeLineOperation(value.Line),
        AppendInlineOperation value => new { lineId = value.LineId, nodes = DescribeNodes(value.Nodes) },
        ReplaceLineOperation value => DescribeLineOperation(value.Line),
        OpenPromptOperation value => new { promptId = value.Prompt.PromptId, inputType = value.Prompt.InputType.ToString(), stopMessageSkip = value.Prompt.StopMessageSkip },
        ClosePromptOperation value => new { promptId = value.PromptId, reason = value.Reason.ToString() },
        DeleteLinesOperation value => new { lineIds = value.LineIds },
        _ => new { }
    };

    private static object DescribeLineOperation(ConsoleLine line) => new
    {
        lineId = line.LineId,
        logicalLineId = line.LogicalLineId,
        physicalIndex = line.PhysicalIndex,
        isLogicalStart = line.IsLogicalStart,
        alignment = line.Alignment.ToString(),
        temporary = line.Temporary,
        noWrap = line.NoWrap,
        layoutWidth = line.LayoutWidth,
        lineHeight = line.LineHeight,
        contentWidth = GetLineContentWidth(line),
        overflow = line.LayoutWidth > 0 && GetLineContentWidth(line) > line.LayoutWidth,
        nodes = DescribeNodes(line.Nodes)
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
                PositionedInlineSegmentNode value => new
                {
                    kind = node.Kind.ToString(),
                    positionX = value.PositionX,
                    measuredWidth = value.MeasuredWidth,
                    endX = checked(value.PositionX + value.MeasuredWidth),
                    hasAction = value.Action is not null,
                    children = DescribeNodes(value.Children)
                },
                _ => new { kind = node.Kind.ToString() }
            });
        }
        return result;
    }

    private static int GetLineContentWidth(ConsoleLine line)
    {
        int contentWidth = 0;
        foreach (PositionedInlineSegmentNode segment in line.Nodes.OfType<PositionedInlineSegmentNode>())
            contentWidth = Math.Max(contentWidth, checked(segment.PositionX + segment.MeasuredWidth));
        return contentWidth;
    }

    private static string Truncate(string value) => value.Length <= MaxTextLength ? value : value[..MaxTextLength] + "...<truncated>";
}
