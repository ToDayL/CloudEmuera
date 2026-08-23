# CloudEmuera 需求与总体设计

| 项目 | 内容 |
| --- | --- |
| 文档状态 | 草案 v0.5 |
| 日期 | 2026-08-12 |
| 对应英文文档 | [requirements.en.md](./requirements.en.md) |
| 目标读者 | 产品、前端、后端、运行时、运维与测试开发者 |

## 1. 文档目的

本文档定义 CloudEmuera 的首版产品需求、系统边界与总体技术设计。CloudEmuera 是由部署者为自己及
其信任参与者运行的单机自托管系统，使多个用户可以通过桌面或移动浏览器上传和管理自己的游戏、
启动独立会话、管理各自存档，并在浏览器断线后重新连接到仍在运行的会话。它保留用户间的应用级
资源授权，但不作为允许敌对租户运行恶意游戏的托管平台；具体信任边界见 ADR-0017。

本文档中的“必须”“应当”“可以”分别表示强制需求、推荐需求和可选能力。带编号的需求用于实现追踪和验收；中英文文档使用相同编号。

## 2. 背景与设计结论

### 2.1 背景

传统 Emuera 是面向桌面环境的单用户应用，解释器、窗口、输入、绘制、文件与存档生命周期存在较强耦合。uEmuera 与 gEmuera 已证明保留 C# 解释器、替换平台与显示层的迁移路线可行，但两者仍以单机客户端为中心，不能直接满足多用户、浏览器重连、资源隔离和服务端运维要求。

### 2.2 版本基线

CloudEmuera 将使用 Emuera.EM+EE 作为运行时基线，并保留面向 `Emuera 1824+v18` 游戏的兼容目标。初始研究基线为 `Emuera.NET 1824+v24+EMv18+EEv56`。正式构建必须在运行时清单中记录具体的上游提交、内置源码修订和 CloudEmuera 源码集成版本，不能仅记录易变的“最新版”。

## 3. 目标与非目标

### 3.1 产品目标

- 通过现代桌面和移动浏览器游玩 Era/Emuera 游戏。
- 支持玩家上传、检查、查看和启用自己拥有的 ERB/CSV/资源游戏包。
- 支持同一用户为同一或不同游戏启动多个并行 Session。
- 让 Session 脱离浏览器连接独立存活，并能够恢复到最新显示与输入状态。
- 为不同用户和 Session 隔离存档、配置、临时文件和运行时状态。
- 尽可能兼容最新 Emuera.EM+EE，同时覆盖常见 `1824+v18` 游戏。
- 为玩家自托管的单容器部署提供可诊断、有界且可备份的工具架构。

### 3.2 MVP 范围

- 本地账户或单一外部身份提供商登录。
- 游戏包上传、校验、查看与启用；不提供浏览器内 ERB/CSV 文件写入能力。
- Session 创建、列表、开启、连接、重连、显式关闭和重新开启。
- 完整结构化支持固定 Emuera.EM+EE 基线中可由浏览器安全表达的文字、行与布局、按钮、HTML/HTML
  Island、图片/Sprite、背景、Shape/CBG、字体、动画和音频语义；明确禁止的宿主能力除外。
- 用户输入、超时输入和移动端软键盘。
- 每个 Session 独立的存档空间，以及存档导入、导出、重命名和删除。
- 管理员查看 Worker/Session 基本状态并强制停止 Session。
- 单个 Docker 容器部署；一个 Web/API 控制面进程直接创建和管理每个活动 Session 的独立 Worker
  进程，并通过挂载的数据目录持久化。

### 3.3 非目标

- 公共游戏商店、内容发现、评分或社区系统。
- 视频桌面串流或远程传输 Godot/Unity 画面。
- 执行任意本地 DLL、程序或不受限制的网络请求。
- 第一阶段的任意指令点进程快照或 Worker 崩溃后从同一条指令继续执行。
- 保证所有 Emuera 分支、非标准补丁和依赖旧缺陷的游戏完全一致。
- 多名玩家共同控制同一游戏的协作玩法。
- 多容器、多 API、多 Worker Host、跨主机横向扩展或无缝迁移。
- 面向互不信任用户开放的游戏托管服务，以及抵御恶意用户通过 Worker/Runtime 漏洞攻击同实例
  其他用户的内核级租户隔离。
- 细粒度用户或进程级 CPU、内存、磁盘、PID、FD 和输出速率调度、计量与计费。

## 4. 角色与权限

### 4.1 玩家

- 查看自己有权访问的游戏。
- 上传游戏包，并管理自己拥有的游戏及其版本。
- 设置自己拥有的游戏的可见性。
- 创建和管理自己的 Session。
- 连接自己的 Session 并提交输入。
- 管理自己的存档。

### 4.2 管理员

- 管理用户与实例级容量配置。
- 查看 Worker、Session 基本健康状态。
- 因故障、安全或维护原因强制停止 Session。
- 管理备份、保留策略和兼容性策略。

## 5. 核心领域模型

| 实体 | 说明 |
| --- | --- |
| User | 身份、角色与偏好 |
| Game | 游戏的稳定身份、所有者、可见性、摄取工作区、当前可运行内容及运行时要求 |
| Session | 用户针对某一 Game 创建的可重连游戏会话；SessionRoot 固定创建时的内容快照 |
| WorkerLease | Session 当前有效 Worker 的路由、租约与 fencing epoch |
| ConsoleSnapshot | 当前有界显示树、输入提示与输出序号 |
| OutputEvent | 对 ConsoleSnapshot 的有序增量 |

关系约束：

```text
User 1 ── N Game
User 1 ── N Session N ── 1 Game
Session 1 ── 0..1 Active WorkerLease
Session 1 ── 1 私有 SessionRoot
```

## 6. 功能需求

### 6.1 身份与授权

- **AUTH-001**：所有非公开 API 和 WebSocket 连接必须经过认证。
- **AUTH-002**：API 必须在每次 Session、存档和游戏文件操作时验证资源所有权或授权，不得仅依赖前端隐藏入口。
- **AUTH-003**：用户不得枚举、读取、控制或删除其他用户的私有 Session 和存档。
- **AUTH-004**：管理员强制停止或修改资源时必须写入审计记录。
- **AUTH-005**：WebSocket 在升级连接和恢复 Session 时必须重新验证身份与访问权。
- **AUTH-006**：全新实例必须仅在持久状态为未初始化时，从部署配置中的管理员 username、email
  和临时 password 原子创建首个管理员；登录只接受 email，首次登录必须修改临时密码。并发启动
  最多创建一次；完成后永久忽略 bootstrap 配置，不得因管理员缺失、禁用或遗失密码而重新执行。

