import { FormEvent, ReactNode, useCallback, useEffect, useState } from "react";
import { QueryClient, QueryClientProvider, useQueryClient } from "@tanstack/react-query";
import {
  Link,
  NavLink,
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
  useParams,
} from "react-router-dom";
import { CreateUserInput, CurrentUser, UpdateUserInput, useAuth } from "./auth";
import { ApiError } from "./api";
import { AdminWorker, forceStopSession, getAdminRuntime, AdminRuntimeResponse } from "./admin";
import {
  ContentScope,
  GameDiagnosticItem,
  GameFileItem,
  GameLibraryItem,
  GameUploadProgress,
  GameTextFile,
  GameVisibility,
  deleteGame,
  downloadFileUrl,
  formatBytes,
  formatDateTime,
  getGame,
  getGameUploadProgress,
  listDiagnostics,
  listFiles,
  listGames,
  readTextFile,
  setGameBlocked,
  shortDigest,
  updateGame,
  uploadGame,
} from "./games";
import { ConsolePage as RealtimeConsolePage } from "./console/ConsolePage";
import { SavesPage as NativeSavesPage } from "./saves/SavesPage";
import { NewSessionPage as RealNewSessionPage, SessionConfigurationPage as RealSessionConfigurationPage, SessionDisplayFields, SessionFontField, SessionWidthFields, SessionYenCompatibilityField, SessionsPage as RealSessionsPage } from "./sessions/pages";
import { updateSessionStartupDefaults, useSessionStartupDefaults, sessionStartupDefaultsQueryKey, DEFAULT_SESSION_STARTUP_DEFAULTS } from "./settings/api";
import { useRuntimeFontCatalog, type RuntimeWidthMode, type SessionFontSizeLineHeightMode } from "./sessions/api";
import { useTranslation } from "react-i18next";
import i18n, { changeUiLocale, normalizeUiLocale, persistDeviceLocale, setLoginLocaleOverride, UI_LOCALES, type UiLocale } from "./i18n";

type IconName =
  | "archive"
  | "arrow"
  | "book"
  | "check"
  | "chevron"
  | "clock"
  | "close"
  | "download"
  | "folder"
  | "gamepad"
  | "grid"
  | "menu"
  | "more"
  | "pause"
  | "play"
  | "plus"
  | "save"
  | "search"
  | "server"
  | "settings"
  | "spark"
  | "upload"
  | "user"
  | "warning";

const paths: Record<IconName, ReactNode> = {
  archive: <><path d="M4 6h16v14H4z"/><path d="M2.8 3h18.4v4H2.8zM9 11h6"/></>,
  arrow: <path d="m9 18 6-6-6-6"/>,
  book: <><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z"/></>,
  check: <path d="m5 12 4 4L19 6"/>,
  chevron: <path d="m6 9 6 6 6-6"/>,
  clock: <><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></>,
  close: <><path d="m6 6 12 12M18 6 6 18"/></>,
  download: <><path d="M12 3v12m-5-5 5 5 5-5"/><path d="M5 20h14"/></>,
  folder: <path d="M3 6.5h7l2 2h9v10.5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/>,
  gamepad: <><path d="M7 7h10a5 5 0 0 1 4.6 6.9l-1.2 3A2.7 2.7 0 0 1 16 18l-2-2h-4l-2 2a2.7 2.7 0 0 1-4.4-1.1l-1.2-3A5 5 0 0 1 7 7Z"/><path d="M8 10v4m-2-2h4m6-1h.01M18 13h.01"/></>,
  grid: <><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/></>,
  menu: <path d="M4 7h16M4 12h16M4 17h16"/>,
  more: <><circle cx="5" cy="12" r="1" fill="currentColor"/><circle cx="12" cy="12" r="1" fill="currentColor"/><circle cx="19" cy="12" r="1" fill="currentColor"/></>,
  pause: <path d="M8 5v14M16 5v14"/>,
  play: <path d="m8 5 11 7-11 7Z"/>,
  plus: <path d="M12 5v14M5 12h14"/>,
  save: <><path d="M5 3h12l3 3v15H4V3Z"/><path d="M8 3v6h8V3M8 21v-7h8v7"/></>,
  search: <><circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/></>,
  server: <><rect x="3" y="4" width="18" height="6" rx="2"/><rect x="3" y="14" width="18" height="6" rx="2"/><path d="M7 7h.01M7 17h.01"/></>,
  settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.88l.06.06-2.83 2.83-.06-.06a1.7 1.7 0 0 0-1.88-.34 1.7 1.7 0 0 0-1.03 1.56V21h-4v-.08A1.7 1.7 0 0 0 9 19.37a1.7 1.7 0 0 0-1.88.34l-.06.06-2.83-2.83.06-.06A1.7 1.7 0 0 0 4.63 15 1.7 1.7 0 0 0 3.08 14H3v-4h.08A1.7 1.7 0 0 0 4.63 9a1.7 1.7 0 0 0-.34-1.88l-.06-.06 2.83-2.83.06.06A1.7 1.7 0 0 0 9 4.63 1.7 1.7 0 0 0 10 3.08V3h4v.08A1.7 1.7 0 0 0 15 4.63a1.7 1.7 0 0 0 1.88-.34l.06-.06 2.83 2.83-.06.06A1.7 1.7 0 0 0 19.37 9 1.7 1.7 0 0 0 20.92 10H21v4h-.08A1.7 1.7 0 0 0 19.4 15Z"/></>,
  spark: <path d="m12 2 1.4 5.1L18 9l-4.6 1.9L12 16l-1.4-5.1L6 9l4.6-1.9ZM19 15l.7 2.3L22 18l-2.3.7L19 21l-.7-2.3L16 18l2.3-.7Z"/>,
  upload: <><path d="M12 16V4m-5 5 5-5 5 5"/><path d="M5 20h14"/></>,
  user: <><circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/></>,
  warning: <><path d="M12 3 2.8 20h18.4Z"/><path d="M12 9v5m0 3h.01"/></>,
};

function Icon({ name, size = 20 }: { name: IconName; size?: number }) {
  return <svg className="icon" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>;
}

function Logo() {
  return <Link className="brand" to="/games" aria-label="CloudEmuera"><span className="brand-mark">C</span><span>CloudEmuera</span></Link>;
}

const localeNames: Record<UiLocale, string> = { "zh-CN": "简体中文", "en-US": "English", "ja-JP": "日本語" };
function LanguageSelect({ login = false, disabled = false, onChange }: { login?: boolean; disabled?: boolean; onChange?: (locale: UiLocale, previous: UiLocale) => void }) {
  const { i18n, t } = useTranslation();
  const current = normalizeUiLocale(i18n.language) ?? "zh-CN";
  return <label className="language-select"><span>{t("common.language")}</span><select aria-label={t("common.language")} value={current} disabled={disabled} onChange={event => { const locale = event.target.value as UiLocale; const previous = current; if (login) setLoginLocaleOverride(locale); else persistDeviceLocale(locale); void changeUiLocale(locale); onChange?.(locale, previous); }}>{UI_LOCALES.map(locale => <option key={locale} value={locale}>{localeNames[locale]}</option>)}</select></label>;
}

