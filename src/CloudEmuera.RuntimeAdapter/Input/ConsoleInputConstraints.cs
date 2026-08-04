using System.Globalization;

namespace CloudEmuera.RuntimeAdapter;

public abstract class ConsoleInputConstraints
{
    private protected ConsoleInputConstraints()
    {
    }

    internal abstract void Validate(ConsoleContractLimits limits);

    internal abstract bool TryValidate(
        string value,
        ConsoleContractLimits limits,
        out ConsoleInputFailureReason failureReason);
}

public sealed class TextInputConstraints : ConsoleInputConstraints
{
    public TextInputConstraints(
        int? maxLength = null,
        bool allowControlCharacters = false)
    {
        if (maxLength is <= 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidInputConstraint,
                "Text input max length must be positive.",
                nameof(maxLength));
        }

        MaxLength = maxLength;
        AllowControlCharacters = allowControlCharacters;
    }

    public int? MaxLength { get; }

    public bool AllowControlCharacters { get; }

    internal override void Validate(ConsoleContractLimits limits)
    {
        if (MaxLength is <= 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidInputConstraint,
                "Text input max length must be positive.");
        }

        if (MaxLength > limits.MaxInputValueLength)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InputValueTooLong,
                "Text input max length exceeds the console contract limit.");
        }
    }

    internal override bool TryValidate(
        string value,
        ConsoleContractLimits limits,
        out ConsoleInputFailureReason failureReason)
    {
        int maxLength = MaxLength ?? limits.MaxInputValueLength;
        if (value.Length > maxLength)
        {
            failureReason = ConsoleInputFailureReason.ValueTooLong;
            return false;
        }

        if (!AllowControlCharacters && value.Any(char.IsControl))
        {
            failureReason = ConsoleInputFailureReason.ControlCharacter;
            return false;
        }

        failureReason = ConsoleInputFailureReason.None;
        return true;
    }
}

public sealed class IntegerInputConstraints : ConsoleInputConstraints
{
    public IntegerInputConstraints(
        long? minimum = null,
        long? maximum = null,
        bool allowSign = false)
    {
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidInputConstraint,
                "The integer minimum cannot exceed its maximum.");
        }

        Minimum = minimum;
        Maximum = maximum;
        AllowSign = allowSign;
    }

    public long? Minimum { get; }

    public long? Maximum { get; }

    public bool AllowSign { get; }

    internal override void Validate(ConsoleContractLimits limits)
    {
        if (Minimum is not null && Maximum is not null && Minimum > Maximum)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidInputConstraint,
                "The integer minimum cannot exceed its maximum.");
        }
    }

    internal override bool TryValidate(
        string value,
        ConsoleContractLimits limits,
        out ConsoleInputFailureReason failureReason)
    {
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
        {
            failureReason = ConsoleInputFailureReason.InvalidInteger;
            return false;
        }

        int firstDigit = 0;
        if (value[0] is '+' or '-')
        {
            if (!AllowSign || value.Length == 1)
            {
                failureReason = ConsoleInputFailureReason.InvalidInteger;
                return false;
            }

            firstDigit = 1;
        }

        if (value[firstDigit] == '0' && value.Length - firstDigit > 1)
        {
            failureReason = ConsoleInputFailureReason.InvalidInteger;
            return false;
        }

        for (int index = firstDigit; index < value.Length; index++)
        {
            if (value[index] is < '0' or > '9')
            {
                failureReason = ConsoleInputFailureReason.InvalidInteger;
                return false;
            }
        }

        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
        {
            failureReason = ConsoleInputFailureReason.InvalidInteger;
            return false;
        }

        if (Minimum is not null && parsed < Minimum || Maximum is not null && parsed > Maximum)
        {
            failureReason = ConsoleInputFailureReason.IntegerOutOfRange;
            return false;
        }

        failureReason = ConsoleInputFailureReason.None;
        return true;
    }
}

public enum ConsoleInputFailureReason
{
    None,
    ValueTooLong,
    ControlCharacter,
    InvalidIdentifier,
    InvalidInteger,
    IntegerOutOfRange
}