### 6.2 游戏包与 ERB 管理

- **GAME-001**：系统必须支持上传包含 `ERB`、`CSV`、配置及资源文件的游戏包。
- **GAME-002**：上传过程必须拒绝路径穿越、绝对路径、非法符号链接、压缩炸弹和超过配额的文件。
- **GAME-003**：系统必须识别并记录文本文件编码，至少覆盖 Shift-JIS、UTF-8 BOM 和 UTF-8 无 BOM。
- **GAME-004**：每个 Game 只保留一个当前可运行内容；该内容在被替换前必须不可变，并记录内容校验和、启用者、启用时间和运行时配置。系统不得提供 GameVersion、版本标签、版本列表或历史回滚资源。
- **GAME-005**：上传和检查必须写入 Game 的独立摄取工作区；验证并原子启用前不得改变当前可运行内容，也不得改变任何既有 Session 已复制的文件内容。
- **GAME-006**：系统必须提供已上传文件的目录浏览、文本只读查看和文件下载能力；浏览器内创建、编辑、重命名、删除或搜索 ERB/CSV 文件不在 MVP 范围内。
- **GAME-007**：把摄取工作区启用为当前内容前必须执行基础验证，包括目录结构、编码、解析错误、缺失资源和已禁止功能的诊断。
- **GAME-008**：创建 Session 时必须完整复制 Game 当时的当前可运行内容到私有 SessionRoot，并记录源内容摘要和运行时清单快照；运行时清单快照存放在受保护的 SessionRoot metadata 中，数据库不保存完整清单；Game 后续上传替换或重新启用不能隐式改变既有 Session。
- **GAME-009**：游戏可见性至少支持私有和服务器共享；公开市场式发布不在 MVP 范围内。
- **GAME-010**：删除仍被任意 Session 引用的 Game 时必须拒绝；未被引用的 Game 先执行可恢复的逻辑删除，不在普通请求中立即递归删除内容。

### 6.3 Session 管理

- **SESS-001**：用户可以为同一或不同 Game 创建 Session；实例默认最多同时运行 8 个活动 Worker，并最多保留 64 个未启动 Session（`CREATING`、`CLOSED` 和 `CRASHED`），均可通过实例容量配置覆盖；不要求按用户调度或预留活动配额。
- **SESS-002**：每个活动 Session 必须由且仅由一个有效 Runtime 所有；切换 Worker 时必须使用递增 epoch 防止旧 Worker 继续接受输入。
- **SESS-003**：浏览器断开不得自动关闭 Session，也不得清除运行时状态。
- **SESS-004**：Session 在无浏览器连接时必须继续处理已经开始的执行、计时输入和内部计时器。
- **SESS-005**：用户必须能查看 Session 名称、游戏、源内容摘要、状态、创建时间、最后活动时间和当前是否等待输入。
- **SESS-006**：用户必须能够显式关闭 Session。关闭流程必须停止新输入、刷新文件、可选生成最终
  自动存档、终止 Worker并把状态置为 `CLOSED`；不得删除或重建 SessionRoot。
- **SESS-007**：API 必须具备相互独立且幂等的创建、开启和关闭语义，以防网络重试重复创建 Session、启动 Worker 或重复关闭。
- **SESS-008**：除管理员操作、安全策略、资源故障或明确配置的部署策略外，系统不得仅因连接空闲而自动关闭 Session。
- **SESS-009**：管理员必须能够查看活动 Session，并在故障或维护时强制停止其 Worker。
- **SESS-010**：Worker 异常退出时，Session 必须在心跳超时后转为 `CRASHED`，不得继续显示为可运行。
- **SESS-011**：`CLOSED` 和已确认没有旧 Worker 写权限的 `CRASHED` Session 必须能够重新开启。
  每次开启都复用原 SessionRoot、递增 worker epoch 并创建新 Worker；不得重新复制 Game current
  content 或要求用户创建新 Session。
- **SESS-012**：Session 是从创建到显式删除持续存在的资源；开启和关闭只获取或释放 Worker。
  删除 Session 必须与关闭分离，仅允许对 `CLOSED`/`CRASHED` 且没有活动 Worker 或进行中的文件操作的
  Session 显式执行，并且不得因关闭、崩溃、API 重启或浏览器断开自动发生。
- **SESS-013**：用户必须能在创建 Session 和停止态 Session 配置中选择 CloudEmuera 支持并随产品
  分发的具体字体 face；选择必须以不可变 face ID 持久化并用于后续 Worker。运行态不得修改字体、字号
  或行高；未知、缺失或损坏的 face 必须在 Worker 启动前明确失败，不得回退到宿主或用户字体。
- **SESS-014**：用户必须能在账户启动默认值、Session 创建和停止态 Session 配置中选择 `ORIGIN`、
  `MAX` 或 `CUSTOM` 宽度模式；模式与 Custom 宽度随 Session 持久化，运行态不得修改。
- **SESS-015**：用户必须能在账户启动默认值、Session 创建和停止态 Session 配置中选择是否把游戏
  展示文本中的 U+005C 反斜杠转换为 U+00A5 半角日元符号；选项随 Session 持久化、默认开启，运行态
  不得修改。

### 6.4 游戏显示与交互

- **PLAY-001**：Worker 必须向 API 输出结构化显示事件，而不是把未验证的原始 HTML 直接交给浏览器执行。
- **PLAY-002**：显示模型必须完整支持固定 Emuera.EM+EE 基线中可由浏览器安全表达的文字、前景/
  背景色、字体与布局、换行和临时行更新、按钮、工具提示、图片、Sprite、背景图层、Shape/CBG、
  HTML Island、动画及音频控制；不得以无声 no-op、丢字段或普通文本降级冒充兼容。
