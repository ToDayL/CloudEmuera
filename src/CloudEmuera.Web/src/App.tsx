import { FormEvent, ReactNode, useCallback, useEffect, useRef, useState } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
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
import {
  ContentScope,
  GameDiagnosticItem,
  GameFileItem,
  GamePackageDiagnostic,
  GameLibraryItem,
  GameTextFile,
  GameValidationResult,
  GameVisibility,
  IngestedGamePackage,
  activateGame,
  bindGamePackage,
  createGame,
  deleteGame,
  downloadFileUrl,
  formatBytes,
  formatDateTime,
  getGame,
  ingestGamePackage,
  listDiagnostics,
  listFiles,
  listGames,
  readTextFile,
  setGameBlocked,
  shortDigest,
  updateGame,
  validateGame,
} from "./games";
import { ConsolePage as RealtimeConsolePage } from "./console/ConsolePage";
import { SavesPage as NativeSavesPage } from "./saves/SavesPage";
import { NewSessionPage as RealNewSessionPage, SessionsPage as RealSessionsPage } from "./sessions/pages";

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
  return <Link className="brand" to="/games" aria-label="CloudEmuera 首页"><span className="brand-mark">C</span><span>CloudEmuera</span></Link>;
}

function AppShell({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth();
  const navigate = useNavigate();
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();
  useEffect(() => setMobileOpen(false), [location.pathname]);
  const nav = [
    { to: "/games", label: "游戏库", icon: "grid" as const },
    { to: "/sessions", label: "Session", icon: "gamepad" as const },
    { to: "/saves", label: "存档", icon: "save" as const },
  ];
  return <div className="app-shell">
    <header className="mobile-header"><Logo/><button className="icon-button" onClick={() => setMobileOpen(!mobileOpen)} aria-label="打开导航"><Icon name="menu"/></button></header>
    <aside className={`sidebar ${mobileOpen ? "is-open" : ""}`}>
      <Logo/>
      <nav className="main-nav" aria-label="主导航">
        <p className="nav-caption">PLAY</p>
        {nav.map((item) => <NavLink key={item.to} to={item.to} className={({ isActive }) => isActive ? "active" : ""}><Icon name={item.icon}/><span>{item.label}</span></NavLink>)}
        {user?.role === "ADMIN" && <><p className="nav-caption second">SYSTEM</p>
        <NavLink to="/admin" className={({ isActive }) => isActive ? "active" : ""}><Icon name="server"/><span>运行状态</span></NavLink>
        <NavLink to="/admin/users" className={({ isActive }) => isActive ? "active" : ""}><Icon name="user"/><span>用户管理</span></NavLink></>}
        <NavLink to="/settings" className={({ isActive }) => isActive ? "active" : ""}><Icon name="settings"/><span>设置</span></NavLink>
      </nav>
      <div className="sidebar-foot">
        <div className="instance-label"><span className="pulse-dot"/>单机实例运行中</div>
        <button className="profile-button" onClick={() => void logout().finally(() => navigate("/login", { replace: true }))}><span className="avatar">{user?.username.slice(0, 1) ?? "?"}</span><span><strong>{user?.username}</strong><small>{user?.role === "ADMIN" ? "管理员" : "玩家账户"} · 注销</small></span><Icon name="more"/></button>
      </div>
    </aside>
    {mobileOpen && <button className="sidebar-scrim" aria-label="关闭导航" onClick={() => setMobileOpen(false)}/>}
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
    if (err.code === "ACTIVATION_VALIDATION_FAILED") return "验证未通过：工作区存在阻断启用的诊断。";
    if (err.code === "GAME_VALIDATION_FAILED") return "验证失败，无法完成启用。";
    if (err.code === "VALIDATION_IN_PROGRESS") return "验证正在进行中，请稍后刷新查看结果。";
    if (err.code === "ACTIVATION_IN_PROGRESS") return "启用正在进行中，请稍后刷新查看结果。";
    if (err.code === "GAME_STATE_CONFLICT") return "游戏状态已变化（可能是验证/启用刚完成），已刷新，请重试。";
  }
  return err instanceof Error ? err.message : "操作失败。";
}

