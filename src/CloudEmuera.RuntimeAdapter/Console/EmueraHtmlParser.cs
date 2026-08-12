using System.Net;

namespace CloudEmuera.RuntimeAdapter;

public enum EmueraHtmlDiagnosticCode
{
    None,
    UnsupportedTagOrAttribute,
    MalformedFragment,
    TagLimitExceeded,
    NestingLimitExceeded,
    OutputLimitExceeded
}

public sealed class EmueraHtmlParseResult
{
    public EmueraHtmlParseResult(
        IEnumerable<ConsoleNode> nodes,
        bool wasFailClosed,
        EmueraHtmlDiagnosticCode diagnosticCode = EmueraHtmlDiagnosticCode.None)
        : this(nodes, wasFailClosed, diagnosticCode, ConsoleContractLimits.Default)
    {
    }

    internal EmueraHtmlParseResult(
        IEnumerable<ConsoleNode> nodes,
        bool wasFailClosed,
        EmueraHtmlDiagnosticCode diagnosticCode,
        ConsoleContractLimits limits)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        ConsoleNode[] copy = nodes.ToArray();
        ConsoleNodeValidation.ValidateBatchIfNotEmpty(copy, limits);
        Nodes = Array.AsReadOnly(copy);
        WasFailClosed = wasFailClosed;
        DiagnosticCode = diagnosticCode;
    }

    public IReadOnlyList<ConsoleNode> Nodes { get; }

    public bool WasFailClosed { get; }

    public EmueraHtmlDiagnosticCode DiagnosticCode { get; }
}

/// <summary>
/// Small linear fragment parser for the approved Emuera formatting subset.
/// It does not create a DOM and unknown/malformed markup becomes one plain
/// text node, so no unapproved tag can acquire behavior accidentally.
/// </summary>
public sealed class EmueraHtmlParser
{
    private readonly ConsoleContractLimits limits;

    public EmueraHtmlParser(ConsoleContractLimits? limits = null)
    {
        this.limits = limits ?? ConsoleContractLimits.Default;
        this.limits.Validate();
    }

    public IReadOnlyList<ConsoleNode> Parse(string fragment) => ParseWithDiagnostics(fragment).Nodes;

    /// <summary>Parses an HTML Island into the executable-free AST contract.</summary>
    public HtmlIslandNode ParseIsland(string fragment)
    {
        try
        {
            return new HtmlIslandNode(new SafeHtmlIslandParser(limits).Parse(fragment));
        }
        catch (ConsoleContractException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ConsoleContractException(ConsoleContractViolationReason.MalformedHtml, "The HTML Island is malformed.");
        }
    }

    public EmueraHtmlParseResult ParseWithDiagnostics(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Length > limits.MaxHtmlInputLength)
        {
            throw new ConsoleContractException(
                ConsoleContractViolationReason.HtmlInputTooLong,
                "The HTML fragment exceeds its input limit.",
                nameof(fragment));
        }