function AppShell({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const { user, logout } = useAuth();
  const navigate = useNavigate();
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();
  useEffect(() => setMobileOpen(false), [location.pathname]);
  const nav = [
    { to: "/games", label: t("common.games"), icon: "grid" as const },
    { to: "/sessions", label: t("common.sessions"), icon: "gamepad" as const },
    { to: "/saves", label: t("common.saves"), icon: "save" as const },
  ];
  return <div className="app-shell">
    <header className="mobile-header"><Logo/><button className="icon-button" onClick={() => setMobileOpen(!mobileOpen)} aria-label={t("common.openNavigation")}><Icon name="menu"/></button></header>
    <aside className={`sidebar ${mobileOpen ? "is-open" : ""}`}>
      <Logo/>
      <nav className="main-nav" aria-label={t("common.mainNavigation")}>
        <p className="nav-caption">{t("chrome.play")}</p>
        {nav.map((item) => <NavLink key={item.to} to={item.to} className={({ isActive }) => isActive ? "active" : ""}><Icon name={item.icon}/><span>{item.label}</span></NavLink>)}
        {user?.role === "ADMIN" && <><p className="nav-caption second">{t("chrome.system")}</p>
        <NavLink to="/admin" className={({ isActive }) => isActive ? "active" : ""}><Icon name="server"/><span>{t("common.runtime")}</span></NavLink>
        <NavLink to="/admin/users" className={({ isActive }) => isActive ? "active" : ""}><Icon name="user"/><span>{t("common.users")}</span></NavLink></>}
        <NavLink to="/settings" className={({ isActive }) => isActive ? "active" : ""}><Icon name="settings"/><span>{t("common.settings")}</span></NavLink>
      </nav>
      <div className="sidebar-foot">
        <div className="instance-label"><span className="pulse-dot"/>{t("common.instanceRunning")}</div>
        <button className="profile-button" onClick={() => void logout().finally(() => navigate("/login", { replace: true }))}><span className="avatar">{user?.username.slice(0, 1) ?? "?"}</span><span><strong>{user?.username}</strong><small>{user?.role === "ADMIN" ? t("common.admin") : t("common.playerAccount")} · {t("common.logout")}</small></span><Icon name="more"/></button>
      </div>
    </aside>
    {mobileOpen && <button className="sidebar-scrim" aria-label={t("common.closeNavigation")} onClick={() => setMobileOpen(false)}/>}
    <main className="main-content">{children}</main>
  </div>;
}

function PageHeader({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description: string; actions?: ReactNode }) {
  return <header className="page-header"><div>{eyebrow && <p className="eyebrow">{eyebrow}</p>}<h1>{title}</h1><p>{description}</p></div>{actions && <div className="page-actions">{actions}</div>}</header>;
}

const coverPalette = ["coral", "violet", "amber", "blue", "green"] as const;
function coverColor(name: string): string {
  let hash = 0;
  for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) >>> 0;
  return coverPalette[hash % coverPalette.length];
}

function actionErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    const keys = { ACTIVATION_VALIDATION_FAILED: "gameDetail.validationBlocked", GAME_VALIDATION_FAILED: "gameDetail.validationFailed", VALIDATION_IN_PROGRESS: "gameDetail.validating", ACTIVATION_IN_PROGRESS: "gameDetail.activating", GAME_STATE_CONFLICT: "gameDetail.conflict" } as const;
    const key = keys[err.code as keyof typeof keys];
    if (key) return i18n.t(key);
  }
  return err instanceof Error ? err.message : i18n.t("errors.generic");
}

