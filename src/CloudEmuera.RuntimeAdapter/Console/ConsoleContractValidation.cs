namespace CloudEmuera.RuntimeAdapter;

internal static class ConsoleContractValidation
{
    public static void ValidateIdentifier(
        string value,
        string parameterName,
        int maxLength,
        ConsoleContractViolationReason reason = ConsoleContractViolationReason.InvalidIdentifier)
    {
        if (value is null)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.NullValue, $"{parameterName} is required.", parameterName);
        }

        if (value.Length == 0)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.EmptyValue, $"{parameterName} is required.", parameterName);
        }

        if (value.Length > maxLength)
        {
            throw new ConsoleContractException(reason, $"{parameterName} exceeds its length limit.", parameterName);
        }

        foreach (char character in value)
        {
            if (character > 0x7f || char.IsControl(character) || char.IsWhiteSpace(character))
            {
                throw new ConsoleContractException(reason, $"{parameterName} contains an invalid character.", parameterName);
            }

            if (!IsIdentifierCharacter(character))
            {
                throw new ConsoleContractException(reason, $"{parameterName} contains an invalid character.", parameterName);
            }
        }
    }

    public static void ValidateText(
        string? value,
        string parameterName,
        int maxLength,
        ConsoleContractViolationReason tooLongReason,
        bool allowControlCharacters = false)
    {
        if (value is null)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.NullValue, $"{parameterName} is required.", parameterName);
        }

        if (value.Length > maxLength)
        {
            throw new ConsoleContractException(tooLongReason, $"{parameterName} exceeds its length limit.", parameterName);
        }

        if (!allowControlCharacters && value.Any(char.IsControl))
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidPrompt, $"{parameterName} contains a control character.", parameterName);
        }
    }

    public static bool IsIdentifierCharacter(char character) =>
        character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '-' or '_' or '.' or '~';

    public static void ValidateFontStyle(ConsoleFontStyle style, string parameterName = "decorations")
    {
        const ConsoleFontStyle known =
            ConsoleFontStyle.Bold |
            ConsoleFontStyle.Italic |
            ConsoleFontStyle.Underline |
            ConsoleFontStyle.Strike;

        if ((style & ~known) != ConsoleFontStyle.None)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.UnknownFontStyle,
                $"{parameterName} contains an unknown flag.",
                parameterName);
        }
    }

}
