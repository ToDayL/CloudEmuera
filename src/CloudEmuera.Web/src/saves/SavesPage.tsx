import { useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { ApiError } from "../api";
import { formatBytes, formatDateTime } from "../games";
import { useSession, useSessionList, type SessionView } from "../sessions/api";
import { canMutateSaves, deleteSave, importSave, renameSave, saveDownloadUrl, saveKindLabel, useInvalidateSaves, useSaves } from "./api";

function stateLabel(state: SessionView["state"]): string {
  return ({ CREATING: "创建中", STARTING: "启动中", RUNNING: "运行中", STOPPING: "停止中", CLOSED: "已关闭", CRASHED: "已崩溃" } as Record<SessionView["state"], string>)[state];
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError && error.code === "SESSION_HAS_ACTIVE_WORKER") return "Session 仍有活动 Worker，请先关闭后再修改存档。";
  return error instanceof Error ? error.message : "存档操作失败。";
}

export function SavesPage() {
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
    if (file.size > 256 * 1024 * 1024) { setMessage("文件超过浏览器端 256 MiB 预检上限。"); return; }
    const normalizedPath = targetPath.trim();
    const existing = saves.data?.items.some(item => item.path === normalizedPath);
    if (existing && !window.confirm(`覆盖已有存档「${normalizedPath}」？这是对原生文件的替换操作。`)) return;
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
        setMessage("已取消等待上传结果；服务端可能已经提交，请刷新列表确认最终文件。\n本次上传不会自动换用新的幂等键重试。");
        await invalidate();
      } else setMessage(errorMessage(error));
    }
    finally { setBusy(false); setUploadProgress(null); setUploadController(null); }
  };

  const rename = async (path: string) => {
    if (!sessionId || !mutable) return;
    const next = window.prompt("输入新的存档路径", path);
    if (!next || next.trim() === path) return;
    if (!window.confirm(`将「${path}」重命名为「${next.trim()}」？`)) return;
    setBusy(true); setMessage(null);
    try { await renameSave(sessionId, path, next.trim()); await invalidate(); }
    catch (error) { setMessage(errorMessage(error)); }
    finally { setBusy(false); }
  };

  const remove = async (path: string) => {
    if (!sessionId || !mutable || !window.confirm(`删除「${path}」？此操作不能撤销。`)) return;
    setBusy(true); setMessage(null);
    try { await deleteSave(sessionId, path); await invalidate(); }
    catch (error) { setMessage(errorMessage(error)); }
    finally { setBusy(false); }
  };

  return <>
    <header className="page-header"><div><p className="eyebrow">NATIVE SAVES</p><h1>原生存档</h1><p>直接管理每个持久 SessionRoot 中由 Emuera 创建的存档文件。</p></div><div className="page-actions"><Link className="secondary-button" to="/sessions">返回 Session</Link></div></header>
    {message && <div className="error-banner" role="alert"><strong>操作未完成</strong><small>{message}</small></div>}
    <div className="save-layout real-save-layout"><aside className="save-sessions"><p className="aside-title">选择 SESSION</p>{sessions.isPending ? <p className="save-loading">正在读取…</p> : (sessions.data?.items ?? []).map(item => <button className={item.id === sessionId ? "active" : ""} key={item.id} onClick={() => select(item.id)}><span className="session-art amber">{item.name.slice(0, 1)}</span><span><strong>{item.name}</strong><small>{item.game.name} · {stateLabel(item.state)}</small></span><span aria-hidden="true">→</span></button>)}</aside><section className="panel save-panel">
      {!sessionId ? <div className="empty-list-panel"><p>还没有可管理的 Session 存档。</p><Link className="primary-button" to="/sessions/new">创建 Session</Link></div> : session.isPending ? <div className="loading-panel" aria-busy="true"><span className="mini-spinner"/>正在读取 Session…</div> : session.isError || !session.data ? <div className="error-panel" role="alert"><strong>无法读取 Session</strong><p>{errorMessage(session.error)}</p></div> : <>
        <div className="panel-heading"><div><h2>{session.data.name}</h2><p>原生布局：<code>{saves.data?.layout === "SAV_DIRECTORY" ? "sav/" : saves.data?.layout === "ROOT" ? "SessionRoot" : "读取中"}</code> · {saves.data?.items.length ?? 0} 个文件</p></div><span className={`status-pill ${session.data.state.toLowerCase()}`}><i/>{stateLabel(session.data.state)}</span></div>
        {!mutable && <div className="locked-banner"><span aria-hidden="true">⚠</span><p><strong>Session 运行时存档由 Worker 独占</strong><small>你仍可下载当前文件，但上传、重命名和删除需要先关闭 Session。</small></p><Link to={`/sessions/${session.data.id}`}>前往 Session</Link></div>}
        {session.data.state === "CRASHED" && <div className="locked-banner crashed-save-banner"><span aria-hidden="true">!</span><p><strong>Worker 曾异常退出</strong><small>这些文件可能正处于原生写入中断后的现场；下载或修改前请确认你接受格式损坏风险。</small></p></div>}
        <div className="save-import-form"><label><span>导入文件</span><input type="file" onChange={event => { const selected = event.target.files?.[0] ?? null; setFile(selected); if (selected && !targetPath) setTargetPath(selected.name); }} disabled={!mutable || busy}/></label><label><span>目标路径</span><input value={targetPath} onChange={event => setTargetPath(event.target.value)} placeholder={saves.data?.layout === "SAV_DIRECTORY" ? "sav/save01.sav" : "save01.sav"} disabled={!mutable || busy}/></label><button className="primary-button" onClick={() => void upload()} disabled={!mutable || busy || !file || !targetPath.trim()}>{busy ? "处理中…" : "导入 / 替换"}</button>{uploadController && <button className="secondary-button" type="button" onClick={() => uploadController.abort()}>取消等待</button>}</div>
        {uploadProgress !== null && <div className="upload-progress" role="status" aria-live="polite"><label htmlFor="save-upload-progress">上传进度 {uploadProgress}%</label><progress id="save-upload-progress" max={100} value={uploadProgress}/><small>取消只停止浏览器等待；服务端是否已提交，请以刷新后的列表为准。</small></div>}
        {saves.isPending ? <div className="loading-panel" aria-busy="true"><span className="mini-spinner"/>正在读取存档…</div> : saves.isError ? <div className="error-panel" role="alert"><strong>无法读取存档</strong><p>{errorMessage(saves.error)}</p><button className="secondary-button" onClick={() => void saves.refetch()}>重试</button></div> : <div className="save-table"><div className="save-table-head"><span>文件</span><span>大小</span><span>修改时间</span><span>类型</span><span/></div>{(saves.data?.items ?? []).map(item => <div className="save-file" key={item.path}><span className="file-icon">▣</span><span><strong>{item.path}</strong><small>{saveKindLabel(item.kind)}</small></span><span>{formatBytes(item.sizeBytes)}</span><span>{formatDateTime(item.modifiedAt)}</span><span className="save-item-type">{item.kind}</span><span className="file-actions"><a href={saveDownloadUrl(session.data.id, item.path)} aria-label={`下载 ${item.path}`} download>⇩</a><button aria-label={`重命名 ${item.path}`} disabled={!mutable || busy} onClick={() => void rename(item.path)}>✎</button><button aria-label={`删除 ${item.path}`} disabled={!mutable || busy} onClick={() => void remove(item.path)}>×</button></span></div>)}{(saves.data?.items ?? []).length === 0 && <p className="file-empty">这个 SessionRoot 中还没有原生存档文件。</p>}</div>}
      </>}
    </section></div>
  </>;
}
