namespace CloudEmuera.Domain.Sessions;

public enum SessionState
{
    Creating,
    Starting,
    Running,
    Stopping,
    Closed,
    Crashed,
}
