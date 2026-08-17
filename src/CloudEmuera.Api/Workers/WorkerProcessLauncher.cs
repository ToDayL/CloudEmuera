using System.Collections.Concurrent;
using System.Diagnostics;

namespace CloudEmuera.Api.Workers;

/// <summary>
/// Starts every Worker from one long-lived native thread.
///
/// Linux PR_SET_PDEATHSIG tracks the particular thread that created a child,
/// not merely the parent process. Starting a Worker on an ordinary managed
/// thread-pool thread therefore makes thread-pool retirement look like API
/// death and the kernel sends the configured SIGKILL to a healthy Worker.
/// Keep this thread alive for the complete WorkerManager lifetime so the
/// parent-death guard instead represents the API lifetime as intended.
/// </summary>
internal sealed class WorkerProcessLauncher : IDisposable
{
    private readonly BlockingCollection<LaunchRequest> requests = new();
    private readonly Thread thread;
    private int disposed;

    public WorkerProcessLauncher()
    {
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CloudEmuera Worker Launcher"
        };
        thread.Start();
    }

    public Process Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var request = new LaunchRequest(startInfo);
        try
        {
            requests.Add(request);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(WorkerProcessLauncher));
        }
        return request.Completion.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        requests.CompleteAdding();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The Worker process launcher thread did not stop.");
        requests.Dispose();
    }

    private void Run()
    {
        foreach (LaunchRequest request in requests.GetConsumingEnumerable())
        {
            try
            {
                var process = new Process
                {
                    StartInfo = request.StartInfo,
                    EnableRaisingEvents = true
                };
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("The Worker process could not be started.");
                }
                request.Completion.TrySetResult(process);
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    private sealed class LaunchRequest(ProcessStartInfo startInfo)
    {
        public ProcessStartInfo StartInfo { get; } = startInfo;

        public TaskCompletionSource<Process> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
