import { useQuery } from "@tanstack/react-query";
import { FormEvent, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { ApiError } from "../api";
import { useAuth } from "../auth";
import { formatDateTime, listGames, shortDigest } from "../games";
import { DEFAULT_SESSION_STARTUP_DEFAULTS, useSessionStartupDefaults } from "../settings/api";
import { closeSession, createSession, deleteSession, openSession, updateSessionConfiguration, useRuntimeFontCatalog, useSession, useSessionList, waitForSession, waitForSessionDeletion, type RuntimeFontFace, type RuntimeWidthMode, type SessionFontSizeLineHeightMode, type SessionState, type SessionView } from "./api";
import { loadRuntimeFont, runtimeFontCssFamily } from "../console/RuntimeFontLoader";
import { useTranslation } from "react-i18next";
import i18n from "../i18n";

function stateLabel(state: SessionState): string {
  return i18n.t(`sessions.state.${state}`);
}

function isActive(state: SessionState): boolean {
  return state === "RUNNING" || state === "STARTING" || state === "STOPPING";
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const keys = { GAME_HAS_NO_CURRENT_CONTENT: "sessionExtra.noCurrent", GAME_BLOCKED: "sessionExtra.gameBlocked", SESSION_TRANSITION_IN_PROGRESS: "sessionExtra.transition", ACTIVE_WORKER_LIMIT_EXCEEDED: "sessionExtra.workerLimit", INACTIVE_SESSION_LIMIT_EXCEEDED: "sessionExtra.inactiveLimit", SESSION_NOT_DELETABLE: "sessionExtra.notDeletable" } as const;
    const key = keys[error.code as keyof typeof keys];
    if (key) return i18n.t(key);
  }
  return error instanceof Error ? error.message : i18n.t("errors.generic");
}

export function SessionsPage() {
  const { t } = useTranslation();
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
    if (operation === "close" && !window.confirm(t("sessionExtra.closeConfirm", { name: session.name }))) return;
    setActionId(session.id); setMessage(null);
    try {
      const result = operation === "open" ? await openSession(session.id) : await closeSession(session.id);
      const settled = await waitForSession(session.id, operation === "open" ? new Set<SessionState>(["RUNNING", "CRASHED"]) : new Set<SessionState>(["CLOSED", "CRASHED"]), { attempts: result.state === (operation === "open" ? "RUNNING" : "CLOSED") ? 1 : 60 });
      if (operation === "open" && settled.state === "CRASHED") throw new Error(`${t("sessions.operationFailed")}：${t("sessions.state.CRASHED")}`);
      await query.refetch();
      if (operation === "open") navigate(`/sessions/${session.id}`);
    } catch (error) { setMessage(errorMessage(error)); }
    finally { setActionId(null); }
  };

  const remove = async (session: SessionView) => {
    if (session.state !== "CLOSED" && session.state !== "CRASHED") return;
    if (!window.confirm(t("sessionExtra.deleteConfirm", { name: session.name }))) return;
    setActionId(session.id); setMessage(null);
    try {
      const result = await deleteSession(session.id);
      if (result.pending) await waitForSessionDeletion(session.id);
      await query.refetch();
    } catch (error) { setMessage(errorMessage(error)); }
    finally { setActionId(null); }
  };

  return <>
    <header className="page-header"><div><p className="eyebrow">{t("chrome.sessions")}</p><h1>{t("sessions.title")}</h1><p>{t("sessions.description")}</p></div><div className="page-actions"><Link className="primary-button" to="/sessions/new">＋ {t("sessions.create")}</Link></div></header>
    <div className="session-stats"><article><span className="pulse-dot"/><div><strong>{activeCount}</strong><small>{t("sessions.activeWorkers")}</small></div></article><article><span aria-hidden="true">◷</span><div><strong>{waitingCount}</strong><small>{t("sessions.waitingInput")}</small></div></article><article><span aria-hidden="true">▦</span><div><strong>{items.length}</strong><small>{t("sessions.listed")}</small></div></article></div>
    <div className="toolbar"><div className="segment-control" aria-label={t("sessions.filter")}><button className={filter === "ALL" ? "selected" : ""} onClick={() => { setFilter("ALL"); setCursor(undefined); }}>{t("sessions.all")}</button><button className={filter === "ACTIVE" ? "selected" : ""} onClick={() => { setFilter("ACTIVE"); setCursor(undefined); }}>{t("sessions.active")}</button><button className={filter === "CLOSED" ? "selected" : ""} onClick={() => { setFilter("CLOSED"); setCursor(undefined); }}>{t("sessions.closed")}</button><button className={filter === "CRASHED" ? "selected" : ""} onClick={() => { setFilter("CRASHED"); setCursor(undefined); }}>{t("sessions.crashed")}</button></div></div>
    {message && <div className="error-banner" role="alert"><strong>{t("sessions.operationFailed")}</strong><small>{message}</small></div>}
    {query.isPending ? <div className="panel loading-panel" aria-busy="true"><span className="mini-spinner"/>{t("sessions.loading")}</div>
      : query.isError ? <div className="panel error-panel" role="alert"><strong>{t("sessions.loadFailed")}</strong><p>{errorMessage(query.error)}</p><button className="secondary-button" onClick={() => void query.refetch()}>{t("common.retry")}</button></div>
      : items.length === 0 ? <div className="empty-state"><span className="empty-icon">◌</span><h2>{t("sessions.empty")}</h2><p>{t("sessions.emptyDescription")}</p><Link className="primary-button" to="/sessions/new">{t("sessions.create")}</Link></div>
      : <section className="session-list" aria-label={t("sessions.list")}>{items.map(session => <SessionRow key={session.id} session={session} busy={actionId === session.id} onLifecycle={operation => void lifecycle(session, operation)} onDelete={() => void remove(session)} />)}</section>}
    {query.data?.nextCursor && <div className="pagination-actions"><button className="secondary-button" onClick={() => setCursor(query.data?.nextCursor ?? undefined)} disabled={query.isFetching}>{t("sessions.loadMore")}</button></div>}
  </>;
}

