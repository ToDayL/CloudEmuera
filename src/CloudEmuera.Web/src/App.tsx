import { FormEvent, ReactNode, useEffect, useState } from "react";
import {
  Link,
  NavLink,
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
} from "react-router-dom";

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

const games = [
  { id: "era-the-world", title: "ERA: The World", subtitle: "世界与少女的物语", version: "v2.14.7", runtime: "1824+v18", updated: "今天 14:32", sessions: 2, color: "coral", glyph: "世", diagnostics: 2 },
  { id: "era-megaten", title: "ERA Megaten", subtitle: "女神转生改造版", version: "v0.312", runtime: "EM+EE v56", updated: "昨天 21:08", sessions: 1, color: "violet", glyph: "M", diagnostics: 0 },
  { id: "era-training", title: "ERA Training", subtitle: "私人训练场", version: "v1.8.2", runtime: "1824+v18", updated: "8 月 2 日", sessions: 0, color: "amber", glyph: "練", diagnostics: 1 },
];

const initialSessions = [
  { id: "sess-world", name: "周目二 · 港口存档", game: "ERA: The World", version: "v2.14.7", state: "RUNNING", waiting: true, activity: "刚刚", played: "3 小时 24 分", glyph: "世", color: "coral" },
  { id: "sess-megaten", name: "初见流程", game: "ERA Megaten", version: "v0.312", state: "DETACHED", waiting: true, activity: "12 分钟前", played: "46 分钟", glyph: "M", color: "violet" },
  { id: "sess-training", name: "测试 07", game: "ERA Training", version: "v1.8.2", state: "CLOSED", waiting: false, activity: "昨天 23:14", played: "1 小时 08 分", glyph: "練", color: "amber" },
];

function Logo() {
  return <Link className="brand" to="/games" aria-label="CloudEmuera 首页"><span className="brand-mark">C</span><span>CloudEmuera</span></Link>;
}

function AppShell({ children }: { children: ReactNode }) {
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
        <p className="nav-caption second">SYSTEM</p>
        <NavLink to="/admin" className={({ isActive }) => isActive ? "active" : ""}><Icon name="server"/><span>运行状态</span></NavLink>
        <NavLink to="/settings" className={({ isActive }) => isActive ? "active" : ""}><Icon name="settings"/><span>设置</span></NavLink>
      </nav>
      <div className="sidebar-foot">
        <div className="quota-label"><span>运行中的 Session</span><strong>2 / 4</strong></div>
        <div className="quota-track"><span/></div>
        <button className="profile-button"><span className="avatar">林</span><span><strong>林间</strong><small>玩家账户</small></span><Icon name="more"/></button>
      </div>
    </aside>
    {mobileOpen && <button className="sidebar-scrim" aria-label="关闭导航" onClick={() => setMobileOpen(false)}/>}
    <main className="main-content">{children}</main>
  </div>;
}

function PageHeader({ eyebrow, title, description, actions }: { eyebrow?: string; title: string; description: string; actions?: ReactNode }) {
  return <header className="page-header"><div>{eyebrow && <p className="eyebrow">{eyebrow}</p>}<h1>{title}</h1><p>{description}</p></div>{actions && <div className="page-actions">{actions}</div>}</header>;
}

