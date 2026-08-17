import { Fragment, useCallback, useEffect, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import type { AssetResolver } from "./AssetResolver";
import { SafeHtmlRenderer, textStyleToCss } from "./SafeHtmlRenderer";
import { SpriteCanvas } from "./SpriteRenderer";
import type { Prompt, RealtimeBoxModel, RealtimeColor, RealtimeInsets, RealtimeLine, RealtimeNode } from "../realtime/protocol";

export interface ConsoleInputEvent {
  value: string;
  source: "KEYBOARD" | "BUTTON" | "POINTER";
  pointer?: { x: number; y: number; button: number; pressed: boolean };
  key?: { keyCode: number; control: boolean; alt: boolean; shift: boolean };
}

export function ScrollbackRenderer({ lines, currentPrompt, assets, onInput, onRenderError, scrollContainerRef, scrollVersion }: { lines: RealtimeLine[]; currentPrompt?: Prompt | null; assets: AssetResolver; onInput: (event: ConsoleInputEvent) => void; onRenderError?: (message: string) => void; scrollContainerRef?: RefObject<HTMLElement | null>; scrollVersion?: string | number }) {
  const [atLatest, setAtLatest] = useState(true);
  const atLatestRef = useRef(true);
  const currentButtonGeneration = latestButtonGeneration(lines);
  const displayLines = trimTrailingEmptyLines(lines);
  const scrollToBottom = useCallback((behavior: ScrollBehavior) => {
    const container = scrollContainerRef?.current;
    if (!container) return;
    const top = Math.max(0, container.scrollHeight - container.clientHeight);
    atLatestRef.current = true;
    setAtLatest(true);
    if (typeof container.scrollTo === "function") container.scrollTo({ top, behavior });
    else container.scrollTop = top;
  }, [scrollContainerRef]);
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
    scrollToBottom("auto");
    // New output is authoritative for the reading position: the console always follows it.
  }, [lines, scrollToBottom, scrollVersion]);
  const scrollToLatest = useCallback(() => scrollToBottom("smooth"), [scrollToBottom]);
  return <div className="scrollback-shell">
    <div className="scrollback" aria-live="polite">
      {displayLines.map(line => <div className={`console-line align-${line.alignment} ${line.temporary ? "is-temporary" : ""} ${line.noWrap ? "is-nowrap" : ""}`} key={line.lineId}>
        {trimTrailingLineBreaks(line.nodes).map((node, index) => <NodeRenderer key={`${line.lineId}-${index}`} node={node} assets={assets} currentPrompt={currentPrompt} currentButtonGeneration={currentButtonGeneration} onInput={onInput} onRenderError={onRenderError} />)}
      </div>)}
    </div>
    {!atLatest && <button className="scrollback-latest" type="button" onClick={scrollToLatest}>↓ 回到最新</button>}
  </div>;
}

export function trimTrailingEmptyLines(lines: readonly RealtimeLine[]): RealtimeLine[] {
  let end = lines.length;
  if (end > 0 && isEmptyLine(lines[end - 1])) end--;
  return lines.slice(0, end);
}

function isEmptyLine(line: RealtimeLine): boolean {
  return line.nodes.length === 0 || line.nodes.every(node => node.type === "lineBreak" || (node.type === "text" && node.text.length === 0));
}

function trimTrailingLineBreaks(nodes: readonly RealtimeNode[]): RealtimeNode[] {
  let end = nodes.length;
  while (end > 0 && nodes[end - 1].type === "lineBreak") end--;
  return nodes.slice(0, end);
}

function splitButtonLabel(children: readonly RealtimeNode[]): { leading: RealtimeNode[]; label: RealtimeNode[]; trailing: RealtimeNode[] } {
  if (children.some(child => child.type !== "text"))
    return { leading: [], label: [...children], trailing: [] };

  const textChildren = children as Extract<RealtimeNode, { type: "text" }>[];
  const text = textChildren.map(child => child.text).join("");
  const leadingLength = text.match(/^ */)?.[0].length ?? 0;
  const trailingLength = text.match(/ *$/)?.[0].length ?? 0;
  const labelStart = leadingLength;
  const labelEnd = text.length - trailingLength;
  if (labelStart >= labelEnd)
    return { leading: sliceTextNodes(textChildren, 0, text.length), label: [], trailing: [] };

  return {
    leading: sliceTextNodes(textChildren, 0, labelStart),
    label: sliceTextNodes(textChildren, labelStart, labelEnd),
    trailing: sliceTextNodes(textChildren, labelEnd, text.length),
  };
}

