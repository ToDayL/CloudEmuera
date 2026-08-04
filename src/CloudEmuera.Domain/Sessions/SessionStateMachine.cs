namespace CloudEmuera.Domain.Sessions;

public static class SessionStateMachine
{
    public static bool CanTransition(SessionState current, SessionState next) =>
        (current, next) switch
        {
            (SessionState.Creating, SessionState.Starting) => true,
            (SessionState.Starting, SessionState.Running or SessionState.Detached or SessionState.Stopping or SessionState.Crashed) => true,
            (SessionState.Running, SessionState.Detached or SessionState.Stopping or SessionState.Crashed) => true,
            (SessionState.Detached, SessionState.Running or SessionState.Stopping or SessionState.Crashed) => true,
            (SessionState.Stopping, SessionState.Closed) => true,
            _ => false,
        };
}

