import { FormEvent, ReactNode, useCallback, useEffect, useRef, useState } from "react";
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
  GameSearchMatch,
  GameTextFile,
  GameValidationResult,
  GameVisibility,
  IngestedGamePackage,
  activateGame,
  bindGamePackage,
  createGame,
  deleteGame,
  deletePath,
  discardWorkspace,
  formatBytes,
  formatDateTime,
  getGame,
  ingestGamePackage,
  listDiagnostics,
  listFiles,
  listGames,
  readTextFile,
  searchFiles,
  setGameBlocked,
  shortDigest,
  startEditing,
  updateGame,
  validateGame,
  writeTextFile,
} from "./games";

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



const initialSessions = [
  { id: "sess-world", name: "周目二 · 港口存档", game: "ERA: The World", sourceContentDigest: "sha256:8f2a…d391", state: "RUNNING", waiting: true, activity: "刚刚", played: "3 小时 24 分", glyph: "世", color: "coral" },
  { id: "sess-megaten", name: "初见流程", game: "ERA Megaten", sourceContentDigest: "sha256:9c41…a07e", state: "RUNNING", waiting: true, activity: "12 分钟前", played: "46 分钟", glyph: "M", color: "violet" },
  { id: "sess-training", name: "测试 07", game: "ERA Training", sourceContentDigest: "sha256:7b0e…c221", state: "CLOSED", waiting: false, activity: "昨天 23:14", played: "1 小时 08 分", glyph: "練", color: "amber" },
];

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
        <div className="quota-label"><span>运行中的 Session</span><strong>2 / 4</strong></div>
        <div className="quota-track"><span/></div>
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
      <div className="tip"><Icon name="spark"/><p><strong>内容验证后原子启用</strong><small>编辑只修改独立工作区，不影响当前可运行内容</small></p></div>
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
      {blocking > 0 && <div className="ingest-warning"><Icon name="warning"/><p><strong>{blocking} 条阻断提醒</strong><small>绑定后仍可查看与编辑草稿；验证通过前不会启用为当前内容。</small></p></div>}
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
  const [confirmDiscard, setConfirmDiscard] = useState(false);
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

  const canEdit = game.workspaceStatus === "DRAFT";
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
          {canEdit && <span className="tag waiting">工作区草稿</span>}
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
        <div><h2>当前内容</h2><p>当前可运行内容是只读快照；编辑写入独立工作区，验证通过并原子启用后才替换。</p></div>
        {canEdit
          ? <button className="primary-button" onClick={() => void run("正在验证并启用", () => activateGame(game.id, game.stateVersion))} disabled={busy !== ""}><Icon name="check"/>验证并启用</button>
          : <button className="secondary-button" onClick={() => void run("正在从当前内容创建工作区", () => startEditing(game.id, game.stateVersion))} disabled={!game.hasCurrentContent || busy !== ""}><Icon name="plus"/>从当前内容开始编辑</button>}
      </div>
      <div className="content-list">
        <article><span className="timeline-dot live"/><div>
          <h3>{game.hasCurrentContent ? `sha256:${shortDigest(game.contentDigest)}` : "尚未启用当前内容"}{game.hasCurrentContent && <span className="tag success">当前内容</span>}</h3>
          <p>内容修订 #{game.contentRevision} · {formatDateTime(game.updatedAt)} 更新</p>
          <small>{game.hasCurrentContent ? "Session 创建时完整复制此快照；继续编辑不会改变既有 Session。" : "导入游戏包并验证启用后，即可创建 Session。"}</small>
        </div><button className="text-button" onClick={() => setTab("文件")}>查看文件 <Icon name="arrow"/></button></article>
      </div>
      {canEdit && <div className="content-list">
        <article><span className="timeline-dot"/><div>
          <h3>工作区草稿 <span className="tag waiting">可编辑</span></h3>
          <p>编辑只影响工作区，当前内容保持不可变。</p>
          <small>可验证后原子启用，或直接丢弃草稿。</small>
        </div>
        <span className="workspace-actions">
          <button className="text-button" onClick={() => void run("正在验证", () => validateGame(game.id, game.stateVersion))} disabled={busy !== ""}>验证</button>
          <button className="danger-text" onClick={() => setConfirmDiscard(true)} disabled={busy !== ""}>丢弃工作区</button>
        </span></article>
      </div>}
      {!game.hasCurrentContent && !canEdit && <div className="content-list empty-list"><article><span className="timeline-dot"/><div><h3>这个游戏还没有内容</h3><p>导入一个游戏包开始。</p></div><button className="secondary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>导入游戏包</button></article></div>}
      {canEdit && <div className="panel-foot-actions"><button className="secondary-button" onClick={() => setUploadOpen(true)}><Icon name="upload"/>上传新游戏包替换工作区</button></div>}
      {(validation || displayDiagnostics.length > 0) && <div className={`validation-banner ${blockingCount === 0 ? "ok" : "bad"}`}><Icon name={blockingCount === 0 ? "check" : "warning"}/><p><strong>{blockingCount === 0 ? "验证通过" : "存在阻断启用的诊断"}</strong><small>{validation ? `${validation.diagnostics.length} 条诊断 · 摘要 ${shortDigest(validation.contentDigest)}` : `${displayDiagnostics.length} 条诊断`}</small></p><button className="text-button" onClick={() => setTab("兼容性")}>查看详情 <Icon name="arrow"/></button></div>}
    </section>}

    {tab === "文件" && <GameFilesPanel game={game} onChanged={() => void refresh()}/>}

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
      {canEdit && <div className="diagnostic-foot"><button className="secondary-button" onClick={() => void run("正在验证", () => validateGame(game.id, game.stateVersion))} disabled={busy !== ""}>{busy === "正在验证" ? "验证中…" : "重新验证"}</button></div>}
    </section>}

    {editOpen && <EditGameDialog game={game} onClose={() => setEditOpen(false)} onSaved={() => { setEditOpen(false); void refresh(); }}/>}
    {uploadOpen && <UploadDialog onClose={() => setUploadOpen(false)} games={[game]} initialGameId={game.id}/>}
    {confirmDiscard && <ConfirmDialog title="丢弃工作区草稿？" body="工作区中的所有编辑都会被丢弃，当前内容保持不变。" confirm="确认丢弃" onCancel={() => setConfirmDiscard(false)} onConfirm={() => void run("正在丢弃工作区", () => discardWorkspace(game.id, game.stateVersion))} pending={busy === "正在丢弃工作区"}/>}
    {confirmDelete && <ConfirmDialog title="删除这个游戏？" body="未被 Session 引用的游戏会先执行可恢复的逻辑删除；被引用的游戏会被拒绝删除。" confirm="确认删除" onCancel={() => setConfirmDelete(false)} onConfirm={() => void deleteGameAction()} pending={busy === "正在删除"}/>}
  </>;
}

