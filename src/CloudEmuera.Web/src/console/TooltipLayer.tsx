import { createContext, useCallback, useContext, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type HTMLAttributes, type ReactNode, type RefCallback } from "react";
import { createPortal } from "react-dom";
import type { TooltipPresentation, TooltipResource } from "../realtime/protocol";

const TOUCH_HOLD_MS = 500;
const TOUCH_MOVE_PX = 10;
const TOUCH_BADGE_PX = 28;
const TOUCH_BADGE_MIN_WIDTH = 44;
const TOUCH_BADGE_MIN_HEIGHT = 24;
const ACTIVATION_TTL_MS = 1_000;

interface TooltipTargetRegistration {
  id: string;
  generation: number;
  tooltip: string;
  element: HTMLElement;
  inputTransparent: boolean;
}

interface OpenTooltip {
  target: TooltipTargetRegistration;
  pinned: boolean;
  pointer?: { x: number; y: number };
}

interface TouchCandidate {
  pointerId: number;
  targetId: string;
  generation: number;
  startX: number;
  startY: number;
  timer: ReturnType<typeof setTimeout>;
}

interface PendingActivation {
  targetId: string;
  generation: number;
  decision: "normal" | "inspect" | "suppress";
  expiresAt: number;
}

interface TooltipContextValue {
  register(target: TooltipTargetRegistration): () => void;
  openFromFocus(id: string, generation: number): void;
  closeFromFocus(id: string, generation: number): void;
  activeIdentity: string | null;
  tooltipId: string;
  inspectMode: boolean;
  toggleInspectMode(): void;
}

const TooltipContext = createContext<TooltipContextValue | null>(null);

