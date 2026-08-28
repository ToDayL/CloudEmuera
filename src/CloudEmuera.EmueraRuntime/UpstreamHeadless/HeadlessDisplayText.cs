// CloudEmuera modification: normalize upstream display-only whitespace before
// it enters the control-character-free structured Console contract.
using System;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

internal static class HeadlessDisplayText
{
    // The pinned upstream StringMeasure expands tabs to eight spaces for its
    // graphics display path. Keep the same visible width in the authoritative
    // headless layout while ensuring TextNode never receives U+0009.
    private const string TabReplacement = "        ";

    public static string Project(string value, bool convertBackslashToYen)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Contains('\t', StringComparison.Ordinal))
            value = value.Replace("\t", TabReplacement, StringComparison.Ordinal);

        if (convertBackslashToYen && value.Contains('\\', StringComparison.Ordinal))
            value = value.Replace('\\', '\u00a5');

        return value;
    }

    public static string ExpandTabs(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains('\t', StringComparison.Ordinal)
            ? value.Replace("\t", TabReplacement, StringComparison.Ordinal)
            : value;
    }
}
