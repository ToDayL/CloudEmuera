using System.Text;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.RuntimeCompatibility.Tests;

internal static class RuntimeTranscriptProjector
{
    public static string Project(IEnumerable<ConsoleNode> nodes)
    {
        var result = new StringBuilder();
        foreach (ConsoleNode node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    result.Append(text.Text);
                    break;
                case LineBreakNode:
                    result.Append('\n');
                    break;
                case ButtonNode button:
                    foreach (TextNode child in button.Children.Cast<TextNode>())
                    {
                        result.Append(child.Text);
                    }

                    break;
                case PositionedInlineSegmentNode segment:
                    result.Append(Project(segment.Children));
                    break;
                case ImageNode:
                case SpriteNode:
                case ShapeNode:
                case HtmlIslandNode:
                    break;
            }
        }

        if (result.Length > 0 && result[^1] == '\n')
        {
            result.Length--;
        }

        return result.ToString();
    }
}
