namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Synchronous runtime-facing console. The interpreter can emit without
/// waiting for a browser, while another thread submits input and wakes a
/// blocked Read call.
/// </summary>
public sealed class StructuredGameConsole : IGameConsole, ITooltipStateSink
{
    private readonly object sync = new();
    private readonly IRuntimeClock clock;
    private readonly IPromptIdGenerator promptIdGenerator;
    private bool isTimeOut;

    public StructuredGameConsole()
        : this(new TimeProviderRuntimeClock(), ConsoleHistoryOptions.Default, new GuidPromptIdGenerator())
    {
    }

    public StructuredGameConsole(IRuntimeClock clock)
        : this(clock, ConsoleHistoryOptions.Default, new GuidPromptIdGenerator())
    {
    }

    public StructuredGameConsole(
        IRuntimeClock clock,
        ConsoleHistoryOptions options,
        IPromptIdGenerator? promptIdGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.clock = clock;
        this.promptIdGenerator = promptIdGenerator ?? new GuidPromptIdGenerator();
        StateStore = new ConsoleStateStore(options);
        InputCoordinator = new InputCoordinator(options);
    }

    public StructuredGameConsole(
        IRuntimeClock clock,
        IPromptIdGenerator promptIdGenerator,
        ConsoleHistoryOptions? options = null)
        : this(clock, options ?? ConsoleHistoryOptions.Default, promptIdGenerator)
    {
    }

    public IRuntimeClock Clock => clock;

    public ConsoleStateStore StateStore { get; }

    public InputCoordinator InputCoordinator { get; }

    public ConsoleSnapshot Snapshot => StateStore.Snapshot;

    public ConsoleSnapshot? CommittedSnapshot => StateStore.CommittedSnapshot;

    public DisplayCommit? CurrentDisplayCommit => StateStore.CurrentDisplayCommit;

    public ConsoleTooltipPresentation TooltipPresentation => StateStore.Snapshot.TooltipPresentation;

    public DisplayCommit CommitDisplayFrame(DisplayCommitReason reason) => StateStore.CommitDisplayFrame(reason);

    public ConsolePrompt? CurrentPrompt => StateStore.CurrentPrompt;

    public SequencedConsoleTransaction EmitTransaction(ConsoleTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        lock (sync)
        {
            return StateStore.ApplyTransaction(transaction);
        }
    }

    public void SetTooltipCustom(bool enabled) => UpdateTooltip(current =>
        current.CustomEnabled == enabled ? current : current.Next(customEnabled: enabled));

    public void SetTooltipColor(ConsoleColor foreground, ConsoleColor background) => UpdateTooltip(current =>
        current.Foreground == foreground && current.Background == background
            ? current
            : current.Next(foreground: foreground, background: background));

