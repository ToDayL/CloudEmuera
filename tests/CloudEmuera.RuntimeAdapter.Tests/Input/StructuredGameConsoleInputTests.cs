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
        string promptId = console.CurrentPrompt!.PromptId;
        ConsoleInputResult result = console.SubmitInput(new ConsoleInputCommand(promptId, "client-1", "7"));
        await runtime;

        Assert.Equal(ConsoleInputResultKind.Accepted, result.Kind);
        Assert.Equal("generated", promptId);
        Assert.Equal("7", input!.Value);
        Assert.Null(console.CurrentPrompt);
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
    public async Task TimeoutThenLateInputIsRejectedAsStale()
    {
        var clock = new ManualRuntimeClock();
        var console = new StructuredGameConsole(clock);
        Task<GameConsoleInput> runtime = Task.Run(() =>
            console.Read(new ConsolePrompt(
                ConsoleInputType.Text,
                "Name",
                timeout: TimeSpan.FromSeconds(5))));

        Assert.True(SpinWait.SpinUntil(() => console.CurrentPrompt is not null, TimeSpan.FromSeconds(10)));
        string promptId = console.CurrentPrompt!.PromptId;
        Assert.True(SpinWait.SpinUntil(() => clock.PendingWaiterCount == 1, TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => console.InputCoordinator.CurrentPrompt is null, TimeSpan.FromSeconds(10)));

        ConsoleInputResult late = console.SubmitInput(new ConsoleInputCommand(promptId, "late", "too-late"));

        await Assert.ThrowsAsync<ConsolePromptTimeoutException>(async () => await runtime);
        Assert.Equal(ConsoleInputResultKind.StalePrompt, late.Kind);
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
        string promptId = console.CurrentPrompt!.PromptId;
        Assert.True(SpinWait.SpinUntil(() => clock.PendingWaiterCount == 1, TimeSpan.FromSeconds(10)));

        var barrier = new Barrier(4);
        ConsoleInputResult? inputResult = null;
        Task inputTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            inputResult = console.SubmitInput(new ConsoleInputCommand(promptId, "winner", "ok"));
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
                ConsoleInputResultKind.StalePrompt
            });
        Assert.Null(console.CurrentPrompt);
    }

    private sealed class FixedPromptIdGenerator(string id) : IPromptIdGenerator
    {
        public string Next() => id;
    }
}
