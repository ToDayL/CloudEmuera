# CloudEmuera 需求与总体设计

| 项目 | 内容 |
| --- | --- |
| 文档状态 | 草案 v0.2 |
| 日期 | 2026-08-03 |
| 对应英文文档 | [requirements.en.md](./requirements.en.md) |
| 目标读者 | 产品、前端、后端、运行时、运维与测试开发者 |

## 1. 文档目的

本文档定义 CloudEmuera 的首版产品需求、系统边界与总体技术设计。CloudEmuera 旨在将 Emuera 文字游戏部署到远程服务器，使多个用户可以通过桌面或移动浏览器上传和管理自己的游戏、启动多个独立会话、管理各自存档，并在浏览器断线后重新连接到仍在运行的会话。

本文档中的“必须”“应当”“可以”分别表示强制需求、推荐需求和可选能力。带编号的需求用于实现追踪和验收；中英文文档使用相同编号。

## 2. 背景与设计结论

### 2.1 背景

传统 Emuera 是面向桌面环境的单用户应用，解释器、窗口、输入、绘制、文件与存档生命周期存在较强耦合。uEmuera 与 gEmuera 已证明保留 C# 解释器、替换平台与显示层的迁移路线可行，但两者仍以单机客户端为中心，不能直接满足多用户、浏览器重连、资源隔离和服务端运维要求。

### 2.2 版本基线

CloudEmuera 将使用 Emuera.EM+EE 作为运行时基线，并保留面向 `Emuera 1824+v18` 游戏的兼容目标。初始研究基线为 `Emuera.NET 1824+v24+EMv18+EEv56`。正式构建必须在运行时清单中记录具体的上游提交、补丁集合和 CloudEmuera 兼容层版本，不能仅记录易变的“最新版”。

## 3. 目标与非目标

### 3.1 产品目标

- 通过现代桌面和移动浏览器游玩 Era/Emuera 游戏。
- 支持玩家上传、管理和版本化自己拥有的 ERB/CSV/资源游戏包。
- 支持同一用户为同一或不同游戏启动多个并行 Session。
- 让 Session 脱离浏览器连接独立存活，并能够恢复到最新显示与输入状态。
- 为不同用户和 Session 隔离存档、配置、临时文件和运行时状态。
- 尽可能兼容最新 Emuera.EM+EE，同时覆盖常见 `1824+v18` 游戏。
- 为玩家自托管的单容器部署提供可观测、可限制、可备份的工具架构。

### 3.2 MVP 范围

- 本地账户或单一外部身份提供商登录。
- 游戏包上传、校验、查看、编辑与版本化。
- Session 创建、列表、连接、重连和显式关闭。
- 文字、样式、按钮、基础 HTML、图片、Sprite 和基础音频事件。
- 用户输入、超时输入和移动端软键盘。
- 每个 Session 独立的存档空间，以及存档导入、导出、重命名和删除。
- 管理员查看 Worker 状态并终止异常 Session。
- 单个 Docker 容器部署；Web/API、Worker Supervisor 和 Session Worker 均运行在容器内，并通过挂载的数据目录持久化。

### 3.3 非目标

- 公共游戏商店、内容发现、评分或社区系统。
- 视频桌面串流或远程传输 Godot/Unity 画面。
- 执行任意本地 DLL、程序或不受限制的网络请求。
- 第一阶段的任意指令点进程快照或 Worker 崩溃后从同一条指令继续执行。
- 保证所有 Emuera 分支、非标准补丁和依赖旧缺陷的游戏完全一致。
- 多名玩家共同控制同一游戏的协作玩法。
- 多容器、多 API、多 Worker Host、跨主机横向扩展或无缝迁移。

## 4. 角色与权限

### 4.1 玩家

- 查看自己有权访问的游戏。
- 上传游戏包，并管理自己拥有的游戏及其版本。
- 设置自己拥有的游戏的可见性。
- 创建和管理自己的 Session。
- 连接自己的 Session 并提交输入。
- 管理自己的存档。

### 4.2 管理员

- 管理用户与配额。
- 查看 Worker、Session 健康状态和资源使用。
- 因安全、资源或维护原因强制停止 Session。
- 管理备份、保留策略和兼容性策略。

