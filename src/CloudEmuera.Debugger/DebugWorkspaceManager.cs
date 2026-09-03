using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEmuera.Debugging.Contracts;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Debugger;

internal sealed record DebugWorkspaceDocument(
    int Version,
    string SessionId,
    DateTimeOffset CreatedAt,
    string SeedSessionRoot,
    string SeedSourceProvenance,
    string SeedTreeDigest);

internal sealed record PreparedDebugWorkspace(
    string WorkspacePath,
    string RootPath,
    string ReplayPath,
    string OutputPath,
    bool SourceModified,
    DebugWorkspaceDocument Document);

internal static class DebugWorkspaceManager
{
    // Large game packages may contain generated/resource trees well above one
    // hundred thousand files; the byte cap remains the primary workspace bound.
    private const int MaxFiles = 250_000;
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;

    public static PreparedDebugWorkspace Prepare(
        string workspacePath,
        string sessionRoot,
        string snapshotPath,
        DebugTraceHeader header,
        string? outputPath,
        bool reset,
        bool allowCaptureMismatch)
    {
        string workspace = Full(workspacePath);
        string source = Full(sessionRoot);
        string snapshot = Full(snapshotPath);
        ValidateCapture(snapshot, header.CaptureId, allowCaptureMismatch);

        string markerPath = Path.Combine(workspace, "workspace.json");
        string root = Path.Combine(workspace, "root");
        if (reset && Directory.Exists(workspace))
            DeleteValidatedWorkspace(workspace, header.SessionId);

        DebugWorkspaceDocument document;
        if (!Directory.Exists(workspace))
        {
            EnsureNormalDirectory(source);
            Directory.CreateDirectory(workspace);
            SetPrivate(workspace, directory: true);
            CopyTree(source, root);
            string seedDigest = ComputeSourceDigest(root, header.SaveLayout);
            document = new DebugWorkspaceDocument(
                1,
                header.SessionId,
                DateTimeOffset.UtcNow,
                source,
                header.SessionRootManifestDigest ?? string.Empty,
                seedDigest);
            WriteJson(markerPath, document);
        }
        else
        {
            EnsureNormalDirectory(workspace);
            if (!File.Exists(markerPath) || !Directory.Exists(root))
                throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace marker or root is missing.");
            document = JsonSerializer.Deserialize<DebugWorkspaceDocument>(File.ReadAllText(markerPath), DebugTraceJson.Options)
                ?? throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace marker is invalid.");
            if (document.Version != 1 || !string.Equals(document.SessionId, header.SessionId, StringComparison.Ordinal))
                throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace belongs to another Session.");
        }

        RestoreSaveSnapshot(root, snapshot, header.SaveLayout);
        string replay = Path.Combine(workspace, "replay");
        Directory.CreateDirectory(replay);
        SetPrivate(replay, directory: true);
        string output = Full(outputPath ?? Path.Combine(workspace, "output"));
        Directory.CreateDirectory(output);
        SetPrivate(output, directory: true);
        bool modified = !CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(document.SeedTreeDigest),
            Encoding.ASCII.GetBytes(ComputeSourceDigest(root, header.SaveLayout)));
        return new PreparedDebugWorkspace(workspace, root, replay, output, modified, document);
    }

    public static void Delete(string workspacePath)
    {
        string workspace = Full(workspacePath);
        string marker = Path.Combine(workspace, "workspace.json");
        if (!File.Exists(marker))
            throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Refusing to delete a directory without workspace.json.");
        DebugWorkspaceDocument document = JsonSerializer.Deserialize<DebugWorkspaceDocument>(File.ReadAllText(marker), DebugTraceJson.Options)
            ?? throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace marker is invalid.");
        DeleteValidatedWorkspace(workspace, document.SessionId);
    }

    private static void ValidateCapture(string snapshot, string captureId, bool allowMismatch)
    {
        string marker = Path.Combine(snapshot, ".capture-id");
        string actual = File.Exists(marker) ? File.ReadAllText(marker).Trim() : string.Empty;
        if (!string.Equals(actual, captureId, StringComparison.Ordinal) && !allowMismatch)
            throw new DebugTraceException(DebugReplayStatuses.CaptureMismatch, "Trace and mutable-state snapshot captureId do not match.");
    }

    private static void RestoreSaveSnapshot(string root, string snapshot, string saveLayout)
    {
        if (saveLayout == "sav")
        {
            string destination = Path.Combine(root, "sav");
            if (Directory.Exists(destination)) DeleteTree(destination);
            string source = Path.Combine(snapshot, "sav");
            if (Directory.Exists(source)) CopyTree(source, destination);
            else { Directory.CreateDirectory(destination); SetPrivate(destination, directory: true); }
            return;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureNormalFile(file);
            if (EmueraSavePathPolicy.IsAllowedSaveFileName(Path.GetFileName(file))) File.Delete(file);
        }
        string rootSnapshot = Path.Combine(snapshot, "root");
        if (!Directory.Exists(rootSnapshot)) return;
        foreach (string file in Directory.EnumerateFiles(rootSnapshot, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureNormalFile(file);
            if (!EmueraSavePathPolicy.IsAllowedSaveFileName(Path.GetFileName(file)))
                throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Root-layout snapshot contains a non-save file.");
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)), overwrite: false);
        }
    }

    private static string ComputeSourceDigest(string root, string saveLayout)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(value => Path.GetRelativePath(root, value), StringComparer.Ordinal))
        {
            EnsureNormalFile(path);
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (saveLayout == "sav" && (relative == "sav" || relative.StartsWith("sav/", StringComparison.OrdinalIgnoreCase))) continue;
            if (saveLayout == "root" && !relative.Contains('/') && EmueraSavePathPolicy.IsAllowedSaveFileName(relative)) continue;
            byte[] name = Encoding.UTF8.GetBytes(relative.Normalize(NormalizationForm.FormC));
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            bytes = checked(bytes + stream.Length);
            if (++count > MaxFiles || bytes > MaxBytes)
                throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace exceeds debugger source limits.");
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer)) != 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CopyTree(string source, string destination)
    {
        EnsureNormalDirectory(source);
        Directory.CreateDirectory(destination);
        SetPrivate(destination, directory: true);
        long bytes = 0;
        int count = 0;
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            EnsureNormalDirectory(directory);
            string target = Path.Combine(destination, Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(target);
            SetPrivate(target, directory: true);
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            EnsureNormalFile(file);
            var info = new FileInfo(file);
            bytes = checked(bytes + info.Length);
            if (++count > MaxFiles || bytes > MaxBytes)
                throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace seed exceeds debugger copy limits.");
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            SetPrivate(target, directory: false);
        }
    }

    private static void DeleteValidatedWorkspace(string workspace, string sessionId)
    {
        EnsureNormalDirectory(workspace);
        string marker = Path.Combine(workspace, "workspace.json");
        DebugWorkspaceDocument document = JsonSerializer.Deserialize<DebugWorkspaceDocument>(File.ReadAllText(marker), DebugTraceJson.Options)
            ?? throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace marker is invalid.");
        if (!string.Equals(document.SessionId, sessionId, StringComparison.Ordinal))
            throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "Workspace Session identity does not match.");
        DeleteTree(workspace);
    }

    private static void DeleteTree(string path)
    {
        EnsureNormalDirectory(path);
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) EnsureNormalFile(file);
        foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)) EnsureNormalDirectory(directory);
        Directory.Delete(path, recursive: true);
    }

    private static void EnsureNormalDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, $"Directory is missing or linked: {path}");
    }

    private static void EnsureNormalFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, $"File is missing, special, or linked: {path}");
    }

    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, DebugTraceJson.Options));
        SetPrivate(path, directory: false);
    }

    private static void SetPrivate(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        File.SetUnixFileMode(path, directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string Full(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new DebugTraceException(DebugReplayStatuses.WorkspaceInvalid, "A debugger path is empty or invalid.");
        return Path.GetFullPath(path);
    }
}
