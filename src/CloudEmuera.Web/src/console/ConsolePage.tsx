import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { ApiError } from "../api";
import { closeSession, useRuntimeFontCatalog, useSession, waitForSession, type SessionState } from "../sessions/api";
import { AssetResolver } from "./AssetResolver";
import { CanvasRenderer } from "./CanvasRenderer";
import { MediaController } from "./media";
import { PromptController, type PromptControllerHandle } from "./PromptController";
import { colorToCss } from "./SafeHtmlRenderer";
import { ScrollbackRenderer, type ConsoleInputEvent } from "./ScrollbackRenderer";
import { getRealtimeConnectionManager, type ConnectionPhase } from "../realtime/connection";
import type { RealtimeColor } from "../realtime/protocol";
import { createSessionStoreState, type SessionStoreState } from "../realtime/sessionStore";
import { loadRuntimeFont, runtimeFontCssFamily } from "./RuntimeFontLoader";
import { ConsoleTooltipProvider } from "./TooltipLayer";

function connectionLabel(phase: ConnectionPhase): string {
  return ({ disconnected: "未连接", connecting: "连接中", hello_pending: "校验中", ready: "实时连接", backing_off: "正在重连", auth_required: "需要重新登录", incompatible: "版本不兼容", disposed: "已结束" } as Record<ConnectionPhase, string>)[phase];
}

function sessionStateLabel(state: SessionState): string {
  return ({ CREATING: "创建中", STARTING: "启动中", RUNNING: "运行中", STOPPING: "停止中", CLOSED: "已关闭", CRASHED: "已崩溃" } as Record<SessionState, string>)[state];
}

