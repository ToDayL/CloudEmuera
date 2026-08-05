namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Resolves logical paths and performs lexical, containment and reparse-point
/// checks immediately before local I/O. This is an application-layer defense;
/// it does not close the TOCTOU window and must be combined with a future
/// worker identity, read-only mounts and no-follow directory-handle operations.
/// </summary>
public sealed class PhysicalPathGuard
{
    private readonly RuntimePaths paths;

    public PhysicalPathGuard(RuntimePaths paths)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string Resolve(RuntimeFilePath path) =>
        Resolve(path, RuntimeFileOperation.Read, requireExisting: false);

    public string Resolve(RuntimeFilePath path, RuntimeFileOperation operation) =>
        Resolve(path, operation, requireExisting: false);

    public string Resolve(RuntimeFileArea area, RuntimeRelativePath relativePath) =>
        Resolve(new RuntimeFilePath(area, relativePath));

    internal string ResolveAreaRoot(
        RuntimeFileArea area,
        RuntimeFileOperation operation,
        bool requireExisting)
    {
        if (!Enum.IsDefined(area))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.PathOutsideArea,
                "The runtime file area is invalid.",
                area: area);
        }

        var rootPath = RuntimeFilePath.Parse(area, "area-root");
        string candidate = paths.GetAreaRoot(area);
        CheckOperationPolicy(rootPath, operation);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(candidate, "<area-root>", area);
        if (requireExisting)
        {
            RuntimePathUtilities.ThrowIfReparsePoint(candidate, "<area-root>", area, missingIsAllowed: false);
        }

        return candidate;
    }

    public string Resolve(
        RuntimeFilePath path,
        RuntimeFileOperation operation,
        bool requireExisting)
    {
        CheckOperationPolicy(path, operation);

        // RuntimePaths owns the save-name and directory-segment policies. The
        // operation selects the appropriate save resolver so nested sav/
        // directories use the same contract for creation, lookup and moves.
        string candidate = ResolveCandidate(path, operation);
        string areaRoot = paths.GetAreaRoot(path.Area);

        RuntimePathUtilities.ThrowIfOutside(candidate, areaRoot, path.LogicalPath, path.Area);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(areaRoot, path.LogicalPath, path.Area);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(candidate, path.LogicalPath, path.Area);
        if (requireExisting)
        {
            RuntimePathUtilities.ThrowIfReparsePoint(
                candidate,
                path.LogicalPath,
                path.Area,
                missingIsAllowed: false);
        }

        return candidate;
    }

    public void ValidateOpenedPath(RuntimeFilePath path, RuntimeFileOperation operation)
    {
        _ = Resolve(path, operation, requireExisting: true);
    }

    private static void CheckOperationPolicy(RuntimeFilePath path, RuntimeFileOperation operation)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.PathOutsideArea,
                "The runtime file operation is invalid.",
                path.LogicalPath,
                path.Area);
        }

        bool writes = operation is not RuntimeFileOperation.Read and
            not RuntimeFileOperation.Enumerate and
            not RuntimeFileOperation.ReadDirectory and
            not RuntimeFileOperation.ReadEntry;
        if (writes && path.Area == RuntimeFileArea.GameContent)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.ReadOnlyArea,
                "Game content is read-only.",
                path.LogicalPath,
                path.Area);
        }
    }

    private string ResolveCandidate(RuntimeFilePath path, RuntimeFileOperation operation)
    {
        if (path.Area != RuntimeFileArea.Save)
        {
            return paths.ResolvePhysicalPath(path);
        }

        return operation switch
        {
            RuntimeFileOperation.CreateDirectory or
            RuntimeFileOperation.Enumerate or
            RuntimeFileOperation.ReadDirectory or
            RuntimeFileOperation.MoveDirectory => paths.ResolveSaveDirectoryPath(path.RelativePath),
            RuntimeFileOperation.ReadEntry or
            RuntimeFileOperation.Move or
            RuntimeFileOperation.Delete => paths.ResolveSaveEntryPath(path.RelativePath),
            _ => paths.ResolveSavePath(path.RelativePath)
        };
    }
}
