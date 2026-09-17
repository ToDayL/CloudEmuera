using System.Diagnostics;
using System.Text;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "P1-01 scenario names use separators for requirement mapping.")]
public sealed class MigrationProcessTests
{
    [Fact]
    [Trait("Category", "MigrationProcess")]
    public async Task RealMigratorProcess_MigratesTwiceAndChecks()
    {
        string repositoryRoot = FindRepositoryRoot();
        string migratorPath = Path.Combine(repositoryRoot, "src", "CloudEmuera.Migrator", "bin", "Release", "net10.0", "CloudEmuera.Migrator.dll");
        Assert.True(File.Exists(migratorPath), migratorPath);
        string dataRoot = Directory.CreateTempSubdirectory("cloudemuera-migrator-process-").FullName;
        try
        {
            ProcessResult first = await RunAsync(migratorPath, "migrate", dataRoot);
            ProcessResult second = await RunAsync(migratorPath, "migrate", dataRoot);
            ProcessResult repair = await RunAsync(migratorPath, "repair-indexes", dataRoot);
            ProcessResult check = await RunAsync(migratorPath, "check", dataRoot);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(0, repair.ExitCode);
            Assert.Equal(0, check.ExitCode);
            Assert.Contains("result=succeeded", first.StandardOutput);
            Assert.Contains("result=up_to_date", second.StandardOutput);
            Assert.Contains("operation=repair-indexes", repair.StandardOutput);
            Assert.Contains("operation=check", check.StandardOutput);
            Assert.DoesNotContain("Data Source=", first.StandardOutput + first.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password_hash", first.StandardOutput + first.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("response_json", first.StandardOutput + first.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "MigrationProcess")]
    public async Task RealMigratorProcess_RejectsInvalidDatabaseName()
    {
        string repositoryRoot = FindRepositoryRoot();
        string migratorPath = Path.Combine(repositoryRoot, "src", "CloudEmuera.Migrator", "bin", "Release", "net10.0", "CloudEmuera.Migrator.dll");
        string dataRoot = Directory.CreateTempSubdirectory("cloudemuera-migrator-invalid-").FullName;
        try
        {
            ProcessResult result = await RunAsync(migratorPath, "migrate", dataRoot, "../escape.db");
            Assert.Equal(10, result.ExitCode);
            Assert.Contains("invalid_configuration", result.StandardError);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "MigrationProcess")]
    public async Task RealMigratorProcesses_OnlyOneWinsCrossProcessMigrationLock()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string repositoryRoot = FindRepositoryRoot();
        string migratorPath = Path.Combine(repositoryRoot, "src", "CloudEmuera.Migrator", "bin", "Release", "net10.0", "CloudEmuera.Migrator.dll");
        Assert.True(File.Exists(migratorPath), migratorPath);
        string dataRoot = Directory.CreateTempSubdirectory("cloudemuera-migrator-lock-process-").FullName;
        RunningProcess? first = null;
        RunningProcess? second = null;
        try
        {
            string databasePath = Path.Combine(dataRoot, SqliteStorageConventions.DatabaseFileName);
            await CreateProbeDatabaseAsync(databasePath);

            ProcessResult firstResult;
            await using (SqliteConnection blocker = await OpenExclusiveBlockerAsync(databasePath))
            {
                first = Start(migratorPath, "migrate", dataRoot);
                // Let the lock holder announce ownership. A test-side TryAcquire probe can
                // briefly win the non-blocking flock race and make the real migrator exit 11.
                await WaitForLockAcquiredAsync(first);

                second = Start(migratorPath, "migrate", dataRoot);
                ProcessResult secondResult = await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(MigrationExitCodes.LockBusy, secondResult.ExitCode);
                Assert.Contains("migration_lock_busy", secondResult.StandardError);
            }

            firstResult = await first.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, firstResult.ExitCode);
            Assert.Contains("result=succeeded", firstResult.StandardOutput);
        }
        finally
        {
            if (second is not null)
            {
                await second.DisposeAsync();
            }

            if (first is not null)
            {
                await first.DisposeAsync();
            }

            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task<ProcessResult> RunAsync(string migratorPath, string operation, string dataRoot, string? databaseName = null)
    {
        await using RunningProcess running = Start(migratorPath, operation, dataRoot, databaseName);
        return await running.Completion;
    }

    private static RunningProcess Start(string migratorPath, string operation, string dataRoot, string? databaseName = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(migratorPath);
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(dataRoot);
        if (databaseName is not null)
        {
            startInfo.ArgumentList.Add("--database");
            startInfo.ArgumentList.Add(databaseName);
        }

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start migrator.");
        TaskCompletionSource lockAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> standardOutput = ReadOutputAsync(process.StandardOutput, line =>
        {
            if (line.Contains("phase=migration_lock_acquired", StringComparison.Ordinal))
            {
                lockAcquired.TrySetResult();
            }
        });
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        Task<ProcessResult> completion = CompleteAsync(process, standardOutput, standardError);
        return new RunningProcess(process, completion, lockAcquired.Task);
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<string> observeLine)
    {
        StringBuilder output = new();
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            observeLine(line);
        }

        return output.ToString();
    }

    private static async Task<ProcessResult> CompleteAsync(Process process, Task<string> standardOutput, Task<string> standardError)
    {
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task WaitForLockAcquiredAsync(RunningProcess process)
    {
        Task completed = await Task.WhenAny(process.LockAcquired, process.Completion).WaitAsync(TimeSpan.FromSeconds(5));
        if (completed == process.LockAcquired)
        {
            return;
        }

        ProcessResult result = await process.Completion;
        throw new Xunit.Sdk.XunitException($"The first migrator exited before acquiring the lock: {result.ExitCode} {result.StandardError}");
    }

    private static async Task<SqliteConnection> OpenExclusiveBlockerAsync(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync();
            await using SqliteCommand begin = connection.CreateCommand();
            begin.CommandText = "BEGIN EXCLUSIVE;";
            await begin.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task CreateProbeDatabaseAsync(string path)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CloudEmuera.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class RunningProcess(Process process, Task<ProcessResult> completion, Task lockAcquired) : IAsyncDisposable
    {
        public Process Process { get; } = process;

        public Task<ProcessResult> Completion { get; } = completion;

        public Task LockAcquired { get; } = lockAcquired;

        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited)
            {
                try
                {
                    Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the check and Kill.
                }
            }

            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch
            {
                // Cleanup must not hide the original test failure.
            }

            Process.Dispose();
        }
    }
}