        try
        {
            IReadOnlyList<ConsoleNode> nodes = ParseCore(fragment);
            ConsoleNodeValidation.ValidateBatchIfNotEmpty(nodes, limits);
            return new EmueraHtmlParseResult(
                nodes,
                wasFailClosed: false,
                EmueraHtmlDiagnosticCode.None,
                limits);
        }
        catch (ParseFailure failure)
        {
            return FailClosed(fragment, failure.Reason, failure.DiagnosticCode);
        }
        catch (ConsoleContractException exception)
        {
            return FailClosed(
                fragment,
                exception.Reason,
                EmueraHtmlDiagnosticCode.OutputLimitExceeded);
        }
    }

    private EmueraHtmlParseResult FailClosed(
        string fragment,
        ConsoleContractViolationReason reason,
        EmueraHtmlDiagnosticCode diagnosticCode)
    {
        string fallback = SanitizeFallbackText(fragment);
        if (fallback.Length > limits.MaxTextLength)
        {
            throw new ConsoleContractException(
                reason,
                "The rejected HTML fragment cannot fit in a safe text node.",
                nameof(fragment));
        }

        try
        {
            return new EmueraHtmlParseResult(
                [new TextNode(fallback)],
                wasFailClosed: true,
                diagnosticCode,
                limits);
        }
        catch (ConsoleContractException exception)
        {
            throw new ConsoleContractException(
                exception.Reason,
                "The rejected HTML fragment cannot fit in a safe text node.",
                nameof(fragment));
        }
    }

    private System.Collections.ObjectModel.ReadOnlyCollection<ConsoleNode> ParseCore(string fragment)
    {
        var nodes = new List<ConsoleNode>();
        var stack = new List<TagFrame>();
        int tagCount = 0;
        int index = 0;

        while (index < fragment.Length)
        {
            int tagStart = fragment.IndexOf('<', index);
            if (tagStart < 0)
            {
                AppendText(nodes, fragment[index..], CurrentStyle(stack));
                break;
            }

            if (tagStart > index)
            {
                AppendText(nodes, fragment[index..tagStart], CurrentStyle(stack));
            }

            if (!LooksLikeTag(fragment, tagStart))
            {
                AppendText(nodes, "<", CurrentStyle(stack));
                index = tagStart + 1;
                continue;
            }

            int tagEnd = FindTagEnd(fragment, tagStart + 1);
            if (tagEnd < 0)
            {
                throw new ParseFailure(
                    ConsoleContractViolationReason.MalformedHtml,
                    EmueraHtmlDiagnosticCode.MalformedFragment);
            }

            tagCount++;
            if (tagCount > limits.MaxHtmlTagCount)
            {
                throw new ParseFailure(
                    ConsoleContractViolationReason.HtmlTagLimitExceeded,
                    EmueraHtmlDiagnosticCode.TagLimitExceeded);
            }

            string tagBody = fragment[(tagStart + 1)..tagEnd];
            ParseTag(tagBody, stack, nodes);
            index = tagEnd + 1;
        }

        if (stack.Count != 0)
        {
            throw new ParseFailure(
                ConsoleContractViolationReason.MalformedHtml,
                EmueraHtmlDiagnosticCode.MalformedFragment);
        }

        return Array.AsReadOnly(nodes.ToArray());
    }

    private void ParseTag(string tagBody, List<TagFrame> stack, List<ConsoleNode> nodes)
    {
        if (tagBody.Length == 0 || tagBody[0] is '!' or '?')
        {
            throw Unsupported();
        }

        int cursor = 0;
        bool closing = false;
        if (tagBody[cursor] == '/')
        {
            closing = true;
            cursor++;
        }

        int nameStart = cursor;
        while (cursor < tagBody.Length && IsAsciiLetter(tagBody[cursor]))
        {
            cursor++;
        }

        if (cursor == nameStart)
        {
            throw Unsupported();
        }

        string name = tagBody[nameStart..cursor].ToLowerInvariant();
        string remainder = tagBody[cursor..];
        if (closing)
        {
            if (remainder.Any(character => !char.IsWhiteSpace(character)))
            {
                throw Unsupported();
            }

            if (stack.Count == 0 || !string.Equals(stack[^1].Name, name, StringComparison.Ordinal))
            {
                throw new ParseFailure(
                    ConsoleContractViolationReason.MalformedHtml,
                    EmueraHtmlDiagnosticCode.MalformedFragment);
            }

            stack.RemoveAt(stack.Count - 1);
            return;
        }

        bool selfClosing = false;
        string trimmedRemainder = remainder.TrimEnd();
        if (name == "br" && trimmedRemainder.EndsWith('/'))
        {
            selfClosing = true;
            remainder = trimmedRemainder[..^1];
        }

        if (remainder.Any(character => !char.IsWhiteSpace(character)))
        {
            throw Unsupported();
        }

        if (name == "br")
        {
            nodes.Add(LineBreakNode.Instance);
            return;
        }

        ConsoleFontStyle decoration = name switch
        {
            "b" or "strong" => ConsoleFontStyle.Bold,
            "i" or "em" => ConsoleFontStyle.Italic,
            "u" => ConsoleFontStyle.Underline,
            "s" or "strike" => ConsoleFontStyle.Strike,
            _ => throw Unsupported()
        };

        if (selfClosing)
        {
            throw new ParseFailure(
                ConsoleContractViolationReason.MalformedHtml,
                EmueraHtmlDiagnosticCode.MalformedFragment);
        }

        if (stack.Count >= limits.MaxHtmlNestingDepth)
        {
            throw new ParseFailure(
                ConsoleContractViolationReason.HtmlNestingLimitExceeded,
                EmueraHtmlDiagnosticCode.NestingLimitExceeded);
        }

        ConsoleTextStyle style = CurrentStyle(stack);
        stack.Add(new TagFrame(name, style.WithDecorations(style.Decorations | decoration).Decorations));
    }

    private static ConsoleTextStyle CurrentStyle(IReadOnlyList<TagFrame> stack)
    {
        ConsoleFontStyle decorations = ConsoleFontStyle.None;
        foreach (TagFrame frame in stack)
        {
            decorations |= frame.Decorations;
        }

        return new ConsoleTextStyle(decorations: decorations);
    }

    private void AppendText(List<ConsoleNode> nodes, string text, ConsoleTextStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        string decoded = WebUtility.HtmlDecode(text) ?? string.Empty;
        int segmentStart = 0;
        for (int index = 0; index <= decoded.Length; index++)
        {
            bool isLineBreak = index < decoded.Length && decoded[index] is '\r' or '\n';
            if (index != decoded.Length && !isLineBreak)
            {
                continue;
            }

            if (index > segmentStart)
            {
                string segment = decoded[segmentStart..index];
                if (segment.Any(char.IsControl))
                {
                    throw new ParseFailure(
                        ConsoleContractViolationReason.MalformedHtml,
                        EmueraHtmlDiagnosticCode.MalformedFragment);
                }

                if (segment.Length > limits.MaxTextLength)
                {
                    throw new ParseFailure(
                        ConsoleContractViolationReason.TextTooLong,
                        EmueraHtmlDiagnosticCode.MalformedFragment);
                }

                AppendOrMergeText(nodes, new TextNode(segment, style));
            }

            if (isLineBreak)
            {
                nodes.Add(LineBreakNode.Instance);
                if (decoded[index] == '\r' && index + 1 < decoded.Length && decoded[index + 1] == '\n')
                {
                    index++;
                }

                segmentStart = index + 1;
            }
        }
    }

    private static void AppendOrMergeText(List<ConsoleNode> nodes, TextNode node)
    {
        if (nodes.Count > 0 && nodes[^1] is TextNode previous && previous.Style == node.Style)
        {
            nodes[^1] = new TextNode(previous.Text + node.Text, node.Style);
            return;
        }

        nodes.Add(node);
    }

    private static bool LooksLikeTag(string value, int index) =>
        index + 1 < value.Length && (IsAsciiLetter(value[index + 1]) || value[index + 1] == '/');

    private static int FindTagEnd(string value, int start)
    {
        char quote = '\0';
        for (int index = start; index < value.Length; index++)
        {
            char character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static ParseFailure Unsupported() => new(
        ConsoleContractViolationReason.UnsupportedHtml,
        EmueraHtmlDiagnosticCode.UnsupportedTagOrAttribute);

    private static string SanitizeFallbackText(string fragment)
    {
        string decoded = WebUtility.HtmlDecode(fragment) ?? string.Empty;
        var builder = new System.Text.StringBuilder(decoded.Length);
        foreach (char character in decoded)
        {
            builder.Append(char.IsControl(character) ? '\uFFFD' : character);
        }

        return builder.ToString();
    }

    private sealed record TagFrame(string Name, ConsoleFontStyle Decorations);

    private sealed class ParseFailure(
        ConsoleContractViolationReason reason,
        EmueraHtmlDiagnosticCode diagnosticCode) : Exception
    {
        public ConsoleContractViolationReason Reason { get; } = reason;

        public EmueraHtmlDiagnosticCode DiagnosticCode { get; } = diagnosticCode;
    }
}