## 5. 核心领域模型

| 实体 | 说明 |
| --- | --- |
| User | 身份、角色、配额与偏好 |
| Game | 游戏的稳定身份、玩家所有者和可见性 |
| GameVersion | 不可变的 ERB/CSV/资源快照及运行时要求 |
| Session | 用户针对某一 GameVersion 创建的可重连游戏会话 |
| WorkerLease | Session 当前有效 Worker 的路由、租约与 fencing epoch |
| SaveArtifact | 用户、游戏和 Session 范围内的具体存档制品 |
| ConsoleSnapshot | 当前有界显示树、输入提示与输出序号 |
| OutputEvent | 对 ConsoleSnapshot 的有序增量 |

关系约束：

```text
User 1 ── N Session N ── 1 GameVersion N ── 1 Game
Session 1 ── 0..1 Active WorkerLease
Session 1 ── N SaveArtifact N ── 1 Game
```

## 6. 功能需求

### 6.1 身份与授权

- **AUTH-001**：所有非公开 API 和 WebSocket 连接必须经过认证。
- **AUTH-002**：API 必须在每次 Session、存档和游戏文件操作时验证资源所有权或授权，不得仅依赖前端隐藏入口。
- **AUTH-003**：用户不得枚举、读取、控制或删除其他用户的私有 Session 和存档。
- **AUTH-004**：管理员强制停止或修改资源时必须写入审计记录。
- **AUTH-005**：WebSocket 在升级连接和恢复 Session 时必须重新验证身份与访问权。

### 6.2 游戏包与 ERB 管理

- **GAME-001**：系统必须支持上传包含 `ERB`、`CSV`、配置及资源文件的游戏包。
- **GAME-002**：上传过程必须拒绝路径穿越、绝对路径、非法符号链接、压缩炸弹和超过配额的文件。
- **GAME-003**：系统必须识别并记录文本文件编码，至少覆盖 Shift-JIS、UTF-8 BOM 和 UTF-8 无 BOM。
- **GAME-004**：每个已发布 GameVersion 必须不可变，并记录内容校验和、创建者、创建时间和运行时配置。
- **GAME-005**：对已发布版本的浏览器编辑必须生成草稿或新版本，不得改变活动 Session 已固定的文件内容。
- **GAME-006**：系统必须提供目录浏览、文本查看、ERB/CSV 文本编辑、搜索和文件下载能力。
- **GAME-007**：发布前必须执行基础验证，包括目录结构、编码、解析错误、缺失资源和已禁止功能的诊断。
- **GAME-008**：创建 Session 时必须固定到明确的 GameVersion；后续发布不能隐式改变正在运行的 Session。
- **GAME-009**：游戏可见性至少支持私有和服务器共享；公开市场式发布不在 MVP 范围内。
- **GAME-010**：删除仍被 Session 或存档引用的版本时必须拒绝删除，或执行可恢复的逻辑删除。

### 6.3 Session 管理

- **SESS-001**：用户可以为同一或不同 GameVersion 创建任意数量的 Session；系统不得因已创建的 Session 总数而拒绝创建请求，但必须限制同时处于活动运行状态并占用 Worker 的 Session 数量。
- **SESS-002**：每个活动 Session 必须由且仅由一个有效 Runtime 所有；切换 Worker 时必须使用递增 epoch 防止旧 Worker 继续接受输入。
- **SESS-003**：浏览器断开不得自动关闭 Session，也不得清除运行时状态。
- **SESS-004**：Session 在无浏览器连接时必须继续处理已经开始的执行、计时输入和内部计时器。
- **SESS-005**：用户必须能查看 Session 名称、游戏版本、状态、创建时间、最后活动时间和当前是否等待输入。
- **SESS-006**：用户必须能够显式关闭 Session。关闭流程必须停止新输入、刷新文件、可选生成最终自动存档、终止 Worker，并把状态置为 `CLOSED`。
- **SESS-007**：API 必须具备幂等的创建和关闭语义，以防网络重试重复创建或重复关闭资源。
- **SESS-008**：除管理员操作、安全策略、资源故障或明确配置的部署策略外，系统不得仅因连接空闲而自动关闭 Session。
- **SESS-009**：管理员必须能够查看和强制停止失控、超配额或违反安全策略的 Session。
- **SESS-010**：Worker 异常退出时，Session 必须在心跳超时后转为 `CRASHED`，不得继续显示为可运行。

