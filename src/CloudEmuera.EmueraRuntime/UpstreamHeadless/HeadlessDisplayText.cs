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

    // HTML title attributes are display-only text. The pinned upstream game
    // uses line-feed separators in them (for example, token name followed by
    // its description), while the structured console contract deliberately
    // keeps control characters out of text fields. Keep the line break in the
    // browser-facing canonical form already understood by TooltipLayer.
    public static string ProjectTooltip(string value, bool convertBackslashToYen)
    {
        value = Project(value, convertBackslashToYen);
        return value
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);
    }

    public static string ExpandTabs(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains('\t', StringComparison.Ordinal)
            ? value.Replace("\t", TabReplacement, StringComparison.Ordinal)
            : value;
    }
}
