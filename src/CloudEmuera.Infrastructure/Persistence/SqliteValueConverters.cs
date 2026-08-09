using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CloudEmuera.Infrastructure.Persistence;

internal static class SqliteValueConverters
{
    public static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMilliseconds =
        new(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));

    public static readonly ValueConverter<DateTimeOffset?, long?> NullableDateTimeOffsetToUnixMilliseconds =
        new(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);

    public static readonly ValueComparer<DateTimeOffset> DateTimeOffsetComparer =
        new(
            (left, right) => left.UtcTicks == right.UtcTicks,
            value => value.UtcTicks.GetHashCode(),
            value => value.ToUniversalTime());

    public static readonly ValueComparer<DateTimeOffset?> NullableDateTimeOffsetComparer =
        new(
            (left, right) => left.GetValueOrDefault().UtcTicks == right.GetValueOrDefault().UtcTicks
                && left.HasValue == right.HasValue,
            value => value.HasValue ? value.Value.UtcTicks.GetHashCode() : 0,
            value => value.HasValue ? value.Value.ToUniversalTime() : null);

    public static ValueConverter<TEnum, string> CreateEnumConverter<TEnum>()
        where TEnum : struct, Enum =>
        new(
            value => ToStorageEnumName(value.ToString()),
            value => ParseEnum<TEnum>(value));

    public static ValueComparer<TEnum> CreateEnumComparer<TEnum>()
        where TEnum : struct, Enum =>
        new(
            (left, right) => left.Equals(right),
            value => value.GetHashCode(),
            value => value);

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        string normalized = NormalizeEnumName(value);
        foreach (TEnum candidate in Enum.GetValues<TEnum>())
            if (NormalizeEnumName(candidate.ToString()) == normalized) return candidate;
        throw new InvalidOperationException($"Unknown persisted {typeof(TEnum).Name} value.");
    }

    private static string ToStorageEnumName(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character) && (char.IsLower(value[index - 1]) || (index + 1 < value.Length && char.IsLower(value[index + 1]))))
                builder.Append('_');
            builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    private static string NormalizeEnumName(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