function GameFilesPanel({ game, onChanged }: { game: GameLibraryItem; onChanged: () => void }) {
  const [scope, setScope] = useState<ContentScope>(game.workspaceStatus === "DRAFT" ? "WORKSPACE" : "CURRENT");
  const [path, setPath] = useState("");
  const [files, setFiles] = useState<GameFileItem[] | null>(null);
  const [filesError, setFilesError] = useState<string | null>(null);
  const [selected, setSelected] = useState<GameTextFile | null>(null);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [readError, setReadError] = useState<string | null>(null);
  const [draft, setDraft] = useState<string | null>(null);
  const [editingNew, setEditingNew] = useState(false);
  const [newPath, setNewPath] = useState("");
  const [saving, setSaving] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [searchResults, setSearchResults] = useState<GameSearchMatch[] | null>(null);
  const [searchNext, setSearchNext] = useState<string | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);

  const workspaceAvailable = game.workspaceStatus === "DRAFT";
  const currentAvailable = game.hasCurrentContent;
  const editable = scope === "WORKSPACE" && workspaceAvailable;

  const loadFiles = useCallback(async (targetScope: ContentScope, targetPath: string) => {
    setFilesError(null);
    try { setFiles(await listFiles(game.id, targetScope, targetPath)); }
    catch (err) { setFilesError(err instanceof Error ? err.message : "无法读取文件列表。"); setFiles([]); }
  }, [game.id]);

  useEffect(() => {
    setPath(""); setSelected(null); setSelectedPath(null); setDraft(null); setReadError(null); setEditingNew(false);
    setSearchResults(null); setSearchNext(null); setSearchQuery("");
    const available = scope === "WORKSPACE" ? workspaceAvailable : currentAvailable;
    if (available) void loadFiles(scope, "");
  }, [scope, loadFiles]);

  const openFile = async (filePath: string) => {
    setSelectedPath(filePath); setSelected(null); setDraft(null); setReadError(null); setEditingNew(false);
    try { const file = await readTextFile(game.id, scope, filePath); setSelected(file); setDraft(file.content); }
    catch (err) { setReadError(err instanceof Error ? err.message : "无法读取文件。"); }
  };

  const save = async () => {
    if (!selected || draft === null) return;
    setSaving(true); setReadError(null);
    try {
      await writeTextFile(game.id, selected.path, draft, game.stateVersion, selected.etag);
      onChanged();
      await openFile(selected.path);
      await loadFiles(scope, path);
    } catch (err) { setReadError(err instanceof Error ? err.message : "保存失败。"); }
    finally { setSaving(false); }
  };

  const createNewFile = async (event: FormEvent) => {
    event.preventDefault();
    const trimmed = newPath.trim();
    if (!trimmed) return;
    setSaving(true); setReadError(null);
    try {
      await writeTextFile(game.id, trimmed, "", game.stateVersion, null, true);
      onChanged();
      setEditingNew(false); setNewPath("");
      await openFile(trimmed);
      await loadFiles(scope, path);
    } catch (err) { setReadError(err instanceof Error ? err.message : "创建失败。"); }
    finally { setSaving(false); }
  };

  const removeSelected = async () => {
    if (!selected) return;
    setSaving(true); setReadError(null);
    try {
      await deletePath(game.id, selected.path, game.stateVersion);
      onChanged();
      setSelected(null); setSelectedPath(null); setDraft(null); setConfirmDelete(false);
      await loadFiles(scope, path);
    } catch (err) { setReadError(err instanceof Error ? err.message : "删除失败。"); }
    finally { setSaving(false); }
  };

  const runSearch = async (cursor?: string | null) => {
    const q = searchQuery.trim();
    if (!q) return;
    setSearching(true); setSearchError(null);
    try {
      const page = await searchFiles(game.id, scope, q, cursor);
      setSearchResults(previous => cursor ? [...(previous ?? []), ...page.items] : page.items);
      setSearchNext(page.nextCursor);
    } catch (err) { setSearchError(err instanceof Error ? err.message : "搜索失败。"); }
    finally { setSearching(false); }
  };

  const segments = path ? path.split("/") : [];
  const goTo = (index: number) => {
    const target = segments.slice(0, index + 1).join("/");
    setPath(target);
    void loadFiles(scope, target);
  };
  const goUp = () => {
    const parent = segments.slice(0, -1).join("/");
    setPath(parent);
    void loadFiles(scope, parent);
  };

  return <div className="file-workspace">
    <div className="file-toolbar">
      <div className="segment-control" aria-label="文件范围">
        <button className={scope === "WORKSPACE" ? "selected" : ""} disabled={!workspaceAvailable} onClick={() => setScope("WORKSPACE")}>工作区</button>
        <button className={scope === "CURRENT" ? "selected" : ""} disabled={!currentAvailable} onClick={() => setScope("CURRENT")}>当前内容</button>
      </div>
      <form className="search-box" onSubmit={(event) => { event.preventDefault(); void runSearch(null); }}>
        <Icon name="search"/><span className="sr-only">搜索文件内容</span>
        <input value={searchQuery} onChange={(event) => setSearchQuery(event.target.value)} placeholder="搜索文本内容…"/>
        <button className="text-button" type="submit" disabled={!searchQuery.trim() || searching}>{searching ? "搜索中…" : "搜索"}</button>
      </form>
      {editable && !editingNew && <button className="secondary-button" onClick={() => { setEditingNew(true); setSelected(null); setDraft(null); setReadError(null); }}><Icon name="plus"/>新建文件</button>}
    </div>
    {searchResults !== null && <div className="panel search-panel">
      <div className="search-head"><h3>搜索结果（{scope === "WORKSPACE" ? "工作区" : "当前内容"}）</h3><button className="text-button" onClick={() => { setSearchResults(null); setSearchNext(null); setSearchError(null); }}>清空</button></div>
      {searchError && <p className="file-error" role="alert">{searchError}</p>}
      {!searchError && searchResults.length === 0 && !searching && <p className="search-empty">没有匹配的内容。</p>}
      {searchResults.map((match, index) => <button className="search-row" key={`${match.path}-${match.line}-${index}`} onClick={() => void openFile(match.path)}><span className="search-path">{match.path}</span><span className="search-loc">第 {match.line} 行</span><p>{match.preview}</p></button>)}
      {searchNext && <button className="text-button search-more" onClick={() => void runSearch(searchNext)} disabled={searching}>{searching ? "加载中…" : "加载更多"}</button>}
    </div>}
    {!workspaceAvailable && !currentAvailable
      ? <div className="panel empty-list-panel"><Icon name="folder" size={26}/><p>这个游戏还没有可浏览的内容。先在「内容」页导入游戏包。</p></div>
      : <section className="panel file-panel">
        <div className="file-tree">
          <h3>{scope === "WORKSPACE" ? "工作区" : "当前内容"}{path ? ` / ${path}` : ""}</h3>
          <div className="file-breadcrumb">
            <button className={path === "" ? "current" : ""} onClick={() => { setPath(""); void loadFiles(scope, ""); }}>根目录</button>
            {segments.map((segment, index) => <button key={index} onClick={() => goTo(index)}>{segment}</button>)}
          </div>
          {path !== "" && <button className="file-row up" onClick={goUp}><Icon name="arrow" size={15}/>上一级</button>}
          {filesError && <p className="file-error" role="alert">{filesError}</p>}
          {files?.map(item => item.isDirectory
            ? <button className="file-row" key={item.path} onClick={() => { setPath(item.path); void loadFiles(scope, item.path); }}><Icon name="folder"/><span>{item.path.split("/").pop()}</span><span className="file-meta">目录</span></button>
            : <button className={`file-row ${selectedPath === item.path ? "selected" : ""}`} key={item.path} onClick={() => void openFile(item.path)}><Icon name="book"/><span>{item.path.split("/").pop()}</span><span className="file-meta">{formatBytes(item.bytes)}</span></button>)}
          {files && files.length === 0 && !filesError && <p className="file-empty">此目录为空。</p>}
        </div>
        <div className="editor-preview">
          {editingNew
            ? <div className="editor-bar"><span>新建文件</span><span>工作区 · UTF-8</span></div>
            : selected ? <div className="editor-bar"><span>{selected.path}</span><span>{selected.encoding}{selected.hasBom ? " BOM" : ""} · {editable ? formatBytes(selected.bytes) : "只读"}</span><span className="editor-actions">{editable && <><button className="text-button" onClick={() => setConfirmDelete(true)}>删除</button><button className="primary-button small" onClick={() => void save()} disabled={saving || draft === selected.content}>{saving ? "保存中…" : "保存"}</button></>}</span></div>
            : readError ? <div className="editor-error" role="alert"><Icon name="warning"/><p>{readError}</p></div>
            : <div className="editor-empty"><Icon name="book" size={26}/><p>选择左侧文件查看内容</p>{!editable && <small>当前内容是只读快照；切换到工作区可编辑。</small>}</div>}
          {editingNew && <form className="new-file-form" onSubmit={createNewFile}><label><span>文件路径（相对工作区根目录）</span><input value={newPath} onChange={(event) => setNewPath(event.target.value)} placeholder="ERB/NEW.ERB" autoFocus required/></label><div className="form-actions"><button className="secondary-button" type="button" onClick={() => setEditingNew(false)}>取消</button><button className="primary-button" disabled={saving || !newPath.trim()}>{saving ? "创建中…" : "创建文件"}</button></div></form>}
          {selected && <textarea className="file-editor" value={draft ?? ""} onChange={(event) => setDraft(event.target.value)} readOnly={!editable} spellCheck={false} aria-label={`编辑 ${selected.path}`}/>}
          {readError && selected && <p className="file-error" role="alert">{readError}</p>}
        </div>
      </section>}
    {confirmDelete && selected && <ConfirmDialog title={`删除 ${selected.path}？`} body="此操作会从工作区移除该文件，且无法撤销。" confirm="确认删除" onCancel={() => setConfirmDelete(false)} onConfirm={() => void removeSelected()} pending={saving}/>}
  </div>;
}