export function ConsoleTooltipProvider({ presentation, resources, toolbar, children }: {
  presentation: TooltipPresentation;
  resources: TooltipResource[];
  toolbar?: ReactNode;
  children: ReactNode;
}) {
  const registry = useRef(new Map<string, TooltipTargetRegistration>());
  const hoverTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const hoverIntent = useRef<{ targetId: string; generation: number } | null>(null);
  const suppressedHover = useRef<{ targetId: string; generation: number } | null>(null);
  const durationTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const touchCandidate = useRef<TouchCandidate | null>(null);
  const touchPointers = useRef(new Set<number>());
  const pendingActivation = useRef<PendingActivation | null>(null);
  const [open, setOpen] = useState<OpenTooltip | null>(null);
  const [inspectMode, setInspectMode] = useState(false);
  const tooltipId = useId().replaceAll(":", "-");

  const clearHoverTimer = useCallback(() => {
    if (hoverTimer.current !== null) clearTimeout(hoverTimer.current);
    hoverTimer.current = null;
    hoverIntent.current = null;
  }, []);
  const clearDurationTimer = useCallback(() => {
    if (durationTimer.current !== null) clearTimeout(durationTimer.current);
    durationTimer.current = null;
  }, []);
  const close = useCallback(() => {
    clearHoverTimer();
    clearDurationTimer();
    setOpen(null);
  }, [clearDurationTimer, clearHoverTimer]);
  const toggleInspectMode = useCallback(() => {
    setInspectMode(value => !value);
    close();
  }, [close]);
  const show = useCallback((target: TooltipTargetRegistration, pinned: boolean, pointer?: { x: number; y: number }) => {
    clearHoverTimer();
    clearDurationTimer();
    setOpen({ target, pinned, pointer });
    if (!pinned && presentation.durationMilliseconds > 0) {
      durationTimer.current = setTimeout(() => setOpen(current => current?.target.id === target.id && current.target.generation === target.generation ? null : current), presentation.durationMilliseconds);
    }
  }, [clearDurationTimer, clearHoverTimer, presentation.durationMilliseconds]);

  const resolve = useCallback((clientX: number, clientY: number) => {
    const candidates: TooltipTargetRegistration[] = [];
    for (const target of registry.current.values()) {
      const rect = target.element.getBoundingClientRect();
      if (clientX < rect.left || clientX > rect.right || clientY < rect.top || clientY > rect.bottom) continue;
      candidates.push(target);
    }
    candidates.sort(compareTooltipPaintOrder);
    return candidates.at(-1) ?? null;
  }, []);

  const register = useCallback((target: TooltipTargetRegistration) => {
    registry.current.set(target.id, target);
    return () => {
      registry.current.delete(target.id);
      setOpen(current => current?.target.id === target.id && current.target.generation === target.generation ? null : current);
      if (touchCandidate.current?.targetId === target.id) {
        clearTimeout(touchCandidate.current.timer);
        touchCandidate.current = null;
      }
      if (pendingActivation.current?.targetId === target.id) pendingActivation.current = null;
    };
  }, []);

  useEffect(() => {
    close();
  }, [close, presentation.revision]);
  useEffect(() => () => {
    clearHoverTimer();
    clearDurationTimer();
    if (touchCandidate.current) clearTimeout(touchCandidate.current.timer);
  }, [clearDurationTimer, clearHoverTimer]);
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && open) {
        event.preventDefault();
        suppressedHover.current = { targetId: open.target.id, generation: open.target.generation };
        close();
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [close, open]);

  const context = useMemo<TooltipContextValue>(() => ({
    register,
    openFromFocus(id, generation) {
      const target = registry.current.get(id);
      if (target?.generation === generation) show(target, false);
    },
    closeFromFocus(id, generation) {
      setOpen(current => current?.target.id === id && current.target.generation === generation && !current.pinned ? null : current);
    },
    activeIdentity: open ? `${open.target.id}:${open.target.generation}` : null,
    tooltipId,
    inspectMode,
    toggleInspectMode,
  }), [inspectMode, open, register, show, toggleInspectMode, tooltipId]);

  const cancelTouchCandidate = useCallback(() => {
    if (touchCandidate.current) clearTimeout(touchCandidate.current.timer);
    touchCandidate.current = null;
  }, []);

  const onPointerMoveCapture = useCallback((event: React.PointerEvent<HTMLDivElement>) => {
    if (isTooltipProductUi(event.target)) {
      if (!open?.pinned) close();
      return;
    }
    if (event.pointerType === "touch") {
      const candidate = touchCandidate.current;
      if (candidate?.pointerId === event.pointerId && Math.hypot(event.clientX - candidate.startX, event.clientY - candidate.startY) > TOUCH_MOVE_PX)
        cancelTouchCandidate();
      return;
    }
    if (event.pointerType !== "mouse" && !(event.pointerType === "pen" && event.buttons === 0)) return;
    const target = resolve(event.clientX, event.clientY);
    if (!target) {
      suppressedHover.current = null;
      if (!open?.pinned) close();
      return;
    }
    if (suppressedHover.current &&
        (suppressedHover.current.targetId !== target.id || suppressedHover.current.generation !== target.generation))
      suppressedHover.current = null;
    if (suppressedHover.current?.targetId === target.id && suppressedHover.current.generation === target.generation)
      return;
    if (open?.target.id === target.id && open.target.generation === target.generation) return;
    if (hoverIntent.current?.targetId === target.id && hoverIntent.current.generation === target.generation) return;
    clearHoverTimer();
    hoverIntent.current = { targetId: target.id, generation: target.generation };
    hoverTimer.current = setTimeout(() => {
      hoverTimer.current = null;
      hoverIntent.current = null;
      const current = registry.current.get(target.id);
      if (current?.generation === target.generation) show(current, false, { x: event.clientX, y: event.clientY });
    }, presentation.delayMilliseconds);
  }, [cancelTouchCandidate, clearHoverTimer, close, open, presentation.delayMilliseconds, resolve, show]);

  const onPointerDownCapture = useCallback((event: React.PointerEvent<HTMLDivElement>) => {
    if (event.pointerType !== "touch") return;
    if (isTooltipProductUi(event.target)) return;
    touchPointers.current.add(event.pointerId);
    if (touchPointers.current.size > 1) { cancelTouchCandidate(); return; }
    const target = resolve(event.clientX, event.clientY);
    if (!target) return;
    cancelTouchCandidate();
    const timer = setTimeout(() => {
      const current = registry.current.get(target.id);
      if (!current || current.generation !== target.generation) return;
      show(current, true, { x: event.clientX, y: event.clientY });
      pendingActivation.current = { targetId: target.id, generation: target.generation, decision: "suppress", expiresAt: performance.now() + ACTIVATION_TTL_MS };
      touchCandidate.current = null;
    }, TOUCH_HOLD_MS);
    touchCandidate.current = { pointerId: event.pointerId, targetId: target.id, generation: target.generation, startX: event.clientX, startY: event.clientY, timer };
  }, [cancelTouchCandidate, resolve, show]);

  const onPointerUpCapture = useCallback((event: React.PointerEvent<HTMLDivElement>) => {
    if (event.pointerType !== "touch") return;
    touchPointers.current.delete(event.pointerId);
    if (isTooltipProductUi(event.target)) return;
    const candidate = touchCandidate.current;
    if (!candidate || candidate.pointerId !== event.pointerId) return;
    cancelTouchCandidate();
    const target = registry.current.get(candidate.targetId);
    if (!target || target.generation !== candidate.generation) return;
    const rect = target.element.getBoundingClientRect();
    const inBadge = rect.width >= TOUCH_BADGE_MIN_WIDTH && rect.height >= TOUCH_BADGE_MIN_HEIGHT &&
      event.clientX >= rect.right - TOUCH_BADGE_PX && event.clientY <= rect.top + TOUCH_BADGE_PX;
    if (inBadge || inspectMode || target.inputTransparent) {
      show(target, true, { x: event.clientX, y: event.clientY });
      pendingActivation.current = { targetId: target.id, generation: target.generation, decision: "inspect", expiresAt: performance.now() + ACTIVATION_TTL_MS };
    } else {
      pendingActivation.current = { targetId: target.id, generation: target.generation, decision: "normal", expiresAt: performance.now() + ACTIVATION_TTL_MS };
    }
  }, [cancelTouchCandidate, inspectMode, show]);

  const onPointerCancelCapture = useCallback((event: React.PointerEvent<HTMLDivElement>) => {
    touchPointers.current.delete(event.pointerId);
    cancelTouchCandidate();
  }, [cancelTouchCandidate]);

  const onClickCapture = useCallback((event: React.MouseEvent<HTMLDivElement>) => {
    if (event.detail === 0 || isTooltipProductUi(event.target)) return;
    const target = resolve(event.clientX, event.clientY);
    const pending = pendingActivation.current;
    if (!pending || performance.now() > pending.expiresAt) {
      pendingActivation.current = null;
      if (inspectMode && target) {
        show(target, true, { x: event.clientX, y: event.clientY });
        event.preventDefault();
        event.stopPropagation();
      }
      return;
    }
    pendingActivation.current = null;
    if (!target || target.id !== pending.targetId || target.generation !== pending.generation || pending.decision === "normal") return;
    event.preventDefault();
    event.stopPropagation();
  }, [inspectMode, resolve, show]);

  return <TooltipContext.Provider value={context}>
    <div className="console-tooltip-root" onPointerMoveCapture={onPointerMoveCapture} onPointerLeave={() => { suppressedHover.current = null; if (!open?.pinned) close(); }} onPointerDownCapture={onPointerDownCapture} onPointerUpCapture={onPointerUpCapture} onPointerCancelCapture={onPointerCancelCapture} onClickCapture={onClickCapture}>
      {toolbar}
      {children}
    </div>
    {open && <TooltipOverlay open={open} presentation={presentation} resources={resources} id={tooltipId} onClose={close} />}
  </TooltipContext.Provider>;
}

