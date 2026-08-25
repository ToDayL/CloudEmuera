using System.Text;
using System.Text.Json;
using CloudEmuera.Infrastructure.Persistence;

namespace CloudEmuera.Infrastructure.Games;

internal sealed record ScannedGameEntry(
    string Path,
    string EntryKind,
    long Bytes,
    string? Digest,
    string? FileKind,
    string? Encoding,
    bool? HasBom);

internal sealed record ScannedGameTree(
    string? ContentDigest,
    int FileCount,
    long TotalBytes,
    string ManifestJson,
    string RuntimeConfigJson,
    IReadOnlyList<ScannedGameEntry> Entries);

internal static class GameContentTreeScanner
{
    public static ScannedGameTree Scan(string root)
    {
        if (!Directory.Exists(root)) throw new IOException("The game content directory is missing.");
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        int entryCount = 0;
        while (pending.Count > 0)
        {
            (DirectoryInfo directory, int depth) = pending.Pop();
            Reject(directory);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().OrderByDescending(value => value.Name, StringComparer.Ordinal))
            {
                if (++entryCount > GameContentScanLimits.MaxEntryCount)
                    throw new GameContentLimitException("GAME_CONTENT_ENTRY_LIMIT");
                Reject(entry);
                string logical = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (logical.StartsWith(".cloudemuera-", StringComparison.Ordinal)) continue;
                if (entry is DirectoryInfo child)
                {
                    if (depth >= GameContentScanLimits.MaxDirectoryDepth)
                        throw new GameContentLimitException("GAME_CONTENT_DEPTH_LIMIT");
                    directories.Add(logical);
                    pending.Push((child, depth + 1));
                }
                else if (entry is FileInfo) files.Add(entry.FullName);
                else throw new IOException("The game content tree contains a special file.");
            }
        }

        List<ScannedGameEntry> entries = directories.Order(StringComparer.Ordinal)
            .Select(path => new ScannedGameEntry(path, "DIRECTORY", 0, null, null, null, null)).ToList();
        long total = 0;
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            FileInfo info = new(file);
            Reject(info);
            if (info.Length > GameContentScanLimits.MaxSingleFileBytes)
                throw new GameContentLimitException("GAME_CONTENT_FILE_LIMIT");
            total = checked(total + info.Length);
            if (total > GameContentScanLimits.MaxTotalBytes)
                throw new GameContentLimitException("GAME_CONTENT_TOTAL_LIMIT");
            byte[] bytes = File.ReadAllBytes(file);
            string logical = Path.GetRelativePath(root, file).Replace('\\', '/');
            string? encoding = null;
            bool hasBom = false;
            string fileKind = IsTextFile(file) && TryDecode(bytes, out _, out encoding, out hasBom) ? "TEXT" : "BINARY";
            entries.Add(new ScannedGameEntry(logical, "FILE", info.Length, null, fileKind, encoding, fileKind == "TEXT" ? hasBom : null));
        }
        string config = entries.FirstOrDefault(entry => entry.EntryKind == "FILE" && entry.Path == "emuera.config") is { } configEntry
            ? JsonSerializer.Serialize(new { path = configEntry.Path }) : "{}";
        string manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            contentDigest = (string?)null,
            fileCount = files.Count,
            directoryCount = directories.Count,
            totalBytes = total,
        });
        return new(null, files.Count, total, manifest, config, entries);
    }

    private static bool IsTextFile(string path) => Path.GetExtension(path).ToUpperInvariant() is ".ERB" or ".ERH" or ".CSV" or ".CONFIG" or ".TXT";

    private static bool TryDecode(ReadOnlySpan<byte> bytes, out string text, out string? encoding, out bool bom)
    {
        bom = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        try
        {
            text = new UTF8Encoding(false, true).GetString(bom ? bytes[3..] : bytes);
            encoding = bom ? "UTF8_BOM" : "UTF8";
            return true;
        }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
                encoding = "SHIFT_JIS";
                bom = false;
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                encoding = "UNKNOWN";
                bom = false;
                return false;
            }
        }
    }

    private static void Reject(FileSystemInfo info)
    {
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The game content tree contains a link or disappeared entry.");
    }
}