- **PLAY-003**：实现的 Emuera HTML 子集必须使用允许列表解析；脚本、事件属性和任意 URL 不得进入浏览器 DOM。
- **PLAY-004**：每个输出事件必须包含 Session 内单调递增的 `sequence`。
- **PLAY-005**：Worker 必须维护一个有界 ConsoleSnapshot；实时批次只需保留到已发送或被更新的快照替代，不要求保存可供历史补发的增量窗口。
- **PLAY-006**：重连必须以当前 `(workerEpoch, snapshotSequence)` 的完整 ConsoleSnapshot 作为新基线，再接收后续实时批次；不要求按客户端 ack 补发历史增量。
- **PLAY-007**：Worker 为每个内部输入请求生成唯一 `promptId`，用于显示、超时和内部终态关联；客户端输入必须包含唯一 `clientMessageId` 和当前 `workerEpoch`，不得把内部 `promptId` 作为提交前置条件。
- **PLAY-008**：Worker 必须在单一输入临界区把客户端输入尝试投递给到达时的当前内部 prompt；没有当前 prompt 时立即返回 `NO_ACTIVE_PROMPT` 并丢弃，不得缓存到未来 prompt。Worker 必须拒绝旧 epoch，并在当前 Worker 的有界内存窗口内对重复 `clientMessageId` 返回首次结果或对异值复用返回 `CONFLICT`；不要求跨 Worker 重启持久去重。
- **PLAY-009**：桌面端必须支持键盘、鼠标和滚动；移动端必须支持触摸按钮、软键盘、视口变化和安全区域。
- **PLAY-010**：显示历史必须有可配置上限。超过上限时应压缩为最新快照并丢弃不可见的早期增量，不能无限增长 Worker 内存。
- **PLAY-011**：同一用户可以同时从多个客户端查看同一 Session；每个当前 Runtime prompt 至多接受一个在线性化点先到的有效输入，不提供独立的客户端控制权租约。
- **PLAY-012**：API 或浏览器消费速度不足时必须使用批处理、背压或快照降级，不能无限堆积消息。
- **PLAY-013**：Runtime 与浏览器必须使用 CloudEmuera 分发的同一不可变字体 face。Worker 加载规范
  TTF，浏览器加载由该 TTF 在构建期无损生成、经独立摘要和度量等价校验的完整 WOFF2；浏览器只按需
  加载当前 face，并以内容摘要长期缓存。MVP 不做字符子集化、不加载游戏包字体、不依赖服务器或用户
  设备字体；游戏内字体请求统一映射到当前 Session 选择的字体，并产生有界兼容性诊断。
- **PLAY-014**：Worker 必须以 `Config.DefaultFont`、`StringMeasure` 和 `Config.DrawableWidth` 作为
  排版权威，输出完整物理 `ConsoleLine`、positioned segment、按钮 `positionX/measuredWidth`、超宽元素
  处理和 `ButtonWrap` 结果。浏览器必须禁用自身自动换行并按后端几何渲染，不得重新决定断行或按钮命中盒。
- **PLAY-015**：Worker 读取游戏配置后必须按启动时浏览器 CSS 宽度计算布局：`ORIGIN` 取浏览器宽度与
  `WindowX` 的较小值，`MAX` 取浏览器宽度与 2000px 的较小值，`CUSTOM` 取浏览器宽度与用户值的较小值。
  运行中视口变化不触发重排；采用新宽度必须关闭并重新开启 Session。
- **PLAY-016**：启用 Era 日元符号兼容时，Worker 必须在展示文本进入权威字体测量与物理排版前把
  U+005C 转换为 U+00A5。转换只作用于可见文本，不得改变按钮提交值、用户输入、prompt 默认值、游戏
  字符串、文件路径、资源 ID 或解析输入；禁用时必须原样显示 U+005C。

### 6.5 存档管理

- **SAVE-001**：存档必须按用户、游戏和 Session 隔离；每个 Session 必须拥有独立的存档工作区，不能与其他 Session 共享物理存档文件。
- **SAVE-002**：Worker 不得通过游戏提供的相对路径越过分配的存档或临时目录。
- **SAVE-003**：用户必须能够按 Session 列出和下载自己的原生存档，并在 Session 没有活动 Worker 时上传、重命名和删除。
- **SAVE-004**：Emuera 必须直接在当前 SessionRoot 中按原生行为写入存档；CloudEmuera 不在运行链路中增加 generation、提交队列或第二份权威存档副本。
- **SAVE-005**：Session 元数据必须记录其 Game、创建时源内容摘要、Runtime 版本、运行时清单快照和私有 SessionRoot；完整运行时清单保存在受保护的 SessionRoot metadata 中，数据库只保留重开和校验所需的摘要/布局字段；原生存档文件作为该 Session 的不透明内容管理，不建立逐次保存 generation。
- **SAVE-006**：导入存档前必须验证文件大小、路径、基本原生文件约束和 Session 权限，并再次确认目标 Session 没有活动 Worker；不要求证明内容与 Game 摘要语义兼容。
- **SAVE-007**：MVP 不提供 Session 之间的直接存档复制；用户可以下载后显式上传到另一个停止态 Session，且系统不得让多个活动 Worker 共享同一物理文件。
- **SAVE-008**：自动保存和覆盖行为遵循游戏及 Emuera 原生语义；系统级历史保留由整个 SessionRoot 的外部备份策略提供，不介入每次运行时保存。
- **SAVE-009**：删除存档必须要求确认；活动 Session 的存档不得由 Worker 以外的进程并发修改。
- **SAVE-010**：存档内容的序列化和反序列化必须使用 Emuera 运行时的原生实现；CloudEmuera 不得为了 Web 存档管理而另行定义不兼容的游戏存档格式。
- **SAVE-011**：每个 Session 必须向 Emuera 提供独立的实际 SessionRoot，并保持原版可见的 `CSV/`、`ERB/`、资源、配置及存档目录结构。
- **SAVE-012**：Session 管理方必须在首次启动 Worker 前，把 Game 当前经过验证的完整普通文件树复制到私有 SessionRoot。Worker 只读写该副本；Game 的当前内容不得挂入运行目录或与 Session 共享可写 inode。
- **SAVE-013**：兼容层必须支持 Emuera 的两种原生存档布局：GameRoot 下的 `save*.sav`/`global.sav`，以及 `UseSaveFolder:YES` 时 GameRoot 下的 `sav/` 目录；布局只由 Session 创建时复制内容中的 `emuera.config` 决定，文件直接保存在当前 SessionRoot 的对应位置。
- **SAVE-014**：原版语义中的 Global 存档必须按 `User + Game + Session` 隔离，不能成为服务器级或同一用户跨 Session 共享文件。
- **SAVE-015**：SessionRoot 自创建起就是该 Session 的持久运行目录并位于挂载数据目录中；Worker 重启时必须复用该目录，不得在启动或退出时把存档复制到另一套提交存储。
- **SAVE-016**：存档上传、重命名和删除必须使用持久 operation、幂等键和停止态 mutation lease；API 重启或请求中断后必须能够区分暂存、已发布和已提交事实，不得用过期 lease 的 wall clock 直接释放写权。