### 6.4 游戏显示与交互

- **PLAY-001**：Worker 必须向 API 输出结构化显示事件，而不是把未验证的原始 HTML 直接交给浏览器执行。
- **PLAY-002**：显示模型必须支持文字、前景/背景色、字体样式、换行、按钮、工具提示、图片、Sprite、背景图层和基础音频控制。
- **PLAY-003**：实现的 Emuera HTML 子集必须使用允许列表解析；脚本、事件属性和任意 URL 不得进入浏览器 DOM。
- **PLAY-004**：每个输出事件必须包含 Session 内单调递增的 `sequence`。
- **PLAY-005**：Worker 必须维护有界 ConsoleSnapshot 和短期输出增量，以支持重连。
- **PLAY-006**：重连必须返回一个确定序号的快照及其后的增量，或返回从客户端最后确认序号开始的完整增量；快照与订阅之间不得出现丢失窗口。
- **PLAY-007**：每个输入请求必须具有唯一 `promptId`，客户端输入必须包含唯一 `clientMessageId`。
- **PLAY-008**：Worker 必须拒绝过期 `promptId`，并对重复 `clientMessageId` 返回原处理结果或明确的重复响应，不得执行两次。
- **PLAY-009**：桌面端必须支持键盘、鼠标和滚动；移动端必须支持触摸按钮、软键盘、视口变化和安全区域。
- **PLAY-010**：显示历史必须有可配置上限。超过上限时应压缩为最新快照并丢弃不可见的早期增量，不能无限增长 Worker 内存。
- **PLAY-011**：同一用户可以同时从多个客户端查看同一 Session。MVP 中每个 `promptId` 只接受第一个有效输入。
- **PLAY-012**：API 或浏览器消费速度不足时必须使用批处理、背压或快照降级，不能无限堆积消息。

### 6.5 存档管理

- **SAVE-001**：存档必须按用户、游戏和 Session 隔离；每个 Session 必须拥有独立的存档工作区，不能与其他 Session 共享物理存档文件。
- **SAVE-002**：Worker 不得通过游戏提供的相对路径越过分配的存档或临时目录。
- **SAVE-003**：用户必须能够按 Session 列出、下载、上传、重命名和删除自己的存档。
- **SAVE-004**：存档写入必须采用临时文件加原子替换，避免进程终止留下半写文件。
- **SAVE-005**：每个 SaveArtifact 必须记录游戏版本、运行时版本、编码/格式、文件大小、校验和、创建时间和来源 Session。
- **SAVE-006**：导入存档前必须验证文件大小、路径、格式以及目标 GameVersion 和 Session 的权限。
- **SAVE-007**：从一个 Session 复制存档到另一个 Session 时必须显式执行，不得让多个活动 Worker 同时写同一物理文件。
- **SAVE-008**：系统应当支持可配置的自动存档和保留策略，但不得覆盖用户手动存档而没有版本或备份。
- **SAVE-009**：删除存档必须要求确认，并应支持管理员配置的软删除保留期。
- **SAVE-010**：存档内容的序列化和反序列化必须使用 Emuera 运行时的原生实现；CloudEmuera 不得为了 Web 存档管理而另行定义不兼容的游戏存档格式。
- **SAVE-011**：每个 Session 必须向 Emuera 提供独立的实际 SessionRoot，并保持原版可见的 `CSV/`、`ERB/`、资源、配置及存档目录结构。
- **SAVE-012**：SessionRoot 必须将不可变 GameVersion 文件只读挂载，将 Session 配置、临时文件和存档映射为私有可写区域；运行时根路径必须可注入，不得固定依赖 API 或其他 Session 的目录。
- **SAVE-013**：兼容层必须支持 Emuera 的两种原生存档布局：GameRoot 下的 `save*.sav`/`global.sav`，以及启用配置后 GameRoot 下的 `sav/` 目录；两种布局都必须重定向到当前 Session 的私有存储。
- **SAVE-014**：原版语义中的 Global 存档必须按 `User + Game + Session` 隔离，不能成为服务器级或同一用户跨 Session 共享文件。
- **SAVE-015**：Session 启动时必须把所需存档物化到该 Session 的本地工作区；保存成功后必须以原子提交方式写入挂载数据目录中的 SaveArtifact。

