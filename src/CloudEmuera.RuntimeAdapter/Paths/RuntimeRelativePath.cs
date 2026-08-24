using System.Globalization;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// A validated, slash-separated path supplied to the game runtime.
/// It is a logical path, never a host absolute path. Creation is only possible
/// through <see cref="Parse"/> or <see cref="TryParse"/>.
/// </summary>
public readonly struct RuntimeRelativePath : IEquatable<RuntimeRelativePath>, IComparable<RuntimeRelativePath>
{
    public const int MaxLength = 4096;
    public const int MaxSegmentCount = 64;
    public const int MaxSegmentLength = 255;

    private readonly string? value;

    private RuntimeRelativePath(string value)
    {
        this.value = value;
    }

    /// <summary>
    /// Gets the canonical slash-separated logical path.
    /// </summary>
    public string Value => value ?? string.Empty;

    /// <summary>
    /// Gets the validated path segments in their original ordinal form.
    /// </summary>
    public IReadOnlyList<string> Segments =>
        Value.Length == 0 ? Array.Empty<string>() : Value.Split('/', StringSplitOptions.None);

    public static RuntimeRelativePath Parse(string? candidate)
    {
        if (!TryParse(candidate, out RuntimeRelativePath parsed))
        {
            throw Invalid(candidate);
        }

        return parsed;
    }

    public static bool TryParse(string? candidate, out RuntimeRelativePath path)
    {
        path = default;

        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength)
        {
            return false;
        }

        if (candidate[0] == '/' || candidate[^1] == '/' || candidate.Contains('\\') || candidate.Contains('\0'))
        {
            return false;
        }

        string[] segments = candidate.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Length > MaxSegmentCount)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                return false;
            }
        }

        path = new RuntimeRelativePath(candidate);
        return true;
    }

    public int CompareTo(RuntimeRelativePath other) =>
        string.Compare(Value, other.Value, StringComparison.Ordinal);

    public bool Equals(RuntimeRelativePath other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is RuntimeRelativePath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static bool operator ==(RuntimeRelativePath left, RuntimeRelativePath right) => left.Equals(right);

    public static bool operator !=(RuntimeRelativePath left, RuntimeRelativePath right) => !left.Equals(right);

    public static bool operator <(RuntimeRelativePath left, RuntimeRelativePath right) => left.CompareTo(right) < 0;

    public static bool operator <=(RuntimeRelativePath left, RuntimeRelativePath right) => left.CompareTo(right) <= 0;

    public static bool operator >(RuntimeRelativePath left, RuntimeRelativePath right) => left.CompareTo(right) > 0;

    public static bool operator >=(RuntimeRelativePath left, RuntimeRelativePath right) => left.CompareTo(right) >= 0;

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > MaxSegmentLength || segment is "." or "..")
        {
            return false;
        }

        for (int index = 0; index < segment.Length; index++)
        {
            char character = segment[index];
            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character) ||
                    index + 1 >= segment.Length ||
                    !char.IsLowSurrogate(segment[index + 1]))
                {
                    return false;
                }

                index++;
            }
        }

        return true;
    }

    private static RuntimePathException Invalid(string? candidate)
    {
        string diagnostic = candidate is null ? "<null>" : "<invalid>";
        return new RuntimePathException(
            RuntimePathReasonCodes.InvalidRelativePath,
            string.Format(CultureInfo.InvariantCulture, "The runtime path is not a valid relative logical path: {0}.", diagnostic),
            candidate is null ? null : "<invalid>");
    }
}
