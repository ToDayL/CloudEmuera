namespace CloudEmuera.RuntimeAdapter;

internal readonly record struct ConsoleNodeMetrics(int NodeCount, long TextLength, long EstimatedBytes)
{
    public static ConsoleNodeMetrics operator +(ConsoleNodeMetrics left, ConsoleNodeMetrics right) =>
        new(
            checked(left.NodeCount + right.NodeCount),
            checked(left.TextLength + right.TextLength),
            checked(left.EstimatedBytes + right.EstimatedBytes));

    public static ConsoleNodeMetrics operator -(ConsoleNodeMetrics left, ConsoleNodeMetrics right) =>
        new(
            left.NodeCount - right.NodeCount,
            left.TextLength - right.TextLength,
            left.EstimatedBytes - right.EstimatedBytes);
}

internal static class ConsoleSizeEstimator
{
    private const int SnapshotOverhead = 128;

    public static ConsoleNodeMetrics MeasureNodes(IEnumerable<ConsoleNode> nodes)
    {
        ConsoleNodeMetrics metrics = default;
        foreach (ConsoleNode node in nodes)
        {
            metrics += MeasureNode(node);
        }

        return metrics;
    }

    public static ConsoleNodeMetrics MeasureNode(ConsoleNode node) =>
        node switch
        {
            TextNode text => new(
                NodeCount: 1,
                TextLength: text.Text.Length,
                EstimatedBytes: checked(48L + text.Text.Length * 2L + MeasureStyle(text.Style))),
            LineBreakNode => new(1, 0, 16),
            ButtonNode button => MeasureButton(button),
            ImageNode image => MeasureImage(image),
            _ => throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "Unknown console node type.")
        };

    public static long MeasurePrompt(ConsolePrompt? prompt)
    {
        if (prompt is null)
        {
            return 0;
        }

        long result = 96L + prompt.PromptId.Length * 2L;
        result = checked(result + (prompt.PromptText?.Length ?? 0) * 2L);
        result = checked(result + (prompt.DefaultValue?.Length ?? 0) * 2L);
        return prompt.Constraints switch
        {
            TextInputConstraints text => checked(result + 16L),
            IntegerInputConstraints integer => checked(result + 32L + (integer.Minimum is null ? 0 : 8) + (integer.Maximum is null ? 0 : 8)),
            _ => checked(result + 16L)
        };
    }

    public static long MeasureOperation(ConsoleOperation operation) =>
        operation switch
        {
            AppendNodesOperation append => checked(64L + MeasureNodes(append.Nodes).EstimatedBytes),
            ClearConsoleOperation => 32L,
            OpenPromptOperation open => checked(64L + MeasurePrompt(open.Prompt)),
            ClosePromptOperation close => checked(48L + close.PromptId.Length * 2L),
            _ => throw new ConsoleContractException(ConsoleContractViolationReason.InvalidNodeType, "Unknown console operation type.")
        };

    public static long MeasureSnapshot(ConsoleNodeMetrics visible, ConsolePrompt? prompt) =>
        checked(SnapshotOverhead + visible.EstimatedBytes + MeasurePrompt(prompt));

    private static ConsoleNodeMetrics MeasureButton(ButtonNode button)
    {
        ConsoleNodeMetrics result = new(
            NodeCount: 1,
            TextLength: button.Value.Length + (button.Tooltip?.Length ?? 0),
            EstimatedBytes: checked(80L + button.Value.Length * 2L + (button.Tooltip?.Length ?? 0) * 2L));
        foreach (ConsoleNode child in button.Children)
        {
            result += MeasureNode(child);
        }

        return result;
    }

    private static ConsoleNodeMetrics MeasureImage(ImageNode image) => new(
        NodeCount: 1,
        TextLength: image.AltText?.Length ?? 0,
        EstimatedBytes: checked(72L + image.AssetId.Value.Length * 2L + (image.AltText?.Length ?? 0) * 2L));

    private static long MeasureStyle(ConsoleTextStyle style) =>
        checked(16L + (style.Foreground is null ? 0 : 4) + (style.Background is null ? 0 : 4));
}