function GamesPage() {
  const [query, setQuery] = useState("");
  const [visibility, setVisibility] = useState("全部");
  const [items, setItems] = useState<GameLibraryItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [createOpen, setCreateOpen] = useState(false);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setItems(await listGames());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "无法加载游戏库。");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const filtered = items.filter(game => {
    const matchesVisibility = visibility === "全部" || (visibility === "我的游戏" ? game.visibility === "PRIVATE" : game.visibility === "SERVER_SHARED");
    const q = query.trim().toLowerCase();
    return matchesVisibility && (!q || game.name.toLowerCase().includes(q));
  });
  const currentCount = items.filter(game => game.hasCurrentContent).length;
  const sharedCount = items.filter(game => game.visibility === "SERVER_SHARED").length;

  return <>
    <PageHeader eyebrow="LIBRARY" title="游戏库" description="管理游戏包、内容与兼容性，然后开启一段新的旅程。"
      actions={<><button className="secondary-button" onClick={() => setCreateOpen(true)}><Icon name="plus"/>创建游戏</button><button className="primary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>导入游戏</button></>}/>
    <section className="summary-strip" aria-label="游戏库概览">
      <div><span className="summary-icon peach"><Icon name="book"/></span><p><strong>{items.length}</strong><small>游戏</small></p></div>
      <div><span className="summary-icon mint"><Icon name="archive"/></span><p><strong>{currentCount}</strong><small>份当前内容</small></p></div>
      <div><span className="summary-icon blue"><Icon name="grid"/></span><p><strong>{sharedCount}</strong><small>服务器共享</small></p></div>
      <div className="tip"><Icon name="spark"/><p><strong>内容验证后原子启用</strong><small>上传包只写入独立工作区，不影响当前可运行内容</small></p></div>
    </section>
    <div className="toolbar">
      <label className="search-box"><Icon name="search"/><span className="sr-only">搜索游戏</span><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="搜索游戏…"/></label>
      <div className="segment-control" aria-label="游戏筛选">{["全部", "我的游戏", "服务器共享"].map(item => <button className={visibility === item ? "selected" : ""} onClick={() => setVisibility(item)} key={item}>{item}</button>)}</div>
    </div>
    {loading ? <div className="panel loading-panel" aria-busy="true"><span className="mini-spinner"/><p>正在加载游戏库…</p></div>
      : error ? <div className="panel error-panel" role="alert"><Icon name="warning"/><div><strong>无法加载游戏库</strong><p>{error}</p></div><button className="secondary-button" onClick={() => void refresh()}>重试</button></div>
      : filtered.length === 0 ? <section className="empty-state"><span className="empty-icon"><Icon name="book" size={26}/></span><h2>{items.length === 0 ? "还没有游戏" : "没有匹配的游戏"}</h2><p>{items.length === 0 ? "创建游戏或导入 ZIP 游戏包，验证通过后即可创建 Session。" : "试试其他关键词或筛选条件。"}</p><div className="empty-actions">{items.length === 0 && <><button className="primary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>导入游戏</button><button className="secondary-button" onClick={() => setCreateOpen(true)}><Icon name="plus"/>创建游戏</button></>}</div></section>
      : <section className="game-grid" aria-label="游戏列表">
          {filtered.map((game) => <article className="game-card" key={game.id}>
            <div className={`game-cover ${coverColor(game.name)}`}><span className="cover-grid"/><span className="cover-glyph">{game.name.slice(0, 1).toUpperCase()}</span><span className="cover-digest">{game.hasCurrentContent ? shortDigest(game.contentDigest) : "无当前内容"}</span></div>
            <div className="game-card-body">
              <div className="card-title-row"><div><h2>{game.name}</h2><p>{game.visibility === "SERVER_SHARED" ? "服务器共享" : "私有游戏"}</p></div><button className="icon-button subtle" aria-label={`${game.name} 更多操作`}><Icon name="more"/></button></div>
              <div className="tag-row">
                {game.hasCurrentContent ? <span className="tag success"><Icon name="check" size={13}/>当前内容</span> : <span className="tag">无当前内容</span>}
                {game.workspaceStatus === "DRAFT" && <span className="tag waiting">工作区草稿</span>}
                {game.status === "BLOCKED" && <span className="tag warning"><Icon name="warning" size={13}/>已禁用</span>}
              </div>
              <div className="card-meta"><span>更新于 {formatDateTime(game.updatedAt)}</span><span>{game.contentRevision} 次启用</span></div>
              <div className="card-actions">
                <Link className="secondary-button" to={`/games/${game.id}`}>管理内容</Link>
                {game.hasCurrentContent && game.status === "ACTIVE"
                  ? <Link className="play-button" to={`/sessions/new?game=${game.id}`}><Icon name="play" size={17}/>开始游戏</Link>
                  : <button className="play-button" disabled title="需要已启用且未禁用的当前内容">开始游戏</button>}
              </div>
            </div>
          </article>)}
          <button className="game-card add-card" onClick={() => setUploadOpen(true)}><span><Icon name="upload" size={24}/></span><strong>导入新的游戏包</strong><small>支持安全校验的 ZIP 文件</small></button>
        </section>}
    {createOpen && <CreateGameDialog onClose={() => setCreateOpen(false)} onCreated={() => { setCreateOpen(false); void refresh(); }}/>}
    {uploadOpen && <UploadDialog onClose={() => setUploadOpen(false)} games={items}/>}
  </>;
}

function CreateGameDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (game: GameLibraryItem) => void }) {
  const [name, setName] = useState("");
  const [visibility, setVisibility] = useState<GameVisibility>("PRIVATE");
  const [error, setError] = useState("");
  const [pending, setPending] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) { setError("请输入游戏名称。"); return; }
    setError(""); setPending(true);
    try { onCreated(await createGame(trimmed, visibility)); }
    catch (err) { setError(err instanceof Error ? err.message : "创建失败。"); setPending(false); }
  };
  return <div className="modal-layer" role="presentation"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="create-game-title">
    <button className="icon-button modal-close" onClick={onClose} aria-label="关闭"><Icon name="close"/></button>
    <p className="eyebrow">NEW GAME</p><h2 id="create-game-title">创建游戏</h2><p className="modal-intro">先创建一个空游戏，之后导入游戏包并验证启用；也可以直接用「导入游戏」一步完成。</p>
    <form className="form-panel modal-form" onSubmit={submit}>
      <label><span>游戏名称</span><input value={name} onChange={(e) => setName(e.target.value)} placeholder="例如：ERA: The World" autoFocus required/></label>
      <label><span>可见性</span><select value={visibility} onChange={(e) => setVisibility(e.target.value as GameVisibility)}><option value="PRIVATE">私有（仅自己可见）</option><option value="SERVER_SHARED">服务器共享（所有玩家可见）</option></select></label>
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="form-actions"><button className="secondary-button" type="button" onClick={onClose}>取消</button><button className="primary-button" disabled={pending}>{pending ? "正在创建…" : "创建游戏"}</button></div>
    </form>
  </section></div>;
}

