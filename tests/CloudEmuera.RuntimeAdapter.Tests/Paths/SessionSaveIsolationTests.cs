using System.Diagnostics;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "SaveIsolation")]
public sealed class SessionSaveIsolationTests
{
    [Fact]
    public void RootLayoutSavesArePrivateToEachSession()
    {
        using var workspace = new RuntimeTestWorkspace();
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

        var firstFileSystem = new LocalRuntimeFileSystem(first.RuntimePaths);
        RuntimeFilePath save = new(RuntimeFileArea.Save, "save00.sav");
        using (Stream stream = firstFileSystem.OpenWrite(save, RuntimeFileOpenMode.CreateNew))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write("session-a");
        }

        Assert.NotEqual(first.RuntimePaths.ResolveSavePath(save.RelativePath), second.RuntimePaths.ResolveSavePath(save.RelativePath));
        Assert.True(File.Exists(Path.Combine(first.SessionRoot, "save00.sav")));
        Assert.False(File.Exists(Path.Combine(second.SessionRoot, "save00.sav")));
        Assert.False(File.Exists(Path.Combine(workspace.GameVersionRoot, "save00.sav")));
    }

    [Fact]
    public void AllocatedUserSessionsDoNotShareWritableInodes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot);
        string firstRoot = Path.Combine(workspace.Root, "user-a", "session-a");
        string secondRoot = Path.Combine(workspace.Root, "user-b", "session-b");
        SessionRootLayout first = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            firstRoot,
            manifest,
            new SessionRootCopyLimits());
        SessionRootLayout second = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            secondRoot,
            manifest,
            new SessionRootCopyLimits(),
            [firstRoot]);

        string sourceContent = Path.Combine(workspace.GameVersionRoot, "ERB", "START.ERB");
        string firstContent = Path.Combine(first.SessionRoot, "ERB", "START.ERB");
        string secondContent = Path.Combine(second.SessionRoot, "ERB", "START.ERB");
        Assert.NotEqual(GetUnixIdentity(sourceContent), GetUnixIdentity(firstContent));
        Assert.NotEqual(GetUnixIdentity(sourceContent), GetUnixIdentity(secondContent));
        Assert.NotEqual(GetUnixIdentity(firstContent), GetUnixIdentity(secondContent));

        File.WriteAllText(Path.Combine(first.SessionRoot, "save00.sav"), "session-a-slot");
        File.WriteAllText(Path.Combine(first.SessionRoot, "global.sav"), "session-a-global");
        File.WriteAllText(Path.Combine(second.SessionRoot, "save00.sav"), "session-b-slot");
        File.WriteAllText(Path.Combine(second.SessionRoot, "global.sav"), "session-b-global");

        Assert.Equal("session-a-slot", File.ReadAllText(Path.Combine(first.SessionRoot, "save00.sav")));
        Assert.Equal("session-a-global", File.ReadAllText(Path.Combine(first.SessionRoot, "global.sav")));
        Assert.Equal("session-b-slot", File.ReadAllText(Path.Combine(second.SessionRoot, "save00.sav")));
        Assert.Equal("session-b-global", File.ReadAllText(Path.Combine(second.SessionRoot, "global.sav")));
        Assert.NotEqual(
            GetUnixIdentity(Path.Combine(first.SessionRoot, "global.sav")),
            GetUnixIdentity(Path.Combine(second.SessionRoot, "global.sav")));
        Assert.False(File.Exists(Path.Combine(workspace.GameVersionRoot, "global.sav")));
    }

    [Fact]
    public void SavDirectoryLayoutDoesNotFallBackToTheSessionRoot()
    {
        using var workspace = new RuntimeTestWorkspace();
        File.WriteAllText(Path.Combine(workspace.GameVersionRoot, "emuera.config"), "Use sav folder:YES\n");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot);
        SessionRootLayout layout = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            Path.Combine(workspace.SessionWorkspaceRoot, "session-a"),
            manifest,
            new SessionRootCopyLimits());
        var fileSystem = new LocalRuntimeFileSystem(layout.RuntimePaths);

        RuntimeFilePath save = new(RuntimeFileArea.Save, "save00.sav");
        using (Stream stream = fileSystem.OpenWrite(save, RuntimeFileOpenMode.CreateNew))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write("sav-directory");
        }

        Assert.True(File.Exists(Path.Combine(layout.SessionRoot, "sav", "save00.sav")));
        Assert.False(File.Exists(Path.Combine(layout.SessionRoot, "save00.sav")));
        Assert.Throws<RuntimeFileAccessException>(() =>
            layout.RuntimePaths.ResolveSavePath("save"));
    }

    [Fact]
    public void SavDirectoryGlobalFilesStayPrivateAcrossAllocatedSessions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new RuntimeTestWorkspace();
        File.WriteAllText(Path.Combine(workspace.GameVersionRoot, "emuera.config"), "Use sav folder:YES\n");
        SessionRootPublishedManifest manifest = SessionRootPublishedManifest.FromDirectory(workspace.GameVersionRoot);
        string firstRoot = Path.Combine(workspace.Root, "user-a", "session-a");
        string secondRoot = Path.Combine(workspace.Root, "user-a", "session-b");
        string secondWorkspace = Path.Combine(workspace.Root, "user-a-session-b-workspace");
        SessionRootLayout first = SessionRootLayoutBuilder.Build(
            workspace.GameVersionRoot,
            firstRoot,
            manifest,
            new SessionRootCopyLimits());
        SessionRootLayout second = new SessionRootLayoutBuilder(
            workspace.GameVersionRoot,
            secondRoot,
            secondWorkspace,
            [firstRoot])
            .Build(manifest, new SessionRootCopyLimits());

        string firstSave = Path.Combine(first.RuntimePaths.SavDirectoryRoot, "save00.sav");
        string firstGlobal = Path.Combine(first.RuntimePaths.SavDirectoryRoot, "global.sav");
        string secondSave = Path.Combine(second.RuntimePaths.SavDirectoryRoot, "save00.sav");
        string secondGlobal = Path.Combine(second.RuntimePaths.SavDirectoryRoot, "global.sav");
        File.WriteAllText(firstSave, "sav-a-slot");
        File.WriteAllText(firstGlobal, "sav-a-global");
        File.WriteAllText(secondSave, "sav-b-slot");
        File.WriteAllText(secondGlobal, "sav-b-global");

        Assert.Equal("sav-a-global", File.ReadAllText(firstGlobal));
        Assert.Equal("sav-b-global", File.ReadAllText(secondGlobal));
        Assert.NotEqual(GetUnixIdentity(firstGlobal), GetUnixIdentity(secondGlobal));
        Assert.False(File.Exists(Path.Combine(first.SessionRoot, "save00.sav")));
        Assert.False(File.Exists(Path.Combine(first.SessionRoot, "global.sav")));
        Assert.False(File.Exists(Path.Combine(second.SessionRoot, "save00.sav")));
        Assert.False(File.Exists(Path.Combine(second.SessionRoot, "global.sav")));
    }

    private static (long Device, long Inode) GetUnixIdentity(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "stat",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("%d:%i");
        startInfo.ArgumentList.Add(path);
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        string[] fields = output.Split(':', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, fields.Length);
        return (
            long.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}
