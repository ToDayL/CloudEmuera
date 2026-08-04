namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleOperationKind
{
    AppendNodes,
    ClearConsole,
    OpenPrompt,
    ClosePrompt
}

public abstract class ConsoleOperation
{
    private protected ConsoleOperation()
    {
    }

    public abstract ConsoleOperationKind Kind { get; }

    public static AppendNodesOperation Append(IEnumerable<ConsoleNode> nodes) => new(nodes);

    public static AppendNodesOperation AppendNodes(IEnumerable<ConsoleNode> nodes) => new(nodes);

    public static ClearConsoleOperation Clear() => new();

    public static ClearConsoleOperation ClearConsole() => new();

    public static OpenPromptOperation Open(ConsolePrompt prompt) => new(prompt);

    public static ClosePromptOperation Close(
        string promptId,
        ConsolePromptCloseReason reason = ConsolePromptCloseReason.Completed) =>
        new(promptId, reason);
}

public sealed class AppendNodesOperation : ConsoleOperation
{
    public AppendNodesOperation(IEnumerable<ConsoleNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ConsoleNode[] copy = nodes.ToArray();
        ConsoleNodeValidation.ValidateBatch(copy, ConsoleContractLimits.Default);
        Nodes = Array.AsReadOnly(copy);
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.AppendNodes;

    public IReadOnlyList<ConsoleNode> Nodes { get; }
}

public sealed class ClearConsoleOperation : ConsoleOperation
{
    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClearConsole;
}

public enum ConsolePromptCloseReason
{
    Completed,
    InputAccepted,
    Cancelled,
    TimedOut,
    Explicit
}

public sealed class OpenPromptOperation : ConsoleOperation
{
    public OpenPromptOperation(ConsolePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.HasPromptId)
        {
            prompt.Validate(ConsoleContractLimits.Default);
        }
        else
        {
            prompt.ValidateTemplate(ConsoleContractLimits.Default);
        }

        Prompt = prompt;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.OpenPrompt;

    public ConsolePrompt Prompt { get; }
}

public sealed class ClosePromptOperation : ConsoleOperation
{
    public ClosePromptOperation(
        string promptId,
        ConsolePromptCloseReason reason = ConsolePromptCloseReason.Completed)
    {
        ConsoleContractValidation.ValidateIdentifier(
            promptId,
            nameof(promptId),
            ConsoleContractLimits.Default.MaxPromptIdLength);
        PromptId = promptId;
        Reason = reason;
    }

    public override ConsoleOperationKind Kind => ConsoleOperationKind.ClosePrompt;

    public string PromptId { get; }

    public ConsolePromptCloseReason Reason { get; }
}