### 6.6 管理与运维

- **OPS-001**：管理员必须能够查看容器内的 Worker Supervisor、Session Worker 进程、Session、心跳、CPU、内存、磁盘和输出速率。
- **OPS-002**：系统必须允许配置每用户活动运行 Session 数量、每 Session 内存/CPU/磁盘、游戏包大小、存档大小和输出速率上限；不得配置 Session 创建总数上限。
- **OPS-003**：API 必须提供健康检查、就绪检查和版本信息。
- **OPS-004**：日志必须包含可关联的 `requestId`、`sessionId`、`workerId` 和 `workerEpoch`，但不得记录密码或默认记录用户输入全文。
- **OPS-005**：必须记录 Session 创建、连接、关闭、崩溃、管理员终止、游戏发布和存档删除等审计事件。
- **OPS-006**：管理员必须能够阻止已知不安全的游戏版本继续创建新 Session，同时不直接破坏现有存档。

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

- **COMP-001**：运行时必须固定到已记录的 Emuera.EM+EE 上游提交和 CloudEmuera 补丁版本。
- **COMP-002**：必须建立 `1824+v18` 兼容测试集，覆盖解析、变量、函数调用、输入、存档、HTML、图片和 Sprite。
- **COMP-003**：必须建立当前 EM+EE 特性测试集，并明确尚未实现的命令和显示能力。
- **COMP-004**：加载游戏时必须输出兼容性报告，区分错误、警告、受限功能和未知命令。
- **COMP-005**：Shift-JIS 与 UTF-8 游戏文件必须保持可重复的解码结果；不得静默使用系统区域设置造成不同服务器行为不同。
- **COMP-006**：文件名大小写行为必须被兼容层规范化或诊断，以减少 Windows 游戏包在 Linux 上运行的差异。
- **COMP-007**：字体测量、换行、固定行高和按钮命中行为必须有视觉回归样例。
- **COMP-008**：`CALLSHARP`、任意 DLL、外部进程和不受限网络访问默认归类为 `Blocked`。
- **COMP-009**：当新版行为与 v18 的已知行为不同且游戏依赖旧行为时，可以提供有界、可测试的兼容开关；不得长期维护无法验证的全局“模拟旧 Bug”模式。

## 8. 三层总体架构

```text
┌──────────────────────────────────────────────┐
│ Docker container                             │
│                                              │
│ Web/API process                              │
│ Auth │ Game/Save │ Session │ WebSocket       │
│                    │ local IPC               │
│                    ▼                         │
│ Worker Supervisor process                    │
│                    │                         │
│                    ├─ Session Worker process │
│                    └─ Session Worker process │
│                       (one per active Session)│
│                                              │
│ /data  ← mounted persistent data directory  │
│ SQLite │ games │ sessions │ logs │ backups   │
└──────────────────────────────────────────────┘
```

### 8.1 Web 层

- 只通过 API 使用资源。
- 使用 HTTPS 完成控制操作，使用 WebSocket 接收实时事件和发送游戏输入。
- 保存 Session 标识、最近确认的输出序号和客户端消息标识，但不保存权威游戏状态。
- 将结构化事件渲染为 DOM/Canvas/WebAudio，不执行游戏提供的原始脚本或 HTML。

### 8.2 API 层

系统只部署一个 API 进程，代码上至少分为以下模块：

- Identity & Authorization
- Game Package Service
- Save Service
- Session Control Plane
- Realtime Gateway
- Session Registry & Scheduler
- Administration & Audit

API 进程不得把活动 Session 仅保存在内存中。API 进程重启后，Worker Supervisor 必须保持 Session Worker 运行，并通过挂载数据目录中的持久元数据重新建立 Session 状态和本地 IPC 连接。

API 进程与 Worker Supervisor 进程必须由容器内的进程管理机制作为相互独立的进程管理；重启 API 进程不得终止 Worker Supervisor 或其管理的 Session Worker。

### 8.3 Worker 层

Worker 层建议区分：

