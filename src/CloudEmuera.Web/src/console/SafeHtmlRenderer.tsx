import { Fragment, useEffect } from "react";
import type { CSSProperties, ReactNode } from "react";
import type { AssetResolver } from "./AssetResolver";
import type { RealtimeColor, RealtimeHtmlNode, RealtimeTextStyle } from "../realtime/protocol";

const safeTags = new Set(["span", "div", "p", "b", "strong", "i", "em", "u", "s", "strike", "img"]);

export function colorToCss(color: RealtimeColor | null | undefined): string | undefined {
  return color ? `rgba(${color.red}, ${color.green}, ${color.blue}, ${color.alpha / 255})` : undefined;
}

export function textStyleToCss(style: RealtimeTextStyle | null | undefined, assets?: AssetResolver): CSSProperties | undefined {
  if (!style) return undefined;
  const family = /^[A-Za-z0-9 _,'-]{1,80}$/.test(style.fontFamily) ? style.fontFamily : "inherit";
  const decorations = new Set(style.decorations.filter(value => ["bold", "italic", "underline", "line-through"].includes(value)));
  const result: CSSProperties = {
    color: colorToCss(style.foreground),
    backgroundColor: colorToCss(style.background),
    fontFamily: assets ? assets.fontFamily(style.fontFamily) : family,
    fontSize: `${style.fontSize}px`,
    lineHeight: style.lineHeight > 0 ? `${style.lineHeight}px` : undefined,
    fontWeight: decorations.has("bold") ? 700 : undefined,
    fontStyle: decorations.has("italic") ? "italic" : undefined,
    textDecoration: [decorations.has("underline") ? "underline" : "", decorations.has("line-through") ? "line-through" : ""].filter(Boolean).join(" ") || undefined,
  };
  if (style.buttonColor) (result as CSSProperties & Record<string, string>)["--console-button-color"] = colorToCss(style.buttonColor) ?? "";
  return result;
}

export function SafeHtmlRenderer({ node, assets, className, onRenderError }: { node: RealtimeHtmlNode; assets: AssetResolver; className?: string; onRenderError?: (message: string) => void }) {
  if (node.type === "element" && node.tag === "div") return renderHtmlNode(node, assets, onRenderError, className);
  return <span className={className}>{renderHtmlNode(node, assets, onRenderError)}</span>;
}

function renderHtmlNode(node: RealtimeHtmlNode, assets: AssetResolver, onRenderError?: (message: string) => void, className?: string): ReactNode {
  if (node.type === "text") return node.text;
  if (node.type === "break") return <br />;
  if (!safeTags.has(node.tag)) return null;
  const imageUrl = assets.url(node.assetId);
  if (node.tag === "img") {
    if (!imageUrl) return <MissingAssetReporter onRenderError={onRenderError} />;
    return <img className={["console-inline-asset", className].filter(Boolean).join(" ")} src={imageUrl} alt={node.altText ?? ""} style={textStyleToCss(node.style, assets)} />;
  }
  const Tag = (node.tag === "strike" ? "s" : node.tag) as "span" | "div" | "p" | "b" | "strong" | "i" | "em" | "u" | "s";
  const missingAsset = node.assetId && !imageUrl;
  return <Tag className={className} style={textStyleToCss(node.style, assets)}>
    {missingAsset && <MissingAssetReporter onRenderError={onRenderError} />}
    {imageUrl && <img className="console-inline-asset" src={imageUrl} alt={node.altText ?? ""} />}
    {node.children.map((child, index) => <Fragment key={index}>{renderHtmlNode(child, assets, onRenderError)}</Fragment>)}
  </Tag>;
}

function MissingAssetReporter({ onRenderError }: { onRenderError?: (message: string) => void }) {
  useEffect(() => { onRenderError?.("HTML Island 引用了未授权的 Session 资源。"); }, [onRenderError]);
  return <span className="console-missing-asset" role="img" aria-label="资源不可用">[资源不可用]</span>;
}
