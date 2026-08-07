using System.Runtime.InteropServices;
using CloudEmuera.Infrastructure.Persistence;

internal static class MigratorCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out Command command, out SqliteDatabaseOptions? options))
        {
            PrintUsage();
            return MigrationExitCodes.InvalidConfiguration;
        }

        using CancellationTokenSource cancellation = new();
        using PosixSignalRegistration? sigterm = RegisterSignal(PosixSignal.SIGTERM, cancellation);
        using PosixSignalRegistration? sigint = RegisterSignal(PosixSignal.SIGINT, cancellation);
        ConsoleCancelEventHandler? cancelKeyPress = null;
        if (!OperatingSystem.IsWindows())
        {
            cancelKeyPress = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelKeyPress;
        }

        try
        {
            DatabaseMigrationRunner runner = new(
                options!,
                log: message => Console.WriteLine(message));
            MigrationResult result = command == Command.Migrate
                ? await runner.MigrateAsync(cancellation.Token).ConfigureAwait(false)
                : await runner.CheckAsync(cancellation.Token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"migrator_error code={result.ErrorCode ?? "operation_failed"} exit_code={result.ExitCode}");
            }

            return result.ExitCode;
        }
        finally
        {
            if (cancelKeyPress is not null)
            {
                Console.CancelKeyPress -= cancelKeyPress;
            }
        }
    }

    private static bool TryParse(string[] args, out Command command, out SqliteDatabaseOptions? options)
    {
        command = default;
        options = null;
        if (args.Length == 0 || !Enum.TryParse(args[0], ignoreCase: true, out command) || command is not (Command.Migrate or Command.Check))
        {
            return false;
        }

        string? dataRoot = null;
        string databaseName = SqliteStorageConventions.DatabaseFileName;
        bool databaseWasSpecified = false;
        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--data-root" when index + 1 < args.Length && dataRoot is null:
                    dataRoot = args[++index];
                    break;
                case "--database" when index + 1 < args.Length && !databaseWasSpecified:
                    databaseName = args[++index];
                    databaseWasSpecified = true;
                    break;
                case "--help" or "-h":
                    return false;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return false;
        }

        options = new SqliteDatabaseOptions
        {
            DataRoot = dataRoot,
            DatabaseName = databaseName,
        };
        return true;
    }

    private static PosixSignalRegistration? RegisterSignal(PosixSignal signal, CancellationTokenSource cancellation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: CloudEmuera.Migrator migrate|check --data-root <path> [--database <file>]");
    }

    private enum Command
    {
        Migrate,
        Check,
    }
}