function GamesPage() {
  const [query, setQuery] = useState("");
  const [uploadOpen, setUploadOpen] = useState(false);
  const [visibility, setVisibility] = useState("全部游戏");
  const filtered = games.filter((game) => game.title.toLowerCase().includes(query.toLowerCase()) || game.subtitle.includes(query));
  return <>
    <PageHeader eyebrow="LIBRARY" title="游戏库" description="管理游戏包、版本与兼容性，然后开启一段新的旅程。" actions={<button className="primary-button" onClick={() => setUploadOpen(true)}><Icon name="plus"/>导入游戏</button>}/>
    <section className="summary-strip" aria-label="游戏库概览">
      <div><span className="summary-icon peach"><Icon name="book"/></span><p><strong>3</strong><small>游戏</small></p></div>
      <div><span className="summary-icon mint"><Icon name="archive"/></span><p><strong>8</strong><small>已发布版本</small></p></div>
      <div><span className="summary-icon blue"><Icon name="gamepad"/></span><p><strong>2</strong><small>运行中</small></p></div>
      <div className="tip"><Icon name="spark"/><p><strong>版本发布后不可修改</strong><small>编辑已发布版本会自动创建新草稿</small></p></div>
    </section>
    <div className="toolbar">
      <label className="search-box"><Icon name="search"/><span className="sr-only">搜索游戏</span><input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="搜索游戏或版本…"/></label>
      <div className="segment-control" aria-label="游戏筛选">{["全部游戏", "我的游戏", "服务器共享"].map(item => <button className={visibility === item ? "selected" : ""} onClick={() => setVisibility(item)} key={item}>{item}</button>)}</div>
    </div>
    <section className="game-grid" aria-label="游戏列表">
      {filtered.map((game) => <article className="game-card" key={game.id}>
        <div className={`game-cover ${game.color}`}><span className="cover-grid"/><span className="cover-glyph">{game.glyph}</span><span className="cover-version">{game.version}</span></div>
        <div className="game-card-body">
          <div className="card-title-row"><div><h2>{game.title}</h2><p>{game.subtitle}</p></div><button className="icon-button subtle" aria-label={`${game.title} 更多操作`}><Icon name="more"/></button></div>
          <div className="tag-row"><span className="tag">{game.runtime}</span>{game.diagnostics === 0 ? <span className="tag success"><Icon name="check" size={13}/>验证通过</span> : <span className="tag warning"><Icon name="warning" size={13}/>{game.diagnostics} 条提醒</span>}</div>
          <div className="card-meta"><span>更新于 {game.updated}</span><span>{game.sessions} 个 Session</span></div>
          <div className="card-actions"><Link className="secondary-button" to={`/games/${game.id}`}>管理版本</Link><Link className="play-button" to={`/sessions/new?game=${game.id}`}><Icon name="play" size={17}/>开始游戏</Link></div>
        </div>
      </article>)}
      <button className="game-card add-card" onClick={() => setUploadOpen(true)}><span><Icon name="upload" size={24}/></span><strong>导入新的游戏包</strong><small>支持安全校验的 ZIP 文件</small></button>
    </section>
    {uploadOpen && <UploadDialog onClose={() => setUploadOpen(false)}/>}
  </>;
}

function UploadDialog({ onClose }: { onClose: () => void }) {
  const [step, setStep] = useState<"choose" | "scan" | "done">("choose");
  const advance = () => { setStep("scan"); window.setTimeout(() => setStep("done"), 700); };
  return <div className="modal-layer" role="presentation"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="upload-title">
    <button className="icon-button modal-close" onClick={onClose} aria-label="关闭"><Icon name="close"/></button>
    <p className="eyebrow">NEW GAME</p><h2 id="upload-title">导入游戏包</h2><p className="modal-intro">文件会先进入隔离区，完成路径、编码和运行时兼容性检查后才可发布。</p>
    {step === "choose" && <><button className="drop-zone" onClick={advance}><span><Icon name="upload" size={28}/></span><strong>选择 ZIP 文件</strong><small>或拖放到这里 · 最大 1 GB</small></button><div className="modal-note"><Icon name="warning"/><p><strong>请确认你拥有游戏内容的使用权</strong><small>CloudEmuera 不提供或分发游戏资源。</small></p></div></>}
    {step === "scan" && <div className="scan-state"><span className="spinner"/><h3>正在检查 era-the-world.zip</h3><p>扫描 2,438 个文件与目录结构…</p><div className="progress"><span/></div></div>}
    {step === "done" && <div className="scan-state done"><span className="success-ring"><Icon name="check" size={30}/></span><h3>游戏包已安全解压</h3><p>识别到 Emuera 1824+v18 · 2 条非阻断提醒</p><Link className="primary-button" to="/games/era-the-world" onClick={onClose}>查看草稿并发布<Icon name="arrow"/></Link></div>}
  </section></div>;
}