    public void SetTooltipDelay(int milliseconds)
    {
        if (milliseconds is < 0 or > ConsoleContractLimits.MaxTooltipDelayMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        UpdateTooltip(current => current.DelayMilliseconds == milliseconds
            ? current
            : current.Next(delayMilliseconds: milliseconds));
    }

    public void SetTooltipDuration(int milliseconds)
    {
        if (milliseconds is < 0 or > ConsoleContractLimits.MaxTooltipDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        UpdateTooltip(current => current.DurationMilliseconds == milliseconds
            ? current
            : current.Next(durationMilliseconds: milliseconds));
    }

    public void SetTooltipFont(string requestedName)
    {
        ArgumentNullException.ThrowIfNull(requestedName);
        // ADR-0029: every game font request maps to the immutable Session face.
        // The requested name is intentionally not protocol state.
    }

    public void SetTooltipFontSize(long size)
    {
        if (size is < 1 or > ConsoleContractLimits.MaxTooltipFontSize)
            throw new ArgumentOutOfRangeException(nameof(size));
        UpdateTooltip(current => current.FontSize == size
            ? current
            : current.Next(fontSize: checked((int)size)));
    }

    public void SetTooltipFormat(long textFormatFlags)
    {
        ConsoleTooltipTextFormat format = ConsoleTooltipTextFormat.FromTextFormatFlags(textFormatFlags);
        UpdateTooltip(current => current.TextFormat == format ? current : current.Next(textFormat: format));
    }

    public void SetTooltipImageMode(bool enabled) => UpdateTooltip(current =>
        current.ImageMode == enabled ? current : current.Next(imageMode: enabled));

    private void UpdateTooltip(Func<ConsoleTooltipPresentation, ConsoleTooltipPresentation> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (sync)
        {
            ConsoleTooltipPresentation current = StateStore.Snapshot.TooltipPresentation;
            ConsoleTooltipPresentation next = update(current);
            if (!ReferenceEquals(current, next))
                ApplyRuntimeOperation(ConsoleOperation.SetTooltipPresentation(next));
        }
    }

    /// <summary>Whether the most recently completed read ended by timeout.</summary>
    public bool IsTimeOut
    {
        get
        {
            lock (sync)
            {
                return isTimeOut;
            }
        }
    }

    public void Emit(ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (sync)
        {
            switch (operation)
            {
                case OpenPromptOperation open:
                    ConsolePrompt assignedPrompt = AssignPromptId(open.Prompt);
                    InputCoordinator.OpenPrompt(assignedPrompt, clock);
                    ConsolePrompt openedPrompt = InputCoordinator.CurrentPrompt!;
                    var assignedOperation = new OpenPromptOperation(openedPrompt);
                    isTimeOut = false;
                    try
                    {
                        ApplyRuntimeOperation(assignedOperation);
                    }
                    catch
                    {
                        InputCoordinator.AbortOpen(assignedPrompt.PromptId);
                        throw;
                    }

                    break;
                case ClosePromptOperation close:
                    ApplyRuntimeOperation(operation);
                    InputCoordinator.ClosePrompt(close.PromptId, close.Reason);
                    break;
                default:
                    ApplyRuntimeOperation(operation);
                    break;
            }
        }
    }

    /// <summary>Submits client input to the current input slot without invoking the runtime thread.</summary>
    public ConsoleInputResult SubmitCurrentInput(ConsoleInputAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (sync)
        {
            ConsoleInputResult result = InputCoordinator.SubmitCurrent(attempt);
            if (result.Kind == ConsoleInputResultKind.Accepted)
            {
                isTimeOut = false;
                CloseStatePromptIfCurrent(result.ResolvedPromptId!, ConsolePromptCloseReason.InputAccepted);
            }

            return result;
        }
    }

    public ConsoleResumeResult ReadSince(long lastSequence) => StateStore.ReadSince(lastSequence);

    public GameConsoleInput Read(ConsolePrompt prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        ConsolePrompt assignedPrompt;
        lock (sync)
        {
            assignedPrompt = AssignPromptId(prompt);
            InputCoordinator.OpenPrompt(assignedPrompt, clock);
            assignedPrompt = InputCoordinator.CurrentPrompt!;
            isTimeOut = false;
            try
            {
                ApplyRuntimeOperation(new OpenPromptOperation(assignedPrompt));
            }
            catch
            {
                InputCoordinator.AbortOpen(assignedPrompt.PromptId);
                throw;
            }
        }

        ConsoleInputResult result;
        try
        {
            // WaitAsync never captures a SynchronizationContext and uses the
            // injected clock. This synchronous boundary is therefore safe for
            // the interpreter thread and does not require a UI message pump.
            result = InputCoordinator.WaitAsync(assignedPrompt, clock, cancellationToken)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            lock (sync)
            {
                InputCoordinator.ClosePrompt(assignedPrompt.PromptId, ConsolePromptCloseReason.Cancelled);
                CloseStatePromptIfCurrent(assignedPrompt.PromptId, ConsolePromptCloseReason.Cancelled);
            }

            throw;
        }

        lock (sync)
        {
            switch (result.Kind)
            {
                case ConsoleInputResultKind.Accepted:
                    isTimeOut = false;
                    CloseStatePromptIfCurrent(assignedPrompt.PromptId, ConsolePromptCloseReason.InputAccepted);
                    return result.Input!;
                case ConsoleInputResultKind.TimedOut:
                    isTimeOut = true;
                    CloseStatePromptIfCurrent(assignedPrompt.PromptId, ConsolePromptCloseReason.TimedOut);
                    if (result.Input is not null)
                    {
                        return result.Input;
                    }

                    if (assignedPrompt.TimeoutAction == ConsolePromptTimeoutAction.ContinueWithoutValue)
                    {
                        return new GameConsoleInput(assignedPrompt.PromptId, assignedPrompt.InputType, string.Empty);
                    }

                    throw new ConsolePromptTimeoutException(assignedPrompt.PromptId);
                case ConsoleInputResultKind.Cancelled:
                    isTimeOut = false;
                    CloseStatePromptIfCurrent(assignedPrompt.PromptId, ConsolePromptCloseReason.Cancelled);
                    throw new ConsolePromptCancelledException(assignedPrompt.PromptId, cancellationToken);
                default:
                    CloseStatePromptIfCurrent(assignedPrompt.PromptId, ConsolePromptCloseReason.Cancelled);
                    throw new InvalidOperationException("The input coordinator returned a non-terminal client result to Read.");
            }
        }
    }

    private ConsolePrompt AssignPromptId(ConsolePrompt prompt)
    {
        if (prompt.HasPromptId)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "StructuredGameConsole assigns prompt ids; callers must provide a prompt template without an id.");
        }

        string promptId = CreatePromptId();
        return prompt.WithPromptId(promptId);
    }

    private string CreatePromptId()
    {
        string candidate = promptIdGenerator.NextId();
        ConsoleContractValidation.ValidateIdentifier(
            candidate,
            nameof(candidate),
            StateStore.Options.ContractLimits.MaxPromptIdLength);

        ConsolePrompt? currentPrompt = StateStore.CurrentPrompt;
        if (currentPrompt is not null && string.Equals(currentPrompt.PromptId, candidate, StringComparison.Ordinal))
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.InvalidPrompt,
                "The prompt id generator returned the id of the active prompt.");
        }

        return candidate;
    }

    private void CloseStatePromptIfCurrent(string promptId, ConsolePromptCloseReason reason)
    {
        ConsolePrompt? current = StateStore.CurrentPrompt;
        if (current is not null && string.Equals(current.PromptId, promptId, StringComparison.Ordinal))
        {
            ApplyRuntimeOperation(new ClosePromptOperation(promptId, reason));
        }
    }

    private void ApplyRuntimeOperation(ConsoleOperation operation) =>
        _ = StateStore.Apply(operation);
}