export function ConsoleTooltipToggle() {
  const context = useContext(TooltipContext);
  if (!context) return null;
  const label = context.inspectMode ? "关闭提示查看" : "开启提示查看";
  return <button className={`console-inspect-toggle ${context.inspectMode ? "is-on" : ""}`} data-tooltip-ui type="button" aria-pressed={context.inspectMode} aria-label={label} title={label} onClick={context.toggleInspectMode}><i aria-hidden="true"/><span>提示</span></button>;
}

export function useConsoleTooltipTarget(tooltip: string | null | undefined, generation: number, inputTransparent = false): {
  ref: RefCallback<HTMLElement>;
  props: HTMLAttributes<HTMLElement>;
  badge: ReactNode;
} {
  const context = useContext(TooltipContext);
  const reactId = useId();
  const id = `console-tooltip-target-${reactId.replaceAll(":", "-")}`;
  const elementRef = useRef<HTMLElement | null>(null);
  const normalized = tooltip?.length ? tooltip : null;
  const ref = useCallback<RefCallback<HTMLElement>>(element => { elementRef.current = element; }, []);
  const register = context?.register;
  useLayoutEffect(() => {
    if (!register || !normalized || !elementRef.current) return;
    return register({ id, generation, tooltip: normalized, element: elementRef.current, inputTransparent });
  }, [generation, id, inputTransparent, normalized, register]);
  const active = context?.activeIdentity === `${id}:${generation}`;
  return {
    ref,
    props: normalized ? {
      onFocus: () => context?.openFromFocus(id, generation),
      onBlur: () => context?.closeFromFocus(id, generation),
      "aria-describedby": active ? context?.tooltipId : undefined,
    } : {},
    badge: normalized ? <span className="console-tooltip-badge" aria-hidden="true">?</span> : null,
  };
}

