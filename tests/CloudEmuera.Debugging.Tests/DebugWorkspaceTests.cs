using CloudEmuera.Debugger;
using CloudEmuera.Debugging.Contracts;
using Xunit;

namespace CloudEmuera.Debugging.Tests;

[Trait("Category", "TraceReplay")]
public sealed class DebugWorkspaceTests
{
    [Fact]
    public void RepeatedPreparePreservesSourceEditsAndRestoresSavSnapshot()
    {
        using var directory = new TemporaryDirectory();
        string sessionRoot = Path.Combine(directory.Path, "session-root");
        string snapshot = Path.Combine(directory.Path, "snapshot");
        string workspace = Path.Combine(directory.Path, "workspace");
        Directory.CreateDirectory(Path.Combine(sessionRoot, "sav"));
        File.WriteAllText(Path.Combine(sessionRoot, "game.erb"), "original");
        File.WriteAllText(Path.Combine(sessionRoot, "sav", "save01.dat"), "live");
        Directory.CreateDirectory(Path.Combine(snapshot, "sav"));
        File.WriteAllText(Path.Combine(snapshot, ".capture-id"), "cap_test");
        File.WriteAllText(Path.Combine(snapshot, "sav", "save01.dat"), "captured");
        DebugTraceHeader header = Header();

        PreparedDebugWorkspace first = DebugWorkspaceManager.Prepare(
            workspace, sessionRoot, snapshot, header, outputPath: null, reset: false, allowCaptureMismatch: false);
        File.WriteAllText(Path.Combine(first.RootPath, "game.erb"), "patched");
        File.WriteAllText(Path.Combine(first.RootPath, "sav", "save01.dat"), "replay-mutated");

        PreparedDebugWorkspace second = DebugWorkspaceManager.Prepare(
            workspace, sessionRoot, snapshot, header, outputPath: null, reset: false, allowCaptureMismatch: false);

        Assert.Equal("patched", File.ReadAllText(Path.Combine(second.RootPath, "game.erb")));
        Assert.Equal("captured", File.ReadAllText(Path.Combine(second.RootPath, "sav", "save01.dat")));
        Assert.True(second.SourceModified);
    }

    [Fact]
    public void CaptureIdMismatchFailsBeforeWorkspaceCreation()
    {
        using var directory = new TemporaryDirectory();
        string sessionRoot = Path.Combine(directory.Path, "session-root");
        string snapshot = Path.Combine(directory.Path, "snapshot");
        string workspace = Path.Combine(directory.Path, "workspace");
        Directory.CreateDirectory(sessionRoot);
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, ".capture-id"), "cap_other");

        DebugTraceException exception = Assert.Throws<DebugTraceException>(() => DebugWorkspaceManager.Prepare(
            workspace, sessionRoot, snapshot, Header(), outputPath: null, reset: false, allowCaptureMismatch: false));

        Assert.Equal(DebugReplayStatuses.CaptureMismatch, exception.Code);
        Assert.False(Directory.Exists(workspace));
    }

    private static DebugTraceHeader Header() => new()
    {
        CaptureId = "cap_test",
        SessionId = "session_test",
        OriginalWorkerEpoch = 1,
        CompatibilityProfile = "v18-compatible",
        SaveLayout = "sav",
        FontSize = 18,
        LineHeight = 19,
        StartupWallClock = DateTimeOffset.UnixEpoch.AddSeconds(1),
        SaveSnapshotComplete = true,
    };
}