function SessionRow({ session, busy, onLifecycle, onDelete }: { session: SessionView; busy: boolean; onLifecycle: (operation: "open" | "close") => void; onDelete: () => void }) {
  const { t } = useTranslation();
  const color = ["coral", "violet", "amber", "blue", "green"][session.id.charCodeAt(0) % 5];
  const canOpen = session.state === "CLOSED" || session.state === "CRASHED";
  return <article className="session-row">
    <span className={`session-art ${color}`}>{session.name.slice(0, 1)}</span>
    <div className="session-main"><div><h2>{session.name}</h2><p>{session.game.name} <span>·</span> {shortDigest(session.sourceContentDigest)}</p></div><div className="session-badges"><span className={`status-pill ${session.state.toLowerCase()}`}><i/>{stateLabel(session.state)}</span>{session.waitingForInput && session.state === "RUNNING" && <span className="status-pill waiting"><i/>{t("sessions.waitingInput")}</span>}</div></div>
    <div className="session-meta"><span>{t("sessions.lastActive")}</span><strong>{formatDateTime(session.lastActivityAt)}</strong></div>
    <div className="session-meta"><span>{t("sessions.createdAt")}</span><strong>{formatDateTime(session.createdAt)}</strong></div>
    <div className="session-row-actions">{canOpen ? <button className="play-button" onClick={() => onLifecycle("open")} disabled={busy}>{busy ? t("sessions.starting") : t("sessions.continue")}</button> : session.state === "RUNNING" ? <Link className="play-button" to={`/sessions/${session.id}`}>{t("sessions.continue")}</Link> : <button className="secondary-button" disabled>{stateLabel(session.state)}</button>}{session.state === "RUNNING" && <button className="text-button" onClick={() => onLifecycle("close")} disabled={busy}>{t("sessions.close")}</button>}{canOpen && <button className="text-button danger" onClick={onDelete} disabled={busy}>{t("sessions.delete")}</button>}<Link className="text-button" to={`/sessions/${session.id}/configuration`}>{t("sessions.configure")}</Link><Link className="text-button" to={`/saves?session=${encodeURIComponent(session.id)}`}>{t("sessions.saves")}</Link></div>
  </article>;
}

