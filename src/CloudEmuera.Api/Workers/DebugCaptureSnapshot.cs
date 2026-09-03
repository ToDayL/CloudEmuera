using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Api.Workers;

internal sealed record DebugCaptureMaterial(string CaptureId, string TracePath);

internal static class DebugCaptureSnapshot
{
    public static DebugCaptureMaterial Create(string sessionRoot, RuntimeSaveLayout layout)
    {
        string root = Path.GetFullPath(sessionRoot);
        string parent = Directory.GetParent(root)?.FullName ?? throw new InvalidDataException("SessionRoot has no parent.");
        string metadata = Path.Combine(parent, "metadata");
        string snapshot = Path.Combine(metadata, "debug-save-snapshot");
        string trace = Path.Combine(metadata, "debug-input-trace.jsonl");
        Directory.CreateDirectory(metadata);
        if (Directory.Exists(snapshot))
        {
            RejectLinks(snapshot);
            Directory.Delete(snapshot, recursive: true);
        }
        Directory.CreateDirectory(snapshot);
        SetPrivate(snapshot, directory: true);
        string captureId = "cap_" + Guid.CreateVersion7().ToString("N");
        File.WriteAllText(Path.Combine(snapshot, ".capture-id"), captureId);
        SetPrivate(Path.Combine(snapshot, ".capture-id"), directory: false);
        if (layout == RuntimeSaveLayout.SavDirectory)
        {
            string source = Path.Combine(root, "sav");
            string destination = Path.Combine(snapshot, "sav");
            if (Directory.Exists(source)) CopyTree(source, destination);
            else Directory.CreateDirectory(destination);
        }
        else
        {
            string destination = Path.Combine(snapshot, "root");
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureFile(file);
                if (EmueraSavePathPolicy.IsAllowedSaveFileName(Path.GetFileName(file)))
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }
        }
        using (var stream = new FileStream(trace, FileMode.Create, FileAccess.Write, FileShare.Read))
            stream.Flush(flushToDisk: true);
        SetPrivate(trace, directory: false);
        return new DebugCaptureMaterial(captureId, trace);
    }

    private static void CopyTree(string source, string destination)
    {
        RejectLinks(source);
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            EnsureDirectory(directory);
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            EnsureFile(file);
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void RejectLinks(string root)
    {
        EnsureDirectory(root);
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) EnsureDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) EnsureFile(file);
    }

    private static void EnsureDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Debug capture contains a linked directory.");
    }

    private static void EnsureFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Debug capture contains a linked file.");
    }

    private static void SetPrivate(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        File.SetUnixFileMode(path, directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