function GamesPage() {
  const { t } = useTranslation();
  const [query, setQuery] = useState("");
  const [visibility, setVisibility] = useState<"ALL" | "MINE" | "SHARED">("ALL");
  const [items, setItems] = useState<GameLibraryItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [uploadOpen, setUploadOpen] = useState(false);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setItems(await listGames());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : t("games.loadFailed"));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const filtered = items.filter(game => {
    const matchesVisibility = visibility === "ALL" || (visibility === "MINE" ? game.visibility === "PRIVATE" : game.visibility === "SERVER_SHARED");
    const q = query.trim().toLowerCase();
    return matchesVisibility && (!q || game.name.toLowerCase().includes(q));
  });
  const currentCount = items.filter(game => game.hasCurrentContent).length;
  const sharedCount = items.filter(game => game.visibility === "SERVER_SHARED").length;

  return <>
    <PageHeader eyebrow="LIBRARY" title={t("games.title")} description={t("games.description")}
      actions={<button className="primary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>{t("games.upload")}</button>}/>
    <section className="summary-strip" aria-label={t("games.overview")}>
      <div><span className="summary-icon peach"><Icon name="book"/></span><p><strong>{items.length}</strong><small>{t("games.gameCount")}</small></p></div>
      <div><span className="summary-icon mint"><Icon name="archive"/></span><p><strong>{currentCount}</strong><small>{t("games.currentCount")}</small></p></div>
      <div><span className="summary-icon blue"><Icon name="grid"/></span><p><strong>{sharedCount}</strong><small>{t("games.sharedCount")}</small></p></div>
      <div className="tip"><Icon name="spark"/><p><strong>{t("games.uploadTip")}</strong><small>{t("games.uploadTipDetail")}</small></p></div>
    </section>
    <div className="toolbar">
      <label className="search-box"><Icon name="search"/><span className="sr-only">{t("games.search")}</span><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder={`${t("games.search")}…`}/></label>
      <div className="segment-control" aria-label={t("games.filter")}>{(["ALL", "MINE", "SHARED"] as const).map(item => <button className={visibility === item ? "selected" : ""} onClick={() => setVisibility(item)} key={item}>{t(item === "ALL" ? "games.all" : item === "MINE" ? "games.mine" : "games.shared")}</button>)}</div>
    </div>
    {loading ? <div className="panel loading-panel" aria-busy="true"><span className="mini-spinner"/><p>{t("games.loading")}</p></div>
      : error ? <div className="panel error-panel" role="alert"><Icon name="warning"/><div><strong>{t("games.loadFailed")}</strong><p>{error}</p></div><button className="secondary-button" onClick={() => void refresh()}>{t("common.retry")}</button></div>
      : filtered.length === 0 ? <section className="empty-state"><span className="empty-icon"><Icon name="book" size={26}/></span><h2>{items.length === 0 ? t("games.empty") : t("games.noMatch")}</h2><p>{items.length === 0 ? t("games.emptyDescription") : t("games.noMatchDescription")}</p><div className="empty-actions">{items.length === 0 && <button className="primary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>{t("games.upload")}</button>}</div></section>
      : <section className="game-grid" aria-label={t("games.list")}>
          {filtered.map((game) => <article className="game-card" key={game.id}>
            <div className={`game-cover ${coverColor(game.name)}`}><span className="cover-grid"/><h2 className="cover-title" title={game.name}>{game.name}</h2><span className="cover-digest">{game.hasCurrentContent ? shortDigest(game.contentDigest) : t("games.noCurrent")}</span></div>
            <div className="game-card-body">
              <div className="card-title-row"><p className="card-visibility">{game.visibility === "SERVER_SHARED" ? t("games.shared") : t("games.private")}</p><button className="icon-button subtle" aria-label={game.name}><Icon name="more"/></button></div>
              <div className="tag-row">
                {game.hasCurrentContent ? <span className="tag success"><Icon name="check" size={13}/>{t("games.current")}</span> : <span className="tag">{t("games.noCurrent")}</span>}
                {game.workspaceStatus === "DRAFT" && <span className="tag waiting">{t("games.draft")}</span>}
                {game.status === "BLOCKED" && <span className="tag warning"><Icon name="warning" size={13}/>{t("games.blocked")}</span>}
              </div>
              <div className="card-meta"><span>{t("games.updated", { date: formatDateTime(game.updatedAt) })}</span><span>{t("games.activations", { count: game.contentRevision })}</span></div>
              <div className="card-actions">
                <Link className="secondary-button" to={`/games/${game.id}`}>{t("games.manage")}</Link>
                {game.hasCurrentContent && game.status === "ACTIVE"
                  ? <Link className="play-button" to={`/sessions/new?game=${game.id}`}><Icon name="play" size={17}/>{t("games.start")}</Link>
                  : <button className="play-button" disabled title={t("games.unavailable")}>{t("games.start")}</button>}
              </div>
            </div>
          </article>)}
          <button className="game-card add-card" onClick={() => setUploadOpen(true)}><span><Icon name="upload" size={24}/></span><strong>{t("games.uploadNew")}</strong><small>{t("games.enabledHint")}</small></button>
        </section>}
    {uploadOpen && (
      <UploadDialog onClose={() => {
        setUploadOpen(false);
        void refresh();
      }}/>
    )}
  </>;
}

function UploadDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const [step, setStep] = useState<"choose" | "uploading" | "done" | "error">("choose");
  const [file, setFile] = useState<File | null>(null);
  const [gameName, setGameName] = useState("");
  const [visibility, setVisibility] = useState<GameVisibility>("PRIVATE");
  const [error, setError] = useState("");
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [createdGame, setCreatedGame] = useState<GameLibraryItem | null>(null);
  const [uploadPercent, setUploadPercent] = useState<number | null>(null);
  const [uploadRequestId, setUploadRequestId] = useState<string | null>(null);
  const [uploadProgress, setUploadProgress] = useState<GameUploadProgress | null>(null);
  const [uploadFailed, setUploadFailed] = useState(false);
  const [uploadController, setUploadController] = useState<AbortController | null>(null);

  useEffect(() => {
    if (step !== "uploading" || !uploadRequestId) return;
    let disposed = false;
    let timer: number | undefined;
    const poll = async () => {
      while (!disposed) {
        try {
          const current = await getGameUploadProgress(uploadRequestId);
          if (disposed) return;
          setUploadProgress(current);
          if (current.status === "COMMITTED" || current.status === "FAILED") return;
        } catch (error) {
          // The operation row is created just after the request starts. A 404
          // during that small window is expected; the upload response remains
          // the source of truth for the final result.
          if (!(error instanceof ApiError && error.status === 404)) return;
        }
        await new Promise<void>(resolve => { timer = window.setTimeout(resolve, 500); });
      }
    };
    void poll();
    return () => {
      disposed = true;
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [step, uploadRequestId]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!file || !gameName.trim()) return;
    setStep("uploading"); setError(""); setErrorCode(null); setUploadPercent(0); setUploadProgress(null); setUploadFailed(false);
    const controller = new AbortController();
    setUploadController(controller);
    try {
      setCreatedGame(await uploadGame(gameName.trim(), visibility, file, {
        signal: controller.signal,
        onRequestId: setUploadRequestId,
        onUploadProgress: (loaded, total) => setUploadPercent(total > 0 ? Math.min(100, Math.round((loaded / total) * 100)) : null),
      }));
      setStep("done");
    } catch (err) {
      setUploadFailed(true);
      setErrorCode(err instanceof ApiError ? err.code : null);
      setError(err instanceof ApiError ? err.message : t("gameDetail.uploadNetwork"));
      setStep("error");
    } finally { setUploadController(null); }
  };

  return <div className="modal-layer" role="presentation"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="upload-title">
    <button className="icon-button modal-close" onClick={() => { uploadController?.abort(); onClose(); }} aria-label={t("common.close")}><Icon name="close"/></button>
    <p className="eyebrow">{t("chrome.newGame")}</p><h2 id="upload-title">{t("upload.title")}</h2><p className="modal-intro">{t("upload.intro")}</p>
    {step === "choose" && <form className="form-panel modal-form" onSubmit={submit}>
      <label><span>{t("upload.zip")}</span><input type="file" accept=".zip,application/zip" onChange={(event) => { const selected = event.target.files?.[0] ?? null; setFile(selected); if (selected && !gameName) setGameName(selected.name.replace(/\.zip$/i, "")); }} required/></label>
      <label><span>{t("upload.name")}</span><input value={gameName} onChange={(event) => setGameName(event.target.value)} placeholder={t("chrome.nameExample")} required/></label>
      <label><span>{t("upload.visibility")}</span><select value={visibility} onChange={(event) => setVisibility(event.target.value as GameVisibility)}><option value="PRIVATE">{t("upload.private")}</option><option value="SERVER_SHARED">{t("upload.shared")}</option></select></label>
      <div className="modal-note"><Icon name="warning"/><p><strong>{t("upload.rights")}</strong><small>{t("upload.rightsDetail")}</small></p></div>
      <div className="form-actions"><button className="secondary-button" type="button" onClick={onClose}>{t("common.cancel")}</button><button className="primary-button" disabled={!file || !gameName.trim()}>{t("upload.submit")}</button></div>
    </form>}
    {step === "uploading" && <div className="scan-state upload-scan-state"><span className="spinner"/><h3>{t("upload.processing", { name: file?.name ?? "ZIP" })}</h3><p>{uploadProgress ? uploadStageDescription(uploadProgress.stage, uploadProgress.currentItem) : uploadPercent !== null && uploadPercent < 100 ? t("upload.uploading") : t("upload.received")}</p><div className="upload-progress-meter">{uploadPercent !== null && uploadPercent < 100 ? <><div className="upload-progress-heading"><span>{t("upload.progress")}</span><strong>{uploadPercent}%</strong></div><progress max={100} value={uploadPercent} aria-label={t("upload.progress")}/></> : <><div className="upload-progress-heading"><span>{t("upload.serverProgress")}</span><small>{t("upload.noPercent")}</small></div><div className="progress indeterminate"><span/></div></>}</div><UploadTaskList fileName={file?.name ?? null} progress={uploadProgress} uploadPercent={uploadPercent} failed={false}/></div>}
    {step === "done" && createdGame && <div className="scan-state done"><span className="success-ring"><Icon name="check" size={30}/></span><h3>{t("upload.done")}</h3><p>{t("upload.doneDetail")}</p><Link className="primary-button" to={`/games/${createdGame.id}`} onClick={onClose}>{t("upload.view")}<Icon name="arrow"/></Link></div>}
    {step === "error" && <div className="scan-state error upload-error-state"><span className="error-ring"><Icon name="warning" size={30}/></span><h3>{t("upload.failed")}</h3><p>{error}</p>{errorCode && <code className="error-code">{errorCode}</code>}<UploadTaskList fileName={file?.name ?? null} progress={uploadProgress} uploadPercent={uploadPercent} failed={uploadFailed}/><button className="secondary-button" onClick={() => { setError(""); setErrorCode(null); setUploadProgress(null); setUploadPercent(null); setUploadRequestId(null); setUploadFailed(false); setStep("choose"); }}>{t("upload.modify")}</button></div>}
  </section></div>;
}

const uploadTaskDefinitions = ["RECEIVING", "INSPECTING_ARCHIVE", "EXTRACTING", "NORMALIZING_ENCODING", "ANALYZING", "CONSUMING_STAGING", "COPYING_CONTENT", "VALIDATING_CONTENT", "RUNNING_VALIDATOR", "PUBLISHING_CONTENT"] as const;

function uploadStageDescription(stage: string, currentItem: string | null): string {
  const label = uploadTaskDefinitions.includes(stage as typeof uploadTaskDefinitions[number]) ? i18n.t(`upload.stage.${stage as typeof uploadTaskDefinitions[number]}`) : i18n.t("upload.stage.fallback");
  return currentItem ? `${label}：${currentItem}` : `${label}…`;
}

function UploadTaskList({ fileName, progress, uploadPercent, failed }: { fileName: string | null; progress: GameUploadProgress | null; uploadPercent: number | null; failed: boolean }) {
  const { t } = useTranslation();
  const currentIndex = progress?.stage === "COMPLETED" ? uploadTaskDefinitions.length : Math.max(0, uploadTaskDefinitions.findIndex(key => key === progress?.stage));
  const serverFailed = progress?.status === "FAILED";
  return <ol className="upload-task-list" aria-label={t("upload.steps")}>
    {uploadTaskDefinitions.map((key, index) => {
      const label = t(`upload.stage.${key}`);
      const isFailed = (serverFailed && index === currentIndex) || (failed && progress?.status !== "COMMITTED" && index === currentIndex);
      const isComplete = !isFailed && (progress?.status === "COMMITTED" || index < currentIndex || (index === 0 && uploadPercent === 100 && currentIndex > 0));
      const state = isFailed ? "failed" : isComplete ? "complete" : index === currentIndex ? "active" : "pending";
      const item = index === 0 ? fileName : progress?.currentItem;
      return <li className={`upload-task ${state}`} key={key}><span className="upload-task-mark" aria-hidden="true">{state === "complete" ? "✓" : state === "failed" ? "×" : state === "active" ? <span className="mini-spinner"/> : ""}</span><span><strong>{label}</strong>{state === "active" && item && <small>{item}</small>}{state === "failed" && progress?.errorCode && <small>{progress.errorCode}</small>}</span></li>;
    })}
  </ol>;
}

function EditGameDialog({ game, onClose, onSaved }: { game: GameLibraryItem; onClose: () => void; onSaved: () => void }) {
  const { t } = useTranslation();
  const [name, setName] = useState(game.name);
  const [visibility, setVisibility] = useState<GameVisibility>(game.visibility);
  const [error, setError] = useState("");
  const [pending, setPending] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) { setError(t("gameDetail.nameRequired")); return; }
    setError(""); setPending(true);
    try { await updateGame(game.id, game.stateVersion, { name: trimmed, visibility }); onSaved(); }
    catch (err) { setError(err instanceof Error ? err.message : t("gameDetail.saveFailed")); setPending(false); }
  };
  return <div className="modal-layer"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="edit-game-title">
    <button className="icon-button modal-close" onClick={onClose} aria-label={t("common.close")}><Icon name="close"/></button>
    <p className="eyebrow">{t("chrome.editGame")}</p><h2 id="edit-game-title">{t("gameDetail.editTitle")}</h2><p className="modal-intro">{t("gameDetail.editIntro")}</p>
    <form className="form-panel modal-form" onSubmit={submit}>
      <label><span>{t("upload.name")}</span><input value={name} onChange={(e) => setName(e.target.value)} required/></label>
      <label><span>{t("upload.visibility")}</span><select value={visibility} onChange={(e) => setVisibility(e.target.value as GameVisibility)}><option value="PRIVATE">{t("upload.private")}</option><option value="SERVER_SHARED">{t("upload.shared")}</option></select></label>
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="form-actions"><button className="secondary-button" type="button" onClick={onClose}>{t("common.cancel")}</button><button className="primary-button" disabled={pending}>{pending ? t("common.saving") : t("common.save")}</button></div>
    </form>
  </section></div>;
}

