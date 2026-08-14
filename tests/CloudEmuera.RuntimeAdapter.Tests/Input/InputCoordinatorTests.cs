using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Input;

[Trait("Category", "ConsoleContract")]
public sealed class InputCoordinatorTests
{
    [Fact]
    public void InvalidIntegerDoesNotClosePromptAndAcceptedInputDoes()
    {
        var coordinator = new InputCoordinator();
        coordinator.OpenPrompt(new ConsolePrompt("p1", ConsoleInputType.Integer));

        ConsoleInputResult invalid = coordinator.Submit(new ConsoleInputCommand("p1", "m1", "not-an-int"));
        Assert.Equal(ConsoleInputResultKind.InvalidFormat, invalid.Kind);
        Assert.NotNull(coordinator.CurrentPrompt);

        ConsoleInputResult accepted = coordinator.Submit(new ConsoleInputCommand("p1", "m2", "42"));
        Assert.Equal(ConsoleInputResultKind.Accepted, accepted.Kind);
        Assert.Equal("42", accepted.Input!.Value);
        Assert.Null(coordinator.CurrentPrompt);
    }

    [Fact]
    public void DuplicateAndConflictResultsAreDeterministic()
    {
        var coordinator = new InputCoordinator();
        coordinator.OpenPrompt(new ConsolePrompt("p1", ConsoleInputType.Text));
        var command = new ConsoleInputCommand("p1", "m1", "hello");

        ConsoleInputResult accepted = coordinator.Submit(command);
        ConsoleInputResult duplicate = coordinator.Submit(command);
        ConsoleInputResult conflict = coordinator.Submit(new ConsoleInputCommand("p1", "m1", "different"));

        Assert.Equal(ConsoleInputResultKind.Accepted, accepted.Kind);
        Assert.Equal(ConsoleInputResultKind.Duplicate, duplicate.Kind);
        Assert.Equal(accepted.Kind, duplicate.OriginalResult!.Kind);
        Assert.Equal(ConsoleInputResultKind.MessageConflict, conflict.Kind);
    }

    [Fact]
    [Trait("Category", "InputDeduplication")]
    public async Task OnlyFirstConcurrentValidMessageCanWin()
    {
        var coordinator = new InputCoordinator();
        coordinator.OpenPrompt(new ConsolePrompt("p1", ConsoleInputType.Text));
        var barrier = new Barrier(3);
        ConsoleInputResult? first = null;
        ConsoleInputResult? second = null;

        Task one = Task.Run(() =>
        {
            barrier.SignalAndWait();
            first = coordinator.Submit(new ConsoleInputCommand("p1", "m1", "one"));
        });
        Task two = Task.Run(() =>
        {
            barrier.SignalAndWait();
            second = coordinator.Submit(new ConsoleInputCommand("p1", "m2", "two"));
        });
        barrier.SignalAndWait();
        await Task.WhenAll(one, two);

        Assert.True(
            new[] { first, second }.Count(result => result!.Kind == ConsoleInputResultKind.Accepted) == 1,
            $"first={first?.Kind}, second={second?.Kind}");
        Assert.True(
            new[] { first, second }.Count(result => result!.Kind == ConsoleInputResultKind.StalePrompt) == 1,
            $"first={first?.Kind}, second={second?.Kind}");
    }

    [Fact]
    public void InvalidIdentifierUsesIdentifierFailureReason()
    {
        var options = new ConsoleHistoryOptions
        {
            ContractLimits = new ConsoleContractLimits
            {
                MaxPromptIdLength = 3,
                MaxClientMessageIdLength = 3
            }
        };
        var coordinator = new InputCoordinator(options);
        coordinator.OpenPrompt(new ConsolePrompt("p1", ConsoleInputType.Text));

        ConsoleInputResult result = coordinator.Submit(new ConsoleInputCommand("p1", "m-too-long", "ok"));

        Assert.Equal(ConsoleInputResultKind.InvalidCommand, result.Kind);
        Assert.Equal(ConsoleInputFailureReason.InvalidIdentifier, result.FailureReason);
        Assert.Equal("InvalidIdentifier", result.ReasonCode);
        Assert.NotNull(coordinator.CurrentPrompt);
    }

    [Fact]
    public async Task ConcurrentInvalidAndValidInputLeavesValidInputAsTheWinner()
    {
        var coordinator = new InputCoordinator();
        coordinator.OpenPrompt(new ConsolePrompt("p1", ConsoleInputType.Text, constraints: new TextInputConstraints(2)));
        var barrier = new Barrier(3);
        ConsoleInputResult? invalid = null;
        ConsoleInputResult? valid = null;

        Task invalidTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            invalid = coordinator.Submit(new ConsoleInputCommand("p1", "invalid", "too-long"));
        });
        Task validTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            valid = coordinator.Submit(new ConsoleInputCommand("p1", "valid", "ok"));
        });
        barrier.SignalAndWait();
        await Task.WhenAll(invalidTask, validTask);

        Assert.Equal(ConsoleInputResultKind.Accepted, valid!.Kind);
        Assert.Contains(
            invalid!.Kind,
            new[]
            {
                ConsoleInputResultKind.InvalidFormat,
                ConsoleInputResultKind.StalePrompt
            });
        if (invalid.Kind == ConsoleInputResultKind.InvalidFormat)
        {
            Assert.Equal(ConsoleInputFailureReason.ValueTooLong, invalid.FailureReason);
        }

        Assert.Null(coordinator.CurrentPrompt);
    }
}
