namespace CloudEmuera.RuntimeAdapter;

public enum ConsoleInputResultKind
{
    Accepted,
    Duplicate,
    StalePrompt,
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
        string promptId,
        string? clientMessageId,
        GameConsoleInput? input = null,
        ConsoleInputResult? originalResult = null,
        ConsoleInputFailureReason failureReason = ConsoleInputFailureReason.None)
    {
        ArgumentNullException.ThrowIfNull(promptId);
        if (promptId.Length > ConsoleContractLimits.Default.MaxPromptIdLength)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "The prompt id is too long.");
        }

        if (clientMessageId is not null && clientMessageId.Length > ConsoleContractLimits.Default.MaxClientMessageIdLength)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidIdentifier, "The client message id is too long.");
        }

        Kind = kind;
        PromptId = promptId;
        ClientMessageId = clientMessageId;
        Input = input;
        OriginalResult = originalResult;
        FailureReason = failureReason;
    }

    public ConsoleInputResultKind Kind { get; }

    public ConsoleInputResultKind Status => Kind;

    public string PromptId { get; }

    public string? ClientMessageId { get; }

    public GameConsoleInput? Input { get; }

    public string? Value => Input?.Value;

    public ConsoleInputResult? OriginalResult { get; }

    public ConsoleInputFailureReason FailureReason { get; }

    public string? ReasonCode => FailureReason == ConsoleInputFailureReason.None ? null : FailureReason.ToString();

    public bool IsAccepted => Kind == ConsoleInputResultKind.Accepted;

    public static ConsoleInputResult Accepted(ConsoleInputCommand command, GameConsoleInput input) =>
        new(ConsoleInputResultKind.Accepted, command.PromptId, command.ClientMessageId, input);

    public static ConsoleInputResult Duplicate(ConsoleInputCommand command, ConsoleInputResult original) =>
        new(ConsoleInputResultKind.Duplicate, command.PromptId, command.ClientMessageId, original.Input, original, original.FailureReason);

    public static ConsoleInputResult Conflict(ConsoleInputCommand command) =>
        new(ConsoleInputResultKind.MessageConflict, command.PromptId, command.ClientMessageId);

    public static ConsoleInputResult InvalidFormat(ConsoleInputCommand command, ConsoleInputFailureReason reason) =>
        new(ConsoleInputResultKind.InvalidFormat, command.PromptId, command.ClientMessageId, failureReason: reason);

    public static ConsoleInputResult Stale(ConsoleInputCommand command) =>
        new(ConsoleInputResultKind.StalePrompt, command.PromptId, command.ClientMessageId);

    public static ConsoleInputResult NoActive(ConsoleInputCommand command) =>
        new(ConsoleInputResultKind.NoActivePrompt, command.PromptId, command.ClientMessageId);

    public static ConsoleInputResult InvalidCommand(ConsoleInputCommand command, ConsoleInputFailureReason reason) =>
        new(ConsoleInputResultKind.InvalidCommand, command.PromptId, command.ClientMessageId, failureReason: reason);

    internal static ConsoleInputResult Cancelled(ConsolePrompt prompt) =>
        new(ConsoleInputResultKind.Cancelled, prompt.PromptId, clientMessageId: null);

    internal static ConsoleInputResult TimedOut(ConsolePrompt prompt, GameConsoleInput? defaultInput = null) =>
        new(ConsoleInputResultKind.TimedOut, prompt.PromptId, clientMessageId: null, input: defaultInput);
}