export function ConsolePage() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const manager = useMemo(() => getRealtimeConnectionManager(), []);
  const [connectionPhase, setConnectionPhase] = useState<ConnectionPhase>(manager.status);
  const [networkOnline, setNetworkOnline] = useState(manager.isNetworkOnline);
  const [stream, setStream] = useState<SessionStoreState>(() => sessionId ? createSessionStoreState(sessionId) : createSessionStoreState("missing"));
  const [closing, setClosing] = useState(false);
  const [closeError, setCloseError] = useState<string | null>(null);
  const [rendererError, setRendererError] = useState<string | null>(null);
  const [soundEnabled, setSoundEnabled] = useState(false);
  const [inputScrollVersion, setInputScrollVersion] = useState(0);
  const [visualViewport, setVisualViewport] = useState(() => currentVisualViewport());
  const session = useSession(sessionId);
  const runtimeFonts = useRuntimeFontCatalog();
  const gameConsoleRef = useRef<HTMLElement>(null);
  const promptControllerRef = useRef<PromptControllerHandle>(null);
  const endedSessionRef = useRef<string | null>(null);
  useEffect(() => {
    const update = () => {
      setVisualViewport(currentVisualViewport());
    };
    window.addEventListener("resize", update);
    window.visualViewport?.addEventListener("resize", update);
    window.visualViewport?.addEventListener("scroll", update);
    return () => {
      window.removeEventListener("resize", update);
      window.visualViewport?.removeEventListener("resize", update);
      window.visualViewport?.removeEventListener("scroll", update);
    };
  }, []);
  const runtimeFont = useMemo(() => session.data && runtimeFonts.data?.items.find(font => font.faceId === session.data!.fontFaceId), [runtimeFonts.data, session.data]);
  const runtimeCssFamily = useMemo(() => runtimeFont ? runtimeFontCssFamily(runtimeFont) : undefined, [runtimeFont]);
  const [runtimeFontReady, setRuntimeFontReady] = useState(false);
  const [runtimeFontFailed, setRuntimeFontFailed] = useState(false);
  const assets = useMemo(() => new AssetResolver(sessionId ?? "missing", runtimeCssFamily), [runtimeCssFamily, sessionId]);
  const media = useRef<MediaController | null>(null);

  if (!media.current) media.current = new MediaController(message => setRendererError(message));

  useEffect(() => {
    if (!session.data) return;
    document.documentElement.classList.add("console-route");
    return () => document.documentElement.classList.remove("console-route");
  }, [session.data]);
  useEffect(() => manager.onStatus((phase, detail) => { setConnectionPhase(phase); if (detail && phase === "incompatible") setCloseError(detail); }), [manager]);
  useEffect(() => manager.onNetworkStatus(setNetworkOnline), [manager]);
  useEffect(() => {
    if (!sessionId) return;
    endedSessionRef.current = null;
    setStream(createSessionStoreState(sessionId));
    return manager.subscribe(sessionId, next => {
      setStream(next);
      if (next.phase === "ended" && endedSessionRef.current !== sessionId) {
        endedSessionRef.current = sessionId;
        media.current?.dispose();
        setSoundEnabled(false);
        void queryClient.invalidateQueries({ queryKey: ["session", sessionId] });
      }
    });
  }, [manager, queryClient, sessionId]);
  useEffect(() => {
    if (stream.workerEpoch !== null) media.current?.reset();
  }, [stream.workerEpoch]);
  useEffect(() => {
    if (!session.data) return;
    if (typeof FontFace === "undefined" || !document.fonts) {
      setRuntimeFontReady(false);
      setRuntimeFontFailed(true);
      return;
    }
    if (!runtimeFont || !runtimeCssFamily) {
      setRuntimeFontReady(runtimeFonts.isError);
      setRuntimeFontFailed(runtimeFonts.isError);
      return;
    }
    let cancelled = false;
    setRuntimeFontReady(false);
    setRuntimeFontFailed(false);
    void loadRuntimeFont(runtimeFont, runtimeCssFamily).then(() => {
      if (cancelled) return;
      setRuntimeFontReady(true);
    }).catch(() => { if (!cancelled) { setRuntimeFontFailed(true); setRuntimeFontReady(false); } });
    return () => { cancelled = true; };
  }, [runtimeFont, runtimeFonts.isError, runtimeCssFamily, session.data]);
  useEffect(() => {
    if (!stream.consoleState) return;
    media.current?.sync(stream.consoleState.mediaState.channels, assets);
  }, [assets, stream.consoleState]);
  useEffect(() => () => media.current?.dispose(), []);

  const scrollToBottom = useCallback((behavior: ScrollBehavior = "smooth") => {
    const container = gameConsoleRef.current;
    if (!container) return;
    const top = Math.max(0, container.scrollHeight - container.clientHeight);
    if (behavior === "auto") container.scrollTop = top;
    if (typeof container.scrollTo === "function") container.scrollTo({ top, behavior });
    else container.scrollTop = top;
  }, []);

  const input = useCallback((event: ConsoleInputEvent) => {
    if (!sessionId) return;
    setInputScrollVersion(version => version + 1);
    scrollToBottom("auto");
    const clientMessageId = manager.sendInput(sessionId, { source: event.source, value: event.value, pointer: event.pointer ?? null, key: event.key ?? null });
    if (!clientMessageId) setCloseError("当前实时连接尚未就绪，输入没有发送。请等待连接恢复后重试。");
  }, [manager, scrollToBottom, sessionId]);
  const reportRendererError = useCallback((message: string) => setRendererError(message), []);
  const handleConsoleSurfaceClick = useCallback((event: React.MouseEvent<HTMLElement>) => {
    if (!isBlankConsoleSurfaceTarget(event.target)) return;
    promptControllerRef.current?.submitBlankEnter();
  }, []);
  const close = async () => {
    if (!sessionId || !session.data || closing) return;
    if (!window.confirm(`关闭「${session.data.name}」？Worker 会停止，但 SessionRoot 和原生存档会保留。`)) return;
    setClosing(true); setCloseError(null);
    try {
      const result = await closeSession(sessionId);
      await waitForSession(sessionId, new Set<SessionState>(["CLOSED", "CRASHED"]), { attempts: result.state === "CLOSED" || result.state === "CRASHED" ? 1 : 60 });
      await queryClient.invalidateQueries({ queryKey: ["sessions"] });
      navigate("/sessions");
    } catch (error) { setCloseError(error instanceof ApiError ? error.message : error instanceof Error ? error.message : "关闭 Session 失败。"); }
    finally { setClosing(false); }
  };

  if (!sessionId) return <div className="console-error" role="alert">缺少 Session ID。</div>;
  if (session.isPending) return <div className="console-loading" aria-busy="true">正在读取 Session…</div>;
  if (session.isError || !session.data) return <div className="console-error" role="alert"><h1>Session 不可用</h1><p>{session.error instanceof Error ? session.error.message : "资源不存在或你没有访问权限。"}</p><Link className="secondary-button" to="/sessions">返回 Session</Link></div>;
  if (runtimeFontFailed) return <div className="console-error" role="alert"><h1>字体不可用</h1><p>运行时字体未能通过校验，已停止渲染以避免布局漂移。</p><Link className="secondary-button" to="/sessions">返回 Session</Link></div>;
  if (!runtimeFontReady) return <div className="console-loading" aria-busy="true">正在加载 Session 字体…</div>;

  const state = stream.consoleState;
  const terminalSession = session.data.state === "CLOSED" || session.data.state === "CRASHED";
  const fatal = stream.fatalRenderError ?? rendererError;
  return <ConsoleSurface promptControllerRef={promptControllerRef} className="console-page realtime-console" style={consoleViewportStyle(visualViewport.height, visualViewport.offsetTop)}>
    <div className="console-overlay-actions">
      <Link className="console-overlay-back" to="/sessions" aria-label="离开游戏">←</Link>
      <span className={`connection-chip ${connectionPhase === "ready" && networkOnline ? "is-online" : ""}`} role="status" aria-live="polite"><span className={connectionPhase === "ready" && networkOnline ? "online" : "offline"}/>{networkOnline ? connectionLabel(connectionPhase) : "浏览器离线"}</span>
      <button className="danger-text" aria-label="关闭 Session" onClick={() => void close()} disabled={closing || session.data.state !== "RUNNING"}>{closing ? "正在关闭…" : "关闭"}</button>
    </div>
    <div className="console-layout">
      <main ref={gameConsoleRef} className="game-console realtime-game-console" aria-label="游戏控制台">
        <div className="realtime-console-stage" style={consoleSurfaceStyle(state?.windowMetadata.defaultBackground, state?.windowMetadata.viewportWidth, undefined, session.data.fontSize, session.data.lineHeight, runtimeCssFamily)} onClick={handleConsoleSurfaceClick}>
        <h1 className="sr-only">{session.data.name}</h1>
        <p className="sr-only">Session 状态：<span>{sessionStateLabel(session.data.state)}</span></p>
        {(!networkOnline || (connectionPhase !== "ready" && connectionPhase !== "disconnected")) && <div className="reconnect-banner" role="status" aria-live="polite"><span className="mini-spinner"/><p><strong>{networkOnline ? connectionLabel(connectionPhase) : "浏览器离线"}</strong><small>{networkOnline ? "游戏仍在服务器上运行；浏览器连接恢复后会按 epoch 和序号重新同步。" : "浏览器离线只影响当前显示；Session 和 Worker 不会被关闭。"}</small></p></div>}
        {stream.phase === "resyncing" && <div className="reconnect-banner resync-banner" role="status" aria-live="polite"><span>↻</span><p><strong>正在重新同步控制台</strong><small>检测到输出间隙或旧 Worker 事件，当前画面暂不继续应用。</small></p></div>}
        {stream.phase === "ended" && <div className="reconnect-banner ended-banner" role="status" aria-live="polite"><span>✓</span><p><strong>{terminalSession ? "Session 实时流已结束" : "Session 实时流暂时中断"}</strong><small>{terminalSession ? "Session 已进入终态，Worker 不再接收输入；SessionRoot 和存档仍会保留。" : "Session 状态仍由服务端维护；Worker 可能仍在运行，页面会以新的完整快照恢复。"}</small></p></div>}
        {(closeError || stream.pendingInput?.status === "unknown") && <div className="error-banner" role="alert"><strong>实时操作提示</strong><small>{closeError ?? "上次输入的结果未知；服务端可能已经处理，请确认当前提示后再决定是否重试。"}</small>{stream.pendingInput?.status === "unknown" && <button className="secondary-button" onClick={() => manager.retryUnknownInput(sessionId)}>重试上次输入</button>}</div>}
        {stream.lastReceipt && <div className="console-receipt" role="status" aria-live="polite">输入回执：{inputReceiptLabel(stream.lastReceipt.status)}</div>}
        {fatal && <div className="console-fatal" role="alert"><strong>无法安全渲染此 Session</strong><p>{fatal}</p><small>服务端会继续保留 Session；请等待重新同步或返回 Session 列表。</small></div>}
        {state ? <ConsoleTooltipProvider presentation={state.tooltipPresentation} resources={state.tooltipResources}><CanvasRenderer scene={state.canvasScene} backgroundLayers={state.backgroundLayers} windowMetadata={state.windowMetadata} assets={assets} onInput={input} onRenderError={reportRendererError} interactive /><ScrollbackRenderer lines={state.scrollback} assets={assets} onInput={input} onRenderError={reportRendererError} scrollContainerRef={gameConsoleRef} scrollVersion={`${connectionPhase}:${stream.phase}:${stream.workerEpoch ?? "none"}:${stream.sequence}:${stream.committedFrameId}`} forceScrollVersion={inputScrollVersion} defaultLineHeight={session.data.lineHeight} /></ConsoleTooltipProvider> : <div className="console-empty" aria-busy={stream.phase !== "ended" && stream.phase !== "error" && stream.phase !== "forbidden"}>{stream.phase !== "ended" && stream.phase !== "error" && stream.phase !== "forbidden" && <span className="mini-spinner"/>}<p>{emptyConsoleLabel(stream.phase)}</p></div>}
        {state && state.mediaState.channels.length > 0 && <button className="sound-toggle" type="button" onClick={() => void media.current?.enable().then(() => setSoundEnabled(true))}>{soundEnabled ? "声音已启用" : "启用声音"}</button>}
        </div>
      </main>
      <div className="console-input-dock">
        <PromptController ref={promptControllerRef} prompt={state?.currentPrompt} disabled={connectionPhase !== "ready" || stream.phase === "resyncing" || (stream.phase === "ended" && terminalSession)} pending={stream.pendingInput?.status === "pending"} serverTimeOffsetMilliseconds={manager.serverTimeOffset} onInput={input}/>
      </div>
    </div>
  </ConsoleSurface>;
}

