using System.Runtime.InteropServices;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Sessions;

internal static class MigratorCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out Command command, out SqliteDatabaseOptions? options, out string? selectionTemplatePath))
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
            if (command == Command.PlanGameCollapse)
            {
                LegacyGameCollapseReport report = await LegacyGameCollapsePlanner.PlanAsync(options!, cancellation.Token).ConfigureAwait(false);
                Console.WriteLine(LegacyGameCollapsePlanner.SerializeReport(report));
                if (selectionTemplatePath is not null)
                {
                    string outputPath = LegacyGameCollapsePlanner.ValidateSelectionOutputPath(selectionTemplatePath);
                    await File.WriteAllTextAsync(outputPath, LegacyGameCollapsePlanner.SerializeSelectionTemplate(report), cancellation.Token).ConfigureAwait(false);
                }

                return MigrationExitCodes.Success;
            }

            if (command == Command.RebindSessionRoots)
            {
                return await SessionRootRestoreRebinder.RunAsync(options!, Console.WriteLine, cancellation.Token).ConfigureAwait(false);
            }

            DatabaseMigrationRunner runner = new(
                options!,
                log: message => Console.WriteLine(message));
            MigrationResult result = command switch
            {
                Command.Migrate => await runner.MigrateAsync(cancellation.Token).ConfigureAwait(false),
                Command.Check => await runner.CheckAsync(cancellation.Token).ConfigureAwait(false),
                Command.RepairIndexes => await runner.RepairIndexesAsync(cancellation.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported migrator command."),
            };
            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"migrator_error code={result.ErrorCode ?? "operation_failed"} exit_code={result.ExitCode}");
            }

            return result.ExitCode;
        }
        catch (LegacyGameCollapseException exception)
        {
            Console.Error.WriteLine($"migrator_error code={exception.Code} exit_code={MigrationExitCodes.MigrationFailed}");
            return MigrationExitCodes.MigrationFailed;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"migrator_error code=cancelled exit_code={MigrationExitCodes.MigrationFailed}");
            return MigrationExitCodes.MigrationFailed;
        }
        finally
        {
            if (cancelKeyPress is not null)
            {
                Console.CancelKeyPress -= cancelKeyPress;
            }
        }
    }

    private static bool TryParse(string[] args, out Command command, out SqliteDatabaseOptions? options, out string? selectionTemplatePath)
    {
        command = default;
        options = null;
        selectionTemplatePath = null;
        if (args.Length == 0)
        {
            return false;
        }

        command = args[0].ToLowerInvariant() switch
        {
            "migrate" => Command.Migrate,
            "check" => Command.Check,
            "repair-indexes" => Command.RepairIndexes,
            "plan-game-collapse" => Command.PlanGameCollapse,
            "rebind-session-roots" => Command.RebindSessionRoots,
            _ => (Command)(-1),
        };
        if (command is not (Command.Migrate or Command.Check or Command.RepairIndexes or Command.PlanGameCollapse or Command.RebindSessionRoots)) return false;

        string? dataRoot = null;
        string databaseName = SqliteStorageConventions.DatabaseFileName;
        bool databaseWasSpecified = false;
        string? gameCollapsePlan = null;
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
                case "--game-collapse-plan" when command == Command.Migrate && index + 1 < args.Length && gameCollapsePlan is null:
                    gameCollapsePlan = args[++index];
                    break;
                case "--selection-template" when command == Command.PlanGameCollapse && index + 1 < args.Length && selectionTemplatePath is null:
                    selectionTemplatePath = args[++index];
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
            GameCollapsePlanPath = gameCollapsePlan,
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
        Console.Error.WriteLine("usage: CloudEmuera.Migrator migrate|check|repair-indexes|rebind-session-roots --data-root <path> [--database <file>] [--game-collapse-plan <file>]");
        Console.Error.WriteLine("       CloudEmuera.Migrator plan-game-collapse --data-root <path> [--database <file>] [--selection-template <file>]");
    }

    private enum Command
    {
        Migrate,
        Check,
        RepairIndexes,
        PlanGameCollapse,
        RebindSessionRoots,
    }
}