function GameDetailPage() {
  const [tab, setTab] = useState("版本");
  return <>
    <div className="backline"><Link to="/games">← 返回游戏库</Link></div>
    <section className="game-detail-hero"><div className="game-cover coral compact"><span className="cover-grid"/><span className="cover-glyph">世</span></div><div><p className="eyebrow">PRIVATE GAME</p><h1>ERA: The World</h1><p>世界与少女的物语</p><div className="tag-row"><span className="tag success"><Icon name="check" size={13}/>最新版本可运行</span><span className="tag">共 3 个版本</span></div></div><Link className="primary-button" to="/sessions/new?game=era-the-world"><Icon name="play"/>创建 Session</Link></section>
    <div className="detail-tabs">{["版本", "文件", "兼容性"].map(item => <button className={tab === item ? "active" : ""} onClick={() => setTab(item)} key={item}>{item}</button>)}</div>
    {tab === "版本" && <section className="panel"><div className="panel-heading"><div><h2>版本历史</h2><p>已发布版本保持不可变，活动 Session 会固定到明确版本。</p></div><button className="secondary-button"><Icon name="plus"/>从 v2.14.7 创建草稿</button></div><div className="version-list">
      <article><span className="timeline-dot live"/><div><h3>v2.14.7 <span className="tag success">当前版本</span></h3><p>修复港口事件分支与图片引用大小写</p><small>今天 14:32 · sha256:8f2a…d391</small></div><button className="text-button">查看文件 <Icon name="arrow"/></button></article>
      <article><span className="timeline-dot"/><div><h3>v2.14.6</h3><p>更新 CSV 数据与简体中文文本</p><small>7 月 28 日 · 1 个 Session 固定此版本</small></div><button className="text-button">查看文件 <Icon name="arrow"/></button></article>
      <article><span className="timeline-dot"/><div><h3>v2.13.0</h3><p>首次导入 CloudEmuera</p><small>7 月 14 日 · 无引用</small></div><button className="text-button">查看文件 <Icon name="arrow"/></button></article>
    </div></section>}
    {tab === "文件" && <section className="panel file-panel"><div className="file-tree"><h3>v2.14.7</h3>{["CSV", "ERB", "resources", "emuera.config"].map((file, i) => <button key={file}><Icon name={i < 3 ? "folder" : "book"}/>{file}<span>{i < 3 ? "›" : "4 KB"}</span></button>)}</div><div className="editor-preview"><div className="editor-bar"><span>emuera.config</span><span>UTF-8 · 只读</span></div><pre><code><span className="code-comment">; CloudEmuera runtime config</span>{"\n"}<span className="code-key">WindowTitle</span>:ERA The World{"\n"}<span className="code-key">UseSaveFolder</span>:YES{"\n"}<span className="code-key">TextDrawingMode</span>:WINAPI</code></pre></div></section>}
    {tab === "兼容性" && <section className="panel diagnostics"><div className="diagnostic-summary"><span className="score">98</span><div><h2>兼容性良好</h2><p>没有阻断发布的问题，发现 2 条建议。</p></div></div>{["2 个文件使用 Shift-JIS 编码，运行时将保持原编码", "发现 1 个仅大小写不同的资源引用，Linux 兼容映射已记录"].map(x => <div className="diagnostic-row" key={x}><Icon name="warning"/><span>{x}</span><small>建议</small></div>)}</section>}
  </>;
}

function SessionsPage() {
  const [filter, setFilter] = useState("全部");
  const shown = initialSessions.filter(s => filter === "全部" || (filter === "活动中" ? ["RUNNING", "DETACHED"].includes(s.state) : s.state === "CLOSED"));
  return <>
    <PageHeader eyebrow="SESSIONS" title="游戏 Session" description="浏览器离开后，活动 Session 仍会继续运行并等待你回来。" actions={<Link className="primary-button" to="/sessions/new"><Icon name="plus"/>创建 Session</Link>}/>
    <div className="session-stats"><article><span className="pulse-dot"/><div><strong>2</strong><small>占用 Worker</small></div></article><article><Icon name="clock"/><div><strong>1</strong><small>等待输入</small></div></article><article><Icon name="archive"/><div><strong>3</strong><small>Session 总数</small></div></article></div>
    <div className="toolbar"><div className="segment-control">{["全部", "活动中", "已关闭"].map(item => <button key={item} onClick={() => setFilter(item)} className={filter === item ? "selected" : ""}>{item}</button>)}</div></div>
    <section className="session-list">
      {shown.map(session => <article key={session.id} className="session-row"><span className={`session-art ${session.color}`}>{session.glyph}</span><div className="session-main"><div><h2>{session.name}</h2><p>{session.game} <span>·</span> {session.version}</p></div><div className="session-badges"><Status state={session.state}/>{session.waiting && <span className="tag waiting"><Icon name="clock" size={13}/>等待输入</span>}</div></div><div className="session-meta"><span>最后活动</span><strong>{session.activity}</strong></div><div className="session-meta"><span>本次时长</span><strong>{session.played}</strong></div>{session.state === "CLOSED" ? <Link className="secondary-button" to="/saves">查看存档</Link> : <Link className="play-button" to={`/sessions/${session.id}`}><Icon name="play" size={17}/>{session.state === "DETACHED" ? "重新连接" : "继续游戏"}</Link>}</article>)}
    </section>
    <div className="info-banner"><Icon name="spark"/><p><strong>Session 与浏览器连接相互独立</strong><small>关闭标签页不会停止游戏。请在不再需要时显式关闭 Session，以释放运行配额。</small></p></div>
  </>;
}

