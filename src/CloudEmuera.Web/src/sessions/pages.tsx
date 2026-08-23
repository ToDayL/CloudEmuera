import { useQuery } from "@tanstack/react-query";
import { FormEvent, useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { ApiError } from "../api";
import { formatDateTime, listGames, shortDigest } from "../games";
import { closeSession, createSession, deleteSession, openSession, updateSessionConfiguration, useRuntimeFontCatalog, useSession, useSessionList, waitForSession, waitForSessionDeletion, type RuntimeFontFace, type SessionState, type SessionView } from "./api";
import { loadRuntimeFont, runtimeFontCssFamily } from "../console/RuntimeFontLoader";

function stateLabel(state: SessionState): string {
  return ({ CREATING: "创建中", STARTING: "启动中", RUNNING: "运行中", STOPPING: "停止中", CLOSED: "已关闭", CRASHED: "已崩溃" } as Record<SessionState, string>)[state];
}

function isActive(state: SessionState): boolean {
  return state === "RUNNING" || state === "STARTING" || state === "STOPPING";
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === "GAME_HAS_NO_CURRENT_CONTENT") return "这个游戏还没有可运行的当前内容。";
    if (error.code === "GAME_BLOCKED") return "这个游戏当前已被禁用。";
    if (error.code === "SESSION_TRANSITION_IN_PROGRESS") return "Session 正在进行另一个生命周期操作，请稍后刷新。";
    if (error.code === "ACTIVE_WORKER_LIMIT_EXCEEDED") return "活动 Worker 名额已满，请先关闭其他 Session。";
    if (error.code === "INACTIVE_SESSION_LIMIT_EXCEEDED") return "未启动 Session 已达到实例上限，请先删除不再需要的 Session。";
    if (error.code === "SESSION_NOT_DELETABLE") return "只有已关闭或已崩溃的 Session 可以删除。";
  }
  return error instanceof Error ? error.message : "操作失败。";
}

export function SessionsPage() {
  const navigate = useNavigate();
  const [filter, setFilter] = useState<"ALL" | "ACTIVE" | "CLOSED" | "CRASHED">("ALL");
  const [cursor, setCursor] = useState<string | undefined>();
  const [actionId, setActionId] = useState<string | null>(null);
  const query = useSessionList({ state: filter === "CLOSED" ? "CLOSED" : filter === "CRASHED" ? "CRASHED" : undefined, cursor, limit: 50 });
  const items = (query.data?.items ?? []).filter(item => filter !== "ACTIVE" || isActive(item.state));
  const activeCount = items.filter(item => isActive(item.state)).length;
  const waitingCount = items.filter(item => item.waitingForInput && item.state === "RUNNING").length;
  const [message, setMessage] = useState<string | null>(null);

  const lifecycle = async (session: SessionView, operation: "open" | "close") => {
    if (operation === "close" && !window.confirm(`关闭「${session.name}」？Worker 会停止，但 SessionRoot 和存档会保留。`)) return;
    setActionId(session.id); setMessage(null);
    try {
      const result = operation === "open" ? await openSession(session.id) : await closeSession(session.id);
      await waitForSession(session.id, operation === "open" ? new Set<SessionState>(["RUNNING"]) : new Set<SessionState>(["CLOSED", "CRASHED"]), { attempts: result.state === (operation === "open" ? "RUNNING" : "CLOSED") ? 1 : 60 });
      await query.refetch();
      if (operation === "open") navigate(`/sessions/${session.id}`);
    } catch (error) { setMessage(errorMessage(error)); }
    finally { setActionId(null); }
  };

  const remove = async (session: SessionView) => {
    if (session.state !== "CLOSED" && session.state !== "CRASHED") return;
    if (!window.confirm(`删除「${session.name}」？此操作会永久删除 SessionRoot 和存档，不能撤销。`)) return;
    setActionId(session.id); setMessage(null);
    try {
      const result = await deleteSession(session.id);
      if (result.pending) await waitForSessionDeletion(session.id);
      await query.refetch();
    } catch (error) { setMessage(errorMessage(error)); }
    finally { setActionId(null); }
  };

  return <>
    <header className="page-header"><div><p className="eyebrow">SESSIONS</p><h1>游戏 Session</h1><p>浏览器离开后，活动 Session 仍会继续运行并等待你回来。</p></div><div className="page-actions"><Link className="primary-button" to="/sessions/new">＋ 创建 Session</Link></div></header>
    <div className="session-stats"><article><span className="pulse-dot"/><div><strong>{activeCount}</strong><small>活动 Worker</small></div></article><article><span aria-hidden="true">◷</span><div><strong>{waitingCount}</strong><small>等待输入</small></div></article><article><span aria-hidden="true">▦</span><div><strong>{items.length}</strong><small>当前列表 Session</small></div></article></div>
    <div className="toolbar"><div className="segment-control" aria-label="Session 筛选"><button className={filter === "ALL" ? "selected" : ""} onClick={() => { setFilter("ALL"); setCursor(undefined); }}>全部</button><button className={filter === "ACTIVE" ? "selected" : ""} onClick={() => { setFilter("ACTIVE"); setCursor(undefined); }}>活动中</button><button className={filter === "CLOSED" ? "selected" : ""} onClick={() => { setFilter("CLOSED"); setCursor(undefined); }}>已关闭</button><button className={filter === "CRASHED" ? "selected" : ""} onClick={() => { setFilter("CRASHED"); setCursor(undefined); }}>已崩溃</button></div></div>
    {message && <div className="error-banner" role="alert"><strong>操作未完成</strong><small>{message}</small></div>}
    {query.isPending ? <div className="panel loading-panel" aria-busy="true"><span className="mini-spinner"/>正在读取 Session…</div>
      : query.isError ? <div className="panel error-panel" role="alert"><strong>无法读取 Session</strong><p>{errorMessage(query.error)}</p><button className="secondary-button" onClick={() => void query.refetch()}>重试</button></div>
      : items.length === 0 ? <div className="empty-state"><span className="empty-icon">◌</span><h2>还没有 Session</h2><p>从已启用当前内容的游戏创建一个独立、可重连的 Session。</p><Link className="primary-button" to="/sessions/new">创建 Session</Link></div>
      : <section className="session-list" aria-label="Session 列表">{items.map(session => <SessionRow key={session.id} session={session} busy={actionId === session.id} onLifecycle={operation => void lifecycle(session, operation)} onDelete={() => void remove(session)} />)}</section>}
    {query.data?.nextCursor && <div className="pagination-actions"><button className="secondary-button" onClick={() => setCursor(query.data?.nextCursor ?? undefined)} disabled={query.isFetching}>加载更多</button></div>}
    <div className="info-banner"><span aria-hidden="true">✦</span><p><strong>Session 与浏览器连接相互独立</strong><small>关闭标签页不会停止游戏。请在不再需要时显式关闭 Session，以释放 Worker 名额。</small></p></div>
  </>;
}

