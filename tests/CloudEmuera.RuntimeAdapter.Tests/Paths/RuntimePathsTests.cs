using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "RuntimePaths")]
public sealed class RuntimePathsTests
{
    [Fact]
    public void RootAndSavDirectoryLayoutsUseDifferentSessionRoots()
    {
        using var rootLayout = new RuntimeTestWorkspace();
        RuntimePaths rootPaths = rootLayout.BuildPaths(RuntimeSaveLayout.Root);
        string rootGlobal = rootPaths.ResolveSavePath("global.sav");

        using var directoryLayout = new RuntimeTestWorkspace();
        RuntimePaths directoryPaths = directoryLayout.BuildPaths(RuntimeSaveLayout.SavDirectory);
        string directoryGlobal = directoryPaths.ResolveSavePath("global.sav");

        Assert.Equal(Path.Combine(rootPaths.RootSaveRoot, "global.sav"), rootGlobal);
        Assert.Equal(Path.Combine(directoryPaths.SavDirectoryRoot, "global.sav"), directoryGlobal);
        Assert.NotEqual(rootGlobal, directoryGlobal);
        Assert.True(RuntimePathUtilitiesForTests.IsWithin(rootGlobal, rootPaths.SessionWorkspaceRoot));
        Assert.True(RuntimePathUtilitiesForTests.IsWithin(directoryGlobal, directoryPaths.SessionWorkspaceRoot));
        Assert.False(RuntimePathUtilitiesForTests.IsWithin(rootGlobal, rootPaths.GameContentRoot));
    }

    [Fact]
    public void SaveContractAllowsKnownAuxiliaryFilesButNotArbitraryFiles()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();

        Assert.EndsWith("save00.sav", paths.ResolveSavePath("save00.sav"), StringComparison.Ordinal);
        Assert.EndsWith("save123.sav", paths.ResolveSavePath("save123.sav"), StringComparison.Ordinal);
        Assert.EndsWith("txt00.txt", paths.ResolveSavePath("txt00.txt"), StringComparison.Ordinal);
        Assert.EndsWith("img0000.png", paths.ResolveSavePath("img0000.png"), StringComparison.Ordinal);

        RuntimeFileAccessException extension = Assert.Throws<RuntimeFileAccessException>(
            () => paths.ResolveSavePath("state.exe"));
        Assert.Equal(RuntimePathReasonCodes.UnsupportedRuntimeFile, extension.ReasonCode);

        RuntimeFileAccessException nestedRoot = Assert.Throws<RuntimeFileAccessException>(
            () => paths.ResolveSavePath("nested/save00.sav"));
        Assert.Equal(RuntimePathReasonCodes.PathOutsideArea, nestedRoot.ReasonCode);
    }

    [Fact]
    public void SavDirectoryLayoutCanUseSafeNestedPathButStillRejectsTraversal()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths(RuntimeSaveLayout.SavDirectory);

        string nested = paths.ResolveSavePath("profiles/slot01/save00.sav");
        Assert.True(RuntimePathUtilitiesForTests.IsWithin(nested, paths.SavDirectoryRoot));

        RuntimePathException exception = Assert.Throws<RuntimePathException>(
            () => paths.ResolveSavePath("../global.sav"));
        Assert.Equal(RuntimePathReasonCodes.InvalidRelativePath, exception.ReasonCode);
    }

    [Fact]
    public void DifferentSessionWorkspacesCannotOverlap()
    {
        using var workspace = new RuntimeTestWorkspace();
        string first = Path.Combine(workspace.Root, "session-1");
        string second = Path.Combine(first, "nested-session");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        RuntimePaths firstPaths = new(
            Path.Combine(first, "root"),
            workspace.GameContentRoot,
            first,
            RuntimeSaveLayout.Root);

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() => new RuntimePaths(
            Path.Combine(second, "root"),
            workspace.GameContentRoot,
            second,
            RuntimeSaveLayout.Root,
            new[] { firstPaths.SessionWorkspaceRoot }));
        Assert.Equal(RuntimePathReasonCodes.CrossSessionPath, exception.ReasonCode);
    }
}

internal static class RuntimePathUtilitiesForTests
{
    public static bool IsWithin(string candidate, string root)
    {
        string candidateFull = Path.GetFullPath(candidate);
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidateFull.Equals(rootFull, StringComparison.Ordinal) ||
            candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
