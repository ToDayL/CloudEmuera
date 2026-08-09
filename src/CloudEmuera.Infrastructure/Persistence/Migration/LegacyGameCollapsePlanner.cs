using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed record LegacyGameVersionCandidate(
    string Id,
    string Status,
    string? ContentDigest,
    string? ContentPath,
    long CreatedAt,
    long? PublishedAt);

public sealed record LegacyGameCollapseGame(
    string GameId,
    IReadOnlyList<LegacyGameVersionCandidate> CurrentCandidates,
    IReadOnlyList<LegacyGameVersionCandidate> WorkspaceCandidates,
    string? SelectedCurrentVersionId,
    string? SelectedWorkspaceVersionId,
    int SessionReferenceCount,
    string? FailureCode,
    IReadOnlyList<LegacyGameVersionCandidate>? RetiredCandidates = null);

public sealed record LegacyGameCollapseReport(
    int SchemaVersion,
    string DatabaseIdentity,
    string SchemaDigest,
    IReadOnlyList<LegacyGameCollapseGame> Games,
    string ReportDigest)
{
    public bool HasAmbiguity => Games.Any(game => game.FailureCode is not null);
}

public sealed record LegacyGameCollapseSelection(
    string GameId,
    string? CurrentVersionId,
    string? WorkspaceVersionId);

public sealed record LegacyGameCollapseSelectionFile(
    int SchemaVersion,
    string DatabaseIdentity,
    string SchemaDigest,
    string ReportDigest,
    IReadOnlyList<LegacyGameCollapseSelection> Selections);