function TooltipOverlay({ open, presentation, resources, id, onClose }: { open: OpenTooltip; presentation: TooltipPresentation; resources: TooltipResource[]; id: string; onClose(): void }) {
  const [position, setPosition] = useState<CSSProperties>({ left: 8, top: 8 });
  const overlayRef = useRef<HTMLDivElement>(null);
  const resource = presentation.imageMode && /^\d+$/.test(open.target.tooltip)
    ? resources.find(item => item.graphicsId === Number(open.target.tooltip))
    : undefined;
  const imageUrl = usePngObjectUrl(resource);
  useLayoutEffect(() => {
    const update = () => {
      const anchor = open.target.element.getBoundingClientRect();
      const overlay = overlayRef.current;
      const viewport = window.visualViewport;
      const leftEdge = viewport?.offsetLeft ?? 0;
      const topEdge = viewport?.offsetTop ?? 0;
      const width = viewport?.width ?? window.innerWidth;
      const height = viewport?.height ?? window.innerHeight;
      const desiredLeft = open.pointer?.x ?? anchor.left;
      const overlayWidth = overlay?.offsetWidth ?? Math.min(320, width - 16);
      const overlayHeight = overlay?.offsetHeight ?? Math.min(180, height - 16);
      const below = anchor.bottom + 8;
      const above = anchor.top - overlayHeight - 8;
      const desiredTop = below + overlayHeight <= topEdge + height - 8 || above < topEdge + 8 ? below : above;
      setPosition({
        left: clamp(desiredLeft, leftEdge + 8, Math.max(leftEdge + 8, leftEdge + width - overlayWidth - 8)),
        top: clamp(desiredTop, topEdge + 8, Math.max(topEdge + 8, topEdge + height - overlayHeight - 8)),
      });
    };
    update();
    const observer = typeof ResizeObserver === "undefined" || !overlayRef.current ? null : new ResizeObserver(update);
    if (overlayRef.current) observer?.observe(overlayRef.current);
    window.addEventListener("resize", update);
    window.addEventListener("scroll", update, true);
    window.visualViewport?.addEventListener("resize", update);
    window.visualViewport?.addEventListener("scroll", update);
    return () => {
      window.removeEventListener("resize", update);
      window.removeEventListener("scroll", update, true);
      window.visualViewport?.removeEventListener("resize", update);
      window.visualViewport?.removeEventListener("scroll", update);
      observer?.disconnect();
    };
  }, [open]);
  const format = presentation.textFormat;
  const style: CSSProperties = {
    ...position,
    ...(presentation.customEnabled ? { color: colorCss(presentation.foreground), backgroundColor: colorCss(presentation.background), fontSize: presentation.fontSize } : {}),
    textAlign: format.horizontal,
    whiteSpace: format.wrap ? "pre-wrap" : "pre",
    direction: format.rightToLeft ? "rtl" : "ltr",
    alignContent: format.vertical === "center" ? "center" : format.vertical === "bottom" ? "end" : "start",
    overflowWrap: format.wrap && format.trimming === "wordEllipsis" ? "break-word" : undefined,
    tabSize: format.expandTabs ? 8 : undefined,
    textOverflow: format.trimming !== "none" ? "ellipsis" : undefined,
    overflow: format.trimming !== "none" ? "hidden" : undefined,
  };
  const pathTrimming = format.trimming === "pathEllipsis";
  return createPortal(<div ref={overlayRef} id={id} className={`console-tooltip ${open.pinned ? "is-pinned" : ""}`} role="tooltip" style={style}>
    {imageUrl ? <img src={imageUrl} width={resource!.width} height={resource!.height} alt={`Graphics ${resource!.graphicsId}`} /> : <span className={pathTrimming ? "console-tooltip-path-ellipsis" : undefined}>{tooltipText(open.target.tooltip)}</span>}
    {open.pinned && <button data-tooltip-ui type="button" className="console-tooltip-close" aria-label="关闭提示" onClick={onClose}>×</button>}
  </div>, document.body);
}

