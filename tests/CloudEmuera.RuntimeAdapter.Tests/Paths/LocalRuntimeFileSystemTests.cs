using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "RuntimePaths")]
public sealed class LocalRuntimeFileSystemTests
{
    [Fact]
    public void ContentCanBeReadButNeverWritten()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();
        var fileSystem = new LocalRuntimeFileSystem(paths);
        RuntimeFilePath content = RuntimeFilePath.Parse(RuntimeFileArea.GameContent, "CSV/GAMEBASE.CSV");

        using (Stream stream = fileSystem.OpenRead(content))
        using (var reader = new StreamReader(stream))
        {
            Assert.Contains("test", reader.ReadToEnd(), StringComparison.Ordinal);
        }

        RuntimeFileAccessException exception = Assert.Throws<RuntimeFileAccessException>(() =>
            fileSystem.OpenWrite(content, RuntimeFileOpenMode.Create));
        Assert.Equal(RuntimePathReasonCodes.ReadOnlyArea, exception.ReasonCode);
    }

    [Fact]
    public void ExistingPathResolutionReturnsTheStoredCaseForAssetIdentity()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();
        var fileSystem = new LocalRuntimeFileSystem(paths);

        RuntimeFilePath requested = RuntimeFilePath.Parse(RuntimeFileArea.GameContent, "csv/gamebase.csv");

        RuntimeFilePath resolved = fileSystem.ResolveExistingPath(requested);

        Assert.Equal("CSV/GAMEBASE.CSV", resolved.LogicalPath);
    }

    [Fact]
    public void SaveAndTemporaryWritesStayInTheirPrivateAreas()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();
        var fileSystem = new LocalRuntimeFileSystem(paths);
        RuntimeFilePath save = RuntimeFilePath.Parse(RuntimeFileArea.Save, "save00.sav");
        RuntimeFilePath temp = RuntimeFilePath.Parse(RuntimeFileArea.Temporary, "worker.tmp");

        using (Stream saveStream = fileSystem.OpenWrite(save, RuntimeFileOpenMode.CreateNew))
        using (StreamWriter writer = new(saveStream))
        {
            writer.Write("save");
        }

        using (Stream tempStream = fileSystem.OpenWrite(temp, RuntimeFileOpenMode.CreateNew))
        using (StreamWriter writer = new(tempStream))
        {
            writer.Write("temp");
        }

        Assert.True(File.Exists(Path.Combine(paths.RootSaveRoot, "save00.sav")));
        Assert.True(File.Exists(Path.Combine(paths.TemporaryRoot, "worker.tmp")));
        Assert.False(File.Exists(Path.Combine(paths.GameContentRoot, "save00.sav")));
    }

    [Fact]
    public void SavDirectoryNestedPathsUseOneConsistentDirectoryAndFileContract()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths(RuntimeSaveLayout.SavDirectory);
        var fileSystem = new LocalRuntimeFileSystem(paths);
        RuntimeFilePath profile = RuntimeFilePath.Parse(RuntimeFileArea.Save, "profiles");
        RuntimeFilePath slot = RuntimeFilePath.Parse(RuntimeFileArea.Save, "profiles/slot01");
        RuntimeFilePath save = RuntimeFilePath.Parse(RuntimeFileArea.Save, "profiles/slot01/save00.sav");

        fileSystem.CreateDirectory(profile);
        fileSystem.CreateDirectory(slot);
        using (Stream stream = fileSystem.OpenWrite(save, RuntimeFileOpenMode.CreateNew))
        using (StreamWriter writer = new(stream))
        {
            writer.Write("nested save");
        }

        Assert.True(fileSystem.DirectoryExists(profile));
        Assert.False(fileSystem.FileExists(profile));
        Assert.True(fileSystem.FileExists(save));
        Assert.False(fileSystem.DirectoryExists(save));
        using (Stream stream = fileSystem.OpenRead(save))
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal("nested save", reader.ReadToEnd());
        }

        Assert.Equal(RuntimeFileEntryKind.Directory, fileSystem.GetMetadata(profile).Kind);
        Assert.Equal(RuntimeFileEntryKind.File, fileSystem.GetMetadata(save).Kind);
        Assert.Equal("profiles", fileSystem.Enumerate(RuntimeFileArea.Save)[0].Path.LogicalPath);
        Assert.Equal("profiles/slot01", fileSystem.Enumerate(profile)[0].Path.LogicalPath);
        Assert.Equal("profiles/slot01/save00.sav", fileSystem.Enumerate(slot)[0].Path.LogicalPath);
    }

    [Fact]
    public void EnumerationIsOrdinalAndSymlinksAreRejected()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();
        var fileSystem = new LocalRuntimeFileSystem(paths);
        RuntimeFilePath directory = RuntimeFilePath.Parse(RuntimeFileArea.Temporary, "items");
        fileSystem.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(paths.TemporaryRoot, "items", "b"), "b");
        File.WriteAllText(Path.Combine(paths.TemporaryRoot, "items", "A"), "A");

        IReadOnlyList<RuntimeFileEntry> entries = fileSystem.Enumerate(directory);
        string[] names = entries.Select(entry => entry.Path.RelativePath.Value).ToArray();
        Assert.Equal(2, names.Length);
        Assert.Equal("items/A", names[0]);
        Assert.Equal("items/b", names[1]);

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(
                Path.Combine(paths.TemporaryRoot, "save00.sav"),
                Path.Combine(paths.GameContentRoot, "CSV", "GAMEBASE.CSV"));
            RuntimeFileAccessException exception = Assert.Throws<RuntimeFileAccessException>(() =>
                fileSystem.FileExists(RuntimeFilePath.Parse(RuntimeFileArea.Temporary, "save00.sav")));
            Assert.Equal(RuntimePathReasonCodes.SymbolicLinkRejected, exception.ReasonCode);
        }
    }

    [Fact]
    public void CrossAreaMoveIsRejected()
    {
        using var workspace = new RuntimeTestWorkspace();
        RuntimePaths paths = workspace.BuildPaths();
        var fileSystem = new LocalRuntimeFileSystem(paths);
        RuntimeFilePath save = RuntimeFilePath.Parse(RuntimeFileArea.Save, "save00.sav");
        RuntimeFilePath temp = RuntimeFilePath.Parse(RuntimeFileArea.Temporary, "save00.sav");
        using (Stream stream = fileSystem.OpenWrite(save, RuntimeFileOpenMode.CreateNew))
        {
        }

        RuntimeFileAccessException exception = Assert.Throws<RuntimeFileAccessException>(() =>
            fileSystem.Move(save, temp));
        Assert.Equal(RuntimePathReasonCodes.PathOutsideArea, exception.ReasonCode);
    }
}