function SessionRow({ session, busy, onLifecycle, onDelete }: { session: SessionView; busy: boolean; onLifecycle: (operation: "open" | "close") => void; onDelete: () => void }) {
  const color = ["coral", "violet", "amber", "blue", "green"][session.id.charCodeAt(0) % 5];
  const canOpen = session.state === "CLOSED" || session.state === "CRASHED";
  return <article className="session-row">
    <span className={`session-art ${color}`}>{session.name.slice(0, 1)}</span>
    <div className="session-main"><div><h2>{session.name}</h2><p>{session.game.name} <span>·</span> {shortDigest(session.sourceContentDigest)}</p></div><div className="session-badges"><span className={`status-pill ${session.state.toLowerCase()}`}><i/>{stateLabel(session.state)}</span>{session.waitingForInput && session.state === "RUNNING" && <span className="tag waiting">◷ 等待输入</span>}</div></div>
    <div className="session-meta"><span>最后活动</span><strong>{formatDateTime(session.lastActivityAt)}</strong></div>
    <div className="session-meta"><span>创建时间</span><strong>{formatDateTime(session.createdAt)}</strong></div>
    <div className="session-row-actions">{canOpen ? <button className="play-button" onClick={() => onLifecycle("open")} disabled={busy}>{busy ? "启动中…" : "继续游戏"}</button> : session.state === "RUNNING" ? <Link className="play-button" to={`/sessions/${session.id}`}>继续游戏</Link> : <button className="secondary-button" disabled>{stateLabel(session.state)}</button>}{session.state === "RUNNING" && <button className="text-button" onClick={() => onLifecycle("close")} disabled={busy}>关闭</button>}{canOpen && <button className="text-button danger" onClick={onDelete} disabled={busy}>删除</button>}<Link className="text-button" to={`/sessions/${session.id}/configuration`}>配置</Link><Link className="text-button" to={`/saves?session=${encodeURIComponent(session.id)}`}>存档</Link></div>
  </article>;
}