function GameDetailPage() {
  const { t } = useTranslation();
  const { gameId = "" } = useParams();
  const { user } = useAuth();
  const navigate = useNavigate();
  const [game, setGame] = useState<GameLibraryItem | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState<"content" | "files" | "compatibility">("content");
  const [busy, setBusy] = useState("");
  const [actionError, setActionError] = useState<string | null>(null);
  const [diagnostics, setDiagnostics] = useState<GameDiagnosticItem[]>([]);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [editOpen, setEditOpen] = useState(false);

  const refresh = useCallback(async () => {
    try { setGame(await getGame(gameId)); setLoadError(null); }
    catch (err) { setLoadError(err instanceof Error ? err.message : t("gameDetail.loadFailed")); }
    finally { setLoading(false); }
  }, [gameId, t]);

  const loadDiagnostics = useCallback(async () => {
    try { setDiagnostics(await listDiagnostics(gameId)); }
    catch { /* diagnostics are best-effort; the tab falls back to the in-session result */ }
  }, [gameId]);

  useEffect(() => { void refresh(); void loadDiagnostics(); }, [refresh, loadDiagnostics]);

  const run = useCallback(async (label: string, action: () => Promise<GameLibraryItem | void>) => {
    setBusy(label); setActionError(null);
    try {
      await action();
      await refresh();
      await loadDiagnostics();
    } catch (err) {
      setActionError(actionErrorMessage(err));
      // Failed actions still bump the server-side state version (the operation
      // transition is durable); refresh so a retry never fails with a stale
      // version, and surface persisted diagnostics for validation failures.
      await refresh();
      if (err instanceof ApiError && ["ACTIVATION_VALIDATION_FAILED", "GAME_VALIDATION_FAILED", "VALIDATION_IN_PROGRESS"].includes(err.code)) {
        await loadDiagnostics();
      }
    } finally { setBusy(""); }
  }, [refresh, loadDiagnostics]);

  const deleteGameAction = async () => {
    if (!game) return;
    setBusy(t("gameDetail.deleting")); setActionError(null);
    try { await deleteGame(game.id, game.stateVersion); navigate("/games", { replace: true }); }
    catch (err) { setActionError(actionErrorMessage(err)); setBusy(""); }
  };

  if (loading || (!game && !loadError)) return <>
    <div className="backline"><Link to="/games">← {t("gameDetail.back")}</Link></div>
    <div className="panel loading-panel" aria-busy="true"><span className="mini-spinner"/><p>{t("gameDetail.loading")}</p></div>
  </>;
  if (loadError || !game) return <>
    <div className="backline"><Link to="/games">← {t("gameDetail.back")}</Link></div>
    <div className="panel error-panel" role="alert"><Icon name="warning"/><div><strong>{t("gameDetail.loadFailed")}</strong><p>{loadError ?? t("gameDetail.notFound")}</p></div><button className="secondary-button" onClick={() => { setLoading(true); void refresh(); }}>{t("common.retry")}</button></div>
  </>;

  const hasDraft = game.workspaceStatus === "DRAFT";
  const canPlay = game.hasCurrentContent && game.status === "ACTIVE";
  const displayDiagnostics: Array<{ id: string; code: string; severity: string; path: string | null; message: string; activationBlocking: boolean }> =
    diagnostics;
  const blockingCount = displayDiagnostics.filter(diagnostic => diagnostic.activationBlocking).length;

  return <>
    <div className="backline"><Link to="/games">← {t("gameDetail.back")}</Link></div>
    <section className="game-detail-hero">
      <div className={`game-cover compact ${coverColor(game.name)}`}><span className="cover-grid"/><span className="cover-glyph">{game.name.slice(0, 1).toUpperCase()}</span></div>
      <div>
        <p className="eyebrow">{game.visibility === "PRIVATE" ? "PRIVATE GAME" : "SERVER SHARED GAME"}</p>
        <h1>{game.name}</h1>
        <p>{t(game.status === "BLOCKED" ? "gameDetail.blockedDescription" : "gameDetail.activeDescription")}</p>
        <div className="tag-row">
          {game.status === "BLOCKED" && <span className="tag warning"><Icon name="warning" size={13}/>{t("games.blocked")}</span>}
          {game.hasCurrentContent ? <span className="tag success"><Icon name="check" size={13}/>{t("gameDetail.runnable")}</span> : <span className="tag">{t("games.noCurrent")}</span>}
          {hasDraft && <span className="tag waiting">{t("gameDetail.workspace")}</span>}
          {game.contentDigest && <span className="tag">sha256:{shortDigest(game.contentDigest)}</span>}
        </div>
      </div>
      <div className="hero-actions">
        {canPlay ? <Link className="play-button" to={`/sessions/new?game=${game.id}`}><Icon name="play"/>{t("gameDetail.createSession")}</Link> : <button className="play-button" disabled title={t("games.unavailable")}><Icon name="play"/>{t("gameDetail.createSession")}</button>}
        <button className="secondary-button" onClick={() => setEditOpen(true)} disabled={busy !== ""}><Icon name="settings"/>{t("gameDetail.edit")}</button>
        {user?.role === "ADMIN" && <button className="secondary-button" onClick={() => void run(t(game.status === "BLOCKED" ? "gameDetail.unblock" : "gameDetail.block"), () => setGameBlocked(game.id, game.stateVersion, game.status !== "BLOCKED"))} disabled={busy !== ""}>{t(game.status === "BLOCKED" ? "gameDetail.unblock" : "gameDetail.block")}</button>}
        <button className="danger-button" onClick={() => setConfirmDelete(true)} disabled={busy !== ""}>{t("gameDetail.delete")}</button>
      </div>
    </section>
    <div className="detail-tabs">{(["content", "files", "compatibility"] as const).map(item => <button className={tab === item ? "active" : ""} onClick={() => setTab(item)} key={item}>{t(`gameDetail.tabs.${item}`)}</button>)}</div>
    {actionError && <div className="error-banner" role="alert"><Icon name="warning"/><span>{actionError}{blockingCount > 0 && <small> · {t("gameDetail.blockingCount", { count: blockingCount })}</small>}</span></div>}
    {busy && <div className="busy-banner" role="status"><span className="mini-spinner"/><span>{busy}…</span></div>}

    {tab === "content" && <section className="panel">
      <div className="panel-heading">
        <div><h2>{t("gameDetail.current")}</h2><p>{t("gameDetail.currentIntro")}</p></div>
      </div>
      <div className="content-list">
        <article><span className="timeline-dot live"/><div>
          <h3>{game.hasCurrentContent ? `sha256:${shortDigest(game.contentDigest)}` : t("gameDetail.noEnabled")}{game.hasCurrentContent && <span className="tag success">{t("gameDetail.current")}</span>}</h3>
          <p>{t("gameDetail.revision", { revision: game.contentRevision, date: formatDateTime(game.updatedAt) })}</p>
          <small>{t(game.hasCurrentContent ? "gameDetail.snapshot" : "gameDetail.legacyNoContent")}</small>
        </div><button className="text-button" onClick={() => setTab("files")}>{t("gameDetail.viewFiles")} <Icon name="arrow"/></button></article>
      </div>
      {hasDraft && <div className="content-list">
        <article><span className="timeline-dot"/><div>
          <h3>{t("gameDetail.draft")} <span className="tag waiting">{t("gameDetail.pending")}</span></h3>
          <p>{t("gameDetail.legacyDraft")}</p>
          <small>{t("gameDetail.reupload")}</small>
        </div></article>
      </div>}
      {!game.hasCurrentContent && !hasDraft && <div className="content-list empty-list"><article><span className="timeline-dot"/><div><h3>{t("gameDetail.noContent")}</h3><p>{t("gameDetail.reupload")}</p></div></article></div>}
      {displayDiagnostics.length > 0 && <div className={`validation-banner ${blockingCount === 0 ? "ok" : "bad"}`}><Icon name={blockingCount === 0 ? "check" : "warning"}/><p><strong>{t(blockingCount === 0 ? "gameDetail.loadComplete" : "gameDetail.diagnosticsExist")}</strong><small>{t("gameDetail.runtimeDiagnostics", { count: displayDiagnostics.length })}</small></p><button className="text-button" onClick={() => setTab("compatibility")}>{t("gameDetail.details")} <Icon name="arrow"/></button></div>}
    </section>}

    {tab === "files" && <GameFilesPanel game={game}/>}

    {tab === "compatibility" && <section className="panel diagnostics">
      {displayDiagnostics.length === 0 ? <div className="diagnostic-empty"><Icon name="spark" size={26}/><h2>{t("gameDetail.loadPassed")}</h2><p>{t("gameDetail.noDiagnosticsDetail")}</p></div>
        : <>
          <div className="diagnostic-summary">
            <span className={`score ${blockingCount === 0 ? "ok" : "bad"}`}>{blockingCount === 0 ? "✓" : "!"}</span>
            <div><h2>{t(blockingCount === 0 ? "gameDetail.loadPassed" : "gameDetail.diagnosticsExist")}</h2><p>{t("gameDetail.diagnostics", { count: displayDiagnostics.length })}</p></div>
          </div>
          {displayDiagnostics.length === 0 ? <p className="diagnostic-none">{t("gameDetail.none")}</p>
            : displayDiagnostics.map(diagnostic => <div className={`diagnostic-row ${diagnostic.activationBlocking ? "blocking" : ""}`} key={diagnostic.id}><Icon name={diagnostic.activationBlocking ? "warning" : "check"}/><span><strong>{diagnostic.code}</strong>{diagnostic.path && <small> · {diagnostic.path}</small>}<p>{diagnostic.message}</p></span><small>{diagnostic.severity}</small></div>)}
        </>}
    </section>}

    {editOpen && <EditGameDialog game={game} onClose={() => setEditOpen(false)} onSaved={() => { setEditOpen(false); void refresh(); }}/>}
    {confirmDelete && <ConfirmDialog title={t("gameDetail.deleteTitle")} body={t("gameDetail.deleteBody")} confirm={t("gameDetail.confirmDelete")} onCancel={() => setConfirmDelete(false)} onConfirm={() => void deleteGameAction()} pending={busy === t("gameDetail.deleting")}/>}
  </>;
}