### 6.6 管理与运维

- **OPS-001**：管理员必须能够查看 Session 状态、当前 Worker 标识/PID、心跳和最近错误，并能强制停止活动 Worker；MVP 不要求细粒度进程资源指标平台。
- **OPS-002**：系统必须允许配置实例级最大活动 Worker 数、未启动 Session 数、游戏包 archive/展开/单文件/entry、SessionRoot 文件数/字节、staging、存档文件/列表、ConsoleSnapshot/IPC/WebSocket 队列、presentation asset 和 DataRoot 最低剩余空间上限；MVP 不要求按用户或进程拆分资源配额、调度、预留和计费。
- **OPS-003**：API 必须提供健康检查、就绪检查和版本信息。
- **OPS-004**：日志必须包含可关联的 `requestId`、`sessionId`、`workerId` 和 `workerEpoch`，但不得记录密码或默认记录用户输入全文。
- **OPS-005**：已实现的身份、资源变更、管理员终止、Game 内容启用和存档删除等关键审计事件必须保留；MVP 不要求通用审计界面或为普通连接/只读操作建立完整审计流水。
- **OPS-006**：管理员必须能够阻止已知不安全的 Game 继续创建或重新开启 Session，同时不直接破坏既有 SessionRoot 和存档。

## 7. 兼容性需求

### 7.1 兼容层级

系统使用以下兼容分类：

| 等级 | 含义 |
| --- | --- |
| Supported | 自动化测试覆盖，并作为发布阻断条件 |
| Compatible | 设计上应支持，但可能存在已知显示或边缘行为差异 |
| Experimental | 可启用但不承诺稳定或完整 |
| Blocked | 因平台或安全原因禁止 |

### 7.2 运行时要求

- **COMP-001**：运行时必须固定到已记录的 Emuera.EM+EE 上游提交和 CloudEmuera 源码集成版本。
- **COMP-002**：必须建立 `1824+v18` 兼容测试集，覆盖解析、变量、函数调用、输入、存档、HTML、图片和 Sprite。
- **COMP-003**：必须建立当前 EM+EE 特性测试集，并明确尚未实现的命令和显示能力。
- **COMP-004**：加载游戏时必须输出兼容性报告，区分错误、警告、受限功能和未知命令。
- **COMP-005**：Shift-JIS 与 UTF-8 游戏文件必须保持可重复的解码结果；不得静默使用系统区域设置造成不同服务器行为不同。
- **COMP-006**：文件名大小写行为必须被兼容层规范化或诊断，以减少 Windows 游戏包在 Linux 上运行的差异。
- **COMP-007**：字体测量、换行、固定行高和按钮命中行为必须有视觉回归样例。
- **COMP-008**：`CALLSHARP`、任意 DLL、外部进程和不受限网络访问默认归类为 `Blocked`。
- **COMP-009**：当新版行为与 v18 的已知行为不同且游戏依赖旧行为时，可以提供有界、可测试的兼容开关；不得长期维护无法验证的全局“模拟旧 Bug”模式。
- **COMP-010**：`PRINTC文字数量` 必须保持固定上游的 Shift-JIS byte/半角格语义、右对齐 N 与左对齐
  N+1 补白差异，并在补白后用实际字体测量修正；`PRINTC并列数量` 只表示主动 flush 的字段数量，不得
  作为字段宽度、Unicode 字符数或浏览器列布局解释。

## 8. 三层总体架构

```text
┌──────────────────────────────────────────────┐
│ Docker container                             │
│                                              │
│ Web/API process                              │
│ Auth │ Game/Save │ Session │ WebSocket       │
│ Worker Manager      │ local IPC              │
│                     ├─ Session Worker process│
│                     └─ Session Worker process│
│                       (one per active Session)│
│                                              │
│ /data  ← mounted persistent data directory  │
│ SQLite │ games │ sessions │ logs │ backups   │
└──────────────────────────────────────────────┘
```

### 8.1 Web 层

- 只通过 API 使用资源。
- 支持通过 HTTP 或 HTTPS 完成控制操作，使用相应协议的 WebSocket 接收实时事件和发送游戏输入；是否启用
  HTTPS 由部署者及上级网关选择，应用不强制跳转。
- 保存 Session 标识、最近确认的输出序号和客户端消息标识，但不保存权威游戏状态。
- 将结构化事件渲染为 DOM/Canvas/WebAudio，不执行游戏提供的原始脚本或 HTML。

### 8.2 API 层

系统只部署一个 API 进程，代码上至少分为以下模块：

- Identity & Authorization
- Game Package Service
- Save Service
- Session Control Plane
- Realtime Gateway
- Session Registry & instance capacity gate
- Administration & Audit

API 进程不得把活动 Session 仅保存在内存中。它是运行期间唯一访问 SQLite 的业务进程，通过持久
Session、WorkerLease、epoch 和状态版本协调 HTTP、后台任务与 Worker 生命周期；Migrator 由同一容器
入口脚本在 API 启动前独占执行迁移或检查，Session Worker 不访问 SQLite。

API 进程直接创建、监视和终止 Session Worker，并只执行实例级活动 Worker 数量门控。API 退出会结束其管理的活动 Worker；API
重新启动后不接管旧 Worker，而是在确认旧进程已失去 SessionRoot 写权限后，把遗留活动 Session
对账为 `CRASHED` 并保留 SessionRoot。

生产单容器中只有 API 是长驻业务进程；每个 Session Worker 是 API 的子进程。容器使用 Docker 提供的
轻量 init/PID 1 转发信号并回收僵尸进程，不新增常驻 Supervisor 或第二个运行期控制面。容器入口脚本
先在同一容器内独占执行 `Migrator`，成功后才以 API 为主进程启动。开发环境默认由 API 和 Vite `web`
两个容器组成：API 直接服务构建后的 SPA，`web` 同时提供 `5173` 的 HMR 前端并把 `/api` 代理到 API；
开发数据始终使用开发专用 named volume，不读取仓库 `./data`。

### 8.3 Worker 层

Worker 层包括：