export function NewSessionPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const requestedGame = searchParams.get("game");
  const games = useQuery({ queryKey: ["games", "session-create"], queryFn: listGames, staleTime: 3_000 });
  const fonts = useRuntimeFontCatalog();
  const availableGames = (games.data ?? []).filter(game => game.status === "ACTIVE" && game.hasCurrentContent);
  const [gameId, setGameId] = useState(requestedGame && availableGames.some(game => game.id === requestedGame) ? requestedGame : "");
  const [name, setName] = useState("");
  const [fontSize, setFontSize] = useState(18);
  const [lineHeight, setLineHeight] = useState(19);
  const [fontFaceId, setFontFaceId] = useState("sarasa-fixed-sc-1.0.40-regular");
  const [fontPreviewReady, setFontPreviewReady] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!gameId && requestedGame && availableGames.some(game => game.id === requestedGame)) setGameId(requestedGame);
  }, [availableGames, gameId, requestedGame]);
  useEffect(() => {
    if (fonts.data && !fonts.data.items.some(font => font.faceId === fontFaceId)) setFontFaceId(fonts.data.defaultFaceId);
  }, [fontFaceId, fonts.data]);

  const selected = availableGames.find(game => game.id === gameId);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!selected) { setError("请选择一个有当前内容的可运行游戏。"); return; }
    setPending(true); setError(null);
    try {
      const created = await createSession(selected.id, name.trim() || `${selected.name} · 新旅程`, fontSize, lineHeight, undefined, fontFaceId);
      const ready = created.state === "CLOSED" || created.state === "CRASHED" ? created : await waitForSession(created.id, new Set<SessionState>(["CLOSED", "CRASHED"]));
      const opened = await openSession(ready.id);
      const running = opened.state === "RUNNING" ? opened : await waitForSession(ready.id, new Set<SessionState>(["RUNNING"]));
      navigate(`/sessions/${running.id}`);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setPending(false); }
  };

  return <div className="narrow-page"><div className="backline"><Link to="/sessions">← 返回 Session</Link></div><header className="page-header"><div><p className="eyebrow">NEW SESSION</p><h1>创建 Session</h1><p>每个 Session 都拥有独立、持久的游戏目录与原生存档。</p></div></header>
    {games.isError && <div className="error-banner" role="alert"><strong>无法读取游戏库</strong><small>{errorMessage(games.error)}</small></div>}
    <form className="form-panel" onSubmit={submit}><label><span>Session 名称</span><input value={name} onChange={event => setName(event.target.value)} placeholder="例如：周目三 · 新旅程" maxLength={120} required /></label><label><span>游戏</span><select value={gameId} onChange={event => setGameId(event.target.value)} disabled={games.isPending || pending} required><option value="">请选择游戏</option>{(games.data ?? []).map(game => <option value={game.id} key={game.id} disabled={game.status !== "ACTIVE" || !game.hasCurrentContent}>{game.name}{game.status !== "ACTIVE" ? "（已禁用）" : !game.hasCurrentContent ? "（无当前内容）" : ""}</option>)}</select></label><SessionFontField value={fontFaceId} fonts={fonts.data?.items ?? []} disabled={fonts.isPending || pending} onChange={setFontFaceId} onReadinessChange={setFontPreviewReady}/><SessionDisplayFields fontSize={fontSize} lineHeight={lineHeight} setFontSize={setFontSize} setLineHeight={setLineHeight}/><div className="form-explain"><span aria-hidden="true">▣</span><p><strong>将创建私有 SessionRoot</strong><small>创建时完整复制游戏当时的当前内容；游戏后续编辑不会改变这个 Session。</small></p></div>{error && <p className="form-error" role="alert">{error}</p>}<div className="form-actions"><Link className="secondary-button" to="/sessions">取消</Link><button className="primary-button" disabled={pending || games.isPending || fonts.isPending || fonts.isError || !fonts.data || !fontPreviewReady || !selected}>{pending ? <><span className="mini-spinner"/>正在创建并启动…</> : "创建并开始"}</button></div></form>
  </div>;
}

function SessionFontField({ value, fonts, disabled, onChange, onReadinessChange }: { value: string; fonts: RuntimeFontFace[]; disabled: boolean; onChange: (value: string) => void; onReadinessChange?: (ready: boolean) => void }) {
  const selected = fonts.find(font => font.faceId === value);
  return <><label><span>运行时字体</span><select value={value} onChange={event => onChange(event.target.value)} disabled={disabled} required>{!selected && <option value={value} disabled>当前字体不可用，请选择现有字体</option>}{fonts.map(font => <option value={font.faceId} key={font.faceId}>{font.displayName} · {font.family} {font.weight}</option>)}</select></label><RuntimeFontPreview face={selected} onReadinessChange={onReadinessChange}/></>;
}