export function NewSessionPage() {
  const { t } = useTranslation();
  const { user } = useAuth();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const requestedGame = searchParams.get("game");
  const games = useQuery({ queryKey: ["games", "session-create"], queryFn: listGames, staleTime: 3_000 });
  const fonts = useRuntimeFontCatalog();
  const startupDefaults = useSessionStartupDefaults(user?.id);
  const availableGames = (games.data ?? []).filter(game => game.status === "ACTIVE" && game.hasCurrentContent);
  const [gameId, setGameId] = useState(requestedGame && availableGames.some(game => game.id === requestedGame) ? requestedGame : "");
  const [name, setName] = useState("");
  const [fontSize, setFontSize] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.fontSize);
  const [lineHeight, setLineHeight] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.lineHeight);
  const [fontSizeLineHeightMode, setFontSizeLineHeightMode] = useState<SessionFontSizeLineHeightMode>(DEFAULT_SESSION_STARTUP_DEFAULTS.fontSizeLineHeightMode);
  const [fontFaceId, setFontFaceId] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.fontFaceId);
  const [widthMode, setWidthMode] = useState<RuntimeWidthMode>(DEFAULT_SESSION_STARTUP_DEFAULTS.widthMode);
  const [customWidth, setCustomWidth] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.customWidth ?? 800);
  const [convertBackslashToYen, setConvertBackslashToYen] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.convertBackslashToYen);
  const [fontPreviewReady, setFontPreviewReady] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const waitController = useRef<AbortController | null>(null);

  useEffect(() => {
    if (!gameId && requestedGame && availableGames.some(game => game.id === requestedGame)) setGameId(requestedGame);
  }, [availableGames, gameId, requestedGame]);
  useEffect(() => {
    if (!startupDefaults.data) return;
    setFontFaceId(startupDefaults.data.fontFaceId);
    setFontSize(startupDefaults.data.fontSize);
    setLineHeight(startupDefaults.data.lineHeight);
    setFontSizeLineHeightMode(startupDefaults.data.fontSizeLineHeightMode);
    setWidthMode(startupDefaults.data.widthMode);
    setCustomWidth(startupDefaults.data.customWidth ?? 800);
    setConvertBackslashToYen(startupDefaults.data.convertBackslashToYen);
  }, [startupDefaults.data]);
  useEffect(() => {
    if (fonts.data && !fonts.data.items.some(font => font.faceId === fontFaceId)) setFontFaceId(fonts.data.defaultFaceId);
  }, [fontFaceId, fonts.data]);
  useEffect(() => () => waitController.current?.abort(), []);

  const selected = availableGames.find(game => game.id === gameId);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!selected) { setError(t("sessionExtra.selectRunnable")); return; }
    setPending(true); setError(null);
    const controller = new AbortController();
    waitController.current = controller;
    try {
      const created = await createSession(selected.id, name.trim() || t("sessionExtra.defaultName", { name: selected.name }), fontSize, lineHeight, undefined, fontFaceId, widthMode, widthMode === "CUSTOM" ? customWidth : null, convertBackslashToYen, fontSizeLineHeightMode);
      // SessionRoot materialization copies the complete immutable game tree and
      // can legitimately take several minutes for large games. The operation
      // is durable and continues after the initial HTTP 202 response.
      const ready = created.state === "CLOSED" || created.state === "CRASHED" ? created : await waitForSession(created.id, new Set<SessionState>(["CLOSED", "CRASHED"]), { attempts: null, signal: controller.signal });
      const opened = await openSession(ready.id);
      const running = opened.state === "RUNNING" ? opened : await waitForSession(ready.id, new Set<SessionState>(["RUNNING", "CRASHED"]), { attempts: null, signal: controller.signal });
      if (running.state === "CRASHED") throw new Error(`${t("sessions.operationFailed")}：${t("sessions.state.CRASHED")}`);
      navigate(`/sessions/${running.id}`);
    } catch (cause) { if (!controller.signal.aborted) setError(errorMessage(cause)); }
    finally {
      if (waitController.current === controller) waitController.current = null;
      if (!controller.signal.aborted) setPending(false);
    }
  };

  return <div className="narrow-page"><div className="backline"><Link to="/sessions">← {t("sessions.back")}</Link></div><header className="page-header"><div><p className="eyebrow">{t("chrome.newSession")}</p><h1>{t("sessions.newTitle")}</h1><p>{t("sessions.newDescription")}</p></div></header>
    {games.isError && <div className="error-banner" role="alert"><strong>{t("sessionExtra.gamesFailed")}</strong><small>{errorMessage(games.error)}</small></div>}
    <form className="form-panel" onSubmit={submit}><label><span>{t("sessions.name")}</span><input value={name} onChange={event => setName(event.target.value)} placeholder={t("sessionExtra.placeholder")} maxLength={120} required /></label><label><span>{t("sessions.game")}</span><select value={gameId} onChange={event => setGameId(event.target.value)} disabled={games.isPending || pending} required><option value="">{t("sessions.selectGame")}</option>{(games.data ?? []).map(game => <option value={game.id} key={game.id} disabled={game.status !== "ACTIVE" || !game.hasCurrentContent}>{game.name}{game.status !== "ACTIVE" ? t("sessionExtra.disabled") : !game.hasCurrentContent ? t("sessionExtra.noContent") : ""}</option>)}</select></label><SessionFontField value={fontFaceId} fonts={fonts.data?.items ?? []} disabled={fonts.isPending || pending} onChange={setFontFaceId} onReadinessChange={setFontPreviewReady}/><SessionDisplayFields fontSize={fontSize} lineHeight={lineHeight} fontSizeLineHeightMode={fontSizeLineHeightMode} setFontSize={setFontSize} setLineHeight={setLineHeight} setFontSizeLineHeightMode={setFontSizeLineHeightMode} disabled={pending}/><SessionWidthFields widthMode={widthMode} customWidth={customWidth} setWidthMode={setWidthMode} setCustomWidth={setCustomWidth} disabled={pending}/><SessionYenCompatibilityField value={convertBackslashToYen} onChange={setConvertBackslashToYen} disabled={pending}/><div className="form-explain"><span aria-hidden="true">▣</span><p><strong>{t("sessions.privateRoot")}</strong><small>{t("sessions.privateRootDescription")}</small></p></div>{error && <p className="form-error" role="alert">{error}</p>}<div className="form-actions"><Link className="secondary-button" to="/sessions">{t("common.cancel")}</Link><button className="primary-button" disabled={pending || startupDefaults.isPending || games.isPending || fonts.isPending || fonts.isError || !fonts.data || !fontPreviewReady || !selected}>{pending ? <><span className="mini-spinner"/>{t("sessions.creatingAndStarting")}</> : t("sessions.createAndStart")}</button></div></form>
  </div>;
}

