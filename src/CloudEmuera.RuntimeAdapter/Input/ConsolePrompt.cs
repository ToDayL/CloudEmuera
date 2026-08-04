namespace CloudEmuera.RuntimeAdapter;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1720", Justification = "Integer is part of the stable runtime input vocabulary.")]
public enum ConsoleInputType
{
    Text,
    Integer
}

public enum ConsolePromptTimeoutBehavior
{
    Cancel,
    ReturnDefaultValue
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
        ConsolePromptTimeoutBehavior timeoutBehavior = ConsolePromptTimeoutBehavior.Cancel)
    {
        ValidateInputType(inputType);
        ValidateTimeout(timeout);
        PromptId = promptId ?? string.Empty;
        InputType = inputType;
        PromptText = promptText;
        DefaultValue = defaultValue;
        Constraints = constraints ?? CreateDefaultConstraints(inputType);
        Timeout = timeout;
        TimeoutBehavior = timeoutBehavior;
    }

    public ConsolePrompt(
        ConsoleInputType inputType,
        string? promptText = null,
        string? defaultValue = null,
        ConsoleInputConstraints? constraints = null,
        TimeSpan? timeout = null,
        ConsolePromptTimeoutBehavior timeoutBehavior = ConsolePromptTimeoutBehavior.Cancel)
        : this(string.Empty, inputType, promptText, defaultValue, constraints, timeout, timeoutBehavior)
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

    public ConsolePrompt WithPromptId(string promptId) =>
        new(promptId, InputType, PromptText, DefaultValue, Constraints, Timeout, TimeoutBehavior);

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
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Unknown prompt timeout behavior.");
        }

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

        Constraints.Validate(limits);
        if (DefaultValue is not null && !Constraints.TryValidate(DefaultValue, limits, out _))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "The prompt default value does not satisfy its constraints.");
        }

        if (TimeoutBehavior == ConsolePromptTimeoutBehavior.ReturnDefaultValue && DefaultValue is null)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "A timeout default behavior requires a default value.");
        }
    }

    private static ConsoleInputConstraints CreateDefaultConstraints(ConsoleInputType inputType) =>
        inputType switch
        {
            ConsoleInputType.Text => new TextInputConstraints(),
            ConsoleInputType.Integer => new IntegerInputConstraints(),
            _ => throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Unknown input type.")
        };

    private static void ValidateInputType(ConsoleInputType inputType)
    {
        if (inputType is not ConsoleInputType.Text and not ConsoleInputType.Integer)
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
