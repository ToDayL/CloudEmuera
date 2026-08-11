namespace CloudEmuera.Domain.Sessions;

public static class SessionStateMachine
{
    public static bool CanTransition(SessionState current, SessionState next) =>
        (current, next) switch
        {
            (SessionState.Creating, SessionState.Closed) => true,
            (SessionState.Closed or SessionState.Crashed, SessionState.Starting) => true,
            (SessionState.Starting, SessionState.Running or SessionState.Stopping or SessionState.Crashed) => true,
            (SessionState.Running, SessionState.Stopping or SessionState.Crashed) => true,
            (SessionState.Stopping, SessionState.Closed or SessionState.Crashed) => true,
            _ => false,
        };

    public static bool IsActive(this SessionState state) =>
        state is SessionState.Starting or SessionState.Running or SessionState.Stopping;

    public static bool IsQuiescent(this SessionState state) =>
        state is SessionState.Closed or SessionState.Crashed;

    public static bool CanOpen(this SessionState state) =>
        state is SessionState.Closed or SessionState.Crashed;

    public static bool CanClose(this SessionState state) =>
        state is SessionState.Starting or SessionState.Running;
}
