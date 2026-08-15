import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { ApiError, apiRequest } from "../api";
import { closeSession, useSession, waitForSession, type SessionState } from "../sessions/api";
import { presentationManifestUrl, AssetResolver, type PresentationManifest } from "./AssetResolver";
import { CanvasRenderer } from "./CanvasRenderer";
import { DeadlineClock } from "./DeadlineClock";
import { MediaController } from "./media";
import { PromptController } from "./PromptController";
import { ScrollbackRenderer, type ConsoleInputEvent } from "./ScrollbackRenderer";
import { getRealtimeConnectionManager, type ConnectionPhase } from "../realtime/connection";
import { createSessionStoreState, type SessionStoreState } from "../realtime/sessionStore";

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
  const [fontLoadFailed, setFontLoadFailed] = useState(false);
  const [soundEnabled, setSoundEnabled] = useState(false);
  const session = useSession(sessionId);
  const gameConsoleRef = useRef<HTMLElement>(null);
  const endedSessionRef = useRef<string | null>(null);
  const manifest = useQuery({
    queryKey: ["presentation-manifest", sessionId],
    queryFn: () => apiRequest<PresentationManifest>(presentationManifestUrlPath(sessionId!)),
    enabled: Boolean(sessionId),
    staleTime: 60_000,
    retry: 1,
  });
  const assets = useMemo(() => new AssetResolver(sessionId ?? "missing", manifest.data), [manifest.data, sessionId]);
  const media = useRef<MediaController | null>(null);

  if (!media.current) media.current = new MediaController(message => setRendererError(message));

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
    if (!manifest.data || typeof FontFace === "undefined" || !document.fonts) return;
    let cancelled = false;
    setFontLoadFailed(false);
    const loaded: FontFace[] = [];
    for (const font of assets.manifestFonts()) {
      const url = assets.url(font.assetId);
      if (!url) continue;
      const face = new FontFace(font.cssFamily, `url("${url}")`);
      void face.load().then(ready => { if (!cancelled) { document.fonts.add(ready); loaded.push(ready); } }).catch(() => { if (!cancelled) setFontLoadFailed(true); });
    }
    return () => { cancelled = true; for (const face of loaded) document.fonts.delete(face); };
  }, [assets, manifest.data]);
  useEffect(() => {
    if (!stream.consoleState) return;
    media.current?.sync(stream.consoleState.mediaState.channels, assets);
  }, [assets, stream.consoleState]);
  useEffect(() => () => media.current?.dispose(), []);

  const input = useCallback((event: ConsoleInputEvent) => {
    if (!sessionId || !stream.consoleState?.currentPrompt) return;
    const prompt = stream.consoleState.currentPrompt;
    const allowedSource = event.source === "KEYBOARD" ? "keyboard" : event.source === "BUTTON" ? "button" : "pointer";
    if (prompt.systemInput || prompt.inputType === "waitOnly" || !prompt.allowedSources.includes(allowedSource)) return;
    if (prompt.inputType === "anyKey" && event.source !== "KEYBOARD") return;
    if (prompt.inputType === "primitivePointerKey" && event.source === "BUTTON") return;
    const clientMessageId = manager.sendInput(sessionId, { promptId: prompt.promptId, source: event.source, value: event.value, pointer: event.pointer ?? null, key: event.key ?? null });
    if (!clientMessageId) setCloseError("当前实时连接尚未就绪，输入没有发送。请等待连接恢复后重试。");
  }, [manager, sessionId, stream.consoleState]);
  const reportRendererError = useCallback((message: string) => setRendererError(message), []);

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

  const state = stream.consoleState;
  const fatal = stream.fatalRenderError ?? rendererError ?? (manifest.isError ? "Presentation manifest 无法加载，已停止渲染资源。" : null);
  return <div className="console-page realtime-console">
    <header className="console-header"><div className="console-title"><Link className="icon-button" to="/sessions" aria-label="返回 Session"><span className="back-arrow">←</span></Link><span className="session-art coral">{session.data.name.slice(0, 1)}</span><div><h1>{session.data.name}</h1><p>{session.data.game.name} · {session.data.runtimeVersion}</p></div></div><div className="console-controls"><span className={`connection-chip ${connectionPhase === "ready" && networkOnline ? "is-online" : ""}`} role="status" aria-live="polite"><span className={connectionPhase === "ready" && networkOnline ? "online" : "offline"}/>{networkOnline ? connectionLabel(connectionPhase) : "浏览器离线"}</span><span className={`status-pill ${session.data.state.toLowerCase()}`}><i/>{sessionStateLabel(session.data.state)}</span><Link className="icon-button" to={`/saves?session=${encodeURIComponent(sessionId)}`} aria-label="打开存档"><span aria-hidden="true">▣</span></Link><button className="danger-text" onClick={() => void close()} disabled={closing || session.data.state !== "RUNNING"}>{closing ? "正在关闭…" : "关闭 Session"}</button></div></header>
    {(!networkOnline || (connectionPhase !== "ready" && connectionPhase !== "disconnected")) && <div className="reconnect-banner" role="status" aria-live="polite"><span className="mini-spinner"/><p><strong>{networkOnline ? connectionLabel(connectionPhase) : "浏览器离线"}</strong><small>{networkOnline ? "游戏仍在服务器上运行；浏览器连接恢复后会按 epoch 和序号重新同步。" : "浏览器离线只影响当前显示；Session 和 Worker 不会被关闭。"}</small></p></div>}
    {stream.phase === "resyncing" && <div className="reconnect-banner resync-banner" role="status" aria-live="polite"><span>↻</span><p><strong>正在重新同步控制台</strong><small>检测到输出间隙或旧 Worker 事件，当前画面暂不继续应用。</small></p></div>}
    {stream.phase === "ended" && <div className="reconnect-banner ended-banner" role="status" aria-live="polite"><span>✓</span><p><strong>Session 实时流已结束</strong><small>正在读取最终 Session 状态；Worker 已释放，当前页面不会继续接收输入或播放媒体。</small></p></div>}
    {(closeError || stream.pendingInput?.status === "unknown") && <div className="error-banner" role="alert"><strong>实时操作提示</strong><small>{closeError ?? "上次输入的结果未知；服务端可能已经处理，请确认当前提示后再决定是否重试。"}</small>{stream.pendingInput?.status === "unknown" && <button className="secondary-button" onClick={() => manager.retryUnknownInput(sessionId)}>重试上次输入</button>}</div>}
    {stream.lastReceipt && <div className="console-receipt" role="status" aria-live="polite">输入回执：{inputReceiptLabel(stream.lastReceipt.status)}</div>}
    {fatal && <div className="console-fatal" role="alert"><strong>无法安全渲染此 Session</strong><p>{fatal}</p><small>服务端会继续保留 Session；请等待重新同步或返回 Session 列表。</small></div>}
    {(assets.diagnostics().length > 0 || fontLoadFailed) && <div className="console-compat-warning" role="status"><strong>字体兼容提示</strong><small>{[...assets.diagnostics(), ...(fontLoadFailed ? ["FONT_LOAD_FAILED"] : [])].map(fontDiagnosticLabel).join("；")}</small></div>}
    <div className="console-layout">
      <main ref={gameConsoleRef} className="game-console realtime-game-console" aria-label="游戏控制台">
        {state ? <><CanvasRenderer scene={state.canvasScene} backgroundLayers={state.backgroundLayers} windowMetadata={state.windowMetadata} assets={assets} onInput={input} onRenderError={reportRendererError} /><ScrollbackRenderer lines={state.scrollback} assets={assets} onInput={input} onRenderError={reportRendererError} scrollContainerRef={gameConsoleRef} />{state.truncation.wasTruncated && <p className="console-truncation" role="status">输出过长，已省略 {state.truncation.droppedLineCount} 行和 {state.truncation.droppedNodeCount} 个节点。</p>}{state.currentPrompt && <PromptController prompt={state.currentPrompt} disabled={connectionPhase !== "ready" || stream.phase === "resyncing" || stream.phase === "ended"} pending={stream.pendingInput?.status === "pending"} serverTimeOffsetMilliseconds={manager.serverTimeOffset} onInput={input}/>}</> : <div className="console-empty" aria-busy="true"><span className="mini-spinner"/><p>{stream.phase === "resyncing" ? "正在请求最新快照…" : "等待 Worker 快照…"}</p></div>}
        {state && state.mediaState.channels.length > 0 && <button className="sound-toggle" type="button" onClick={() => void media.current?.enable().then(() => setSoundEnabled(true))}>{soundEnabled ? "声音已启用" : "启用声音"}</button>}
      </main>
      <aside className="console-aside"><div className="aside-block"><p className="aside-title">SESSION</p><dl><div><dt>状态</dt><dd><span className={`status-pill ${session.data.state.toLowerCase()}`}><i/>{sessionStateLabel(session.data.state)}</span></dd></div><div><dt>浏览器连接</dt><dd>{connectionLabel(connectionPhase)}</dd></div><div><dt>存档布局</dt><dd>{state ? "原生 SessionRoot" : "读取中"}</dd></div><div><dt>输出序号</dt><dd>{stream.sequence === null ? "—" : `#${stream.sequence}`}</dd></div><div><dt>Worker epoch</dt><dd>{stream.workerEpoch ?? session.data.workerEpoch}</dd></div></dl></div><div className="aside-block"><p className="aside-title">QUICK ACTIONS</p><Link to={`/saves?session=${encodeURIComponent(sessionId)}`}><span>▣</span>查看存档<span>→</span></Link></div>{state?.currentPrompt && <div className="aside-note"><span>◷</span><p><strong>正在等待输入</strong><small>其他已连接设备也能看到此提示，第一个有效回答会生效。</small></p></div>}<div className="aside-note"><span>◌</span><p><strong>当前 SessionRoot 持久保留</strong><small>关闭浏览器不会关闭 Worker，也不会删除目录。</small></p></div></aside>
    </div>
  </div>;
}

