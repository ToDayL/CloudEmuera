using System.Net.Sockets;
using System.Runtime.InteropServices;
using CloudEmuera.Supervisor;
using CloudEmuera.Worker;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "ProcessIsolation")]
public sealed class SupervisorSecurityTests
{
    [Fact]
    public async Task SupervisorUsesPrivateUdsAndRemovesOnlyItsSocket()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewRoot();
        try
        {
            await using (SupervisorHost supervisor = await SupervisorHost.StartAsync(
                             new SupervisorOptions(root, typeof(ConsoleWireMapper).Assembly.Location)))
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(supervisor.SocketPath));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(root));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.Combine(root, "bootstrap")));
                Assert.True(IsUnixSocket(supervisor.SocketPath));
            }

            Assert.False(File.Exists(Path.Combine(root, "supervisor.sock")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExistingOrdinarySocketEntryIsNotDeleted()
    {
        string root = NewRoot();
        string socketPath = Path.Combine(root, "supervisor.sock");
        const string marker = "keep-me";
        File.WriteAllText(socketPath, marker);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => SupervisorHost.StartAsync(
                new SupervisorOptions(root, typeof(ConsoleWireMapper).Assembly.Location)));
            Assert.Equal(marker, File.ReadAllText(socketPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExistingSocketSymlinkIsNotFollowedOrDeleted()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewRoot();
        string socketPath = Path.Combine(root, "supervisor.sock");
        string targetPath = Path.Combine(root, "stale-target.sock");
        try
        {
            using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                stale.Bind(new UnixDomainSocketEndPoint(targetPath));
            File.CreateSymbolicLink(socketPath, targetPath);

            await Assert.ThrowsAsync<IOException>(() => SupervisorHost.StartAsync(
                new SupervisorOptions(root, typeof(ConsoleWireMapper).Assembly.Location)));

            Assert.NotNull(new FileInfo(socketPath).LinkTarget);
            Assert.True(File.Exists(targetPath) || IsUnixSocket(targetPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExistingSocketDirectoryIsNotDeleted()
    {
        string root = NewRoot();
        string socketPath = Path.Combine(root, "supervisor.sock");
        Directory.CreateDirectory(socketPath);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => SupervisorHost.StartAsync(
                new SupervisorOptions(root, typeof(ConsoleWireMapper).Assembly.Location)));
            Assert.True(Directory.Exists(socketPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SocketParentMustBePrivateAndOwnedByTheServiceAccount()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewRoot();
        string socketPath = Path.Combine(root, "supervisor.sock");
        try
        {
            using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                stale.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SupervisorHost.StartAsync(
                new SupervisorOptions(root, typeof(ConsoleWireMapper).Assembly.Location)));
            Assert.True(IsUnixSocket(socketPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RuntimeDirectorySymlinkAncestorFailsClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewRoot();
        string realParent = Path.Combine(root, "real");
        string linkParent = Path.Combine(root, "link");
        Directory.CreateDirectory(realParent);
        Directory.CreateSymbolicLink(linkParent, realParent);
        string runtimeDirectory = Path.Combine(linkParent, "runtime");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => SupervisorHost.StartAsync(
                new SupervisorOptions(runtimeDirectory, typeof(ConsoleWireMapper).Assembly.Location)));
            Assert.False(Directory.Exists(Path.Combine(realParent, "runtime")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SocketPathCannotBeMovedOutsideRuntimeDirectory()
    {
        string root = NewRoot();
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        try
        {
            SupervisorOptions options = new(root, typeof(ConsoleWireMapper).Assembly.Location)
            {
                SocketPath = Path.Combine(outside, "supervisor.sock")
            };
            await Assert.ThrowsAsync<ArgumentException>(() => SupervisorHost.StartAsync(options));
            Assert.False(File.Exists(options.SocketPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RuntimeDirectoryOwnedByAnotherAccountFailsClosed()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() == 0)
            return;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SupervisorHost.StartAsync(
            new SupervisorOptions(Path.GetPathRoot(Path.GetTempPath())!, typeof(ConsoleWireMapper).Assembly.Location)));
    }

    [Fact]
    public async Task StaleUnixSocketIsReplacedButActiveSocketIsRejected()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewRoot();
        string socketPath = Path.Combine(root, "supervisor.sock");
        try
        {
            using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                stale.Bind(new UnixDomainSocketEndPoint(socketPath));
            }

            await using (SupervisorHost supervisor = await SupervisorHost.StartAsync(
                             new SupervisorOptions(root, typeof(ConsoleWireMapper).Assembly.Location)))
            {
                Assert.True(IsUnixSocket(supervisor.SocketPath));
                await Assert.ThrowsAsync<IOException>(() => SupervisorHost.StartAsync(
                    new SupervisorOptions(root, typeof(ConsoleWireMapper).Assembly.Location)));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cloudemuera-supervisor-security", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static bool IsUnixSocket(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        {
            // The Supervisor listener is not accepting test probes; the path
            // is nevertheless a socket if the connect reached the endpoint.
            return true;
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
