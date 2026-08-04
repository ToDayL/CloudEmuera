namespace CloudEmuera.RuntimeAdapter;

public sealed class ConsolePromptTimeoutException : TimeoutException
{
    public ConsolePromptTimeoutException(string promptId)
        : base("The console prompt timed out.")
    {
        PromptId = promptId;
    }

    public string PromptId { get; }
}

public sealed class ConsolePromptCancelledException : OperationCanceledException
{
    public ConsolePromptCancelledException(string promptId, CancellationToken cancellationToken = default)
        : base("The console prompt was cancelled.", cancellationToken)
    {
        PromptId = promptId;
    }

    public string PromptId { get; }
}
