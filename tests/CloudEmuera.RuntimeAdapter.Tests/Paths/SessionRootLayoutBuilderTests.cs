using System.Diagnostics;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "SessionRoot")]
public sealed class SessionRootLayoutBuilderTests
{
    [Fact]
    public void BuilderCopiesEveryManifestEntryIncludingUnknownDirectories()
    {
        using var workspace = new RuntimeTestWorkspace();
        string unknown = Path.Combine(workspace.GameVersionRoot, "custom-data", "nested", "state.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(unknown)!);
        File.WriteAllBytes(unknown, [1, 2, 3, 4]);
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameVersionRoot,
            "v18-test");

        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());

        Assert.Equal(manifest.ManifestDigest, layout.CopiedManifestDigest);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(layout.SessionRoot, "custom-data", "nested", "state.bin")));
        Assert.Equal(
            File.ReadAllText(Path.Combine(workspace.GameVersionRoot, "emuera.config")),
            File.ReadAllText(Path.Combine(layout.SessionRoot, "emuera.config")));
        Assert.Empty(layout.ContentLinks);
        Assert.Contains(layout.Mappings, mapping => mapping.LogicalTarget == "root");
        Assert.True(Directory.Exists(layout.RuntimePaths.TemporaryRoot));
        Assert.True(Directory.Exists(layout.RuntimePaths.RootSaveRoot));
    }

    [Fact]
    public void BuilderIsIdempotentAndPreservesRuntimeChangesAndNativeSaves()
    {
        using var workspace = new RuntimeTestWorkspace();
        string sessionRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameVersionRoot,
            "v18-test");
        SessionRootLayout first = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            sessionRoot,
            manifest,
            new SessionRootCopyLimits());

        string copiedFile = Path.Combine(first.SessionRoot, "ERB", "START.ERB");
        string config = Path.Combine(first.SessionRoot, "emuera.config");
        File.AppendAllText(copiedFile, "\n; runtime mutation\n");
        File.AppendAllText(config, "; runtime setting\n");
        File.WriteAllText(Path.Combine(first.SessionRoot, "save00.sav"), "native-save");
        File.WriteAllText(Path.Combine(first.SessionRoot, "global.sav"), "native-global");

        SessionRootLayout second = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            sessionRoot,
            manifest,
            new SessionRootCopyLimits());

        Assert.Equal(first.CopiedManifestDigest, second.CopiedManifestDigest);
        Assert.Contains("runtime mutation", File.ReadAllText(copiedFile), StringComparison.Ordinal);
        Assert.Contains("runtime setting", File.ReadAllText(config), StringComparison.Ordinal);
        Assert.Equal("native-save", File.ReadAllText(Path.Combine(second.SessionRoot, "save00.sav")));
        Assert.Equal("native-global", File.ReadAllText(Path.Combine(second.SessionRoot, "global.sav")));
    }

    [Fact]
    public void BuilderCreatesOnlyRegularSessionEntriesAndKeepsUnknownCopiesPrivate()
    {
        using var workspace = new RuntimeTestWorkspace();
        string sourceUnknown = Path.Combine(workspace.GameVersionRoot, "custom-data", "state.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceUnknown)!);
        File.WriteAllText(sourceUnknown, "source");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot);

        SessionRootLayout first = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());
        SessionRootLayout second = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-b"),
            manifest,
            new SessionRootCopyLimits());

        first.RuntimePaths.ValidateSessionRoot();
        second.RuntimePaths.ValidateSessionRoot();
        string firstUnknown = Path.Combine(first.SessionRoot, "custom-data", "state.bin");
        string secondUnknown = Path.Combine(second.SessionRoot, "custom-data", "state.bin");
        File.WriteAllText(firstUnknown, "session-a");

        Assert.Equal("source", File.ReadAllText(sourceUnknown));
        Assert.Equal("source", File.ReadAllText(secondUnknown));
        Assert.Equal("session-a", File.ReadAllText(firstUnknown));
        Assert.All(
            Directory.EnumerateFileSystemEntries(first.SessionRoot, "*", SearchOption.AllDirectories),
            path => Assert.False((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0));
    }

    [Fact]
    public void BuilderRejectsExistingRootBoundToAnotherVersion()
    {
        using var workspace = new RuntimeTestWorkspace();
        string sessionRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        SessionRootPublishedManifest firstManifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameVersionRoot,
            "version-a");
        _ = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            sessionRoot,
            firstManifest,
            new SessionRootCopyLimits());

        SessionRootPublishedManifest otherManifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameVersionRoot,
            "version-b");
        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameVersionRoot,
                sessionRoot,
                otherManifest,
                new SessionRootCopyLimits()));

        Assert.Equal(RuntimePathReasonCodes.LayoutConflict, exception.ReasonCode);
    }

    [Fact]
    public void BuilderRejectsManifestMismatchAndDoesNotPublishPartialRoot()
    {
        using var workspace = new RuntimeTestWorkspace();
        string sessionRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameVersionRoot,
            "version-a");
        File.AppendAllText(Path.Combine(workspace.GameVersionRoot, "ERB", "START.ERB"), "\n; changed\n");

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameVersionRoot,
                sessionRoot,
                manifest,
                new SessionRootCopyLimits()));

        Assert.Equal(RuntimePathReasonCodes.LayoutConflict, exception.ReasonCode);
        Assert.False(Directory.Exists(sessionRoot));
        if (Directory.Exists(workspace.SessionWorkspaceRoot))
        {
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(workspace.SessionWorkspaceRoot),
                path => Path.GetFileName(path).StartsWith(".cloudemuera-staging-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void BuilderFailureDoesNotDeleteAnExistingSessionRoot()
    {
        using var workspace = new RuntimeTestWorkspace();
        string sessionRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameVersionRoot,
            "version-a");
        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            sessionRoot,
            manifest,
            new SessionRootCopyLimits());
        string marker = Path.Combine(layout.SessionRoot, "save00.sav");
        File.WriteAllText(marker, "must-survive");

        File.AppendAllText(Path.Combine(workspace.GameVersionRoot, "ERB", "START.ERB"), "\n; source changed\n");
        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameVersionRoot,
                sessionRoot,
                manifest,
                new SessionRootCopyLimits()));

        Assert.Equal(RuntimePathReasonCodes.LayoutConflict, exception.ReasonCode);
        Assert.Equal("must-survive", File.ReadAllText(marker));
        Assert.True(File.Exists(Path.Combine(layout.SessionRoot, SessionRootLayoutBuilder.BindingMetadataFileName)));
    }

    [Fact]
    public void BuilderRejectsOverlappingAllocatedSessionRoots()
    {
        using var workspace = new RuntimeTestWorkspace();
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot);
        string firstRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        _ = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            firstRoot,
            manifest,
            new SessionRootCopyLimits());

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameVersionRoot,
                Path.Combine(firstRoot, "nested"),
                manifest,
                new SessionRootCopyLimits(),
                [firstRoot]));

        Assert.Equal(RuntimePathReasonCodes.CrossSessionPath, exception.ReasonCode);
    }

    [Fact]
    public void BuilderEnforcesCopyLimitsDuringCopy()
    {
        using var workspace = new RuntimeTestWorkspace();
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot);
        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameVersionRoot,
                Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
                manifest,
                new SessionRootCopyLimits(maxTotalBytes: 1)));

        Assert.Equal(RuntimePathReasonCodes.LayoutConflict, exception.ReasonCode);
    }

    [Fact]
    public void BuilderRejectsConflictingOrInvalidUseSaveFolder()
    {
        using var workspace = new RuntimeTestWorkspace();
        File.WriteAllText(
            Path.Combine(workspace.GameVersionRoot, "emuera.config"),
            "Use sav folder:YES\nUse sav folder:NO\n");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot);

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameVersionRoot,
                Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
                manifest,
                new SessionRootCopyLimits()));

        Assert.Equal(RuntimePathReasonCodes.LayoutConflict, exception.ReasonCode);
        Assert.False(Directory.Exists(Path.Combine(workspace.SessionWorkspaceRoot, "session-a")));
    }

    [Fact]
    public void BuilderRejectsSourceLinkHardLinkOrSpecialFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        string outside = Path.Combine(workspace.Root, "outside.bin");
        File.WriteAllText(outside, "outside");
        string link = Path.Combine(workspace.GameVersionRoot, "custom-link.bin");
        File.CreateSymbolicLink(link, outside);

        RuntimePathException linkException = Assert.ThrowsAny<RuntimePathException>(() =>
            SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot));
        Assert.Equal(RuntimePathReasonCodes.SymbolicLinkRejected, linkException.ReasonCode);

        File.Delete(link);
        string hardLink = Path.Combine(workspace.GameVersionRoot, "hard-link.bin");
        var hardLinkStartInfo = new ProcessStartInfo
        {
            FileName = "ln",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        hardLinkStartInfo.ArgumentList.Add(outside);
        hardLinkStartInfo.ArgumentList.Add(hardLink);
        using (Process hardLinkProcess = Process.Start(hardLinkStartInfo)!)
        {
            hardLinkProcess.WaitForExit();
            Assert.Equal(0, hardLinkProcess.ExitCode);
        }
        RuntimePathException hardLinkException = Assert.ThrowsAny<RuntimePathException>(() =>
            SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot));
        Assert.Equal(RuntimePathReasonCodes.UnsupportedRuntimeFile, hardLinkException.ReasonCode);

        File.Delete(hardLink);
        string fifo = Path.Combine(workspace.GameVersionRoot, "runtime.fifo");
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "mkfifo",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { fifo }
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        RuntimePathException fifoException = Assert.ThrowsAny<RuntimePathException>(() =>
            SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot));
        Assert.Equal(RuntimePathReasonCodes.UnsupportedRuntimeFile, fifoException.ReasonCode);
    }

    [Fact]
    public void LayoutComesFromUseSaveFolderAndDefaultsToRoot()
    {
        using var rootWorkspace = new RuntimeTestWorkspace();
        RuntimePaths root = rootWorkspace.BuildPaths();
        Assert.Equal(RuntimeSaveLayout.Root, root.SaveLayout);
        Assert.Equal(root.SessionRoot, root.RootSaveRoot);
        Assert.Equal(Path.Combine(root.SessionRoot, "sav"), root.SavDirectoryRoot);

        using var savWorkspace = new RuntimeTestWorkspace();
        File.WriteAllText(
            Path.Combine(savWorkspace.GameVersionRoot, "emuera.config"),
            "Use sav folder:YES\n");
        RuntimePaths sav = savWorkspace.BuildPaths(RuntimeSaveLayout.SavDirectory);
        Assert.Equal(RuntimeSaveLayout.SavDirectory, sav.SaveLayout);
        Assert.Equal(Path.Combine(sav.SessionRoot, "sav"), sav.SavDirectoryRoot);
    }
}
