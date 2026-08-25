using System.Collections.ObjectModel;
using System.Text.Json;
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
/// Projects a session's frozen ordinary-file manifest into path-based
/// presentation assets. Logical paths are encoded as opaque ids and never
/// cross the HTTP boundary as request paths. Legacy digest aliases remain
/// readable for old SessionRoots.
/// </summary>
public sealed class SessionAssetService(
    CloudEmueraDbContext db,
    IResourceAuthorizer authorizer,
    SqliteDatabaseOptions databaseOptions,
    PresentationAssetOptions assetOptions,
    PresentationAssetReadGate readGate) : ISessionAssetService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
        SessionAssetContext context = await LoadContextAsync(actor, sessionId, cancellationToken).ConfigureAwait(false);
        SessionPresentationManifest manifest = BuildManifest(context.Entries);
        if (JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions).LongLength > assetOptions.MaxManifestBytes)
            throw new SessionAssetException(SessionAssetErrorCodes.ManifestTooLarge, "Presentation asset 清单超过实例上限。", 413);
        return manifest;
    }

    public async Task<SessionAssetRead> OpenReadAsync(CurrentActor actor, string sessionId, string assetId, CancellationToken cancellationToken = default)
    {
        if (!IsSafeAssetId(assetId)) throw NotFound();
        SessionAssetContext context = await LoadContextAsync(actor, sessionId, cancellationToken).ConfigureAwait(false);
        ManifestAsset? asset = BuildAssets(context.Entries).FirstOrDefault(item => string.Equals(item.AssetId, assetId, StringComparison.Ordinal));
        if (asset is null) throw NotFound();
        if (asset.ByteLength <= 0 || asset.ByteLength > assetOptions.MaxAssetBytes)
            throw CapacityExceeded("Presentation asset 超过实例单资源上限。");

        FileStream? stream = null;
        IDisposable? readLease = null;
        try
        {
            stream = OpenSecureRead(context.RootPath, asset.Path);
            long actualLength = stream.Length;
            if (actualLength <= 0 || actualLength > assetOptions.MaxAssetBytes)
                throw CapacityExceeded("Presentation asset 超过实例单资源上限。");
            if (!readGate.TryAcquire(actualLength, out readLease))
                throw CapacityExceeded("Presentation asset 并发容量暂时已用尽。");
            if (!HasAllowedSignature(stream, asset.MediaType))
            {
                stream.Dispose();
                throw StorageFailure("Presentation asset MIME 与文件签名不一致。");
            }
            stream.Position = 0;
            asset = asset with { ByteLength = actualLength };
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
        return new SessionAssetRead(asset.AssetId, asset.MediaType, asset.ByteLength, asset.ContentDigest, leasedStream);
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
        List<ManifestEntry> entries;
        try
        {
            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(rootPath, "session-asset-root", RuntimeFileArea.GameContent);
            if (!Directory.Exists(rootPath)) throw NotFound();
            SessionRootProtectedMarker marker = SessionRootProtectedMarkerStore.Read(databaseOptions, session.Id);
            if (!string.Equals(marker.SessionId, session.Id, StringComparison.Ordinal) ||
                !string.Equals(marker.OwnerUserId, session.OwnerUserId, StringComparison.Ordinal) ||
                !SessionRootProtectedMarkerStore.SameRootIdentity(marker, rootPath))
                throw StorageFailure("SessionRoot identity 校验失败。");
            string runtimeManifestJson = SessionRootProtectedMarkerStore.ReadRuntimeManifest(databaseOptions, session.Id);
            using JsonDocument runtimeManifest = JsonDocument.Parse(runtimeManifestJson);
            if (runtimeManifest.RootElement.ValueKind != JsonValueKind.Object)
                throw StorageFailure("Session runtime manifest 无效。");
            entries = ParseEntries(runtimeManifestJson);
        }
        catch (SessionAssetException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or SessionRuntimeException)
        {
            throw new SessionAssetException(SessionAssetErrorCodes.StorageFailure, "SessionRoot 无法安全读取。", 503, exception);
        }
        return new SessionAssetContext(session.Id, rootPath, entries);
    }

    private static SessionPresentationManifest BuildManifest(IReadOnlyList<ManifestEntry> entries)
    {
        ManifestAsset[] projected = BuildAssets(entries).ToArray();
        SessionPresentationAsset[] assets = projected.Select(asset => new SessionPresentationAsset(asset.AssetId, asset.MediaType, asset.ByteLength, asset.ContentDigest, null)).ToArray();
        // Game fonts are deliberately absent: the Worker binds the immutable
        // product font catalog, and the browser loads that same face through
        // the runtime-font endpoint. Keep the response fields for protocol
        // compatibility, but never infer a CSS default from game files.
        return new SessionPresentationManifest(2, assets, Array.Empty<SessionPresentationFont>(), Array.Empty<string>());
    }

    private static IEnumerable<ManifestAsset> BuildAssets(IEnumerable<ManifestEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestEntry entry in entries)
        {
            if (!string.Equals(entry.EntryKind, "FILE", StringComparison.OrdinalIgnoreCase) || entry.Bytes < 0) continue;
            string extension = Path.GetExtension(entry.Path);
            if (!MediaTypes.TryGetValue(extension, out string? mediaType) || mediaType is null) continue;
            string assetId = ConsoleAssetIdCodec.EncodePath(entry.Path);
            if (seen.Add(assetId)) yield return new ManifestAsset(assetId, entry.Path, mediaType, entry.Bytes, null);
            string? digest = NormalizeDigest(entry.Digest);
            if (digest is not null)
            {
                string legacyId = $"sha256-{digest["sha256:".Length..].ToLowerInvariant()}";
                if (seen.Add(legacyId)) yield return new ManifestAsset(legacyId, entry.Path, mediaType, entry.Bytes, digest);
            }
        }
    }

    private static List<ManifestEntry> ParseEntries(string json)
    {
        try
        {
            FrozenRuntimeManifest? manifest = JsonSerializer.Deserialize<FrozenRuntimeManifest>(json, JsonOptions);
            if (manifest is null || manifest.Entries is null || manifest.Entries.Count == 0 || manifest.Entries.Count > 200_000) throw new InvalidDataException("runtime manifest entries are invalid");
            var entries = new List<ManifestEntry>(manifest.Entries.Count);
            foreach (FrozenManifestEntry entry in manifest.Entries)
            {
                string entryKind = entry.EntryKind?.ToUpperInvariant() ?? throw new InvalidDataException("runtime manifest entry kind is missing");
                if (!RuntimeRelativePath.TryParse(entry.Path, out RuntimeRelativePath path) ||
                    (entryKind != "FILE" && entryKind != "DIRECTORY") ||
                    entry.Bytes < 0 ||
                    (entryKind == "DIRECTORY" && entry.Bytes != 0))
                    throw new InvalidDataException("runtime manifest path is invalid");
                entries.Add(new ManifestEntry(path.Value, entryKind, entry.Bytes, entry.Digest));
            }
            return entries;
        }
        catch (SessionAssetException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            throw StorageFailure("Session runtime manifest 无效。", exception);
        }
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

    private static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? $"sha256:{value["sha256:".Length..]}" : value.Length == 64 ? $"sha256:{value}" : string.Empty;
        if (!normalized.StartsWith("sha256:", StringComparison.Ordinal) || normalized.Length != "sha256:".Length + 64 || !normalized["sha256:".Length..].All(Uri.IsHexDigit)) return null;
        string digest = normalized["sha256:".Length..];
        return $"sha256:{digest.ToLowerInvariant()}";
    }

    private static bool IsSafeAssetId(string value) =>
        value.Length is > 7 and <= 2_048 &&
        (ConsoleAssetIdCodec.TryDecodePath(value, out _) || ConsoleAssetIdCodec.IsLegacyDigestId(value));
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
    private sealed record SessionAssetContext(string SessionId, string RootPath, IReadOnlyList<ManifestEntry> Entries);
    private sealed record ManifestEntry(string Path, string EntryKind, long Bytes, string? Digest);
    private sealed record ManifestAsset(string AssetId, string Path, string MediaType, long ByteLength, string? ContentDigest);
    private sealed record FrozenManifestEntry(string Path, string EntryKind, long Bytes, string? Digest);
    private sealed record FrozenRuntimeManifest(IReadOnlyList<FrozenManifestEntry> Entries);

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