function SessionsPage() {
  const [filter, setFilter] = useState("全部");
  const shown = initialSessions.filter(s => filter === "全部" || (filter === "活动中" ? s.state === "RUNNING" : s.state === "CLOSED"));
  return <>
    <PageHeader eyebrow="SESSIONS" title="游戏 Session" description="浏览器离开后，活动 Session 仍会继续运行并等待你回来。" actions={<Link className="primary-button" to="/sessions/new"><Icon name="plus"/>创建 Session</Link>}/>
    <div className="session-stats"><article><span className="pulse-dot"/><div><strong>2</strong><small>占用 Worker</small></div></article><article><Icon name="clock"/><div><strong>1</strong><small>等待输入</small></div></article><article><Icon name="archive"/><div><strong>3</strong><small>Session 总数</small></div></article></div>
    <div className="toolbar"><div className="segment-control">{["全部", "活动中", "已关闭"].map(item => <button key={item} onClick={() => setFilter(item)} className={filter === item ? "selected" : ""}>{item}</button>)}</div></div>
    <section className="session-list">
      {shown.map(session => <article key={session.id} className="session-row"><span className={`session-art ${session.color}`}>{session.glyph}</span><div className="session-main"><div><h2>{session.name}</h2><p>{session.game} <span>·</span> {session.sourceContentDigest}</p></div><div className="session-badges"><Status state={session.state}/>{session.waiting && <span className="tag waiting"><Icon name="clock" size={13}/>等待输入</span>}</div></div><div className="session-meta"><span>最后活动</span><strong>{session.activity}</strong></div><div className="session-meta"><span>本次时长</span><strong>{session.played}</strong></div>{session.state === "CLOSED" ? <Link className="secondary-button" to="/saves">查看存档</Link> : <Link className="play-button" to={`/sessions/${session.id}`}><Icon name="play" size={17}/>继续游戏</Link>}</article>)}
    </section>
    <div className="info-banner"><Icon name="spark"/><p><strong>Session 与浏览器连接相互独立</strong><small>关闭标签页不会停止游戏。请在不再需要时显式关闭 Session，以释放运行配额。</small></p></div>
  </>;
}

