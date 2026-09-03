using CloudEmuera.RuntimeAdapter;
using CloudEmuera.RuntimeAdapter.Tests.Time;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Input;

[Trait("Category", "ConsoleContract")]
public sealed class StructuredGameConsoleInputTests
{
    [Fact]
    public async Task ReadUsesInjectedPromptIdAndSubmitWakesRuntime()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock, new FixedPromptIdGenerator("generated"));
        GameConsoleInput? input = null;
        Task runtime = Task.Run(() => input = console.Read(new ConsolePrompt(ConsoleInputType.Integer, "Number")));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        ConsoleInputResult result = console.SubmitCurrentInput(new ConsoleInputAttempt("client-1", "7"));
        await runtime;

        Assert.Equal(ConsoleInputResultKind.Accepted, result.Kind);
        Assert.Equal("generated", result.ResolvedPromptId);
        Assert.Equal("7", input!.Value);
        Assert.Null(console.CurrentPrompt);
    }

    [Theory]
    [InlineData(ConsoleInputType.EnterKey)]
    [InlineData(ConsoleInputType.AnyKey)]
    public async Task RightPointerInputMarksMessageWaitAsMessageSkip(ConsoleInputType inputType)
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock, new FixedPromptIdGenerator("generated"));
        Task<GameConsoleInput> runtime = Task.Run(() => console.Read(new ConsolePrompt(inputType)));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        ConsoleInputResult result = console.SubmitCurrentInput(new ConsoleInputAttempt(
            "right-click",
            string.Empty,
            ConsoleInputSource.Pointer,
            pointer: new ConsolePointerPayload(24, 12, button: 2)));

        GameConsoleInput input = await runtime;
        Assert.Equal(ConsoleInputResultKind.Accepted, result.Kind);
        Assert.True(input.SkipMessage);
        Assert.Equal(string.Empty, input.Value);
    }

    [Fact]
    public async Task RightPointerInputDoesNotMarkValuePromptAsMessageSkip()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock, new FixedPromptIdGenerator("generated"));
        Task<GameConsoleInput> runtime = Task.Run(() => console.Read(new ConsolePrompt(ConsoleInputType.Integer)));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        ConsoleInputResult result = console.SubmitCurrentInput(new ConsoleInputAttempt(
            "right-click-value",
            "7",
            ConsoleInputSource.Pointer,
            pointer: new ConsolePointerPayload(24, 12, button: 2)));

        GameConsoleInput input = await runtime;
        Assert.Equal(ConsoleInputResultKind.Accepted, result.Kind);
        Assert.False(input.SkipMessage);
        Assert.Equal("7", input.Value);
    }

    [Fact]
    public void ReadRejectsCallerSuppliedPromptId()
    {
        var console = new StructuredGameConsole(new ManualRuntimeClock(), new FixedPromptIdGenerator("generated"));

        ConsoleContractException exception = Assert.Throws<ConsoleContractException>(() =>
            console.Read(new ConsolePrompt("caller-id", ConsoleInputType.Text)));

        Assert.Equal(ConsoleContractViolationReason.InvalidPrompt, exception.Reason);
        Assert.Null(console.CurrentPrompt);
    }

    [Fact]
    public void PromptGenerationIsNonNegativeAndSurvivesIdentityAndTimingAssignment()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConsolePrompt(ConsoleInputType.Integer, buttonGeneration: -1));

        var template = new ConsolePrompt(ConsoleInputType.Integer, buttonGeneration: 17);
        ConsolePrompt opened = template.WithPromptId("generated").WithTiming(
            DateTimeOffset.FromUnixTimeMilliseconds(1_000),
            deadlineUnixMilliseconds: 2_000);

        Assert.Equal(17, opened.ButtonGeneration);
    }

    [Fact]
    public async Task ManualClockTimeoutClosesPromptWithoutWallClockDelay()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock);
        Task<GameConsoleInput> runtime = Task.Run(() =>
            console.Read(new ConsolePrompt(
                ConsoleInputType.Text,
                "Name",
                timeout: TimeSpan.FromSeconds(5))));

        // The setup waits are only reaching a state, not timing the runtime: keep a
        // generous wall-clock budget so the manual-clock assertion stays load-safe.
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => clock.PendingWaiterCount == 1, TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ConsolePromptTimeoutException>(async () => await runtime);
        Assert.Null(console.CurrentPrompt);
    }

    [Fact]
    public async Task TimeoutThenLateInputIsRejectedAsNoActivePrompt()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock);
        Task<GameConsoleInput> runtime = Task.Run(() =>
            console.Read(new ConsolePrompt(
                ConsoleInputType.Text,
                "Name",
                timeout: TimeSpan.FromSeconds(5))));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => clock.PendingWaiterCount == 1, TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => console.InputCoordinator.CurrentPrompt is null, TimeSpan.FromSeconds(10)));

        ConsoleInputResult late = console.SubmitCurrentInput(new ConsoleInputAttempt("late", "too-late"));

        await Assert.ThrowsAsync<ConsolePromptTimeoutException>(async () => await runtime);
        Assert.Equal(ConsoleInputResultKind.NoActivePrompt, late.Kind);
        Assert.Null(late.ResolvedPromptId);
        Assert.Null(console.CurrentPrompt);
    }

    [Fact]
    public async Task CancellationTimeoutAndInputRaceHasExactlyOneTerminalWinner()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock);
        using var cancellation = new CancellationTokenSource();
        Task<GameConsoleInput> runtime = Task.Run(() =>
            console.Read(
                new ConsolePrompt(ConsoleInputType.Text, timeout: TimeSpan.FromSeconds(5)),
                cancellation.Token));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => clock.PendingWaiterCount == 1, TimeSpan.FromSeconds(10)));

        var barrier = new Barrier(4);
        ConsoleInputResult? inputResult = null;
        Task inputTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            inputResult = console.SubmitCurrentInput(new ConsoleInputAttempt("winner", "ok"));
        });
        Task cancellationTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            cancellation.Cancel();
        });
        Task timeoutTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            clock.Advance(TimeSpan.FromSeconds(5));
        });
        barrier.SignalAndWait();
        await Task.WhenAll(inputTask, cancellationTask, timeoutTask);

        GameConsoleInput? accepted = null;
        Exception? terminalException = null;
        try
        {
            accepted = await runtime;
        }
        catch (Exception exception) when (exception is ConsolePromptCancelledException or ConsolePromptTimeoutException)
        {
            terminalException = exception;
        }

        Assert.Equal(1, (accepted is not null ? 1 : 0) + (terminalException is not null ? 1 : 0));
        Assert.NotNull(inputResult);
        Assert.Contains(
            inputResult!.Kind,
            new[]
            {
                ConsoleInputResultKind.Accepted,
                ConsoleInputResultKind.NoActivePrompt
            });
        Assert.Null(console.CurrentPrompt);
    }

    [Fact]
    public async Task TraceObserverSeesOneOpenedAndOneAcceptedResolution()
    {
        var observer = new RecordingTraceObserver();
        var console = new StructuredGameConsole(
            new ManualRuntimeClock(),
            ConsoleHistoryOptions.Default,
            new FixedPromptIdGenerator("traced"),
            observer);
        Task<GameConsoleInput> runtime = Task.Run(() => console.Read(new ConsolePrompt(ConsoleInputType.Text)));
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));

        ConsoleInputResult result = console.SubmitCurrentInput(new ConsoleInputAttempt("accepted", "value"));
        _ = await runtime;

        Assert.Equal(ConsoleInputResultKind.Accepted, result.Kind);
        Assert.Single(observer.Opened);
        (ConsolePrompt Prompt, ConsoleInputResult Result, ConsoleInputAttempt? Attempt) resolution = Assert.Single(observer.Resolved);
        Assert.Equal("traced", resolution.Prompt.PromptId);
        Assert.Equal("accepted", resolution.Attempt!.ClientMessageId);
    }

    [Fact]
    public void NoActivePromptAttemptDoesNotReachTraceObserver()
    {
        var observer = new RecordingTraceObserver();
        var console = new StructuredGameConsole(
            new ManualRuntimeClock(),
            ConsoleHistoryOptions.Default,
            new FixedPromptIdGenerator("unused"),
            observer);

        ConsoleInputResult result = console.SubmitCurrentInput(new ConsoleInputAttempt("inactive", "value"));

        Assert.Equal(ConsoleInputResultKind.NoActivePrompt, result.Kind);
        Assert.Empty(observer.Opened);
        Assert.Empty(observer.Resolved);
    }

    [Fact]
    public async Task TimeoutResolutionReachesObserverWithoutSubmittedInput()
    {
        var clock = new ManualRuntimeClock();
        var observer = new RecordingTraceObserver();
        var console = new StructuredGameConsole(
            clock,
            ConsoleHistoryOptions.Default,
            new FixedPromptIdGenerator("timeout"),
            observer);
        Task<GameConsoleInput> runtime = Task.Run(() => console.Read(new ConsolePrompt(
            ConsoleInputType.Text,
            timeout: TimeSpan.FromSeconds(2))));
        Assert.True(SpinWait.SpinUntil(() => clock.PendingWaiterCount == 1, TimeSpan.FromSeconds(10)));

        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ConsolePromptTimeoutException>(async () => await runtime);

        (ConsolePrompt _, ConsoleInputResult Result, ConsoleInputAttempt? Attempt) resolution = Assert.Single(observer.Resolved);
        Assert.Equal(ConsoleInputResultKind.TimedOut, resolution.Result.Kind);
        Assert.Null(resolution.Attempt);
    }

    [Fact]
    public async Task FormalPromptCancellationProducesOneCancelledResolution()
    {
        var observer = new RecordingTraceObserver();
        var console = new StructuredGameConsole(
            new ManualRuntimeClock(), ConsoleHistoryOptions.Default,
            new FixedPromptIdGenerator("cancelled"), observer);
        Task<GameConsoleInput> runtime = Task.Run(() => console.Read(new ConsolePrompt(ConsoleInputType.Text)));
        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));

        Assert.True(console.CancelCurrentPrompt());
        await Assert.ThrowsAsync<ConsolePromptCancelledException>(async () => await runtime);

        Assert.False(console.CancelCurrentPrompt());
        (ConsolePrompt _, ConsoleInputResult Result, ConsoleInputAttempt? Attempt) resolution = Assert.Single(observer.Resolved);
        Assert.Equal(ConsoleInputResultKind.Cancelled, resolution.Result.Kind);
        Assert.Null(resolution.Attempt);
    }

    private sealed class FixedPromptIdGenerator(string id) : IPromptIdGenerator
    {
        public string Next() => id;
    }

    private sealed class RecordingTraceObserver : IConsoleInputTraceObserver
    {
        public List<ConsolePrompt> Opened { get; } = [];

        public List<(ConsolePrompt Prompt, ConsoleInputResult Result, ConsoleInputAttempt? Attempt)> Resolved { get; } = [];

        public void PromptOpened(ConsolePrompt prompt) => Opened.Add(prompt);

        public void PromptResolved(ConsolePrompt prompt, ConsoleInputResult result, ConsoleInputAttempt? attempt) =>
            Resolved.Add((prompt, result, attempt));
    }
}
