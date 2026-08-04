namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Owns one active prompt, deterministic input completion and a bounded
/// client-message receipt cache. All decisions happen under one lock.
/// </summary>
public sealed class InputCoordinator
{
    private readonly object sync = new();
    private readonly ConsoleContractLimits limits;
    private readonly int maxReceiptCount;
    private readonly Dictionary<string, Receipt> receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> receiptOrder = new();
    private readonly HashSet<string> completedPromptIds = new(StringComparer.Ordinal);
    private readonly Queue<string> completedPromptOrder = new();
    private readonly Dictionary<string, PromptWaitState> waiters = new(StringComparer.Ordinal);
    private readonly Queue<string> completedWaiterOrder = new();
    private ConsolePrompt? currentPrompt;

    public InputCoordinator()
        : this(ConsoleHistoryOptions.Default)
    {
    }

    public InputCoordinator(ConsoleHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        limits = options.ContractLimits;
        maxReceiptCount = options.MaxInputReceiptCount;
    }

    public InputCoordinator(ConsoleContractLimits limits, int maxReceiptCount = 2_048)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (maxReceiptCount <= 0)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InputReceiptLimitInvalid,
                "The input receipt limit must be positive.",
                nameof(maxReceiptCount));
        }

        this.limits = limits;
        this.maxReceiptCount = maxReceiptCount;
    }

    public ConsolePrompt? CurrentPrompt
    {
        get
        {
            lock (sync)
            {
                return currentPrompt;
            }
        }
    }

    public int ReceiptCount
    {
        get
        {
            lock (sync)
            {
                return receipts.Count;
            }
        }
    }

    public int WaiterCount
    {
        get
        {
            lock (sync)
            {
                return waiters.Count;
            }
        }
    }

    public void OpenPrompt(ConsolePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        prompt.Validate(limits);

        lock (sync)
        {
            if (currentPrompt is not null)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.PromptAlreadyActive,
                    "A console prompt is already active.");
            }

            if (waiters.ContainsKey(prompt.PromptId) || completedPromptIds.Contains(prompt.PromptId))
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidPrompt,
                    "A prompt id cannot be reused by the same coordinator.");
            }

            currentPrompt = prompt;
            waiters.Add(prompt.PromptId, new PromptWaitState(prompt));
        }
    }

    public ConsoleInputResult Submit(ConsoleInputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (sync)
        {
            if (receipts.TryGetValue(command.ClientMessageId, out Receipt? receipt))
            {
                return receipt.PromptId == command.PromptId && receipt.Value == command.Value
                    ? ConsoleInputResult.Duplicate(command, receipt.Result)
                    : ConsoleInputResult.Conflict(command);
            }

            ConsoleInputResult result;
            if (!ValidateCommand(command, out ConsoleInputFailureReason commandFailure))
            {
                result = ConsoleInputResult.InvalidCommand(command, commandFailure);
                AddReceipt(command, result);
                return result;
            }

            if (currentPrompt is null)
            {
                result = completedPromptIds.Contains(command.PromptId)
                    ? ConsoleInputResult.Stale(command)
                    : ConsoleInputResult.NoActive(command);
                AddReceipt(command, result);
                return result;
            }

            if (!string.Equals(currentPrompt.PromptId, command.PromptId, StringComparison.Ordinal))
            {
                result = ConsoleInputResult.Stale(command);
                AddReceipt(command, result);
                return result;
            }

            if (!currentPrompt.Constraints.TryValidate(command.Value, limits, out ConsoleInputFailureReason valueFailure))
            {
                result = ConsoleInputResult.InvalidFormat(command, valueFailure);
                AddReceipt(command, result);
                return result;
            }

            ConsolePrompt prompt = currentPrompt;
            var input = new GameConsoleInput(prompt.PromptId, prompt.InputType, command.Value);
            result = ConsoleInputResult.Accepted(command, input);
            currentPrompt = null;
            MarkPromptCompleted(prompt.PromptId);
            AddReceipt(command, result);
            CompleteWaiter(prompt.PromptId, result);
            return result;
        }
    }

    public ConsoleInputResult SubmitInput(ConsoleInputCommand command) => Submit(command);

    public void Open(ConsolePrompt prompt) => OpenPrompt(prompt);

    internal async Task<ConsoleInputResult> WaitAsync(
        ConsolePrompt prompt,
        IRuntimeClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(clock);

        PromptWaitState waiter;
        lock (sync)
        {
            if (!waiters.TryGetValue(prompt.PromptId, out waiter!))
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.PromptAlreadyCompleted,
                    "The prompt is not registered with this input coordinator.");
            }
        }

        CancellationTokenRegistration cancellationRegistration = default;
        CancellationTokenSource? delayCancellation = null;
        try
        {
            cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var data = ((InputCoordinator Coordinator, string PromptId))state!;
                    data.Coordinator.CompleteCancelled(data.PromptId);
                },
                (this, prompt.PromptId));

            if (prompt.Timeout is not null && prompt.Timeout != Timeout.InfiniteTimeSpan)
            {
                delayCancellation = new CancellationTokenSource();
                RuntimeDeadline deadline = RuntimeDeadline.After(clock, prompt.Timeout.Value);
                Task delayTask = deadline.DelayAsync(delayCancellation.Token).AsTask();
                if (!waiter.Completion.Task.IsCompleted)
                {
                    await Task.WhenAny(waiter.Completion.Task, delayTask).ConfigureAwait(false);
                }

                if (!waiter.Completion.Task.IsCompleted)
                {
                    if (delayTask.IsCanceled)
                    {
                        CompleteCancelled(prompt.PromptId);
                    }
                    else
                    {
                        CompleteTimedOut(prompt);
                    }
                }
            }
            else
            {
                await waiter.Completion.Task.ConfigureAwait(false);
            }

            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
            delayCancellation?.Cancel();
            RemoveWaiter(prompt.PromptId, waiter);
        }
    }

    internal ConsoleInputResult? ClosePrompt(string promptId, ConsolePromptCloseReason reason)
    {
        ConsoleContractValidation.ValidateIdentifier(promptId, nameof(promptId), limits.MaxPromptIdLength);
        lock (sync)
        {
            if (currentPrompt is null || !string.Equals(currentPrompt.PromptId, promptId, StringComparison.Ordinal))
            {
                return null;
            }

            ConsoleInputResult result = reason switch
            {
                ConsolePromptCloseReason.TimedOut => ConsoleInputResult.TimedOut(currentPrompt),
                ConsolePromptCloseReason.Cancelled => ConsoleInputResult.Cancelled(currentPrompt),
                _ => ConsoleInputResult.Cancelled(currentPrompt)
            };
            currentPrompt = null;
            MarkPromptCompleted(promptId);
            CompleteWaiter(promptId, result);
            return result;
        }
    }

    internal void AbortOpen(string promptId)
    {
        lock (sync)
        {
            if (currentPrompt is not null && string.Equals(currentPrompt.PromptId, promptId, StringComparison.Ordinal))
            {
                currentPrompt = null;
            }

            waiters.Remove(promptId);
        }
    }

    internal ConsoleInputResult CompleteCancelled(string promptId)
    {
        lock (sync)
        {
            if (!waiters.TryGetValue(promptId, out PromptWaitState? waiter))
            {
                return ConsoleInputResult.Cancelled(new ConsolePrompt(promptId, ConsoleInputType.Text));
            }

            if (waiter.Completion.Task.IsCompleted)
            {
                return waiter.Completion.Task.GetAwaiter().GetResult();
            }

            currentPrompt = null;
            MarkPromptCompleted(waiter.Prompt.PromptId);
            ConsoleInputResult result = ConsoleInputResult.Cancelled(waiter.Prompt);
            CompleteWaiter(promptId, result);
            return result;
        }
    }

    private ConsoleInputResult CompleteTimedOut(ConsolePrompt prompt)
    {
        lock (sync)
        {
            if (!waiters.TryGetValue(prompt.PromptId, out PromptWaitState? waiter))
            {
                return ConsoleInputResult.TimedOut(prompt);
            }

            if (waiter.Completion.Task.IsCompleted)
            {
                return waiter.Completion.Task.GetAwaiter().GetResult();
            }

            currentPrompt = null;
            MarkPromptCompleted(prompt.PromptId);
            GameConsoleInput? defaultInput = prompt.TimeoutBehavior == ConsolePromptTimeoutBehavior.ReturnDefaultValue &&
                prompt.DefaultValue is not null
                ? new GameConsoleInput(prompt.PromptId, prompt.InputType, prompt.DefaultValue, isDefaultValue: true)
                : null;
            ConsoleInputResult result = ConsoleInputResult.TimedOut(prompt, defaultInput);
            CompleteWaiter(prompt.PromptId, result);
            return result;
        }
    }

    private void CompleteWaiter(string promptId, ConsoleInputResult result)
    {
        if (waiters.TryGetValue(promptId, out PromptWaitState? waiter) && waiter.Completion.TrySetResult(result))
        {
            completedWaiterOrder.Enqueue(promptId);
            PruneCompletedWaiters();
        }
    }

    private void PruneCompletedWaiters()
    {
        while (completedWaiterOrder.Count > maxReceiptCount)
        {
            string promptId = completedWaiterOrder.Dequeue();
            if (waiters.TryGetValue(promptId, out PromptWaitState? waiter) && waiter.Completion.Task.IsCompleted)
            {
                waiters.Remove(promptId);
            }
        }
    }

    private void RemoveWaiter(string promptId, PromptWaitState waiter)
    {
        lock (sync)
        {
            if (waiters.TryGetValue(promptId, out PromptWaitState? existing) && ReferenceEquals(existing, waiter))
            {
                waiters.Remove(promptId);
            }
        }
    }

    private bool ValidateCommand(ConsoleInputCommand command, out ConsoleInputFailureReason failureReason)
    {
        try
        {
            ConsoleContractValidation.ValidateIdentifier(command.PromptId, nameof(command.PromptId), limits.MaxPromptIdLength);
            ConsoleContractValidation.ValidateIdentifier(command.ClientMessageId, nameof(command.ClientMessageId), limits.MaxClientMessageIdLength);
            ConsoleContractValidation.ValidateText(
                command.Value,
                nameof(command.Value),
                limits.MaxInputValueLength,
                ConsoleContractViolationReason.InputValueTooLong,
                allowControlCharacters: true);
            failureReason = ConsoleInputFailureReason.None;
            return true;
        }
        catch (ConsoleContractException exception)
        {
            failureReason = exception.Reason switch
            {
                ConsoleContractViolationReason.InputValueTooLong => ConsoleInputFailureReason.ValueTooLong,
                ConsoleContractViolationReason.InvalidIdentifier or ConsoleContractViolationReason.EmptyValue => ConsoleInputFailureReason.InvalidIdentifier,
                _ => ConsoleInputFailureReason.ControlCharacter
            };
            return false;
        }
    }

    private void AddReceipt(ConsoleInputCommand command, ConsoleInputResult result)
    {
        receipts[command.ClientMessageId] = new Receipt(command.PromptId, command.Value, result);
        receiptOrder.Enqueue(command.ClientMessageId);
        while (receipts.Count > maxReceiptCount)
        {
            string oldestId = receiptOrder.Dequeue();
            receipts.Remove(oldestId);
        }
    }

    private void MarkPromptCompleted(string promptId)
    {
        if (!completedPromptIds.Add(promptId))
        {
            return;
        }

        completedPromptOrder.Enqueue(promptId);
        while (completedPromptIds.Count > maxReceiptCount)
        {
            completedPromptIds.Remove(completedPromptOrder.Dequeue());
        }
    }

    private sealed record Receipt(string PromptId, string Value, ConsoleInputResult Result);

    private sealed class PromptWaitState(ConsolePrompt prompt)
    {
        public ConsolePrompt Prompt { get; } = prompt;

        public TaskCompletionSource<ConsoleInputResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
