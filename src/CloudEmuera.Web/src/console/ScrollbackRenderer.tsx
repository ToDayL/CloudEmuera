import { Fragment, useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import type { AssetResolver } from "./AssetResolver";
import { SafeHtmlRenderer, textStyleToCss } from "./SafeHtmlRenderer";
import { inlineSpriteSlotStyle, inlineSpriteStyle, SpriteCanvas } from "./SpriteRenderer";
import type { RealtimeBoxModel, RealtimeColor, RealtimeInsets, RealtimeLine, RealtimeNode, RealtimeRect } from "../realtime/protocol";

export interface ConsoleInputEvent {
  value: string;
  source: "KEYBOARD" | "BUTTON" | "POINTER";
  pointer?: { x: number; y: number; button: number; pressed: boolean };
  key?: { keyCode: number; control: boolean; alt: boolean; shift: boolean };
}

export function ScrollbackRenderer({ lines, assets, onInput, onRenderError, scrollContainerRef, scrollVersion, forceScrollVersion }: { lines: RealtimeLine[]; assets: AssetResolver; onInput: (event: ConsoleInputEvent) => void; onRenderError?: (message: string) => void; scrollContainerRef?: RefObject<HTMLElement | null>; scrollVersion?: string | number; forceScrollVersion?: string | number }) {
  const [atLatest, setAtLatest] = useState(true);
  const atLatestRef = useRef(true);
  const scrollbackShellRef = useRef<HTMLDivElement>(null);
  const displayLines = trimTrailingEmptyLines(lines);
  const leadingOverflow = leadingVisualOverflow(displayLines);
  const visualOverflow = trailingVisualOverflow(displayLines);
  const scrollToBottom = useCallback((behavior: ScrollBehavior) => {
    const container = scrollContainerRef?.current;
    if (!container) return;
    const top = Math.max(0, container.scrollHeight - container.clientHeight);
    atLatestRef.current = true;
    setAtLatest(true);
    // Assigning scrollTop first makes the initial connection deterministic on
    // browsers where an element scrollTo call can be deferred until after the
    // first layout. Keep scrollTo for smooth user-invoked navigation.
    if (behavior === "auto") container.scrollTop = top;
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
  useLayoutEffect(() => {
    if (forceScrollVersion === undefined) return;
    // Input is an explicit navigation action. It must win even when the
    // reader was looking at older output, and it must update the ref used by
    // the following display frame so newly appended output stays visible.
    scrollToBottom("auto");
    if (typeof requestAnimationFrame !== "function") return;
    const frame = requestAnimationFrame(() => scrollToBottom("auto"));
    return () => cancelAnimationFrame(frame);
  }, [forceScrollVersion, scrollToBottom]);
  useLayoutEffect(() => {
    // A display frame follows the reader only when the reader was already at
    // the latest position. Do not pull a user back while reading old output.
    if (!atLatestRef.current) return;
    scrollToBottom("auto");
    // The connection snapshot can complete one more layout after React's
    // layout effects (for example when a late image measurement changes the
    // scroll height). Re-apply the initial position on the next frame.
    if (typeof requestAnimationFrame !== "function") return;
    const frame = requestAnimationFrame(() => scrollToBottom("auto"));
    return () => cancelAnimationFrame(frame);
  }, [lines, scrollToBottom, scrollVersion]);
  useEffect(() => {
    const shell = scrollbackShellRef.current;
    if (!shell || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => {
      if (atLatestRef.current) scrollToBottom("auto");
    });
    observer.observe(shell);
    return () => observer.disconnect();
  }, [scrollToBottom]);
  const scrollToLatest = useCallback(() => scrollToBottom("smooth"), [scrollToBottom]);
  return <div ref={scrollbackShellRef} className="scrollback-shell">
    <div className="scrollback" aria-live="polite">
      {leadingOverflow > 0 && <div className="console-overflow-leading" style={{ height: leadingOverflow }} aria-hidden="true" />}
      {displayLines.map(line => <div className={`console-line align-${line.alignment} ${line.temporary ? "is-temporary" : ""} ${line.noWrap ? "is-nowrap" : ""}`} style={physicalLineStyle(line)} key={line.lineId}>
        {trimTrailingLineBreaks(line.nodes).map((node, index) => <NodeRenderer key={`${line.lineId}-${index}`} node={node} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}
      </div>)}
      {visualOverflow > 0 && <div className="console-overflow-reserve" style={{ height: visualOverflow }} aria-hidden="true" />}
    </div>
    {!atLatest && <button className="scrollback-latest" type="button" onClick={scrollToLatest}>↓ 回到最新</button>}
  </div>;
}

export function trimTrailingEmptyLines(lines: readonly RealtimeLine[]): RealtimeLine[] {
  let end = lines.length;
  if (end > 0 && isEmptyLine(lines[end - 1])) end--;
  return lines.slice(0, end);
}

/**
 * Inline image parts can paint above the physical line that owns them. Keep
 * that leading portion inside the scrollback's flow so the first title image
 * is not clipped by the scrollback's overflow boundary.
 */
export function leadingVisualOverflow(lines: readonly RealtimeLine[]): number {
  let flowHeight = 0;
  let visualTop = 0;
  for (const line of lines) {
    const lineHeight = effectiveLineFlowHeight(line);
    visualTop = Math.min(visualTop, flowHeight + nodeVisualTop(line.nodes));
    flowHeight += lineHeight;
  }
  return Math.max(0, Math.ceil(-visualTop));
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

export function NodeRenderer({ node, assets, onInput, onRenderError }: { node: RealtimeNode; assets: AssetResolver; onInput: (event: ConsoleInputEvent) => void; onRenderError?: (message: string) => void }): ReactNode {
  switch (node.type) {
    case "text": return <span className={node.style.buttonColor ? "console-text has-button-color" : "console-text"} style={textStyleToCss(node.style, assets)}>{renderRuntimeText(node.text)}</span>;
    case "lineBreak": return <br />;
    case "button": {
      if (!node.enabled && node.value.length === 0) {
        return <span className="console-nonbutton" style={positionStyle(node.positionX)} title={node.tooltip ?? undefined}>{node.children.map((child, index) => <NodeRenderer key={`nonbutton-${index}`} node={child} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}</span>;
      }
      const parts = splitButtonLabel(node.children);
      const renderChildren = (children: readonly RealtimeNode[], prefix: string) => children.map((child, index) => <NodeRenderer key={`${prefix}-${index}`} node={child} assets={assets} onInput={onInput} onRenderError={onRenderError} />);
      const buttonStyle: CSSProperties = interactivePositionStyle(node.positionX);
      return <Fragment>
        {renderChildren(parts.leading, "leading")}
        <button className="console-choice" style={buttonStyle} type="button" disabled={!node.enabled} title={node.tooltip ?? undefined} onClick={() => onInput({ value: node.value, source: "BUTTON" })}>
          <span className="console-choice-label">{renderChildren(parts.label, "label")}</span>
        </button>
        {renderChildren(parts.trailing, "trailing")}
      </Fragment>;
    }
    case "positionedInlineSegment": {
      const children = node.children.map((child, index) => <NodeRenderer key={`positioned-${index}`} node={child} assets={assets} onInput={onInput} onRenderError={onRenderError} />);
      const hasEscapedVisual = node.children.some(containsEscapedVisual);
      const style: CSSProperties = {
        position: "absolute",
        left: node.positionX,
        top: 0,
        width: node.measuredWidth,
        height: "100%",
        boxSizing: "border-box",
        whiteSpace: "pre",
        ...(hasEscapedVisual ? { overflow: "visible" } : {}),
      };
      if (!node.action) return <span className="positioned-inline-segment" style={style}>{children}</span>;
      return <button className="console-choice positioned-inline-action" style={style} type="button" disabled={!node.action.enabled} title={node.action.tooltip ?? undefined} onClick={() => onInput({ value: node.action!.value, source: "BUTTON" })}>{children}</button>;
    }
    case "image": {
      const destination = node.destination ?? node.sourceRect;
      if (node.sourceRect && destination) return <span className="console-sprite-slot" style={inlineSpriteSlotStyle(destination)}><SpriteCanvas sprite={{ assetId: node.assetId, sourceRect: node.sourceRect, frame: 0, animationFrames: [], opacity: 1 }} assets={assets} alt={node.decorative ? "" : node.altText ?? "游戏图片"} className="console-image" width={destination.width} height={destination.height} style={inlineSpriteStyle(destination)} onRenderError={onRenderError} /></span>;
      return <AssetImage assetId={node.assetId} alt={node.decorative ? "" : node.altText ?? "游戏图片"} assets={assets} className="console-image" width={node.destination?.width} height={node.destination?.height} />;
    }
    case "sprite": return <span className="console-sprite-slot" style={inlineSpriteSlotStyle(node.destination)}><SpriteCanvas sprite={node} assets={assets} alt={node.altText ?? "游戏精灵"} className="console-sprite" width={node.destination.width} height={node.destination.height} style={inlineSpriteStyle(node.destination)} onRenderError={onRenderError} /></span>;
    case "shape": return <ShapeSvg shape={node.shape} bounds={node.bounds} points={node.points} fill={node.fill} stroke={node.stroke} buttonColor={node.buttonColor} />;
    case "div": return <div className="console-emuera-div" style={divStyle(node.bounds, node.zIndex, node.background, node.isRelative, node.box)}>{node.children.map((child, index) => <NodeRenderer key={`div-${index}`} node={child} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}</div>;
    case "htmlIsland": return node.nodes
      ? node.nodes.map((child, index) => <NodeRenderer key={`island-${index}`} node={child} assets={assets} onInput={onInput} onRenderError={onRenderError} />)
      : <SafeHtmlRenderer node={node.root!} assets={assets} className="console-html-island" onRenderError={onRenderError} />;
  }
}

const eraWideCell = /[―∥\u2500-\u257F■□○●★☆]/u;
const eraWideShape = /[■□○●★☆]/u;

function renderRuntimeText(value: string): ReactNode {
  if (![...value].some(character => eraWideCell.test(character))) return value;
  return [...value].map((character, index) => eraWideCell.test(character)
    ? <span className="console-era-wide-cell" key={`era-wide-${index}`}><span className={eraWideShape.test(character) ? "console-era-wide-shape" : "console-era-wide-line"}>{character}</span></span>
    : character);
}

function physicalLineStyle(line: RealtimeLine): CSSProperties {
  const style: CSSProperties = {};
  if (line.layoutWidth && line.layoutWidth > 0) style.width = line.layoutWidth;
  if (line.lineHeight && line.lineHeight > 0) {
    style.height = line.lineHeight;
    style.minHeight = line.lineHeight;
    // React treats numeric lineHeight values as unitless multipliers.  The
    // protocol value is an authoritative physical pixel height, so it must
    // stay a CSS length or an 18px font with a 19px line would become 342px.
    style.lineHeight = `${line.lineHeight}px`;
  }
  return style;
}

/**
 * Inline image parts keep the upstream fixed line height and paint outside
 * that line like ConsoleImagePart. The browser scroll container still needs
 * ordinary flow height after the last line, otherwise overflow:hidden on the
 * scrollback clips the lower part of a portrait (or the whole portrait).
 */
export function trailingVisualOverflow(lines: readonly RealtimeLine[]): number {
  let flowHeight = 0;
  let visualBottom = 0;
  for (const line of lines) {
    const lineHeight = effectiveLineFlowHeight(line);
    visualBottom = Math.max(visualBottom, flowHeight + nodeVisualBottom(line.nodes));
    flowHeight += lineHeight;
  }
  return Math.max(0, Math.ceil(visualBottom - flowHeight));
}

function effectiveLineFlowHeight(line: RealtimeLine): number {
  if (line.lineHeight && line.lineHeight > 0) return line.lineHeight;
  return Math.max(0, ...line.nodes.map(nodeTextLineHeight));
}

function nodeTextLineHeight(node: RealtimeNode): number {
  switch (node.type) {
    case "text": return node.style.lineHeight > 0 ? node.style.lineHeight : 0;
    case "button":
    case "positionedInlineSegment":
    case "div": return Math.max(0, ...node.children.map(nodeTextLineHeight));
    case "htmlIsland": return node.nodes ? Math.max(0, ...node.nodes.map(nodeTextLineHeight)) : 0;
    default: return 0;
  }
}

function nodeVisualBottom(nodes: readonly RealtimeNode[]): number {
  return Math.max(0, ...nodes.map(node => {
    switch (node.type) {
      case "image": return rectBottom(node.destination ?? node.sourceRect);
      case "sprite": return rectBottom(node.destination);
      case "shape": return rectBottom(node.bounds);
      case "div": return Math.max(rectBottom(node.bounds), nodeVisualBottom(node.children));
      case "button":
      case "positionedInlineSegment": return nodeVisualBottom(node.children);
      case "htmlIsland": return node.layout ? rectBottom(node.layout) : node.nodes ? nodeVisualBottom(node.nodes) : 0;
      default: return 0;
    }
  }));
}

function nodeVisualTop(nodes: readonly RealtimeNode[]): number {
  return Math.min(0, ...nodes.map(node => {
    switch (node.type) {
      case "image": return rectTop(node.destination ?? node.sourceRect);
      case "sprite": return rectTop(node.destination);
      case "shape": return rectTop(node.bounds);
      case "div": return Math.min(rectTop(node.bounds), nodeVisualTop(node.children));
      case "button":
      case "positionedInlineSegment": return nodeVisualTop(node.children);
      case "htmlIsland": return node.layout ? rectTop(node.layout) : node.nodes ? nodeVisualTop(node.nodes) : 0;
      default: return 0;
    }
  }));
}

function rectTop(rect: RealtimeRect | null | undefined): number {
  return rect?.y ?? 0;
}

function rectBottom(rect: RealtimeRect | null | undefined): number {
  return rect ? Math.max(0, rect.y + rect.height) : 0;
}

function containsEscapedVisual(node: RealtimeNode): boolean {
  switch (node.type) {
    case "image":
    case "sprite": return true;
    case "button":
    case "positionedInlineSegment":
    case "div": return node.children.some(containsEscapedVisual);
    case "htmlIsland": return node.nodes?.some(containsEscapedVisual) ?? false;
    default: return false;
  }
}

function positionStyle(positionX: number | null | undefined): CSSProperties {
  return positionX === null || positionX === undefined
    ? {}
    : { position: "absolute", left: `${positionX}px`, top: 0 };
}

function interactivePositionStyle(positionX: number | null | undefined): CSSProperties {
  return positionX === null || positionX === undefined
    ? {}
    : { position: "relative", left: `${positionX}px` };
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
  const shapeClassName = `console-shape ${buttonColor ? "is-selectable" : ""} ${shape === "space" ? "is-space" : ""}`;
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
