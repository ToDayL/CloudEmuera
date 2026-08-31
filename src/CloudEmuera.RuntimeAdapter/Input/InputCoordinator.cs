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
        => OpenPrompt(prompt, clock: null);

    /// <summary>
    /// Opens a prompt and, when a clock is supplied, captures its monotonic
    /// start timestamp and wall-clock display metadata at publication time.
    /// </summary>
    public void OpenPrompt(ConsolePrompt prompt, IRuntimeClock? clock)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        prompt.Validate(limits);

        ConsolePrompt effectivePrompt = prompt;
        long? startTimestamp = null;
        if (clock is not null)
        {
            startTimestamp = clock.GetTimestamp();
            DateTimeOffset openedAt = clock.UtcNow;
            long? deadline = prompt.Timeout is { } timeout && timeout != Timeout.InfiniteTimeSpan
                ? openedAt.Add(timeout).ToUnixTimeMilliseconds()
                : null;
            effectivePrompt = prompt.WithTiming(openedAt, deadline);
            effectivePrompt.Validate(limits);
        }

        lock (sync)
        {
            if (currentPrompt is not null)
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.PromptAlreadyActive,
                    "A console prompt is already active.");
            }

            if (waiters.ContainsKey(effectivePrompt.PromptId) || completedPromptIds.Contains(effectivePrompt.PromptId))
            {
                throw new ConsoleContractException(
                    ConsoleContractViolationReason.InvalidPrompt,
                    "A prompt id cannot be reused by the same coordinator.");
            }

            currentPrompt = effectivePrompt;
            waiters.Add(effectivePrompt.PromptId, new PromptWaitState(effectivePrompt, startTimestamp));
        }
    }

    /// <summary>
    /// Submits an input intention to the prompt that is active when this call
    /// acquires the coordinator lock. No inactive attempt is queued for a
    /// later prompt.
    /// </summary>
    public ConsoleInputResult SubmitCurrent(ConsoleInputAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (sync)
        {
            if (receipts.TryGetValue(attempt.ClientMessageId, out Receipt? receipt))
            {
                return receipt.Fingerprint == attempt.Fingerprint
                    ? ConsoleInputResult.Duplicate(attempt, receipt.Result)
                    : ConsoleInputResult.Conflict(attempt);
            }

            ConsoleInputResult result;
            if (!ValidateAttempt(attempt, out ConsoleInputFailureReason commandFailure))
            {
                result = ConsoleInputResult.InvalidCommand(attempt, commandFailure);
                AddReceipt(attempt, result);
                return result;
            }

            if (currentPrompt is null)
            {
                result = ConsoleInputResult.NoActive(attempt);
                AddReceipt(attempt, result);
                return result;
            }

            if ((currentPrompt.AllowedSources & attempt.Source) != attempt.Source)
            {
                result = ConsoleInputResult.InvalidFormat(attempt, currentPrompt.PromptId, ConsoleInputFailureReason.SourceNotAllowed);
                AddReceipt(attempt, result);
                return result;
            }

            if (currentPrompt.InputType == ConsoleInputType.WaitOnly)
            {
                result = ConsoleInputResult.InvalidFormat(attempt, currentPrompt.PromptId, ConsoleInputFailureReason.SourceNotAllowed);
                AddReceipt(attempt, result);
                return result;
            }

            // Upstream permits a multi-character value from a clicked game
            // button when AllowLongInputByMouse is enabled. Keep keyboard
            // ONEINPUT behavior unchanged while preserving structured button
            // values such as EraFL's 4000-series menu IDs.
            bool preserveLongButtonValue = currentPrompt.OneInput &&
                currentPrompt.AllowLongInputByButton &&
                attempt.Source is ConsoleInputSource.Button or ConsoleInputSource.Pointer;
            string value = currentPrompt.OneInput && !preserveLongButtonValue && attempt.Value.Length > 1
                ? attempt.Value[..1]
                : attempt.Value;
            if (!currentPrompt.Constraints.TryValidate(value, limits, out ConsoleInputFailureReason valueFailure))
            {
                result = ConsoleInputResult.InvalidFormat(attempt, currentPrompt.PromptId, valueFailure);
                AddReceipt(attempt, result);
                return result;
            }

            ConsolePrompt prompt = currentPrompt;
            bool skipMessage = attempt.IsMessageSkip &&
                prompt.InputType is ConsoleInputType.EnterKey or ConsoleInputType.AnyKey;
            var input = new GameConsoleInput(
                prompt.PromptId,
                prompt.InputType,
                value,
                skipMessage: skipMessage,
                pointer: attempt.Pointer);
            result = ConsoleInputResult.Accepted(attempt, input);
            currentPrompt = null;
            MarkPromptCompleted(prompt.PromptId);
            AddReceipt(attempt, result);
            CompleteWaiter(prompt.PromptId, result);
            return result;
        }
    }

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
                RuntimeDeadline deadline = waiter.StartTimestamp is long startTimestamp
                    ? RuntimeDeadline.FromStart(clock, startTimestamp, prompt.Timeout.Value)
                    : RuntimeDeadline.After(clock, prompt.Timeout.Value);
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
            GameConsoleInput? defaultInput = prompt.TimeoutAction == ConsolePromptTimeoutAction.ReturnDefaultValue &&
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

    private bool ValidateAttempt(ConsoleInputAttempt attempt, out ConsoleInputFailureReason failureReason)
    {
        try
        {
            ConsoleContractValidation.ValidateIdentifier(attempt.ClientMessageId, nameof(attempt.ClientMessageId), limits.MaxClientMessageIdLength);
            ConsoleContractValidation.ValidateText(
                attempt.Value,
                nameof(attempt.Value),
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

    private void AddReceipt(ConsoleInputAttempt attempt, ConsoleInputResult result)
    {
        receipts[attempt.ClientMessageId] = new Receipt(attempt.Fingerprint, result);
        receiptOrder.Enqueue(attempt.ClientMessageId);
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

    private sealed record Receipt(string Fingerprint, ConsoleInputResult Result);

    private sealed class PromptWaitState(ConsolePrompt prompt, long? startTimestamp)
    {
        public ConsolePrompt Prompt { get; } = prompt;

        public long? StartTimestamp { get; } = startTimestamp;

        public TaskCompletionSource<ConsoleInputResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
