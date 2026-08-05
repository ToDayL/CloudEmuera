using System.Runtime.InteropServices;
using CloudEmuera.Supervisor;

return await SupervisorProcess.RunAsync(args).ConfigureAwait(false);

internal static class SupervisorProcess
{
    public static async Task<int> RunAsync(string[] args)
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), "cloudemuera-supervisor");
        string workerAssembly = Path.Combine(AppContext.BaseDirectory, "CloudEmuera.Worker.dll");
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--runtime-directory" when index + 1 < args.Length:
                    runtimeDirectory = args[++index];
                    break;
                case "--worker-assembly" when index + 1 < args.Length:
                    workerAssembly = args[++index];
                    break;
                default:
                    Console.Error.WriteLine("supervisor_error code=invalid_arguments");
                    return 2;
            }
        }

        using var cancellation = new CancellationTokenSource();
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(
            PosixSignal.SIGINT,
            context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });

        try
        {
            await using SupervisorHost host = await SupervisorHost.StartAsync(
                    new SupervisorOptions(runtimeDirectory, workerAssembly),
                    cancellation.Token)
                .ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("supervisor_error code=start_failed");
            return 1;
        }
    }
}
