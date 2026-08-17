import { useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import type { AssetResolver } from "./AssetResolver";
import { SafeHtmlRenderer, textStyleToCss } from "./SafeHtmlRenderer";
import { SpriteCanvas } from "./SpriteRenderer";
import type { Prompt, RealtimeLine, RealtimeNode } from "../realtime/protocol";

export interface ConsoleInputEvent {
  value: string;
  source: "KEYBOARD" | "BUTTON" | "POINTER";
  pointer?: { x: number; y: number; button: number; pressed: boolean };
  key?: { keyCode: number; control: boolean; alt: boolean; shift: boolean };
}

export function ScrollbackRenderer({ lines, currentPrompt, assets, onInput, onRenderError, scrollContainerRef }: { lines: RealtimeLine[]; currentPrompt?: Prompt | null; assets: AssetResolver; onInput: (event: ConsoleInputEvent) => void; onRenderError?: (message: string) => void; scrollContainerRef?: RefObject<HTMLElement | null> }) {
  const [atLatest, setAtLatest] = useState(true);
  const atLatestRef = useRef(true);
  const lastLine = lines[lines.length - 1];
  const contentVersion = `${lastLine?.lineId ?? ""}:${lastLine?.nodes.length ?? 0}:${lines.length}`;
  const currentButtonGeneration = latestButtonGeneration(lines);
  useEffect(() => {
    const container = scrollContainerRef?.current;
    if (!container) return;
    const updatePosition = () => {
      const next = container.scrollHeight - container.clientHeight - container.scrollTop <= 24;
      atLatestRef.current = next;
      setAtLatest(next);
    };
    updatePosition();
    container.addEventListener("scroll", updatePosition, { passive: true });
    return () => container.removeEventListener("scroll", updatePosition);
  }, [scrollContainerRef]);
  useEffect(() => {
    const container = scrollContainerRef?.current;
    if (container && atLatestRef.current && typeof container.scrollTo === "function") container.scrollTo({ top: container.scrollHeight, behavior: "auto" });
  }, [contentVersion, scrollContainerRef]);
  const scrollToLatest = () => {
    const container = scrollContainerRef?.current;
    if (!container) return;
    atLatestRef.current = true;
    setAtLatest(true);
    if (typeof container.scrollTo === "function") container.scrollTo({ top: container.scrollHeight, behavior: "smooth" });
  };
  return <div className="scrollback-shell">
    <div className="scrollback" aria-live="polite">
      {lines.map(line => <div className={`console-line align-${line.alignment} ${line.temporary ? "is-temporary" : ""}`} key={line.lineId}>
        {line.nodes.map((node, index) => <NodeRenderer key={`${line.lineId}-${index}`} node={node} assets={assets} currentPrompt={currentPrompt} currentButtonGeneration={currentButtonGeneration} onInput={onInput} onRenderError={onRenderError} />)}
      </div>)}
    </div>
    {!atLatest && <button className="scrollback-latest" type="button" onClick={scrollToLatest}>↓ 回到最新</button>}
  </div>;
}

function NodeRenderer({ node, currentPrompt, currentButtonGeneration, assets, onInput, onRenderError }: { node: RealtimeNode; currentPrompt?: Prompt | null; currentButtonGeneration: number; assets: AssetResolver; onInput: (event: ConsoleInputEvent) => void; onRenderError?: (message: string) => void }): ReactNode {
  switch (node.type) {
    case "text": return <span style={textStyleToCss(node.style, assets)}>{node.text}</span>;
    case "lineBreak": return <br />;
    case "button": {
      const isCurrentFrame = isCurrentFrameButton(node, currentPrompt, currentButtonGeneration);
      return <button className={`console-choice ${isCurrentFrame ? "" : "is-stale"}`} type="button" disabled={!isCurrentFrame} title={node.tooltip ?? (!isCurrentFrame && node.enabled ? "上一帧选项已失效" : undefined)} onClick={() => onInput({ value: node.value, source: "BUTTON" })}>
        {node.children.map((child, index) => <NodeRenderer key={index} node={child} currentPrompt={currentPrompt} currentButtonGeneration={currentButtonGeneration} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}
      </button>;
    }
    case "image": {
      const destination = node.destination ?? node.sourceRect;
      if (node.sourceRect && destination) return <SpriteCanvas sprite={{ assetId: node.assetId, sourceRect: node.sourceRect, frame: 0, animationFrames: [], opacity: 1 }} assets={assets} alt={node.decorative ? "" : node.altText ?? "游戏图片"} className="console-image" width={destination.width} height={destination.height} onRenderError={onRenderError} />;
      return <AssetImage assetId={node.assetId} alt={node.decorative ? "" : node.altText ?? "游戏图片"} assets={assets} className="console-image" width={node.destination?.width} height={node.destination?.height} />;
    }
    case "sprite": return <SpriteCanvas sprite={node} assets={assets} alt={node.altText ?? "游戏精灵"} className="console-sprite" width={node.destination.width} height={node.destination.height} onRenderError={onRenderError} />;
    case "shape": return <ShapeSvg shape={node.shape} bounds={node.bounds} points={node.points} fill={node.fill} stroke={node.stroke} />;
    case "htmlIsland": return <SafeHtmlRenderer node={node.root} assets={assets} className="console-html-island" onRenderError={onRenderError} />;
  }
}

function isCurrentFrameButton(node: Extract<RealtimeNode, { type: "button" }>, prompt: Prompt | null | undefined, currentGeneration: number): boolean {
  if (!node.enabled || !prompt || !prompt.allowedSources.includes("button")) return false;
  if (!["enterKey", "integer", "text", "anyValue", "integerButton", "textButton"].includes(prompt.inputType)) return false;
  return node.generation === currentGeneration;
}

function latestButtonGeneration(lines: readonly RealtimeLine[]): number {
  let generation = 0;
  for (const line of lines) {
    for (const node of line.nodes) {
      if (node.type === "button") generation = Math.max(generation, node.generation);
    }
  }
  return generation;
}

function AssetImage({ assetId, alt, assets, className, width, height }: { assetId: string; alt: string; assets: AssetResolver; className: string; width?: number; height?: number }) {
  const url = assets.url(assetId);
  if (!url) return <span className="console-missing-asset" role="img" aria-label={alt}>[资源不可用]</span>;
  return <img className={className} src={url} alt={alt} width={width} height={height} loading="lazy" decoding="async" />;
}

export function ShapeSvg({ shape, bounds, points, fill, stroke }: { shape: string; bounds: { x: number; y: number; width: number; height: number }; points: { x: number; y: number }[]; fill?: { red: number; green: number; blue: number; alpha: number } | null; stroke?: { red: number; green: number; blue: number; alpha: number } | null }) {
  const fillValue = fill ? `rgba(${fill.red},${fill.green},${fill.blue},${fill.alpha / 255})` : "none";
  const strokeValue = stroke ? `rgba(${stroke.red},${stroke.green},${stroke.blue},${stroke.alpha / 255})` : "none";
  if (shape === "line") {
    const first = points[0] ?? { x: 0, y: 0 };
    const second = points[1] ?? { x: bounds.width, y: bounds.height };
    return <svg className="console-shape" viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><line x1={first.x} y1={first.y} x2={second.x} y2={second.y} stroke={strokeValue === "none" ? fillValue : strokeValue} /></svg>;
  }
  if (shape === "ellipse") return <svg className="console-shape" viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><ellipse cx={bounds.width / 2} cy={bounds.height / 2} rx={bounds.width / 2} ry={bounds.height / 2} fill={fillValue} stroke={strokeValue} /></svg>;
  if (shape === "polygon") return <svg className="console-shape" viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><polygon points={points.map(point => `${point.x},${point.y}`).join(" ")} fill={fillValue} stroke={strokeValue} /></svg>;
  return <svg className="console-shape" viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><rect width={bounds.width} height={bounds.height} fill={shape === "space" ? "none" : fillValue} stroke={strokeValue} /></svg>;
}
