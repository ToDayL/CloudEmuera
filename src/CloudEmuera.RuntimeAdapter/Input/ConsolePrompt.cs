namespace CloudEmuera.RuntimeAdapter;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1720", Justification = "Integer is part of the stable runtime input vocabulary.")]
public enum ConsoleInputType
{
    EnterKey,
    AnyKey,
    Integer,
    Text,
    AnyValue,
    IntegerButton,
    TextButton,
    PrimitivePointerKey,
    WaitOnly,
    PrimitiveMouseKey = PrimitivePointerKey
}

public enum ConsolePromptTimeoutBehavior
{
    Cancel,
    ReturnDefaultValue,
    ContinueWithoutValue,
    CancelRuntime = Cancel
}

public enum ConsolePromptTimeoutAction
{
    ReturnDefaultValue,
    ContinueWithoutValue,
    CancelRuntime
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1720", Justification = "Pointer is a stable input-source vocabulary term.")]
[Flags]
public enum ConsoleInputSource
{
    None = 0,
    Keyboard = 1,
    Button = 2,
    Pointer = 4,
    System = 8,
    All = Keyboard | Button | Pointer | System
}

/// <summary>
/// Immutable prompt description. The constructor without an id creates a
/// prompt template; <see cref="StructuredGameConsole.Read"/> assigns the id
/// from its injected generator before opening it.
/// </summary>
public sealed class ConsolePrompt
{
    public ConsolePrompt(
        string promptId,
        ConsoleInputType inputType,
        string? promptText = null,
        string? defaultValue = null,
        ConsoleInputConstraints? constraints = null,
        TimeSpan? timeout = null,
        ConsolePromptTimeoutBehavior timeoutBehavior = ConsolePromptTimeoutBehavior.Cancel,
        bool oneInput = false,
        bool systemInput = false,
        bool stopMessageSkip = false,
        bool displayTime = false,
        string? timeoutMessage = null,
        ConsolePromptTimeoutAction? timeoutAction = null,
        ConsoleInputSource allowedSources = ConsoleInputSource.All,
        long openedAtUnixMilliseconds = 0,
        long deadlineUnixMilliseconds = 0,
        bool allowLongInputByButton = false,
        long buttonGeneration = 0)
    {
        ValidateInputType(inputType);
        ValidateTimeout(timeout);
        if (buttonGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(buttonGeneration), "Button generation cannot be negative.");
        PromptId = promptId ?? string.Empty;
        InputType = inputType;
        PromptText = promptText;
        DefaultValue = defaultValue;
        Constraints = constraints ?? CreateDefaultConstraints(inputType);
        Timeout = timeout;
        TimeoutBehavior = timeoutBehavior;
        OneInput = oneInput;
        SystemInput = systemInput;
        StopMessageSkip = stopMessageSkip;
        DisplayTime = displayTime;
        TimeoutMessage = timeoutMessage;
        TimeoutAction = timeoutAction ?? timeoutBehavior switch
        {
            ConsolePromptTimeoutBehavior.ReturnDefaultValue => ConsolePromptTimeoutAction.ReturnDefaultValue,
            ConsolePromptTimeoutBehavior.ContinueWithoutValue => ConsolePromptTimeoutAction.ContinueWithoutValue,
            _ => ConsolePromptTimeoutAction.CancelRuntime
        };
        AllowedSources = allowedSources;
        OpenedAtUnixMilliseconds = openedAtUnixMilliseconds;
        DeadlineUnixMilliseconds = deadlineUnixMilliseconds;
        AllowLongInputByButton = allowLongInputByButton;
        ButtonGeneration = buttonGeneration;
    }

    public ConsolePrompt(
        ConsoleInputType inputType,
        string? promptText = null,
        string? defaultValue = null,
        ConsoleInputConstraints? constraints = null,
        TimeSpan? timeout = null,
        ConsolePromptTimeoutBehavior timeoutBehavior = ConsolePromptTimeoutBehavior.Cancel,
        bool oneInput = false,
        bool systemInput = false,
        bool stopMessageSkip = false,
        bool displayTime = false,
        string? timeoutMessage = null,
        ConsolePromptTimeoutAction? timeoutAction = null,
        ConsoleInputSource allowedSources = ConsoleInputSource.All,
        long openedAtUnixMilliseconds = 0,
        long deadlineUnixMilliseconds = 0,
        bool allowLongInputByButton = false,
        long buttonGeneration = 0)
        : this(string.Empty, inputType, promptText, defaultValue, constraints, timeout, timeoutBehavior, oneInput, systemInput,
            stopMessageSkip, displayTime, timeoutMessage, timeoutAction, allowedSources, openedAtUnixMilliseconds, deadlineUnixMilliseconds,
            allowLongInputByButton, buttonGeneration)
    {
    }

    public string PromptId { get; }

    public bool HasPromptId => PromptId.Length != 0;

    public ConsoleInputType InputType { get; }

    public string? PromptText { get; }

    public string? Text => PromptText;

    public string? DefaultValue { get; }

    public ConsoleInputConstraints Constraints { get; }

    public TimeSpan? Timeout { get; }

    public ConsolePromptTimeoutBehavior TimeoutBehavior { get; }

    public bool OneInput { get; }

    /// <summary>
    /// Preserves the pinned upstream AllowLongInputByMouse exception for a
    /// semantic game button. Keyboard input remains subject to OneInput's
    /// single-character normalization.
    /// </summary>
    public bool AllowLongInputByButton { get; }

    /// <summary>
    /// Runtime-authoritative generation of the game buttons that may be
    /// activated while this prompt is current. This is display state and is
    /// intentionally not part of an input attempt's identity.
    /// </summary>
    public long ButtonGeneration { get; }

