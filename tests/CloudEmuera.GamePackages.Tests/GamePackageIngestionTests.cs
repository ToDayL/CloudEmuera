using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using CloudEmuera.Application.GamePackages;
using CloudEmuera.Infrastructure.GamePackages;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CloudEmuera.GamePackages.Tests;

public sealed class GamePackageIngestionTests : IAsyncLifetime, IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("cloudemuera-p1-03-").FullName;
    private SqliteConnection connection = null!;
    private CloudEmueraDbContext db = null!;
    private string userId = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection($"Data Source={Path.Combine(root, SqliteStorageConventions.DatabaseFileName)}");
        await connection.OpenAsync();
        await using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();
        }
        DbContextOptions<CloudEmueraDbContext> contextOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable)).Options;
        db = new CloudEmueraDbContext(contextOptions);
        await db.Database.MigrateAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuotaProfileRow quota = new()
        {
            Id = $"qtp_{Guid.CreateVersion7():N}", Name = "P1-03", MaxActiveSessions = 1,
            MaxGamePackageBytes = 8 * 1024 * 1024, MaxSessionBytes = 16 * 1024 * 1024,
            MaxOutputBytesPerSecond = 1024, CreatedAt = now, UpdatedAt = now,
        };
        CloudEmueraUser user = new()
        {
            Id = $"usr_{Guid.CreateVersion7():N}", LoginName = "package-owner", NormalizedLoginName = "PACKAGE-OWNER",
            SecurityStamp = Guid.NewGuid().ToString("N"), Role = UserRole.Player, Status = UserStatus.Active,
            QuotaProfileId = quota.Id, CreatedAt = now, UpdatedAt = now,
        };
        db.AddRange(quota, user);
        await db.SaveChangesAsync();
        userId = user.Id;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        db?.Dispose();
        connection?.Dispose();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Category", "Encoding")]
    public async Task IngestsUtf8AndShiftJisFromNonSeekableStream()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] zip = CreateZip(
            ("ERB/UTF8.ERB", new UTF8Encoding(false).GetBytes("PRINTL 你好"), null),
            ("CSV/SJIS.CSV", Encoding.GetEncoding(932).GetBytes("名前,値"), null),
            ("emuera.config", Encoding.UTF8.GetBytes("Use sav folder:NO"), null));
        IngestedGamePackage result = await Service().IngestAsync(
            new(userId, new NonSeekableReadStream(zip)), Limits());
        Assert.Equal(3, result.Manifest.FileCount);
        Assert.Contains(result.Manifest.Files, file => file.Path == "ERB/UTF8.ERB" && file.Encoding == GamePackageTextEncoding.Utf8);
        Assert.Contains(result.Manifest.Files, file => file.Path == "CSV/SJIS.CSV" && file.Encoding == GamePackageTextEncoding.ShiftJis);
        Assert.Null(result.Manifest.ContentDigest);
        Assert.Null(result.Manifest.ArchiveDigest);
        string leasePath = Path.Combine(root, "games", "staging", result.IngestionId, "lease.json");
        Assert.True(File.Exists(leasePath));
        Assert.Contains(result.IngestionId, await File.ReadAllTextAsync(leasePath), StringComparison.Ordinal);
        Assert.Contains(await db.AuditEvents.AsNoTracking().ToListAsync(), audit => audit.Action == "GAME_PACKAGE_INGESTED" && audit.ResourceId == result.IngestionId);
        GamePackageConsumption consumption = await Service().BeginConsumeAsync(result.IngestionId, userId, result.Manifest.ContentDigest);
        Assert.Equal(result.Manifest.ContentDigest, consumption.ContentDigest);
        await Assert.ThrowsAsync<GamePackageIngestionException>(() => Service().BeginConsumeAsync(result.IngestionId, userId, result.Manifest.ContentDigest));
        await Service().CompleteConsumeAsync(result.IngestionId, userId);
    }

    [Fact]
    [Trait("Category", "Progress")]
    public async Task ReportsArchiveStagesAndCurrentFilesWithoutChangingIngestionResult()
    {
        byte[] zip = CreateZip(
            ("ERB/START.ERB", "@SYSTEM_TITLE\nQUIT\n"u8.ToArray(), null),
            ("CSV/GAMEBASE.CSV", "title,test\n"u8.ToArray(), null),
            ("emuera.config", "Use sav folder:NO\n"u8.ToArray(), null));
        List<GamePackageProgressUpdate> updates = [];

        IngestedGamePackage result = await Service().IngestAsync(
            new GamePackageIngestionRequest(
                userId,
                new MemoryStream(zip),
                "progress-test",
                (update, _) =>
                {
                    updates.Add(update);
                    return Task.CompletedTask;
                }),
            Limits());

        Assert.Equal(3, result.Manifest.FileCount);
        Assert.Contains(updates, update => update.Stage == GamePackageProgressStage.Receiving);
        Assert.Contains(updates, update => update.Stage == GamePackageProgressStage.InspectingArchive);
        Assert.Contains(updates, update => update.Stage == GamePackageProgressStage.Extracting && update.CurrentItem == "ERB/START.ERB");
        Assert.Contains(updates, update => update.Stage == GamePackageProgressStage.Analyzing && update.CurrentItem == "ERB/START.ERB");
        Assert.Equal(GamePackageProgressStage.Ready, updates[^1].Stage);
    }

    [Fact]
    [Trait("Category", "Encoding")]
    public async Task ConvertsUtf16TextFilesToUtf8OnIngestion()
    {
        byte[] utf16Body = Encoding.Unicode.GetBytes("@SYSTEM_TITLE\nINPUT\nQUIT\n");
        byte[] utf16Erb = [0xFF, 0xFE, .. utf16Body]; // UTF-16 LE with BOM
        byte[] zip = CreateZip(
            ("ERB/START.ERB", utf16Erb, null),
            ("CSV/GAMEBASE.CSV", Encoding.UTF8.GetBytes("title,test\n"), null),
            ("emuera.config", Encoding.UTF8.GetBytes("Use sav folder:NO\n"), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());

        string staged = Path.Combine(root, "games", "staging", result.IngestionId, "ready", "content", "ERB", "START.ERB");
        byte[] onDisk = await File.ReadAllBytesAsync(staged);
        Assert.Equal("@SYSTEM_TITLE\nINPUT\nQUIT\n", new UTF8Encoding(false, true).GetString(onDisk));
        GamePackageFileManifest erb = result.Manifest.Files.Single(file => file.Path == "ERB/START.ERB");
        Assert.Null(erb.Digest);
        Assert.Equal(GamePackageTextEncoding.Utf8, erb.Encoding);
        Assert.DoesNotContain(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_UTF16_OR_UTF32_UNSUPPORTED");
        Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
            diagnostic.Code == "TEXT_ENCODING_CONVERTED" && diagnostic.LogicalPath == "ERB/START.ERB" && !diagnostic.PublishBlocking);
        Assert.DoesNotContain(result.Manifest.Diagnostics, diagnostic =>
            diagnostic.Code == "TEXT_ENCODING_CONVERTED" && diagnostic.LogicalPath != "ERB/START.ERB");
    }

    [Fact]
    [Trait("Category", "Encoding")]
    public async Task ShiftJisAndUtf8FilesAreNotConverted()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] zip = CreateZip(
            ("ERB/START.ERB", Encoding.UTF8.GetBytes("@SYSTEM_TITLE\nQUIT\n"), null),
            ("CSV/GAMEBASE.CSV", Encoding.GetEncoding(932).GetBytes("名前,値\n"), null),
            ("emuera.config", Encoding.UTF8.GetBytes("Use sav folder:NO\n"), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());

        Assert.DoesNotContain(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_ENCODING_CONVERTED");
        Assert.Contains(result.Manifest.Files, file => file.Path == "CSV/GAMEBASE.CSV" && file.Encoding == GamePackageTextEncoding.ShiftJis);
        Assert.Contains(result.Manifest.Files, file => file.Path == "ERB/START.ERB" && file.Encoding == GamePackageTextEncoding.Utf8);
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task FlattensSingleTopLevelDirectory()
    {
        byte[] zip = CreateZip(
            ("game-folder/ERB/START.ERB", "@SYSTEM_TITLE\n"u8.ToArray(), null),
            ("game-folder/CSV/GAMEBASE.CSV", "title,x\n"u8.ToArray(), null),
            ("game-folder/emuera.config", "Use sav folder:NO\n"u8.ToArray(), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new NonSeekableReadStream(zip)), Limits());

        Assert.Equal(3, result.Manifest.FileCount);
        Assert.Contains(result.Manifest.Files, file => file.Path == "ERB/START.ERB");
        Assert.Contains(result.Manifest.Files, file => file.Path == "CSV/GAMEBASE.CSV");
        Assert.DoesNotContain(result.Manifest.Files, file => file.Path.StartsWith("game-folder/", StringComparison.Ordinal));
        string contentRoot = Path.Combine(root, "games", "staging", result.IngestionId, "ready", "content");
        Assert.True(File.Exists(Path.Combine(contentRoot, "ERB", "START.ERB")));
        Assert.True(File.Exists(Path.Combine(contentRoot, "emuera.config")));
        Assert.False(Directory.Exists(Path.Combine(contentRoot, "game-folder")));
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task DoesNotFlattenMultipleTopLevelEntries()
    {
        byte[] zip = CreateZip(
            ("game-folder/ERB/START.ERB", "@SYSTEM_TITLE\n"u8.ToArray(), null),
            ("README.txt", "readme\n"u8.ToArray(), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new NonSeekableReadStream(zip)), Limits());

        Assert.Equal(2, result.Manifest.FileCount);
        Assert.Contains(result.Manifest.Files, file => file.Path == "game-folder/ERB/START.ERB");
        Assert.Contains(result.Manifest.Files, file => file.Path == "README.txt");
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task DoesNotFlattenSingleTopLevelFileOrEmptyWrapper()
    {
        byte[] fileOnly = CreateZip(("START.ERB", "@SYSTEM_TITLE\n"u8.ToArray(), null));
        IngestedGamePackage fileResult = await Service().IngestAsync(new(userId, new NonSeekableReadStream(fileOnly)), Limits());
        Assert.Contains(fileResult.Manifest.Files, file => file.Path == "START.ERB");

        // A directory that wraps nothing is not flattened into a malformed root.
        byte[] emptyWrapper = CreateZip(("wrapper/", Array.Empty<byte>(), null));
        IngestedGamePackage emptyResult = await Service().IngestAsync(new(userId, new NonSeekableReadStream(emptyWrapper)), Limits());
        Assert.Equal(0, emptyResult.Manifest.FileCount);
    }

    [Theory]
    [Trait("Category", "ArchiveSecurity")]
    [InlineData("../escape.txt", GamePackageRejectionCodes.PathInvalid)]
    [InlineData("/absolute.txt", GamePackageRejectionCodes.PathInvalid)]
    [InlineData("ERB\\backslash.erb", GamePackageRejectionCodes.PathInvalid)]
    public async Task RejectsUnsafePathsWithoutReadyContent(string entryName, string expectedCode)
    {
        await AssertRejectedAsync(CreateZip((entryName, "x"u8.ToArray(), null)), expectedCode);
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task AcceptsCaseDistinctPaths()
    {
        byte[] zip = CreateZip(("ERB/A.ERB", "a"u8.ToArray(), null), ("erb/a.erb", "b"u8.ToArray(), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());
        Assert.Contains(result.Manifest.Files, file => file.Path == "ERB/A.ERB");
        Assert.Contains(result.Manifest.Files, file => file.Path == "erb/a.erb");
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task AcceptsUnicodeNormalizationDistinctPathsWithoutRewriting()
    {
        byte[] zip = CreateZip(("ERB/é.ERB", "a"u8.ToArray(), null), ("ERB/e\u0301.ERB", "b"u8.ToArray(), null), ("emuera.config", Array.Empty<byte>(), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());
        Assert.Contains(result.Manifest.Files, file => file.Path == "ERB/é.ERB");
        Assert.Contains(result.Manifest.Files, file => file.Path == "ERB/e\u0301.ERB");
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task MaterializesUnixSymbolicLinkMetadataAsOrdinaryFile()
    {
        byte[] zip = CreateZip(("ERB/link", "../../outside"u8.ToArray(), (0xA000 | 0x1FF) << 16), ("emuera.config", Array.Empty<byte>(), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());
        string path = Path.Combine(root, "games", "staging", result.IngestionId, "ready", "content", "ERB", "link");
        Assert.Equal("../../outside", await File.ReadAllTextAsync(path));
        Assert.Null(new FileInfo(path).LinkTarget);
    }

    [Theory]
    [InlineData("CON.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("trailing. ")]
    [InlineData(" ")]
    public async Task AcceptsNamesWithoutPortablePathPolicy(string path)
    {
        IngestedGamePackage result = await Service().IngestAsync(
            new(userId, new MemoryStream(CreateZip((path, "x"u8.ToArray(), null)))), Limits());
        Assert.Contains(result.Manifest.Files, file => file.Path == path);
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsCompressionBombByDeclaredRatio()
    {
        byte[] zip = CreateZip(("ERB/BOMB.ERB", Enumerable.Repeat((byte)'A', 32_000).ToArray(), null));
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.CompressionRatioExceeded, Limits() with { MaxCompressionRatio = 2 });
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsArchiveAtLimitPlusOneAndReleasesReservation()
    {
        byte[] zip = CreateZip(("A.txt", "content"u8.ToArray(), null));
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.ArchiveTooLarge, Limits() with { MaxArchiveBytes = zip.Length - 1 });
    }

    [Fact]
    [Trait("Category", "ArchiveQuota")]
    public async Task AcceptsArchiveAndExpandedContentAtExactLimits()
    {
        byte[] content = Enumerable.Repeat((byte)'x', 1024).ToArray();
        byte[] zip = CreateZip(("A.txt", content, null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)),
            Limits() with { MaxArchiveBytes = zip.Length, MaxExpandedBytes = content.Length, MaxSingleFileBytes = content.Length });
        Assert.Equal(content.Length, result.Manifest.ContentBytes);
    }

    [Fact]
    [Trait("Category", "ArchiveQuota")]
    public async Task EnforcesConfiguredCentralDirectoryLimitAtExactBoundary()
    {
        byte[] zip = CreateZip(("A.txt", "a"u8.ToArray(), null));
        int end = FindSignature(zip, 0x06054b50);
        long centralBytes = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 12));
        IngestedGamePackage accepted = await Service().IngestAsync(new(userId, new MemoryStream(zip)),
            Limits() with { MaxCentralDirectoryBytes = centralBytes });
        Assert.Equal(1, accepted.Manifest.FileCount);
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.CentralDirectoryTooLarge,
            Limits() with { MaxCentralDirectoryBytes = centralBytes - 1 });
    }

    [Fact]
    [Trait("Category", "Manifest")]
    public async Task IngestionIdentityDoesNotDependOnArchiveBytes()
    {
        byte[] first = CreateZip(("B.txt", "b"u8.ToArray(), null), ("A.txt", "a"u8.ToArray(), null));
        byte[] second = CreateZip(("A.txt", "a"u8.ToArray(), null), ("B.txt", "b"u8.ToArray(), null));
        IngestedGamePackage a = await Service().IngestAsync(new(userId, new MemoryStream(first)), Limits());
        IngestedGamePackage b = await Service().IngestAsync(new(userId, new MemoryStream(second)), Limits());
        Assert.Null(a.Manifest.ContentDigest);
        Assert.Null(a.Manifest.ArchiveDigest);
        Assert.Null(b.Manifest.ContentDigest);
        Assert.Null(b.Manifest.ArchiveDigest);
        Assert.Equal(a.Manifest.Files.Select(file => file.Path), b.Manifest.Files.Select(file => file.Path));
    }

    [Fact]
    [Trait("Category", "IngestionRecovery")]
    public async Task ReaperClaimsExpiredReadyLeaseAndReleasesPersistentBudget()
    {
        IngestedGamePackage result = await Service().IngestAsync(
            new(userId, new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        GamePackageIngestionRow before = await db.GamePackageIngestions.AsNoTracking().SingleAsync(row => row.Id == result.IngestionId);
        string stagingRoot = Path.Combine(root, before.StagingPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(Path.Combine(stagingRoot, "ready")));
        GamePackageIngestionMaintenance reaper = new(db, StorageOptions(), new FixedTimeProvider(result.ExpiresAt + TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await reaper.ReapExpiredAsync());
        GamePackageIngestionRow after = await db.GamePackageIngestions.AsNoTracking().SingleAsync(row => row.Id == result.IngestionId);
        Assert.Equal(GamePackageIngestionStatus.Abandoned, after.Status);
        Assert.Equal(0, after.ReservedBytes);
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    [Trait("Category", "IngestionRecovery")]
    public async Task CancellationDuringReceiveLeavesNoReadyContentAndReleasesBudget()
    {
        byte[] zip = CreateZip(("A.txt", Enumerable.Repeat((byte)'a', 128 * 1024).ToArray(), null));
        using CancellationTokenSource cancellation = new();
        GamePackageIngestionException error = await Assert.ThrowsAsync<GamePackageIngestionException>(() =>
            Service().IngestAsync(new(userId, new CancellingReadStream(zip, cancellation)), Limits(), cancellation.Token));
        Assert.Equal(GamePackageRejectionCodes.IngestionCancelled, error.Code);
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().OrderByDescending(item => item.CreatedAt).FirstAsync();
        Assert.Equal(GamePackageIngestionStatus.Failed, row.Status);
        Assert.Equal(0, row.ReservedBytes);
        Assert.False(Directory.Exists(Path.Combine(root, row.StagingPath, "ready")));
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task LocalAndCentralCrcMismatchIsRejected()
    {
        byte[] zip = CreateZip(("A.txt", "content"u8.ToArray(), null));
        int central = FindSignature(zip, 0x02014b50);
        zip[central + 16] ^= 0x40;
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.ArchiveCorrupt);
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsZip64Sentinel()
    {
        byte[] zip = CreateZip(("A.txt", "a"u8.ToArray(), null));
        int end = FindSignature(zip, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 10), ushort.MaxValue);
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.Zip64Unsupported);
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsEncryptedEntry()
    {
        byte[] zip = CreateZip(("A.txt", "a"u8.ToArray(), null));
        int local = FindSignature(zip, 0x04034b50);
        int central = FindSignature(zip, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(local + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(central + 8), 1);
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.ArchiveEncrypted);
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsUnknownCompressionMethod()
    {
        byte[] zip = CreateZip(("A.txt", "a"u8.ToArray(), null));
        int local = FindSignature(zip, 0x04034b50);
        int central = FindSignature(zip, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(local + 8), 99);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(central + 10), 99);
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.ZipMethodUnsupported);
    }

    [Theory]
    [Trait("Category", "ArchiveSecurity")]
    [InlineData(0x1000)]
    [InlineData(0x2000)]
    [InlineData(0x6000)]
    [InlineData(0xC000)]
    public async Task MaterializesUnixSpecialFileMetadataAsOrdinaryFiles(int unixType)
    {
        byte[] zip = CreateZip(("special", "x"u8.ToArray(), (unixType | 0x180) << 16));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());
        string path = Path.Combine(root, "games", "staging", result.IngestionId, "ready", "content", "special");
        Assert.True(File.Exists(path));
        Assert.Equal("x", await File.ReadAllTextAsync(path));
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsOverlappingEntryRanges()
    {
        byte[] zip = CreateZip(("A.txt", "aaaa"u8.ToArray(), null), ("B.txt", "bbbb"u8.ToArray(), null));
        int firstLocal = FindSignature(zip, 0x04034b50);
        int secondLocal = FindSignature(zip, 0x04034b50, firstLocal + 4);
        int firstCentral = FindSignature(zip, 0x02014b50);
        int firstData = firstLocal + 30 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(firstLocal + 26))
            + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(firstLocal + 28));
        uint overlappingSize = checked((uint)(secondLocal - firstData + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(firstLocal + 18), overlappingSize);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(firstCentral + 20), overlappingSize);
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.ArchiveCorrupt);
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsTruncatedDeflateStream()
    {
        byte[] zip = CreateZip(("A.txt", Enumerable.Repeat((byte)'a', 4096).ToArray(), null));
        int local = FindSignature(zip, 0x04034b50);
        int central = FindSignature(zip, 0x02014b50);
        uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(central + 20));
        Assert.True(compressed > 1);
        uint truncated = Math.Max(1, compressed / 2);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + 18), truncated);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 20), truncated);
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.ArchiveCorrupt);
    }

    [Fact]
    [Trait("Category", "ArchiveQuota")]
    public async Task EnforcesActualExpandedLimitIndependentlyOfDeclaration()
    {
        byte[] zip = CreateZip(("A.txt", "aa"u8.ToArray(), null), ("B.txt", "bb"u8.ToArray(), null));
        int local = FindSignature(zip, 0x04034b50);
        int central = FindSignature(zip, 0x02014b50);
        for (int index = 0; index < 2; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + 22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24), 1);
            if (index == 0)
            {
                local = FindSignature(zip, 0x04034b50, local + 4);
                central = FindSignature(zip, 0x02014b50, central + 4);
            }
        }
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.ExpandedSizeExceeded,
            Limits() with { MaxExpandedBytes = 3, MaxSingleFileBytes = 3 });
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task AcceptsValidatedInfoZipUnicodePathExtra()
    {
        IngestedGamePackage result = await Service().IngestAsync(
            new(userId, new MemoryStream(CreateStoredZipWithUnicodePath("legacy.txt"u8, "é.txt", "ok"u8))), Limits());
        Assert.Contains(result.Manifest.Files, file => file.Path == "é.txt");
    }

    [Fact]
    [Trait("Category", "Encoding")]
    public async Task KeepsUtf16ConversionAndMakesContentDiagnosticsNonBlocking()
    {
        byte[] zip = CreateZip(
            ("e\u0301.txt", new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, null),
            ("zero.txt", new byte[] { (byte)'a', 0, (byte)'b' }, null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits() with { MaxDiagnostics = 1 });
        Assert.DoesNotContain(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "PATH_NORMALIZED_TO_NFC");
        Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_ENCODING_CONVERTED" && !diagnostic.PublishBlocking);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_NUL_CHARACTER" && !diagnostic.PublishBlocking);
    }

    [Fact]
    [Trait("Category", "Encoding")]
    public async Task ConvertsUtf16AndKeepsNulDiagnosticInformational()
    {
        byte[] zip = CreateZip(
            ("utf16.txt", new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, null),
            ("zero.txt", new byte[] { (byte)'a', 0, (byte)'b' }, null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());
        Assert.DoesNotContain(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_UTF16_OR_UTF32_UNSUPPORTED");
        Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_ENCODING_CONVERTED"
            && diagnostic.LogicalPath == "utf16.txt" && !diagnostic.PublishBlocking);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == "TEXT_NUL_CHARACTER" && !diagnostic.PublishBlocking);
        Assert.All(result.Manifest.Diagnostics, diagnostic => Assert.StartsWith("gamePackage.diagnostic.", diagnostic.MessageKey, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "IngestionRecovery")]
    public async Task InvalidManifestDoesNotTransitionReadyLeaseToConsuming()
    {
        IngestedGamePackage result = await Service().IngestAsync(new(userId,
            new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        string manifestPath = Path.Combine(root, "games", "staging", result.IngestionId, "ready", "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{}");
        await Assert.ThrowsAsync<GamePackageIngestionException>(() =>
            Service().BeginConsumeAsync(result.IngestionId, userId, result.Manifest.ContentDigest));
        Assert.Equal(GamePackageIngestionStatus.Ready,
            (await db.GamePackageIngestions.AsNoTracking().SingleAsync(row => row.Id == result.IngestionId)).Status);
    }

    [Fact]
    [Trait("Category", "IngestionRecovery")]
    public async Task ReaperRecoversExpiredConsumingLease()
    {
        IngestedGamePackage result = await Service().IngestAsync(new(userId,
            new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        await using GamePackageConsumption consumption = await Service().BeginConsumeAsync(result.IngestionId, userId, result.Manifest.ContentDigest);
        GamePackageIngestionRow consuming = await db.GamePackageIngestions.AsNoTracking().SingleAsync(row => row.Id == result.IngestionId);
        var reaper = new GamePackageIngestionMaintenance(db, StorageOptions(), new FixedTimeProvider(consuming.ExpiresAt + TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await reaper.ReapExpiredAsync());
        GamePackageIngestionRow reaped = await db.GamePackageIngestions.AsNoTracking().SingleAsync(row => row.Id == result.IngestionId);
        Assert.Equal(GamePackageIngestionStatus.Abandoned, reaped.Status);
        Assert.NotNull(reaped.CleanupCompletedAt);
    }

    [Fact]
    [Trait("Category", "IngestionRecovery")]
    public async Task ReaperRefusesTamperedLeaseAndRetainsReservation()
    {
        IngestedGamePackage result = await Service().IngestAsync(new(userId,
            new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        string leasePath = Path.Combine(root, "games", "staging", result.IngestionId, "lease.json");
        await File.WriteAllTextAsync(leasePath, "{}");
        var reaper = new GamePackageIngestionMaintenance(db, StorageOptions(), new FixedTimeProvider(result.ExpiresAt + TimeSpan.FromSeconds(1)));
        Assert.Equal(0, await reaper.ReapExpiredAsync());
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().SingleAsync(item => item.Id == result.IngestionId);
        Assert.Equal(GamePackageIngestionStatus.Abandoned, row.Status);
        Assert.True(row.ReservedBytes > 0);
        Assert.Null(row.CleanupCompletedAt);
        Assert.True(Directory.Exists(Path.Combine(root, "games", "staging", result.IngestionId)));
    }

    [Fact]
    [Trait("Category", "IngestionConcurrency")]
    public async Task AbandonCasCannotOverwriteConcurrentComplete()
    {
        IngestedGamePackage result = await Service().IngestAsync(new(userId,
            new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        await using GamePackageConsumption lease = await Service().BeginConsumeAsync(result.IngestionId, userId, result.Manifest.ContentDigest);
        await using SqliteConnection abandonConnection = await OpenAdditionalConnectionAsync();
        await using SqliteConnection completeConnection = await OpenAdditionalConnectionAsync();
        await using CloudEmueraDbContext abandonDb = CreateContext(abandonConnection);
        await using CloudEmueraDbContext completeDb = CreateContext(completeConnection);
        var gate = new AbandonGateFaultInjector();
        var abandonService = new GamePackageIngestionService(abandonDb, StorageOptions(), TimeProvider.System, gate);
        var completeService = new GamePackageIngestionService(completeDb, StorageOptions(), TimeProvider.System);

        Task abandon = abandonService.AbandonAsync(result.IngestionId, userId);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await completeService.CompleteConsumeAsync(result.IngestionId, userId);
        gate.Release.SetResult();
        await abandon;

        GamePackageIngestionRow final = await db.GamePackageIngestions.AsNoTracking().SingleAsync(item => item.Id == result.IngestionId);
        Assert.Equal(GamePackageIngestionStatus.Consumed, final.Status);
    }

    [Fact]
    [Trait("Category", "PersistenceConstraint")]
    public void IngestionStateVersionIsConfiguredAsConcurrencyToken()
    {
        Assert.True(db.Model.FindEntityType(typeof(GamePackageIngestionRow))!
            .FindProperty(nameof(GamePackageIngestionRow.StateVersion))!.IsConcurrencyToken);
    }

    [Fact]
    [Trait("Category", "IngestionConcurrency")]
    public async Task ReadyConsumptionAndReaperRaceHasOneCasWinner()
    {
        IngestedGamePackage result = await Service().IngestAsync(new(userId,
            new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        await using SqliteConnection consumeConnection = await OpenAdditionalConnectionAsync();
        await using SqliteConnection reapConnection = await OpenAdditionalConnectionAsync();
        await using CloudEmueraDbContext consumeDb = CreateContext(consumeConnection);
        await using CloudEmueraDbContext reapDb = CreateContext(reapConnection);
        var consumeService = new GamePackageIngestionService(consumeDb, StorageOptions(), new FixedTimeProvider(result.ExpiresAt - TimeSpan.FromSeconds(1)));
        var reaper = new GamePackageIngestionMaintenance(reapDb, StorageOptions(), new FixedTimeProvider(result.ExpiresAt + TimeSpan.FromSeconds(1)));

        Task<GamePackageConsumption?> consume = Task.Run(async () =>
        {
            try { return await consumeService.BeginConsumeAsync(result.IngestionId, userId, result.Manifest.ContentDigest); }
            catch (GamePackageIngestionException exception) when (exception.Code == "INGESTION_NOT_READY") { return null; }
        });
        Task<int> reap = reaper.ReapExpiredAsync();
        await Task.WhenAll(consume, reap);
        await using GamePackageConsumption? lease = await consume;
        Assert.NotEqual(lease is null, await reap == 0);
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().SingleAsync(item => item.Id == result.IngestionId);
        Assert.Contains(row.Status, new[] { GamePackageIngestionStatus.Consuming, GamePackageIngestionStatus.Abandoned });
        if (row.Status == GamePackageIngestionStatus.Consuming) await Service().AbandonAsync(result.IngestionId, userId);
    }

    [Fact]
    [Trait("Category", "IngestionRecovery")]
    public async Task SecureCleanupRefusesSymlinkWithoutDeletingItsTargetAndCanRetry()
    {
        IngestedGamePackage result = await Service().IngestAsync(new(userId,
            new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        await using GamePackageConsumption consumption = await Service().BeginConsumeAsync(result.IngestionId, userId, result.Manifest.ContentDigest);
        string outside = Path.Combine(root, "outside.txt");
        await File.WriteAllTextAsync(outside, "keep");
        string staged = Path.Combine(root, "games", "staging", result.IngestionId, "ready", "content", "A.txt");
        File.Delete(staged);
        File.CreateSymbolicLink(staged, outside);
        await Assert.ThrowsAsync<IOException>(() => Service().CompleteConsumeAsync(result.IngestionId, userId));
        Assert.Equal("keep", await File.ReadAllTextAsync(outside));
        GamePackageIngestionRow pending = await db.GamePackageIngestions.AsNoTracking().SingleAsync(row => row.Id == result.IngestionId);
        Assert.Equal(GamePackageIngestionStatus.Consumed, pending.Status);
        Assert.Null(pending.CleanupCompletedAt);
        File.Delete(staged);
        var reaper = new GamePackageIngestionMaintenance(db, StorageOptions(), TimeProvider.System);
        Assert.Equal(1, await reaper.ReapExpiredAsync());
        Assert.False(Directory.Exists(Path.Combine(root, "games", "staging", result.IngestionId)));
    }

    [Theory]
    [Trait("Category", "IngestionFailure")]
    [InlineData(GamePackageIngestionFaultPoint.BeforeArchiveWrite)]
    [InlineData(GamePackageIngestionFaultPoint.BeforePublishRename)]
    public async Task IoFailuresAreCleanedAndReleaseReservation(GamePackageIngestionFaultPoint point)
    {
        var injector = new DelegateFaultInjector((actual, _) => actual == point
            ? ValueTask.FromException(new IOException("simulated ENOSPC or rename failure"))
            : ValueTask.CompletedTask);
        GamePackageIngestionException error = await Assert.ThrowsAsync<GamePackageIngestionException>(() =>
            new GamePackageIngestionService(db, StorageOptions(), TimeProvider.System, injector).IngestAsync(
                new(userId, new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits()));
        Assert.Equal(GamePackageRejectionCodes.StagingIoFailed, error.Code);
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().OrderByDescending(item => item.CreatedAt).FirstAsync();
        Assert.Equal(0, row.ReservedBytes);
        Assert.NotNull(row.CleanupCompletedAt);
    }

    [Fact]
    [Trait("Category", "IngestionFailure")]
    public async Task ReadyCasConflictCannotPublishCandidate()
    {
        var injector = new DelegateFaultInjector(async (point, token) =>
        {
            if (point == GamePackageIngestionFaultPoint.BeforeReadyCas)
                await db.GamePackageIngestions.Where(row => row.Status == GamePackageIngestionStatus.Analyzing)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, GamePackageIngestionStatus.Abandoned), token);
        });
        GamePackageIngestionException error = await Assert.ThrowsAsync<GamePackageIngestionException>(() =>
            new GamePackageIngestionService(db, StorageOptions(), TimeProvider.System, injector).IngestAsync(
                new(userId, new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits()));
        Assert.Equal("INGESTION_STATE_CONFLICT", error.Code);
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().OrderByDescending(item => item.CreatedAt).FirstAsync();
        Assert.False(Directory.Exists(Path.Combine(root, "games", "staging", row.Id)));
    }

    [Fact]
    [Trait("Category", "IngestionFailure")]
    public async Task AuditCommitFailureRollsBackReadyAndLeavesOnlyRejectionAudit()
    {
        var injector = new DelegateFaultInjector((point, _) => point == GamePackageIngestionFaultPoint.BeforeAuditCommit
            ? ValueTask.FromException(new InvalidOperationException("simulated audit store failure"))
            : ValueTask.CompletedTask);
        GamePackageIngestionException error = await Assert.ThrowsAsync<GamePackageIngestionException>(() =>
            new GamePackageIngestionService(db, StorageOptions(), TimeProvider.System, injector).IngestAsync(
                new(userId, new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits()));
        Assert.Equal(GamePackageRejectionCodes.IngestionCommitFailed, error.Code);
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().OrderByDescending(item => item.CreatedAt).FirstAsync();
        List<AuditEventRow> audits = await db.AuditEvents.AsNoTracking().Where(audit => audit.ResourceId == row.Id).ToListAsync();
        Assert.DoesNotContain(audits, audit => audit.Action == "GAME_PACKAGE_INGESTED");
        Assert.Contains(audits, audit => audit.Action == "GAME_PACKAGE_REJECTED");
    }

    [Fact]
    [Trait("Category", "IngestionFailure")]
    public async Task IndependentAnalysisPassAcceptsContentChangedAfterExtraction()
    {
        var injector = new DelegateFaultInjector(async (point, token) =>
        {
            if (point != GamePackageIngestionFaultPoint.BeforeAnalyze) return;
            string ingestionId = await db.GamePackageIngestions.Where(row => row.Status == GamePackageIngestionStatus.Analyzing)
                .Select(row => row.Id).SingleAsync(token);
            await File.WriteAllTextAsync(Path.Combine(root, "games", "staging", ingestionId, "candidate.work", "content", "A.txt"), "changed", token);
        });
        IngestedGamePackage result = await new GamePackageIngestionService(db, StorageOptions(), TimeProvider.System, injector).IngestAsync(
            new(userId, new MemoryStream(CreateZip(("A.txt", "a"u8.ToArray(), null)))), Limits());
        Assert.Null(result.Manifest.ContentDigest);
    }

    [Fact]
    [Trait("Category", "ArchiveSecurity")]
    public async Task RejectsUnicodePathExtraWithWrongLegacyNameCrc()
    {
        byte[] zip = CreateStoredZipWithUnicodePath("legacy.txt"u8, "é.txt", "ok"u8);
        int local = FindSignature(zip, 0x04034b50);
        int localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 26));
        zip[local + 30 + localNameLength + 5] ^= 0x40;
        await AssertRejectedAsync(zip, GamePackageRejectionCodes.PathInvalid);
    }

    [Fact]
    [Trait("Category", "ArchiveQuota")]
    public async Task PersistentReservationPreventsConcurrentBudgetOversubscription()
    {
        byte[] zip = CreateZip(("A.txt", "content"u8.ToArray(), null));
        GamePackageIngestionLimits limits = Limits();
        GamePackageStorageOptions constrained = StorageOptions() with
        {
            MaxStagingReservedBytes = limits.MaxArchiveBytes + limits.MaxExpandedBytes,
        };
        var gate = new GateReadStream(zip);
        Task<IngestedGamePackage> first = new GamePackageIngestionService(db, constrained, TimeProvider.System)
            .IngestAsync(new(userId, gate), limits);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using SqliteConnection otherConnection = new($"Data Source={Path.Combine(root, SqliteStorageConventions.DatabaseFileName)}");
        await otherConnection.OpenAsync();
        DbContextOptions<CloudEmueraDbContext> otherOptions = new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(otherConnection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable)).Options;
        await using CloudEmueraDbContext otherDb = new(otherOptions);
        GamePackageIngestionException error = await Assert.ThrowsAsync<GamePackageIngestionException>(() =>
            new GamePackageIngestionService(otherDb, constrained, TimeProvider.System)
                .IngestAsync(new(userId, new MemoryStream(zip)), limits));
        Assert.Equal(GamePackageRejectionCodes.StagingBudgetExhausted, error.Code);
        gate.Release.SetResult();
        IngestedGamePackage completed = await first;
        await Service().AbandonAsync(completed.IngestionId, userId);
    }

    [Fact]
    [Trait("Category", "ArchiveQuota")]
    public async Task ReservationSettlesToActualSizeAfterAnalysis()
    {
        byte[] zip = CreateZip(("ERB/START.ERB", "@SYSTEM_TITLE\n"u8.ToArray(), null), ("emuera.config", "Use sav folder:NO\n"u8.ToArray(), null));
        IngestedGamePackage result = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());

        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().SingleAsync(item => item.Id == result.IngestionId);
        long expected = result.Manifest.ArchiveBytes + result.Manifest.ContentBytes;
        Assert.Equal(expected, row.ReservedBytes);
        Assert.True(row.ReservedBytes < Limits().MaxArchiveBytes + Limits().MaxExpandedBytes,
            "an ingested package must not keep the worst-case quota reservation");
    }

    [Fact]
    [Trait("Category", "ArchiveQuota")]
    public async Task UnconsumedReadyIngestionsReserveOnlyActualBytes()
    {
        byte[] zip = CreateZip(("ERB/START.ERB", "@SYSTEM_TITLE\n"u8.ToArray(), null), ("emuera.config", "Use sav folder:NO\n"u8.ToArray(), null));
        IngestedGamePackage first = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());
        IngestedGamePackage second = await Service().IngestAsync(new(userId, new MemoryStream(zip)), Limits());

        long total = await db.GamePackageIngestions.Where(item => item.Status == GamePackageIngestionStatus.Ready)
            .SumAsync(item => (long)item.ReservedBytes);
        long one = first.Manifest.ArchiveBytes + first.Manifest.ContentBytes;
        Assert.True(total <= 2 * one,
            $"two unconsumed READY packages must not accumulate worst-case reservations (reserved={total}, actual per package={one})");
    }

    private async Task AssertRejectedAsync(byte[] zip, string expectedCode, GamePackageIngestionLimits? limits = null)
    {
        GamePackageIngestionException error = await Assert.ThrowsAsync<GamePackageIngestionException>(
            () => Service().IngestAsync(new(userId, new NonSeekableReadStream(zip)), limits ?? Limits()));
        Assert.Equal(expectedCode, error.Code);
        GamePackageIngestionRow row = await db.GamePackageIngestions.AsNoTracking().OrderByDescending(item => item.CreatedAt).FirstAsync();
        Assert.Equal(GamePackageIngestionStatus.Failed, row.Status);
        Assert.Equal(0, row.ReservedBytes);
        Assert.False(Directory.Exists(Path.Combine(root, row.StagingPath, "ready")));
        Assert.Contains(await db.AuditEvents.AsNoTracking().ToListAsync(), audit => audit.Action == "GAME_PACKAGE_REJECTED" && audit.ResourceId == row.Id && audit.ReasonCode == expectedCode);
    }

    private GamePackageIngestionService Service() => new(db, StorageOptions(), TimeProvider.System);

    private async Task<SqliteConnection> OpenAdditionalConnectionAsync()
    {
        var additional = new SqliteConnection($"Data Source={Path.Combine(root, SqliteStorageConventions.DatabaseFileName)}");
        await additional.OpenAsync();
        await using SqliteCommand pragma = additional.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync();
        return additional;
    }

    private static CloudEmueraDbContext CreateContext(SqliteConnection databaseConnection) => new(
        new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(databaseConnection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable)).Options);

    private GamePackageStorageOptions StorageOptions() => new()
    {
        DataRoot = root, MaxStagingReservedBytes = 64 * 1024 * 1024, MinDataRootFreeBytes = 0,
    };

    private static GamePackageIngestionLimits Limits() => new()
    {
        MaxArchiveBytes = 4 * 1024 * 1024, MaxExpandedBytes = 8 * 1024 * 1024,
        MaxSingleFileBytes = 4 * 1024 * 1024, MaxEntryCount = 100, MaxCompressionRatio = 500,
        MaxDuration = TimeSpan.FromSeconds(30),
    };

    private static byte[] CreateZip(params (string Name, byte[] Content, int? ExternalAttributes)[] files)
    {
        using MemoryStream memory = new();
        using (ZipArchive archive = new(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(file.Name, CompressionLevel.SmallestSize);
                if (file.ExternalAttributes is int attributes) entry.ExternalAttributes = attributes;
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream target = entry.Open();
                target.Write(file.Content);
            }
        }
        return memory.ToArray();
    }

    private static int FindSignature(byte[] bytes, uint signature, int start = 0)
    {
        for (int index = start; index <= bytes.Length - 4; index++)
            if (BitConverter.ToUInt32(bytes, index) == signature) return index;
        throw new InvalidOperationException("ZIP signature was not found.");
    }

    private static byte[] CreateStoredZipWithUnicodePath(ReadOnlySpan<byte> rawName, string unicodeName, ReadOnlySpan<byte> content)
    {
        byte[] utf8Name = Encoding.UTF8.GetBytes(unicodeName);
        uint nameCrc = Crc32(rawName);
        uint contentCrc = Crc32(content);
        byte[] extra = new byte[9 + utf8Name.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x7075);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), checked((ushort)(5 + utf8Name.Length)));
        extra[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(5), nameCrc);
        utf8Name.CopyTo(extra.AsSpan(9));

        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x04034b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(contentCrc);
        writer.Write(checked((uint)content.Length));
        writer.Write(checked((uint)content.Length));
        writer.Write(checked((ushort)rawName.Length));
        writer.Write(checked((ushort)extra.Length));
        writer.Write(rawName);
        writer.Write(extra);
        writer.Write(content);
        uint centralOffset = checked((uint)memory.Position);
        writer.Write(0x02014b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(contentCrc);
        writer.Write(checked((uint)content.Length));
        writer.Write(checked((uint)content.Length));
        writer.Write(checked((ushort)rawName.Length));
        writer.Write(checked((ushort)extra.Length));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(rawName);
        writer.Write(extra);
        uint centralBytes = checked((uint)memory.Position - centralOffset);
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralBytes);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
        return memory.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return crc ^ uint.MaxValue;
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        public override long Position { get => base.Position; set => throw new NotSupportedException(); }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CancellingReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
    {
        private bool cancelled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (!cancelled) { cancelled = true; cancellation.Cancel(); }
            return read;
        }
    }

    private sealed class GateReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private bool waited;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!waited)
            {
                waited = true;
                Started.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class DelegateFaultInjector(
        Func<GamePackageIngestionFaultPoint, CancellationToken, ValueTask> callback) : IGamePackageIngestionFaultInjector
    {
        public ValueTask InjectAsync(GamePackageIngestionFaultPoint point, CancellationToken cancellationToken) =>
            callback(point, cancellationToken);
    }

    private sealed class AbandonGateFaultInjector : IGamePackageIngestionFaultInjector
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask InjectAsync(GamePackageIngestionFaultPoint point, CancellationToken cancellationToken)
        {
            if (point != GamePackageIngestionFaultPoint.BeforeAbandonCas) return;
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
