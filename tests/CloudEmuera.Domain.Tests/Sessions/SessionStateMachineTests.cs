using CloudEmuera.Domain.Sessions;
using Xunit;

namespace CloudEmuera.Domain.Tests.Sessions;

public sealed class SessionStateMachineTests
{
    [Theory]
    [InlineData(SessionState.Creating, SessionState.Closed)]
    [InlineData(SessionState.Closed, SessionState.Starting)]
    [InlineData(SessionState.Crashed, SessionState.Starting)]
    [InlineData(SessionState.Starting, SessionState.Running)]
    [InlineData(SessionState.Starting, SessionState.Stopping)]
    [InlineData(SessionState.Starting, SessionState.Crashed)]
    [InlineData(SessionState.Running, SessionState.Stopping)]
    [InlineData(SessionState.Running, SessionState.Crashed)]
    [InlineData(SessionState.Stopping, SessionState.Closed)]
    [InlineData(SessionState.Stopping, SessionState.Crashed)]
    public void DocumentedTransitionsAreAllowed(SessionState current, SessionState next)
    {
        Assert.True(SessionStateMachine.CanTransition(current, next));
    }

    [Theory]
    [InlineData(SessionState.Creating, SessionState.Starting)]
    [InlineData(SessionState.Creating, SessionState.Running)]
    [InlineData(SessionState.Closed, SessionState.Closed)]
    [InlineData(SessionState.Closed, SessionState.Running)]
    [InlineData(SessionState.Running, SessionState.Closed)]
    [InlineData(SessionState.Stopping, SessionState.Running)]
    public void UnlistedTransitionsAreRejected(SessionState current, SessionState next)
    {
        Assert.False(SessionStateMachine.CanTransition(current, next));
    }

    [Theory]
    [InlineData(SessionState.Starting, true, false, false)]
    [InlineData(SessionState.Running, true, false, false)]
    [InlineData(SessionState.Stopping, true, false, false)]
    [InlineData(SessionState.Closed, false, true, true)]
    [InlineData(SessionState.Crashed, false, true, true)]
    [InlineData(SessionState.Creating, false, false, false)]
    public void StateClassificationMatchesLifecycleContract(SessionState state, bool active, bool quiescent, bool openable)
    {
        Assert.Equal(active, state.IsActive());
        Assert.Equal(quiescent, state.IsQuiescent());
        Assert.Equal(openable, state.CanOpen());
    }
}