    public bool SystemInput { get; }

    public bool StopMessageSkip { get; }

    public bool DisplayTime { get; }

    public string? TimeoutMessage { get; }

    public string? TimeUpMes => TimeoutMessage;

    public ConsolePromptTimeoutAction TimeoutAction { get; }

    public ConsoleInputSource AllowedSources { get; }

    public long OpenedAtUnixMilliseconds { get; }

    public long DeadlineUnixMilliseconds { get; }

    public bool HasDeadline => DeadlineUnixMilliseconds > 0;

    public ConsolePrompt WithPromptId(string promptId) =>
        new(promptId, InputType, PromptText, DefaultValue, Constraints, Timeout, TimeoutBehavior, OneInput, SystemInput,
            StopMessageSkip, DisplayTime, TimeoutMessage, TimeoutAction, AllowedSources, OpenedAtUnixMilliseconds, DeadlineUnixMilliseconds,
            AllowLongInputByButton, ButtonGeneration);

    public ConsolePrompt WithTiming(DateTimeOffset openedAt, long? deadlineUnixMilliseconds)
    {
        long opened = openedAt.ToUnixTimeMilliseconds();
        long deadline = deadlineUnixMilliseconds ?? 0;
        return new ConsolePrompt(PromptId, InputType, PromptText, DefaultValue, Constraints, Timeout, TimeoutBehavior, OneInput,
            SystemInput, StopMessageSkip, DisplayTime, TimeoutMessage, TimeoutAction, AllowedSources, opened, deadline,
            AllowLongInputByButton, ButtonGeneration);
    }

    internal void Validate(ConsoleContractLimits limits)
    {
        limits.Validate();
        ValidateCommon(limits);
        if (!HasPromptId)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "An opened prompt must have a prompt id.");
        }

        ConsoleContractValidation.ValidateIdentifier(PromptId, nameof(PromptId), limits.MaxPromptIdLength);
    }

    internal void ValidateTemplate(ConsoleContractLimits limits)
    {
        limits.Validate();
        ValidateCommon(limits);
    }

    private void ValidateCommon(ConsoleContractLimits limits)
    {
        if (TimeoutBehavior is not ConsolePromptTimeoutBehavior.Cancel and not ConsolePromptTimeoutBehavior.ReturnDefaultValue)
        {
            if (TimeoutBehavior is not ConsolePromptTimeoutBehavior.ContinueWithoutValue)
                throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Unknown prompt timeout behavior.");
        }

        if (TimeoutAction is not ConsolePromptTimeoutAction.CancelRuntime and not ConsolePromptTimeoutAction.ReturnDefaultValue and
            not ConsolePromptTimeoutAction.ContinueWithoutValue)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Unknown prompt timeout action.");
        }

        const ConsoleInputSource knownSources = ConsoleInputSource.All;
        if ((AllowedSources & ~knownSources) != ConsoleInputSource.None || AllowedSources == ConsoleInputSource.None)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "The prompt input source set is invalid.");

        if (PromptText is not null)
        {
            ConsoleContractValidation.ValidateText(
                PromptText,
                nameof(PromptText),
                limits.MaxPromptTextLength,
                ConsoleContractViolationReason.PromptTextTooLong);
        }

        if (DefaultValue is not null)
        {
            ConsoleContractValidation.ValidateText(
                DefaultValue,
                nameof(DefaultValue),
                limits.MaxPromptDefaultValueLength,
                ConsoleContractViolationReason.PromptDefaultValueTooLong,
                allowControlCharacters: Constraints is TextInputConstraints { AllowControlCharacters: true });
        }

        if (TimeoutMessage is not null)
        {
            ConsoleContractValidation.ValidateText(
                TimeoutMessage,
                nameof(TimeoutMessage),
                limits.MaxPromptTextLength,
                ConsoleContractViolationReason.PromptTextTooLong);
        }

        if (OpenedAtUnixMilliseconds < 0 || DeadlineUnixMilliseconds < 0 ||
            DeadlineUnixMilliseconds > 0 && DeadlineUnixMilliseconds < OpenedAtUnixMilliseconds)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Prompt timing metadata is invalid.");

        Constraints.Validate(limits);
        if (DefaultValue is not null && !Constraints.TryValidate(DefaultValue, limits, out _))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "The prompt default value does not satisfy its constraints.");
        }

        if ((TimeoutBehavior == ConsolePromptTimeoutBehavior.ReturnDefaultValue ||
             TimeoutAction == ConsolePromptTimeoutAction.ReturnDefaultValue) && DefaultValue is null)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "A timeout default behavior requires a default value.");
        }
    }

    private static ConsoleInputConstraints CreateDefaultConstraints(ConsoleInputType inputType) =>
        inputType switch
        {
            ConsoleInputType.Integer or ConsoleInputType.IntegerButton => new IntegerInputConstraints(),
            ConsoleInputType.AnyValue => new AnyValueInputConstraints(),
            _ => new TextInputConstraints(),
        };

    private static void ValidateInputType(ConsoleInputType inputType)
    {
        if (inputType is not ConsoleInputType.EnterKey and not ConsoleInputType.AnyKey and not ConsoleInputType.Integer and
            not ConsoleInputType.Text and not ConsoleInputType.AnyValue and not ConsoleInputType.IntegerButton and
            not ConsoleInputType.TextButton and not ConsoleInputType.PrimitivePointerKey and not ConsoleInputType.WaitOnly)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Unknown input type.", nameof(inputType));
        }
    }

    private static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is TimeSpan value && value < TimeSpan.Zero && value != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Prompt timeout cannot be negative.", nameof(timeout));
        }
    }
}
