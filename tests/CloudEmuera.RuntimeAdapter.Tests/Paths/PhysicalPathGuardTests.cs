using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "RuntimePaths")]
public sealed class PhysicalPathGuardTests
{
    [Fact]
    public void GuardUsesDirectoryBoundariesRatherThanStringPrefixes()
    {
        using var workspace = new RuntimeTestWorkspace();
        string firstWorkspace = Path.Combine(workspace.Root, "session-1");
        string prefixWorkspace = Path.Combine(workspace.Root, "session-10");
        RuntimePaths paths = new(
            Path.Combine(firstWorkspace, "root"),
            workspace.GameContentRoot,
            firstWorkspace,
            RuntimeSaveLayout.Root);
        var guard = new PhysicalPathGuard(paths);

        string resolved = guard.Resolve(RuntimeFilePath.Parse(RuntimeFileArea.Save, "global.sav"));

        Assert.StartsWith(paths.RootSaveRoot, resolved, StringComparison.Ordinal);
        Assert.False(resolved.StartsWith(prefixWorkspace, StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingSymlinkIsRejectedEvenWhenTargetStaysInsideTheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();
        string target = Path.Combine(paths.RootSaveRoot, "inside.sav");
        File.WriteAllText(target, "inside");
        File.CreateSymbolicLink(Path.Combine(paths.RootSaveRoot, "save00.sav"), target);
        var guard = new PhysicalPathGuard(paths);

        RuntimeFileAccessException exception = Assert.Throws<RuntimeFileAccessException>(() =>
            guard.Resolve(RuntimeFilePath.Parse(RuntimeFileArea.Save, "save00.sav")));

        Assert.Equal(RuntimePathReasonCodes.SymbolicLinkRejected, exception.ReasonCode);
        Assert.DoesNotContain(paths.GameContentRoot, exception.Message, StringComparison.Ordinal);
        Assert.Equal("save00.sav", exception.LogicalPath);
    }

    [Fact]
    public void SymlinkInAnIntermediateDirectoryIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();
        string outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(Path.Combine(paths.TemporaryRoot, "nested"), outside);
        var guard = new PhysicalPathGuard(paths);

        RuntimeFileAccessException exception = Assert.Throws<RuntimeFileAccessException>(() =>
            guard.Resolve(RuntimeFilePath.Parse(RuntimeFileArea.Temporary, "nested/value.tmp")));

        Assert.Equal(RuntimePathReasonCodes.SymbolicLinkRejected, exception.ReasonCode);
    }
}