- **Worker Supervisor 进程**：在容器内启动、监视、限制和终止 Session Worker 子进程。
- **Session Worker 进程**：每个活动 Session 一个独立操作系统进程，承载一个 Runtime、一个 ConsoleSnapshot 和一个 Session 文件沙箱。

Session Worker 必须在 API 进程暂时重启或本地 IPC 暂时中断时继续运行，并保留有界输出。控制通道恢复后应使用 `sessionId + workerEpoch` 重新注册。

每个 Session Worker 必须以实际的 `SessionRoot` 作为进程工作目录和 Emuera 的 GameRoot。GameVersion 文件从挂载数据目录中的游戏目录以只读方式挂载，Session 配置、存档和临时文件写入 Session 自己的物理目录：

```text
SessionRoot/
├── CSV/              → /data/games/{game}/{version}/CSV，只读挂载
├── ERB/              → /data/games/{game}/{version}/ERB，只读挂载
├── resources/        → /data/games/{game}/{version}/resources，只读挂载
├── sav/              → /data/sessions/{session}/sav，可写
├── save*.sav         → Session 私有存档文件，可写
├── global.sav        → Session 私有存档文件，可写
└── emuera.config     → /data/sessions/{session}/emuera.config，可写
```

对于旧游戏写入 GameRoot 根部的 `save*.sav` 和 `global.sav`，SessionRoot 必须提供对应的 Session 私有可写路径。每个 Session Worker 必须运行在独立进程中，不能在 API 进程或其他 Session Worker 中运行 Emuera Runtime。

### 8.4 通信协议

- 浏览器到 API：HTTPS + WebSocket。
- API 到 Worker：容器内本地 IPC，首选 Unix domain socket；协议模型不得依赖特定传输才能测试。
- 大型静态资源：通过 API 从挂载数据目录读取，不通过实时消息重复传输。
- 所有控制命令必须包含 `sessionId`、`workerEpoch`、命令 ID 和协议版本。

## 9. Session 状态机

```text
CREATING → STARTING → RUNNING ↔ DETACHED
                         │          │
                         ├──────────┤
                         ▼          ▼
                      STOPPING → CLOSED

STARTING/RUNNING/DETACHED → CRASHED
CRASHED → RECOVERING → RUNNING       （未来能力）
RUNNING/DETACHED → SUSPENDING → SUSPENDED → RESUMING  （未来能力）
```

状态定义：

| 状态 | 说明 |
| --- | --- |
| CREATING | API 已接受请求，尚未分配 Worker |
| STARTING | Worker 正在启动和加载游戏 |
| RUNNING | Worker 健康，至少有一个实时连接 |
| DETACHED | Worker 健康，没有实时连接 |
| STOPPING | 正在拒绝新输入、刷新文件并终止 Worker |
| CLOSED | 用户或管理员已完成关闭 |
| CRASHED | Worker 不可恢复地退出或租约失效 |
| SUSPENDED | 未来的持久运行时快照状态，不占用活动 Worker |

活动运行配额统计 `CREATING`、`STARTING`、`RUNNING`、`DETACHED` 和 `STOPPING` 状态；`CREATING` 会预留配额，`STOPPING` 直到 Worker 释放前仍计入。`CLOSED`、`CRASHED` 和 `SUSPENDED` 不占用活动运行配额。`DETACHED` 虽然没有浏览器连接，但仍占用 Worker，因此必须计入。

状态转换必须在数据库中使用版本号或事务保护。WorkerLease 必须有递增 epoch；来自旧 epoch 的心跳、输出与输入响应必须被拒绝。

## 10. 重连与消息语义

### 10.1 输出事件

示例：

```json
{
  "protocolVersion": 1,
  "sessionId": "sess_123",
  "workerEpoch": 4,
  "sequence": 1052,
  "type": "display.line.append",
  "payload": {
    "parts": [
      { "type": "text", "text": "选择行动：" },
      { "type": "button", "text": "[0] 休息", "value": "0" }
    ]
  }
}
```

### 10.2 恢复请求

```json
{
  "type": "session.resume",
  "sessionId": "sess_123",
  "lastSequence": 1040
}
```