function GameFilesPanel({ game }: { game: GameLibraryItem }) {
  const { t } = useTranslation();
  const [scope, setScope] = useState<ContentScope>(game.workspaceStatus === "DRAFT" ? "WORKSPACE" : "CURRENT");
  const [path, setPath] = useState("");
  const [files, setFiles] = useState<GameFileItem[] | null>(null);
  const [filesError, setFilesError] = useState<string | null>(null);
  const [selected, setSelected] = useState<GameTextFile | null>(null);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [readError, setReadError] = useState<string | null>(null);

  const workspaceAvailable = game.workspaceStatus === "DRAFT";
  const currentAvailable = game.hasCurrentContent;
  const loadFiles = useCallback(async (targetScope: ContentScope, targetPath: string) => {
    setFilesError(null);
    try { setFiles(await listFiles(game.id, targetScope, targetPath)); }
    catch (err) { setFilesError(err instanceof Error ? err.message : t("gameDetail.fileListFailed")); setFiles([]); }
  }, [game.id, t]);

  useEffect(() => {
    setPath(""); setSelected(null); setSelectedPath(null); setReadError(null);
    const available = scope === "WORKSPACE" ? workspaceAvailable : currentAvailable;
    if (available) void loadFiles(scope, "");
  }, [scope, loadFiles, workspaceAvailable, currentAvailable]);

  const openFile = async (filePath: string) => {
    setSelectedPath(filePath); setSelected(null); setReadError(null);
    try { setSelected(await readTextFile(game.id, scope, filePath)); }
    catch (err) { setReadError(err instanceof Error ? err.message : t("gameDetail.fileFailed")); }
  };

  const segments = path ? path.split("/") : [];
  const goTo = (index: number) => {
    const target = segments.slice(0, index + 1).join("/");
    setPath(target); void loadFiles(scope, target);
  };
  const goUp = () => {
    const parent = segments.slice(0, -1).join("/");
    setPath(parent); void loadFiles(scope, parent);
  };

  return <div className="file-workspace">
    <div className="file-toolbar">
      <div className="segment-control" aria-label={t("gameDetail.fileScope")}>
        <button className={scope === "WORKSPACE" ? "selected" : ""} disabled={!workspaceAvailable} onClick={() => setScope("WORKSPACE")}>{t("gameDetail.workspace")}</button>
        <button className={scope === "CURRENT" ? "selected" : ""} disabled={!currentAvailable} onClick={() => setScope("CURRENT")}>{t("gameDetail.current")}</button>
      </div>
      <span className="readonly-note">{t("gameDetail.readonly")}</span>
    </div>
    {!workspaceAvailable && !currentAvailable
      ? <div className="panel empty-list-panel"><Icon name="folder" size={26}/><p>{t("gameDetail.noBrowsable")}</p></div>
      : <section className="panel file-panel">
        <div className="file-tree">
          <h3>{t(scope === "WORKSPACE" ? "gameDetail.workspace" : "gameDetail.current")}{path ? ` / ${path}` : ""}</h3>
          <div className="file-breadcrumb">
            <button className={path === "" ? "current" : ""} onClick={() => { setPath(""); void loadFiles(scope, ""); }}>{t("gameDetail.root")}</button>
            {segments.map((segment, index) => <button key={index} onClick={() => goTo(index)}>{segment}</button>)}
          </div>
          {path !== "" && <button className="file-row up" onClick={goUp}><Icon name="arrow" size={15}/>{t("gameDetail.up")}</button>}
          {filesError && <p className="file-error" role="alert">{filesError}</p>}
          {files?.map(item => item.isDirectory
            ? <button className="file-row" key={item.path} onClick={() => { setPath(item.path); void loadFiles(scope, item.path); }}><Icon name="folder"/><span>{item.path.split("/").pop()}</span><span className="file-meta">{t("gameDetail.directory")}</span></button>
            : <div className="file-row-wrap" key={item.path}><button className={`file-row ${selectedPath === item.path ? "selected" : ""}`} onClick={() => void openFile(item.path)}><Icon name="book"/><span>{item.path.split("/").pop()}</span><span className="file-meta">{formatBytes(item.bytes)}</span></button><a className="file-download" href={downloadFileUrl(game.id, scope, item.path)} download aria-label={t("gameDetail.download", { path: item.path })}><Icon name="download" size={15}/></a></div>)}
          {files && files.length === 0 && !filesError && <p className="file-empty">{t("gameDetail.emptyDirectory")}</p>}
        </div>
        <div className="file-viewer">
          {selected ? <div className="file-viewer-bar"><span>{selected.path}</span><span>{selected.encoding}{selected.hasBom ? " BOM" : ""} · {formatBytes(selected.bytes)} · {t("gameDetail.readOnly")}</span></div>
            : readError ? <div className="file-viewer-error" role="alert"><Icon name="warning"/><p>{readError}</p></div>
            : <div className="file-viewer-empty"><Icon name="book" size={26}/><p>{t("gameDetail.selectFile")}</p><small>{t("gameDetail.immutableFile")}</small></div>}
          {selected && (
            <textarea className="file-viewer-content" value={selected.content} readOnly spellCheck={false} aria-label={t("gameDetail.view", { path: selected.path })}/>
          )}
          {readError && selected && <p className="file-error" role="alert">{readError}</p>}
        </div>
      </section>}
  </div>;
}

function Status({ state }: { state: string }) {
  const normalized = state === "RUNNING" || state === "STARTING" || state === "STOPPING" || state === "CRASHED" ? state : "CLOSED";
  const label = i18n.t(`sessions.state.${normalized}`);
  return <span className={`status-pill ${state.toLowerCase()}`}><i/>{label}</span>;
}