export function SessionFontField({ value, fonts, disabled, onChange, onReadinessChange }: { value: string; fonts: RuntimeFontFace[]; disabled: boolean; onChange: (value: string) => void; onReadinessChange?: (ready: boolean) => void }) {
  const { t } = useTranslation();
  const selected = fonts.find(font => font.faceId === value);
  return <><label><span>{t("sessions.runtimeFont")}</span><select value={value} onChange={event => onChange(event.target.value)} disabled={disabled} required>{!selected && <option value={value} disabled>{t("sessions.fontUnavailable")}</option>}{fonts.map(font => <option value={font.faceId} key={font.faceId}>{font.displayName} · {font.family} {font.weight}</option>)}</select></label><RuntimeFontPreview face={selected} onReadinessChange={onReadinessChange}/></>;
}

function RuntimeFontPreview({ face, onReadinessChange }: { face?: RuntimeFontFace; onReadinessChange?: (ready: boolean) => void }) {
  const { t } = useTranslation();
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
  if (!face) return <p className="runtime-font-preview is-error" role="status">{t("sessions.fontUnavailable")}</p>;
  return <div className={`runtime-font-preview ${state === "error" ? "is-error" : ""}`} role="status" aria-live="polite">
    <span className="runtime-font-preview-text" style={state === "ready" ? { fontFamily: `"${runtimeFontCssFamily(face)}"` } : undefined}>ABC 123　中文 日本語</span>
    {state !== "ready" && <small>{state === "loading" ? t("sessions.fontLoading") : t("sessions.fontFailed")}</small>}
  </div>;
}

