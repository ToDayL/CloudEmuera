namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// A logical path paired with a controlled runtime area.
/// The constructor validates the area and path and never accepts a host path.
/// </summary>
public readonly struct RuntimeFilePath : IEquatable<RuntimeFilePath>
{
    public RuntimeFilePath(RuntimeFileArea area, string relativePath)
        : this(area, RuntimeRelativePath.Parse(relativePath))
    {
    }

    public RuntimeFilePath(RuntimeFileArea area, RuntimeRelativePath relativePath)
    {
        if (!Enum.IsDefined(area))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.InvalidRelativePath,
                "The runtime file area is invalid.");
        }

        if (string.IsNullOrEmpty(relativePath.Value))
        {
            throw new RuntimePathException(
                RuntimePathReasonCodes.InvalidRelativePath,
                "A runtime file path cannot be empty.",
                area: area);
        }

        Area = area;
        RelativePath = relativePath;
    }

    public RuntimeFileArea Area { get; }

    public RuntimeRelativePath RelativePath { get; }

    public string LogicalPath => RelativePath.Value;

    public static RuntimeFilePath Parse(RuntimeFileArea area, string? relativePath) =>
        new(area, RuntimeRelativePath.Parse(relativePath));

    public static bool TryParse(RuntimeFileArea area, string? relativePath, out RuntimeFilePath path)
    {
        path = default;
        if (!Enum.IsDefined(area) || !RuntimeRelativePath.TryParse(relativePath, out RuntimeRelativePath parsed))
        {
            return false;
        }

        path = new RuntimeFilePath(area, parsed);
        return true;
    }

    public bool Equals(RuntimeFilePath other) => Area == other.Area && RelativePath == other.RelativePath;

    public override bool Equals(object? obj) => obj is RuntimeFilePath other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Area, RelativePath);

    public override string ToString() => $"{Area}:{RelativePath}";

    public static bool operator ==(RuntimeFilePath left, RuntimeFilePath right) => left.Equals(right);

    public static bool operator !=(RuntimeFilePath left, RuntimeFilePath right) => !left.Equals(right);
}