- **API Worker Manager 模块**：在 API 进程内启动、监视和终止 Session Worker 子进程，
  但不加载 Emuera Runtime，也不以进程内状态替代持久 lease。
- **Session Worker 进程**：每个活动 Session 一个独立操作系统进程，承载一个 Runtime、一个 ConsoleSnapshot 和一个 SessionRoot 工作目录；该进程边界用于状态隔离和可终止性，不提供恶意代码执行隔离。

Session Worker 的本地 IPC 控制通道断开后必须停止 Runtime 并有界退出，不在后台继续运行或重新
注册。API 实例退出或实例身份变化时，Worker 不能被新 API 实例接管。

每个 Session Worker 必须以实际的 `SessionRoot` 作为进程工作目录和 Emuera 的 GameRoot。Session 管理方在创建时完整复制 Game 当前已验证的合法普通文件树；配置、游戏自定义目录、存档和临时文件都直接位于 Session 自己的物理目录：

```text
SessionRoot/
├── CSV/              ← Game 当前内容的 Session 私有副本
├── ERB/              ← Game 当前内容的 Session 私有副本
├── resources/        ← Game 当前内容的 Session 私有副本
├── any-game-dir/     ← 其他合法目录也完整复制
├── sav/              ← Session 私有目录，可写
├── save*.sav         → Session 私有存档文件，可写
├── global.sav        → Session 私有存档文件，可写
└── emuera.config     ← Session 私有副本，可写
```

对于旧游戏写入 GameRoot 根部的 `save*.sav` 和 `global.sav`，SessionRoot 必须提供对应的 Session 私有可写路径。每个 Session Worker 必须运行在独立进程中，不能在 API 进程或其他 Session Worker 中运行 Emuera Runtime。

### 8.4 通信协议

- 浏览器到 API：HTTP 或 HTTPS + WebSocket；外部 HTTPS 终结和跳转由部署者的上级网关决定。
- API 到 Worker：容器内本地 IPC，首选 Unix domain socket；协议模型不得依赖特定传输才能测试。
- 大型静态资源：通过 API 从挂载数据目录读取，不通过实时消息重复传输。
- 所有控制命令必须包含 `sessionId`、`workerEpoch`、命令 ID 和协议版本。

## 9. Session 状态机

```text
CREATING → CLOSED
CLOSED/CRASHED → STARTING → RUNNING
STARTING/RUNNING → STOPPING → CLOSED
STARTING/RUNNING/STOPPING → CRASHED
RUNNING → SUSPENDING → SUSPENDED → RESUMING  （未来能力）
```

状态定义：

| 状态 | 说明 |
| --- | --- |
| CREATING | API 已接受请求，正在物化持久 SessionRoot |
| STARTING | Worker 正在启动和加载游戏 |
| RUNNING | Worker 健康；不论当前是否存在浏览器实时连接 |
| STOPPING | 正在拒绝新输入、刷新文件并终止 Worker |
| CLOSED | SessionRoot 存在且无活动 Worker；最近一次运行正常关闭，可重新开启 |
| CRASHED | SessionRoot 存在且无活动 Worker；最近一次运行异常结束，可在旧写权限释放后重新开启 |
| SUSPENDED | 未来的持久运行时快照状态，不占用活动 Worker |

实例级活动 Worker 门控统计 `STARTING`、`RUNNING` 和 `STOPPING` 状态；`CLOSED`、`CRASHED`、
`CREATING` 和 `SUSPENDED` 不占用名额。open 在数据库事务中检查全局上限，不维护按用户拆分的活动名额。
浏览器连接数不属于 Session 状态，由 Realtime Gateway 在内存中维护；连接数变为零不改变
`RUNNING`、不停止 Worker，也不释放活动 Worker 名额。

状态转换必须在数据库中使用版本号或事务保护。`CLOSED/CRASHED → STARTING` 每次创建新的
WorkerLease 并递增 epoch；来自旧 epoch 的心跳、输出与输入响应必须被拒绝。重新开启复用已有
SessionRoot，不重新复制 Game current content，也不恢复崩溃前的解释器内存状态。

## 10. 重连与消息语义

### 10.1 输出事件

示例：

```json
{
  "protocolVersion": 3,
  "sessionId": "sess_123",
  "workerEpoch": 4,
  "sequence": 1052,
  "type": "display.frame",
  "payload": {
    "workerEpoch": 4,
    "frameId": 8,
    "commitSequence": 1052,
    "reason": "WAITING_FOR_INPUT",
    "requiresSnapshot": false,
    "consoleState": null,
    "transactions": [
      {
        "sequence": 1052,
        "operations": [
          {
            "type": "openPrompt",
            "prompt": {
              "promptId": "prompt_1052",
              "inputType": "text",
              "constraints": { "type": "text" },
              "timeoutBehavior": "none",
              "timeoutAction": "none",
              "allowedSources": ["keyboard"],
              "oneInput": false,
              "systemInput": false,
              "stopMessageSkip": false,
              "displayTime": false,
              "openedAtUnixMilliseconds": 0,
              "deadlineUnixMilliseconds": 0
            }
          }
        ]
      }
    ]
  }
}
```

### 10.2 恢复请求

```json
{
  "type": "session.resume",
  "sessionId": "sess_123",
  "lastEpoch": 4
}
```

Worker/API 必须返回当前 epoch 最近一次已提交的完整 ConsoleSnapshot，并携带
`committedFrameId` 作为显示基线；随后只推送原子的 `display.frame`。MVP 不按 `lastSequence` 或 ack
补发断线期间的历史增量，working Snapshot 不得用于浏览器 resume/resync。

当前协议将该请求封装在 `GET /api/v1/realtime` 的原生 WebSocket v3 envelope 中，协商子协议
`cloudemuera.realtime.v3`。每次 `session.resume` 都重新检查登录态、Session 授权和当前 Worker binding，
并以 `session.snapshot` 或 `display.frame` 作为该连接该 epoch 的已提交显示状态；Hub 尚未取得首个 commit
时先保留有界订阅，待 Worker 的 committed frame 到达后发送；v3 客户端收到 `SNAPSHOT_NOT_READY` 后退避重试。
连接、订阅和 Snapshot 不写入 SQLite。协议接收缓冲、控制队列、pending input、订阅数和最终 UTF-8 消息
均有消息数/字节数硬上限，溢出只影响当前连接。显示提交边界见 [`ADR-0026`](adr/0026-display-commit-boundary-realtime-v3.md)。

