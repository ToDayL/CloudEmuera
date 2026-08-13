namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Pure, explicitly sequenced reduction of structured console transactions.
/// It never mutates the supplied baseline. The Worker state store and the API
/// mirror use this same path so that a reconnect cannot acquire a different
/// interpretation of a transaction.
/// </summary>
public static class ConsoleSnapshotReducer
{
    public static ConsoleSnapshot Apply(
        ConsoleSnapshot baseline,
        SequencedConsoleTransaction transaction,
        ConsoleHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return ApplyBatch(baseline, [transaction], options);
    }

    public static ConsoleSnapshot ApplyBatch(
        ConsoleSnapshot baseline,
        IReadOnlyList<SequencedConsoleTransaction> transactions,
        ConsoleHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ConsoleSnapshotValidation.Validate(baseline, options);

        if (transactions.Count == 0)
            return baseline;

        long expectedSequence = baseline.SnapshotSequence;
        var store = new ConsoleStateStore(baseline, options);
        foreach (SequencedConsoleTransaction transaction in transactions)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            if (expectedSequence == long.MaxValue)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.SequenceExhausted,
                    "Console sequence is exhausted and cannot wrap.",
                    nameof(transactions));
            }

            long nextSequence = checked(expectedSequence + 1);
            if (transaction.Sequence != nextSequence)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidCursor,
                    "Console transactions must be strictly continuous and start after the snapshot.",
                    nameof(transactions));
            }

            store.ApplyExternalTransaction(transaction);
            expectedSequence = transaction.Sequence;
        }

        ConsoleSnapshot result = store.StructuredSnapshot;
        ConsoleSnapshotValidation.Validate(result, options);
        return result;
    }
}
