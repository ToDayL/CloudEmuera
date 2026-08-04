using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class EmueraHtmlParserTests
{
    [Fact]
    public void ApprovedFormattingProducesStyledTextNodes()
    {
        var parser = new EmueraHtmlParser();
        IReadOnlyList<ConsoleNode> nodes = parser.Parse("<b>bold</b> <i>italic</i><br>done &amp; ready");

        Assert.Collection(
            nodes,
            node => Assert.Equal(ConsoleFontStyle.Bold, Assert.IsType<TextNode>(node).Style.Decorations),
            node => Assert.Equal(" ", Assert.IsType<TextNode>(node).Text),
            node => Assert.Equal(ConsoleFontStyle.Italic, Assert.IsType<TextNode>(node).Style.Decorations),
            node => Assert.IsType<LineBreakNode>(node),
            node => Assert.Equal("done & ready", Assert.IsType<TextNode>(node).Text));
    }

    [Fact]
    public void UnknownTagsAndAttributesFailClosedAsOnePlainTextNode()
    {
        var parser = new EmueraHtmlParser();
        EmueraHtmlParseResult result = parser.ParseWithDiagnostics("<script>alert(1)</script><b onclick=evil>x</b>");

        Assert.True(result.WasFailClosed);
        Assert.NotEqual(EmueraHtmlDiagnosticCode.None, result.DiagnosticCode);
        TextNode node = Assert.IsType<TextNode>(Assert.Single(result.Nodes));
        Assert.Equal(ConsoleFontStyle.None, node.Style.Decorations);
        Assert.Contains("script", node.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedAndUnclosedFragmentsDoNotPartiallyParse()
    {
        var parser = new EmueraHtmlParser();
        EmueraHtmlParseResult result = parser.ParseWithDiagnostics("prefix <b>bold");

        Assert.True(result.WasFailClosed);
        TextNode node = Assert.IsType<TextNode>(Assert.Single(result.Nodes));
        Assert.Equal("prefix <b>bold", node.Text);
    }

    [Fact]
    public void ParserRejectsOversizedInputWithStableReason()
    {
        var limits = new ConsoleContractLimits { MaxHtmlInputLength = 4 };
        var exception = Assert.Throws<ConsoleContractException>(() => new EmueraHtmlParser(limits).Parse("12345"));

        Assert.Equal(ConsoleContractViolationReason.HtmlInputTooLong, exception.Reason);
    }

    [Fact]
    public void ParserAppliesInjectedBatchTextTagAndNestingLimits()
    {
        var batchLimited = new EmueraHtmlParser(new ConsoleContractLimits { MaxBatchNodeCount = 1 });
        EmueraHtmlParseResult batchResult = batchLimited.ParseWithDiagnostics("a<br>b");
        Assert.True(batchResult.WasFailClosed);
        Assert.Single(batchResult.Nodes);

        var textLimited = new EmueraHtmlParser(new ConsoleContractLimits { MaxTextLength = 3 });
        ConsoleContractException textException = Assert.Throws<ConsoleContractException>(
            () => textLimited.ParseWithDiagnostics("abcd"));
        Assert.Equal(ConsoleContractViolationReason.TextTooLong, textException.Reason);

        var tagLimited = new EmueraHtmlParser(new ConsoleContractLimits { MaxHtmlTagCount = 1 });
        Assert.True(tagLimited.ParseWithDiagnostics("<b>x</b>").WasFailClosed);

        var nestingLimited = new EmueraHtmlParser(new ConsoleContractLimits { MaxHtmlNestingDepth = 1 });
        Assert.True(nestingLimited.ParseWithDiagnostics("<b><i>x</i></b>").WasFailClosed);
    }

    [Fact]
    public void DangerousAttributesAndUrlLikeValuesNeverBecomeCapabilityNodes()
    {
        var parser = new EmueraHtmlParser();
        var attacks = new List<string>
        {
            "<a href=\"javascript:alert(1)\">x</a>",
            "<img src=\"data:image/svg+xml,evil\">",
            "<b style=\"background:url(https://evil.invalid)\">x</b>",
            "<a href=\"//evil.invalid/path\">x</a>",
            "<iframe src=\"https://evil.invalid\"></iframe>"
        };

        foreach (string attack in attacks)
        {
            EmueraHtmlParseResult result = parser.ParseWithDiagnostics(attack);
            Assert.True(result.WasFailClosed);
            Assert.DoesNotContain(result.Nodes, node => node is ButtonNode or ImageNode);
        }
    }
}
