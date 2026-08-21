namespace CloudEmuera.Api.Workers;

internal static class WorkerShutdownDefaults
{
    public static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PersistenceTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ComposeStopGracePeriod = TimeSpan.FromSeconds(20);
}
