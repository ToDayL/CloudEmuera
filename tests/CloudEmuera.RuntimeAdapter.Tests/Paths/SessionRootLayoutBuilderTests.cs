using CloudEmuera.RuntimeAdapter;
using System.Diagnostics;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "RuntimePaths")]
public sealed class SessionRootLayoutBuilderTests
{
    [Fact]
    public void BuilderCreatesPrivateLayoutAndStableMappings()
    {
        using var workspace = new RuntimeTestWorkspace();
        SessionRootLayoutBuilder builder = new(
            workspace.GameVersionRoot,
            workspace.SessionWorkspaceRoot,
            RuntimeSaveLayout.Root);

        SessionRootLayout first = builder.Build();
        SessionRootLayout second = builder.Build();

        Assert.Equal(first.RuntimePaths.SessionRoot, second.RuntimePaths.SessionRoot);
        Assert.Equal(first.Mappings.Select(mapping => mapping.LogicalTarget), second.Mappings.Select(mapping => mapping.LogicalTarget));
        Assert.True(Directory.Exists(first.RuntimePaths.WritableRoot));
        Assert.True(Directory.Exists(first.RuntimePaths.RootSaveRoot));
        Assert.True(Directory.Exists(first.RuntimePaths.SavDirectoryRoot));
        Assert.True(File.Exists(Path.Combine(first.RuntimePaths.ConfigurationRoot, "emuera.config")));
        Assert.Contains(first.Mappings, mapping => mapping.LogicalTarget == "root/CSV" && mapping.ReadOnly);
        Assert.Contains(first.Mappings, mapping => mapping.LogicalTarget == "root/ERB" && mapping.ReadOnly);
        Assert.Contains(first.Mappings, mapping => mapping.LogicalTarget == "writable/tmp" && !mapping.ReadOnly);
        Assert.DoesNotContain(workspace.GameVersionRoot, first.DiagnosticDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderDoesNotOverwritePrivateConfigurationOrExistingSaves()
    {
        using var workspace = new RuntimeTestWorkspace();
        SessionRootLayout first = new SessionRootLayoutBuilder(
            workspace.GameVersionRoot,
            workspace.SessionWorkspaceRoot).Build();
        string configPath = Path.Combine(first.RuntimePaths.ConfigurationRoot, "emuera.config");
        string savePath = Path.Combine(first.RuntimePaths.RootSaveRoot, "global.sav");
        File.WriteAllText(configPath, "session override\n");
        File.WriteAllText(savePath, "keep\n");

        _ = new SessionRootLayoutBuilder(
            workspace.GameVersionRoot,
            workspace.SessionWorkspaceRoot).Build();

        Assert.Equal("session override\n", File.ReadAllText(configPath));
        Assert.Equal("keep\n", File.ReadAllText(savePath));
    }

    [Fact]
    public void MissingRequiredGameVersionEntryFailsWithoutDeletingWorkspace()
    {
        using var workspace = new RuntimeTestWorkspace();
        Directory.Delete(Path.Combine(workspace.GameVersionRoot, "ERB"), recursive: true);
        File.WriteAllText(Path.Combine(workspace.Root, "keep.txt"), "keep");

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            new SessionRootLayoutBuilder(workspace.GameVersionRoot, workspace.SessionWorkspaceRoot).Build());

        Assert.Equal(RuntimePathReasonCodes.LayoutConflict, exception.ReasonCode);
        Assert.False(Directory.Exists(workspace.SessionWorkspaceRoot));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(workspace.Root, "keep.txt")));
    }

    [Fact]
    public void SymlinkInGameVersionIsRejectedWhenSupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        string outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(Path.Combine(workspace.GameVersionRoot, "resources", "outside.txt"), Path.Combine(outside, "outside.txt"));

        RuntimeFileAccessException exception = Assert.Throws<RuntimeFileAccessException>(() =>
            new SessionRootLayoutBuilder(workspace.GameVersionRoot, workspace.SessionWorkspaceRoot).Build());
        Assert.Equal(RuntimePathReasonCodes.SymbolicLinkRejected, exception.ReasonCode);
    }

    [Fact]
    public void ExistingPrivateRootWithoutWritePermissionIsRejectedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        SessionRootLayout layout = new SessionRootLayoutBuilder(
            workspace.GameVersionRoot,
            workspace.SessionWorkspaceRoot).Build();
        string root = layout.RuntimePaths.RootSaveRoot;
        UnixFileMode originalMode = File.GetUnixFileMode(root);
        File.SetUnixFileMode(
            root,
            originalMode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));

        try
        {
            RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
                new SessionRootLayoutBuilder(workspace.GameVersionRoot, workspace.SessionWorkspaceRoot).Build());
            Assert.Equal(RuntimePathReasonCodes.LayoutConflict, exception.ReasonCode);
        }
        finally
        {
            File.SetUnixFileMode(root, originalMode);
        }
    }

    [Fact]
    public void FifoEntryIsRejectedOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        string fifo = Path.Combine(workspace.GameVersionRoot, "resources", "runtime.fifo");
        var startInfo = new ProcessStartInfo
        {
            FileName = "mkfifo",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(fifo);
        using (var process = Process.Start(startInfo)!)
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        RuntimeFileAccessException exception = Assert.Throws<RuntimeFileAccessException>(() =>
            new SessionRootLayoutBuilder(workspace.GameVersionRoot, workspace.SessionWorkspaceRoot).Build());
        Assert.Equal(RuntimePathReasonCodes.UnsupportedRuntimeFile, exception.ReasonCode);
    }
}
