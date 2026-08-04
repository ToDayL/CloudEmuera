namespace CloudEmuera.Domain.Sessions;

public enum SessionState
{
    Creating,
    Starting,
    Running,
    Detached,
    Stopping,
    Closed,
    Crashed,
}

