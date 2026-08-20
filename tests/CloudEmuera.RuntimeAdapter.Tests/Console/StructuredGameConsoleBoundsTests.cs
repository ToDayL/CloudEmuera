using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class StructuredGameConsoleBoundsTests
{
    [Fact]
    public void PromptIdsAreAllocatedByConsoleAndCallerIdsAreRejected()
    {
        var options = new ConsoleHistoryOptions
        {
            MaxInputReceiptCount = 2
        };
        var console = new StructuredGameConsole(
            new ImmediateRuntimeClock(),
            options,
            new SequencePromptIdGenerator("p1", "p2", "p3"));

        console.Emit(new OpenPromptOperation(new ConsolePrompt(ConsoleInputType.Text)));
        Assert.Equal("p1", console.CurrentPrompt!.PromptId);
        ConsoleContractException callerId = Assert.Throws<ConsoleContractException>(() =>
            console.Emit(new OpenPromptOperation(new ConsolePrompt("caller-id", ConsoleInputType.Text))));

        Assert.Equal(ConsoleContractViolationReason.InvalidPrompt, callerId.Reason);
        Assert.Equal("p1", console.CurrentPrompt!.PromptId);

        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt("m1", "ok")).Kind);
        console.Emit(new OpenPromptOperation(new ConsolePrompt(ConsoleInputType.Text)));
        Assert.Equal("p2", console.CurrentPrompt!.PromptId);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt("m2", "ok")).Kind);
        console.Emit(new OpenPromptOperation(new ConsolePrompt(ConsoleInputType.Text)));
        Assert.Equal("p3", console.CurrentPrompt!.PromptId);
        Assert.Equal(
            ConsoleInputResultKind.Accepted,
            console.SubmitCurrentInput(new ConsoleInputAttempt("m3", "ok")).Kind);
    }

    [Fact]
    public void CurrentSlotAcceptsInputRegardlessOfAnEarlierPromptIdentity()
    {
        var options = new ConsoleHistoryOptions { MaxInputReceiptCount = 2 };
        var console = new StructuredGameConsole(
            new ImmediateRuntimeClock(),
            options,
            new SequencePromptIdGenerator("p1", "p2", "p3", "p4"));

        for (int index = 1; index <= 3; index++)
        {
            console.Emit(new OpenPromptOperation(new ConsolePrompt(ConsoleInputType.Text)));
            string promptId = console.CurrentPrompt!.PromptId;
            Assert.Equal($"p{index}", promptId);
            Assert.Equal(
                ConsoleInputResultKind.Accepted,
                console.SubmitCurrentInput(new ConsoleInputAttempt($"message-{index}", "ok")).Kind);
        }

        Assert.Equal(options.MaxInputReceiptCount, console.InputCoordinator.ReceiptCount);
        ConsoleContractException reuse = Assert.Throws<ConsoleContractException>(() =>
            console.Emit(new OpenPromptOperation(new ConsolePrompt("p1", ConsoleInputType.Text))));
        Assert.Equal(ConsoleContractViolationReason.InvalidPrompt, reuse.Reason);

        console.Emit(new OpenPromptOperation(new ConsolePrompt(ConsoleInputType.Text)));
        Assert.Equal("p4", console.CurrentPrompt!.PromptId);
        ConsoleInputResult current = console.SubmitCurrentInput(new ConsoleInputAttempt("late-p1", "old"));

        Assert.Equal(ConsoleInputResultKind.Accepted, current.Kind);
        Assert.Equal("p4", current.ResolvedPromptId);
    }

    [Fact]
    public void ContinuousPrintAndPromptActivityKeepsEveryRetainedCollectionBounded()
    {
        const int operationCount = 256;
        var options = new ConsoleHistoryOptions
        {
            MaxVisibleNodes = 4,
            MaxVisibleTextLength = 32,
            MaxDeltaCount = 3,
            MaxEstimatedBytes = 1_024,
            MaxInputReceiptCount = 3
        };
        var console = new StructuredGameConsole(
            new ImmediateRuntimeClock(),
            options,
            new SequencePromptIdGenerator(Enumerable.Range(1, operationCount).Select(index => $"prompt-{index}")));

        for (int index = 0; index < operationCount; index++)
        {
            console.Emit(new AppendNodesOperation([new TextNode($"print-{index}")]));
            console.Emit(new OpenPromptOperation(new ConsolePrompt(ConsoleInputType.Text)));
            string promptId = console.CurrentPrompt!.PromptId;
            Assert.Equal(
                ConsoleInputResultKind.Accepted,
                console.SubmitCurrentInput(new ConsoleInputAttempt($"message-{index}", "ok")).Kind);

            Assert.True(console.StateStore.History.Count <= options.MaxDeltaCount);
            Assert.True(console.StateStore.Snapshot.VisibleNodeCount <= options.MaxVisibleNodes);
            Assert.True(console.StateStore.Snapshot.VisibleTextLength <= options.MaxVisibleTextLength);
            Assert.True(console.StateStore.Snapshot.EstimatedBytes <= options.MaxEstimatedBytes);
            Assert.True(console.StateStore.HistoryEstimatedBytes <= options.MaxEstimatedBytes);
            Assert.True(console.InputCoordinator.ReceiptCount <= options.MaxInputReceiptCount);
            Assert.True(console.InputCoordinator.WaiterCount <= options.MaxInputReceiptCount);
        }
    }

    private sealed class SequencePromptIdGenerator : IPromptIdGenerator
    {
        private readonly Queue<string> remaining;

        public SequencePromptIdGenerator(params string[] ids)
            : this((IEnumerable<string>)ids)
        {
        }

        public SequencePromptIdGenerator(IEnumerable<string> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            remaining = new Queue<string>(ids);
        }

        public string Next()
        {
            if (remaining.Count == 0)
            {
                throw new InvalidOperationException("The test prompt id generator is exhausted.");
            }

            return remaining.Dequeue();
        }
    }

    private sealed class ImmediateRuntimeClock : IRuntimeClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
