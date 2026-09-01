using System.Diagnostics;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "SessionRoot")]
public sealed class SessionRootLayoutBuilderTests
{
    [Fact]
    public void DefaultCopyLimitsSupportLargeGamePackageBoundary()
    {
        SessionRootCopyLimits limits = new();

        Assert.Equal(1_000_000, limits.MaxFileCount);
        Assert.Equal(1_000_000, limits.MaxDirectoryCount);
        Assert.Equal(16L * 1024 * 1024 * 1024, limits.MaxTotalBytes);
        Assert.Equal(1L * 1024 * 1024 * 1024, limits.MaxSingleFileBytes);
    }

    [Fact]
    public void RootOnlyBuilderCopiesCompleteTreeWithoutPerFileManifest()
    {
        using var workspace = new RuntimeTestWorkspace();
        string unknown = Path.Combine(workspace.GameContentRoot, "custom-data", "nested", "state.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(unknown)!);
        File.WriteAllBytes(unknown, [1, 2, 3, 4]);
        string identity = "game/game_test/revision/7";

        SessionRootLayout layout = new SessionRootLayoutBuilder(
                workspace.GameContentRoot,
                Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
                RuntimeSaveLayout.Root)
            .WithRootOnlyContentIdentity(identity)
            .WithCopyLimits(new SessionRootCopyLimits())
            .BuildRootOnly();

        Assert.Equal(identity, layout.CopiedManifestDigest);
        Assert.Empty(layout.CopiedManifestEntries);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(layout.SessionRoot, "custom-data", "nested", "state.bin")));
        Assert.True(File.Exists(Path.Combine(layout.SessionRoot, "CSV", "GAMEBASE.CSV")));
        Assert.True(File.Exists(Path.Combine(layout.SessionRoot, SessionRootLayoutBuilder.BindingMetadataFileName)));
        Assert.False(File.Exists(Path.Combine(workspace.SessionWorkspaceRoot, "session-a", "metadata", "runtime-manifest.json")));
        Assert.Contains("manifest=none", layout.DiagnosticDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void RootOnlyBuilderMaterializesCaseAliasesAndPreservesExistingRoot()
    {
        using var workspace = new RuntimeTestWorkspace();
        File.Delete(Path.Combine(workspace.GameContentRoot, "CSV", "GAMEBASE.CSV"));
        File.WriteAllText(Path.Combine(workspace.GameContentRoot, "CSV", "GameBase.csv"), "; case variant\n");
        string sessionRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        string identity = "game/game_test/revision/8";
        SessionRootLayout first = new SessionRootLayoutBuilder(
                workspace.GameContentRoot,
                sessionRoot,
                RuntimeSaveLayout.Root)
            .WithRootOnlyContentIdentity(identity)
            .BuildRootOnly();

        string runtimeFile = Path.Combine(first.SessionRoot, "ERB", "START.ERB");
        File.AppendAllText(runtimeFile, "\n; runtime mutation\n");
        SessionRootLayout second = new SessionRootLayoutBuilder(
                workspace.GameContentRoot,
                sessionRoot,
                RuntimeSaveLayout.Root)
            .WithRootOnlyContentIdentity(identity)
            .BuildRootOnly();

        Assert.Equal(identity, second.CopiedManifestDigest);
        Assert.Contains("runtime mutation", File.ReadAllText(runtimeFile), StringComparison.Ordinal);
        Assert.Equal("; case variant\n", File.ReadAllText(Path.Combine(second.SessionRoot, "CSV", "GAMEBASE.CSV")));
    }

    [Fact]
    public void BuilderCopiesEveryManifestEntryIncludingUnknownDirectories()
    {
        using var workspace = new RuntimeTestWorkspace();
        string unknown = Path.Combine(workspace.GameContentRoot, "custom-data", "nested", "state.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(unknown)!);
        File.WriteAllBytes(unknown, [1, 2, 3, 4]);
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameContentRoot,
            "v18-test");

        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());

        Assert.Equal(manifest.ManifestDigest, layout.CopiedManifestDigest);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(layout.SessionRoot, "custom-data", "nested", "state.bin")));
        Assert.Equal(
            File.ReadAllText(Path.Combine(workspace.GameContentRoot, "emuera.config")),
            File.ReadAllText(Path.Combine(layout.SessionRoot, "emuera.config")));
        Assert.Empty(layout.ContentLinks);
        Assert.Contains(layout.Mappings, mapping => mapping.LogicalTarget == "root");
        Assert.True(Directory.Exists(layout.RuntimePaths.TemporaryRoot));
        Assert.True(Directory.Exists(layout.RuntimePaths.RootSaveRoot));
    }

    [Fact]
    public void BuilderMaterializesFixedCaseAliasForUniqueCaseVariant()
    {
        using var workspace = new RuntimeTestWorkspace();
        string exact = Path.Combine(workspace.GameContentRoot, "CSV", "GAMEBASE.CSV");
        File.Delete(exact);
        File.WriteAllText(Path.Combine(workspace.GameContentRoot, "CSV", "GameBase.csv"), "; case variant\n");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameContentRoot,
            "v18-test");

        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());

        Assert.Equal("; case variant\n", File.ReadAllText(Path.Combine(layout.SessionRoot, "CSV", "GAMEBASE.CSV")));
        Assert.True(File.Exists(Path.Combine(layout.SessionRoot, "CSV", "GameBase.csv")));
        layout.RuntimePaths.ValidateSessionRoot();
    }

    [Fact]
    public void BuilderDoesNotDuplicateExactFixedCaseName()
    {
        using var workspace = new RuntimeTestWorkspace();
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameContentRoot,
            "v18-test");

        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());

        Assert.Single(Directory.GetFiles(Path.Combine(layout.SessionRoot, "CSV")));
        Assert.Equal("; test\n", File.ReadAllText(Path.Combine(layout.SessionRoot, "CSV", "GAMEBASE.CSV")));
    }

    [Fact]
    public void BuilderAcceptsCaseVariantConfigurationViaAlias()
    {
        using var workspace = new RuntimeTestWorkspace();
        string exact = Path.Combine(workspace.GameContentRoot, "emuera.config");
        File.Delete(exact);
        File.WriteAllText(Path.Combine(workspace.GameContentRoot, "Emuera.Config"), "Use sav folder:NO\n");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameContentRoot,
            "v18-test");

        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());

        Assert.True(File.Exists(Path.Combine(layout.SessionRoot, "emuera.config")));
        layout.RuntimePaths.ValidateSessionRoot();
    }

    [Fact]
    public void BuilderIsIdempotentAndPreservesRuntimeChangesAndNativeSaves()
    {
        using var workspace = new RuntimeTestWorkspace();
        string sessionRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameContentRoot,
            "v18-test");
        SessionRootLayout first = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
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
            workspace.GameContentRoot,
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
        string sourceUnknown = Path.Combine(workspace.GameContentRoot, "custom-data", "state.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceUnknown)!);
        File.WriteAllText(sourceUnknown, "source");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameContentRoot);

        SessionRootLayout first = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());
        SessionRootLayout second = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
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
            workspace.GameContentRoot,
            "version-a");
        _ = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            sessionRoot,
            firstManifest,
            new SessionRootCopyLimits());

        SessionRootPublishedManifest otherManifest = SessionRootPublishedManifest.FromDirectory(
            workspace.GameContentRoot,
            "version-b");
        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameContentRoot,
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
            workspace.GameContentRoot,
            "version-a");
        File.AppendAllText(Path.Combine(workspace.GameContentRoot, "ERB", "START.ERB"), "\n; changed\n");

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameContentRoot,
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
            workspace.GameContentRoot,
            "version-a");
        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            sessionRoot,
            manifest,
            new SessionRootCopyLimits());
        string marker = Path.Combine(layout.SessionRoot, "save00.sav");
        File.WriteAllText(marker, "must-survive");

        File.AppendAllText(Path.Combine(workspace.GameContentRoot, "ERB", "START.ERB"), "\n; source changed\n");
        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameContentRoot,
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
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameContentRoot);
        string firstRoot = Path.Combine(workspace.SessionWorkspaceRoot, "session-a");
        _ = SessionRootLayoutBuilder.Build(
            workspace.GameContentRoot,
            firstRoot,
            manifest,
            new SessionRootCopyLimits());

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameContentRoot,
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
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameContentRoot);
        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameContentRoot,
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
            Path.Combine(workspace.GameContentRoot, "emuera.config"),
            "Use sav folder:YES\nUse sav folder:NO\n");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameContentRoot);

        RuntimePathException exception = Assert.Throws<RuntimePathException>(() =>
            SessionRootLayoutBuilder.Build(
                workspace.GameContentRoot,
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
        string link = Path.Combine(workspace.GameContentRoot, "custom-link.bin");
        File.CreateSymbolicLink(link, outside);

        RuntimePathException linkException = Assert.ThrowsAny<RuntimePathException>(() =>
            SessionRootPublishedManifest.FromDirectory(workspace.GameContentRoot));
        Assert.Equal(RuntimePathReasonCodes.SymbolicLinkRejected, linkException.ReasonCode);

        File.Delete(link);
        string hardLink = Path.Combine(workspace.GameContentRoot, "hard-link.bin");
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
            SessionRootPublishedManifest.FromDirectory(workspace.GameContentRoot));
        Assert.Equal(RuntimePathReasonCodes.UnsupportedRuntimeFile, hardLinkException.ReasonCode);

        File.Delete(hardLink);
        string fifo = Path.Combine(workspace.GameContentRoot, "runtime.fifo");
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
            SessionRootPublishedManifest.FromDirectory(workspace.GameContentRoot));
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
            Path.Combine(savWorkspace.GameContentRoot, "emuera.config"),
            "Use sav folder:YES\n");
        RuntimePaths sav = savWorkspace.BuildPaths(RuntimeSaveLayout.SavDirectory);
        Assert.Equal(RuntimeSaveLayout.SavDirectory, sav.SaveLayout);
        Assert.Equal(Path.Combine(sav.SessionRoot, "sav"), sav.SavDirectoryRoot);
    }
}
