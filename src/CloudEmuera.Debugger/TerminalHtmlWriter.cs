using System.Net;
using System.Text;
using System.Text.Json;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Debugging.Contracts;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Debugger;

internal static class TerminalHtmlWriter
{
    public static void Write(string path, ConsoleSnapshot? snapshot, DebugReplayResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var html = new StringBuilder(32 * 1024);
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>CloudEmuera replay console</title><style>")
            .Append("html{color-scheme:dark;background:#10130f;color:#eef2e8;font-family:monospace}.realtime-console-stage{max-width:100%;overflow:auto;padding:12px}.console-line{white-space:pre-wrap;min-height:var(--line-height)}button{font:inherit;color:inherit;background:#273126;border:1px solid #73806d}.debug-meta{border-bottom:1px solid #394137;padding:8px;color:#b8c3b2}</style></head><body>")
            .Append("<div class=\"debug-meta\" data-debug-status=\"").Append(E(result.Status)).Append("\">status: ").Append(E(result.Status)).Append("</div>");
        if (snapshot is null)
        {
            html.Append("<main class=\"realtime-console-stage\" data-debug-unavailable=\"true\">No committed console frame.</main>");
        }
        else
        {
            ConsoleSnapshot value = snapshot;
            html.Append("<main class=\"realtime-console-stage\" data-debug-scroll-top=\"0\" data-debug-client-width=\"")
                .Append(value.WindowMetadata.ViewportWidth).Append("\" data-debug-client-height=\"")
                .Append(value.WindowMetadata.ViewportHeight).Append("\" style=\"--line-height:")
                .Append(value.WindowMetadata.DefaultFont.LineHeight).Append("px;font-family:")
                .Append(E(value.WindowMetadata.DefaultFont.Family)).Append(";font-size:")
                .Append(value.WindowMetadata.DefaultFont.Size).Append("px\">");
            foreach (ConsoleLine line in value.Scrollback)
            {
                html.Append("<div class=\"console-line\" data-line-id=\"").Append(E(line.LineId)).Append("\" data-debug-width=\"")
                    .Append(line.LayoutWidth).Append("\" data-debug-height=\"").Append(line.LineHeight).Append("\">");
                foreach (ConsoleNode node in line.Nodes) AppendNode(html, node);
                html.Append("</div>");
            }
            if (value.CurrentPrompt is { } prompt)
                html.Append("<div class=\"console-prompt\" data-prompt-id=\"").Append(E(prompt.PromptId)).Append("\" data-input-type=\"")
                    .Append(E(prompt.InputType.ToString())).Append("\"><input readonly value=\"").Append(E(prompt.DefaultValue ?? string.Empty)).Append("\"></div>");
            html.Append("</main>");
            var realtime = RealtimePayloadMapper.ToSnapshot(1, value, result.LastFrameId);
            html.Append("<script type=\"application/json\" id=\"cloudemuera-terminal-state\">")
                .Append(JsonSerializer.Serialize(realtime, DebugTraceJson.Options).Replace("<", "\\u003c", StringComparison.Ordinal))
                .Append("</script>");
        }
        html.Append("</body></html>");
        string temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, html.ToString(), new UTF8Encoding(false));
        File.Move(temporary, fullPath, overwrite: true);
    }

    private static void AppendNode(StringBuilder html, ConsoleNode node)
    {
        switch (node)
        {
            case TextNode text:
                html.Append("<span class=\"console-text\">").Append(E(text.Text)).Append("</span>");
                break;
            case LineBreakNode:
                html.Append("<br>");
                break;
            case ButtonNode button:
                html.Append("<button type=\"button\" tabindex=\"-1\" data-value=\"").Append(E(button.Value)).Append("\" data-generation=\"")
                    .Append(button.Generation).Append("\" aria-disabled=\"true\">");
                foreach (ConsoleNode child in button.Children) AppendNode(html, child);
                html.Append("</button>");
                break;
            case PositionedInlineSegmentNode positioned:
                html.Append("<span class=\"positioned-inline-segment\" style=\"display:inline-block;transform:translateX(")
                    .Append(positioned.PositionX).Append("px)\">");
                foreach (ConsoleNode child in positioned.Children) AppendNode(html, child);
                html.Append("</span>");
                break;
            case DivNode div:
                html.Append("<div class=\"console-div\" data-debug-x=\"").Append(div.Bounds.X).Append("\" data-debug-y=\"").Append(div.Bounds.Y)
                    .Append("\" data-debug-width=\"").Append(div.Bounds.Width).Append("\" data-debug-height=\"").Append(div.Bounds.Height).Append("\">");
                foreach (ConsoleNode child in div.Children) AppendNode(html, child);
                html.Append("</div>");
                break;
            case ImageNode image:
                html.Append("<span class=\"console-image\" data-asset-id=\"").Append(E(image.AssetId.Value)).Append("\">[image: ").Append(E(image.AltText ?? image.AssetId.Value)).Append("]</span>");
                break;
            case SpriteNode sprite:
                html.Append("<span class=\"console-sprite\" data-asset-id=\"").Append(E(sprite.AssetId.Value)).Append("\">[sprite: ").Append(E(sprite.AltText ?? sprite.AssetId.Value)).Append("]</span>");
                break;
            default:
                html.Append("<span data-node-kind=\"").Append(E(node.GetType().Name)).Append("\"></span>");
                break;
        }
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