function usePngObjectUrl(resource: TooltipResource | undefined) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!resource) { setUrl(null); return; }
    const binary = atob(resource.pngData);
    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
    const next = URL.createObjectURL(new Blob([bytes], { type: "image/png" }));
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [resource]);
  return url;
}

function tooltipText(value: string) { return value.replaceAll("<br>", "\n"); }
function colorCss(value: { red: number; green: number; blue: number; alpha: number }) { return `rgba(${value.red}, ${value.green}, ${value.blue}, ${value.alpha / 255})`; }
function clamp(value: number, minimum: number, maximum: number) { return Math.min(maximum, Math.max(minimum, value)); }

function isTooltipProductUi(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest("[data-tooltip-ui]") !== null;
}

function compareTooltipPaintOrder(left: TooltipTargetRegistration, right: TooltipTargetRegistration): number {
  if (left.element === right.element) return 0;
  if (left.element.contains(right.element)) return -1;
  if (right.element.contains(left.element)) return 1;
  const leftStack = stackingPath(left.element);
  const rightStack = stackingPath(right.element);
  for (let index = 0; index < Math.max(leftStack.length, rightStack.length); index++) {
    const difference = (leftStack[index] ?? 0) - (rightStack[index] ?? 0);
    if (difference !== 0) return difference;
  }
  const relation = left.element.compareDocumentPosition(right.element);
  if ((relation & Node.DOCUMENT_POSITION_FOLLOWING) !== 0) return -1;
  if ((relation & Node.DOCUMENT_POSITION_PRECEDING) !== 0) return 1;
  return left.id.localeCompare(right.id);
}

function stackingPath(element: HTMLElement): number[] {
  const result: number[] = [];
  for (let current: HTMLElement | null = element; current; current = current.parentElement) {
    const value = Number.parseInt(getComputedStyle(current).zIndex, 10);
    if (Number.isFinite(value)) result.unshift(value);
  }
  return result;
}