function UploadDialog({ onClose, games, initialGameId }: { onClose: () => void; games: GameLibraryItem[]; initialGameId?: string }) {
  const [step, setStep] = useState<"choose" | "uploading" | "ingested" | "done" | "error">("choose");
  const [fileName, setFileName] = useState("");
  const [ingestion, setIngestion] = useState<IngestedGamePackage | null>(null);
  const [error, setError] = useState("");
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [createMode, setCreateMode] = useState(!initialGameId);
  const [gameName, setGameName] = useState("");
  const [visibility, setVisibility] = useState<GameVisibility>("PRIVATE");
  const [targetGameId, setTargetGameId] = useState(initialGameId ?? "");
  const [boundGameId, setBoundGameId] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const startUpload = (file: File) => {
    if (!file) return;
    setFileName(file.name);
    setGameName(file.name.replace(/\.zip$/i, "") || "New Game");
    setStep("uploading");
    setError("");
    void ingestGamePackage(file)
      .then(result => { setIngestion(result); setStep("ingested"); })
      .catch(err => {
        // A transport-level failure (e.g. "Failed to fetch") usually means the
        // server aborted the body: the archive exceeded the server/network limit.
        setErrorCode(err instanceof ApiError ? err.code : null);
        setError(err instanceof ApiError ? err.message : "网络错误：上传未能完成。请确认文件未超过大小限制，并检查网络后重试。");
        setStep("error");
      });
  };

  const bind = async (event: FormEvent) => {
    event.preventDefault();
    if (!ingestion) return;
    setPending(true); setError("");
    try {
      const fresh = createMode
        ? await createGame(gameName.trim() || "New Game", visibility)
        : await getGame(targetGameId);
      await bindGamePackage(fresh.id, fresh.stateVersion, ingestion.ingestionId, ingestion.manifest.contentDigest);
      setBoundGameId(fresh.id);
      setStep("done");
    } catch (err) {
      setErrorCode(err instanceof ApiError ? err.code : null);
      setError(err instanceof ApiError ? err.message : "绑定失败。");
      setPending(false);
    }
  };

  const ownedGames = games.filter(game => game.visibility === "PRIVATE");
  const blocking = ingestion?.manifest.diagnostics.filter(diagnostic => diagnostic.publishBlocking).length ?? 0;

  return <div className="modal-layer" role="presentation"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="upload-title">
    <button className="icon-button modal-close" onClick={onClose} aria-label="关闭"><Icon name="close"/></button>
    <p className="eyebrow">NEW GAME</p><h2 id="upload-title">{initialGameId ? "替换游戏包" : "导入游戏包"}</h2><p className="modal-intro">文件会先进入隔离区，完成路径、编码和运行时兼容性检查后才可启用为当前内容。</p>
    {step === "choose" && <>
      <button className="drop-zone" type="button" onClick={() => fileInputRef.current?.click()}><span><Icon name="upload" size={28}/></span><strong>选择 ZIP 文件</strong><small>或拖放到这里 · 最大 2 GB</small></button>
      <input ref={fileInputRef} type="file" accept=".zip,application/zip" className="sr-only" onChange={(e) => { const file = e.target.files?.[0]; if (file) startUpload(file); }}/>
      <div className="modal-note"><Icon name="warning"/><p><strong>请确认你拥有游戏内容的使用权</strong><small>CloudEmuera 不提供或分发游戏资源。</small></p></div>
    </>}
    {step === "uploading" && <div className="scan-state"><span className="spinner"/><h3>正在检查 {fileName}</h3><p>上传并扫描文件与目录结构…</p><div className="progress"><span/></div></div>}
    {step === "ingested" && ingestion && <>
      <div className="ingest-summary">
        <span className="success-ring"><Icon name="check" size={30}/></span>
        <div><h3>游戏包已安全解压</h3><p>{ingestion.manifest.fileCount} 个文件 · {ingestion.manifest.directoryCount} 个目录 · {formatBytes(ingestion.manifest.contentBytes)}</p><small>摘要 sha256:{shortDigest(ingestion.manifest.contentDigest)}</small></div>
      </div>
      {blocking > 0 && <div className="ingest-warning"><Icon name="warning"/><p><strong>{blocking} 条阻断提醒</strong><small>绑定后可查看工作区草稿；验证通过前不会启用为当前内容。</small></p></div>}
      {blocking > 0 && <ul className="ingest-blocking-list" aria-label="阻断提醒明细">
        {ingestion.manifest.diagnostics.filter(diagnostic => diagnostic.publishBlocking).slice(0, 6).map((diagnostic: GamePackageDiagnostic, index) => <li key={index}><span>{diagnostic.code}</span>{diagnostic.logicalPath && <small>{diagnostic.logicalPath}</small>}</li>)}
        {blocking > 6 && <li className="ingest-blocking-more">…另有 {blocking - 6} 条</li>}
      </ul>}
      <form className="form-panel modal-form" onSubmit={bind}>
        {!initialGameId && <label className="radio-row"><input type="radio" name="target" checked={createMode} onChange={() => setCreateMode(true)}/><span><strong>创建新游戏并绑定</strong><small>新游戏会立即拥有此工作区草稿</small></span></label>}
        {createMode && <><label><span>游戏名称</span><input value={gameName} onChange={(e) => setGameName(e.target.value)} required/></label><label><span>可见性</span><select value={visibility} onChange={(e) => setVisibility(e.target.value as GameVisibility)}><option value="PRIVATE">私有</option><option value="SERVER_SHARED">服务器共享</option></select></label></>}
        {!initialGameId && <label className="radio-row"><input type="radio" name="target" checked={!createMode} onChange={() => setCreateMode(false)}/><span><strong>绑定到已有游戏</strong><small>替换该游戏的工作区草稿，不影响当前内容</small></span></label>}
        {!createMode && !initialGameId && <label><span>目标游戏</span><select value={targetGameId} onChange={(e) => setTargetGameId(e.target.value)} required><option value="" disabled>选择游戏…</option>{ownedGames.map(game => <option key={game.id} value={game.id}>{game.name}</option>)}</select></label>}
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="form-actions"><button className="secondary-button" type="button" onClick={() => setStep("choose")}>重新选择</button><button className="primary-button" disabled={pending || (createMode ? !gameName.trim() : !targetGameId)}>{pending ? "正在绑定…" : "绑定并查看草稿"}</button></div>
      </form>
    </>}
    {step === "done" && boundGameId && <div className="scan-state done"><span className="success-ring"><Icon name="check" size={30}/></span><h3>游戏包已绑定到工作区</h3><p>草稿尚未启用为当前内容；验证通过后原子启用。</p><Link className="primary-button" to={`/games/${boundGameId}`} onClick={onClose}>查看草稿并启用<Icon name="arrow"/></Link></div>}
    {step === "error" && <div className="scan-state error"><span className="error-ring"><Icon name="warning" size={30}/></span><h3>游戏包未通过安全检查</h3><p>{error}</p>{errorCode && <code className="error-code">{errorCode}</code>}<button className="secondary-button" onClick={() => { setError(""); setErrorCode(null); setStep("choose"); }}>重新选择文件</button></div>}
  </section></div>;
}

