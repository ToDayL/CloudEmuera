namespace CloudEmuera.RuntimeAdapter;

/// <summary>Client input scoped to exactly one prompt.</summary>
public sealed class ConsoleInputCommand
{
    public ConsoleInputCommand(string promptId, string clientMessageId, string value)
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
    }

    public string PromptId { get; }

    public string ClientMessageId { get; }

    public string Value { get; }

    public string Fingerprint => $"{PromptId.Length}:{PromptId}|{Value.Length}:{Value}";
}
