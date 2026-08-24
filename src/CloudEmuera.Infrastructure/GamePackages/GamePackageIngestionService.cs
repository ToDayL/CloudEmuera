using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Capacity;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.GamePackages;

public sealed class GamePackageIngestionService(
    CloudEmueraDbContext db,
    GamePackageStorageOptions storageOptions,
    TimeProvider timeProvider,
    IGamePackageIngestionFaultInjector? faultInjector = null,
    InstanceCapacityOptions? capacityOptions = null) : IGamePackageIngestionService
{
    private const string ArchiveFileName = "archive.zip.part";
    private const string CandidateDirectoryName = "candidate.work";
    private const string ContentDirectoryName = "content";
    private const string ReadyDirectoryName = "ready";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public async Task<IngestedGamePackage> IngestAsync(
        GamePackageIngestionRequest request,
        GamePackageIngestionLimits? requestedLimits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerUserId);
        ArgumentNullException.ThrowIfNull(request.Content);
        GamePackageIngestionLimits limits = requestedLimits ?? new();
        limits.Validate();
        storageOptions.Validate();
        InstanceCapacityOptions capacity = EffectiveCapacity;
        // The API composition root validates the injected deployment-wide
        // options before opening listeners. Standalone infrastructure callers
        // intentionally keep the historical StorageOptions seam for staging
        // and free-space fixtures, so their derived compatibility profile is
        // not revalidated against the larger production defaults here.
        if (capacityOptions is not null)
            capacity.Validate();
        GamePackageIngestionLimits effectiveLimits = limits with
        {
            MaxArchiveBytes = Math.Min(limits.MaxArchiveBytes, capacity.MaxArchiveBytes),
            MaxExpandedBytes = Math.Min(limits.MaxExpandedBytes, capacity.MaxExpandedBytes),
            MaxSingleFileBytes = Math.Min(limits.MaxSingleFileBytes, capacity.MaxArchiveSingleFileBytes),
            MaxEntryCount = Math.Min(limits.MaxEntryCount, capacity.MaxArchiveEntryCount),
        };
        effectiveLimits.Validate();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveLimits.MaxDuration);
        string ingestionId = $"ing_{Guid.CreateVersion7():N}";
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now + storageOptions.ReadyLifetime;
        string relativePath = $"games/staging/{ingestionId}";
        long effectiveArchiveLimit;
        long effectiveExpandedLimit;
        await using (SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, deadline.Token).ConfigureAwait(false))
        {
            bool ownerActive = await db.Users.AsNoTracking()
                .AnyAsync(user => user.Id == request.OwnerUserId && user.Status == UserStatus.Active, deadline.Token)
                .ConfigureAwait(false);
            if (!ownerActive)
                throw new GamePackageIngestionException("OWNER_NOT_ACTIVE", "The package owner is unavailable.");
            effectiveArchiveLimit = effectiveLimits.MaxArchiveBytes;
            effectiveExpandedLimit = effectiveLimits.MaxExpandedBytes;
            long reservation = checked(effectiveArchiveLimit + effectiveExpandedLimit);
            long active = await db.GamePackageIngestions.SumAsync(row => (long?)row.ReservedBytes, deadline.Token).ConfigureAwait(false) ?? 0;
            if (active > capacity.MaxStagingReservedBytes - reservation)
                throw new GamePackageIngestionException(GamePackageRejectionCodes.StagingBudgetExhausted, "The staging budget is exhausted.");
            EnsureFreeSpace(reservation);
            db.GamePackageIngestions.Add(new GamePackageIngestionRow
            {
                Id = ingestionId,
                OwnerUserId = request.OwnerUserId,
                Status = GamePackageIngestionStatus.Reserved,
                StagingPath = relativePath,
                ReservedBytes = reservation,
                LimitsJson = JsonSerializer.Serialize(effectiveLimits, JsonOptions),
                SummaryJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = expiresAt,
            });
            await db.SaveChangesAsync(deadline.Token).ConfigureAwait(false);
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        }

        using var stagingStore = new LinuxGamePackageStagingStore(storageOptions);
        SafeFileHandle? ingestionRoot = null;
        try
        {
            ingestionRoot = stagingStore.CreateIngestion(ingestionId);
            await TransitionAsync(ingestionId, GamePackageIngestionStatus.Reserved, GamePackageIngestionStatus.Receiving, deadline.Token).ConfigureAwait(false);
            using (SafeFileHandle archiveTarget = LinuxGamePackageStagingStore.CreateFile(ingestionRoot, ArchiveFileName))
            {
                (long archiveBytes, string archiveDigest) = await ReceiveAsync(request.Content, archiveTarget, effectiveArchiveLimit, deadline.Token).ConfigureAwait(false);
                await UpdateArchiveAsync(ingestionId, archiveBytes, archiveDigest, deadline.Token).ConfigureAwait(false);

                using SafeFileHandle archiveSource = LinuxGamePackageStagingStore.OpenFile(ingestionRoot, ArchiveFileName);
                IReadOnlyList<ValidatedZipEntry> inspected = ZipStructureInspector.Inspect(LinuxGamePackageStagingStore.DescriptorPath(archiveSource), effectiveLimits);
                string? wrapperPrefix = SingleRootDirectoryPrefix(inspected);
                IReadOnlyDictionary<string, PreparedEntry> prepared = Preflight(inspected, effectiveLimits, wrapperPrefix);
                await TransitionAsync(ingestionId, GamePackageIngestionStatus.Inspecting, GamePackageIngestionStatus.Extracting, deadline.Token).ConfigureAwait(false);

                using SafeFileHandle candidate = LinuxGamePackageStagingStore.CreateDirectory(ingestionRoot, CandidateDirectoryName);
                using SafeFileHandle content = LinuxGamePackageStagingStore.CreateDirectory(candidate, ContentDirectoryName);
                ExtractionResult extraction = await ExtractAsync(LinuxGamePackageStagingStore.DescriptorPath(archiveSource), content, prepared, effectiveLimits, deadline.Token).ConfigureAwait(false);
                await TransitionAsync(ingestionId, GamePackageIngestionStatus.Extracting, GamePackageIngestionStatus.Analyzing, deadline.Token).ConfigureAwait(false);
                if (faultInjector is not null) await faultInjector.InjectAsync(GamePackageIngestionFaultPoint.BeforeAnalyze, deadline.Token).ConfigureAwait(false);
                var encodingDiagnostics = new DiagnosticCollector(effectiveLimits.MaxDiagnostics);
                extraction = ConvertTextEncodings(content, extraction, effectiveLimits, encodingDiagnostics);
                List<GamePackageDiagnostic> conversionDiagnostics = encodingDiagnostics.Build();
                GamePackageManifest manifest = Analyze(archiveBytes, archiveDigest, content, extraction, effectiveLimits);
                if (conversionDiagnostics.Count > 0)
                    manifest = manifest with { Diagnostics = manifest.Diagnostics.Concat(conversionDiagnostics).ToArray() };
                await AdjustReservationAsync(ingestionId, archiveBytes, extraction.TotalBytes, deadline.Token).ConfigureAwait(false);
                string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
                using (SafeFileHandle manifestHandle = LinuxGamePackageStagingStore.CreateFile(candidate, "manifest.json"))
                await using (FileStream manifestStream = LinuxGamePackageStagingStore.Stream(manifestHandle, FileAccess.Write, async: true))
                await using (var writer = new StreamWriter(manifestStream, new UTF8Encoding(false), leaveOpen: false))
                {
                    await writer.WriteAsync(manifestJson.AsMemory(), deadline.Token).ConfigureAwait(false);
                    await writer.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                if (faultInjector is not null) await faultInjector.InjectAsync(GamePackageIngestionFaultPoint.BeforePublishRename, deadline.Token).ConfigureAwait(false);
                LinuxGamePackageStagingStore.Rename(ingestionRoot, CandidateDirectoryName, ReadyDirectoryName);
                await MarkReadyAsync(ingestionId, request.OwnerUserId, request.RequestId, manifest, deadline.Token).ConfigureAwait(false);
                return new(ingestionId, request.OwnerUserId, expiresAt, manifest);
            }
        }
        catch (OperationCanceledException exception)
        {
            string code = cancellationToken.IsCancellationRequested ? GamePackageRejectionCodes.IngestionCancelled : GamePackageRejectionCodes.IngestionDeadlineExceeded;
            ingestionRoot?.Dispose();
            ingestionRoot = null;
            await FailAndCleanAsync(ingestionId, stagingStore, request.OwnerUserId, request.RequestId, code).ConfigureAwait(false);
            throw new GamePackageIngestionException(code, "Game package ingestion was cancelled.", innerException: exception);
        }
        catch (GamePackageIngestionException exception)
        {
            ingestionRoot?.Dispose();
            ingestionRoot = null;
            await FailAndCleanAsync(ingestionId, stagingStore, request.OwnerUserId, request.RequestId, exception.Code).ConfigureAwait(false);
            throw;
        }
        catch (InvalidDataException exception)
        {
            ingestionRoot?.Dispose();
            ingestionRoot = null;
            await FailAndCleanAsync(ingestionId, stagingStore, request.OwnerUserId, request.RequestId, GamePackageRejectionCodes.ArchiveCorrupt).ConfigureAwait(false);
            throw new GamePackageIngestionException(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP archive is corrupt.", innerException: exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ingestionRoot?.Dispose();
            ingestionRoot = null;
            await FailAndCleanAsync(ingestionId, stagingStore, request.OwnerUserId, request.RequestId, GamePackageRejectionCodes.StagingIoFailed).ConfigureAwait(false);
            throw new GamePackageIngestionException(GamePackageRejectionCodes.StagingIoFailed, "Game package staging failed.", innerException: exception);
        }
        catch (Exception exception)
        {
            ingestionRoot?.Dispose();
            ingestionRoot = null;
            await FailAndCleanAsync(ingestionId, stagingStore, request.OwnerUserId, request.RequestId, GamePackageRejectionCodes.IngestionCommitFailed).ConfigureAwait(false);
            throw new GamePackageIngestionException(GamePackageRejectionCodes.IngestionCommitFailed, "Game package ingestion commit failed.", innerException: exception);
        }
        finally { ingestionRoot?.Dispose(); }
    }

    public async Task<GamePackageConsumption> BeginConsumeAsync(string ingestionId, string ownerUserId, string expectedContentDigest, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ingestionId && item.OwnerUserId == ownerUserId
                && item.Status == GamePackageIngestionStatus.Ready && item.ContentDigest == expectedContentDigest
                && item.ExpiresAt > now, cancellationToken).ConfigureAwait(false)
            ?? throw new GamePackageIngestionException("INGESTION_NOT_READY", "The package ingestion is not ready for consumption.");
        ValidateStagingPath(row, ingestionId);
        using var stagingStore = new LinuxGamePackageStagingStore(storageOptions);
        using SafeFileHandle root = stagingStore.OpenIngestion(ingestionId);
        using SafeFileHandle ready = LinuxGamePackageStagingStore.OpenDirectory(root, ReadyDirectoryName);
        string manifest;
        using (SafeFileHandle manifestHandle = LinuxGamePackageStagingStore.OpenFile(ready, "manifest.json"))
        using (FileStream manifestStream = LinuxGamePackageStagingStore.Stream(manifestHandle, FileAccess.Read))
        using (var reader = new StreamReader(manifestStream, new UTF8Encoding(false, true)))
            manifest = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        GamePackageManifest parsed = JsonSerializer.Deserialize<GamePackageManifest>(manifest, JsonOptions)
            ?? throw new GamePackageIngestionException(GamePackageRejectionCodes.StagedContentChanged, "The staged manifest is invalid.");
        if (!string.Equals(parsed.ContentDigest, expectedContentDigest, StringComparison.Ordinal))
            throw new GamePackageIngestionException(GamePackageRejectionCodes.StagedContentChanged, "The staged manifest digest changed.");
        SafeFileHandle content = LinuxGamePackageStagingStore.OpenDirectory(ready, ContentDirectoryName);
        int changed = await db.GamePackageIngestions
            .Where(item => item.Id == ingestionId && item.OwnerUserId == ownerUserId && item.Status == GamePackageIngestionStatus.Ready
                && item.ContentDigest == expectedContentDigest && item.ExpiresAt > now && item.StateVersion == row.StateVersion)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, GamePackageIngestionStatus.Consuming)
                .SetProperty(item => item.ExpiresAt, now + storageOptions.ConsumptionLifetime)
                .SetProperty(item => item.UpdatedAt, now).SetProperty(item => item.StateVersion, item => item.StateVersion + 1), cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            content.Dispose();
            throw new GamePackageIngestionException("INGESTION_NOT_READY", "The package ingestion is not ready for consumption.");
        }
        return new(ingestionId, ownerUserId, expectedContentDigest, content, manifest);
    }

    public async Task<DateTimeOffset> RenewConsumeAsync(string ingestionId, string ownerUserId, string expectedContentDigest, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now + storageOptions.ConsumptionLifetime;
        int changed = await db.GamePackageIngestions
            .Where(item => item.Id == ingestionId && item.OwnerUserId == ownerUserId
                && item.ContentDigest == expectedContentDigest
                && item.Status == GamePackageIngestionStatus.Consuming
                && item.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ExpiresAt, expiresAt)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.StateVersion, item => item.StateVersion + 1), cancellationToken)
            .ConfigureAwait(false);
        if (changed != 1)
            throw new GamePackageIngestionException("INGESTION_LEASE_EXPIRED", "The package consumption lease is no longer renewable.");
        return expiresAt;
    }

    public async Task CompleteConsumeAsync(string ingestionId, string ownerUserId, CancellationToken cancellationToken = default)
    {
        GamePackageIngestionRow? row = await db.GamePackageIngestions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ingestionId && item.OwnerUserId == ownerUserId, cancellationToken).ConfigureAwait(false);
        if (row is null) throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion is not being consumed.");
        if (row.Status == GamePackageIngestionStatus.Consumed) return;
        if (row.Status != GamePackageIngestionStatus.Consuming)
            throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion is not being consumed.");
        ValidateStagingPath(row, ingestionId);
        await FinishAsync(ingestionId, ownerUserId, GamePackageIngestionStatus.Consuming,
            row.StateVersion, GamePackageIngestionStatus.Consumed, cancellationToken).ConfigureAwait(false);
        await TryCleanTerminalAsync(ingestionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task AbandonAsync(string ingestionId, string ownerUserId, CancellationToken cancellationToken = default)
    {
        GamePackageIngestionRow? row = await db.GamePackageIngestions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ingestionId && item.OwnerUserId == ownerUserId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.Status is GamePackageIngestionStatus.Consumed or GamePackageIngestionStatus.Abandoned) return;
        ValidateStagingPath(row, ingestionId);
        if (faultInjector is not null) await faultInjector.InjectAsync(GamePackageIngestionFaultPoint.BeforeAbandonCas, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        int changed = await db.GamePackageIngestions
            .Where(item => item.Id == ingestionId && item.OwnerUserId == ownerUserId
                && item.Status == row.Status && item.StateVersion == row.StateVersion)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, GamePackageIngestionStatus.Abandoned)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.StateVersion, item => item.StateVersion + 1), cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            GamePackageIngestionStatus? current = await db.GamePackageIngestions.AsNoTracking()
                .Where(item => item.Id == ingestionId && item.OwnerUserId == ownerUserId)
                .Select(item => (GamePackageIngestionStatus?)item.Status)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (current is null or GamePackageIngestionStatus.Consumed or GamePackageIngestionStatus.Abandoned) return;
            throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion state changed concurrently.");
        }
        await TryCleanTerminalAsync(ingestionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(
        string id,
        string owner,
        GamePackageIngestionStatus expected,
        int expectedStateVersion,
        GamePackageIngestionStatus next,
        CancellationToken token)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int changed = await db.GamePackageIngestions.Where(row => row.Id == id && row.OwnerUserId == owner
                && row.Status == expected && row.StateVersion == expectedStateVersion)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, next).SetProperty(row => row.ReservedBytes, 0L)
                .SetProperty(row => row.ReservationReleasedAt, now).SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.StateVersion, row => row.StateVersion + 1), token).ConfigureAwait(false);
        if (changed != 1) throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion state changed concurrently.");
    }

    private async Task TryCleanTerminalAsync(string ingestionId, CancellationToken token)
    {
        using var stagingStore = new LinuxGamePackageStagingStore(storageOptions);
        if (!stagingStore.DeleteIngestion(ingestionId)) return;
        DateTimeOffset now = timeProvider.GetUtcNow();
        await db.GamePackageIngestions.Where(row => row.Id == ingestionId && row.CleanupCompletedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReservedBytes, 0L)
                .SetProperty(row => row.ReservationReleasedAt, row => row.ReservationReleasedAt ?? now)
                .SetProperty(row => row.CleanupCompletedAt, now).SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.StateVersion, row => row.StateVersion + 1), token).ConfigureAwait(false);
    }

    private static Dictionary<string, PreparedEntry> Preflight(IReadOnlyList<ValidatedZipEntry> entries, GamePackageIngestionLimits limits, string? wrapperPrefix)
    {
        var pathPolicy = new ZipEntryPathPolicy(limits);
        var result = new Dictionary<string, PreparedEntry>(StringComparer.Ordinal);
        long total = 0;
        foreach (ValidatedZipEntry entry in entries)
        {
            bool directory = entry.RawName.EndsWith('/');
            string logical = StripWrapper(entry.RawName, wrapperPrefix);
            if (logical.Length == 0) continue; // the single-root wrapper directory entry itself
            string path = pathPolicy.Add(logical, directory);
            if (!directory)
            {
                if (entry.ExpandedBytes > limits.MaxSingleFileBytes) Reject(GamePackageRejectionCodes.EntryTooLarge, "ZIP entry exceeds the single-file limit.", path);
                total = checked(total + entry.ExpandedBytes);
                if (total > limits.MaxExpandedBytes) Reject(GamePackageRejectionCodes.ExpandedSizeExceeded, "ZIP expanded size exceeds the limit.", path);
                double ratio = entry.ExpandedBytes == 0 ? 1 : entry.CompressedBytes == 0 ? double.PositiveInfinity : (double)entry.ExpandedBytes / entry.CompressedBytes;
                if (ratio > limits.MaxCompressionRatio) Reject(GamePackageRejectionCodes.CompressionRatioExceeded, "ZIP entry compression ratio exceeds the limit.", path);
            }
            string rawLogical = directory ? logical.TrimEnd('/') : logical;
            result.Add(entry.RawName, new(entry, path, directory, !string.Equals(path, rawLogical, StringComparison.Ordinal)));
        }
        return result;
    }

    /// <summary>
    /// Many era-game ZIPs wrap the game in a single top-level folder. When every
    /// entry shares exactly one top-level directory and that directory contains at
    /// least one file, treat it as a distribution wrapper and strip the prefix so
    /// ERB/CSV/emuera.config land at the workspace root. Multiple top-level entries
    /// (for example __MACOSX/ next to the game) are never flattened.
    /// </summary>
    private static string? SingleRootDirectoryPrefix(IReadOnlyList<ValidatedZipEntry> entries)
    {
        string? wrapper = null;
        bool hasNestedFile = false;
        foreach (ValidatedZipEntry entry in entries)
        {
            string raw = entry.RawName;
            int slash = raw.IndexOf('/');
            string top = slash < 0 ? raw : raw[..slash];
            if (!IsUsableWrapperSegment(top)) return null;
            if (wrapper is null) wrapper = top;
            else if (!string.Equals(wrapper, top, StringComparison.Ordinal)) return null;
            if (slash >= 0 && !raw.EndsWith('/')) hasNestedFile = true;
        }
        return wrapper is null || !hasNestedFile ? null : $"{wrapper}/";
    }

    /// <summary>
    /// A distribution wrapper must be a plain directory name. Traversal or
    /// absolute-style segments (".", "..", "C:", backslashes, control characters)
    /// are never treated as a wrapper so the path policy still rejects them.
    /// </summary>
    private static bool IsUsableWrapperSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or "..") return false;
        foreach (char character in segment)
        {
            if (char.IsControl(character) || character is '/' or '\\' or ':' or '\0') return false;
        }
        return true;
    }

    private static string StripWrapper(string rawName, string? wrapperPrefix)
    {
        if (wrapperPrefix is null) return rawName;
        if (rawName.Length <= wrapperPrefix.Length)
        {
            return rawName.Length == wrapperPrefix.Length && rawName.StartsWith(wrapperPrefix, StringComparison.Ordinal) ? string.Empty : rawName;
        }
        return rawName.StartsWith(wrapperPrefix, StringComparison.Ordinal) ? rawName[wrapperPrefix.Length..] : rawName;
    }

    private static async Task<ExtractionResult> ExtractAsync(string archivePath, SafeFileHandle contentRoot, IReadOnlyDictionary<string, PreparedEntry> prepared, GamePackageIngestionLimits limits, CancellationToken token)
    {
        var files = new List<ExtractedFile>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var normalizedPaths = new HashSet<string>(StringComparer.Ordinal);
        string? metadataMismatchPath = null;
        long total = 0;
        using FileStream archive = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (PreparedEntry item in prepared.Values.OrderBy(value => value.Entry.LocalHeaderOffset))
        {
            token.ThrowIfCancellationRequested();
            if (item.PathNormalized) normalizedPaths.Add(item.Path);
            if (item.Directory)
            {
                using SafeFileHandle ignored = LinuxGamePackageStagingStore.OpenDirectoryPath(contentRoot, item.Path, create: true);
                directories.Add(item.Path);
                continue;
            }
            string? parentPath = Path.GetDirectoryName(item.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parentPath))
            {
                using SafeFileHandle ignored = LinuxGamePackageStagingStore.OpenDirectoryPath(contentRoot, parentPath.Replace(Path.DirectorySeparatorChar, '/'), create: true);
                AddParents(directories, item.Path);
            }
            using SafeFileHandle targetHandle = LinuxGamePackageStagingStore.OpenFilePath(contentRoot, item.Path, create: true);
            await using FileStream target = LinuxGamePackageStagingStore.Stream(targetHandle, FileAccess.Write, async: true);
            archive.Position = item.Entry.DataOffset;
            using var bounded = new BoundedReadStream(archive, item.Entry.CompressedBytes);
            using Stream source = item.Entry.Method == 8
                ? new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: false)
                : bounded;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long fileBytes = 0;
            uint crc32 = uint.MaxValue;
            while (true)
            {
                int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                if (fileBytes > limits.MaxSingleFileBytes - read) Reject(GamePackageRejectionCodes.EntryTooLarge, "ZIP entry actual size exceeds the single-file limit.", item.Path);
                if (total > limits.MaxExpandedBytes - read) Reject(GamePackageRejectionCodes.ExpandedSizeExceeded, "ZIP actual expanded size exceeds the limit.", item.Path);
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                crc32 = UpdateCrc32(crc32, buffer.AsSpan(0, read));
                fileBytes += read;
                total += read;
            }
            await target.FlushAsync(token).ConfigureAwait(false);
            if (fileBytes != item.Entry.ExpandedBytes || (crc32 ^ uint.MaxValue) != item.Entry.Crc32)
                metadataMismatchPath ??= item.Path;
            if (LinuxFileOperations.ReadIdentity(target.SafeFileHandle).LinkCount != 1)
                Reject(GamePackageRejectionCodes.StagedContentChanged, "Extracted files must not share an inode.", item.Path);
            files.Add(new(item.Path, fileBytes, Digest(hash.GetHashAndReset())));
        }
        if (metadataMismatchPath is not null)
            Reject(GamePackageRejectionCodes.ArchiveCorrupt, "ZIP entry actual size or CRC32 differs from its declaration.", metadataMismatchPath);
        return new(total, files, directories.Order(StringComparer.Ordinal).ToArray(), normalizedPaths.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Normalizes UTF-16/UTF-32 text files (with BOM) to UTF-8 inside the private
    /// staging copy so validation, the file viewer, the runtime and the content digest
    /// all agree on a canonical encoding (ADR-0014). Shift-JIS and UTF-8 files are
    /// left untouched because the upstream runtime auto-detects them.
    /// </summary>
    private static ExtractionResult ConvertTextEncodings(
        SafeFileHandle contentRoot,
        ExtractionResult extraction,
        GamePackageIngestionLimits limits,
        DiagnosticCollector diagnostics)
    {
        bool convertedAny = false;
        var files = new List<ExtractedFile>(extraction.Files.Count);
        long total = 0;
        foreach (ExtractedFile file in extraction.Files)
        {
            if (IsText(file.Path)
                && TryConvertUtf16Or32ToUtf8(contentRoot, file.Path, limits, out (long Bytes, string Digest) rewritten))
            {
                files.Add(file with { Bytes = rewritten.Bytes, Digest = rewritten.Digest });
                total = checked(total + rewritten.Bytes);
                convertedAny = true;
                diagnostics.Add("TEXT_ENCODING_CONVERTED", GamePackageDiagnosticSeverity.Info, "ENCODING", file.Path,
                    "gamePackage.diagnostic.textEncodingConverted", publishBlocking: false);
            }
            else
            {
                files.Add(file);
                total = checked(total + file.Bytes);
            }
        }
        return convertedAny
            ? new ExtractionResult(total, files, extraction.Directories, extraction.NormalizedPaths)
            : extraction;
    }

    private static bool TryConvertUtf16Or32ToUtf8(
        SafeFileHandle contentRoot,
        string logicalPath,
        GamePackageIngestionLimits limits,
        out (long Bytes, string Digest) rewritten)
    {
        rewritten = default;
        byte[] source = ReadAll(contentRoot, logicalPath);
        Encoding? sourceEncoding = DetectUtf16Or32(source);
        if (sourceEncoding is null) return false;
        byte[] utf8;
        try
        {
            string text = sourceEncoding.GetString(source);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            utf8 = new UTF8Encoding(false, true).GetBytes(text);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or EncoderFallbackException)
        {
            return false;
        }
        if (utf8.Length == 0 || utf8.Length > limits.MaxSingleFileBytes) return false;
        RewriteUtf8(contentRoot, logicalPath, utf8);
        rewritten = (utf8.Length, Digest(SHA256.HashData(utf8)));
        return true;
    }

    private static Encoding? DetectUtf16Or32(byte[] source)
    {
        if (source.Length < 2) return null;
        if (source.Length >= 4 && source[0] == 0x00 && source[1] == 0x00 && source[2] == 0xFE && source[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
        if (source.Length >= 4 && source[0] == 0xFF && source[1] == 0xFE && source[2] == 0x00 && source[3] == 0x00)
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
        if (source[0] == 0xFF && source[1] == 0xFE)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        if (source[0] == 0xFE && source[1] == 0xFF)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
        return null;
    }

    private static byte[] ReadAll(SafeFileHandle contentRoot, string logicalPath)
    {
        using SafeFileHandle handle = LinuxGamePackageStagingStore.OpenFilePath(contentRoot, logicalPath, create: false);
        using FileStream stream = LinuxGamePackageStagingStore.Stream(handle, FileAccess.Read);
        var buffer = new byte[stream.Length];
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) break;
            offset += read;
        }
        return buffer;
    }

    private static void RewriteUtf8(SafeFileHandle contentRoot, string logicalPath, byte[] utf8)
    {
        string normalized = logicalPath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        string parentPath = slash < 0 ? string.Empty : normalized[..slash];
        string name = slash < 0 ? normalized : normalized[(slash + 1)..];
        using SafeFileHandle parent = LinuxGamePackageStagingStore.OpenDirectoryPath(contentRoot, parentPath, create: false);
        string tmpName = $".{name}.utf8-{Guid.NewGuid():N}";
        using (SafeFileHandle tmp = LinuxGamePackageStagingStore.CreateFile(parent, tmpName))
        using (FileStream stream = LinuxGamePackageStagingStore.Stream(tmp, FileAccess.Write))
        {
            stream.Write(utf8, 0, utf8.Length);
            stream.Flush(flushToDisk: true);
        }
        LinuxGamePackageStagingStore.Rename(parent, tmpName, name);
    }

    private static GamePackageManifest Analyze(long archiveBytes, string archiveDigest, SafeFileHandle contentRoot, ExtractionResult extraction, GamePackageIngestionLimits limits)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var diagnostics = new DiagnosticCollector(limits.MaxDiagnostics);
        var files = new List<GamePackageFileManifest>(extraction.Files.Count);
        foreach (string normalizedPath in extraction.NormalizedPaths)
            diagnostics.Add("PATH_NORMALIZED_TO_NFC", GamePackageDiagnosticSeverity.Info, "PATH", normalizedPath,
                "gamePackage.diagnostic.pathNormalizedToNfc", publishBlocking: false);
        foreach (ExtractedFile file in extraction.Files.OrderBy(item => Encoding.UTF8.GetBytes(item.Path), ByteArrayComparer.Instance))
        {
            using SafeFileHandle handle = LinuxGamePackageStagingStore.OpenFilePath(contentRoot, file.Path, create: false);
            LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(handle);
            if (identity.LinkCount != 1 || identity.UserId != LinuxFileOperations.CurrentUserId
                || (identity.Mode & 0x1FF) != 0x180 || ComputeFileDigest(handle) != file.Digest)
                Reject(GamePackageRejectionCodes.StagedContentChanged, "Staged content changed during analysis.", file.Path);
            bool text = IsText(file.Path);
            TextAnalysis textAnalysis = text ? AnalyzeText(handle) : new(GamePackageTextEncoding.None, false, []);
            foreach (string code in textAnalysis.DiagnosticCodes)
                diagnostics.Add(code, GamePackageDiagnosticSeverity.Warning, "ENCODING", file.Path,
                    $"gamePackage.diagnostic.{ToCamelCase(code)}", publishBlocking: false);
            files.Add(new(file.Path, file.Bytes, file.Digest,
                text ? GamePackageFileKind.Text : GamePackageFileKind.Binary,
                textAnalysis.Encoding,
                textAnalysis.Encoding == GamePackageTextEncoding.Utf8Bom));
        }
        string contentDigest = ComputeContentDigest(files, extraction.Directories);
        return GamePackageManifest.Create(archiveBytes, archiveDigest, extraction.TotalBytes, contentDigest, files, extraction.Directories, diagnostics.Build());
    }

    private static TextAnalysis AnalyzeText(SafeFileHandle handle)
    {
        Span<byte> prefix = stackalloc byte[4];
        using (FileStream stream = OpenDescriptorStream(handle)) _ = stream.Read(prefix);
        if (prefix[..2].SequenceEqual(new byte[] { 0xFF, 0xFE }) || prefix[..2].SequenceEqual(new byte[] { 0xFE, 0xFF })
            || prefix.SequenceEqual(new byte[] { 0x00, 0x00, 0xFE, 0xFF })
            || prefix.SequenceEqual(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return new(GamePackageTextEncoding.Unknown, true, ["TEXT_UTF16_OR_UTF32_UNSUPPORTED"]);

        GamePackageTextEncoding encoding = DetectEncoding(handle);
        if (encoding == GamePackageTextEncoding.Unknown)
            return new(encoding, false, ["TEXT_ENCODING_UNSUPPORTED"]);
        Encoding decoder = encoding switch
        {
            GamePackageTextEncoding.ShiftJis => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => new UTF8Encoding(false, true),
        };
        int skip = encoding == GamePackageTextEncoding.Utf8Bom ? 3 : 0;
        var codes = new HashSet<string>(StringComparer.Ordinal);
        using FileStream content = OpenDescriptorStream(handle);
        content.Position = skip;
        using var reader = new StreamReader(content, decoder, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        char[] chars = new char[32 * 1024];
        int count;
        while ((count = reader.Read(chars, 0, chars.Length)) != 0)
        {
            for (int index = 0; index < count; index++)
            {
                char character = chars[index];
                if (character == '\0') codes.Add("TEXT_NUL_CHARACTER");
                else if (char.IsControl(character) && character is not ('\t' or '\n' or '\r' or '\f'))
                    codes.Add("TEXT_CONTROL_CHARACTER");
            }
        }
        return new(encoding, encoding == GamePackageTextEncoding.Utf8Bom, codes.Order(StringComparer.Ordinal).ToArray());
    }

    private static GamePackageTextEncoding DetectEncoding(SafeFileHandle handle)
    {
        Span<byte> prefix = stackalloc byte[3];
        using (FileStream stream = OpenDescriptorStream(handle)) _ = stream.Read(prefix);
        if (prefix.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })) return CanDecode(handle, new UTF8Encoding(false, true), 3) ? GamePackageTextEncoding.Utf8Bom : GamePackageTextEncoding.Unknown;
        if (CanDecode(handle, new UTF8Encoding(false, true), 0)) return GamePackageTextEncoding.Utf8;
        Encoding shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return CanDecode(handle, shiftJis, 0) ? GamePackageTextEncoding.ShiftJis : GamePackageTextEncoding.Unknown;
    }

    private static bool CanDecode(SafeFileHandle handle, Encoding encoding, int skip)
    {
        try
        {
            using FileStream stream = OpenDescriptorStream(handle);
            stream.Position = skip;
            using StreamReader reader = new(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: false);
            char[] chars = new char[32 * 1024];
            while (reader.Read(chars, 0, chars.Length) != 0) { }
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static string ComputeContentDigest(IReadOnlyList<GamePackageFileManifest> files, IReadOnlyList<string> directories)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("CloudEmuera.GamePackageContent\0"u8);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(number, 1);
        hash.AppendData(number[..4]);
        var entries = directories.Select(path => (Path: path, Kind: (byte)0, Bytes: 0L, Digest: (string?)null))
            .Concat(files.Select(file => (file.Path, Kind: (byte)1, file.Bytes, Digest: (string?)file.Digest)))
            .OrderBy(entry => Encoding.UTF8.GetBytes(entry.Path), ByteArrayComparer.Instance);
        foreach (var entry in entries)
        {
            hash.AppendData([entry.Kind]);
            byte[] path = Encoding.UTF8.GetBytes(entry.Path);
            BinaryPrimitives.WriteUInt32BigEndian(number, checked((uint)path.Length));
            hash.AppendData(number[..4]);
            hash.AppendData(path);
            BinaryPrimitives.WriteUInt64BigEndian(number, checked((ulong)entry.Bytes));
            hash.AppendData(number);
            hash.AppendData(entry.Digest is null ? new byte[32] : Convert.FromHexString(entry.Digest[7..]));
        }
        return Digest(hash.GetHashAndReset());
    }

    private async Task<(long Bytes, string Digest)> ReceiveAsync(Stream input, SafeFileHandle target, long limit, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream output = LinuxGamePackageStagingStore.Stream(target, FileAccess.Write, async: true);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (total > limit - read) Reject(GamePackageRejectionCodes.ArchiveTooLarge, "ZIP archive exceeds the configured limit.");
            if (faultInjector is not null) await faultInjector.InjectAsync(GamePackageIngestionFaultPoint.BeforeArchiveWrite, token).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            total += read;
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        return (total, Digest(hash.GetHashAndReset()));
    }

    /// <summary>
    /// After the archive is received and expanded, settle the staging reservation
    /// to the actual bytes on disk instead of the worst-case archive+expanded
    /// budget. The conservative start reservation still bounds concurrent uploads;
    /// settling prevents a handful of unconsumed READY packages from exhausting
    /// the whole staging budget (each upload would otherwise reserve several GiB
    /// regardless of its real size).
    /// </summary>
    private async Task AdjustReservationAsync(string ingestionId, long archiveBytes, long expandedBytes, CancellationToken token)
    {
        long actualReservation = checked(archiveBytes + expandedBytes);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, token).ConfigureAwait(false);
        long active = await db.GamePackageIngestions.Where(item => item.Id != ingestionId).SumAsync(item => (long?)item.ReservedBytes, token).ConfigureAwait(false) ?? 0;
        if (active > EffectiveCapacity.MaxStagingReservedBytes - actualReservation)
            throw new GamePackageIngestionException(GamePackageRejectionCodes.StagingBudgetExhausted, "The staging budget is exhausted after the package was received.");
        int changed = await db.GamePackageIngestions
            .Where(item => item.Id == ingestionId && item.Status == GamePackageIngestionStatus.Analyzing)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ReservedBytes, actualReservation)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.StateVersion, item => item.StateVersion + 1), token).ConfigureAwait(false);
        if (changed != 1) throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion state changed concurrently.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task UpdateArchiveAsync(string id, long bytes, string digest, CancellationToken token)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int changed = await db.GamePackageIngestions.Where(row => row.Id == id && row.Status == GamePackageIngestionStatus.Receiving)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, GamePackageIngestionStatus.Inspecting)
                .SetProperty(row => row.ArchiveBytes, bytes).SetProperty(row => row.ArchiveDigest, digest)
                .SetProperty(row => row.UpdatedAt, now).SetProperty(row => row.StateVersion, row => row.StateVersion + 1), token).ConfigureAwait(false);
        if (changed != 1) throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion state changed concurrently.");
    }

    private async Task MarkReadyAsync(string id, string ownerUserId, string? requestId, GamePackageManifest manifest, CancellationToken token)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string summary = JsonSerializer.Serialize(new { manifest.FileCount, manifest.DirectoryCount, diagnostics = manifest.Diagnostics.Count }, JsonOptions);
        if (faultInjector is not null) await faultInjector.InjectAsync(GamePackageIngestionFaultPoint.BeforeReadyCas, token).ConfigureAwait(false);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, token).ConfigureAwait(false);
        int changed = await db.GamePackageIngestions.Where(row => row.Id == id && row.Status == GamePackageIngestionStatus.Analyzing)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, GamePackageIngestionStatus.Ready)
                .SetProperty(row => row.ExpandedBytes, manifest.ContentBytes).SetProperty(row => row.EntryCount, manifest.FileCount + manifest.DirectoryCount)
                .SetProperty(row => row.ContentDigest, manifest.ContentDigest).SetProperty(row => row.SummaryJson, summary)
                .SetProperty(row => row.UpdatedAt, now).SetProperty(row => row.StateVersion, row => row.StateVersion + 1), token).ConfigureAwait(false);
        if (changed != 1) throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion state changed concurrently.");
        db.AuditEvents.Add(NewAudit(AuditActions.GamePackageIngested, id, ownerUserId, requestId, AuditResult.Succeeded, null, now));
        if (faultInjector is not null) await faultInjector.InjectAsync(GamePackageIngestionFaultPoint.BeforeAuditCommit, token).ConfigureAwait(false);
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task TransitionAsync(string id, GamePackageIngestionStatus expected, GamePackageIngestionStatus next, CancellationToken token)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        int changed = await db.GamePackageIngestions.Where(row => row.Id == id && row.Status == expected)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, next).SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.StateVersion, row => row.StateVersion + 1), token).ConfigureAwait(false);
        if (changed != 1) throw new GamePackageIngestionException("INGESTION_STATE_CONFLICT", "The package ingestion state changed concurrently.");
    }

    private async Task FailAndCleanAsync(string id, LinuxGamePackageStagingStore stagingStore, string ownerUserId, string? requestId, string reasonCode)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        try
        {
            db.ChangeTracker.Clear();
            await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, CancellationToken.None).ConfigureAwait(false);
            await db.GamePackageIngestions.Where(row => row.Id == id && row.Status != GamePackageIngestionStatus.Consumed)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, GamePackageIngestionStatus.Failed)
                    .SetProperty(row => row.UpdatedAt, now).SetProperty(row => row.StateVersion, row => row.StateVersion + 1), CancellationToken.None).ConfigureAwait(false);
            db.AuditEvents.Add(NewAudit(AuditActions.GamePackageRejected, id, ownerUserId, requestId, AuditResult.Failed, reasonCode, now));
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
        try
        {
            if (stagingStore.DeleteIngestion(id)) await TryCleanTerminalAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private void EnsureFreeSpace(long reservation)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(storageOptions.DataRoot)) ?? "/";
        DriveInfo drive = new(root);
        long available = drive.AvailableFreeSpace;
        if (available < EffectiveCapacity.MinDataRootFreeBytes
            || available - EffectiveCapacity.MinDataRootFreeBytes < reservation)
            throw new GamePackageIngestionException(GamePackageRejectionCodes.DataRootSpaceLow, "DataRoot free space is below the safety threshold.");
    }

    private InstanceCapacityOptions EffectiveCapacity => capacityOptions ??
        InstanceCapacityOptions.Default with
        {
            // Standalone Infrastructure callers historically supplied these
            // two bounds through GamePackageStorageOptions. Preserve that
            // test/maintenance seam while the API composition root remains
            // the authoritative deployment configuration.
            MaxStagingReservedBytes = storageOptions.MaxStagingReservedBytes,
            MinDataRootFreeBytes = storageOptions.MinDataRootFreeBytes,
        };

    private static void ValidateStagingPath(GamePackageIngestionRow row, string ingestionId)
    {
        if (!string.Equals(row.StagingPath, $"games/staging/{ingestionId}", StringComparison.Ordinal))
            throw new GamePackageIngestionException(GamePackageRejectionCodes.PathInvalid, "The persisted staging path is inconsistent.");
    }

    private static void AddParents(HashSet<string> directories, string path)
    {
        string[] segments = path.Split('/');
        for (int index = 1; index < segments.Length; index++) directories.Add(string.Join('/', segments.Take(index)));
    }

    private static bool IsText(string path) => Path.GetFileName(path).Equals("emuera.config", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".erb", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".erh", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".config", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    private static string Digest(byte[] hash) => $"sha256:{Convert.ToHexStringLower(hash)}";
    private static string ComputeFileDigest(SafeFileHandle handle)
    {
        using FileStream stream = OpenDescriptorStream(handle);
        return Digest(SHA256.HashData(stream));
    }

    private static FileStream OpenDescriptorStream(SafeFileHandle handle) =>
        new(LinuxGamePackageStagingStore.DescriptorPath(handle), FileMode.Open, FileAccess.Read, FileShare.Read);

    private static AuditEventRow NewAudit(string action, string resourceId, string ownerUserId, string? requestId, AuditResult result, string? reasonCode, DateTimeOffset now) => new()
    {
        Id = $"audit_{Guid.CreateVersion7():N}", OccurredAt = now, ActorUserId = ownerUserId,
        ActorType = AuditActorType.User, Action = action, ResourceType = "GAME_PACKAGE_INGESTION",
        ResourceId = resourceId, RequestId = requestId, Result = result, ReasonCode = reasonCode,
        MetadataJson = "{}",
    };
    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return crc;
    }
    private static void Reject(string code, string message, string? path = null) => throw new GamePackageIngestionException(code, message, path);
    private static string ToCamelCase(string code)
    {
        string[] parts = code.ToLowerInvariant().Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
    private sealed record PreparedEntry(ValidatedZipEntry Entry, string Path, bool Directory, bool PathNormalized);
    private sealed record ExtractedFile(string Path, long Bytes, string Digest);
    private sealed record ExtractionResult(long TotalBytes, IReadOnlyList<ExtractedFile> Files, IReadOnlyList<string> Directories, IReadOnlyList<string> NormalizedPaths);
    private sealed record TextAnalysis(GamePackageTextEncoding Encoding, bool HasBom, IReadOnlyList<string> DiagnosticCodes);

    private sealed class DiagnosticCollector(int maxDetails)
    {
        private readonly List<GamePackageDiagnostic> details = [];
        private readonly Dictionary<string, int> suppressed = new(StringComparer.Ordinal);

        public void Add(string code, GamePackageDiagnosticSeverity severity, string stage, string? logicalPath,
            string messageKey, bool publishBlocking, IReadOnlyDictionary<string, string>? arguments = null)
        {
            if (details.Count < maxDetails)
            {
                details.Add(new(code, severity, stage, SanitizePath(logicalPath), messageKey,
                    arguments ?? new Dictionary<string, string>(), publishBlocking));
            }
            else
            {
                suppressed[code] = suppressed.GetValueOrDefault(code) + 1;
            }
        }

        public List<GamePackageDiagnostic> Build()
        {
            if (suppressed.Count == 0) return details;
            int total = suppressed.Values.Sum();
            string counts = string.Join(',', suppressed.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"));
            details.Add(new("DIAGNOSTICS_TRUNCATED", GamePackageDiagnosticSeverity.Warning, "STRUCTURE", null,
                "gamePackage.diagnostic.diagnosticsTruncated",
                new Dictionary<string, string> { ["suppressedTotal"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture), ["counts"] = counts },
                suppressed.Keys.Any(IsBlockingCode), total));
            return details;
        }

        private static string? SanitizePath(string? path)
        {
            if (path is null) return null;
            string filtered = new(path.Where(character => !char.IsControl(character)).Take(512).ToArray());
            return filtered;
        }

        private static bool IsBlockingCode(string code) => code.StartsWith("TEXT_", StringComparison.Ordinal);
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public int Compare(byte[]? x, byte[]? y) => (x, y) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            _ => x.AsSpan().SequenceCompareTo(y),
        };
    }

    private sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        private long remainingBytes = remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int requested = checked((int)Math.Min(count, remainingBytes));
            int read = requested == 0 ? 0 : inner.Read(buffer, offset, requested);
            remainingBytes -= read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            int requested = checked((int)Math.Min(buffer.Length, remainingBytes));
            int read = requested == 0 ? 0 : inner.Read(buffer[..requested]);
            remainingBytes -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int requested = checked((int)Math.Min(buffer.Length, remainingBytes));
            int read = requested == 0 ? 0 : await inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            remainingBytes -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