function EditGameDialog({ game, onClose, onSaved }: { game: GameLibraryItem; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(game.name);
  const [visibility, setVisibility] = useState<GameVisibility>(game.visibility);
  const [error, setError] = useState("");
  const [pending, setPending] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) { setError("游戏名称不能为空。"); return; }
    setError(""); setPending(true);
    try { await updateGame(game.id, game.stateVersion, { name: trimmed, visibility }); onSaved(); }
    catch (err) { setError(err instanceof Error ? err.message : "保存失败。"); setPending(false); }
  };
  return <div className="modal-layer"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="edit-game-title">
    <button className="icon-button modal-close" onClick={onClose} aria-label="关闭"><Icon name="close"/></button>
    <p className="eyebrow">EDIT GAME</p><h2 id="edit-game-title">编辑游戏资料</h2><p className="modal-intro">名称与可见性变更立即生效，不影响已启用的当前内容。</p>
    <form className="form-panel modal-form" onSubmit={submit}>
      <label><span>游戏名称</span><input value={name} onChange={(e) => setName(e.target.value)} required/></label>
      <label><span>可见性</span><select value={visibility} onChange={(e) => setVisibility(e.target.value as GameVisibility)}><option value="PRIVATE">私有（仅自己可见）</option><option value="SERVER_SHARED">服务器共享（所有玩家可见）</option></select></label>
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="form-actions"><button className="secondary-button" type="button" onClick={onClose}>取消</button><button className="primary-button" disabled={pending}>{pending ? "正在保存…" : "保存"}</button></div>
    </form>
  </section></div>;
}

