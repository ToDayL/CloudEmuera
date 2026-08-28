import { Fragment, memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { AssetResolver } from "./AssetResolver";
import { SafeHtmlRenderer, textStyleToCss } from "./SafeHtmlRenderer";
import { inlineSpriteSlotStyle, inlineSpriteStyle, SpriteCanvas } from "./SpriteRenderer";
import type { RealtimeBoxModel, RealtimeColor, RealtimeInsets, RealtimeLine, RealtimeNode, RealtimeRect } from "../realtime/protocol";
import { useConsoleTooltipTarget } from "./TooltipLayer";

export interface ConsoleInputEvent {
  value: string;
  source: "KEYBOARD" | "BUTTON" | "POINTER";
  pointer?: { x: number; y: number; button: number; pressed: boolean };
  key?: { keyCode: number; control: boolean; alt: boolean; shift: boolean };
}

interface NodeRendererProps {
  node: RealtimeNode;
  assets: AssetResolver;
  onInput: (event: ConsoleInputEvent) => void;
  onRenderError?: (message: string) => void;
}

interface ConsoleLineViewProps extends Omit<NodeRendererProps, "node"> {
  line: RealtimeLine;
}

const DEFAULT_RUNTIME_LINE_HEIGHT = 16;
const VIRTUAL_OVERSCAN_SCREENS = 1;
const DEFAULT_VIRTUAL_VIEWPORT_HEIGHT = 640;
const BOTTOM_DISTANCE_EPSILON_PX = 1;

export interface ScrollbackRendererProps {
  lines: RealtimeLine[];
  assets: AssetResolver;
  onInput: (event: ConsoleInputEvent) => void;
  onRenderError?: (message: string) => void;
  scrollContainerRef?: RefObject<HTMLElement | null>;
  scrollVersion?: string | number;
  forceScrollVersion?: string | number;
  defaultLineHeight?: number;
}

export function ScrollbackRenderer({ lines, assets, onInput, onRenderError, scrollContainerRef, scrollVersion, forceScrollVersion, defaultLineHeight }: ScrollbackRendererProps) {
  const [atLatest, setAtLatest] = useState(true);
  const atLatestRef = useRef(true);
  const scrollbackShellRef = useRef<HTMLDivElement>(null);
  const virtualContentRef = useRef<HTMLDivElement>(null);
  const displayLines = useMemo(() => trimTrailingEmptyLines(lines), [lines]);
  const lineHeight = normalizeLineHeight(defaultLineHeight);
  const leadingOverflow = useMemo(() => leadingVisualOverflow(displayLines, lineHeight), [displayLines, lineHeight]);
  const trailingOverflow = useMemo(() => trailingVisualOverflow(displayLines, lineHeight), [displayLines, lineHeight]);
  const getLineFlowHeight = useCallback((index: number) => {
    const line = displayLines[index];
    return line ? effectiveLineFlowHeight(line, lineHeight) : lineHeight;
  }, [displayLines, lineHeight]);
  const getItemKey = useCallback((index: number) => displayLines[index]?.lineId ?? index, [displayLines]);
  const measureElement = useCallback((element: HTMLDivElement) => {
    const index = Number(element.dataset.index);
    return getLineFlowHeight(Number.isInteger(index) && index >= 0 ? index : -1);
  }, [getLineFlowHeight]);
  const virtualizer = useVirtualizer<HTMLElement, HTMLDivElement>({
    count: displayLines.length,
    getScrollElement: () => scrollContainerRef?.current ?? null,
    estimateSize: getLineFlowHeight,
    getItemKey,
    measureElement,
    // TanStack Virtual's overscan is expressed in items.  The runtime uses
    // mostly fixed physical line heights, so one nominal 640px viewport is a
    // stable approximation without making the window depend on DOM layout.
    overscan: Math.max(1, Math.ceil((DEFAULT_VIRTUAL_VIEWPORT_HEIGHT * VIRTUAL_OVERSCAN_SCREENS) / lineHeight)),
    paddingStart: leadingOverflow,
    paddingEnd: trailingOverflow,
    initialRect: { width: 0, height: DEFAULT_VIRTUAL_VIEWPORT_HEIGHT },
    // Keep scroll-only positions out of React's render path.  The row boxes
    // are fixed flow-height boxes; portraits paint outside them and therefore
    // must never participate in virtual size measurement.
    directDomUpdates: true,
    directDomUpdatesMode: "position",
  });
  const virtualItems = virtualizer.getVirtualItems();
  const setVirtualContentRef = useCallback((node: HTMLDivElement | null) => {
    virtualContentRef.current = node;
    virtualizer.containerRef(node);
  }, [virtualizer]);
  const scrollToBottom = useCallback((behavior: ScrollBehavior) => {
    const container = scrollContainerRef?.current;
    if (!container) return;
    atLatestRef.current = true;
    setAtLatest(true);
    scrollContainerToBottom(container, behavior);
  }, [scrollContainerRef]);
  const settleScrollToBottom = useCallback((force: boolean) => {
    scrollToBottom("auto");
    if (typeof requestAnimationFrame !== "function") return;

    let frame: number | null = null;
    let remainingFrames = 2;
    const settle = () => {
      frame = null;
      if (!force && !atLatestRef.current) return;

      scrollToBottom("auto");
      remainingFrames -= 1;
      if (remainingFrames > 0) {
        frame = requestAnimationFrame(settle);
      }
    };

    frame = requestAnimationFrame(settle);
    return () => {
      if (frame !== null && typeof cancelAnimationFrame === "function") {
        cancelAnimationFrame(frame);
      }
    };
  }, [scrollToBottom]);
  useEffect(() => {
    const container = scrollContainerRef?.current;
    if (!container) return;
    const updatePosition = () => {
      const next = isScrollAtBottom(container);
      atLatestRef.current = next;
      setAtLatest(previous => previous === next ? previous : next);
    };
    updatePosition();
    container.addEventListener("scroll", updatePosition, { passive: true });
    return () => {
      container.removeEventListener("scroll", updatePosition);
    };
  }, [scrollContainerRef]);
  useLayoutEffect(() => {
    if (forceScrollVersion === undefined) return;
    // Input is an explicit navigation action. It must win even when the
    // reader was looking at older output, and it must update the ref used by
    // the following display frame so newly appended output stays visible.
    return settleScrollToBottom(true);
  }, [forceScrollVersion, settleScrollToBottom]);
  useLayoutEffect(() => {
    // A display frame follows the reader only when the reader was already at
    // the latest position. Do not pull a user back while reading old output.
    if (!atLatestRef.current) return;
    return settleScrollToBottom(false);
  }, [lines, scrollVersion, settleScrollToBottom]);
  useEffect(() => {
    const shell = scrollbackShellRef.current;
    const virtualContent = virtualContentRef.current;
    if ((!shell && !virtualContent) || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => {
      // A virtualized row can change the scroll extent after both React's
      // commit and the settling animation frames. Re-apply the bottom only
      // while the reader is following the latest output.
      if (atLatestRef.current) settleScrollToBottom(false);
    });
    if (shell) observer.observe(shell);
    if (virtualContent && virtualContent !== shell) observer.observe(virtualContent);
    return () => observer.disconnect();
  }, [settleScrollToBottom]);
  const scrollToLatest = useCallback(() => {
    const container = scrollContainerRef?.current;
    if (!container) return;

    // An immediate jump avoids a long smooth-scroll paint competing with a
    // large bounded history. The control remains mounted while hidden, so
    // toggling it cannot change the scroll extent.
    scrollContainerToBottom(container, "auto");
    atLatestRef.current = true;
    setAtLatest(true);
  }, [scrollContainerRef]);
  return <div ref={scrollbackShellRef} className="scrollback-shell">
    <div className="scrollback" aria-live="polite">
      <div ref={setVirtualContentRef} className="console-virtual-content">
        {virtualItems.map(virtualItem => {
          const line = displayLines[virtualItem.index];
          if (!line) return null;
          return <div
            key={virtualItem.key}
            ref={virtualizer.measureElement}
            data-index={virtualItem.index}
            className="console-virtual-row"
            style={{ width: "max-content", minWidth: "100%", height: virtualItem.size, pointerEvents: "none" }}
          >
            <ConsoleLineView line={line} assets={assets} onInput={onInput} onRenderError={onRenderError} />
          </div>;
        })}
      </div>
    </div>
    <button className={`scrollback-latest ${atLatest ? "is-hidden" : ""}`} type="button" aria-hidden={atLatest} tabIndex={atLatest ? -1 : 0} onClick={scrollToLatest}>↓ 回到最新</button>
  </div>;
}

function scrollContainerToBottom(container: HTMLElement, behavior: ScrollBehavior): void {
  const top = Math.max(0, container.scrollHeight - container.clientHeight);
  // Assigning scrollTop first makes the jump deterministic on browsers where
  // scrollTo can be deferred until after the current layout.
  if (behavior === "auto") container.scrollTop = top;
  if (typeof container.scrollTo === "function") container.scrollTo({ top, behavior });
  else container.scrollTop = top;
}

export function isScrollAtBottom(
  container: Pick<HTMLElement, "scrollHeight" | "clientHeight" | "scrollTop">,
): boolean {
  const distance = container.scrollHeight - container.clientHeight - container.scrollTop;
  return distance <= BOTTOM_DISTANCE_EPSILON_PX;
}

function normalizeLineHeight(value: number | undefined): number {
  return value && Number.isFinite(value) && value > 0 ? value : DEFAULT_RUNTIME_LINE_HEIGHT;
}

export function trimTrailingEmptyLines(lines: readonly RealtimeLine[]): readonly RealtimeLine[] {
  let end = lines.length;
  if (end > 0 && isEmptyLine(lines[end - 1])) end--;
  return end === lines.length ? lines : lines.slice(0, end);
}

/**
 * Inline image parts can paint above the physical line that owns them. Keep
 * that leading portion inside the scrollback's flow so the first title image
 * is not clipped by the scrollback's overflow boundary.
 */
export function leadingVisualOverflow(lines: readonly RealtimeLine[], defaultLineHeight = DEFAULT_RUNTIME_LINE_HEIGHT): number {
  let flowHeight = 0;
  let visualTop = 0;
  for (const line of lines) {
    const lineHeight = effectiveLineFlowHeight(line, defaultLineHeight);
    visualTop = Math.min(visualTop, flowHeight + nodeVisualTop(line.nodes));
    flowHeight += lineHeight;
  }
  return Math.max(0, Math.ceil(-visualTop));
}

function isEmptyLine(line: RealtimeLine): boolean {
  return line.nodes.length === 0 || line.nodes.every(node => node.type === "lineBreak" || (node.type === "text" && node.text.length === 0));
}

function trimTrailingLineBreaks(nodes: readonly RealtimeNode[]): readonly RealtimeNode[] {
  let end = nodes.length;
  while (end > 0 && nodes[end - 1].type === "lineBreak") end--;
  return end === nodes.length ? nodes : nodes.slice(0, end);
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

const ConsoleLineView = memo(function ConsoleLineView({ line, assets, onInput, onRenderError }: ConsoleLineViewProps) {
  return <div className={`console-line align-${line.alignment} ${line.temporary ? "is-temporary" : ""} ${line.noWrap ? "is-nowrap" : ""}`} style={physicalLineStyle(line)}>
    {trimTrailingLineBreaks(line.nodes).map((node, index) => <NodeRenderer key={`${line.lineId}-${index}`} node={node} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}
  </div>;
});

export const NodeRenderer = memo(function NodeRendererImpl({ node, assets, onInput, onRenderError }: NodeRendererProps): ReactNode {
  switch (node.type) {
    case "text": return <span className={node.style.buttonColor ? "console-text has-button-color" : "console-text"} style={textStyleToCss(node.style, assets)}>{renderRuntimeText(node.text)}</span>;
    case "lineBreak": return <br />;
    case "button": {
      if (!node.enabled && node.value.length === 0) {
        return <TooltipNonButton node={node} style={positionStyle(node.positionX)} assets={assets} onInput={onInput} onRenderError={onRenderError} />;
      }
      const parts = splitButtonLabel(node.children);
      const renderChildren = (children: readonly RealtimeNode[], prefix: string) => children.map((child, index) => <NodeRenderer key={`${prefix}-${index}`} node={child} assets={assets} onInput={onInput} onRenderError={onRenderError} />);
      const buttonStyle: CSSProperties = interactivePositionStyle(node.positionX);
      return <Fragment>
        {renderChildren(parts.leading, "leading")}
        <TooltipButton node={node} style={buttonStyle} onInput={onInput}><span className="console-choice-label">{renderChildren(parts.label, "label")}</span></TooltipButton>
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
      // The translator represents a positioned <nonbutton> with the same
      // empty, disabled action sentinel used by a top-level ButtonNode. It
      // must remain a span here too: wrapping it in a disabled button applies
      // the global button:disabled opacity and changes the appearance of
      // every later portrait layer in a composite.
      if (!node.action || (!node.action.enabled && node.action.value.length === 0)) {
        return node.action ? <TooltipPositionedNonButton action={node.action} style={style}>{children}</TooltipPositionedNonButton> : <span className="positioned-inline-segment" style={style}>{children}</span>;
      }
      return <TooltipPositionedButton action={node.action} style={style} onInput={onInput}>{children}</TooltipPositionedButton>;
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
});

function TooltipButton({ node, style, onInput, children }: { node: Extract<RealtimeNode, { type: "button" }>; style: CSSProperties; onInput: (event: ConsoleInputEvent) => void; children: ReactNode }) {
  const target = useConsoleTooltipTarget(node.tooltip, node.generation);
  return <button ref={target.ref as React.RefCallback<HTMLButtonElement>} {...target.props} className="console-choice console-tooltip-target" style={{ ...style, pointerEvents: "auto" }} type="button" disabled={!node.enabled} onClick={() => onInput({ value: node.value, source: "BUTTON" })}>{children}{target.badge}</button>;
}

function TooltipNonButton({ node, style, assets, onInput, onRenderError }: { node: Extract<RealtimeNode, { type: "button" }>; style: CSSProperties; assets: AssetResolver; onInput: (event: ConsoleInputEvent) => void; onRenderError?: (message: string) => void }) {
  const target = useConsoleTooltipTarget(node.tooltip, node.generation, true);
  return <span ref={target.ref as React.RefCallback<HTMLSpanElement>} className="console-nonbutton console-tooltip-target" style={style}>{node.children.map((child, index) => <NodeRenderer key={`nonbutton-${index}`} node={child} assets={assets} onInput={onInput} onRenderError={onRenderError} />)}{target.badge}</span>;
}

function TooltipPositionedButton({ action, style, onInput, children }: { action: NonNullable<Extract<RealtimeNode, { type: "positionedInlineSegment" }>["action"]>; style: CSSProperties; onInput: (event: ConsoleInputEvent) => void; children: ReactNode }) {
  const target = useConsoleTooltipTarget(action.tooltip, action.generation);
  return <button ref={target.ref as React.RefCallback<HTMLButtonElement>} {...target.props} className="console-choice positioned-inline-action console-tooltip-target" style={{ ...style, pointerEvents: "auto" }} type="button" disabled={!action.enabled} onClick={() => onInput({ value: action.value, source: "BUTTON" })}>{children}{target.badge}</button>;
}

function TooltipPositionedNonButton({ action, style, children }: { action: NonNullable<Extract<RealtimeNode, { type: "positionedInlineSegment" }>["action"]>; style: CSSProperties; children: ReactNode }) {
  const target = useConsoleTooltipTarget(action.tooltip, action.generation, true);
  return <span ref={target.ref as React.RefCallback<HTMLSpanElement>} className="console-nonbutton positioned-inline-segment console-tooltip-target" style={style}>{children}{target.badge}</span>;
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
  const style: CSSProperties = { pointerEvents: "none" };
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
export function trailingVisualOverflow(lines: readonly RealtimeLine[], defaultLineHeight = DEFAULT_RUNTIME_LINE_HEIGHT): number {
  let flowHeight = 0;
  let visualBottom = 0;
  for (const line of lines) {
    const lineHeight = effectiveLineFlowHeight(line, defaultLineHeight);
    visualBottom = Math.max(visualBottom, flowHeight + nodeVisualBottom(line.nodes));
    flowHeight += lineHeight;
  }
  return Math.max(0, Math.ceil(visualBottom - flowHeight));
}

function effectiveLineFlowHeight(line: RealtimeLine, defaultLineHeight: number): number {
  if (line.lineHeight && line.lineHeight > 0) return line.lineHeight;
  return Math.max(defaultLineHeight, ...line.nodes.map(nodeTextLineHeight));
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