function Status({ state }: { state: string }) {
  const label = state === "RUNNING" ? "运行中" : "已关闭";
  return <span className={`status-pill ${state.toLowerCase()}`}><i/>{label}</span>;
}

function NewSessionPage() {
  const navigate = useNavigate();
  const [name, setName] = useState("周目三 · 新旅程");
  const [creating, setCreating] = useState(false);
  const submit = (event: FormEvent) => { event.preventDefault(); setCreating(true); window.setTimeout(() => navigate("/sessions/sess-world"), 650); };
  return <div className="narrow-page"><div className="backline"><Link to="/sessions">← 返回 Session</Link></div><PageHeader eyebrow="NEW SESSION" title="创建 Session" description="每个 Session 都拥有独立、持久的游戏目录与原生存档。"/>
    <form className="form-panel" onSubmit={submit}><label><span>Session 名称</span><input value={name} onChange={e => setName(e.target.value)} required/></label><label><span>游戏</span><select defaultValue="world"><option value="world">ERA: The World</option><option value="megaten">ERA Megaten</option><option value="training">ERA Training</option></select></label><div className="form-explain"><Icon name="archive"/><p><strong>将创建私有 SessionRoot</strong><small>创建时完整复制游戏当时的当前内容；游戏后续编辑不会改变这个 Session。</small></p></div><div className="form-actions"><Link className="secondary-button" to="/sessions">取消</Link><button className="primary-button" disabled={creating}>{creating ? <><span className="mini-spinner"/>正在启动 Worker…</> : <><Icon name="play"/>创建并开始</>}</button></div></form>
  </div>;
}

