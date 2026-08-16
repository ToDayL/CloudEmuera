// CloudEmuera modification: provide Windows-compatible, case-insensitive path
// semantics for the pinned upstream runtime while it runs on Linux. Resolution
// is restricted to the already validated private SessionRoot; paths outside it
// retain normal platform behavior.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;

namespace MinorShift.Emuera.Runtime.Utils;

internal static class HeadlessPathResolver
{
    private static readonly object Sync = new();
    private static string sessionRoot;
    private static string sessionRootPrefix;

    public static void Configure(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (Sync)
        {
            sessionRoot = canonical;
            sessionRootPrefix = canonical + Path.DirectorySeparatorChar;
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            sessionRoot = null;
            sessionRootPrefix = null;
        }
    }

    public static string ExistingOrOriginal(string path)
    {
        string resolved = ResolveExisting(path);
        return resolved ?? path;
    }

    public static string ForCreate(string path)
    {
        if (!TryGetControlledFullPath(path, out string fullPath, out string root))
            return path;

        string existing = ResolveControlled(fullPath, root, allowMissingTail: true, out _);
        return existing;
    }

    public static string ResolveExisting(string path)
    {
        if (!TryGetControlledFullPath(path, out string fullPath, out string root))
            return System.IO.File.Exists(path) || System.IO.Directory.Exists(path) ? path : null;

        string resolved = ResolveControlled(fullPath, root, allowMissingTail: false, out bool exists);
        return exists ? resolved : null;
    }

    private static bool TryGetControlledFullPath(string path, out string fullPath, out string root)
    {
        fullPath = null;
        root = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        lock (Sync)
        {
            if (sessionRoot is null)
                return false;
            root = sessionRoot;
            fullPath = Path.GetFullPath(path);
            return fullPath.Equals(sessionRoot, StringComparison.Ordinal) ||
                   fullPath.StartsWith(sessionRootPrefix, StringComparison.Ordinal);
        }
    }

    private static string ResolveControlled(string fullPath, string root, bool allowMissingTail, out bool exists)
    {
        string relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            exists = System.IO.Directory.Exists(root);
            return root;
        }

        string current = root;
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            string requested = segments[index];
            string exact = Path.Combine(current, requested);
            if (System.IO.File.Exists(exact) || System.IO.Directory.Exists(exact))
            {
                RejectReparsePoint(exact);
                current = exact;
                continue;
            }

            if (!System.IO.Directory.Exists(current))
            {
                exists = false;
                return allowMissingTail
                    ? Path.Combine(current, Path.Combine(segments[index..]))
                    : fullPath;
            }

            string normalizedRequested = requested.Normalize(NormalizationForm.FormC);
            string[] matches = System.IO.Directory.EnumerateFileSystemEntries(current)
                .Where(candidate => Path.GetFileName(candidate)
                    .Normalize(NormalizationForm.FormC)
                    .Equals(normalizedRequested, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length > 1)
                throw new IOException($"The controlled runtime path is ambiguous under case-insensitive lookup: '{fullPath}'.");
            if (matches.Length == 0)
            {
                exists = false;
                return allowMissingTail
                    ? Path.Combine(current, Path.Combine(segments[index..]))
                    : fullPath;
            }

            RejectReparsePoint(matches[0]);
            current = matches[0];
        }

        exists = true;
        return current;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The controlled runtime path contains a reparse point: '{path}'.");
    }
}