function Status({ state }: { state: string }) {
  const label = state === "RUNNING" ? "已连接" : state === "DETACHED" ? "后台运行" : "已关闭";
  return <span className={`status-pill ${state.toLowerCase()}`}><i/>{label}</span>;
}

function NewSessionPage() {
  const navigate = useNavigate();
  const [name, setName] = useState("周目三 · 新旅程");
  const [creating, setCreating] = useState(false);
  const submit = (event: FormEvent) => { event.preventDefault(); setCreating(true); window.setTimeout(() => navigate("/sessions/sess-world"), 650); };
  return <div className="narrow-page"><div className="backline"><Link to="/sessions">← 返回 Session</Link></div><PageHeader eyebrow="NEW SESSION" title="创建 Session" description="每个 Session 都拥有独立、持久的游戏目录与原生存档。"/>
    <form className="form-panel" onSubmit={submit}><label><span>Session 名称</span><input value={name} onChange={e => setName(e.target.value)} required/></label><label><span>游戏</span><select defaultValue="world"><option value="world">ERA: The World</option><option value="megaten">ERA Megaten</option><option value="training">ERA Training</option></select></label><label><span>固定版本</span><select defaultValue="2.14.7"><option>v2.14.7 · 推荐</option><option>v2.14.6</option></select></label><div className="form-explain"><Icon name="archive"/><p><strong>将创建私有 SessionRoot</strong><small>已发布版本会完整复制，原游戏版本和其他 Session 不会被改动。</small></p></div><div className="form-actions"><Link className="secondary-button" to="/sessions">取消</Link><button className="primary-button" disabled={creating}>{creating ? <><span className="mini-spinner"/>正在启动 Worker…</> : <><Icon name="play"/>创建并开始</>}</button></div></form>
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
      <aside className="console-aside"><div className="aside-block"><p className="aside-title">SESSION</p><dl><div><dt>状态</dt><dd><Status state={connected ? "RUNNING" : "DETACHED"}/></dd></div><div><dt>运行时长</dt><dd>3 小时 24 分</dd></div><div><dt>存档布局</dt><dd>sav/</dd></div><div><dt>输出序号</dt><dd>#18,429</dd></div></dl></div><div className="aside-block"><p className="aside-title">QUICK ACTIONS</p><Link to="/saves"><Icon name="save"/>查看存档<Icon name="arrow"/></Link><button><Icon name="download"/>保存控制台记录<Icon name="arrow"/></button></div><div className="aside-note"><Icon name="clock"/><p><strong>正在等待输入</strong><small>其他已连接设备也能看到此提示，第一个有效回答会生效。</small></p></div></aside>
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
  return <><PageHeader eyebrow="SYSTEM" title="运行状态" description="观察单机实例、Supervisor 与每个活动 Worker 的健康状态。" actions={<span className="updated"><i/>刚刚更新</span>}/><section className="health-grid"><article><span className="summary-icon mint"><Icon name="server"/></span><div><p>API</p><strong>健康</strong><small>12 ms · v0.1.0-dev</small></div></article><article><span className="summary-icon mint"><Icon name="settings"/></span><div><p>Supervisor</p><strong>健康</strong><small>最后心跳 2 秒前</small></div></article><article><span className="summary-icon blue"><Icon name="gamepad"/></span><div><p>Worker</p><strong>2 / 4</strong><small>50% 活动配额</small></div></article><article><span className="summary-icon peach"><Icon name="archive"/></span><div><p>数据目录</p><strong>18.6 GB</strong><small>剩余 74%</small></div></article></section><section className="panel"><div className="panel-heading"><div><h2>活动 Worker</h2><p>每个活动 Session 由一个独立进程持有。</p></div><button className="secondary-button">查看审计记录</button></div><div className="worker-table"><div className="worker-head"><span>Session / Worker</span><span>状态</span><span>CPU</span><span>内存</span><span>输出速率</span><span>心跳</span></div>{[{name:"周目二 · 港口存档",id:"wrk_8fd2 · epoch 3",cpu:"2.4%",mem:"286 MB",rate:"18 evt/s"},{name:"初见流程",id:"wrk_1ac4 · epoch 1",cpu:"0.8%",mem:"241 MB",rate:"0 evt/s"}].map((w,i)=><div className="worker-row" key={w.id}><span><strong>{w.name}</strong><small>{w.id}</small></span><span><Status state={i ? "DETACHED" : "RUNNING"}/></span><span>{w.cpu}</span><span>{w.mem}</span><span>{w.rate}</span><span>2 秒前</span></div>)}</div></section><div className="security-banner"><span><Icon name="check"/></span><p><strong>运行环境隔离检查已通过</strong><small>namespace、cgroup v2、seccomp 与私有文件系统边界均已启用。</small></p><button>查看详情</button></div></>;
}