function sliceTextNodes(children: readonly Extract<RealtimeNode, { type: "text" }>[], start: number, end: number): RealtimeNode[] {
  const result: RealtimeNode[] = [];
  let offset = 0;
  for (const child of children) {
    const childStart = offset;
    const childEnd = childStart + child.text.length;
    const sliceStart = Math.max(start, childStart);
    const sliceEnd = Math.min(end, childEnd);
    if (sliceStart < sliceEnd)
      result.push({ ...child, text: child.text.slice(sliceStart - childStart, sliceEnd - childStart) });
    offset = childEnd;
  }
  return result;
}

export function NodeRenderer({ node, currentPrompt, currentButtonGeneration, assets, onInput, onRenderError }: { node: RealtimeNode; currentPrompt?: Prompt | null; currentButtonGeneration: number; assets: AssetResolver; onInput: (event: ConsoleInputEvent) => void; onRenderError?: (message: string) => void }): ReactNode {
  switch (node.type) {
    case "text": return <span className={node.style.buttonColor ? "console-text has-button-color" : "console-text"} style={textStyleToCss(node.style, assets)}>{node.text}</span>;
    case "lineBreak": return <br />;
    case "button": {
      if (!node.enabled && node.value.length === 0) {
        return <span className="console-nonbutton" title={node.tooltip ?? undefined}>{node.children.map((child, index) => <NodeRenderer key={`nonbutton-${index}`} node={child} currentPrompt={currentPrompt} currentButtonGeneration={currentButtonGeneration} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}</span>;
      }
      const isCurrentFrame = isCurrentFrameButton(node, currentPrompt, currentButtonGeneration);
      const parts = splitButtonLabel(node.children);
      const renderChildren = (children: readonly RealtimeNode[], prefix: string) => children.map((child, index) => <NodeRenderer key={`${prefix}-${index}`} node={child} currentPrompt={currentPrompt} currentButtonGeneration={currentButtonGeneration} assets={assets} onInput={onInput} onRenderError={onRenderError} />);
      const buttonStyle: CSSProperties = node.positionX === undefined ? {} : { marginLeft: `${node.positionX}px` };
      return <Fragment>
        {renderChildren(parts.leading, "leading")}
        <button className={`console-choice ${isCurrentFrame ? "" : "is-stale"}`} style={buttonStyle} type="button" disabled={!isCurrentFrame} title={node.tooltip ?? (!isCurrentFrame && node.enabled ? "上一帧选项已失效" : undefined)} onClick={() => onInput({ value: node.value, source: "BUTTON" })}>
          <span className="console-choice-label">{renderChildren(parts.label, "label")}</span>
        </button>
        {renderChildren(parts.trailing, "trailing")}
      </Fragment>;
    }
    case "image": {
      const destination = node.destination ?? node.sourceRect;
      if (node.sourceRect && destination) return <SpriteCanvas sprite={{ assetId: node.assetId, sourceRect: node.sourceRect, frame: 0, animationFrames: [], opacity: 1 }} assets={assets} alt={node.decorative ? "" : node.altText ?? "游戏图片"} className="console-image" width={destination.width} height={destination.height} onRenderError={onRenderError} />;
      return <AssetImage assetId={node.assetId} alt={node.decorative ? "" : node.altText ?? "游戏图片"} assets={assets} className="console-image" width={node.destination?.width} height={node.destination?.height} />;
    }
    case "sprite": return <SpriteCanvas sprite={node} assets={assets} alt={node.altText ?? "游戏精灵"} className="console-sprite" width={node.destination.width} height={node.destination.height} onRenderError={onRenderError} />;
    case "shape": return <ShapeSvg shape={node.shape} bounds={node.bounds} points={node.points} fill={node.fill} stroke={node.stroke} buttonColor={node.buttonColor} />;
    case "div": return <div className="console-emuera-div" style={divStyle(node.bounds, node.zIndex, node.background, node.isRelative, node.box)}>{node.children.map((child, index) => <NodeRenderer key={`div-${index}`} node={child} currentPrompt={currentPrompt} currentButtonGeneration={currentButtonGeneration} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}</div>;
    case "htmlIsland": return node.nodes
      ? node.nodes.map((child, index) => <NodeRenderer key={`island-${index}`} node={child} currentPrompt={currentPrompt} currentButtonGeneration={currentButtonGeneration} assets={assets} onInput={onInput} onRenderError={onRenderError} />)
      : <SafeHtmlRenderer node={node.root!} assets={assets} className="console-html-island" onRenderError={onRenderError} />;
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
    generation = Math.max(generation, latestNodeButtonGeneration(line.nodes));
  }
  return generation;
}

function latestNodeButtonGeneration(nodes: readonly RealtimeNode[]): number {
  let generation = 0;
  for (const node of nodes) {
    if (node.type === "button") generation = Math.max(generation, node.generation, latestNodeButtonGeneration(node.children));
    else if (node.type === "div" || node.type === "htmlIsland") {
      generation = Math.max(generation, latestNodeButtonGeneration(node.type === "div" ? node.children : node.nodes ?? []));
    }
  }
  return generation;
}

function AssetImage({ assetId, alt, assets, className, width, height }: { assetId: string; alt: string; assets: AssetResolver; className: string; width?: number; height?: number }) {
  const url = assets.url(assetId);
  if (!url) return <span className="console-missing-asset" role="img" aria-label={alt}>[资源不可用]</span>;
  return <img className={className} src={url} alt={alt} width={width} height={height} loading="lazy" decoding="async" />;
}

export function ShapeSvg({ shape, bounds, points, fill, stroke, buttonColor }: { shape: string; bounds: { x: number; y: number; width: number; height: number }; points: { x: number; y: number }[]; fill?: RealtimeColor | null; stroke?: RealtimeColor | null; buttonColor?: RealtimeColor | null }) {
  const fillValue = fill ? `rgba(${fill.red},${fill.green},${fill.blue},${fill.alpha / 255})` : "none";
  const strokeValue = stroke ? `rgba(${stroke.red},${stroke.green},${stroke.blue},${stroke.alpha / 255})` : "none";
  const shapeStyle = geometryStyle(bounds, buttonColor);
  const shapeClassName = `console-shape ${buttonColor ? "is-selectable" : ""}`;
  const localPoints = points.map(point => ({ x: point.x - bounds.x, y: point.y - bounds.y }));
  if (shape === "line") {
    const first = localPoints[0] ?? { x: 0, y: 0 };
    const second = localPoints[1] ?? { x: bounds.width, y: bounds.height };
    return <svg className={shapeClassName} style={shapeStyle} viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><line x1={first.x} y1={first.y} x2={second.x} y2={second.y} stroke={strokeValue === "none" ? fillValue : strokeValue} /></svg>;
  }
  if (shape === "ellipse") return <svg className={shapeClassName} style={shapeStyle} viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><ellipse cx={bounds.width / 2} cy={bounds.height / 2} rx={bounds.width / 2} ry={bounds.height / 2} fill={fillValue} stroke={strokeValue} /></svg>;
  if (shape === "polygon") return <svg className={shapeClassName} style={shapeStyle} viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><polygon points={localPoints.map(point => `${point.x},${point.y}`).join(" ")} fill={fillValue} stroke={strokeValue} /></svg>;
  return <svg className={shapeClassName} style={shapeStyle} viewBox={`0 0 ${Math.max(1, bounds.width)} ${Math.max(1, bounds.height)}`} role="img" aria-label="游戏图形"><rect width={bounds.width} height={bounds.height} fill={shape === "space" ? "none" : fillValue} stroke={strokeValue} /></svg>;
}

function divStyle(bounds: { x: number; y: number; width: number; height: number }, zIndex: number, background: RealtimeColor | null | undefined, isRelative: boolean, box: RealtimeBoxModel | null | undefined): CSSProperties {
  const style: CSSProperties = {
    position: isRelative ? "relative" : "absolute",
    left: bounds.x,
    top: isRelative ? bounds.y : undefined,
    bottom: isRelative ? undefined : bounds.y,
    width: bounds.width,
    height: bounds.height,
    zIndex,
    backgroundColor: colorToCss(background),
    boxSizing: "border-box",
    overflow: "hidden",
  };
  if (box) {
    style.margin = insetCss(box.margin);
    style.padding = insetCss(box.padding);
    style.borderTopWidth = box.border.top;
    style.borderRightWidth = box.border.right;
    style.borderBottomWidth = box.border.bottom;
    style.borderLeftWidth = box.border.left;
    style.borderStyle = box.border.top || box.border.right || box.border.bottom || box.border.left ? "solid" : undefined;
    style.borderTopColor = colorToCss(box.borderColors[0]);
    style.borderRightColor = colorToCss(box.borderColors[1]);
    style.borderBottomColor = colorToCss(box.borderColors[2]);
    style.borderLeftColor = colorToCss(box.borderColors[3]);
    style.borderRadius = insetCss(box.radius);
  }
  return style;
}

function geometryStyle(bounds: { x: number; y: number; width: number; height: number }, buttonColor: RealtimeColor | null | undefined): CSSProperties {
  const style: CSSProperties = { width: bounds.width, height: bounds.height, position: "relative", left: bounds.x, top: bounds.y };
  if (buttonColor) (style as CSSProperties & Record<string, string>)["--console-button-color"] = colorToCss(buttonColor) ?? "";
  return style;
}

function insetCss(insets: RealtimeInsets): string {
  return `${insets.top}px ${insets.right}px ${insets.bottom}px ${insets.left}px`;
}

function colorToCss(color: RealtimeColor | null | undefined): string | undefined {
  return color ? `rgba(${color.red},${color.green},${color.blue},${color.alpha / 255})` : undefined;
}