如果增量仍然存在，Worker/API 可以返回 `1041..current`；否则必须返回序号为 `N` 的完整 ConsoleSnapshot，再从 `N+1` 推送增量。

### 10.3 输入请求

```json
{
  "type": "session.input",
  "sessionId": "sess_123",
  "workerEpoch": 4,
  "promptId": "prompt_88",
  "clientMessageId": "01JXYZ...",
  "value": "0"
}
```

服务器必须对输入返回接受、重复、过期提示、无控制权或非法格式中的一种确定结果。

## 11. 数据与存储设计

### 11.1 容器内嵌入式数据库

使用 SQLite 或等价的嵌入式关系数据库，数据库文件位于挂载数据目录中，例如 `/data/cloudemuera.db`。

保存：

- 用户、角色与配额
- Game 与不可变 GameVersion 元数据
- Session 状态与当前 WorkerLease
- SaveArtifact 索引和审计数据
- 管理策略与兼容性报告摘要

数据库文件必须与游戏文件、Session 目录和存档一起纳入备份；容器重启不得改变已提交数据。

### 11.2 本地物理文件系统

容器必须将一个宿主机数据目录挂载到 `/data`。所有游戏、Session、存档、日志和备份都必须使用该目录下的物理文件或目录：

```text
/data/
├── cloudemuera.db
├── games/{gameId}/{version}/       ← GameVersion 文件树，只读挂载来源
├── sessions/{sessionId}/
│   ├── root/                       ← SessionRoot 实际运行目录
│   ├── saves/                      ← SaveArtifact 内容
│   └── metadata/
├── logs/
└── backups/
```

GameVersion 目录必须只读挂载到对应的 SessionRoot；SessionRoot、Session 临时目录和 SaveArtifact 工作副本必须可写且相互隔离。不得使用对象存储、远程文件系统或外部文件服务作为运行时或持久化依赖。备份必须通过宿主机文件系统快照、目录复制或等价的本地备份工具完成。

## 12. 安全需求

- **SEC-001**：所有游戏包均视为不受信任输入。
- **SEC-002**：Session Worker 默认不得访问公网、宿主密钥、其他用户文件和容器管理接口。
- **SEC-003**：游戏版本以只读方式提供给 Worker；仅分配的 Session 临时目录与存档目录可写。
- **SEC-004**：必须对 Worker 实施 CPU、内存、进程数、打开文件数、磁盘和输出速率限制。
- **SEC-005**：必须防止归档路径穿越、符号链接逃逸、大小写碰撞和超量解压。
- **SEC-006**：浏览器渲染必须对文本和属性编码；Emuera HTML 必须转换为受支持的结构化节点。
- **SEC-007**：Worker 本地 IPC 端点不得暴露到容器外，并必须验证来自 API 的服务身份。
- **SEC-008**：存档下载和资源 URL 必须短期有效、绑定权限，或通过经过授权的 API 代理。
- **SEC-009**：日志、指标和崩溃文件必须避免泄露认证信息及不必要的用户输入内容。
- **SEC-010**：依赖和上游解释器更新必须经过兼容性回归和安全审查后才能成为新的运行时基线。

## 13. 非功能需求

### 13.1 可用性与恢复

- **NFR-001**：正常负载下，已运行 Session 的重连及初始快照显示目标为 P95 不超过 2 秒，不含用户外部网络异常。
- **NFR-002**：API 进程重启不得主动终止健康的 Session Worker；Worker Supervisor 必须继续管理这些进程。
- **NFR-003**：本地 IPC 暂时中断时 Session Worker 必须继续运行并尝试重新注册；输出缓冲达到上限后必须保留最新 ConsoleSnapshot。
- **NFR-004**：Worker 崩溃不得损坏已经成功提交的存档；Session 必须清晰标记为 `CRASHED`。
- **NFR-005**：第一阶段不把任意执行点恢复作为 SLA；仅保证持久存档恢复和诊断信息可用。

### 13.2 性能与容量