function ConsolePage() {
  const [connected, setConnected] = useState(true);
  const [input, setInput] = useState("");
  const [log, setLog] = useState<string[]>([]);
  const [closing, setClosing] = useState(false);
  const [displayMode, setDisplayMode] = useState<"modern" | "compatibility">("modern");
  const submit = (value: string) => { if (!value.trim()) return; setLog(previous => [...previous, `> ${value}`, "港口的风带来微咸的气息。接下来要去哪里？"]); setInput(""); };
  return <div className={`console-page ${displayMode === "compatibility" ? "compatibility-mode" : "modern-mode"}`}>
    <header className="console-header"><div className="console-title"><Link className="icon-button" to="/sessions" aria-label="返回 Session"><span className="back-arrow">←</span></Link><span className="session-art coral">世</span><div><h1>周目二 · 港口存档</h1><p>ERA: The World · v2.14.7</p></div></div><div className="console-controls"><div className="display-mode-toggle" aria-label="控制台显示模式"><button aria-pressed={displayMode === "modern"} onClick={() => setDisplayMode("modern")}>现代</button><button aria-pressed={displayMode === "compatibility"} onClick={() => setDisplayMode("compatibility")}>兼容</button></div><button className="connection-chip" onClick={() => setConnected(!connected)}><span className={connected ? "online" : "offline"}/>{connected ? "实时连接" : "连接中断"}</button><button className="icon-button" aria-label="Session 设置"><Icon name="settings"/></button><button className="danger-text" onClick={() => setClosing(true)}>关闭 Session</button></div></header>
    {!connected && <div className="reconnect-banner"><span className="mini-spinner"/><p><strong>连接已中断，正在恢复…</strong><small>游戏仍在服务器上运行，你的输入会在重新连接后恢复。</small></p><button onClick={() => setConnected(true)}>立即重试</button></div>}
    <div className="console-layout">
      {displayMode === "modern"
        ? <ModernConsole log={log} onSubmit={submit}/>
        : <CompatibilityConsole log={log} onSubmit={submit}/>}
      <aside className="console-aside"><div className="aside-block"><p className="aside-title">SESSION</p><dl><div><dt>状态</dt><dd><Status state="RUNNING"/></dd></div><div><dt>浏览器连接</dt><dd>{connected ? "已连接" : "正在重连"}</dd></div><div><dt>运行时长</dt><dd>3 小时 24 分</dd></div><div><dt>存档布局</dt><dd>sav/</dd></div><div><dt>输出序号</dt><dd>#18,429</dd></div></dl></div><div className="aside-block"><p className="aside-title">QUICK ACTIONS</p><Link to="/saves"><Icon name="save"/>查看存档<Icon name="arrow"/></Link><button><Icon name="download"/>保存控制台记录<Icon name="arrow"/></button></div><div className="aside-note"><Icon name="clock"/><p><strong>正在等待输入</strong><small>其他已连接设备也能看到此提示，第一个有效回答会生效。</small></p></div></aside>
    </div>
    <form className="console-input" onSubmit={e => { e.preventDefault(); submit(input); }}><div className="input-inner"><label><span className="sr-only">输入游戏指令</span><input value={input} onChange={e => setInput(e.target.value)} placeholder="输入选项编号或文字…" disabled={!connected}/></label><span className="input-hint">Enter 发送</span><button className="primary-button" disabled={!connected || !input.trim()}>发送 <Icon name="arrow"/></button></div></form>
    {closing && <ConfirmDialog title="关闭这个 Session？" body="Worker 会停止，之后不能从当前指令继续；SessionRoot 和其中的原生存档会被保留。" confirm="确认关闭" onCancel={() => setClosing(false)}/>}
  </div>;
}

