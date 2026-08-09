using System.Text;
using CloudEmuera.Application.GamePackages;

namespace CloudEmuera.Infrastructure.GamePackages;

internal sealed class ZipEntryPathPolicy(GamePackageIngestionLimits limits)
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    private readonly Dictionary<string, (string Path, bool Directory)> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> explicitDirectories = new(StringComparer.Ordinal);

    public string Add(string rawName, bool directory)
    {
        if (rawName.Length == 0 || rawName[0] == '/' || rawName.Contains('\\') || rawName.Contains(':')
            || rawName.Contains('\0') || (!directory && rawName.EndsWith('/')))
            Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry path is invalid.");
        string value = directory ? rawName.TrimEnd('/') : rawName;
        string[] rawSegments = value.Split('/');
        if (rawSegments.Length > limits.MaxDirectoryDepth) Reject(GamePackageRejectionCodes.PathDepthExceeded, "ZIP entry path is too deep.");
        var segments = new string[rawSegments.Length];
        for (int index = 0; index < rawSegments.Length; index++)
        {
            string raw = rawSegments[index];
            if (raw.Length == 0 || raw is "." or ".." || raw.Any(character => char.IsControl(character) || IsNonCharacter(character)))
                Reject(GamePackageRejectionCodes.PathInvalid, "ZIP entry path segment is invalid.");
            string segment = raw.Normalize(NormalizationForm.FormC);
            if (segment.EndsWith(' ') || segment.EndsWith('.')) Reject(GamePackageRejectionCodes.PathReservedName, "ZIP entry path has a non-portable suffix.");
            string stem = segment.Split('.')[0];
            if (Reserved.Contains(stem)) Reject(GamePackageRejectionCodes.PathReservedName, "ZIP entry path uses a reserved name.");
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
            if (existing.Path != normalized) Reject(GamePackageRejectionCodes.PathCollision, "ZIP entry paths collide by case or Unicode normalization.");
            if (!directory || !existing.Directory || !explicitDirectories.Add(normalized))
                Reject(existing.Directory == directory ? GamePackageRejectionCodes.PathCollision : GamePackageRejectionCodes.PathTypeConflict, "ZIP entry path is duplicated or has a type conflict.");
        }
        else
        {
            paths.Add(normalized, (normalized, directory));
            if (directory) explicitDirectories.Add(normalized);
        }
        if (!directory && paths.Keys.Any(key => key.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)))
            Reject(GamePackageRejectionCodes.PathTypeConflict, "A file conflicts with an existing directory prefix.");
        return normalized;
    }

    private static bool IsNonCharacter(char value) => value is >= '\uFDD0' and <= '\uFDEF' || (value & 0xFFFE) == 0xFFFE;
    private static void Reject(string code, string message) => throw new GamePackageIngestionException(code, message);
}