public static class LegacyGameCollapsePlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<LegacyGameCollapseReport> PlanAsync(SqliteDatabaseOptions options, CancellationToken cancellationToken = default)
    {
        SqliteDatabasePaths paths = options.ResolvePaths(createDataRoot: false);
        await using SqliteConnection connection = new SqliteConnectionFactory(options, createDataRoot: false)
            .OpenConnection(SqliteConnectionAccess.ReadOnly);
        if (!await TableExistsAsync(connection, "game_versions", cancellationToken).ConfigureAwait(false))
            return CreateReport(DatabaseIdentity(paths), await SchemaDigestAsync(connection, cancellationToken).ConfigureAwait(false), []);

        string databaseIdentity = DatabaseIdentity(paths);
        string schemaDigest = await SchemaDigestAsync(connection, cancellationToken).ConfigureAwait(false);
        var games = new List<LegacyGameCollapseGame>();
        await using SqliteCommand gameCommand = connection.CreateCommand();
        gameCommand.CommandText = "SELECT id FROM games ORDER BY id;";
        await using SqliteDataReader gameReader = await gameCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await gameReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string gameId = gameReader.GetString(0);
            IReadOnlyList<LegacyGameVersionCandidate> candidates = await ReadCandidatesAsync(connection, gameId, cancellationToken).ConfigureAwait(false);
            LegacyGameVersionCandidate[] current = candidates.Where(value => value.Status is "PUBLISHED" or "BLOCKED").ToArray();
            LegacyGameVersionCandidate[] workspace = candidates.Where(value => value.Status is "DRAFT" or "VALIDATING").ToArray();
            LegacyGameVersionCandidate[] retired = candidates.Where(value => value.Status == "DELETED").ToArray();
            string? currentSelection = SelectCandidate(current, out string? currentFailure);
            string? workspaceSelection = SelectCandidate(workspace, out string? workspaceFailure);
            int sessionReferences = await ScalarIntAsync(connection,
                "SELECT COUNT(*) FROM sessions WHERE game_version_id IN (SELECT id FROM game_versions WHERE game_id = $game);",
                gameId, cancellationToken).ConfigureAwait(false);
            games.Add(new LegacyGameCollapseGame(gameId, current, workspace, currentSelection, workspaceSelection,
                sessionReferences, currentFailure ?? workspaceFailure, retired));
        }
        return CreateReport(databaseIdentity, schemaDigest, games);
    }

    public static async Task<IReadOnlyList<LegacyGameCollapseSelection>> LoadAndValidateSelectionsAsync(
        string selectionPath,
        LegacyGameCollapseReport report,
        CancellationToken cancellationToken = default)
    {
        selectionPath = Path.GetFullPath(selectionPath);
        SqlitePathSecurity.EnsureNoSymlinkAncestors(selectionPath);
        SqlitePathSecurity.ValidateOptionalFile(selectionPath, "legacy collapse selection file");
        await using FileStream stream = new(selectionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
        LegacyGameCollapseSelectionFile? file = await JsonSerializer.DeserializeAsync<LegacyGameCollapseSelectionFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (file is null || file.SchemaVersion != report.SchemaVersion
            || file.DatabaseIdentity != report.DatabaseIdentity || file.SchemaDigest != report.SchemaDigest
            || file.ReportDigest != report.ReportDigest)
            throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_PLAN_STALE");

        Dictionary<string, LegacyGameCollapseSelection> selections = file.Selections
            .GroupBy(value => value.GameId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var validated = new List<LegacyGameCollapseSelection>(report.Games.Count);
        foreach (LegacyGameCollapseGame game in report.Games)
        {
            if (!selections.TryGetValue(game.GameId, out LegacyGameCollapseSelection? selection))
                throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_SELECTION_MISSING");
            ValidateSelection(game, selection);
            validated.Add(selection);
        }
        if (selections.Count != report.Games.Count) throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_SELECTION_EXTRA");
        return validated;
    }

    public static IReadOnlyList<LegacyGameCollapseSelection> AutomaticSelections(LegacyGameCollapseReport report)
    {
        if (report.HasAmbiguity) throw new LegacyGameCollapseException(report.Games.First(game => game.FailureCode is not null).FailureCode!);
        return report.Games.Select(game => new LegacyGameCollapseSelection(game.GameId, game.SelectedCurrentVersionId, game.SelectedWorkspaceVersionId)).ToArray();
    }

    public static string SerializeReport(LegacyGameCollapseReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static string ValidateSelectionOutputPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        SqlitePathSecurity.EnsureNoSymlinkAncestors(fullPath);
        SqlitePathSecurity.ValidateOptionalFile(fullPath, "legacy collapse selection template");
        return fullPath;
    }

    public static string SerializeSelectionTemplate(LegacyGameCollapseReport report) => JsonSerializer.Serialize(
        new LegacyGameCollapseSelectionFile(report.SchemaVersion, report.DatabaseIdentity, report.SchemaDigest, report.ReportDigest,
            report.Games.Select(game => new LegacyGameCollapseSelection(game.GameId, game.SelectedCurrentVersionId, game.SelectedWorkspaceVersionId)).ToArray()), JsonOptions);

    private static void ValidateSelection(LegacyGameCollapseGame game, LegacyGameCollapseSelection selection)
    {
        if (!string.Equals(game.GameId, selection.GameId, StringComparison.Ordinal)) throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_SELECTION_GAME_MISMATCH");
        if (selection.CurrentVersionId is not null)
        {
            LegacyGameVersionCandidate? current = game.CurrentCandidates.FirstOrDefault(value => value.Id == selection.CurrentVersionId);
            if (current is null || current.ContentDigest is null) throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_SELECTION_INVALID");
        }
        if (selection.WorkspaceVersionId is not null)
        {
            LegacyGameVersionCandidate? workspace = game.WorkspaceCandidates.FirstOrDefault(value => value.Id == selection.WorkspaceVersionId);
            if (workspace is null || workspace.ContentDigest is null) throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_SELECTION_INVALID");
        }
        if (game.CurrentCandidates.Count != 0 && selection.CurrentVersionId is null)
            throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_CURRENT_REQUIRED");
        if (game.WorkspaceCandidates.Count != 0 && selection.WorkspaceVersionId is null)
            throw new LegacyGameCollapseException("LEGACY_GAME_COLLAPSE_WORKSPACE_REQUIRED");
    }

    private static string? SelectCandidate(LegacyGameVersionCandidate[] candidates, out string? failure)
    {
        failure = null;
        if (candidates.Length == 0) return null;
        if (candidates.Any(value => value.ContentDigest is null))
        {
            failure = candidates.Any(value => value.Status is "PUBLISHED" or "BLOCKED")
                ? "LEGACY_GAME_CURRENT_INVALID" : "LEGACY_GAME_WORKSPACE_INVALID";
            return null;
        }
        if (candidates.Select(value => value.ContentDigest).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            failure = candidates.Any(value => value.Status is "PUBLISHED" or "BLOCKED")
                ? "LEGACY_GAME_CURRENT_AMBIGUOUS" : "LEGACY_GAME_WORKSPACE_AMBIGUOUS";
            return null;
        }
        return candidates.OrderByDescending(value => value.PublishedAt ?? value.CreatedAt)
            .ThenByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id, StringComparer.Ordinal)
            .First().Id;
    }

    private static async Task<IReadOnlyList<LegacyGameVersionCandidate>> ReadCandidatesAsync(SqliteConnection connection, string gameId, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,status,content_digest,content_path,manifest_json,runtime_config_json,compatibility_summary_json,created_at,published_at
            FROM game_versions WHERE game_id = $game ORDER BY id;
            """;
        command.Parameters.AddWithValue("$game", gameId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var values = new List<LegacyGameVersionCandidate>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        return values;
    }

    private static LegacyGameCollapseReport CreateReport(string databaseIdentity, string schemaDigest, IReadOnlyList<LegacyGameCollapseGame> games)
    {
        var report = new LegacyGameCollapseReport(1, databaseIdentity, schemaDigest, games, string.Empty);
        string canonical = JsonSerializer.Serialize(report, JsonOptions);
        return report with { ReportDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}" };
    }

    private static string DatabaseIdentity(SqliteDatabasePaths paths)
    {
        if (!OperatingSystem.IsLinux()) return $"path:{Path.GetFileName(paths.DatabasePath)}";
        using SafeFileHandle parent = LinuxFileOperations.OpenDirectory(paths.DataRoot);
        using SafeFileHandle database = LinuxFileOperations.OpenRegularFileAt(parent, Path.GetFileName(paths.DatabasePath), readOnly: true, create: false, exclusive: false);
        LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(database);
        return $"{identity.DeviceMajor}:{identity.DeviceMinor}:{identity.Inode}";
    }

    private static async Task<string> SchemaDigestAsync(SqliteConnection connection, CancellationToken token)
    {
        var ids = new List<string>();
        if (await TableExistsAsync(connection, "schema_migrations", token).ConfigureAwait(false))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM schema_migrations ORDER BY MigrationId;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        }
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids)))).ToLowerInvariant()}";
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, string gameId, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$game", gameId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class LegacyGameCollapseException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