function ModernConsole({ log, onSubmit }: { log: string[]; onSubmit: (value: string) => void }) {
  return <section className="game-console" aria-label="游戏控制台（现代模式）" aria-live="polite"><div className="console-paper">
    <div className="chapter-mark"><span>03</span><i/></div><p className="narration muted">—— 海风历 1024 年，夏月第七日 ——</p><h2>港口都市 · 阿尔忒弥斯</h2><p className="narration">午后的阳光穿过百叶窗，在木质地板上留下细长的光斑。远处传来海鸟的鸣叫，与码头工人的号子混在一起。</p><p className="speaker">艾莉西亚</p><p className="dialogue">“你终于醒了。今天可有不少事情要做呢。”</p><div className="stat-board"><div><span>体力</span><strong>842 / 1,000</strong><i><b style={{width:"84%"}}/></i></div><div><span>心情</span><strong>平静</strong><i><b className="mood" style={{width:"68%"}}/></i></div><div><span>时间</span><strong>14:20</strong></div></div><p className="prompt-title">你打算怎么做？</p><div className="choice-grid"><button onClick={() => onSubmit("前往港口市场")}><kbd>1</kbd><span><strong>前往港口市场</strong><small>也许能找到一些有用的东西</small></span><Icon name="arrow"/></button><button onClick={() => onSubmit("留在旅店休息")}><kbd>2</kbd><span><strong>留在旅店休息</strong><small>恢复体力，推进时间</small></span><Icon name="arrow"/></button><button onClick={() => onSubmit("和艾莉西亚交谈")}><kbd>3</kbd><span><strong>和艾莉西亚交谈</strong><small>询问关于委托的消息</small></span><Icon name="arrow"/></button></div>{log.map((line, index) => line.startsWith(">") ? <p className="player-input" key={index}>{line}</p> : <p className="narration" key={index}>{line}</p>)}<div className="scroll-anchor"/>
  </div></section>;
}

