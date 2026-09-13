import { useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { ApiError } from "../api";
import { formatBytes, formatDateTime } from "../games";
import { useSession, useSessionList, type SessionView } from "../sessions/api";
import { canMutateSaves, deleteSave, importSave, renameSave, saveDownloadUrl, saveKindLabel, useInvalidateSaves, useSaves } from "./api";
import { useTranslation } from "react-i18next";
import i18n from "../i18n";

function stateLabel(state: SessionView["state"]): string {
  return i18n.t(`sessions.state.${state}`);
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError && error.code === "SESSION_HAS_ACTIVE_WORKER") return i18n.t("saveExtra.activeWorker");
  return error instanceof Error ? error.message : i18n.t("saveExtra.failed");
}

export function SavesPage() {
  const { t } = useTranslation();
  const { sessionId: routeSessionId } = useParams<{ sessionId?: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const sessions = useSessionList({ limit: 100 });
  const requestedId = searchParams.get("session") ?? routeSessionId;
  const [selectedId, setSelectedId] = useState<string | undefined>(requestedId ?? undefined);
  const sessionId = selectedId ?? sessions.data?.items[0]?.id;
  const session = useSession(sessionId);
  const saves = useSaves(sessionId);
  const invalidate = useInvalidateSaves(sessionId);
  const [busy, setBusy] = useState(false);
  const [uploadProgress, setUploadProgress] = useState<number | null>(null);
  const [uploadController, setUploadController] = useState<AbortController | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [targetPath, setTargetPath] = useState("");

  useEffect(() => {
    if (!selectedId && sessions.data?.items[0]) setSelectedId(sessions.data.items[0].id);
  }, [selectedId, sessions.data?.items]);
  useEffect(() => {
    if (sessionId && requestedId !== sessionId) setSearchParams({ session: sessionId }, { replace: true });
  }, [requestedId, sessionId, setSearchParams]);

  const select = (id: string) => { setSelectedId(id); setMessage(null); setSearchParams({ session: id }); };
  const mutable = canMutateSaves(session.data?.state);

  const upload = async () => {
    if (!sessionId || !file || !targetPath.trim() || !mutable) return;
    if (file.size > 256 * 1024 * 1024) { setMessage(t("saveExtra.tooLarge")); return; }
    const normalizedPath = targetPath.trim();
    const existing = saves.data?.items.some(item => item.path === normalizedPath);
    if (existing && !window.confirm(t("saveExtra.replaceConfirm", { path: normalizedPath }))) return;
    const controller = new AbortController();
    setBusy(true); setUploadProgress(0); setUploadController(controller); setMessage(null);
    try {
      await importSave(sessionId, normalizedPath, file, {
        signal: controller.signal,
        onProgress: (loaded, total) => setUploadProgress(total > 0 ? Math.round((loaded / total) * 100) : null),
      });
      setFile(null); setTargetPath(""); await invalidate();
    }
    catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        setMessage(t("saveExtra.cancelNotice"));
        await invalidate();
      } else setMessage(errorMessage(error));
    }
    finally { setBusy(false); setUploadProgress(null); setUploadController(null); }
  };

  const rename = async (path: string) => {
    if (!sessionId || !mutable) return;
    const next = window.prompt(t("saveExtra.newPath"), path);
    if (!next || next.trim() === path) return;
    if (!window.confirm(t("saveExtra.renameConfirm", { path, next: next.trim() }))) return;
    setBusy(true); setMessage(null);
    try { await renameSave(sessionId, path, next.trim()); await invalidate(); }
    catch (error) { setMessage(errorMessage(error)); }
    finally { setBusy(false); }
  };

  const remove = async (path: string) => {
    if (!sessionId || !mutable || !window.confirm(t("saveExtra.deleteConfirm", { path }))) return;
    setBusy(true); setMessage(null);
    try { await deleteSave(sessionId, path); await invalidate(); }
    catch (error) { setMessage(errorMessage(error)); }
    finally { setBusy(false); }
  };

  return <>
    <header className="page-header"><div><p className="eyebrow">{t("chrome.nativeSaves")}</p><h1>{t("saves.title")}</h1><p>{t("saves.description")}</p></div><div className="page-actions"><Link className="secondary-button" to="/sessions">{t("saves.back")}</Link></div></header>
    {message && <div className="error-banner" role="alert"><strong>{t("saves.operationFailed")}</strong><small>{message}</small></div>}
    <div className="save-layout real-save-layout"><aside className="save-sessions"><p className="aside-title">{t("saves.select")}</p>{sessions.isPending ? <p className="save-loading">{t("saves.reading")}</p> : (sessions.data?.items ?? []).map(item => <button className={item.id === sessionId ? "active" : ""} key={item.id} onClick={() => select(item.id)}><span className="session-art amber">{item.name.slice(0, 1)}</span><span><strong>{item.name}</strong><small>{item.game.name} · {stateLabel(item.state)}</small></span><span aria-hidden="true">→</span></button>)}</aside><section className="panel save-panel">
      {!sessionId ? <div className="empty-list-panel"><p>{t("saves.none")}</p><Link className="primary-button" to="/sessions/new">{t("saves.createSession")}</Link></div> : session.isPending ? <div className="loading-panel" aria-busy="true"><span className="mini-spinner"/>{t("saves.loadingSession")}</div> : session.isError || !session.data ? <div className="error-panel" role="alert"><strong>{t("saves.sessionFailed")}</strong><p>{errorMessage(session.error)}</p></div> : <>
        <div className="panel-heading"><div><h2>{session.data.name}</h2><p>{t("saves.nativeLayout")}：<code>{saves.data?.layout === "SAV_DIRECTORY" ? "sav/" : saves.data?.layout === "ROOT" ? "SessionRoot" : t("saves.reading")}</code> · {t("saves.filesCount", { count: saves.data?.items.length ?? 0 })}</p></div><span className={`status-pill ${session.data.state.toLowerCase()}`}><i/>{stateLabel(session.data.state)}</span></div>
        {!mutable && <div className="locked-banner"><span aria-hidden="true">⚠</span><p><strong>{t("saveExtra.workerLocked")}</strong><small>{t("saveExtra.workerLockedDetail")}</small></p><Link to={`/sessions/${session.data.id}`}>{t("saveExtra.goSession")}</Link></div>}
        {session.data.state === "CRASHED" && <div className="locked-banner crashed-save-banner"><span aria-hidden="true">!</span><p><strong>{t("saveExtra.crashedWarning")}</strong><small>{t("saveExtra.crashedDetail")}</small></p></div>}
        <div className="save-import-form"><label><span>{t("saves.importFile")}</span><input className="file-input" type="file" onChange={event => { const selected = event.target.files?.[0] ?? null; setFile(selected); if (selected && !targetPath) setTargetPath(selected.name); }} disabled={!mutable || busy}/></label><label><span>{t("saves.targetPath")}</span><input value={targetPath} onChange={event => setTargetPath(event.target.value)} placeholder={saves.data?.layout === "SAV_DIRECTORY" ? "sav/save01.sav" : "save01.sav"} disabled={!mutable || busy}/></label><button className="primary-button" onClick={() => void upload()} disabled={!mutable || busy || !file || !targetPath.trim()}>{busy ? t("saves.processing") : t("saves.importReplace")}</button>{uploadController && <button className="secondary-button" type="button" onClick={() => uploadController.abort()}>{t("saves.cancelWait")}</button>}</div>
        {uploadProgress !== null && <div className="upload-progress" role="status" aria-live="polite"><label htmlFor="save-upload-progress">{t("saves.uploadProgress", { percent: uploadProgress })}</label><progress id="save-upload-progress" max={100} value={uploadProgress}/><small>{t("saveExtra.cancelDetail")}</small></div>}
        {saves.isPending ? <div className="loading-panel" aria-busy="true"><span className="mini-spinner"/>{t("saves.loading")}</div> : saves.isError ? <div className="error-panel" role="alert"><strong>{t("saves.loadFailed")}</strong><p>{errorMessage(saves.error)}</p><button className="secondary-button" onClick={() => void saves.refetch()}>{t("common.retry")}</button></div> : <div className="save-table"><div className="save-table-head"><span>{t("saves.file")}</span><span>{t("saves.size")}</span><span>{t("saves.modified")}</span><span>{t("saves.type")}</span><span/></div>{(saves.data?.items ?? []).map(item => <div className="save-file" key={item.path}><span className="file-icon">▣</span><span><strong>{item.path}</strong><small>{saveKindLabel(item.kind)}</small></span><span>{formatBytes(item.sizeBytes)}</span><span>{formatDateTime(item.modifiedAt)}</span><span className="save-item-type">{item.kind}</span><span className="file-actions"><a href={saveDownloadUrl(session.data.id, item.path)} aria-label={t("saves.download", { path: item.path })} download>⇩</a><button aria-label={t("saves.rename", { path: item.path })} disabled={!mutable || busy} onClick={() => void rename(item.path)}>✎</button><button aria-label={t("saves.delete", { path: item.path })} disabled={!mutable || busy} onClick={() => void remove(item.path)}>×</button></span></div>)}{(saves.data?.items ?? []).length === 0 && <p className="file-empty">{t("saves.empty")}</p>}</div>}
      </>}
    </section></div>
  </>;
}