function RuntimeFontPreview({ face, onReadinessChange }: { face?: RuntimeFontFace; onReadinessChange?: (ready: boolean) => void }) {
  const [state, setState] = useState<"loading" | "ready" | "error">(face ? "loading" : "error");
  useEffect(() => {
    let cancelled = false;
    onReadinessChange?.(false);
    if (!face) { setState("error"); return () => { cancelled = true; }; }
    setState("loading");
    const family = runtimeFontCssFamily(face);
    void loadRuntimeFont(face, family).then(() => { if (!cancelled) { setState("ready"); onReadinessChange?.(true); } }).catch(() => { if (!cancelled) { setState("error"); onReadinessChange?.(false); } });
    return () => { cancelled = true; };
  }, [face, onReadinessChange]);
  if (!face) return <p className="runtime-font-preview is-error" role="status">字体目录中没有当前字体，请选择一个可用 face。</p>;
  return <div className={`runtime-font-preview ${state === "error" ? "is-error" : ""}`} role="status" aria-live="polite">
    <span className="runtime-font-preview-text" style={state === "ready" ? { fontFamily: `"${runtimeFontCssFamily(face)}"` } : undefined}>ABC 123　中文 日本語</span>
    <small>{state === "loading" ? "正在加载所选字体预览…" : state === "ready" ? `${face.displayName} 已通过 WOFF2 校验` : "字体预览加载失败；创建/保存已禁用。"}</small>
  </div>;
}

function SessionDisplayFields({ fontSize, lineHeight, setFontSize, setLineHeight }: { fontSize: number; lineHeight: number; setFontSize: (value: number) => void; setLineHeight: (value: number) => void }) {
  return <div className="form-grid"><label><span>字号（px）</span><input type="number" min={8} max={72} value={fontSize} onChange={event => setFontSize(Number(event.target.value))}/></label><label><span>行高（px）</span><input type="number" min={8} max={128} value={lineHeight} onChange={event => setLineHeight(Number(event.target.value))}/></label></div>;
}

export function SessionConfigurationPage() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();
  const session = useSession(sessionId);
  const fonts = useRuntimeFontCatalog();
  const [name, setName] = useState(""); const [fontFaceId, setFontFaceId] = useState("sarasa-fixed-sc-1.0.40-regular"); const [fontPreviewReady, setFontPreviewReady] = useState(false); const [fontSize, setFontSize] = useState(18); const [lineHeight, setLineHeight] = useState(19); const [pending, setPending] = useState(false); const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (session.data) { setName(session.data.name); setFontFaceId(session.data.fontFaceId); setFontSize(session.data.fontSize); setLineHeight(session.data.lineHeight); } }, [session.data]);
  const submit = async (event: FormEvent) => { event.preventDefault(); if (!sessionId) return; setPending(true); setError(null); try { await updateSessionConfiguration(sessionId, name, fontSize, lineHeight, undefined, fontFaceId); navigate("/sessions"); } catch (cause) { setError(errorMessage(cause)); } finally { setPending(false); } };
  if (session.isPending || !session.data) return <div className="narrow-page">正在读取 Session…</div>;
  const selectedFontExists = fonts.data?.items.some(font => font.faceId === fontFaceId) === true;
  return <div className="narrow-page"><div className="backline"><Link to="/sessions">← 返回 Session</Link></div><header className="page-header"><div><p className="eyebrow">SESSION SETTINGS</p><h1>Session 配置</h1><p>游戏固定为 {session.data.game.name}；运行中的 Session 需要先关闭才能修改。</p></div></header><form className="form-panel" onSubmit={submit}><label><span>Session 名称</span><input value={name} onChange={event => setName(event.target.value)} required/></label><label><span>游戏</span><input value={session.data.game.name} disabled/></label><SessionFontField value={fontFaceId} fonts={fonts.data?.items ?? []} disabled={fonts.isPending || pending || session.data.state === "RUNNING" || session.data.state === "STARTING" || session.data.state === "STOPPING"} onChange={setFontFaceId} onReadinessChange={setFontPreviewReady}/><SessionDisplayFields fontSize={fontSize} lineHeight={lineHeight} setFontSize={setFontSize} setLineHeight={setLineHeight}/>{error && <p className="form-error" role="alert">{error}</p>}<div className="form-actions"><Link className="secondary-button" to="/sessions">取消</Link><button className="primary-button" disabled={pending || fonts.isPending || fonts.isError || !fonts.data || !selectedFontExists || !fontPreviewReady || session.data.state === "RUNNING" || session.data.state === "STARTING" || session.data.state === "STOPPING"}>{pending ? "保存中…" : "保存配置"}</button></div></form></div>;
}

export function sessionGameGlyph(session: SessionView): string { return session.game.name.slice(0, 1); }