internal static class HeadlessFile
{
    public static bool Exists(string path) => HeadlessPathResolver.ResolveExisting(path) is string resolved && System.IO.File.Exists(resolved);
    public static FileAttributes GetAttributes(string path) => System.IO.File.GetAttributes(HeadlessPathResolver.ExistingOrOriginal(path));
    public static DateTime GetLastWriteTime(string path) => System.IO.File.GetLastWriteTime(HeadlessPathResolver.ExistingOrOriginal(path));
    public static void Delete(string path) => System.IO.File.Delete(HeadlessPathResolver.ExistingOrOriginal(path));
    public static void Move(string source, string destination) => System.IO.File.Move(
        HeadlessPathResolver.ExistingOrOriginal(source),
        HeadlessPathResolver.ForCreate(destination));
    public static System.IO.FileStream Open(string path, FileMode mode) =>
        new(ResolveForMode(path, mode), mode);
    public static byte[] ReadAllBytes(string path) => System.IO.File.ReadAllBytes(HeadlessPathResolver.ExistingOrOriginal(path));
    public static string[] ReadAllLines(string path) => System.IO.File.ReadAllLines(HeadlessPathResolver.ExistingOrOriginal(path));
    public static string[] ReadAllLines(string path, Encoding encoding) => System.IO.File.ReadAllLines(HeadlessPathResolver.ExistingOrOriginal(path), encoding);
    public static string ReadAllText(string path) => System.IO.File.ReadAllText(HeadlessPathResolver.ExistingOrOriginal(path));
    public static string ReadAllText(string path, Encoding encoding) => System.IO.File.ReadAllText(HeadlessPathResolver.ExistingOrOriginal(path), encoding);
    public static IEnumerable<string> ReadLines(string path, Encoding encoding) => System.IO.File.ReadLines(HeadlessPathResolver.ExistingOrOriginal(path), encoding);
    public static void WriteAllBytes(string path, byte[] bytes) => System.IO.File.WriteAllBytes(HeadlessPathResolver.ForCreate(path), bytes);
    public static void WriteAllText(string path, string contents) => System.IO.File.WriteAllText(HeadlessPathResolver.ForCreate(path), contents);
    public static void WriteAllText(string path, string contents, Encoding encoding) => System.IO.File.WriteAllText(HeadlessPathResolver.ForCreate(path), contents, encoding);

    internal static string ResolveForMode(string path, FileMode mode) => mode is FileMode.Open or FileMode.Truncate
        ? HeadlessPathResolver.ExistingOrOriginal(path)
        : HeadlessPathResolver.ForCreate(path);
}

internal static class HeadlessDirectory
{
    public static bool Exists(string path) => HeadlessPathResolver.ResolveExisting(path) is string resolved && System.IO.Directory.Exists(resolved);
    public static DirectoryInfo CreateDirectory(string path) => System.IO.Directory.CreateDirectory(HeadlessPathResolver.ForCreate(path));
    public static string GetCurrentDirectory() => System.IO.Directory.GetCurrentDirectory();
    public static string[] GetFiles(string path, string pattern) => GetFiles(path, pattern, SearchOption.TopDirectoryOnly);
    public static string[] GetFiles(string path, string pattern, SearchOption option) => EnumerateFiles(path, pattern, option).ToArray();
    public static IEnumerable<string> EnumerateFiles(string path, string pattern) => EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly);
    public static IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option)
    {
        string resolved = HeadlessPathResolver.ExistingOrOriginal(path);
        return System.IO.Directory.EnumerateFiles(resolved, "*", option)
            .Where(candidate => FileSystemName.MatchesSimpleExpression(
                pattern.AsSpan(),
                Path.GetFileName(candidate).AsSpan(),
                ignoreCase: true));
    }

    public static string[] GetDirectories(string path, string pattern, SearchOption option)
    {
        string resolved = HeadlessPathResolver.ExistingOrOriginal(path);
        return System.IO.Directory.EnumerateDirectories(resolved, "*", option)
            .Where(candidate => FileSystemName.MatchesSimpleExpression(
                pattern.AsSpan(),
                Path.GetFileName(candidate).AsSpan(),
                ignoreCase: true))
            .ToArray();
    }
}

internal class HeadlessFileStream : System.IO.FileStream
{
    public HeadlessFileStream(string path, FileMode mode)
        : base(HeadlessFile.ResolveForMode(path, mode), mode)
    {
    }

    public HeadlessFileStream(string path, FileMode mode, FileAccess access)
        : base(HeadlessFile.ResolveForMode(path, mode), mode, access)
    {
    }
}

internal class HeadlessStreamReader : System.IO.StreamReader
{
    public HeadlessStreamReader(Stream stream)
        : base(stream)
    {
    }

    public HeadlessStreamReader(string path, Encoding encoding)
        : base(HeadlessPathResolver.ExistingOrOriginal(path), encoding)
    {
    }

    public HeadlessStreamReader(Stream stream, Encoding encoding)
        : base(stream, encoding)
    {
    }

    public HeadlessStreamReader(Stream stream, Encoding encoding, bool detectEncodingFromByteOrderMarks, int bufferSize, bool leaveOpen)
        : base(stream, encoding, detectEncodingFromByteOrderMarks, bufferSize, leaveOpen)
    {
    }
}

internal class HeadlessStreamWriter : System.IO.StreamWriter
{
    public HeadlessStreamWriter(string path, bool append, Encoding encoding)
        : base(HeadlessPathResolver.ForCreate(path), append, encoding)
    {
    }

    public HeadlessStreamWriter(Stream stream, Encoding encoding)
        : base(stream, encoding)
    {
    }
}