function GameDetailPage() {
  const { gameId = "" } = useParams();
  const { user } = useAuth();
  const navigate = useNavigate();
  const [game, setGame] = useState<GameLibraryItem | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState<"内容" | "文件" | "兼容性">("内容");
  const [busy, setBusy] = useState("");
  const [actionError, setActionError] = useState<string | null>(null);
  const [validation, setValidation] = useState<GameValidationResult | null>(null);
  const [diagnostics, setDiagnostics] = useState<GameDiagnosticItem[]>([]);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);

  const refresh = useCallback(async () => {
    try { setGame(await getGame(gameId)); setLoadError(null); }
    catch (err) { setLoadError(err instanceof Error ? err.message : "无法加载游戏。"); }
    finally { setLoading(false); }
  }, [gameId]);

  const loadDiagnostics = useCallback(async () => {
    try { setDiagnostics(await listDiagnostics(gameId)); }
    catch { /* diagnostics are best-effort; the tab falls back to the in-session result */ }
  }, [gameId]);

  useEffect(() => { void refresh(); void loadDiagnostics(); }, [refresh, loadDiagnostics]);

  const run = useCallback(async (label: string, action: () => Promise<GameLibraryItem | GameValidationResult | void>) => {
    setBusy(label); setActionError(null);
    try {
      const result = await action();
      if (result && typeof result === "object" && "canActivate" in result) setValidation(result as GameValidationResult);
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
    setBusy("正在删除"); setActionError(null);
    try { await deleteGame(game.id, game.stateVersion); navigate("/games", { replace: true }); }
    catch (err) { setActionError(actionErrorMessage(err)); setBusy(""); }
  };

  if (loading || (!game && !loadError)) return <>
    <div className="backline"><Link to="/games">← 返回游戏库</Link></div>
    <div className="panel loading-panel" aria-busy="true"><span className="mini-spinner"/><p>正在加载游戏…</p></div>
  </>;
  if (loadError || !game) return <>
    <div className="backline"><Link to="/games">← 返回游戏库</Link></div>
    <div className="panel error-panel" role="alert"><Icon name="warning"/><div><strong>无法加载游戏</strong><p>{loadError ?? "游戏不存在。"}</p></div><button className="secondary-button" onClick={() => { setLoading(true); void refresh(); }}>重试</button></div>
  </>;

  const hasDraft = game.workspaceStatus === "DRAFT";
  const canPlay = game.hasCurrentContent && game.status === "ACTIVE";
  const displayDiagnostics: Array<{ id: string; code: string; severity: string; path: string | null; message: string; activationBlocking: boolean }> =
    diagnostics.length > 0
      ? diagnostics
      : (validation?.diagnostics ?? []).map((diagnostic, index) => ({ id: `validation-${index}`, code: diagnostic.code, severity: diagnostic.severity, path: diagnostic.path, message: diagnostic.message, activationBlocking: diagnostic.activationBlocking }));
  const blockingCount = displayDiagnostics.filter(diagnostic => diagnostic.activationBlocking).length;

  return <>
    <div className="backline"><Link to="/games">← 返回游戏库</Link></div>
    <section className="game-detail-hero">
      <div className={`game-cover compact ${coverColor(game.name)}`}><span className="cover-grid"/><span className="cover-glyph">{game.name.slice(0, 1).toUpperCase()}</span></div>
      <div>
        <p className="eyebrow">{game.visibility === "PRIVATE" ? "PRIVATE GAME" : "SERVER SHARED GAME"}</p>
        <h1>{game.name}</h1>
        <p>{game.status === "BLOCKED" ? "已被管理员禁用，无法创建新 Session；既有 Session 不受影响。" : "内容验证后原子启用；当前内容对既有 Session 不可变。"}</p>
        <div className="tag-row">
          {game.status === "BLOCKED" && <span className="tag warning"><Icon name="warning" size={13}/>已禁用</span>}
          {game.hasCurrentContent ? <span className="tag success"><Icon name="check" size={13}/>当前内容可运行</span> : <span className="tag">无当前内容</span>}
          {hasDraft && <span className="tag waiting">待验证工作区</span>}
          {game.contentDigest && <span className="tag">sha256:{shortDigest(game.contentDigest)}</span>}
        </div>
      </div>
      <div className="hero-actions">
        {canPlay ? <Link className="play-button" to={`/sessions/new?game=${game.id}`}><Icon name="play"/>创建 Session</Link> : <button className="play-button" disabled title="需要已启用且未禁用的当前内容"><Icon name="play"/>创建 Session</button>}
        <button className="secondary-button" onClick={() => setEditOpen(true)} disabled={busy !== ""}><Icon name="settings"/>编辑资料</button>
        {user?.role === "ADMIN" && <button className="secondary-button" onClick={() => void run(game.status === "BLOCKED" ? "取消禁用" : "禁用游戏", () => setGameBlocked(game.id, game.stateVersion, game.status !== "BLOCKED"))} disabled={busy !== ""}>{game.status === "BLOCKED" ? "取消禁用" : "禁用"}</button>}
        <button className="danger-button" onClick={() => setConfirmDelete(true)} disabled={busy !== ""}>删除游戏</button>
      </div>
    </section>
    <div className="detail-tabs">{(["内容", "文件", "兼容性"] as const).map(item => <button className={tab === item ? "active" : ""} onClick={() => setTab(item)} key={item}>{item}</button>)}</div>
    {actionError && <div className="error-banner" role="alert"><Icon name="warning"/><span>{actionError}{blockingCount > 0 && <small> · {blockingCount} 条阻断诊断，详见「兼容性」</small>}</span></div>}
    {busy && <div className="busy-banner" role="status"><span className="mini-spinner"/><span>{busy}…</span></div>}

    {tab === "内容" && <section className="panel">
      <div className="panel-heading">
        <div><h2>当前内容</h2><p>当前可运行内容是只读快照；上传包写入独立摄取工作区，验证通过并原子启用后才替换。</p></div>
        {hasDraft
          ? <button className="primary-button" onClick={() => void run("正在验证并启用", () => activateGame(game.id, game.stateVersion))} disabled={busy !== ""}><Icon name="check"/>验证并启用</button>
          : <button className="secondary-button" onClick={() => setUploadOpen(true)} disabled={busy !== ""}><Icon name="upload"/>上传游戏包</button>}
      </div>
      <div className="content-list">
        <article><span className="timeline-dot live"/><div>
          <h3>{game.hasCurrentContent ? `sha256:${shortDigest(game.contentDigest)}` : "尚未启用当前内容"}{game.hasCurrentContent && <span className="tag success">当前内容</span>}</h3>
          <p>内容修订 #{game.contentRevision} · {formatDateTime(game.updatedAt)} 更新</p>
          <small>{game.hasCurrentContent ? "Session 创建时完整复制此快照；后续上传不会改变既有 Session。" : "导入游戏包并验证启用后，即可创建 Session。"}</small>
        </div><button className="text-button" onClick={() => setTab("文件")}>查看文件 <Icon name="arrow"/></button></article>
      </div>
      {hasDraft && <div className="content-list">
        <article><span className="timeline-dot"/><div>
          <h3>工作区草稿 <span className="tag waiting">待验证</span></h3>
          <p>上传包只写入工作区，当前内容保持不可变。</p>
          <small>验证通过后可原子启用；不启用时可再次上传包替换。</small>
        </div>
        <span className="workspace-actions">
          <button className="text-button" onClick={() => void run("正在验证", () => validateGame(game.id, game.stateVersion))} disabled={busy !== ""}>验证</button>
        </span></article>
      </div>}
      {!game.hasCurrentContent && !hasDraft && <div className="content-list empty-list"><article><span className="timeline-dot"/><div><h3>这个游戏还没有内容</h3><p>导入一个游戏包开始。</p></div><button className="secondary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>导入游戏包</button></article></div>}
      {hasDraft && <div className="panel-foot-actions"><button className="secondary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>上传新游戏包替换工作区</button></div>}
      {(validation || displayDiagnostics.length > 0) && <div className={`validation-banner ${blockingCount === 0 ? "ok" : "bad"}`}><Icon name={blockingCount === 0 ? "check" : "warning"}/><p><strong>{blockingCount === 0 ? "验证通过" : "存在阻断启用的诊断"}</strong><small>{validation ? `${validation.diagnostics.length} 条诊断 · 摘要 ${shortDigest(validation.contentDigest)}` : `${displayDiagnostics.length} 条诊断`}</small></p><button className="text-button" onClick={() => setTab("兼容性")}>查看详情 <Icon name="arrow"/></button></div>}
    </section>}

    {tab === "文件" && <GameFilesPanel game={game}/>}

    {tab === "兼容性" && <section className="panel diagnostics">
      {!validation && displayDiagnostics.length === 0 ? <div className="diagnostic-empty"><Icon name="spark" size={26}/><h2>尚未运行验证</h2><p>运行一次验证以检查目录结构、编码、解析错误与禁止能力。</p><button className="primary-button" onClick={() => void run("正在验证", () => validateGame(game.id, game.stateVersion))} disabled={busy !== ""}>运行验证</button></div>
        : <>
          <div className="diagnostic-summary">
            <span className={`score ${blockingCount === 0 ? "ok" : "bad"}`}>{blockingCount === 0 ? "✓" : "!"}</span>
            <div><h2>{blockingCount === 0 ? "验证通过，可以启用" : "存在阻断启用的诊断"}</h2><p>{validation ? `${validation.fileCount} 个文件 · ${formatBytes(validation.totalBytes)} · 摘要 ${shortDigest(validation.contentDigest)}` : `${displayDiagnostics.length} 条诊断 · 工作区草稿`}</p></div>
          </div>
          {displayDiagnostics.length === 0 ? <p className="diagnostic-none">没有诊断。</p>
            : displayDiagnostics.map(diagnostic => <div className={`diagnostic-row ${diagnostic.activationBlocking ? "blocking" : ""}`} key={diagnostic.id}><Icon name={diagnostic.activationBlocking ? "warning" : "check"}/><span><strong>{diagnostic.code}</strong>{diagnostic.path && <small> · {diagnostic.path}</small>}<p>{diagnostic.message}</p></span><small>{diagnostic.severity}</small></div>)}
        </>}
      {hasDraft && <div className="diagnostic-foot"><button className="secondary-button" onClick={() => void run("正在验证", () => validateGame(game.id, game.stateVersion))} disabled={busy !== ""}>{busy === "正在验证" ? "验证中…" : "重新验证"}</button></div>}
    </section>}

    {editOpen && <EditGameDialog game={game} onClose={() => setEditOpen(false)} onSaved={() => { setEditOpen(false); void refresh(); }}/>}
    {uploadOpen && <UploadDialog onClose={() => setUploadOpen(false)} games={[game]} initialGameId={game.id}/>}
    {confirmDelete && <ConfirmDialog title="删除这个游戏？" body="未被 Session 引用的游戏会先执行可恢复的逻辑删除；被引用的游戏会被拒绝删除。" confirm="确认删除" onCancel={() => setConfirmDelete(false)} onConfirm={() => void deleteGameAction()} pending={busy === "正在删除"}/>}
  </>;
}

function GameFilesPanel({ game }: { game: GameLibraryItem }) {
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
    catch (err) { setFilesError(err instanceof Error ? err.message : "无法读取文件列表。"); setFiles([]); }
  }, [game.id]);

  useEffect(() => {
    setPath(""); setSelected(null); setSelectedPath(null); setReadError(null);
    const available = scope === "WORKSPACE" ? workspaceAvailable : currentAvailable;
    if (available) void loadFiles(scope, "");
  }, [scope, loadFiles, workspaceAvailable, currentAvailable]);

  const openFile = async (filePath: string) => {
    setSelectedPath(filePath); setSelected(null); setReadError(null);
    try { setSelected(await readTextFile(game.id, scope, filePath)); }
    catch (err) { setReadError(err instanceof Error ? err.message : "无法读取文件。"); }
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
      <div className="segment-control" aria-label="文件范围">
        <button className={scope === "WORKSPACE" ? "selected" : ""} disabled={!workspaceAvailable} onClick={() => setScope("WORKSPACE")}>待验证工作区</button>
        <button className={scope === "CURRENT" ? "selected" : ""} disabled={!currentAvailable} onClick={() => setScope("CURRENT")}>当前内容</button>
      </div>
      <span className="readonly-note">文件仅供查看和下载</span>
    </div>
    {!workspaceAvailable && !currentAvailable
      ? <div className="panel empty-list-panel"><Icon name="folder" size={26}/><p>这个游戏还没有可浏览的内容。先在「内容」页导入游戏包。</p></div>
      : <section className="panel file-panel">
        <div className="file-tree">
          <h3>{scope === "WORKSPACE" ? "待验证工作区" : "当前内容"}{path ? ` / ${path}` : ""}</h3>
          <div className="file-breadcrumb">
            <button className={path === "" ? "current" : ""} onClick={() => { setPath(""); void loadFiles(scope, ""); }}>根目录</button>
            {segments.map((segment, index) => <button key={index} onClick={() => goTo(index)}>{segment}</button>)}
          </div>
          {path !== "" && <button className="file-row up" onClick={goUp}><Icon name="arrow" size={15}/>上一级</button>}
          {filesError && <p className="file-error" role="alert">{filesError}</p>}
          {files?.map(item => item.isDirectory
            ? <button className="file-row" key={item.path} onClick={() => { setPath(item.path); void loadFiles(scope, item.path); }}><Icon name="folder"/><span>{item.path.split("/").pop()}</span><span className="file-meta">目录</span></button>
            : <div className="file-row-wrap" key={item.path}><button className={`file-row ${selectedPath === item.path ? "selected" : ""}`} onClick={() => void openFile(item.path)}><Icon name="book"/><span>{item.path.split("/").pop()}</span><span className="file-meta">{formatBytes(item.bytes)}</span></button><a className="file-download" href={downloadFileUrl(game.id, scope, item.path)} download aria-label={`下载 ${item.path}`}><Icon name="download" size={15}/></a></div>)}
          {files && files.length === 0 && !filesError && <p className="file-empty">此目录为空。</p>}
        </div>
        <div className="file-viewer">
          {selected ? <div className="file-viewer-bar"><span>{selected.path}</span><span>{selected.encoding}{selected.hasBom ? " BOM" : ""} · {formatBytes(selected.bytes)} · 只读</span></div>
            : readError ? <div className="file-viewer-error" role="alert"><Icon name="warning"/><p>{readError}</p></div>
            : <div className="file-viewer-empty"><Icon name="book" size={26}/><p>选择左侧文件查看内容</p><small>文件内容不可在浏览器中修改。</small></div>}
          {selected && (
            <textarea className="file-viewer-content" value={selected.content} readOnly spellCheck={false} aria-label={`查看 ${selected.path}`}/>
          )}
          {readError && selected && <p className="file-error" role="alert">{readError}</p>}
        </div>
      </section>}
  </div>;
}

function Status({ state }: { state: string }) {
  const label = state === "RUNNING" ? "运行中" : "已关闭";
  return <span className={`status-pill ${state.toLowerCase()}`}><i/>{label}</span>;
}

function AdminPage() {
  return <><PageHeader eyebrow="SYSTEM" title="运行状态" description="查看单机 API、Worker Manager 和活动 Session 的基本状态。" actions={<span className="updated"><i/>刚刚更新</span>}/><section className="health-grid"><article><span className="summary-icon mint"><Icon name="server"/></span><div><p>API</p><strong>健康</strong><small>12 ms · v0.1.0-dev</small></div></article><article><span className="summary-icon mint"><Icon name="settings"/></span><div><p>Worker Manager</p><strong>健康</strong><small>进程监视运行中</small></div></article><article><span className="summary-icon blue"><Icon name="gamepad"/></span><div><p>活动 Worker</p><strong>2</strong><small>当前运行进程</small></div></article><article><span className="summary-icon peach"><Icon name="archive"/></span><div><p>数据目录</p><strong>可用</strong><small>空间检查正常</small></div></article></section><section className="panel"><div className="panel-heading"><div><h2>活动 Worker</h2><p>每个活动 Session 由一个独立进程持有。</p></div></div><div className="worker-table"><div className="worker-head"><span>Session / Worker</span><span>状态</span><span>epoch</span><span>心跳</span></div>{[{name:"周目二 · 港口存档",id:"wrk_8fd2",epoch:"3"},{name:"初见流程",id:"wrk_1ac4",epoch:"1"}].map(w=><div className="worker-row" key={w.id}><span><strong>{w.name}</strong><small>{w.id}</small></span><span><Status state="RUNNING"/></span><span>{w.epoch}</span><span>2 秒前</span></div>)}</div></section></>;
}

const defaultUserInput: CreateUserInput = { username: "", email: "", temporaryPassword: "", role: "PLAYER" };

function AdminUsersPage() {
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
    } catch { setError("无法加载用户列表。请确认当前账户仍有管理员权限。"); }
    finally { setLoading(false); }
  };
  useEffect(() => { void refresh(); }, []);
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError(""); setCreating(true);
    try {
      const created = await createUser(form);
      setUsers(current => [...current, created]);
      setForm(defaultUserInput);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "无法创建用户。"); }
    finally { setCreating(false); }
  };
  const changeStatus = async (target: CurrentUser) => {
    setError("");
    try {
      const updated = await updateUser(target.id, target.stateVersion, { status: target.status === "ACTIVE" ? "DISABLED" : "ACTIVE" });
      setUsers(current => current.map(item => item.id === updated.id ? updated : item));
    } catch (cause) { setError(cause instanceof Error ? cause.message : "无法更新用户状态。"); }
  };
  const toggleRole = async (target: CurrentUser) => {
    setError("");
    try {
      const updated = await updateUser(target.id, target.stateVersion, { role: target.role === "ADMIN" ? "PLAYER" : "ADMIN" });
      setUsers(current => current.map(item => item.id === updated.id ? updated : item));
    } catch (cause) { setError(cause instanceof Error ? cause.message : "无法更新用户角色。"); }
  };
  const submitReset = async (event: FormEvent) => {
    event.preventDefault(); if (!resetting) return; setError("");
    try { await resetUserPassword(resetting.id, resetting.stateVersion, temporaryPassword); setResetting(null); setTemporaryPassword(""); await refresh(); }
    catch (cause) { setError(cause instanceof Error ? cause.message : "无法重置密码。"); }
  };
  const submitProfile = async (event: FormEvent) => {
    event.preventDefault(); if (!editing) return; setError("");
    try {
      const updated = await updateUser(editing.id, editing.stateVersion, profile);
      setUsers(current => current.map(item => item.id === updated.id ? updated : item));
      setEditing(null);
    } catch (cause) { setError(cause instanceof Error ? cause.message : "无法更新用户资料。"); }
  };
  return <>
    <PageHeader eyebrow="IDENTITY" title="用户管理" description="创建本地账户，并管理账号状态、角色与临时密码。所有更改立即撤销该用户的现有登录会话。" />
    {error && <p className="form-error" role="alert">{error}</p>}
    <section className="panel admin-users-panel"><div className="panel-heading"><div><h2>本地账户</h2><p>用户必须使用邮箱登录；新建与重置账户均需首次修改临时密码。</p></div><span className="tag">{users.length} 个账户</span></div>
      {loading ? <p className="admin-empty" aria-busy="true">正在加载用户…</p> : <div className="admin-user-table"><div className="admin-user-head"><span>用户</span><span>角色</span><span>状态</span><span>状态版本</span><span>操作</span></div>{users.map(target => <div className="admin-user-row" key={target.id}><span><strong>{target.username}</strong><small>{target.email}</small></span><span><span className={`tag ${target.role === "ADMIN" ? "warning" : "success"}`}>{target.role === "ADMIN" ? "管理员" : "玩家"}</span></span><span>{target.status === "ACTIVE" ? (target.mustChangePassword ? "需改密" : "启用") : "已禁用"}</span><span>#{target.stateVersion}</span><span className="admin-row-actions"><button className="text-button" onClick={() => { setEditing(target); setProfile({ username: target.username, email: target.email }); }}>编辑</button><button className="text-button" onClick={() => void toggleRole(target)} disabled={target.id === actor?.id}>{target.role === "ADMIN" ? "降为玩家" : "设为管理员"}</button><button className="text-button" onClick={() => void changeStatus(target)} disabled={target.id === actor?.id}>{target.status === "ACTIVE" ? "禁用" : "启用"}</button><button className="text-button" onClick={() => { setResetting(target); setTemporaryPassword(""); }} disabled={target.id === actor?.id}>重置密码</button></span></div>)}</div>}
    </section>
    <section className="form-panel admin-create-form"><div><p className="eyebrow">NEW ACCOUNT</p><h2>创建用户</h2><p>临时密码仅用于本次安全传递；服务端不会在响应中返回它。</p></div><form onSubmit={submit}><label><span>用户名</span><input value={form.username} onChange={event => setForm({ ...form, username: event.target.value })} autoComplete="off" required /></label><label><span>登录邮箱</span><input value={form.email} onChange={event => setForm({ ...form, email: event.target.value })} type="email" autoComplete="off" required /></label><label><span>临时密码</span><input value={form.temporaryPassword} onChange={event => setForm({ ...form, temporaryPassword: event.target.value })} type="password" autoComplete="new-password" minLength={12} required /></label><label><span>角色</span><select value={form.role} onChange={event => setForm({ ...form, role: event.target.value as CurrentUser["role"] })}><option value="PLAYER">玩家</option><option value="ADMIN">管理员</option></select></label><div className="form-actions"><button className="primary-button" disabled={creating}>{creating ? "正在创建…" : "创建用户"}</button></div></form></section>
    {resetting && <div className="modal-layer"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="reset-password-title"><button className="icon-button modal-close" aria-label="关闭" onClick={() => setResetting(null)}><Icon name="close"/></button><h2 id="reset-password-title">重置 {resetting.username} 的密码</h2><p className="modal-intro">此操作会立即注销该用户，并要求其在下次登录后修改临时密码。</p><form className="form-panel modal-form" onSubmit={submitReset}><label><span>新临时密码</span><input type="password" value={temporaryPassword} onChange={event => setTemporaryPassword(event.target.value)} autoComplete="new-password" minLength={12} required /></label><div className="form-actions"><button className="secondary-button" type="button" onClick={() => setResetting(null)}>取消</button><button className="danger-button">确认重置</button></div></form></section></div>}
    {editing && <div className="modal-layer"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="edit-user-title"><button className="icon-button modal-close" aria-label="关闭" onClick={() => setEditing(null)}><Icon name="close"/></button><h2 id="edit-user-title">编辑 {editing.username}</h2><p className="modal-intro">更改用户名或登录邮箱会立即撤销该账户的现有登录会话。</p><form className="form-panel modal-form" onSubmit={submitProfile}><label><span>用户名</span><input value={profile.username ?? ""} onChange={event => setProfile({ ...profile, username: event.target.value })} required /></label><label><span>登录邮箱</span><input value={profile.email ?? ""} onChange={event => setProfile({ ...profile, email: event.target.value })} type="email" required /></label><div className="form-actions"><button className="secondary-button" type="button" onClick={() => setEditing(null)}>取消</button><button className="primary-button">保存资料</button></div></form></section></div>}
  </>;
}

function SettingsPage() {
  return <><PageHeader eyebrow="PREFERENCES" title="设置" description="调整浏览器体验与账户偏好；运行时配置由游戏的当前内容决定。"/><section className="panel settings-panel"><h2>外观与交互</h2><label className="setting-row"><span><strong>跟随游戏自动滚动</strong><small>收到新输出或提交输入后回到控制台底部</small></span><input type="checkbox" defaultChecked/></label><label className="setting-row"><span><strong>减少动态效果</strong><small>关闭界面过渡与加载动画</small></span><input type="checkbox"/></label><label className="setting-row"><span><strong>控制台文字大小</strong><small>仅影响游戏输出，不改变应用界面</small></span><select defaultValue="medium"><option value="small">较小</option><option value="medium">标准</option><option value="large">较大</option></select></label></section></>;
}

function ConfirmDialog({ title, body, confirm, onCancel, onConfirm, pending = false }: { title: string; body: string; confirm: string; onCancel: () => void; onConfirm?: () => void; pending?: boolean }) {
  return <div className="modal-layer"><section className="modal confirm-modal" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title"><span className="confirm-icon"><Icon name="warning"/></span><h2 id="confirm-title">{title}</h2><p>{body}</p><div className="form-actions"><button className="secondary-button" onClick={onCancel} disabled={pending}>取消</button><button className="danger-button" onClick={onConfirm ?? onCancel} disabled={pending}>{pending ? "处理中…" : confirm}</button></div></section></div>;
}

function LoginPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const { user, login } = useAuth();
  const [email, setEmail] = useState(""); const [password, setPassword] = useState(""); const [rememberMe, setRememberMe] = useState(false); const [error, setError] = useState(""); const [pending, setPending] = useState(false);
  if (user) return <Navigate to={user.mustChangePassword ? "/change-password" : "/games"} replace/>;
  const returnTo = new URLSearchParams(location.search).get("returnTo");
  const safeReturnTo = returnTo && /^\/(?!\/)/.test(returnTo) && !returnTo.includes("\\") ? returnTo : "/games";
  const submit = async (event: FormEvent) => { event.preventDefault(); setError(""); setPending(true); try { const current = await login(email, password, rememberMe); navigate(current.mustChangePassword ? "/change-password" : safeReturnTo, { replace: true }); } catch (failure) { const error = failure as { code?: string; status?: number }; setError(error.code === "SERVICE_NOT_READY" ? "服务尚未完成数据库迁移或首次初始化。" : error.code === "TOO_MANY_ATTEMPTS" ? "登录尝试过于频繁，请稍后重试。" : error.status && error.status >= 500 ? "登录服务暂时不可用。" : "邮箱或密码不正确。"); } finally { setPending(false); } };
  return <main className="login-page"><section className="login-story"><Logo/><div><p className="eyebrow">YOUR ERA, ANYWHERE</p><h1>故事不会因为<br/>离开浏览器而暂停。</h1><p>在桌面或手机上继续你的 Emuera 游戏。每段旅程独立运行，安全保存，随时重连。</p></div><small>CloudEmuera · Self-hosted runtime</small></section><section className="login-form-wrap"><form className="login-form" onSubmit={submit}><p className="eyebrow">WELCOME BACK</p><h2>登录 CloudEmuera</h2><p>使用登录邮箱访问你的游戏库与正在运行的 Session。</p><label><span>登录邮箱</span><input value={email} onChange={e => setEmail(e.target.value)} type="email" autoComplete="email" required/></label><label><span>密码</span><input value={password} onChange={e => setPassword(e.target.value)} type="password" autoComplete="current-password" required/></label><div className="login-options"><label><input type="checkbox" checked={rememberMe} onChange={e => setRememberMe(e.target.checked)}/> 保持登录</label></div>{error && <p className="form-error" role="alert">{error}</p>}<button className="primary-button wide" disabled={pending}>{pending ? "正在登录…" : <>登录 <Icon name="arrow"/></>}</button></form></section></main>;
}

function ChangePasswordPage() {
  const { user, changePassword } = useAuth(); const navigate = useNavigate(); const [currentPassword, setCurrentPassword] = useState(""); const [newPassword, setNewPassword] = useState(""); const [confirmation, setConfirmation] = useState(""); const [error, setError] = useState(""); const [pending, setPending] = useState(false);
  if (!user) return <Navigate to="/login" replace/>;
  if (!user.mustChangePassword) return <Navigate to="/games" replace/>;
  const submit = async (event: FormEvent) => { event.preventDefault(); setError(""); if (newPassword !== confirmation) { setError("两次输入的新密码不一致。"); return; } setPending(true); try { await changePassword(currentPassword, newPassword); navigate("/games", { replace: true }); } catch { setError("无法修改密码，请检查当前密码和新密码长度。"); } finally { setPending(false); } };
  return <main className="login-page"><section className="login-story"><Logo/><div><p className="eyebrow">SECURITY REQUIRED</p><h1>先更新临时密码。</h1><p>这是首次登录或管理员重置密码后的必要步骤。</p></div></section><section className="login-form-wrap"><form className="login-form" onSubmit={submit}><h2>修改密码</h2><label><span>当前密码</span><input type="password" value={currentPassword} onChange={e => setCurrentPassword(e.target.value)} autoComplete="current-password" required/></label><label><span>新密码</span><input type="password" value={newPassword} onChange={e => setNewPassword(e.target.value)} autoComplete="new-password" minLength={12} required/></label><label><span>确认新密码</span><input type="password" value={confirmation} onChange={e => setConfirmation(e.target.value)} autoComplete="new-password" minLength={12} required/></label>{error && <p className="form-error" role="alert">{error}</p>}<button className="primary-button wide" disabled={pending}>{pending ? "正在保存…" : "保存新密码"}</button></form></section></main>;
}

function RequireAuthenticated({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth(); const location = useLocation();
  if (loading) return <main className="auth-loading" aria-busy="true">正在检查登录状态…</main>;
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
