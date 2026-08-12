namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Typed input returned to the fixed upstream runtime. Value is kept in its
/// original textual form so adapters can apply the upstream's exact parsing
/// rules without losing what the client submitted.
/// </summary>
public sealed record GameConsoleInput
{
    public GameConsoleInput(
        string promptId,
        ConsoleInputType inputType,
        string value,
        bool isDefaultValue = false)
    {
        ConsoleContractValidation.ValidateIdentifier(
            promptId,
            nameof(promptId),
            ConsoleContractLimits.Default.MaxPromptIdLength);
        ArgumentNullException.ThrowIfNull(value);
        if (inputType is not ConsoleInputType.EnterKey and not ConsoleInputType.AnyKey and not ConsoleInputType.Integer and
            not ConsoleInputType.Text and not ConsoleInputType.AnyValue and not ConsoleInputType.IntegerButton and
            not ConsoleInputType.TextButton and not ConsoleInputType.PrimitivePointerKey and not ConsoleInputType.WaitOnly)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Unknown input type.", nameof(inputType));
        }

        PromptId = promptId;
        InputType = inputType;
        Value = value;
        IsDefaultValue = isDefaultValue;
    }

    public string PromptId { get; }

    public ConsoleInputType InputType { get; }

    public string Value { get; }

    public string RawValue => Value;

    public bool IsDefaultValue { get; }
}

/// <summary>
/// Runtime console boundary. It has no dependency on a window, control, font,
/// color or desktop event loop.
/// </summary>
public interface IGameConsole
{
    void Emit(ConsoleOperation operation);

    GameConsoleInput Read(ConsolePrompt prompt, CancellationToken cancellationToken = default);
}