export function SessionDisplayFields({ fontSize, lineHeight, fontSizeLineHeightMode, setFontSize, setLineHeight, setFontSizeLineHeightMode, disabled = false }: { fontSize: number; lineHeight: number; fontSizeLineHeightMode: SessionFontSizeLineHeightMode; setFontSize: (value: number) => void; setLineHeight: (value: number) => void; setFontSizeLineHeightMode: (value: SessionFontSizeLineHeightMode) => void; disabled?: boolean }) {
  const { t } = useTranslation();
  const metricsDisabled = disabled || fontSizeLineHeightMode === "CONFIG";
  return <><label><span>{t("sessions.metricsMode")}</span><select value={fontSizeLineHeightMode} onChange={event => setFontSizeLineHeightMode(event.target.value as SessionFontSizeLineHeightMode)} disabled={disabled}><option value="OVERRIDE">{t("sessions.overrideConfig")}</option><option value="CONFIG">{t("sessions.useConfig")}</option></select></label><div className="form-grid"><label><span>{t("sessions.fontSize")}</span><input type="number" min={8} max={72} value={fontSize} onChange={event => setFontSize(Number(event.target.value))} disabled={metricsDisabled}/></label><label><span>{t("sessions.lineHeight")}</span><input type="number" min={Math.max(8, fontSize)} max={128} value={lineHeight} onChange={event => setLineHeight(Number(event.target.value))} disabled={metricsDisabled}/></label></div></>;
}

export function SessionWidthFields({ widthMode, customWidth, setWidthMode, setCustomWidth, disabled = false }: { widthMode: RuntimeWidthMode; customWidth: number; setWidthMode: (value: RuntimeWidthMode) => void; setCustomWidth: (value: number) => void; disabled?: boolean }) {
  const { t } = useTranslation();
  const browserBounded = widthMode === "MAX" || widthMode === "ADAPTIVE";
  return <><label><span>{t("sessions.width")}</span><select value={widthMode} onChange={event => setWidthMode(event.target.value as RuntimeWidthMode)} disabled={disabled}><option value="ORIGINAL">{t("sessions.originalWidth")}</option><option value="MAX">{t("sessions.maxWidth")}</option><option value="ADAPTIVE">{t("sessions.adaptiveWidth")}</option><option value="CUSTOM">{t("sessions.customWidthMode")}</option></select></label>{widthMode === "CUSTOM" && <label><span>{t("sessions.customWidth")}</span><input type="number" min={240} max={16384} value={customWidth} onChange={event => setCustomWidth(Number(event.target.value))} disabled={disabled} required/></label>}</>;
}

export function SessionYenCompatibilityField({ value, onChange, disabled = false }: { value: boolean; onChange: (value: boolean) => void; disabled?: boolean }) {
  const { t } = useTranslation();
  return <fieldset className="checkbox-field">
    <legend>{t("sessions.yen")}</legend>
    <label className="checkbox-option">
      <input type="checkbox" checked={value} onChange={event => onChange(event.target.checked)} disabled={disabled}/>
      <span className="checkbox-copy"><span className="checkbox-title">{t("sessions.yenTitle")}</span><small>{t("sessions.yenDescription")}</small></span>
    </label>
  </fieldset>;
}

