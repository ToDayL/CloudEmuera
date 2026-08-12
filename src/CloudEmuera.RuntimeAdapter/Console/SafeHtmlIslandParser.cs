using System.Globalization;
using System.Net;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Parses the intentionally small HTML Island vocabulary into an executable-free
/// tree. The parser never returns a raw fragment, attribute string, URL or CSS
/// value to callers.
/// </summary>
internal sealed class SafeHtmlIslandParser
{
    private readonly ConsoleContractLimits limits;
    private string source = string.Empty;
    private int cursor;
    private int tagCount;
    private int nodeCount;

    public SafeHtmlIslandParser(ConsoleContractLimits limits)
    {
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        limits.Validate();
    }

    public ConsoleHtmlNode Parse(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Length > limits.MaxHtmlInputLength)
            throw new ConsoleContractException(ConsoleContractViolationReason.HtmlInputTooLong, "The HTML fragment exceeds its input limit.");
        source = fragment;
        cursor = 0;
        tagCount = 0;
        nodeCount = 0;
        List<ConsoleHtmlNode> children = ParseChildren(null, 0);
        if (cursor != source.Length)
            throw Failure("The HTML fragment contains trailing data.");
        var root = new ConsoleHtmlElementNode("div", children);
        root.Validate(limits, 1);
        return root;
    }

    private List<ConsoleHtmlNode> ParseChildren(string? closingTag, int depth)
    {
        if (depth >= limits.MaxHtmlNestingDepth)
            throw new ConsoleContractException(ConsoleContractViolationReason.HtmlNestingLimitExceeded, "The HTML tree is too deep.");
        var result = new List<ConsoleHtmlNode>();
        while (cursor < source.Length)
        {
            if (source[cursor] != '<')
            {
                int start = cursor;
                int next = source.IndexOf('<', cursor);
                cursor = next < 0 ? source.Length : next;
                string decoded = WebUtility.HtmlDecode(source[start..cursor]) ?? string.Empty;
                if (decoded.Length > 0)
                {
                    AddNode(result, new ConsoleHtmlTextNode(decoded));
                }
                continue;
            }

            int end = FindTagEnd(cursor + 1);
            if (end < 0)
                throw Failure("The HTML tag is not closed.");
            string body = source[(cursor + 1)..end];
            cursor = end + 1;
            tagCount = checked(tagCount + 1);
            if (tagCount > limits.MaxHtmlTagCount)
                throw new ConsoleContractException(ConsoleContractViolationReason.HtmlTagLimitExceeded, "The HTML tag count exceeds its limit.");
            if (body.StartsWith('/'))
            {
                string found = body[1..].Trim();
                if (!string.Equals(found, closingTag, StringComparison.Ordinal))
                    throw Failure("The HTML closing tag does not match the open tag.");
                return result;
            }

            bool selfClosing = body.TrimEnd().EndsWith('/');
            if (selfClosing)
                body = body.TrimEnd()[..^1];
            (string tag, Dictionary<string, string> attributes) = ParseStartTag(body);
            if (tag == "br")
            {
                if (attributes.Count != 0 || !selfClosing)
                    throw Failure("The br element must be an empty allowlisted element.");
                AddNode(result, ConsoleHtmlBreakNode.Instance);
                continue;
            }

            if (tag == "img")
            {
                if (!selfClosing || !attributes.TryGetValue("asset", out string? asset) || attributes.ContainsKey("src"))
                    throw Failure("The img element requires a manifest asset and cannot use a URL.");
                string? alt = attributes.GetValueOrDefault("alt");
                _ = new ConsoleAssetId(asset);
                AddNode(result, new ConsoleHtmlElementNode(tag, Array.Empty<ConsoleHtmlNode>(), altText: alt, assetId: asset));
                continue;
            }

            ConsoleTextStyle style = ParseStyle(attributes);
            List<ConsoleHtmlNode> nested = ParseChildren(tag, depth + 1);
            AddNode(result, new ConsoleHtmlElementNode(tag, nested, style));
        }

        if (closingTag is not null)
            throw Failure("The HTML fragment has an unclosed element.");
        return result;
    }

    private static (string Tag, Dictionary<string, string> Attributes) ParseStartTag(string body)
    {
        int index = 0;
        SkipWhitespace(body, ref index);
        int nameStart = index;
        while (index < body.Length && IsNameCharacter(body[index]))
            index++;
        if (nameStart == index)
            throw Failure("The HTML tag name is invalid.");
        string tag = body[nameStart..index].ToLowerInvariant();
        if (tag is not ("span" or "div" or "p" or "b" or "strong" or "i" or "em" or "u" or "s" or "strike" or "img" or "br"))
            throw new ConsoleContractException(ConsoleContractViolationReason.UnsupportedHtml, "The HTML tag is not allowlisted.");

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (index < body.Length)
        {
            SkipWhitespace(body, ref index);
            if (index == body.Length)
                break;
            int attributeStart = index;
            while (index < body.Length && IsNameCharacter(body[index]))
                index++;
            if (attributeStart == index)
                throw Failure("The HTML attribute name is invalid.");
            string attribute = body[attributeStart..index].ToLowerInvariant();
            SkipWhitespace(body, ref index);
            if (index >= body.Length || body[index] != '=')
                throw Failure("HTML attributes require a value.");
            index++;
            SkipWhitespace(body, ref index);
            if (index >= body.Length || body[index] is not ('\'' or '"'))
                throw Failure("HTML attribute values must be quoted.");
            char quote = body[index++];
            int valueStart = index;
            while (index < body.Length && body[index] != quote)
                index++;
            if (index >= body.Length)
                throw Failure("The HTML attribute value is not closed.");
            string value = WebUtility.HtmlDecode(body[valueStart..index]) ?? string.Empty;
            index++;
            if (!attributes.TryAdd(attribute, value))
                throw Failure("Duplicate HTML attributes are not allowed.");
            if (attributes.Count > 8)
                throw new ConsoleContractException(ConsoleContractViolationReason.HtmlNodeLimitExceeded, "The HTML element has too many attributes.");
        }

        foreach (string name in attributes.Keys)
        {
            if (name is not ("asset" or "alt" or "color" or "bgcolor" or "font" or "size"))
                throw new ConsoleContractException(ConsoleContractViolationReason.UnsupportedHtml, "The HTML attribute is not allowlisted.");
        }

        return (tag, attributes);
    }

    private static ConsoleTextStyle ParseStyle(Dictionary<string, string> attributes)
    {
        ConsoleColor? foreground = attributes.TryGetValue("color", out string? color) ? ParseColor(color) : null;
        ConsoleColor? background = attributes.TryGetValue("bgcolor", out string? backgroundValue) ? ParseColor(backgroundValue) : null;
        string? family = attributes.GetValueOrDefault("font");
        int size = attributes.TryGetValue("size", out string? sizeValue) && int.TryParse(sizeValue, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 16;
        if (attributes.ContainsKey("size") && (size <= 0 || size > 256))
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidFont, "The HTML font size is outside its limit.");
        return new ConsoleTextStyle(foreground, background, ConsoleFontStyle.None, family, size);
    }

    private static ConsoleColor ParseColor(string value)
    {
        if (value.Length is not (4 or 7) || value[0] != '#')
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidColor, "HTML colors must use #RGB or #RRGGBB.");
        static byte ParseHex(ReadOnlySpan<char> value) => byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return value.Length == 4
            ? ConsoleColor.FromRgb((byte)(ParseHex(value.AsSpan(1, 1)) * 17), (byte)(ParseHex(value.AsSpan(2, 1)) * 17), (byte)(ParseHex(value.AsSpan(3, 1)) * 17))
            : ConsoleColor.FromRgb(ParseHex(value.AsSpan(1, 2)), ParseHex(value.AsSpan(3, 2)), ParseHex(value.AsSpan(5, 2)));
    }

    private void AddNode(List<ConsoleHtmlNode> nodes, ConsoleHtmlNode node)
    {
        nodeCount = checked(nodeCount + 1);
        if (nodeCount > limits.MaxHtmlChildren * limits.MaxHtmlNestingDepth)
            throw new ConsoleContractException(ConsoleContractViolationReason.HtmlNodeLimitExceeded, "The HTML node count exceeds its limit.");
        nodes.Add(node);
    }

    private int FindTagEnd(int start)
    {
        char quote = '\0';
        for (int index = start; index < source.Length; index++)
        {
            char value = source[index];
            if (quote != '\0')
            {
                if (value == quote)
                    quote = '\0';
            }
            else if (value is '\'' or '"')
            {
                quote = value;
            }
            else if (value == '>')
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsNameCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '_' or '-';

    private static void SkipWhitespace(string value, ref int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;
    }

    private static ConsoleContractException Failure(string message) =>
        new(ConsoleContractViolationReason.MalformedHtml, message);
}