- **NFR-006**：API 在正常负载下的 Session 列表和详情请求目标为 P95 不超过 300 ms，不含大文件传输。
- **NFR-007**：已连接 Session 的输入从 API 接受到 Worker 接收的内部传输目标为 P95 不超过 200 ms，不含 ERB 执行时间。
- **NFR-008**：Worker 必须对高频 `PRINT` 执行批处理并限制内存；慢客户端不能阻塞解释器主循环或无限增长队列。
- **NFR-009**：并发容量必须通过可重复的代表性游戏负载测试确定，不能仅依据空闲 Worker 数量估算。
- **NFR-010**：系统必须暴露每 Session 的内存、CPU、事件速率、快照大小和输入等待时间，以支持容量规划。

### 13.3 可维护性

- **NFR-011**：解释器核心、平台抽象、Worker 协议和 Web 渲染器必须分别测试，不能依赖浏览器端到端测试覆盖全部逻辑。
- **NFR-012**：中英文需求文档必须保持相同需求编号；变更某一语言时必须同步检查另一份文档。
- **NFR-013**：所有消息协议必须包含版本字段，并对未知可选字段向前兼容。
- **NFR-014**：上游 EM+EE 合并必须生成变更报告并运行 v18 与当前 EM+EE 双测试集。

### 13.4 可访问性与客户端兼容

- **NFR-015**：Web UI 应遵循 WCAG 2.1 AA 的关键交互要求，包括键盘操作、焦点可见性和足够对比度。
- **NFR-016**：必须支持发布时仍受维护的 Chrome、Firefox、Safari 和 Edge 主要版本。
- **NFR-017**：移动端不得依赖悬停才能操作；按钮和输入区域必须适合触摸。

## 14. 故障模型

| 故障 | 预期行为 |
| --- | --- |
| 浏览器刷新/断网 | Session 转为或保持 DETACHED；重连后恢复快照与当前 prompt |
| API 进程重启 | Worker Supervisor 和 Session Worker 保持运行；API 通过持久元数据找回 Session |
| API 与 Worker 本地 IPC 短时中断 | Worker 有界缓冲并重连；恢复后核对 epoch 与序号 |
| Worker 崩溃 | Session 标记 CRASHED；保留已提交存档；不承诺指令级恢复 |
| Docker 容器或宿主机重启 | 活动 Worker 按故障处理；挂载数据目录中的已提交游戏和存档保留 |
| 挂载数据目录不可用 | 禁止新建 Session 和写入存档，并明确报告持久化故障 |
| 客户端重复输入 | 通过 promptId 和 clientMessageId 去重 |
| 旧 Worker 恢复连接 | 因 epoch 落后被 fencing，不能继续产生有效状态 |

## 15. MVP 验收场景

- **AC-001**：一个用户同时启动同一游戏的两个 Session，两者变量、显示、输入和存档互不影响。
- **AC-002**：用户在等待输入时关闭网页，至少在配置的测试间隔后重新登录，能够看到最新显示并继续同一 prompt。
- **AC-003**：API 服务重启期间 Worker 不退出；API 恢复后用户可以重新连接。
- **AC-004**：两个用户使用同一 GameVersion 时，不能访问或覆盖彼此存档和 Session。
- **AC-005**：重复提交同一输入消息只执行一次。
- **AC-006**：用户显式关闭 Session 后 Worker 在限定时间内退出，Session 状态变为 CLOSED，随后输入被拒绝。
- **AC-007**：强制终止 Worker 后，Session 在心跳窗口内变为 CRASHED，已提交存档仍可下载。
- **AC-008**：代表性的 `1824+v18` 测试游戏能够完成加载、输入、保存、加载和主要显示场景。
- **AC-009**：代表性的当前 EM+EE 测试游戏能够运行已声明为 Supported 的特性；未支持功能产生明确诊断。
- **AC-010**：包含路径穿越、符号链接逃逸或压缩炸弹特征的游戏包被拒绝且不写出沙箱。
- **AC-011**：桌面和移动浏览器均可完成创建 Session、游戏输入、断线重连和存档下载。
- **AC-012**：持续大量输出时，Worker 与浏览器内存保持在配置边界内，重连仍能获得一致快照。
- **AC-013**：代表性游戏在根目录存档模式和 `sav/` 模式下均可用 Emuera 原生逻辑完成保存与加载；物理存档只出现在对应 Session 的私有区域。
- **AC-014**：两个用户以及同一用户的两个 Session 使用相同 GameVersion 时，GameVersion 文件保持只读，Global 存档按 `User + Game + Session` 隔离，不会跨用户或跨 Session 共享。

