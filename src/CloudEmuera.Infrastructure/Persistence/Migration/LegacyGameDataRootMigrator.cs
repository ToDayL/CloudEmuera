using System.Text.Json;
using CloudEmuera.Infrastructure.Games;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

/// <summary>
/// Converts the legacy per-version content directories without deleting the old
/// tree until the corresponding database migration has committed.
/// </summary>
public static class LegacyGameDataRootMigrator
{
    private const string CompletionStatus = "COMPLETED";
    private const string PreparedStatus = "PREPARED";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task PrepareAsync(
        SqliteDatabaseOptions options,
        LegacyGameCollapseReport report,
        string migrationId,
        CancellationToken cancellationToken = default)
    {
        ValidateMigrationId(migrationId);
        SqliteDatabasePaths paths = options.ResolvePaths(createDataRoot: true);
        List<LegacyGamePhysicalEntry> entries = BuildEntries(report, migrationId);
        if (entries.Count == 0) return;
        Dictionary<string, string> owners = await ReadGameOwnersAsync(options, cancellationToken).ConfigureAwait(false);
        string journalDirectory = JournalDirectory(paths.DataRoot, migrationId);
        Directory.CreateDirectory(journalDirectory);
        SqlitePathSecurity.EnsureNoSymlinkAncestors(journalDirectory);
        SetPrivateMode(journalDirectory);

        foreach (LegacyGameCollapseGame game in report.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LegacyGameVersionCandidate? current = game.CurrentCandidates.FirstOrDefault(value => value.Id == game.SelectedCurrentVersionId);
            LegacyGameVersionCandidate? workspace = game.WorkspaceCandidates.FirstOrDefault(value => value.Id == game.SelectedWorkspaceVersionId);
            string ownerUserId = owners.GetValueOrDefault(game.GameId) ?? "usr_migration";
            if (current is not null) await PrepareSelectedAsync(paths.DataRoot, game.GameId, ownerUserId, current, "CURRENT", migrationId, cancellationToken).ConfigureAwait(false);
            if (workspace is not null) await PrepareSelectedAsync(paths.DataRoot, game.GameId, ownerUserId, workspace, "WORKSPACE", migrationId, cancellationToken).ConfigureAwait(false);
        }

        LegacyGamePhysicalJournal journal = new(1, migrationId, report.ReportDigest, PreparedStatus, entries);
        await WriteJsonAsync(Path.Combine(journalDirectory, "journal.json"), journal, cancellationToken).ConfigureAwait(false);
    }

