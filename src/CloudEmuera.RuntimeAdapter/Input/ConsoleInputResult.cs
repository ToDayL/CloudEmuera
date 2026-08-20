namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleInputResultKind
{
    Accepted,
    Duplicate,
    InvalidFormat,
    NoActivePrompt,
    MessageConflict,
    InvalidCommand,
    Cancelled,
    TimedOut
}

public sealed record ConsoleInputResult
{
    public ConsoleInputResult(
        ConsoleInputResultKind kind,
        string? resolvedPromptId,
        string? clientMessageId,
        GameConsoleInput? input = null,
        ConsoleInputResult? originalResult = null,
        ConsoleInputFailureReason failureReason = ConsoleInputFailureReason.None)
    {
        if (resolvedPromptId is not null)
        {
            ConsoleContractValidation.ValidateIdentifier(
                resolvedPromptId,
                nameof(resolvedPromptId),
                ConsoleContractLimits.Default.MaxPromptIdLength);
        }

        if (clientMessageId is not null && clientMessageId.Length > ConsoleContractLimits.Default.MaxClientMessageIdLength)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidIdentifier, "The client message id is too long.");
        }

        Kind = kind;
        ResolvedPromptId = resolvedPromptId;
        ClientMessageId = clientMessageId;
        Input = input;
        OriginalResult = originalResult;
        FailureReason = failureReason;
    }

    public ConsoleInputResultKind Kind { get; }

    public ConsoleInputResultKind Status => Kind;

    /// <summary>The prompt actually inspected by the current-slot submission, if any.</summary>
    public string? ResolvedPromptId { get; }

    public string? ClientMessageId { get; }

    public GameConsoleInput? Input { get; }

    public string? Value => Input?.Value;

    public ConsoleInputResult? OriginalResult { get; }

    public ConsoleInputFailureReason FailureReason { get; }

    public string? ReasonCode => FailureReason == ConsoleInputFailureReason.None ? null : FailureReason.ToString();

    public bool IsAccepted => Kind == ConsoleInputResultKind.Accepted;

    public static ConsoleInputResult Accepted(ConsoleInputAttempt attempt, GameConsoleInput input) =>
        new(ConsoleInputResultKind.Accepted, input.PromptId, attempt.ClientMessageId, input);

    public static ConsoleInputResult Duplicate(ConsoleInputAttempt attempt, ConsoleInputResult original) =>
        new(ConsoleInputResultKind.Duplicate, original.ResolvedPromptId, attempt.ClientMessageId, original.Input, original, original.FailureReason);

    public static ConsoleInputResult Conflict(ConsoleInputAttempt attempt) =>
        new(ConsoleInputResultKind.MessageConflict, null, attempt.ClientMessageId);

    public static ConsoleInputResult InvalidFormat(ConsoleInputAttempt attempt, string resolvedPromptId, ConsoleInputFailureReason reason) =>
        new(ConsoleInputResultKind.InvalidFormat, resolvedPromptId, attempt.ClientMessageId, failureReason: reason);

    public static ConsoleInputResult NoActive(ConsoleInputAttempt attempt) =>
        new(ConsoleInputResultKind.NoActivePrompt, null, attempt.ClientMessageId);

    public static ConsoleInputResult InvalidCommand(ConsoleInputAttempt attempt, ConsoleInputFailureReason reason) =>
        new(ConsoleInputResultKind.InvalidCommand, null, attempt.ClientMessageId, failureReason: reason);

    internal static ConsoleInputResult Cancelled(ConsolePrompt prompt) =>
        new(ConsoleInputResultKind.Cancelled, prompt.PromptId, clientMessageId: null);

    internal static ConsoleInputResult TimedOut(ConsolePrompt prompt, GameConsoleInput? defaultInput = null) =>
        new(ConsoleInputResultKind.TimedOut, prompt.PromptId, clientMessageId: null, input: defaultInput);
}