### 10.3 输入请求

```json
{
  "type": "session.input",
  "sessionId": "sess_123",
  "workerEpoch": 4,
  "clientMessageId": "01JXYZ...",
  "value": "0"
}
```

服务器必须对输入返回接受、重复、无当前输入槽、无控制权或非法格式中的一种确定结果。

正式 v2 输入 envelope 必须同时携带 `workerEpoch`、`clientMessageId`、`source` 和 `value`，不得携带
内部 `promptId`；浏览器只能使用 `KEYBOARD`、`BUTTON`、`POINTER`，不得发送 Runtime 内部的 `SYSTEM`。
Worker 在收到时的单一输入临界区读取当前 prompt；没有 prompt 则返回 `NO_ACTIVE_PROMPT` 并立即丢弃。
同一 Worker 内相同 `clientMessageId` 和 payload 重试返回首次结果及原规范化值，异值复用返回
`CONFLICT`；API 不复制或持久化去重结果，Worker 仍是 prompt、格式、timeout 和首个有效输入的最终裁决者。
输入与 close 共享 Session command gate，`BeginStopping` 先线性化后不得把新输入写入 Worker。v1 和带
`promptId` 的旧输入不是受支持协议，必须拒绝而不能静默接受或改写。

## 11. 数据与存储设计

### 11.1 容器内嵌入式数据库

使用 SQLite 或等价的嵌入式关系数据库，数据库文件位于挂载数据目录中，例如 `/data/cloudemuera.db`。

保存：

- 用户与角色
- Game 元数据、摄取工作区、当前内容摘要和运行时清单
- Session 状态与当前 WorkerLease
- SessionRoot 路径、Session 生命周期和存档管理审计数据
- 实例级容量配置与兼容性报告摘要

数据库文件必须与游戏文件和完整 Session 目录一起纳入备份；容器重启不得清理或重建已有 SessionRoot。

### 11.2 本地物理文件系统

生产容器必须将一个宿主机数据目录或 Docker managed volume 挂载到 `/data`。所有游戏、Session、存档、日志和备份都必须使用该目录下的物理文件或目录：

生产 Compose 默认以 root 运行 API、Worker、Validator 和 Migrator，以便 named volume 开箱可写；
`CLOUDEMUERA_DATA_PATH` 未设置时使用 Docker named volume，设置后可通过 `CLOUDEMUERA_UID/GID`
让进程以预先创建数据目录的宿主账号运行。不得要求 bind mount 数据目录归镜像内固定 UID 所有，也不得
以 root entrypoint 自动递归修改目录所有权。

```text
/data/
├── cloudemuera.db
├── games/{gameId}/workspace/       ← Game 的内部摄取/校验工作区，不提供浏览器编辑
├── games/{gameId}/content/         ← 当前不可变复制来源，不保留历史版本
├── sessions/{sessionId}/
│   ├── root/                       ← SessionRoot 实际运行目录
│   └── metadata/
├── logs/
└── backups/
```

Session 管理方必须复制 Game 当前清单中的全部合法普通文件和目录，不按已知目录白名单丢弃未知内容。游戏包自带的软链接、硬链接和特殊文件仍必须拒绝。普通字节复制是基线；支持时可以使用保持写时复制语义的 reflink，并在不可用时回退普通复制。不得使用硬链接。每个 SessionRoot 必须私有、持久且相互隔离，Emuera 直接读写其中的完整游戏副本、配置、临时文件和原生存档。不得使用对象存储、远程文件系统或外部文件服务作为运行时或持久化依赖。备份必须通过宿主机文件系统快照、目录复制或等价的本地备份工具完成。

生产停机备份必须是冷备份：先停止 API，再复制 `/data` 的完整目录树，至少包含 SQLite 主文件及其
`-wal`/`-shm` 文件、Data Protection keys、games、sessions、logs 和 backups；复制完成后才可启动 API。
恢复必须整体替换 `/data`，离线执行当前镜像入口的 `rebind-session-roots`（先迁移并校验数据库 marker，再重新
校验恢复后的 Game 目录和 SessionRoot 的目录 identity）后启动 API，不得只恢复数据库或单个 SessionRoot。正常
停机由 API 处理 SIGTERM；默认给所有 Worker 共用 5 秒优雅停止预算、仍存活 Worker 共用 5 秒强制停止
预算，Host 停止预算为 15 秒，Compose 停止宽限期为 20 秒。

## 12. 安全需求

- **SEC-001**：游戏包、文件名和显示内容必须按不安全数据格式解析；部署者只应运行自己信任的游戏，系统不承诺安全执行恶意 ERB/Runtime 内容。
- **SEC-002**：生产容器默认可使用 root 以兼容 Docker named volume；bind mount 可由部署者提供 UID/GID。生产
  宿主 HTTP 端口默认只绑定 loopback；是否使用 HTTPS、是否由上级网关跳转由部署者选择，应用不强制协议。
  Cookie 默认不要求 Secure；仅当 `CLOUDEMUERA_SECURITY_SECURE_COOKIES=true` 时使用 Secure Cookie。无论
  身份都不得挂载容器管理接口、宿主密钥或无关宿主目录；Worker 不应主动使用公网，执行边界以容器整体限制
  和应用路径校验为准。
- **SEC-003**：Session 管理方只把分配的完整 SessionRoot 路径交给 Worker，正常 Worker 逻辑不得访问 Game 库或其他 SessionRoot；API 与 Worker 可以同 UID，故该约束不构成抵御恶意 Worker 的内核强制租户隔离。
- **SEC-004**：ConsoleSnapshot、IPC/WebSocket 队列、ZIP archive/expanded/entry/single-file、SessionRoot
  文件数/字节、存档列表、presentation asset 和 DataRoot 使用必须有实例级上限；生产 Compose 不设置 CPU
  上限，内存和 PID 仍可由部署者按需配置，不要求细粒度进程资源治理。
- **SEC-005**：必须防止归档路径穿越、符号链接逃逸、大小写碰撞和超量解压。
- **SEC-006**：浏览器渲染必须对文本和属性编码；Emuera HTML 必须转换为受支持的结构化节点。
- **SEC-007**：Worker 本地 IPC 端点不得暴露到容器外；Worker 注册必须绑定 API 启动时签发的 Session、Worker 和 epoch 信息，不要求额外的跨实例服务身份挑战协议。
- **SEC-008**：存档下载和资源必须通过经过授权的 API 代理；MVP 不要求签名或短期有效 URL。Session
  runtime 资源只能使用按 Session 冻结清单投影的 opaque `assetId`，服务端交集校验 MIME/signature，
  并以私有缓存、ETag 和有界 Range 响应提供；浏览器不得获得 SessionRoot 路径或任意外部/`data:` URL。
