namespace CloudEmuera.RuntimeAdapter;

/// <summary>Client input scoped to exactly one prompt.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1720", Justification = "Pointer payload is part of the stable structured input contract.")]
public sealed class ConsoleInputCommand
{
    public ConsoleInputCommand(string promptId, string clientMessageId, string value)
        : this(promptId, clientMessageId, value, ConsoleInputSource.Keyboard)
    {
    }

    public ConsoleInputCommand(
        string promptId,
        string clientMessageId,
        string value,
        ConsoleInputSource source,
        ConsolePointerPayload? pointer = null,
        ConsoleKeyPayload? key = null)
    {
        ConsoleContractValidation.ValidateIdentifier(
            promptId,
            nameof(promptId),
            ConsoleContractLimits.Default.MaxPromptIdLength);
        ConsoleContractValidation.ValidateIdentifier(
            clientMessageId,
            nameof(clientMessageId),
            ConsoleContractLimits.Default.MaxClientMessageIdLength);
        ConsoleContractValidation.ValidateText(
            value,
            nameof(value),
            ConsoleContractLimits.Default.MaxInputValueLength,
            ConsoleContractViolationReason.InputValueTooLong,
            allowControlCharacters: true);

        PromptId = promptId;
        ClientMessageId = clientMessageId;
        Value = value;
        if (source is < ConsoleInputSource.None or > ConsoleInputSource.All || source == ConsoleInputSource.None)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Input source is invalid.");
        if (pointer is not null && !source.HasFlag(ConsoleInputSource.Pointer))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Pointer payload requires the pointer source.");
        if (key is not null && !source.HasFlag(ConsoleInputSource.Keyboard))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, "Key payload requires the keyboard source.");
        Source = source;
        Pointer = pointer;
        Key = key;
    }

    public string PromptId { get; }

    public string ClientMessageId { get; }

    public string Value { get; }

    public ConsoleInputSource Source { get; }

    public ConsolePointerPayload? Pointer { get; }

    public ConsoleKeyPayload? Key { get; }

    public string Fingerprint => $"{PromptId.Length}:{PromptId}|{Value.Length}:{Value}|{Source}|{Pointer?.Position.X}:{Pointer?.Position.Y}:{Pointer?.Button}|{Key?.KeyCode}:{Key?.Control}:{Key?.Alt}:{Key?.Shift}";
}
