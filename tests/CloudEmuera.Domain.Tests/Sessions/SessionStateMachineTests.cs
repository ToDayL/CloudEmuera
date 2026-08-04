using CloudEmuera.Domain.Sessions;
using Xunit;

namespace CloudEmuera.Domain.Tests.Sessions;

public sealed class SessionStateMachineTests
{
    [Fact]
    public void ClosedIsATerminalState()
    {
        foreach (var next in Enum.GetValues<SessionState>())
        {
            Assert.False(SessionStateMachine.CanTransition(SessionState.Closed, next));
        }
    }

    [Theory]
    [InlineData(SessionState.Running, SessionState.Detached)]
    [InlineData(SessionState.Detached, SessionState.Running)]
    [InlineData(SessionState.Stopping, SessionState.Closed)]
    public void DocumentedTransitionsAreAllowed(SessionState current, SessionState next)
    {
        Assert.True(SessionStateMachine.CanTransition(current, next));
    }
}