function CompatibilityConsole({ log, onSubmit }: { log: string[]; onSubmit: (value: string) => void }) {
  return <section className="game-console classic-console" aria-label="游戏控制台（兼容模式）" aria-live="polite"><div className="classic-screen">
    <div className="classic-toolbar"><span>Emuera Console</span><span>640 × 480 等宽布局</span></div>
    <div className="classic-output">
      <p className="classic-rule">━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</p>
      <p className="classic-center">海风历 1024 年　夏月第七日</p>
      <p className="classic-center classic-heading">港口都市・阿尔忒弥斯</p>
      <p className="classic-rule">━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</p>
      <p>午后的阳光穿过百叶窗，在木质地板上留下细长的光斑。</p>
      <p>远处传来海鸟的鸣叫，与码头工人的号子混在一起。</p>
      <p className="classic-spacer">　</p>
      <p><span className="classic-name">【艾莉西亚】</span>“你终于醒了。今天可有不少事情要做呢。”</p>
      <p className="classic-spacer">　</p>
      <p>体力：<span className="classic-value">842 / 1000</span>　心情：<span className="classic-value">平静</span>　时间：<span className="classic-value">14:20</span></p>
      <p className="classic-spacer">　</p>
      <p className="classic-prompt">你打算怎么做？</p>
      <div className="classic-choices">
        <button onClick={() => onSubmit("1")}><span>[1]</span> 前往港口市场</button>
        <button onClick={() => onSubmit("2")}><span>[2]</span> 留在旅店休息</button>
        <button onClick={() => onSubmit("3")}><span>[3]</span> 和艾莉西亚交谈</button>
      </div>
      {log.map((line, index) => <p className={line.startsWith(">") ? "classic-user-input" : ""} key={index}>{line}</p>)}
      <p className="classic-caret" aria-hidden="true">■</p>
    </div>
  </div></section>;
}

function SavesPage() {
  const [session, setSession] = useState("closed");
  const [deleteName, setDeleteName] = useState<string | null>(null);
  const files = [{name:"save01.sav", label:"港口 · 第 18 日", size:"2.4 MB", modified:"今天 14:18"},{name:"save02.sav", label:"王都入口 · 第 12 日", size:"2.3 MB", modified:"昨天 22:40"},{name:"global.sav", label:"全局数据", size:"18 KB", modified:"今天 14:18"}];
  const locked = session === "running";
  return <>
    <PageHeader eyebrow="NATIVE SAVES" title="原生存档" description="直接管理每个 SessionRoot 中由 Emuera 创建的存档文件。" actions={<button className="secondary-button" disabled={locked}><Icon name="upload"/>导入存档</button>}/>
    <div className="save-layout"><aside className="save-sessions"><p className="aside-title">选择 SESSION</p><button className={session === "closed" ? "active" : ""} onClick={() => setSession("closed")}><span className="session-art amber">練</span><span><strong>测试 07</strong><small>ERA Training · 已关闭</small></span><Icon name="arrow"/></button><button className={session === "running" ? "active" : ""} onClick={() => setSession("running")}><span className="session-art coral">世</span><span><strong>周目二 · 港口存档</strong><small>ERA: The World · 运行中</small></span><Icon name="arrow"/></button></aside><section className="panel save-panel">
      <div className="panel-heading"><div><h2>{locked ? "周目二 · 港口存档" : "测试 07"}</h2><p>原生布局：<code>{locked ? "sav/" : "GameRoot"}</code> · {locked ? "3 个文件" : "3 个文件，共 4.7 MB"}</p></div><Status state={locked ? "RUNNING" : "CLOSED"}/></div>
      {locked && <div className="locked-banner"><Icon name="warning"/><p><strong>Session 运行时存档由 Worker 独占</strong><small>你仍可下载当前文件，但上传、重命名和删除需要先关闭 Session。</small></p><Link to="/sessions/sess-world">前往 Session</Link></div>}
      <div className="save-table"><div className="save-table-head"><span>文件</span><span>大小</span><span>修改时间</span><span/></div>{files.map(file => <div className="save-file" key={file.name}><span className="file-icon"><Icon name="save"/></span><span><strong>{file.name}</strong><small>{file.label}</small></span><span>{file.size}</span><span>{file.modified}</span><span className="file-actions"><button aria-label={`下载 ${file.name}`}><Icon name="download"/></button><button aria-label={`删除 ${file.name}`} disabled={locked} onClick={() => setDeleteName(file.name)}><Icon name="close"/></button></span></div>)}</div>
    </section></div>
    {deleteName && <ConfirmDialog title={`删除 ${deleteName}？`} body="此操作会删除 SessionRoot 中的原生文件，且不能撤销。其他 Session 的存档不会受到影响。" confirm="删除存档" onCancel={() => setDeleteName(null)}/>}
  </>;
}

