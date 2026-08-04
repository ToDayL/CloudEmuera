namespace CloudEmuera.RuntimeAdapter;

public sealed class SequencedConsoleEvent
{
    public SequencedConsoleEvent(long sequence, ConsoleOperation operation)
    {
        if (sequence <= 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidCursor,
                "A console event sequence must be positive.",
                nameof(sequence));
        }

        ArgumentNullException.ThrowIfNull(operation);
        Sequence = sequence;
        Operation = operation;
    }

    public long Sequence { get; }

    public ConsoleOperation Operation { get; }
}