export function SessionConfigurationPage() {
  const { t } = useTranslation();
  const { sessionId } = useParams<{ sessionId: string }>();
  const navigate = useNavigate();
  const session = useSession(sessionId);
  const fonts = useRuntimeFontCatalog();
  const [name, setName] = useState(""); const [fontFaceId, setFontFaceId] = useState("sarasa-fixed-sc-1.0.40-regular"); const [fontPreviewReady, setFontPreviewReady] = useState(false); const [fontSize, setFontSize] = useState(18); const [lineHeight, setLineHeight] = useState(19); const [fontSizeLineHeightMode, setFontSizeLineHeightMode] = useState<SessionFontSizeLineHeightMode>("OVERRIDE"); const [widthMode, setWidthMode] = useState<RuntimeWidthMode>("ADAPTIVE"); const [customWidth, setCustomWidth] = useState(800); const [convertBackslashToYen, setConvertBackslashToYen] = useState(true); const [pending, setPending] = useState(false); const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (session.data) { setName(session.data.name); setFontFaceId(session.data.fontFaceId); setFontSize(session.data.fontSize); setLineHeight(session.data.lineHeight); setFontSizeLineHeightMode(session.data.fontSizeLineHeightMode); setWidthMode(session.data.widthMode); setCustomWidth(session.data.customWidth ?? 800); setConvertBackslashToYen(session.data.convertBackslashToYen); } }, [session.data]);
  const submit = async (event: FormEvent) => { event.preventDefault(); if (!sessionId) return; setPending(true); setError(null); try { await updateSessionConfiguration(sessionId, name, fontSize, lineHeight, undefined, fontFaceId, widthMode, widthMode === "CUSTOM" ? customWidth : null, convertBackslashToYen, fontSizeLineHeightMode); navigate("/sessions"); } catch (cause) { setError(errorMessage(cause)); } finally { setPending(false); } };
  if (session.isPending || !session.data) return <div className="narrow-page">{t("sessions.loading")}</div>;
  const selectedFontExists = fonts.data?.items.some(font => font.faceId === fontFaceId) === true;
  return <div className="narrow-page"><div className="backline"><Link to="/sessions">← {t("sessions.back")}</Link></div><header className="page-header"><div><p className="eyebrow">{t("chrome.sessionSettings")}</p><h1>{t("sessionExtra.configTitle")}</h1><p>{t("sessionExtra.configDescription", { name: session.data.game.name })}</p></div></header><form className="form-panel" onSubmit={submit}><label><span>{t("sessions.name")}</span><input value={name} onChange={event => setName(event.target.value)} required/></label><label><span>{t("sessions.game")}</span><input value={session.data.game.name} disabled/></label><SessionFontField value={fontFaceId} fonts={fonts.data?.items ?? []} disabled={fonts.isPending || pending || session.data.state === "RUNNING" || session.data.state === "STARTING" || session.data.state === "STOPPING"} onChange={setFontFaceId} onReadinessChange={setFontPreviewReady}/><SessionDisplayFields fontSize={fontSize} lineHeight={lineHeight} fontSizeLineHeightMode={fontSizeLineHeightMode} setFontSize={setFontSize} setLineHeight={setLineHeight} setFontSizeLineHeightMode={setFontSizeLineHeightMode} disabled={pending || session.data.state === "RUNNING" || session.data.state === "STARTING" || session.data.state === "STOPPING"}/><SessionWidthFields widthMode={widthMode} customWidth={customWidth} setWidthMode={setWidthMode} setCustomWidth={setCustomWidth} disabled={pending || session.data.state === "RUNNING" || session.data.state === "STARTING" || session.data.state === "STOPPING"}/><SessionYenCompatibilityField value={convertBackslashToYen} onChange={setConvertBackslashToYen} disabled={pending || session.data.state === "RUNNING" || session.data.state === "STARTING" || session.data.state === "STOPPING"}/>{error && <p className="form-error" role="alert">{error}</p>}<div className="form-actions"><Link className="secondary-button" to="/sessions">{t("common.cancel")}</Link><button className="primary-button" disabled={pending || fonts.isPending || fonts.isError || !fonts.data || !selectedFontExists || !fontPreviewReady || session.data.state === "RUNNING" || session.data.state === "STARTING" || session.data.state === "STOPPING"}>{pending ? t("common.saving") : t("sessionExtra.saveConfig")}</button></div></form></div>;
}

export function sessionGameGlyph(session: SessionView): string { return session.game.name.slice(0, 1); }