function SettingsPage() {
  return <><PageHeader eyebrow="PREFERENCES" title="设置" description="调整浏览器体验与账户偏好；运行时配置由每个游戏版本决定。"/><section className="panel settings-panel"><h2>外观与交互</h2><label className="setting-row"><span><strong>跟随游戏自动滚动</strong><small>仅当你已经靠近控制台底部时自动滚动</small></span><input type="checkbox" defaultChecked/></label><label className="setting-row"><span><strong>减少动态效果</strong><small>关闭界面过渡与加载动画</small></span><input type="checkbox"/></label><label className="setting-row"><span><strong>控制台文字大小</strong><small>仅影响游戏输出，不改变应用界面</small></span><select defaultValue="medium"><option value="small">较小</option><option value="medium">标准</option><option value="large">较大</option></select></label></section></>;
}

function ConfirmDialog({ title, body, confirm, onCancel }: { title: string; body: string; confirm: string; onCancel: () => void }) {
  return <div className="modal-layer"><section className="modal confirm-modal" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title"><span className="confirm-icon"><Icon name="warning"/></span><h2 id="confirm-title">{title}</h2><p>{body}</p><div className="form-actions"><button className="secondary-button" onClick={onCancel}>取消</button><button className="danger-button" onClick={onCancel}>{confirm}</button></div></section></div>;
}

function LoginPage() {
  const navigate = useNavigate();
  return <main className="login-page"><section className="login-story"><Logo/><div><p className="eyebrow">YOUR ERA, ANYWHERE</p><h1>故事不会因为<br/>离开浏览器而暂停。</h1><p>在桌面或手机上继续你的 Emuera 游戏。每段旅程独立运行，安全保存，随时重连。</p></div><small>CloudEmuera · Self-hosted runtime</small></section><section className="login-form-wrap"><form className="login-form" onSubmit={e => {e.preventDefault(); navigate("/games");}}><p className="eyebrow">WELCOME BACK</p><h2>登录 CloudEmuera</h2><p>访问你的游戏库与正在运行的 Session。</p><label><span>用户名</span><input defaultValue="linjian" autoComplete="username"/></label><label><span>密码</span><input type="password" defaultValue="prototype" autoComplete="current-password"/></label><div className="login-options"><label><input type="checkbox" defaultChecked/> 保持登录</label><button type="button">忘记密码？</button></div><button className="primary-button wide">登录 <Icon name="arrow"/></button><div className="demo-note"><Icon name="spark"/><span>原型模式：使用任意账户即可进入</span></div></form></section></main>;
}

export function App() {
  return <Routes>
    <Route path="/login" element={<LoginPage/>}/>
    <Route path="*" element={<AppShell><Routes>
      <Route path="/games" element={<GamesPage/>}/>
      <Route path="/games/:gameId" element={<GameDetailPage/>}/>
      <Route path="/sessions" element={<SessionsPage/>}/>
      <Route path="/sessions/new" element={<NewSessionPage/>}/>
      <Route path="/sessions/:sessionId" element={<ConsolePage/>}/>
      <Route path="/saves" element={<SavesPage/>}/>
      <Route path="/admin" element={<AdminPage/>}/>
      <Route path="/settings" element={<SettingsPage/>}/>
      <Route path="*" element={<Navigate to="/games" replace/>}/>
    </Routes></AppShell>}/>
  </Routes>;
}