- **SEC-009**：日志、指标和崩溃文件必须避免泄露认证信息及不必要的用户输入内容。
- **SEC-010**：依赖和上游解释器更新必须经过兼容性回归和安全审查后才能成为新的运行时基线。

## 13. 非功能需求

### 13.1 可用性与恢复

- **NFR-001**：正常负载下，已运行 Session 的重连及初始快照显示目标为 P95 不超过 2 秒，不含用户外部网络异常。
- **NFR-002**：API 正常停止时必须有界优雅停止其 Worker，超时后强制终止；产品默认给所有 Worker
  共用 5 秒优雅停止预算和 5 秒强制停止预算，Host 停止预算为 15 秒，Compose 停止宽限期为 20 秒；API 意外退出或控制
  通道断开后，Worker 必须立即开始有界退出或由父子进程/进程组兜底回收。受影响的活动 Session 必须对账为
  `CRASHED` 并保留 SessionRoot。
- **NFR-003**：Session Worker 在控制通道中断后必须停止运行，也不得被新 API 实例接管；
  用户在故障对账完成后显式重新开启同一 Session。
- **NFR-004**：Worker 崩溃必须保留其 SessionRoot 现场且不得影响 Game 当前内容、workspace 或其他 Session；Session 必须清晰标记为 `CRASHED`。不承诺正在被原生 writer 覆盖的文件仍然有效。
- **NFR-005**：第一阶段不把任意执行点或每次保存的事务性恢复作为 SLA；仅保证 SessionRoot 持久存在和诊断信息可用。

### 13.2 性能与容量

- **NFR-006**：API 在正常负载下的 Session 列表和详情请求目标为 P95 不超过 300 ms，不含大文件传输。
- **NFR-007**：已连接 Session 的输入从 API 接受到 Worker 接收的内部传输目标为 P95 不超过 200 ms，不含 ERB 执行时间。
- **NFR-008**：Worker 必须对高频 `PRINT` 执行批处理并限制内存；慢客户端不能阻塞解释器主循环或无限增长队列。
- **NFR-009**：实例级最大活动 Worker 数应通过代表性游戏的手工或可重复验证确定，并由部署配置显式限制。
- **NFR-010**：系统必须暴露 Session/Worker 基本状态、最近心跳、快照大小和队列溢出诊断；不要求提供完整容量规划指标。

### 13.3 可维护性

- **NFR-011**：解释器核心、平台抽象、Worker 协议和 Web 渲染器必须分别测试，不能依赖浏览器端到端测试覆盖全部逻辑。
- **NFR-012**：中英文需求文档必须保持相同需求编号；变更某一语言时必须同步检查另一份文档。
- **NFR-013**：所有消息协议必须包含版本字段，并对未知可选字段向前兼容。
- **NFR-014**：上游 EM+EE 合并必须生成变更报告并运行 v18 与当前 EM+EE 双测试集。
- **NFR-018**：在同一 checkout 中运行的自动化身份校验不得读取或修改人工 `.env`、`./data` 或
  Compose project；必须使用显式临时 env、唯一 project 名、该 project 的隔离 named volume 和隔离端口。

### 13.4 可访问性与客户端兼容

- **NFR-015**：Web UI 应遵循 WCAG 2.1 AA 的关键交互要求，包括键盘操作、焦点可见性和足够对比度。
- **NFR-016**：必须支持发布时仍受维护的 Chrome、Firefox、Safari 和 Edge 主要版本。
- **NFR-017**：移动端不得依赖悬停才能操作；按钮和输入区域必须适合触摸。

## 14. 故障模型

| 故障 | 预期行为 |
| --- | --- |
| 浏览器刷新/断网 | Session 保持 RUNNING；Realtime Gateway 最终发现断流，重连后恢复快照与当前 prompt |
| API 进程退出或重启 | Worker 有界停止或被回收；新 API 确认旧 Worker 已退出后把活动 Session 对账为 CRASHED，SessionRoot 保留 |
| API 与 Worker 本地 IPC 中断 | Worker 停止 Runtime 并有界退出；Session 对账为 CRASHED，旧控制通道不可恢复 |
| Worker 崩溃 | Session 标记 CRASHED；原样保留 SessionRoot；确认旧 Worker 退出后可重新开启同一 Session 并从原生存档继续，不承诺指令级恢复 |
| Docker 容器或宿主机重启 | 活动 Worker 按故障处理；挂载数据目录中的 Game 内容、workspace 与 SessionRoot 保留，恢复后可重新开启同一 Session |
| 挂载数据目录不可用 | 禁止新建 Session 和写入存档，并明确报告持久化故障 |
| 客户端重复输入 | 通过当前 Worker 内的 clientMessageId 和无 prompt 的输入 fingerprint 去重 |
| 旧 Worker 恢复连接 | 因 epoch 落后被 fencing，不能继续产生有效状态 |

## 15. MVP 验收场景

- **AC-001**：一个用户同时启动同一游戏的两个 Session，两者变量、显示、输入和存档互不影响。
- **AC-002**：用户在等待输入时关闭网页，至少在配置的测试间隔后重新登录，能够看到最新显示并继续同一 prompt。
- **AC-003**：API 正常停止时 Worker 在限定时间内优雅退出；强制终止 API 或控制通道断开后 Worker
  立即开始退出或被进程组回收。API 恢复后相关 Session 变为 `CRASHED`、活动 Worker 名额被释放、SessionRoot
  原样保留；旧 Worker 写权限确认释放后，用户能重新开启同一 Session 并加载已有原生存档。
- **AC-004**：两个用户使用同一 Game 当前内容创建 Session 时，不能访问或覆盖彼此存档和 Session。
- **AC-005**：重复提交同一输入消息只执行一次。
- **AC-006**：用户显式关闭 Session 后 Worker 在限定时间内退出，Session 状态变为 CLOSED，随后
  输入被拒绝；再次开启同一 Session 时复用原 SessionRoot、epoch 增大且能加载关闭前存档。
