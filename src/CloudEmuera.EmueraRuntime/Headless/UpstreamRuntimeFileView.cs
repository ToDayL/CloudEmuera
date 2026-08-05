using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.EmueraRuntime.Headless;

/// <summary>
/// Materializes a session-private compatibility view exclusively through the
/// runtime file port. The pinned upstream loader receives only paths inside
/// this disposable view, never the original game or configuration roots.
/// </summary>
internal sealed class UpstreamRuntimeFileView : IDisposable
{
    private readonly IRuntimeFileSystem fileSystem;
    private readonly RuntimeFilePath viewRoot;
    private bool disposed;

    private UpstreamRuntimeFileView(
        IRuntimeFileSystem fileSystem,
        RuntimeFilePath viewRoot,
        string configurationRoot,
        string csvRoot,
        string erbRoot,
        string resourceRoot,
        string soundRoot,
        string fontRoot,
        string temporaryRoot)
    {
        this.fileSystem = fileSystem;
        this.viewRoot = viewRoot;
        ConfigurationRoot = configurationRoot;
        CsvRoot = csvRoot;
        ErbRoot = erbRoot;
        ResourceRoot = resourceRoot;
        SoundRoot = soundRoot;
        FontRoot = fontRoot;
        TemporaryRoot = temporaryRoot;
    }

    public string ConfigurationRoot { get; }
    public string CsvRoot { get; }
    public string ErbRoot { get; }
    public string ResourceRoot { get; }
    public string SoundRoot { get; }
    public string FontRoot { get; }
    public string TemporaryRoot { get; }

    public static UpstreamRuntimeFileView Create(
        RuntimePaths paths,
        IRuntimeFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileSystem);
        string rootName = $"upstream-view-{Guid.NewGuid():N}";
        var viewRoot = new RuntimeFilePath(RuntimeFileArea.Temporary, rootName);
        fileSystem.CreateDirectory(viewRoot, cancellationToken);

        try
        {
            fileSystem.CreateDirectory(Child(viewRoot, "config"), cancellationToken);
            CopyFile(
                fileSystem,
                new RuntimeFilePath(RuntimeFileArea.Configuration, "emuera.config"),
                Child(viewRoot, "config/emuera.config"),
                cancellationToken);
            CopyTree(fileSystem, new RuntimeFilePath(RuntimeFileArea.GameContent, "CSV"), Child(viewRoot, "csv"), cancellationToken);
            CopyTree(fileSystem, new RuntimeFilePath(RuntimeFileArea.GameContent, "ERB"), Child(viewRoot, "erb"), cancellationToken);
            CopyOptionalTree(fileSystem, new RuntimeFilePath(RuntimeFileArea.GameContent, "resources"), Child(viewRoot, "resources"), cancellationToken);
            CopyOptionalTree(fileSystem, new RuntimeFilePath(RuntimeFileArea.GameContent, "sound"), Child(viewRoot, "sound"), cancellationToken);
            CopyOptionalTree(fileSystem, new RuntimeFilePath(RuntimeFileArea.GameContent, "font"), Child(viewRoot, "font"), cancellationToken);
            fileSystem.CreateDirectory(Child(viewRoot, "data"), cancellationToken);

            return new UpstreamRuntimeFileView(
                fileSystem,
                viewRoot,
                ResolveDirectory(paths, Child(viewRoot, "config")),
                ResolveDirectory(paths, Child(viewRoot, "csv")),
                ResolveDirectory(paths, Child(viewRoot, "erb")),
                ResolveDirectory(paths, Child(viewRoot, "resources")),
                ResolveDirectory(paths, Child(viewRoot, "sound")),
                ResolveDirectory(paths, Child(viewRoot, "font")),
                ResolveDirectory(paths, Child(viewRoot, "data")));
        }
        catch
        {
            fileSystem.Delete(viewRoot, recursive: true, CancellationToken.None);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        fileSystem.Delete(viewRoot, recursive: true);
    }

    private static void CopyOptionalTree(
        IRuntimeFileSystem fileSystem,
        RuntimeFilePath source,
        RuntimeFilePath destination,
        CancellationToken cancellationToken)
    {
        if (fileSystem.DirectoryExists(source, cancellationToken))
            CopyTree(fileSystem, source, destination, cancellationToken);
        else
            fileSystem.CreateDirectory(destination, cancellationToken);
    }

    private static void CopyTree(
        IRuntimeFileSystem fileSystem,
        RuntimeFilePath source,
        RuntimeFilePath destination,
        CancellationToken cancellationToken)
    {
        fileSystem.CreateDirectory(destination, cancellationToken);
        foreach (RuntimeFileEntry entry in fileSystem.Enumerate(source, cancellationToken))
        {
            string name = entry.Path.RelativePath.Segments[^1];
            RuntimeFilePath target = Child(destination, name);
            if (entry.Kind == RuntimeFileEntryKind.Directory)
                CopyTree(fileSystem, entry.Path, target, cancellationToken);
            else
                CopyFile(fileSystem, entry.Path, target, cancellationToken);
        }
    }

    private static void CopyFile(
        IRuntimeFileSystem fileSystem,
        RuntimeFilePath source,
        RuntimeFilePath destination,
        CancellationToken cancellationToken)
    {
        using Stream input = fileSystem.OpenRead(source, cancellationToken);
        using Stream output = fileSystem.OpenWrite(destination, RuntimeFileOpenMode.CreateNew, cancellationToken);
        input.CopyTo(output);
    }

    private static RuntimeFilePath Child(RuntimeFilePath parent, string child) =>
        new(parent.Area, $"{parent.RelativePath.Value}/{child}");

    private static string ResolveDirectory(RuntimePaths paths, RuntimeFilePath path) =>
        paths.ResolvePhysicalPath(path);
}