interface ConsoleSurfaceProps {
  children: ReactNode;
  promptControllerRef: { current: PromptControllerHandle | null };
  className?: string;
  style?: CSSProperties;
}

const touchSuppressionMilliseconds = 2_000;
const mouseSelectionMovementThreshold = 4;

interface MousePress {
  pointerId: number;
  clientX: number;
  clientY: number;
  moved: boolean;
}

export function ConsoleSurface({ children, promptControllerRef, className, style }: ConsoleSurfaceProps) {
  const suppressTouchContextMenuUntil = useRef(0);
  const suppressTouchClickUntil = useRef(0);
  const touchGestureActive = useRef(false);
  const touchPointerIds = useRef(new Set<number>());
  const mousePressRef = useRef<MousePress | null>(null);
  const selectionClearFrameRef = useRef<number | null>(null);
  const clearConsoleSelection = useCallback(() => {
    document.getSelection()?.removeAllRanges();
    if (selectionClearFrameRef.current !== null && typeof cancelAnimationFrame === "function") {
      cancelAnimationFrame(selectionClearFrameRef.current);
      selectionClearFrameRef.current = null;
    }
    if (typeof requestAnimationFrame !== "function") return;
    selectionClearFrameRef.current = requestAnimationFrame(() => {
      selectionClearFrameRef.current = null;
      document.getSelection()?.removeAllRanges();
    });
  }, []);
  const cancelPendingSelectionClear = useCallback(() => {
    if (selectionClearFrameRef.current !== null && typeof cancelAnimationFrame === "function") {
      cancelAnimationFrame(selectionClearFrameRef.current);
      selectionClearFrameRef.current = null;
    }
  }, []);
  useEffect(() => () => {
    if (selectionClearFrameRef.current !== null && typeof cancelAnimationFrame === "function") {
      cancelAnimationFrame(selectionClearFrameRef.current);
    }
  }, []);
  const handleClickCapture = useCallback((event: React.MouseEvent<HTMLElement>) => {
    if (Date.now() >= suppressTouchClickUntil.current) return;
    suppressTouchClickUntil.current = 0;
    event.preventDefault();
    event.stopPropagation();
  }, []);
  const handleContextMenu = useCallback((event: React.MouseEvent<HTMLElement>) => {
    if (Date.now() < suppressTouchContextMenuUntil.current) {
      suppressTouchContextMenuUntil.current = 0;
      event.preventDefault();
      return;
    }
    const stage = consoleStageElement(event.currentTarget);
    if (stage && event.target instanceof Node && stage.contains(event.target)) {
      promptControllerRef.current?.submitRightClick(surfacePointerPosition(stage, event.clientX, event.clientY));
    }
    // The console surface is an application surface. Do not expose the
    // browser's context menu there, including when the current prompt cannot
    // consume a right click.
    event.preventDefault();
  }, [promptControllerRef]);
  const handleTouchStartCapture = useCallback((event: React.TouchEvent<HTMLElement>) => {
    if (event.touches.length < 2) return;
    if (touchGestureActive.current) {
      return;
    }
    const stage = consoleStageElement(event.currentTarget);
    const touch = event.touches[0];
    const handled = promptControllerRef.current?.submitRightClick(touch ? surfacePointerPosition(stage ?? event.currentTarget, touch.clientX, touch.clientY) : undefined) ?? false;
    if (!handled) return;
    touchGestureActive.current = true;
    const suppressionDeadline = Date.now() + touchSuppressionMilliseconds;
    suppressTouchClickUntil.current = suppressionDeadline;
    suppressTouchContextMenuUntil.current = Date.now() + 1_000;
  }, [promptControllerRef]);
  const handlePointerDownCapture = useCallback((event: React.PointerEvent<HTMLElement>) => {
    if (event.pointerType === "mouse") {
      if (event.button !== 0) return;
      cancelPendingSelectionClear();
      const stage = consoleStageElement(event.currentTarget);
      if (!stage || !(event.target instanceof Node) || !stage.contains(event.target)) return;
      mousePressRef.current = { pointerId: event.pointerId, clientX: event.clientX, clientY: event.clientY, moved: false };
      return;
    }
    if (event.pointerType !== "touch") return;
    touchPointerIds.current.add(event.pointerId);
    if (touchPointerIds.current.size < 2) return;
    if (touchGestureActive.current) {
      return;
    }
    const stage = consoleStageElement(event.currentTarget);
    const handled = promptControllerRef.current?.submitRightClick(surfacePointerPosition(stage ?? event.currentTarget, event.clientX, event.clientY)) ?? false;
    if (!handled) return;
    touchGestureActive.current = true;
    const suppressionDeadline = Date.now() + touchSuppressionMilliseconds;
    suppressTouchClickUntil.current = suppressionDeadline;
    suppressTouchContextMenuUntil.current = Date.now() + 1_000;
  }, [cancelPendingSelectionClear, promptControllerRef]);
  const handlePointerMoveCapture = useCallback((event: React.PointerEvent<HTMLElement>) => {
    if (event.pointerType !== "mouse") return;
    const press = mousePressRef.current;
    if (!press || press.pointerId !== event.pointerId) return;
    if (Math.hypot(event.clientX - press.clientX, event.clientY - press.clientY) > mouseSelectionMovementThreshold) {
      press.moved = true;
    }
  }, []);
  const handlePointerEndCapture = useCallback((event: React.PointerEvent<HTMLElement>) => {
    if (event.pointerType === "mouse") {
      const press = mousePressRef.current;
      if (!press || press.pointerId !== event.pointerId) return;
      mousePressRef.current = null;
      if (event.type !== "pointerup" || press.moved) return;
      const stage = consoleStageElement(event.currentTarget);
      if (stage && event.target instanceof Node && stage.contains(event.target)) clearConsoleSelection();
      return;
    }
    if (event.pointerType !== "touch") return;
    touchPointerIds.current.delete(event.pointerId);
    if (touchPointerIds.current.size === 0) touchGestureActive.current = false;
  }, [clearConsoleSelection]);
  const handleTouchEndCapture = useCallback((event: React.TouchEvent<HTMLElement>) => {
    if (!touchGestureActive.current) return;
    if (event.touches.length === 0) touchGestureActive.current = false;
  }, []);
  const handleTouchCancelCapture = useCallback((event: React.TouchEvent<HTMLElement>) => {
    if (!touchGestureActive.current) return;
    touchGestureActive.current = false;
  }, []);
  return <div className={className} style={style} onClickCapture={handleClickCapture} onContextMenuCapture={handleContextMenu} onPointerDownCapture={handlePointerDownCapture} onPointerMoveCapture={handlePointerMoveCapture} onPointerUpCapture={handlePointerEndCapture} onPointerCancelCapture={handlePointerEndCapture} onTouchStartCapture={handleTouchStartCapture} onTouchEndCapture={handleTouchEndCapture} onTouchCancelCapture={handleTouchCancelCapture}>{children}</div>;
}