- **AC-007**：强制终止 Worker 后，Session 在心跳窗口内变为 CRASHED，SessionRoot 不被清理；
  旧 Worker 退出屏障完成后，同一 Session 可重新开启并检查或加载其中现存原生存档。
- **AC-008**：代表性的 `1824+v18` 测试游戏能够完成加载、输入、保存、加载和主要显示场景。
- **AC-009**：代表性的当前 EM+EE 测试游戏能够运行已声明为 Supported 的特性；未支持功能产生明确诊断。
- **AC-010**：包含路径穿越、符号链接逃逸或压缩炸弹特征的游戏包被拒绝且不写出受保护的摄取目录。
- **AC-011**：桌面和移动浏览器均可完成创建 Session、游戏输入、断线重连和存档下载。
- **AC-012**：持续大量输出时，Worker 与浏览器内存保持在配置边界内，重连仍能获得一致快照。
- **AC-013**：代表性游戏在根目录存档模式和 `sav/` 模式下均可用 Emuera 原生逻辑完成保存与加载；物理存档只出现在对应 Session 的私有区域。
- **AC-014**：两个用户以及同一用户的两个 Session 使用同一 Game 当前内容时，Game 库内容保持不被 Worker 修改，Global 存档按 `User + Game + Session` 隔离，不会跨用户或跨 Session 共享；Game 后续包替换不改变这些 SessionRoot。

## 16. 分阶段交付建议

### Phase 0：兼容性与运行时验证

- 固定 EM+EE 上游提交。
- 将解释器与 WinForms/GDI+ 分离，定义 `IGameConsole`、`RuntimePaths`、文件、时钟、音频和图像抽象。
- 构建 v18 与 EM+EE 最小测试游戏集。
- 验证单 Session 无 UI Worker 能运行到 INPUT、接受输入，并使用原生格式在根目录与 `sav/` 两种布局中保存和加载。

### Phase 1：单机 MVP

- 单 Docker 容器、单 API 控制面进程、SQLite 和挂载的数据目录。
- API 内的 Worker Manager 直接管理每 Session 一个独立子进程；运行期只有 API 访问 SQLite。
- WebSocket 重连、ConsoleSnapshot、存档隔离，以及固定 Emuera.EM+EE 基线全部 Supported 能力的
  结构化 Web 渲染和输入。
- 游戏包上传、内部摄取工作区、当前内容原子启用与基础诊断；不提供浏览器内文件写入工具。

### Phase 2：自托管完善

- Docker 镜像、健康检查、日志、文件系统备份和升级流程。
- 关键审计、停机备份、基本诊断和管理员强制停止；不建设资源指标平台或通用审计控制台。
- 不把 MVP 已承诺的 Emuera HTML、绘图、Sprite、音频或移动端兼容推迟到本阶段。

### Phase 3：挂起与恢复

- 研究 INPUT 安全点的解释器快照。
- 支持 SUSPENDED/RESUMING，释放长期离线 Session 的活动内存。
- 在可验证条件下研究从解释器安全点恢复内存状态；MVP 已支持复用同一 SessionRoot 冷启动并加载
  原生存档，但不把它描述为指令级恢复。

## 17. 待确认事项

身份方案已确认采用本地账户、email 登录、可撤销 Cookie Session，以及仅未初始化实例读取
`docker/.env` 的首个管理员 bootstrap；切换 OIDC 的触发条件由 ADR-0001 记录。其余待确认事项：

1. 多标签页是否需要显式“控制权租约”，还是采用第一个有效输入生效？
2. MVP 对 Emuera HTML、Sprite、CBG 和音频分别承诺到什么兼容等级？
3. 是否允许管理员配置 Session 最大存活时间，还是仅允许资源/安全原因强制关闭？
4. 游戏和存档是否需要跨服务器导入导出格式？
5. 哪些代表性 v18 和 EM+EE 游戏可合法用于自动化兼容测试？
6. ~~是否需要保留原始游戏包中的字体；相关字体和游戏资源授权如何处理？~~ 已由 ADR-0029 确认：
   MVP 忽略游戏字体，只分发和使用 CloudEmuera 固定支持的 OFL 字体目录。

## 18. 主要风险

| 风险 | 影响 | 缓解方向 |
| --- | --- | --- |
| EM+EE 与 UI/平台代码耦合 | Worker 无头化工作量增加 | 先提取平台接口，建立差异测试 |
| Emuera 全局静态状态 | 同进程多 Session 相互污染 | 初期每 Session 独立进程 |
| 游戏根路径与存档路径耦合 | 共享资源可能被写入或用户存档串线 | 每 Session 完整独立副本、无共享可写 inode、SessionRoot 隔离 |
| 显示语义复杂 | 游戏可运行但 UI 错位 | 结构化显示模型和视觉回归测试 |
| 损坏或恶意构造的包 | 文件逃逸、磁盘耗尽或浏览器注入 | 受保护摄取目录、不可变 current、完整复制校验、实例级上限、结构化显示 |
| 部署者运行恶意游戏 | 同 UID Worker 可能读取 DataRoot 内其他资源 | 明确仅支持可信参与者/可信游戏；敌对租户托管必须重新引入内核隔离机制 |
| Session 永久常驻 | 内存成本随遗忘会话增长 | 实例级活动 Worker 上限、管理员强制停止、未来挂起快照 |
| Worker 崩溃无法原地恢复 | 用户丢失未保存进度，正在覆盖的原生存档可能无效 | 整目录备份、游戏原生自动存档、明确故障语义、研究安全点快照 |
| 上游持续演进 | 内置源码升级冲突或兼容倒退 | 固定提交、独立导入提交、修改账本、双兼容测试集 |
| 游戏与字体授权不清 | 无法合法托管或再分发 | 游戏字体不进入 MVP Runtime；内置字体固定版本、摘要并随制品附完整 OFL/版权声明 |

## 19. 参考资料

- [Emuera.EM+EE 帮助文档](https://evilmask.gitlab.io/emuera.em.doc/zh/index.html)
- [Emuera.EM+EE 变更记录](https://evilmask.gitlab.io/emuera.em.doc/en/Changelog/index.html)
- [gEmuera](https://github.com/wwwXiaoHan17/gEmuera)
- [uEmuera](https://github.com/xerysherry/uEmuera)
- [lispcoc/gemuera IGameConsole](https://github.com/lispcoc/gemuera/blob/main/godot/src/Bridge/IGameConsole.cs)
