// CloudEmuera modification: normalize upstream display-only controls before
// they enter the control-character-free structured Console contract.
using System;
using System.Text;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

internal static class HeadlessDisplayText
{
    // The pinned upstream StringMeasure expands tabs to eight spaces for its
    // graphics display path. Keep the same visible width in the authoritative
    // headless layout while ensuring TextNode never receives U+0009.
    private const string TabReplacement = "        ";
    // GET_BETWEEN_STRING intentionally returns the upstream ETX sentinel when
    // a requested field is out of range. Desktop text controls do not render
    // that sentinel; remove it only from the display projection so malformed
    // optional labels do not abort the Worker. Runtime values and script data
    // never pass through this method.
    private const char DisplayOnlyEtxSentinel = '\u0003';

    public static string Project(string value, bool convertBackslashToYen)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Only the controls with a pinned upstream display meaning survive
        // this projection: TAB is expanded here, while LF/CR are consumed by
        // the structural line-break paths. Every other C0/C1 control has no
        // portable browser/display representation and is dropped at this
        // display-only boundary. This is deliberately not a global string
        // sanitizer: runtime values, input, paths and identifiers stay exact
        // and continue through their existing strict validators.
        StringBuilder builder = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '\t')
            {
                if (builder is null)
                {
                    builder = new StringBuilder(value.Length);
                    builder.Append(value, 0, index);
                }
                builder.Append(TabReplacement);
                continue;
            }

            if (character == DisplayOnlyEtxSentinel ||
                (char.IsControl(character) && character is not ('\n' or '\r')))
            {
                if (builder is null)
                {
                    builder = new StringBuilder(value.Length);
                    builder.Append(value, 0, index);
                }
                continue;
            }

            if (convertBackslashToYen && character == '\\')
            {
                if (builder is null)
                {
                    builder = new StringBuilder(value.Length);
                    builder.Append(value, 0, index);
                }
                builder.Append('\u00a5');
                continue;
            }

            builder?.Append(character);
        }

        return builder?.ToString() ?? value;
    }

    // The pinned upstream PRINT path treats LF as a line boundary. CR is also
    // a meaningful line boundary when a game returns CR or CRLF from its
    // string helpers; consume CRLF as one boundary before creating TextNode.
    public static bool TryGetLineBreak(string value, out int index, out int length)
    {
        ArgumentNullException.ThrowIfNull(value);

        int lineFeedIndex = value.IndexOf('\n');
        int carriageReturnIndex = value.IndexOf('\r');
        if (lineFeedIndex < 0 && carriageReturnIndex < 0)
        {
            index = -1;
            length = 0;
            return false;
        }

        if (carriageReturnIndex >= 0 &&
            (lineFeedIndex < 0 || carriageReturnIndex < lineFeedIndex))
        {
            index = carriageReturnIndex;
            length = carriageReturnIndex + 1 < value.Length && value[carriageReturnIndex + 1] == '\n' ? 2 : 1;
            return true;
        }

        index = lineFeedIndex;
        length = 1;
        return true;
    }

    // HtmlManager already has a structural LF -> <br> path. Normalize CR and
    // CRLF before handing a display-only fragment to that pinned parser so a
    // carriage return never becomes a raw TextNode character.
    public static string NormalizeHtmlLineBreaks(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\r', StringComparison.Ordinal))
            return value;

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    // Buttons, window titles and prompt metadata are single-line contract
    // fields. Preserve the upstream visible text while consuming their
    // meaningful line separators instead of passing controls to validation.
    public static string ProjectSingleLine(string value, bool convertBackslashToYen)
    {
        value = Project(value, convertBackslashToYen);
        return value
            .Replace("\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    // HTML title attributes are display-only text. The pinned upstream can use
    // line-feed separators in them (for example, a name followed by a
    // description), while the structured console contract deliberately keeps
    // control characters out of text fields. Keep the line break in the
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