export function isBlankConsoleSurfaceTarget(target: EventTarget | null): boolean {
  return target instanceof Element && !target.closest("button, a, input, select, textarea, [role=\"button\"]");
}

function consoleStageElement(root: HTMLElement): HTMLElement | null {
  return root.querySelector<HTMLElement>(".realtime-console-stage");
}

function surfacePointerPosition(target: HTMLElement, clientX: number, clientY: number): { x: number; y: number } {
  const bounds = target.getBoundingClientRect();
  if (bounds.width <= 0 || bounds.height <= 0 || !Number.isFinite(clientX) || !Number.isFinite(clientY))
    return { x: 0, y: 0 };
  return {
    x: Math.max(0, Math.round(clientX - bounds.left)),
    y: Math.max(0, Math.round(clientY - bounds.top)),
  };
}

export function consoleSurfaceStyle(background: RealtimeColor | null | undefined, viewportWidth?: number, _screenWidth?: number, fontSize?: number, lineHeight?: number, runtimeCssFamily?: string): CSSProperties {
  const width = effectiveConsoleWidth(viewportWidth);
  return {
    ...(background ? { backgroundColor: colorToCss(background) } : {}),
    ...(width > 0 ? { width: `${width}px` } : {}),
    ...(fontSize && fontSize > 0 ? { "--runtime-font-size": `${fontSize}px` } : {}),
    ...(lineHeight && lineHeight > 0 ? { "--runtime-line-height": `${lineHeight}px` } : {}),
    ...(runtimeCssFamily ? { "--runtime-font-family": `"${runtimeCssFamily}"`, fontFamily: `"${runtimeCssFamily}"` } : {}),
  };
}