function AdminPage() {
  const { t } = useTranslation();
  const [runtime, setRuntime] = useState<AdminRuntimeResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selected, setSelected] = useState<AdminWorker | null>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    try {
      setRuntime(await getAdminRuntime());
      setError("");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("adminRuntime.loadFailed"));
    } finally {
      setLoading(false);
    }
  }, [t]);
  useEffect(() => {
    let timer: number | undefined;
    const stopPolling = () => {
      if (timer !== undefined) {
        window.clearInterval(timer);
        timer = undefined;
      }
    };
    const startPolling = () => {
      if (document.hidden) return;
      void refresh();
      timer = window.setInterval(() => {
        if (!document.hidden) void refresh();
      }, 5000);
    };
    const onVisibilityChange = () => {
      stopPolling();
      if (!document.hidden) startPolling();
    };
    startPolling();
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      stopPolling();
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [refresh]);

  const submitForceStop = async (event: FormEvent) => {
    event.preventDefault();
    if (!selected || reason.trim().length === 0) return;
    setBusy(true); setError("");
    try {
      await forceStopSession(selected.session.id, reason.trim());
      setSelected(null); setReason("");
      await refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : t("adminRuntime.stopFailed"));
    } finally { setBusy(false); }
  };

  const state = runtime?.instance.controlPlaneState ?? (loading ? "STARTING" : "NOT_READY");
  return <>
    <PageHeader eyebrow="SYSTEM" title={t("adminRuntime.title")} description={t("adminRuntime.description")} actions={<span className="updated"><i/>{runtime ? t("adminRuntime.sampled", { date: formatDateTime(runtime.observedAt) }) : t("adminRuntime.waiting")}</span>}/>
    {error && <p className="form-error" role="alert">{error}</p>}
    <section className="health-grid">
      <article><span className="summary-icon mint"><Icon name="server"/></span><div><p>{t("adminRuntime.control")}</p><strong>{state}</strong><small>{t("adminRuntime.controlDetail")}</small></div></article>
      <article><span className="summary-icon mint"><Icon name="settings"/></span><div><p>{t("adminRuntime.activeWorkers")}</p><strong>{runtime?.instance.activeWorkerCount ?? "—"}</strong><small>{t("adminRuntime.subscriptions", { count: runtime?.instance.subscriptionCount ?? 0 })}</small></div></article>
      <article><span className="summary-icon blue"><Icon name="gamepad"/></span><div><p>WebSocket</p><strong>{runtime?.instance.webSocketConnectionCount ?? "—"}</strong><small>{t("adminRuntime.connections")}</small></div></article>
      <article><span className="summary-icon peach"><Icon name="archive"/></span><div><p>{t("adminRuntime.failures")}</p><strong>{runtime?.recentFailures.length ?? "—"}</strong><small>{t("adminRuntime.failuresDetail")}</small></div></article>
    </section>
    <section className="panel">
      <div className="panel-heading"><div><h2>{t("adminRuntime.activeWorkers")}</h2><p>{t("adminRuntime.workerIntro")}</p></div></div>
      {loading && !runtime ? <p className="admin-empty" aria-busy="true">{t("adminRuntime.loading")}</p> : <div className="worker-table"><div className="worker-head"><span>{t("chrome.sessionWorker")}</span><span>{t("adminRuntime.state")}</span><span>epoch</span><span>Realtime</span><span>{t("adminRuntime.action")}</span></div>{runtime?.workers.map(worker => <div className="worker-row" key={worker.session.id}><span><strong>{worker.session.name}</strong><small>{worker.session.id} · {worker.session.ownerUsername} · {worker.worker.workerId ?? t("adminRuntime.unregistered")} · PID {worker.worker.pid ?? "—"} · {worker.worker.heartbeatAgeMilliseconds === null ? "—" : t("adminRuntime.heartbeat", { seconds: Math.round(worker.worker.heartbeatAgeMilliseconds / 1000) })}</small></span><span><Status state={worker.session.state}/><small className="worker-consistency">{worker.runtimeConsistency}</small></span><span>{worker.worker.workerEpoch}</span><span><strong>{worker.realtime.hubState}</strong><small>{t("adminRuntime.subscribed", { count: worker.realtime.subscriptionCount })} · {t("chrome.snapshot")} {worker.realtime.snapshotBytes === null ? worker.realtime.snapshotSizeStatus : formatBytes(worker.realtime.snapshotBytes)}</small></span><span>{["STARTING", "RUNNING", "STOPPING"].includes(worker.session.state) ? <button className="text-button danger-text" onClick={() => { setSelected(worker); setReason(""); }}>{t("adminRuntime.forceStop")}</button> : <small>{t("adminRuntime.unavailable")}</small>}</span></div>)}{runtime?.workers.length === 0 && <p className="admin-empty">{t("adminRuntime.noWorkers")}</p>}</div>}
    </section>
    {runtime && runtime.recentFailures.length > 0 && <section className="panel"><div className="panel-heading"><div><h2>{t("adminRuntime.recentFailures")}</h2><p>{t("adminRuntime.recentIntro")}</p></div></div><div className="worker-table"><div className="worker-head"><span>{t("chrome.session")}</span><span>{t("sessions.game")}</span><span>epoch</span><span>{t("adminRuntime.reason")}</span><span>{t("adminRuntime.time")}</span></div>{runtime.recentFailures.map(failure => <div className="worker-row" key={failure.sessionId + "-" + failure.failedAt}><span><strong>{failure.sessionName}</strong><small>{failure.sessionId} · {failure.ownerUsername}</small></span><span>{failure.gameName}</span><span>{failure.workerEpoch}</span><span>{failure.reasonCode}</span><span>{failure.failedAt ? formatDateTime(failure.failedAt) : "—"}</span></div>)}</div></section>}
    {selected && <div className="modal-layer"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="force-stop-title"><button className="icon-button modal-close" aria-label={t("common.close")} onClick={() => setSelected(null)} disabled={busy}><Icon name="close"/></button><h2 id="force-stop-title">{t("adminRuntime.stopTitle", { name: selected.session.name })}</h2><p className="modal-intro">{t("adminRuntime.stopIntro")}</p><form className="form-panel modal-form" onSubmit={submitForceStop}><label><span>{t("adminRuntime.reasonLabel")}</span><textarea value={reason} onChange={event => setReason(event.target.value)} maxLength={500} rows={4} required placeholder={t("adminRuntime.reasonPlaceholder")}/></label><div className="form-actions"><button className="secondary-button" type="button" onClick={() => setSelected(null)} disabled={busy}>{t("common.cancel")}</button><button className="danger-button" disabled={busy || reason.trim().length === 0}>{busy ? t("adminRuntime.processing") : t("adminRuntime.confirmStop")}</button></div></form></section></div>}
  </>;
}

const defaultUserInput: CreateUserInput = { username: "", email: "", temporaryPassword: "", role: "PLAYER" };

