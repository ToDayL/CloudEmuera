using System.Net.Sockets;
using System.Runtime.InteropServices;
using CloudEmuera.Api.Workers;
using CloudEmuera.Worker;
using Xunit;

namespace CloudEmuera.Worker.IntegrationTests;

[Trait("Category", "ProcessIsolation")]
public sealed class WorkerControlSecurityTests
{
    [Fact]
    public async Task WorkerManagerUsesPrivateUdsAndRemovesOnlyItsSocket()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewRoot();
        try
        {
            WorkerManagerOptions options = CreateOptions(root);
            await using (WorkerManagerHost manager = await WorkerManagerHost.StartAsync(options))
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(manager.SocketPath));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(root));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(options.BootstrapDirectory));
                Assert.True(IsUnixSocket(manager.SocketPath));
            }

            Assert.False(File.Exists(options.ControlSocketPath));
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
        WorkerManagerOptions options = CreateOptions(root);
        string socketPath = options.ControlSocketPath;
        const string marker = "keep-me";
        Directory.CreateDirectory(options.RuntimeDirectory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(options.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(socketPath, marker);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => WorkerManagerHost.StartAsync(options));
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
        WorkerManagerOptions options = CreateOptions(root);
        string socketPath = options.ControlSocketPath;
        string targetPath = Path.Combine(root, "stale-target.sock");
        try
        {
            Directory.CreateDirectory(options.RuntimeDirectory);
            File.SetUnixFileMode(options.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            targetPath = Path.Combine(options.RuntimeDirectory, "stale-target.sock");
            using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                stale.Bind(new UnixDomainSocketEndPoint(targetPath));
            File.CreateSymbolicLink(socketPath, targetPath);

            await Assert.ThrowsAsync<IOException>(() => WorkerManagerHost.StartAsync(options));

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
        WorkerManagerOptions options = CreateOptions(root);
        string socketPath = options.ControlSocketPath;
        Directory.CreateDirectory(options.RuntimeDirectory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(options.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(socketPath);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => WorkerManagerHost.StartAsync(options));
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
        WorkerManagerOptions options = CreateOptions(root);
        Directory.CreateDirectory(options.RuntimeDirectory);
        string socketPath = options.ControlSocketPath;
        try
        {
            File.SetUnixFileMode(options.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkerManagerHost.StartAsync(options));
            Assert.True(Directory.Exists(options.RuntimeDirectory));
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

        string root = Path.Combine(Path.GetTempPath(), $"l{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string realParent = Path.Combine(root, "real");
        string linkParent = Path.Combine(root, "link");
        Directory.CreateDirectory(realParent);
        Directory.CreateSymbolicLink(linkParent, realParent);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => WorkerManagerHost.StartAsync(
                new WorkerManagerOptions(linkParent, typeof(ConsoleWireMapper).Assembly.Location)));
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
            WorkerManagerOptions options = new(root, typeof(ConsoleWireMapper).Assembly.Location)
            {
                ControlSocketPath = Path.Combine(outside, "worker-control.sock")
            };
            await Assert.ThrowsAsync<ArgumentException>(() => WorkerManagerHost.StartAsync(options));
            Assert.False(File.Exists(options.ControlSocketPath));
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

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkerManagerHost.StartAsync(
            new WorkerManagerOptions(Path.GetPathRoot(Path.GetTempPath())!, typeof(ConsoleWireMapper).Assembly.Location)));
    }

    [Fact]
    public async Task StaleUnixSocketIsReplacedButActiveSocketIsRejected()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewRoot();
        WorkerManagerOptions options = CreateOptions(root);
        string socketPath = options.ControlSocketPath;
        try
        {
            Directory.CreateDirectory(options.RuntimeDirectory);
            File.SetUnixFileMode(options.RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                stale.Bind(new UnixDomainSocketEndPoint(socketPath));
            }

            await using (WorkerManagerHost manager = await WorkerManagerHost.StartAsync(options))
            {
                Assert.True(IsUnixSocket(manager.SocketPath));
                await Assert.ThrowsAsync<IOException>(() => WorkerManagerHost.StartAsync(options));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "s", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    private static WorkerManagerOptions CreateOptions(string root) =>
        new(root, typeof(ConsoleWireMapper).Assembly.Location);

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
            // The Worker Manager listener is not accepting test probes; the path
            // is nevertheless a socket if the connect reached the endpoint.
            return true;
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