    public static async Task FinalizeAsync(
        SqliteDatabaseOptions options,
        string migrationId,
        CancellationToken cancellationToken = default)
    {
        ValidateMigrationId(migrationId);
        SqliteDatabasePaths paths = options.ResolvePaths(createDataRoot: false);
        string journalPath = Path.Combine(JournalDirectory(paths.DataRoot, migrationId), "journal.json");
        if (!File.Exists(journalPath)) return;

        LegacyGamePhysicalJournal journal = await ReadJsonAsync<LegacyGamePhysicalJournal>(journalPath, cancellationToken).ConfigureAwait(false)
            ?? throw new LegacyGameCollapseException("LEGACY_GAME_PHYSICAL_JOURNAL_INVALID");
        if (journal.Status == CompletionStatus) return;
        if (!string.Equals(journal.MigrationId, migrationId, StringComparison.Ordinal))
            throw new LegacyGameCollapseException("LEGACY_GAME_PHYSICAL_JOURNAL_MISMATCH");

        await EnsureDatabaseCommittedAsync(options, journal, cancellationToken).ConfigureAwait(false);
        HashSet<string> protectedTargets = journal.Entries
            .Where(value => value.Role is "CURRENT" or "WORKSPACE")
            .Select(value => value.TargetPath)
            .ToHashSet(StringComparer.Ordinal);
        List<LegacyGamePhysicalEntry> completed = new(journal.Entries.Count);
        foreach (LegacyGamePhysicalEntry entry in journal.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed.Add(await MoveToBackupAsync(paths.DataRoot, entry, migrationId, protectedTargets, cancellationToken).ConfigureAwait(false));
        }

        string journalDirectory = JournalDirectory(paths.DataRoot, migrationId);
        LegacyGamePhysicalJournal final = journal with { Status = CompletionStatus, Entries = completed };
        await WriteJsonAsync(Path.Combine(journalDirectory, "journal.json"), final, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(journalDirectory, "completion.json"), final, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PrepareSelectedAsync(
        string dataRoot,
        string gameId,
        string ownerUserId,
        LegacyGameVersionCandidate candidate,
        string role,
        string migrationId,
        CancellationToken cancellationToken)
    {
        if (candidate.ContentPath is null || candidate.ContentDigest is null)
            throw new LegacyGameCollapseException(role == "CURRENT" ? "LEGACY_GAME_CURRENT_INVALID" : "LEGACY_GAME_WORKSPACE_INVALID");

        string sourcePath = NormalizeRelativePath(candidate.ContentPath);
        string targetPath = $"games/{gameId}/{(role == "CURRENT" ? "content" : "workspace")}";
        string source = SafeAbsolute(dataRoot, sourcePath);
        string target = SafeAbsolute(dataRoot, targetPath);
        string gameDirectory = SafeAbsolute(dataRoot, $"games/{gameId}");
        bool sourceExists = Directory.Exists(source);
        bool targetExists = Directory.Exists(target);
        if (!sourceExists && !targetExists)
        {
            if (IsEmptyLegacyGameDirectory(gameDirectory)) return;
            throw new LegacyGameCollapseException("LEGACY_GAME_CONTENT_MISSING");
        }

        EnsureGameOwnerMarker(gameDirectory, gameId, ownerUserId);
        if (targetExists)
        {
            if (role == "CURRENT") MakeReadOnly(target);
            return;
        }

        if (!OperatingSystem.IsLinux()) throw new LegacyGameCollapseException("LEGACY_GAME_PHYSICAL_MIGRATION_UNSUPPORTED");
        using SafeFileHandle dataRootHandle = LinuxFileOperations.OpenDirectory(dataRoot);
        using SafeFileHandle sourceHandle = LinuxFileOperations.OpenDirectoryPath(dataRootHandle, sourcePath, create: false);
        ValidateLegacyDirectory(sourceHandle);
        using SafeFileHandle gameDirectoryHandle = LinuxFileOperations.OpenDirectory(gameDirectory);
        string stagingName = $".migration-{StableLeaf(migrationId, gameId, role)}";
        string stagingPath = Path.Combine(gameDirectory, stagingName);
        using (SafeFileHandle? existingStaging = LinuxFileOperations.TryOpenDirectoryAt(gameDirectoryHandle, stagingName))
        {
            if (existingStaging is null)
                LinuxFileOperations.CopyTree(sourceHandle, gameDirectoryHandle, stagingName, syncToDisk: true);
        }

        MakeReadOnlyIfNeeded(stagingPath, role);
        using SafeFileHandle? existingTarget = LinuxFileOperations.TryOpenDirectoryAt(gameDirectoryHandle, Path.GetFileName(target));
        if (existingTarget is null)
        {
            LinuxFileOperations.RenameAt(gameDirectoryHandle, stagingName, Path.GetFileName(target));
            LinuxFileOperations.Sync(gameDirectoryHandle);
        }
        else
        {
            LinuxFileOperations.TryDeleteTreeAt(gameDirectoryHandle, stagingName, allowReadOnly: true);
        }
    }

    private static async Task<LegacyGamePhysicalEntry> MoveToBackupAsync(
        string dataRoot,
        LegacyGamePhysicalEntry entry,
        string migrationId,
        HashSet<string> protectedTargets,
        CancellationToken cancellationToken)
    {
        if (entry.SourcePath is null || string.Equals(entry.SourcePath, entry.TargetPath, StringComparison.Ordinal)
            || protectedTargets.Contains(entry.SourcePath))
            return entry with { Status = "TARGET" };

        string source = SafeAbsolute(dataRoot, entry.SourcePath);
        string backupRelative = entry.BackupPath ?? $"backups/legacy-game-content/{migrationId}/{entry.GameId}/{entry.VersionId}-{entry.Role}";
        string backup = SafeAbsolute(dataRoot, backupRelative);
        if (Directory.Exists(backup)) return entry with { Status = "BACKED_UP", BackupPath = backupRelative };
        if (!Directory.Exists(source)) return entry with { Status = "MISSING", BackupPath = backupRelative };
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new LegacyGameCollapseException("LEGACY_GAME_PHYSICAL_MIGRATION_UNSUPPORTED");

        string? sourceParentPath = Path.GetDirectoryName(source);
        string? backupParentPath = Path.GetDirectoryName(backup);
        if (sourceParentPath is null || backupParentPath is null) throw new LegacyGameCollapseException("LEGACY_GAME_CONTENT_PATH_INVALID");
        Directory.CreateDirectory(backupParentPath);
        SetPrivateMode(backupParentPath);
        using SafeFileHandle dataRootHandle = LinuxFileOperations.OpenDirectory(dataRoot);
        using SafeFileHandle sourceParent = LinuxFileOperations.OpenDirectoryPath(dataRootHandle, Relative(dataRoot, sourceParentPath), create: false);
        using SafeFileHandle sourceDirectory = LinuxFileOperations.OpenDirectory(source);
        ValidateLegacyDirectory(sourceDirectory);
        using SafeFileHandle backupParent = LinuxFileOperations.OpenDirectoryPath(dataRootHandle, Relative(dataRoot, backupParentPath), create: true);
        LinuxFileOperations.RenameBetweenDirectories(sourceParent, Path.GetFileName(source), backupParent, Path.GetFileName(backup));
        LinuxFileOperations.Sync(sourceParent);
        LinuxFileOperations.Sync(backupParent);
        return entry with { Status = "BACKED_UP", BackupPath = backupRelative };
    }

    private static async Task EnsureDatabaseCommittedAsync(
        SqliteDatabaseOptions options,
        LegacyGamePhysicalJournal journal,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new SqliteConnectionFactory(options, createDataRoot: false)
            .OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using SqliteCommand table = connection.CreateCommand();
        table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='game_versions' LIMIT 1;";
        if (await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new LegacyGameCollapseException("LEGACY_GAME_PHYSICAL_DB_NOT_COMMITTED");

        foreach (LegacyGamePhysicalEntry entry in journal.Entries.Where(value => value.Role is "CURRENT" or "WORKSPACE"))
        {
            await using SqliteCommand command = connection.CreateCommand();
            string column = entry.Role == "CURRENT" ? "current_content_path" : "workspace_path";
            command.CommandText = $"SELECT COUNT(*) FROM games WHERE id=$game AND {column}=$path;";
            command.Parameters.AddWithValue("$game", entry.GameId);
            command.Parameters.AddWithValue("$path", entry.TargetPath);
            long count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count != 1) throw new LegacyGameCollapseException("LEGACY_GAME_PHYSICAL_DB_NOT_COMMITTED");
        }
    }

    private static List<LegacyGamePhysicalEntry> BuildEntries(LegacyGameCollapseReport report, string migrationId)
    {
        var entries = new List<LegacyGamePhysicalEntry>();
        foreach (LegacyGameCollapseGame game in report.Games)
        {
            AddCandidates(game, game.CurrentCandidates, game.SelectedCurrentVersionId, "CURRENT", migrationId, entries);
            AddCandidates(game, game.WorkspaceCandidates, game.SelectedWorkspaceVersionId, "WORKSPACE", migrationId, entries);
            AddCandidates(game, game.RetiredCandidates ?? [], null, "LEGACY", migrationId, entries);
        }
        return entries;
    }

    private static void AddCandidates(
        LegacyGameCollapseGame game,
        IReadOnlyList<LegacyGameVersionCandidate> candidates,
        string? selectedId,
        string role,
        string migrationId,
        ICollection<LegacyGamePhysicalEntry> entries)
    {
        foreach (LegacyGameVersionCandidate candidate in candidates)
        {
            string candidateRole = candidate.Id == selectedId ? role : "LEGACY";
            if (entries.Any(entry => entry.GameId == game.GameId && entry.VersionId == candidate.Id)) continue;
            entries.Add(CreateEntry(game.GameId, candidate, candidateRole, migrationId));
        }
    }

    private static LegacyGamePhysicalEntry CreateEntry(string gameId, LegacyGameVersionCandidate candidate, string role, string migrationId)
    {
        string target = role == "CURRENT"
            ? $"games/{gameId}/content"
            : role == "WORKSPACE" ? $"games/{gameId}/workspace" : $"games/{gameId}/legacy/{candidate.Id}";
        string? source = candidate.ContentPath is null ? null : NormalizeRelativePath(candidate.ContentPath);
        string backup = $"backups/legacy-game-content/{migrationId}/{gameId}/{candidate.Id}-{role}";
        return new(gameId, candidate.Id, role, source, target, candidate.ContentDigest, "PENDING", backup);
    }

    private static bool IsEmptyLegacyGameDirectory(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return true;
        FileSystemInfo[] entries = new DirectoryInfo(gameDirectory).EnumerateFileSystemInfos().ToArray();
        return entries.All(value => value.Name is "owner.json" or ".mutation.lock");
    }

    private static void EnsureGameOwnerMarker(string gameDirectory, string gameId, string ownerUserId)
    {
        Directory.CreateDirectory(gameDirectory);
        SqlitePathSecurity.EnsureNoSymlinkAncestors(gameDirectory);
        using SafeFileHandle directory = LinuxFileOperations.OpenDirectory(gameDirectory);
        using SafeFileHandle? marker = LinuxFileOperations.TryOpenRegularFileAt(directory, "owner.json", readOnly: true);
        if (marker is null)
            GameStorageOwnerMarker.Initialize(gameDirectory, gameId, ownerUserId);
        else
            GameStorageOwnerMarker.Validate(gameDirectory, gameId);
    }

    private static async Task<Dictionary<string, string>> ReadGameOwnersAsync(SqliteDatabaseOptions options, CancellationToken cancellationToken)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        await using SqliteConnection connection = new SqliteConnectionFactory(options, createDataRoot: false)
            .OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, owner_user_id FROM games;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            owners[reader.GetString(0)] = reader.GetString(1);
        return owners;
    }

    private static void ValidateLegacyDirectory(SafeFileHandle directory)
    {
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(directory);
        if (!identity.IsDirectory || identity.UserId != LinuxFileOperations.CurrentUserId || (identity.Mode & 0x12) != 0)
            throw new LegacyGameCollapseException("LEGACY_GAME_CONTENT_UNSAFE");
    }

    private static void MakeReadOnlyIfNeeded(string path, string role)
    {
        if (role == "CURRENT") MakeReadOnly(path);
    }

    private static void MakeReadOnly(string root)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Append(root))
            File.SetUnixFileMode(file, UnixFileMode.UserRead);
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static string JournalDirectory(string dataRoot, string migrationId) =>
        Path.Combine(dataRoot, "backups", "legacy-game-content", migrationId);