function AdminUsersPage() {
  const { t } = useTranslation();
  const { listUsers, createUser, updateUser, resetUserPassword, user: actor } = useAuth();
  const [users, setUsers] = useState<CurrentUser[]>([]);
  const [form, setForm] = useState<CreateUserInput>(defaultUserInput);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<CurrentUser | null>(null);
  const [profile, setProfile] = useState<UpdateUserInput>({});
  const [resetting, setResetting] = useState<CurrentUser | null>(null);
  const [temporaryPassword, setTemporaryPassword] = useState("");

  const refresh = async () => {
    setLoading(true);
    try {
      const loaded = await listUsers();
      setUsers(loaded);
    } catch { setError(t("adminUsers.loadFailed")); }
    finally { setLoading(false); }
  };
  useEffect(() => { void refresh(); }, []);
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError(""); setCreating(true);
    try {
      const created = await createUser(form);
      setUsers(current => [...current, created]);
      setForm(defaultUserInput);
    } catch (cause) { setError(cause instanceof Error ? cause.message : t("adminUsers.createFailed")); }
    finally { setCreating(false); }
  };
  const changeStatus = async (target: CurrentUser) => {
    setError("");
    try {
      const updated = await updateUser(target.id, target.stateVersion, { status: target.status === "ACTIVE" ? "DISABLED" : "ACTIVE" });
      setUsers(current => current.map(item => item.id === updated.id ? updated : item));
    } catch (cause) { setError(cause instanceof Error ? cause.message : t("adminUsers.statusFailed")); }
  };
  const toggleRole = async (target: CurrentUser) => {
    setError("");
    try {
      const updated = await updateUser(target.id, target.stateVersion, { role: target.role === "ADMIN" ? "PLAYER" : "ADMIN" });
      setUsers(current => current.map(item => item.id === updated.id ? updated : item));
    } catch (cause) { setError(cause instanceof Error ? cause.message : t("adminUsers.roleFailed")); }
  };
  const submitReset = async (event: FormEvent) => {
    event.preventDefault(); if (!resetting) return; setError("");
    try { await resetUserPassword(resetting.id, resetting.stateVersion, temporaryPassword); setResetting(null); setTemporaryPassword(""); await refresh(); }
    catch (cause) { setError(cause instanceof Error ? cause.message : t("adminUsers.resetFailed")); }
  };
  const submitProfile = async (event: FormEvent) => {
    event.preventDefault(); if (!editing) return; setError("");
    try {
      const updated = await updateUser(editing.id, editing.stateVersion, profile);
      setUsers(current => current.map(item => item.id === updated.id ? updated : item));
      setEditing(null);
    } catch (cause) { setError(cause instanceof Error ? cause.message : t("adminUsers.profileFailed")); }
  };
  return <>
    <PageHeader eyebrow="IDENTITY" title={t("adminUsers.title")} description={t("adminUsers.description")} />
    {error && <p className="form-error" role="alert">{error}</p>}
    <section className="panel admin-users-panel"><div className="panel-heading"><div><h2>{t("adminUsers.accounts")}</h2><p>{t("adminUsers.intro")}</p></div><span className="tag">{t("adminUsers.count", { count: users.length })}</span></div>
      {loading ? <p className="admin-empty" aria-busy="true">{t("adminUsers.loading")}</p> : <div className="admin-user-table"><div className="admin-user-head"><span>{t("adminUsers.user")}</span><span>{t("adminUsers.role")}</span><span>{t("adminUsers.state")}</span><span>{t("adminUsers.version")}</span><span>{t("adminUsers.action")}</span></div>{users.map(target => <div className="admin-user-row" key={target.id}><span><strong>{target.username}</strong><small>{target.email}</small></span><span><span className={`tag ${target.role === "ADMIN" ? "warning" : "success"}`}>{target.role === "ADMIN" ? t("common.admin") : t("adminUsers.player")}</span></span><span>{target.status === "ACTIVE" ? (target.mustChangePassword ? t("adminUsers.mustChange") : t("adminUsers.enabled")) : t("adminUsers.disabled")}</span><span>#{target.stateVersion}</span><span className="admin-row-actions"><button className="text-button" onClick={() => { setEditing(target); setProfile({ username: target.username, email: target.email }); }}>{t("adminUsers.edit")}</button><button className="text-button" onClick={() => void toggleRole(target)} disabled={target.id === actor?.id}>{t(target.role === "ADMIN" ? "adminUsers.demote" : "adminUsers.promote")}</button><button className="text-button" onClick={() => void changeStatus(target)} disabled={target.id === actor?.id}>{t(target.status === "ACTIVE" ? "adminUsers.disable" : "adminUsers.enable")}</button><button className="text-button" onClick={() => { setResetting(target); setTemporaryPassword(""); }} disabled={target.id === actor?.id}>{t("adminUsers.reset")}</button></span></div>)}</div>}
    </section>
    <section className="form-panel admin-create-form"><div><p className="eyebrow">{t("chrome.newAccount")}</p><h2>{t("adminUsers.createTitle")}</h2><p>{t("adminUsers.createIntro")}</p></div><form onSubmit={submit}><label><span>{t("adminUsers.username")}</span><input value={form.username} onChange={event => setForm({ ...form, username: event.target.value })} autoComplete="off" required /></label><label><span>{t("adminUsers.email")}</span><input value={form.email} onChange={event => setForm({ ...form, email: event.target.value })} type="email" autoComplete="off" required /></label><label><span>{t("adminUsers.temporaryPassword")}</span><input value={form.temporaryPassword} onChange={event => setForm({ ...form, temporaryPassword: event.target.value })} type="password" autoComplete="new-password" minLength={8} required /></label><label><span>{t("adminUsers.role")}</span><select value={form.role} onChange={event => setForm({ ...form, role: event.target.value as CurrentUser["role"] })}><option value="PLAYER">{t("adminUsers.player")}</option><option value="ADMIN">{t("common.admin")}</option></select></label><div className="form-actions"><button className="primary-button" disabled={creating}>{creating ? t("adminUsers.creating") : t("adminUsers.create")}</button></div></form></section>
    {resetting && <div className="modal-layer"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="reset-password-title"><button className="icon-button modal-close" aria-label={t("common.close")} onClick={() => setResetting(null)}><Icon name="close"/></button><h2 id="reset-password-title">{t("adminUsers.resetTitle", { name: resetting.username })}</h2><p className="modal-intro">{t("adminUsers.resetIntro")}</p><form className="form-panel modal-form" onSubmit={submitReset}><label><span>{t("adminUsers.newTemporaryPassword")}</span><input type="password" value={temporaryPassword} onChange={event => setTemporaryPassword(event.target.value)} autoComplete="new-password" minLength={8} required /></label><div className="form-actions"><button className="secondary-button" type="button" onClick={() => setResetting(null)}>{t("common.cancel")}</button><button className="danger-button">{t("adminUsers.confirmReset")}</button></div></form></section></div>}
    {editing && <div className="modal-layer"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="edit-user-title"><button className="icon-button modal-close" aria-label={t("common.close")} onClick={() => setEditing(null)}><Icon name="close"/></button><h2 id="edit-user-title">{t("adminUsers.editTitle", { name: editing.username })}</h2><p className="modal-intro">{t("adminUsers.editIntro")}</p><form className="form-panel modal-form" onSubmit={submitProfile}><label><span>{t("adminUsers.username")}</span><input value={profile.username ?? ""} onChange={event => setProfile({ ...profile, username: event.target.value })} required /></label><label><span>{t("adminUsers.email")}</span><input value={profile.email ?? ""} onChange={event => setProfile({ ...profile, email: event.target.value })} type="email" required /></label><div className="form-actions"><button className="secondary-button" type="button" onClick={() => setEditing(null)}>{t("common.cancel")}</button><button className="primary-button">{t("adminUsers.saveProfile")}</button></div></form></section></div>}
  </>;
}

function SettingsPage() {
  const { user, updateUiLocale } = useAuth();
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const fonts = useRuntimeFontCatalog();
  const startupDefaults = useSessionStartupDefaults(user?.id);
  const [fontFaceId, setFontFaceId] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.fontFaceId);
  const [fontSize, setFontSize] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.fontSize);
  const [lineHeight, setLineHeight] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.lineHeight);
  const [fontSizeLineHeightMode, setFontSizeLineHeightMode] = useState<SessionFontSizeLineHeightMode>(DEFAULT_SESSION_STARTUP_DEFAULTS.fontSizeLineHeightMode);
  const [widthMode, setWidthMode] = useState<RuntimeWidthMode>(DEFAULT_SESSION_STARTUP_DEFAULTS.widthMode);
  const [customWidth, setCustomWidth] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.customWidth ?? 800);
  const [convertBackslashToYen, setConvertBackslashToYen] = useState(DEFAULT_SESSION_STARTUP_DEFAULTS.convertBackslashToYen);
  const [fontPreviewReady, setFontPreviewReady] = useState(false);
  const [pending, setPending] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [languagePending, setLanguagePending] = useState(false);
  const [languageMessage, setLanguageMessage] = useState<string | null>(null);

  const saveLanguage = async (locale: UiLocale, previous: UiLocale) => {
    setLanguagePending(true); setLanguageMessage(null);
    try { await updateUiLocale(locale); persistDeviceLocale(locale); setLanguageMessage(i18n.t("settings.languageSaved", { lng: locale })); }
    catch { await changeUiLocale(previous); persistDeviceLocale(previous); setLanguageMessage(i18n.t("settings.languageSaveFailed", { lng: previous })); }
    finally { setLanguagePending(false); }
  };

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

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setMessage(null);
    setError(null);
    if (fontSize < 8 || fontSize > 72 || lineHeight < fontSize || lineHeight > 128) {
      setError(t("settingsExtra.invalidMetrics"));
      return;
    }
    setPending(true);
    try {
      const saved = await updateSessionStartupDefaults({ fontFaceId, fontSize, lineHeight, fontSizeLineHeightMode, widthMode, customWidth: widthMode === "CUSTOM" ? customWidth : null, convertBackslashToYen });
      queryClient.setQueryData(sessionStartupDefaultsQueryKey(user?.id), saved);
      setFontFaceId(saved.fontFaceId);
      setFontSize(saved.fontSize);
      setLineHeight(saved.lineHeight);
      setFontSizeLineHeightMode(saved.fontSizeLineHeightMode);
      setWidthMode(saved.widthMode);
      setCustomWidth(saved.customWidth ?? 800);
      setConvertBackslashToYen(saved.convertBackslashToYen);
      setMessage(t("settingsExtra.saved"));
    } catch (cause) {
      setError(cause instanceof ApiError ? cause.message : cause instanceof Error ? cause.message : t("settingsExtra.saveFailed"));
    } finally {
      setPending(false);
    }
  };

  return <><PageHeader eyebrow={t("settings.eyebrow")} title={t("settings.title")} description={t("settings.description")}/><section className="form-panel settings-panel"><h2>{t("settings.languageTitle")}</h2><p className="settings-description">{t("settings.languageDescription")}</p><LanguageSelect disabled={languagePending} onChange={(locale, previous) => void saveLanguage(locale, previous)}/>{languageMessage && <p className="settings-success" role="status">{languageMessage}</p>}</section><form className="form-panel settings-panel" onSubmit={submit}><h2>{t("settings.sessionTitle")}</h2><p className="settings-description">{t("settingsExtra.description")}</p><SessionFontField value={fontFaceId} fonts={fonts.data?.items ?? []} disabled={fonts.isPending || pending} onChange={setFontFaceId} onReadinessChange={setFontPreviewReady}/><SessionDisplayFields fontSize={fontSize} lineHeight={lineHeight} fontSizeLineHeightMode={fontSizeLineHeightMode} setFontSize={setFontSize} setLineHeight={setLineHeight} setFontSizeLineHeightMode={setFontSizeLineHeightMode} disabled={pending}/><SessionWidthFields widthMode={widthMode} customWidth={customWidth} setWidthMode={setWidthMode} setCustomWidth={setCustomWidth} disabled={pending}/><SessionYenCompatibilityField value={convertBackslashToYen} onChange={setConvertBackslashToYen} disabled={pending}/>{startupDefaults.isError && <p className="settings-warning" role="status">{t("settingsExtra.defaultsFailed")}</p>}{error && <p className="form-error" role="alert">{error}</p>}{message && <p className="settings-success" role="status">{message}</p>}<div className="form-actions"><button className="primary-button" disabled={pending || fonts.isPending || fonts.isError || !fonts.data || !fontPreviewReady}>{pending ? t("common.saving") : t("settingsExtra.saveDefaults")}</button></div></form></>;
}

