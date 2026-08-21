using CloudEmuera.Application.Saves;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Saves;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.RuntimeAdapter;
using System.Runtime.Versioning;

namespace CloudEmuera.Infrastructure.Tests.Saves;

[Trait("Category", "SavePathSecurity")]
[Trait("Category", "SaveOperation")]
[SupportedOSPlatform("linux")]
public sealed class LinuxSessionSaveRootAccessorTests
{
    [Fact]
    public async Task ProtectedRootListsAndReadsNativeSaveFiles()
    {
        using SaveRootFixture fixture = new(RuntimeSaveLayout.Root);
        File.WriteAllText(Path.Combine(fixture.SaveRoot, "global.sav"), "0\n0\n");
        File.SetUnixFileMode(Path.Combine(fixture.SaveRoot, "global.sav"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        LinuxSessionSaveRootAccessor accessor = new(fixture.Options);
        SessionSaveRootSnapshot listing = await accessor.ListAsync(fixture.SessionId);
        Assert.Equal(SessionSaveLayout.Root, listing.Layout);
        SessionSaveItem item = Assert.Single(listing.Items);
        Assert.Equal("global.sav", item.Path);
        Assert.Equal(SessionSaveFileKind.Global, item.Kind);

        SessionSaveFileRead read = await accessor.OpenReadAsync(fixture.SessionId, "global.sav")
            ?? throw new Xunit.Sdk.XunitException("The save file was not opened.");
        await using (read.Content)
        using (var reader = new StreamReader(read.Content))
            Assert.Equal("0\n0\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SavDirectoryAndFileLookupIsCaseInsensitiveWithoutDuplicatingEntries()
    {
        using SaveRootFixture fixture = new(RuntimeSaveLayout.SavDirectory);
        string mixedRoot = Path.Combine(fixture.SessionRoot, "Sav");
        Directory.Move(fixture.SaveRoot, mixedRoot);
        string mixedFile = Path.Combine(mixedRoot, "Save00.SAV");
        File.WriteAllText(mixedFile, "case-save");
        File.SetUnixFileMode(mixedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        LinuxSessionSaveRootAccessor accessor = new(fixture.Options);
        SessionSaveRootSnapshot listing = await accessor.ListAsync(fixture.SessionId);
        SessionSaveItem item = Assert.Single(listing.Items);
        Assert.Equal("Save00.SAV", item.Path);

        SessionSaveFileRead read = await accessor.OpenReadAsync(fixture.SessionId, "save00.sav")
            ?? throw new Xunit.Sdk.XunitException("The mixed-case save file was not opened.");
        await using (read.Content)
        using (var reader = new StreamReader(read.Content))
            Assert.Equal("case-save", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SymlinkSaveEntryIsRejectedWithoutFollowingItsTarget()
    {
        using SaveRootFixture fixture = new(RuntimeSaveLayout.Root);
        string outside = Path.Combine(fixture.Root, "outside.sav");
        File.WriteAllText(outside, "0\n0\n");
        File.CreateSymbolicLink(Path.Combine(fixture.SaveRoot, "save00.sav"), outside);

        LinuxSessionSaveRootAccessor accessor = new(fixture.Options);
        SessionSaveException exception = await Assert.ThrowsAsync<SessionSaveException>(() => accessor.ListAsync(fixture.SessionId));
        Assert.Equal(SaveErrorCodes.SessionRootInvalid, exception.Code);
        Assert.Equal("0\n0\n", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task HardlinkSaveEntryIsRejected()
    {
        using SaveRootFixture fixture = new(RuntimeSaveLayout.Root);
        string outside = Path.Combine(fixture.Root, "outside.sav");
        File.WriteAllText(outside, "0\n0\n");
        using (Microsoft.Win32.SafeHandles.SafeFileHandle source = LinuxFileOperations.OpenDirectory(fixture.Root))
        using (Microsoft.Win32.SafeHandles.SafeFileHandle destination = LinuxFileOperations.OpenDirectory(fixture.SaveRoot))
        {
            LinuxFileOperations.LinkAtFromName(source, "outside.sav", destination, "save00.sav");
        }

        LinuxSessionSaveRootAccessor accessor = new(fixture.Options);
        SessionSaveException exception = await Assert.ThrowsAsync<SessionSaveException>(() => accessor.ListAsync(fixture.SessionId));
        Assert.Equal(SaveErrorCodes.SessionRootInvalid, exception.Code);
    }

    [Fact]
    public async Task WorldReadableSaveDirectoryIsRejected()
    {
        using SaveRootFixture fixture = new(RuntimeSaveLayout.Root);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(fixture.SaveRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);

        LinuxSessionSaveRootAccessor accessor = new(fixture.Options);
        SessionSaveException exception = await Assert.ThrowsAsync<SessionSaveException>(() => accessor.ListAsync(fixture.SessionId));
        Assert.Equal(SaveErrorCodes.SessionRootInvalid, exception.Code);
    }

    [Fact]
    public async Task MalformedProtectedMarkerMapsToSessionRootFailure()
    {
        using SaveRootFixture fixture = new(RuntimeSaveLayout.Root);
        await File.WriteAllTextAsync(Path.Combine(fixture.Container, "metadata", "session-root.json"), "{");

        LinuxSessionSaveRootAccessor accessor = new(fixture.Options);
        SessionSaveException exception = await Assert.ThrowsAsync<SessionSaveException>(() => accessor.ListAsync(fixture.SessionId));
        Assert.Equal(SaveErrorCodes.SessionRootInvalid, exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task SaveListingStopsAtConfiguredFileCount()
    {
        using SaveRootFixture fixture = new(RuntimeSaveLayout.Root);
        foreach (string name in new[] { "global.sav", "save00.sav" })
        {
            string path = Path.Combine(fixture.SaveRoot, name);
            await File.WriteAllTextAsync(path, "0\n0\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        InstanceCapacityOptions capacity = new() { MaxSaveListedFiles = 1 };
        LinuxSessionSaveRootAccessor accessor = new(fixture.Options, capacity);

        SessionSaveException exception = await Assert.ThrowsAsync<SessionSaveException>(() => accessor.ListAsync(fixture.SessionId));
        Assert.Equal(SaveErrorCodes.ListLimitExceeded, exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    private sealed class SaveRootFixture : IDisposable
    {
        public SaveRootFixture(RuntimeSaveLayout layout)
        {
            Root = Directory.CreateTempSubdirectory("cloudemuera-save-accessor-").FullName;
            Options = new SqliteDatabaseOptions { DataRoot = Root };
            SessionId = "sess_accessor";
            string sessions = Path.Combine(Root, "sessions");
            Container = Path.Combine(sessions, SessionId);
            SessionRoot = Path.Combine(Container, "root");
            SaveRoot = layout == RuntimeSaveLayout.Root ? SessionRoot : Path.Combine(SessionRoot, "sav");
            Directory.CreateDirectory(SaveRoot);
            Directory.CreateDirectory(Path.Combine(Container, "metadata"));
            SetPrivateDirectory(Root);
            SetPrivateDirectory(sessions);
            SetPrivateDirectory(Container);
            SetPrivateDirectory(SessionRoot);
            SetPrivateDirectory(SaveRoot);
            File.WriteAllText(Path.Combine(SessionRoot, "emuera.config"), layout == RuntimeSaveLayout.Root ? "Use sav folder:NO\n" : "Use sav folder:YES\n");
            File.SetUnixFileMode(Path.Combine(SessionRoot, "emuera.config"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            SessionRootProtectedMarkerStore.Write(
                Options,
                Container,
                SessionId,
                "usr_fixture",
                "game_fixture",
                1,
                Digest("content"),
                Digest("manifest"),
                Digest("manifest"),
                layout,
                "test-runtime",
                DateTimeOffset.UtcNow,
                SessionRoot);
        }

        public string Root { get; }
        public SqliteDatabaseOptions Options { get; }
        public string SessionId { get; }
        public string Container { get; }
        public string SessionRoot { get; }
        public string SaveRoot { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }

        private static void SetPrivateDirectory(string path)
        {
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private static string Digest(string value) =>
            $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
    }
}