    private static string StableLeaf(string migrationId, string gameId, string role) =>
        $"{migrationId.Replace('_', '-')}-{gameId.Replace('_', '-')}-{role.ToLowerInvariant()}";

    private static string NormalizeRelativePath(string value)
    {
        string normalized = value.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0'))
            throw new LegacyGameCollapseException("LEGACY_GAME_CONTENT_PATH_INVALID");
        string[] segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new LegacyGameCollapseException("LEGACY_GAME_CONTENT_PATH_INVALID");
        return string.Join('/', segments);
    }

    private static string SafeAbsolute(string dataRoot, string relative)
    {
        string fullRoot = Path.GetFullPath(dataRoot);
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new LegacyGameCollapseException("LEGACY_GAME_CONTENT_PATH_INVALID");
        SqlitePathSecurity.EnsureNoSymlinkAncestors(full);
        return full;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)).Replace('\\', '/');

    private static void SetPrivateMode(string path)
    {
        if (OperatingSystem.IsLinux() && Directory.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = $"{path}.part-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateMigrationId(string migrationId)
    {
        if (migrationId.Length is < 1 or > 80 || migrationId.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('_' or '-')))
            throw new LegacyGameCollapseException("LEGACY_GAME_MIGRATION_ID_INVALID");
    }

    private sealed record LegacyGamePhysicalJournal(
        int SchemaVersion,
        string MigrationId,
        string ReportDigest,
        string Status,
        List<LegacyGamePhysicalEntry> Entries);

    private sealed record LegacyGamePhysicalEntry(
        string GameId,
        string VersionId,
        string Role,
        string? SourcePath,
        string TargetPath,
        string? ContentDigest,
        string Status,
        string? BackupPath);
}