function ConfirmDialog({ title, body, confirm, onCancel, onConfirm, pending = false }: { title: string; body: string; confirm: string; onCancel: () => void; onConfirm?: () => void; pending?: boolean }) {
  const { t } = useTranslation();
  return <div className="modal-layer"><section className="modal confirm-modal" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title"><span className="confirm-icon"><Icon name="warning"/></span><h2 id="confirm-title">{title}</h2><p>{body}</p><div className="form-actions"><button className="secondary-button" onClick={onCancel} disabled={pending}>{t("common.cancel")}</button><button className="danger-button" onClick={onConfirm ?? onCancel} disabled={pending}>{pending ? t("adminRuntime.processing") : confirm}</button></div></section></div>;
}

function LoginPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const { user, login } = useAuth();
  const [email, setEmail] = useState(""); const [password, setPassword] = useState(""); const [rememberMe, setRememberMe] = useState(false); const [error, setError] = useState(""); const [pending, setPending] = useState(false);
  if (user) return <Navigate to={user.mustChangePassword ? "/change-password" : "/games"} replace/>;
  const returnTo = new URLSearchParams(location.search).get("returnTo");
  const safeReturnTo = returnTo && /^\/(?!\/)/.test(returnTo) && !returnTo.includes("\\") ? returnTo : "/games";
  const submit = async (event: FormEvent) => { event.preventDefault(); setError(""); setPending(true); try { const current = await login(email, password, rememberMe); navigate(current.mustChangePassword ? "/change-password" : safeReturnTo, { replace: true }); } catch (failure) { const error = failure as { code?: string; status?: number }; setError(error.code === "SERVICE_NOT_READY" ? t("auth.serviceNotReady") : error.code === "TOO_MANY_ATTEMPTS" ? t("auth.tooMany") : error.status && error.status >= 500 ? t("auth.unavailable") : t("auth.invalid")); } finally { setPending(false); } };
  const storyTitle = t("auth.storyTitle").split("\n");
  return <main className="login-page"><section className="login-story"><Logo/><div><p className="eyebrow">{t("auth.storyEyebrow")}</p><h1>{storyTitle.map((line, index) => <span key={line}>{index > 0 && <br/>}{line}</span>)}</h1><p>{t("auth.storyBody")}</p></div><small>CloudEmuera · {t("chrome.selfHostedRuntime")}</small></section><section className="login-form-wrap"><form className="login-form" onSubmit={submit}><LanguageSelect login disabled={pending}/><p className="eyebrow">{t("auth.welcome")}</p><h2>{t("auth.title")}</h2><p>{t("auth.intro")}</p><label><span>{t("auth.email")}</span><input value={email} onChange={e => setEmail(e.target.value)} type="email" autoComplete="email" required/></label><label><span>{t("auth.password")}</span><input value={password} onChange={e => setPassword(e.target.value)} type="password" autoComplete="current-password" required/></label><div className="login-options"><label className="login-checkbox"><input type="checkbox" checked={rememberMe} onChange={e => setRememberMe(e.target.checked)}/><span>{t("auth.remember")}</span></label></div>{error && <p className="form-error" role="alert">{error}</p>}<button className="primary-button wide" disabled={pending}>{pending ? t("auth.submitting") : <>{t("auth.submit")} <Icon name="arrow"/></>}</button></form></section></main>;
}

function ChangePasswordPage() {
  const { t } = useTranslation(); const { user, changePassword } = useAuth(); const navigate = useNavigate(); const [currentPassword, setCurrentPassword] = useState(""); const [newPassword, setNewPassword] = useState(""); const [confirmation, setConfirmation] = useState(""); const [error, setError] = useState(""); const [pending, setPending] = useState(false);
  if (!user) return <Navigate to="/login" replace/>;
  if (!user.mustChangePassword) return <Navigate to="/games" replace/>;
  const submit = async (event: FormEvent) => { event.preventDefault(); setError(""); if (newPassword !== confirmation) { setError(t("passwordChange.mismatch")); return; } setPending(true); try { await changePassword(currentPassword, newPassword); navigate("/games", { replace: true }); } catch { setError(t("passwordChange.failed")); } finally { setPending(false); } };
  return <main className="login-page"><section className="login-story"><Logo/><div><p className="eyebrow">{t("passwordChange.eyebrow")}</p><h1>{t("passwordChange.storyTitle")}</h1><p>{t("passwordChange.storyBody")}</p></div></section><section className="login-form-wrap"><form className="login-form" onSubmit={submit}><h2>{t("passwordChange.title")}</h2><p>{t("passwordChange.intro")}</p><label><span>{t("passwordChange.current")}</span><input type="password" value={currentPassword} onChange={e => setCurrentPassword(e.target.value)} autoComplete="current-password" required/></label><label><span>{t("passwordChange.next")}</span><input type="password" value={newPassword} onChange={e => setNewPassword(e.target.value)} autoComplete="new-password" minLength={8} required/></label><label><span>{t("passwordChange.confirm")}</span><input type="password" value={confirmation} onChange={e => setConfirmation(e.target.value)} autoComplete="new-password" minLength={8} required/></label>{error && <p className="form-error" role="alert">{error}</p>}<button className="primary-button wide" disabled={pending}>{pending ? t("passwordChange.saving") : t("passwordChange.save")}</button></form></section></main>;
}

function RequireAuthenticated({ children }: { children: ReactNode }) {
  const { t } = useTranslation(); const { user, loading } = useAuth(); const location = useLocation();
  if (loading) return <main className="auth-loading" aria-busy="true">{t("auth.checking")}</main>;
  if (!user) return <Navigate to={`/login?returnTo=${encodeURIComponent(location.pathname)}`} replace/>;
  if (user.mustChangePassword) return <Navigate to="/change-password" replace/>;
  return <>{children}</>;
}

function RequireAdmin({ children }: { children: ReactNode }) { const { user } = useAuth(); return user?.role === "ADMIN" ? <>{children}</> : <Navigate to="/games" replace/>; }

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: true },
    mutations: { retry: false },
  },
});

function AppRoutes() {
  return <Routes>
    <Route path="/login" element={<LoginPage/>}/>
    <Route path="/change-password" element={<ChangePasswordPage/>}/>
    <Route path="*" element={<RequireAuthenticated><AppShell><Routes>
      <Route path="/games" element={<GamesPage/>}/>
      <Route path="/games/:gameId" element={<GameDetailPage/>}/>
      <Route path="/sessions" element={<RealSessionsPage/>}/>
      <Route path="/sessions/new" element={<RealNewSessionPage/>}/>
      <Route path="/sessions/:sessionId/configuration" element={<RealSessionConfigurationPage/>}/>
      <Route path="/sessions/:sessionId/saves" element={<NativeSavesPage/>}/>
      <Route path="/sessions/:sessionId" element={<RealtimeConsolePage/>}/>
      <Route path="/saves" element={<NativeSavesPage/>}/>
      <Route path="/admin" element={<RequireAdmin><AdminPage/></RequireAdmin>}/>
      <Route path="/admin/users" element={<RequireAdmin><AdminUsersPage/></RequireAdmin>}/>
      <Route path="/settings" element={<SettingsPage/>}/>
      <Route path="*" element={<Navigate to="/games" replace/>}/>
    </Routes></AppShell></RequireAuthenticated>}/>
  </Routes>;
}

export function App() {
  return <QueryClientProvider client={queryClient}><AppRoutes/></QueryClientProvider>;
}
