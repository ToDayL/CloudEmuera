using System.Collections.ObjectModel;
using CloudEmuera.Application.Assets;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.RuntimeAdapter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Assets;

/// <summary>
/// Reads presentation assets directly from the private SessionRoot. The
/// realtime protocol carries a reversible path reference; there is no
/// persisted asset index or runtime manifest lookup.
/// </summary>
public sealed class SessionAssetService(
    CloudEmueraDbContext db,
    IResourceAuthorizer authorizer,
    SqliteDatabaseOptions databaseOptions,
    PresentationAssetOptions assetOptions,
    PresentationAssetReadGate readGate) : ISessionAssetService
{
    private static readonly ReadOnlyDictionary<string, string> MediaTypes = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ogg"] = "audio/ogg",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".webm"] = "audio/webm",
        [".flac"] = "audio/flac",
    });

    public async Task<SessionPresentationManifest> GetManifestAsync(CurrentActor actor, string sessionId, CancellationToken cancellationToken = default)
    {
        _ = await LoadContextAsync(actor, sessionId, cancellationToken).ConfigureAwait(false);
        // Assets are referenced by path in realtime output. Keep this endpoint
        // as a protocol-compatible no-op for clients that still request it;
        // fonts are provided by the product runtime font catalog.
        return new SessionPresentationManifest(2, Array.Empty<SessionPresentationAsset>(), Array.Empty<SessionPresentationFont>(), Array.Empty<string>());
    }

    public async Task<SessionAssetRead> OpenReadAsync(CurrentActor actor, string sessionId, string assetId, CancellationToken cancellationToken = default)
    {
        if (!ConsoleAssetIdCodec.TryDecodePath(assetId, out string relativePath) ||
            !MediaTypes.TryGetValue(Path.GetExtension(relativePath), out string? mediaType) ||
            mediaType is null)
            throw NotFound();
        SessionAssetContext context = await LoadContextAsync(actor, sessionId, cancellationToken).ConfigureAwait(false);

        FileStream? stream = null;
        IDisposable? readLease = null;
        try
        {
            stream = OpenSecureRead(context.RootPath, relativePath);
            long actualLength = stream.Length;
            if (actualLength <= 0 || actualLength > assetOptions.MaxAssetBytes)
                throw CapacityExceeded("Presentation asset 超过实例单资源上限。");
            if (!readGate.TryAcquire(actualLength, out readLease))
                throw CapacityExceeded("Presentation asset 并发容量暂时已用尽。");
            if (!HasAllowedSignature(stream, mediaType))
            {
                stream.Dispose();
                throw StorageFailure("Presentation asset MIME 与文件签名不一致。");
            }
            stream.Position = 0;
        }
        catch (SessionAssetException)
        {
            stream?.Dispose();
            readLease?.Dispose();
            throw;
        }
        catch (FileNotFoundException)
        {
            stream?.Dispose();
            readLease?.Dispose();
            throw NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            stream?.Dispose();
            readLease?.Dispose();
            throw NotFound();
        }
        catch (UnauthorizedAccessException exception)
        {
            stream?.Dispose();
            readLease?.Dispose();
            throw new SessionAssetException(SessionAssetErrorCodes.StorageFailure, "Presentation asset 无法安全读取。", 503, exception);
        }
        catch (SqlitePathException exception)
        {
            stream?.Dispose();
            readLease?.Dispose();
            throw new SessionAssetException(SessionAssetErrorCodes.StorageFailure, "Presentation asset 路径未通过安全校验。", 503, exception);
        }
        catch (IOException exception)
        {
            stream?.Dispose();
            readLease?.Dispose();
            throw new SessionAssetException(SessionAssetErrorCodes.StorageFailure, "Presentation asset 无法读取。", 503, exception);
        }

        Stream leasedStream = new LeaseStream(stream, readLease);
        stream = null;
        readLease = null;
        return new SessionAssetRead(assetId, mediaType, leasedStream.Length, null, leasedStream);
    }

    private async Task<SessionAssetContext> LoadContextAsync(CurrentActor actor, string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(actor.UserId)) throw NotFound();
        // Owner filtering happens before the authorizer to keep hidden session
        // IDs on one indistinguishable not-found path.
        SessionAssetRow? session = await db.Sessions.AsNoTracking()
            .Where(row => row.Id == sessionId && row.OwnerUserId == actor.UserId)
            .Select(row => new SessionAssetRow(row.Id, row.OwnerUserId, row.SessionRootPath))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) throw NotFound();
        ResourceAccessDecision decision = await authorizer.AuthorizeAsync(actor, ResourceKind.Session, session.Id, ResourceAction.SessionRead, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (decision != ResourceAccessDecision.Allowed) throw NotFound();

        string rootPath = ResolveRoot(session.SessionRootPath, session.Id);
        try
        {
            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(rootPath, "session-asset-root", RuntimeFileArea.GameContent);
            if (!Directory.Exists(rootPath)) throw NotFound();
            SessionRootProtectedMarker marker = SessionRootProtectedMarkerStore.Read(databaseOptions, session.Id);
            if (!string.Equals(marker.SessionId, session.Id, StringComparison.Ordinal) ||
                !string.Equals(marker.OwnerUserId, session.OwnerUserId, StringComparison.Ordinal) ||
                !SessionRootProtectedMarkerStore.SameRootIdentity(marker, rootPath))
                throw StorageFailure("SessionRoot identity 校验失败。");
        }
        catch (SessionAssetException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or SessionRuntimeException)
        {
            throw new SessionAssetException(SessionAssetErrorCodes.StorageFailure, "SessionRoot 无法安全读取。", 503, exception);
        }
        return new SessionAssetContext(session.Id, rootPath);
    }

    private string ResolveRoot(string relativePath, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains('\0')) throw StorageFailure("SessionRoot 路径无效。");
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment is "" or "." or "..")) throw StorageFailure("SessionRoot 路径无效。");
        string root = Path.GetFullPath(databaseOptions.DataRoot);
        string full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!RuntimePathUtilities.IsStrictlyWithin(full, root) || !relativePath.Equals($"sessions/{sessionId}/root", StringComparison.Ordinal)) throw StorageFailure("SessionRoot 路径不符合持久布局。");
        return full;
    }

    private static FileStream OpenSecureRead(string rootPath, string relativePath)
    {
        if (OperatingSystem.IsLinux())
        {
            using SafeFileHandle root = LinuxFileOperations.OpenDirectory(rootPath);
            string[] parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw NotFound();
            string parentPath = string.Join('/', parts[..^1]);
            using SafeFileHandle parent = LinuxFileOperations.OpenDirectoryPath(root, parentPath, create: false);
            SafeFileHandle? handle = LinuxFileOperations.TryOpenRegularFileAt(parent, parts[^1], readOnly: true);
            if (handle is null) throw NotFound();
            LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(handle);
            int permissions = identity.Mode & 0x1FF;
            if (!identity.IsRegularFile || identity.UserId != LinuxFileOperations.CurrentUserId || identity.LinkCount != 1 || (permissions & 0x05B) != 0)
            {
                handle.Dispose();
                throw StorageFailure("Presentation asset 不是安全的私有普通文件。");
            }
            // openat currently returns a synchronous descriptor.  Keep the
            // stream synchronous; ASP.NET can still stream it without
            // claiming overlapped I/O support the descriptor does not have.
            return LinuxFileOperations.CreateFileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }

        string full = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(full, relativePath, RuntimeFileArea.GameContent);
        RuntimePathUtilities.ThrowIfReparsePoint(full, relativePath, RuntimeFileArea.GameContent, missingIsAllowed: false);
        RuntimePathUtilities.ThrowIfHardLink(full, relativePath, RuntimeFileArea.GameContent);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
    }

    private static SessionAssetException NotFound() => new(SessionAssetErrorCodes.NotFound, "资源不存在。", 404);
    private static SessionAssetException CapacityExceeded(string message) => new(SessionAssetErrorCodes.CapacityExceeded, message, 503);
    private static SessionAssetException StorageFailure(string message, Exception? inner = null) => new(SessionAssetErrorCodes.StorageFailure, message, 503, inner);

    private static bool HasAllowedSignature(Stream stream, string mediaType)
    {
        Span<byte> header = stackalloc byte[16];
        int read = 0;
        while (read < header.Length)
        {
            int current = stream.Read(header[read..]);
            if (current == 0) break;
            read += current;
        }
        stream.Position = 0;
        return mediaType switch
        {
            "image/png" => StartsWith(header[..read], 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            "image/jpeg" => StartsWith(header[..read], 0xFF, 0xD8, 0xFF),
            "image/gif" => StartsWithAscii(header[..read], 0, "GIF87a") || StartsWithAscii(header[..read], 0, "GIF89a"),
            "image/webp" => StartsWithAscii(header[..read], 0, "RIFF") && StartsWithAscii(header[..read], 8, "WEBP"),
            "image/bmp" => StartsWithAscii(header[..read], 0, "BM"),
            "audio/ogg" => StartsWithAscii(header[..read], 0, "OggS"),
            "audio/mpeg" => StartsWithAscii(header[..read], 0, "ID3") || (read >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0),
            "audio/wav" => StartsWithAscii(header[..read], 0, "RIFF") && StartsWithAscii(header[..read], 8, "WAVE"),
            "audio/webm" => StartsWith(header[..read], 0x1A, 0x45, 0xDF, 0xA3),
            "audio/flac" => StartsWithAscii(header[..read], 0, "fLaC"),
            _ => false,
        };
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, params byte[] prefix) => value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static bool StartsWithAscii(ReadOnlySpan<byte> value, int offset, string prefix)
    {
        if (offset < 0 || value.Length < offset + prefix.Length) return false;
        for (int index = 0; index < prefix.Length; index++)
            if (value[offset + index] != prefix[index]) return false;
        return true;
    }

    private sealed record SessionAssetRow(string Id, string OwnerUserId, string SessionRootPath);
    private sealed record SessionAssetContext(string SessionId, string RootPath);

    private sealed class LeaseStream(Stream inner, IDisposable lease) : Stream
    {
        private int disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new NotSupportedException());

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try { inner.Dispose(); }
                finally { lease.Dispose(); }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { lease.Dispose(); }
            }
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
