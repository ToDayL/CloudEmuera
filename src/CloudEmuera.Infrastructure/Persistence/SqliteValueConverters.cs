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
            value => value.ToString().ToUpperInvariant(),
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
        if (!Enum.TryParse(value, ignoreCase: true, out TEnum result) || !Enum.IsDefined(result))
        {
            throw new InvalidOperationException($"Unknown persisted {typeof(TEnum).Name} value.");
        }

        return result;
    }
}