## 16. 分阶段交付建议

### Phase 0：兼容性与运行时验证

- 固定 EM+EE 上游提交。
- 将解释器与 WinForms/GDI+ 分离，定义 `IGameConsole`、`RuntimePaths`、文件、时钟、音频和图像抽象。
- 构建 v18 与 EM+EE 最小测试游戏集。
- 验证单 Session 无 UI Worker 能运行到 INPUT、接受输入，并使用原生格式在根目录与 `sav/` 两种布局中保存和加载。

### Phase 1：单机 MVP

- 单 Docker 容器、单 API 进程、单 Worker Supervisor、SQLite 和挂载的数据目录。
- 容器内一个 API 进程、一个 Worker Supervisor 进程，以及每 Session 一个独立子进程。
- WebSocket 重连、ConsoleSnapshot、存档隔离和基础 Web 渲染。
- 游戏包上传、编辑、不可变发布与基础诊断。

### Phase 2：自托管完善

- Docker 镜像、健康检查、日志、文件系统备份和升级流程。
- 资源配额、审计、指标、备份和管理员控制台。
- 更完整的 HTML、Sprite、音频与移动端兼容。

### Phase 3：挂起与恢复

- 研究 INPUT 安全点的解释器快照。
- 支持 SUSPENDED/RESUMING，释放长期离线 Session 的活动内存。
- 在可验证条件下支持容器内 Worker 崩溃后的 Session 恢复。

## 17. 待确认事项

1. MVP 使用本地账户，还是接入 OIDC/OAuth 身份提供商？
2. 每用户默认活动运行 Session 配额，以及游戏包/存档配额是多少？
3. 多标签页是否需要显式“控制权租约”，还是采用第一个有效输入生效？
4. MVP 对 Emuera HTML、Sprite、CBG 和音频分别承诺到什么兼容等级？
5. 是否允许管理员配置 Session 最大存活时间，还是仅允许资源/安全原因强制关闭？
6. 游戏和存档是否需要跨服务器导入导出格式？
7. 哪些代表性 v18 和 EM+EE 游戏可合法用于自动化兼容测试？
8. 是否需要保留原始游戏包中的字体；相关字体和游戏资源授权如何处理？

## 18. 主要风险

| 风险 | 影响 | 缓解方向 |
| --- | --- | --- |
| EM+EE 与 UI/平台代码耦合 | Worker 无头化工作量增加 | 先提取平台接口，建立差异测试 |
| Emuera 全局静态状态 | 同进程多 Session 相互污染 | 初期每 Session 独立进程 |
| 游戏根路径与存档路径耦合 | 共享资源可能被写入或用户存档串线 | 每 Session 实际 SessionRoot、只读游戏目录挂载、可写存档目录隔离 |
| 显示语义复杂 | 游戏可运行但 UI 错位 | 结构化显示模型和视觉回归测试 |
| 用户上传恶意包 | 文件逃逸或资源耗尽 | 沙箱、只读挂载、配额、上传校验 |
| Session 永久常驻 | 内存成本随遗忘会话增长 | 活动运行配额、管理员控制、未来挂起快照 |
| Worker 崩溃无法原地恢复 | 用户丢失未保存进度 | 原子存档、自动存档、明确故障语义、研究安全点快照 |
| 上游持续演进 | 补丁难以合并或兼容倒退 | 固定提交、薄适配层、双兼容测试集 |
| 游戏与字体授权不清 | 无法合法托管或再分发 | 保留所有权元数据、部署者确认授权、避免默认公开 |

## 19. 参考资料

- [Emuera.EM+EE 帮助文档](https://evilmask.gitlab.io/emuera.em.doc/zh/index.html)
- [Emuera.EM+EE 变更记录](https://evilmask.gitlab.io/emuera.em.doc/en/Changelog/index.html)
- [gEmuera](https://github.com/wwwXiaoHan17/gEmuera)
- [uEmuera](https://github.com/xerysherry/uEmuera)
- [lispcoc/gemuera IGameConsole](https://github.com/lispcoc/gemuera/blob/main/godot/src/Bridge/IGameConsole.cs)