function presentationManifestUrlPath(sessionId: string): string {
  return presentationManifestUrl(sessionId).replace(/^\/api\/v1/, "");
}

function inputReceiptLabel(status: string): string {
  return ({ ACCEPTED: "已接受", DUPLICATE: "已处理（重复消息）", CONFLICT: "冲突：其他设备已回答", STALE_PROMPT: "已失效：提示已变化", NO_ACTIVE_PROMPT: "已失效：当前没有提示", INVALID_FORMAT: "格式无效", INVALID_COMMAND: "命令无效", CANCELLED: "已取消", TIMED_OUT: "已超时", SESSION_NOT_ACCEPTING_INPUT: "Session 当前不接受输入", STALE_EPOCH: "Worker 已更换", SESSION_NOT_RUNNING: "Session 未运行", INPUT_BACKPRESSURE: "服务繁忙，可使用相同 ID 重试", WORKER_UNAVAILABLE: "Worker 暂不可用，可使用相同 ID 重试", FORBIDDEN: "没有输入权限" } as Record<string, string>)[status] ?? "服务端返回了未分类回执";
}

function fontDiagnosticLabel(code: string): string {
  return ({
    FONT_FAMILY_COLLISION: "多个字体文件已使用独立 family，避免浏览器合并",
    FONT_FAMILY_UNMAPPED: "部分字体文件没有可确定的逻辑 family，已使用摘要 family",
    FONT_DEFAULT_FALLBACK_ASSIGNED: "未找到 default 字体，已选择确定性的首个字体作为 default",
    FONT_MULTIPLE_ASSETS_ISOLATED: "检测到多个字体资源，已分别加载",
    FONT_LOAD_FAILED: "字体资源加载失败，文本已使用清单 fallback",
  } as Record<string, string>)[code] ?? "字体资源存在兼容性差异，已使用安全回退";
}