export function effectiveConsoleWidth(runtimeWidth?: number, _screenWidth?: number): number {
  if (!runtimeWidth || runtimeWidth <= 0) return 0;
  return runtimeWidth;
}

export function consoleViewportStyle(height: number, offsetTop = 0): CSSProperties {
  if (!Number.isFinite(height) || height <= 0) return {};
  const safeOffsetTop = Number.isFinite(offsetTop) ? Math.max(0, offsetTop) : 0;
  return {
    "--console-visual-viewport-height": `${Math.round(height)}px`,
    "--console-visual-viewport-offset-top": `${Math.round(safeOffsetTop)}px`,
  } as CSSProperties;
}

function currentVisualViewport(): { height: number; offsetTop: number } {
  if (typeof window === "undefined") return { height: 0, offsetTop: 0 };
  const viewport = window.visualViewport;
  return {
    height: viewport?.height ?? window.innerHeight,
    offsetTop: viewport?.offsetTop ?? 0,
  };
}

function inputReceiptLabel(status: string): string {
  return ({ ACCEPTED: "已接受", DUPLICATE: "已处理（重复消息）", CONFLICT: "冲突：其他设备已回答", NO_ACTIVE_PROMPT: "已失效：当前没有提示", INVALID_FORMAT: "格式无效", INVALID_COMMAND: "命令无效", CANCELLED: "已取消", TIMED_OUT: "已超时", SESSION_NOT_ACCEPTING_INPUT: "Session 当前不接受输入", STALE_EPOCH: "Worker 已更换", SESSION_NOT_RUNNING: "Session 未运行", INPUT_BACKPRESSURE: "服务繁忙，可使用相同 ID 重试", WORKER_UNAVAILABLE: "Worker 暂不可用，可使用相同 ID 重试", FORBIDDEN: "没有输入权限" } as Record<string, string>)[status] ?? "服务端返回了未分类回执";
}

function emptyConsoleLabel(phase: SessionStoreState["phase"]): string {
  return phase === "resyncing"
    ? "正在请求最新快照…"
    : phase === "ended"
      ? "Worker 实时流已结束"
      : phase === "error" || phase === "forbidden"
        ? "无法获取 Worker 快照"
        : "等待 Worker 快照…";
}
