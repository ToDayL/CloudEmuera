using System.Text;
using CloudEmuera.Application.GamePackages;

namespace CloudEmuera.Infrastructure.GamePackages;

internal sealed class ZipEntryPathPolicy(GamePackageIngestionLimits limits)
{
    private readonly Dictionary<string, (string Path, bool Directory)> paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> explicitDirectories = new(StringComparer.Ordinal);
    private readonly HashSet<string> pathsWithDescendants = new(StringComparer.Ordinal);

    public string Add(string rawName, bool directory)
    {
        if (rawName.Length == 0 || rawName[0] == '/' || rawName.Contains('\\') || rawName.Contains('\0') || (!directory && rawName.EndsWith('/')))
            Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry path is invalid.");
        string value = directory ? rawName.TrimEnd('/') : rawName;
        string[] rawSegments = value.Split('/');
        if (rawSegments.Length > limits.MaxDirectoryDepth) Reject(GamePackageRejectionCodes.PathDepthExceeded, "ZIP entry path is too deep.");
        var segments = new string[rawSegments.Length];
        for (int index = 0; index < rawSegments.Length; index++)
        {
            string raw = rawSegments[index];
            if (raw.Length == 0 || raw is "." or "..")
                Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry path segment is invalid.");
            string segment = raw;
            if (Encoding.UTF8.GetByteCount(segment) > limits.MaxSegmentUtf8Bytes) Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry path segment is too long.");
            segments[index] = segment;
        }
        string normalized = string.Join('/', segments);
        if (Encoding.UTF8.GetByteCount(normalized) > limits.MaxPathUtf8Bytes) Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry path is too long.");
        for (int index = 1; index < segments.Length; index++)
        {
            string parent = string.Join('/', segments.Take(index));
            if (paths.TryGetValue(parent, out var existingParent) && !existingParent.Directory)
                Reject(GamePackageRejectionCodes.PathTypeConflict, "A file conflicts with an entry parent directory.");
            paths.TryAdd(parent, (parent, true));
        }
        if (paths.TryGetValue(normalized, out var existing))
        {
            if (!directory || !existing.Directory || !explicitDirectories.Add(normalized))
                Reject(existing.Directory == directory ? GamePackageRejectionCodes.PathCollision : GamePackageRejectionCodes.PathTypeConflict, "ZIP entry path is duplicated or has a type conflict.");
        }
        else
        {
            paths.Add(normalized, (normalized, directory));
            if (directory) explicitDirectories.Add(normalized);
        }
        if (!directory && pathsWithDescendants.Contains(normalized))
            Reject(GamePackageRejectionCodes.PathTypeConflict, "A file conflicts with an existing directory prefix.");
        for (int index = 1; index < segments.Length; index++)
            pathsWithDescendants.Add(string.Join('/', segments.Take(index)));
        return normalized;
    }

    private static void Reject(string code, string message) => throw new GamePackageIngestionException(code, message);
}
