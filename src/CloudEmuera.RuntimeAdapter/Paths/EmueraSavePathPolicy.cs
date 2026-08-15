using System.Text;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>Fixed file categories exposed by the native Emuera save contract.</summary>
public enum EmueraSaveFileKind
{
    Normal,
    Global,
    AuxiliaryText,
    AuxiliaryImage,
}

/// <summary>A canonical logical path below the selected native save root.</summary>
public readonly record struct EmueraSavePath(
    RuntimeSaveLayout Layout,
    string Value,
    EmueraSaveFileKind Kind)
{
    public IReadOnlyList<string> Segments => Value.Split('/', StringSplitOptions.None);

    public string FileName => Segments[^1];

    public string? ParentPath
    {
        get
        {
            IReadOnlyList<string> segments = Segments;
            return segments.Count <= 1 ? null : string.Join('/', segments.Take(segments.Count - 1));
        }
    }
}

/// <summary>
/// Pure logical path rules shared by Runtime and the Session save-file API.
/// It never resolves a path against a host filesystem.
/// </summary>
public static class EmueraSavePathPolicy
{
    public const int MaximumDirectoryDepth = RuntimeRelativePath.MaxSegmentCount - 1;

    public static EmueraSavePath Parse(
        RuntimeSaveLayout layout,
        string? candidate,
        bool allowPhysicalSavPrefix = false,
        bool allowAuxiliaryInRoot = false)
    {
        if (!TryParse(layout, candidate, out EmueraSavePath parsed, allowPhysicalSavPrefix, allowAuxiliaryInRoot))
            throw new RuntimePathException(
                RuntimePathReasonCodes.InvalidRelativePath,
                "The logical Emuera save path is invalid.",
                candidate is null ? null : "<invalid>",
                RuntimeFileArea.Save);
        return parsed;
    }

    public static bool TryParse(
        RuntimeSaveLayout layout,
        string? candidate,
        out EmueraSavePath path,
        bool allowPhysicalSavPrefix = false,
        bool allowAuxiliaryInRoot = false)
    {
        path = default;
        if (!Enum.IsDefined(layout) || string.IsNullOrWhiteSpace(candidate))
            return false;

        string normalized;
        try
        {
            normalized = candidate.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!RuntimeRelativePath.TryParse(normalized, out RuntimeRelativePath relative))
            return false;

        string[] segments = relative.Segments.ToArray();
        if (!allowPhysicalSavPrefix && segments.Length > 0 && string.Equals(segments[0], "sav", StringComparison.Ordinal))
            return false;
        if (layout == RuntimeSaveLayout.Root && segments.Length != 1)
            return false;
        if (layout == RuntimeSaveLayout.SavDirectory && segments.Length > RuntimeRelativePath.MaxSegmentCount)
            return false;
        if (!IsAllowedSaveFileName(segments[^1]))
            return false;

        EmueraSaveFileKind kind = ClassifyFileName(segments[^1]);
        if (layout == RuntimeSaveLayout.Root && !allowAuxiliaryInRoot &&
            (kind is EmueraSaveFileKind.AuxiliaryText or EmueraSaveFileKind.AuxiliaryImage))
            return false;
        if (layout == RuntimeSaveLayout.SavDirectory && segments.Length > 1 &&
            segments[..^1].Any(segment => !IsAllowedSaveDirectorySegment(segment)))
            return false;

        path = new EmueraSavePath(layout, string.Join('/', segments), kind);
        return true;
    }

    public static bool IsAllowedSaveFileName(string? filename)
    {
        if (filename is null)
            return false;
        if (filename.Equals("global.sav", StringComparison.Ordinal))
            return true;
        return IsNumberedFile(filename, "save", ".sav") ||
            IsNumberedFile(filename, "txt", ".txt") ||
            IsNumberedFile(filename, "img", ".png");
    }

    public static bool IsAllowedSaveDirectorySegment(string? segment) =>
        segment is not null &&
        !IsAllowedSaveFileName(segment) &&
        segment.Length is > 0 and <= 64 &&
        segment.All(static character =>
            character is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '-' or '_');

    public static EmueraSaveFileKind ClassifyFileName(string filename)
    {
        if (filename.Equals("global.sav", StringComparison.Ordinal))
            return EmueraSaveFileKind.Global;
        if (filename.StartsWith("save", StringComparison.Ordinal) && filename.EndsWith(".sav", StringComparison.Ordinal))
            return EmueraSaveFileKind.Normal;
        if (filename.StartsWith("txt", StringComparison.Ordinal) && filename.EndsWith(".txt", StringComparison.Ordinal))
            return EmueraSaveFileKind.AuxiliaryText;
        if (filename.StartsWith("img", StringComparison.Ordinal) && filename.EndsWith(".png", StringComparison.Ordinal))
            return EmueraSaveFileKind.AuxiliaryImage;
        throw new ArgumentException("The file name is outside the native save contract.", nameof(filename));
    }

    public static bool AreCollisionFree(IEnumerable<string> names)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            string normalized;
            try
            {
                normalized = name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (!seen.Add(normalized))
                return false;
        }
        return true;
    }

    private static bool IsNumberedFile(string value, string prefix, string suffix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        int digitsLength = value.Length - prefix.Length - suffix.Length;
        if (digitsLength is < 1 or > 10)
            return false;
        for (int index = prefix.Length; index < prefix.Length + digitsLength; index++)
            if (value[index] is < '0' or > '9')
                return false;
        return true;
    }
}