function AdminPage() {
  return <><PageHeader eyebrow="SYSTEM" title="运行状态" description="观察单机实例、Supervisor 与每个活动 Worker 的健康状态。" actions={<span className="updated"><i/>刚刚更新</span>}/><section className="health-grid"><article><span className="summary-icon mint"><Icon name="server"/></span><div><p>API</p><strong>健康</strong><small>12 ms · v0.1.0-dev</small></div></article><article><span className="summary-icon mint"><Icon name="settings"/></span><div><p>Supervisor</p><strong>健康</strong><small>最后心跳 2 秒前</small></div></article><article><span className="summary-icon blue"><Icon name="gamepad"/></span><div><p>Worker</p><strong>2 / 4</strong><small>50% 活动配额</small></div></article><article><span className="summary-icon peach"><Icon name="archive"/></span><div><p>数据目录</p><strong>18.6 GB</strong><small>剩余 74%</small></div></article></section><section className="panel"><div className="panel-heading"><div><h2>活动 Worker</h2><p>每个活动 Session 由一个独立进程持有。</p></div><button className="secondary-button">查看审计记录</button></div><div className="worker-table"><div className="worker-head"><span>Session / Worker</span><span>状态</span><span>CPU</span><span>内存</span><span>输出速率</span><span>心跳</span></div>{[{name:"周目二 · 港口存档",id:"wrk_8fd2 · epoch 3",cpu:"2.4%",mem:"286 MB",rate:"18 evt/s"},{name:"初见流程",id:"wrk_1ac4 · epoch 1",cpu:"0.8%",mem:"241 MB",rate:"0 evt/s"}].map(w=><div className="worker-row" key={w.id}><span><strong>{w.name}</strong><small>{w.id}</small></span><span><Status state="RUNNING"/></span><span>{w.cpu}</span><span>{w.mem}</span><span>{w.rate}</span><span>2 秒前</span></div>)}</div></section><div className="security-banner"><span><Icon name="check"/></span><p><strong>运行环境隔离检查已通过</strong><small>namespace、cgroup v2、seccomp 与私有文件系统边界均已启用。</small></p><button>查看详情</button></div></>;
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
  return <><PageHeader eyebrow="PREFERENCES" title="设置" description="调整浏览器体验与账户偏好；运行时配置由游戏的当前内容决定。"/><section className="panel settings-panel"><h2>外观与交互</h2><label className="setting-row"><span><strong>跟随游戏自动滚动</strong><small>仅当你已经靠近控制台底部时自动滚动</small></span><input type="checkbox" defaultChecked/></label><label className="setting-row"><span><strong>减少动态效果</strong><small>关闭界面过渡与加载动画</small></span><input type="checkbox"/></label><label className="setting-row"><span><strong>控制台文字大小</strong><small>仅影响游戏输出，不改变应用界面</small></span><select defaultValue="medium"><option value="small">较小</option><option value="medium">标准</option><option value="large">较大</option></select></label></section></>;
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

export function App() {
  return <Routes>
    <Route path="/login" element={<LoginPage/>}/>
    <Route path="/change-password" element={<ChangePasswordPage/>}/>
    <Route path="*" element={<RequireAuthenticated><AppShell><Routes>
      <Route path="/games" element={<GamesPage/>}/>
      <Route path="/games/:gameId" element={<GameDetailPage/>}/>
      <Route path="/sessions" element={<SessionsPage/>}/>
      <Route path="/sessions/new" element={<NewSessionPage/>}/>
      <Route path="/sessions/:sessionId" element={<ConsolePage/>}/>
      <Route path="/saves" element={<SavesPage/>}/>
      <Route path="/admin" element={<RequireAdmin><AdminPage/></RequireAdmin>}/>
      <Route path="/admin/users" element={<RequireAdmin><AdminUsersPage/></RequireAdmin>}/>
      <Route path="/settings" element={<SettingsPage/>}/>
      <Route path="*" element={<Navigate to="/games" replace/>}/>
    </Routes></AppShell></RequireAuthenticated>}/>
  </Routes>;
}
