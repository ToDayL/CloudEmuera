namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Synchronous runtime-facing console. The interpreter can emit without
/// waiting for a browser, while another thread submits input and wakes a
/// blocked Read call.
/// </summary>
public sealed class StructuredGameConsole : IGameConsole
{
    private readonly object sync = new();
    private readonly IRuntimeClock clock;
    private readonly IPromptIdGenerator promptIdGenerator;

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

    public ConsolePrompt? CurrentPrompt => StateStore.CurrentPrompt;

    public void Emit(ConsoleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (sync)
        {
            switch (operation)
            {
                case OpenPromptOperation open:
                    ConsolePrompt assignedPrompt = AssignPromptId(open.Prompt);
                    var assignedOperation = new OpenPromptOperation(assignedPrompt);
                    InputCoordinator.OpenPrompt(assignedPrompt);
                    try
                    {
                        StateStore.Apply(assignedOperation);
                    }
                    catch
                    {
                        InputCoordinator.AbortOpen(assignedPrompt.PromptId);
                        throw;
                    }

                    break;
                case ClosePromptOperation close:
                    StateStore.Apply(operation);
                    InputCoordinator.ClosePrompt(close.PromptId, close.Reason);
                    break;
                default:
                    StateStore.Apply(operation);
                    break;
            }
        }
    }

    /// <summary>Submits client input without invoking the runtime thread.</summary>
    public ConsoleInputResult SubmitInput(ConsoleInputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (sync)
        {
            ConsoleInputResult result = InputCoordinator.Submit(command);
            if (result.Kind == ConsoleInputResultKind.Accepted)
            {
                CloseStatePromptIfCurrent(command.PromptId, ConsolePromptCloseReason.InputAccepted);
            }

            return result;
        }
    }

    public ConsoleInputResult Submit(ConsoleInputCommand command) => SubmitInput(command);

    public ConsoleResumeResult ReadSince(long lastSequence) => StateStore.ReadSince(lastSequence);

    public GameConsoleInput Read(ConsolePrompt prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        ConsolePrompt assignedPrompt;
        lock (sync)
        {
            assignedPrompt = AssignPromptId(prompt);
            InputCoordinator.OpenPrompt(assignedPrompt);
            try
            {
                StateStore.Apply(new OpenPromptOperation(assignedPrompt));
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
                    CloseStatePromptIfCurrent(assignedPrompt.PromptId, ConsolePromptCloseReason.InputAccepted);
                    return result.Input!;
                case ConsoleInputResultKind.TimedOut:
                    CloseStatePromptIfCurrent(assignedPrompt.PromptId, ConsolePromptCloseReason.TimedOut);
                    if (result.Input is not null)
                    {
                        return result.Input;
                    }

                    throw new ConsolePromptTimeoutException(assignedPrompt.PromptId);
                case ConsoleInputResultKind.Cancelled:
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
            StateStore.Apply(new ClosePromptOperation(promptId, reason));
        }
    }
}
