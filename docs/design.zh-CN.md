# CloudEmuera 详细设计

| 项目 | 内容 |
| --- | --- |
| 文档状态 | 草案 v0.5 |
| 日期 | 2026-08-12 |
| 输入文档 | [requirements.zh-CN.md](./requirements.zh-CN.md) v0.5 |
| 目标阶段 | Phase 0、Phase 1（MVP），并为 Phase 2 预留扩展点 |
| 目标读者 | 架构、前端、后端、运行时、运维、安全与测试开发者 |

## 1. 文档目的

本文档把需求文档中的产品要求和总体架构细化为可实施的软件设计，定义进程边界、模块职责、领域状态、持久化模型、文件布局、HTTP/WebSocket/IPC 协议、关键一致性算法、安全控制、故障处理和测试策略。

本文档不替代需求文档。若两者冲突，以带编号的需求为准，并通过设计评审修改本文档。本文中的“决定”表示 MVP 实现约束；“建议”表示可在不破坏接口语义的前提下调整；“待决”表示实现前仍需产品或技术评审确认。

范围说明：本文中保留的早期独立控制面、强隔离、细粒度资源和控制流恢复内容只描述历史实现或替代
方案；当前 MVP 以 [`ADR-0017`](adr/0017-trusted-self-hosted-mvp-simplification.md) 及 P1-S01 为准。

## 2. 范围与设计假设

### 2.1 MVP 边界

MVP 由一个 Docker 容器承载，容器内包含以下独立进程：

- 一个 Web/API 进程；
- 每个活动 Session 一个 Session Worker 进程；
- 一个负责监督 API、回收孤儿进程和转发容器信号的 init/进程管理器。

系统只依赖一个挂载到 `/data` 的持久化目录，不依赖外部数据库、对象存储、消息队列或远程文件系统。SQLite 是权威元数据存储，文件系统是游戏内容、完整 SessionRoot、原生存档、日志和备份的权威内容存储。

依据 ADR-0017，本实例由部署者为自己及其信任参与者运行。身份、角色和资源授权仍是应用级边界，
但 Worker 不作为敌对租户执行隔离边界；部署者只运行其信任的游戏。游戏包、路径、编码、显示和浏览器
输入仍按不安全数据格式验证，以防损坏输入、文件逃逸和浏览器注入。

### 2.2 暂定决策

需求文档第 17 节仍有未决项。为使接口设计可继续推进，本文采用以下可替换假设：

| 事项 | MVP 暂定方案 | 可替换边界 |
| --- | --- | --- |
| 身份认证 | 本地账户、email-only 登录、可撤销 HttpOnly Cookie Session；未初始化实例从 `.env` 原子 bootstrap 首个管理员 | `IIdentityProvider` 可替换为单一 OIDC Provider；bootstrap 完成后永久忽略首次配置且不开放注册 |
| 多客户端输入 | 同一 `promptId` 的第一个有效输入生效 | 后续可在 Realtime Gateway 前增加控制权租约 |
| Session 空闲 | 断连不自动关闭，持续占用实例级活动 Worker 名额 | 管理员策略只能通过显式配置启用 |
| 存档删除 | 无活动 Worker 时显式确认后直接删除 | 历史恢复由 SessionRoot 外部备份提供 |
| HTML/媒体兼容 | 固定上游中浏览器可安全表达的能力全部结构化支持；宿主禁止能力 fail closed | 能力由运行时清单和机器可校验兼容性矩阵声明 |
| 跨服务器迁移 | MVP 不定义可移植整包格式 | 数据备份不依赖该格式 |

实例级容量数值、测试游戏集、字体授权和具体媒体兼容等级不在本文中硬编码，统一从部署配置和运行时能力清单读取。MVP 不定义按用户或进程拆分的资源配额。

### 2.3 关键设计原则

1. **控制面与执行面分离**：API 管理身份、元数据和连接，Worker 独占解释器状态。
2. **一个活动 Session 一个进程**：隔离 Emuera 全局状态，并支持独立生命周期和有界终止。
3. **API 独占运行期数据库并确认进程事实**：Session 状态是持久化事实；API 内 Worker Manager
   观测进程、心跳和退出并以条件更新回写，不能只依赖进程内注册表。
4. **管理命令可重试、实时状态可替换**：持久 HTTP 命令有幂等键，控制命令有 `commandId`，输入
   有有界内存去重的 `clientMessageId`；断线显示状态由完整 Snapshot 替换，不持久补发历史增量。
5. **epoch fencing 优先于连接状态**：任何旧 Worker 即使恢复连接，也不能影响当前 Session。
6. **当前内容原子替换，Session 工作区私有**：Game 只有一份当前可运行内容；上传包进入独立
   摄取 workspace，验证后原子替换当前内容；每个 Session 只写自己的完整副本。
7. **显示数据结构化**：浏览器不执行游戏提供的 HTML、脚本或任意 URL。
8. **有界缓存**：Snapshot、实时连接队列、日志和当前 Worker 去重记录均有明确上限及降级方式。

### 2.4 技术选型结论

以下是 MVP 的主选技术栈。除标记为“按发布锁定”的工具外，主版本属于架构基线，不能在普通依赖升级中擅自切换。

| 组件 | 主选方案 | 版本基线 | 选择理由 |
| --- | --- | --- | --- |
| 服务端语言与运行时 | C# / .NET | .NET 10 LTS，使用当前安全补丁 | 与 Emuera Runtime 同语言，可直接复用解释器并共享领域/协议类型；LTS 支持至 2028-11-14 |
| Web/API | ASP.NET Core Minimal APIs + Kestrel | 10.x | 原生支持 WebSocket、认证授权、限流、健康检查、静态文件和 OpenAPI；单进程即可服务 API 与 SPA |
| 身份认证 | ASP.NET Core Identity + Cookie Authentication + Data Protection | 10.x | MVP 复用成熟的用户、密码、角色和安全戳能力；Data Protection key ring 持久化到 `/data`，保留替换为 OIDC 的认证端口 |
| API 描述 | ASP.NET Core OpenAPI + 仓库内 JSON Schema | 10.x / schema v1 | HTTP 契约由 OpenAPI 生成；WebSocket 负载用 JSON Schema 单独校验和生成 TypeScript 类型 |
| 浏览器实时通信 | ASP.NET Core 原生 WebSocket | RFC 6455，应用协议 v1 | CloudEmuera 需要自定义 epoch、sequence、完整 snapshot 替换和背压语义；不采用 SignalR 的 Hub/RPC 抽象 |
| WebSocket 编码 | UTF-8 JSON + `System.Text.Json` source generation | 应用协议 v1 | 易调试、浏览器零额外解码依赖；达到性能瓶颈后才通过新协议版本评估 MessagePack |
| 进程间通信 | gRPC 双向流 + Protocol Buffers over Unix Domain Socket | IPC 协议 v1 | 强类型、代码生成、截止时间和流式通信成熟；UDS 不暴露容器网络端口 |
| 元数据数据库 | SQLite | 3.x，随运行镜像锁定 | 符合单容器和本地备份要求，无外部服务；使用 WAL、外键和 busy timeout |
| 数据访问 | EF Core SQLite Provider；关键 CAS 使用参数化原生 SQL | EF Core 10.x | 普通 CRUD、关系和迁移成本低；状态机、实例级容量检查和 epoch 更新用显式 SQL 保证条件更新可审查 |
| 数据库迁移 | 独立 `CloudEmuera.Migrator` 控制台程序 | 与应用同版本 | 容器初始化阶段执行并持有独占迁移锁；API 和 Worker 不在运行时自动迁移 |
| 后台任务 | ASP.NET Core `BackgroundService` + SQLite 持久任务表 | .NET 10 | 上传验证、孤儿清理等任务需要重启可恢复；不引入外部消息队列 |
| Runtime Host | C# Console Worker + 仓库内 Emuera.EM+EE 固定源码快照 | 清单锁定 commit 与 integration version | 保持原生解释器和存档格式；直接修改内置源码并通过适配层替换 WinForms/GDI+、路径、输入和显示 |
| 图像与字体兼容层 | System.Drawing.Common 6.0.0 + libgdiplus（MVP） | NuGet lock + 镜像包锁定 | 依据 ADR-0019 复用固定上游的 GDI+ 像素语义；只存在于 Worker 内部，输出转换为平台无关结构，Skia/HarfBuzz 为长期替换方向 |
| 游戏包格式 | MVP 仅接受 ZIP；使用 `System.IO.Compression` 安全逐项解包 | .NET 10 | 收窄攻击面且不增加原生解压依赖；7z/RAR 等格式需另行评审和协议声明 |
| 文本编码 | `System.Text.Encoding` + `System.Text.Encoding.CodePages` 严格解码器 | .NET 10 | 明确支持 UTF-8 BOM/无 BOM 与 Shift-JIS，并禁用依赖系统 locale 的隐式回退 |
| 容器进程管理 | 轻量 init/PID 1 | 随生产镜像锁定 | 只负责转发信号和回收僵尸进程；API 是唯一长驻服务，Session Worker 由 API Worker Manager 管理 |
| Worker 进程约束 | 同容器非 root 子进程 + 私有启动绑定 + 可选容器整体限制 | 随生产镜像锁定 | 独立进程隔离 Emuera 全局状态并支持有界终止；API 与 Worker 可同 UID，MVP 不宣称具备敌对租户隔离 |
| 前端语言和框架 | TypeScript + React | TypeScript 7.x、React 19.x | 适合复杂交互和长期状态界面，生态成熟；不采用 SSR，输出纯 SPA |
| 前端构建 | Vite + Node.js + pnpm | Vite 8.x、Node.js 24 LTS、pnpm 11.x | 仅在构建阶段使用 Node；生产镜像不包含 Node.js，依赖由 lockfile 固定 |
| 路由与服务端状态 | React Router + TanStack Query | 当前兼容主版本，lockfile 固定 | 路由与 HTTP 缓存/失效交给成熟库；游戏实时状态不进入 Query 缓存 |
| 游戏实时状态 | React `useSyncExternalStore` + 自研 Session Store | 内部接口 v1 | 高频增量需要按 sequence 串行应用和有界内存，避免通用全局状态库造成不必要重渲染 |
| 样式与无障碍 | CSS Modules + CSS Custom Properties + React Aria primitives | 按发布锁定 | 保持样式可控，以可访问交互原语实现焦点、键盘和触摸行为；不引入完整视觉组件框架 |
| Game 文件查看 | React 只读文本/目录视图 | 随前端版本 | 展示编码和 Validator 诊断并支持下载；不引入浏览器写入或搜索状态 |
| 显示渲染 | React DOM + Canvas 2D + Web Audio API | 浏览器标准 | 文本和按钮使用可访问 DOM；Sprite/背景层使用 Canvas；音频使用浏览器原生 API |
| 日志 | `Microsoft.Extensions.Logging` JSON Console | .NET 10 | 默认写 stdout/stderr 交给容器采集；可选写 `/data/logs` 轮转文件，不绑定专有日志服务 |
| 诊断 | 结构化日志 + health/version + 管理员基本状态接口 | 随应用版本 | 保留请求/Session/Worker 关联与最近错误；MVP 不引入专用遥测平台或时序容量面板 |
| 后端测试 | xUnit + ASP.NET Core `WebApplicationFactory` | 与 .NET 10 兼容版本 | 单元、契约和 API 集成测试统一在 .NET 工具链运行 |
| 前端测试 | Vitest + Testing Library | 与 Vite 8 兼容版本 | 组件和状态归约器测试速度快，并复用 Vite 配置 |
| 端到端测试 | Playwright | 按发布锁定 | 同时覆盖 Chromium、Firefox、WebKit 和移动设备配置，匹配浏览器兼容需求 |
| 容量验证 | xUnit 集成场景 + 代表性 ERB workload | 随测试工程 | 验证活动 Worker 上限、持续输出和有界队列；MVP 不引入独立负载测试框架 |
| 容器镜像 | Debian slim 系列的 .NET 10 ASP.NET Runtime 多阶段镜像 | digest 锁定 | 相比 Alpine/musl 对原生库、字体和诊断工具兼容性更稳；最终镜像不包含 SDK/Node |

### 2.5 选型边界与明确不选项

#### 2.5.1 SignalR 与原生 WebSocket

ASP.NET Core 官方通常建议业务应用优先考虑 SignalR，但 CloudEmuera 需要显式控制完整 Snapshot
替换、序号缺口检测、逐连接有界队列和输入回执。因此 MVP 选择原生 WebSocket，避免同时维护
SignalR 重连语义。若将来需要 SSE/long polling 降级，再以独立 ADR 评估。

#### 2.5.2 EF Core 与关键状态更新

EF Core 用于身份、Game、普通查询和迁移；以下操作必须使用显式事务和参数化条件 SQL，并检查受影响行数：

- SessionRoot 存储预算与 `CREATING` Session 插入；
- 实例级活动 Worker 上限检查与 `CLOSED/CRASHED → STARTING` open 转换；
- `state_version` 比较交换；
- Worker epoch 递增和租约替换；
- 第一个 prompt 输入抢占；
- 停止态存档修改权与 Worker 租约的互斥抢占。

SQLite 不提供数据库自动生成的并发 token，故 `state_version` 由应用显式递增。SQLite Provider 的部分迁移需要重建表，因此生产升级必须先创建一致性备份，由 Migrator 单独执行，禁止多个业务进程自动调用 `Migrate()`。

#### 2.5.3 gRPC 与浏览器协议

gRPC/Protobuf 只用于容器内 API 和 Worker。浏览器不使用 gRPC-Web，因为浏览器通道需要双向、低延迟且具备自定义 snapshot/resume 语义；大型资源继续走 HTTP。`.proto` 文件是 IPC 的权威契约，生成的 C# 类型不能直接成为领域模型。

#### 2.5.4 前端状态划分

TanStack Query 只管理 HTTP 资源，例如游戏列表、Session 元数据和存档索引。游戏控制台的完整
snapshot、后续实时 batch、prompt 和当前 Worker 内输入回执由每 Session 独立 Store 管理，按
`(workerEpoch, sequence)` 归约。两者不得复制保存同一份权威实时状态。

#### 2.5.5 不引入的基础设施

MVP 不采用 PostgreSQL、Redis、Kafka/RabbitMQ、对象存储、Kubernetes、Node.js 服务端、SSR/Next.js 或独立反向代理。它们与单容器自托管边界不匹配，并会增加备份和故障域。若未来需求进入多容器/多主机阶段，必须重新评审，而不是预先加入闲置依赖。

### 2.6 版本与依赖治理

- 仓库固定 `.NET SDK 10.0.x` feature band、Node.js 24 LTS、pnpm 主版本，并提交 NuGet/npm lockfile；
- 基础镜像、轻量 init 和下载的外部二进制同时锁定版本与 SHA-256/digest；
- .NET 与 ASP.NET Core 跟随 .NET 10 每月安全补丁，不锁死到初始 patch；
- React、Vite、EF Core、gRPC 等主版本升级必须通过兼容性、安全和性能回归；
- 生产构建生成 SBOM、NuGet/npm 依赖清单和 Runtime manifest；
- 前端构建产物复制进 ASP.NET Core `wwwroot`，最终运行镜像不保留 npm token、源码映射（除非显式启用）或 Node 工具链；
- 所有第三方库在引入前检查许可证、维护状态和已知安全问题，避免仅为一个简单辅助函数新增运行时依赖。

### 2.7 建议的解决方案结构

```text
src/
├── CloudEmuera.Domain/            # 领域实体、状态机、策略，无基础设施依赖
├── CloudEmuera.Application/       # 用例、端口、DTO、授权调用
├── CloudEmuera.Infrastructure/    # EF Core、SQLite、文件系统、Worker 子进程、审计实现
├── CloudEmuera.Api/               # ASP.NET Core HTTP/WS、Worker IPC、静态 SPA
├── CloudEmuera.Contracts/         # HTTP/WS schema 与共享常量
├── CloudEmuera.Ipc/               # .proto 与 gRPC 生成代码
├── CloudEmuera.Realtime/           # API/Worker 共用的闭合结构化输出映射
├── CloudEmuera.Worker/            # 单 Session Runtime Host
├── CloudEmuera.RuntimeAdapter/    # 平台无关的 Console/Input/File/Clock/Media 契约
├── CloudEmuera.EmueraRuntime/     # 内置 Emuera 源码、headless host 与接线
├── CloudEmuera.Migrator/          # 独立数据库迁移程序
└── CloudEmuera.Web/               # React/TypeScript/Vite SPA
tests/
├── CloudEmuera.Domain.Tests/
├── CloudEmuera.Runtime.Tests/
├── CloudEmuera.IntegrationTests/
├── CloudEmuera.Protocol.Tests/
└── e2e/
```

依赖方向必须保持：`Domain ← Application ← adapters`，以及 `RuntimeAdapter ← EmueraRuntime ← Worker`；
`Realtime` 只依赖 `Ipc` 与 `RuntimeAdapter`，供 API/Worker 共用边界映射，不反向依赖宿主。
`Application` 只定义 Worker 生命周期端口和事务用例；进程/UDS 实现位于外部适配层。
`RuntimeAdapter` 不引用真实解释器、Worker 或 Web/API；`Worker` 不引用 EF Core；API 不加载
Emuera 解释器；前端只依赖生成的公开契约。

### 2.8 选型依据

- [.NET 官方支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)：.NET 10 是当前活动 LTS，支持至 2028-11-14。
- [ASP.NET Core WebSocket 文档](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)：Kestrel 原生提供 WebSocket 和 Origin 等配置能力。
- [.NET gRPC over UDS 文档](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds?view=aspnetcore-10.0)：官方支持以 Unix Domain Socket 承载进程间 gRPC。
- [EF Core SQLite 限制](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)：明确了并发 token 和表重建式迁移等限制，本文据此采用应用版本号和独立 Migrator。
- [React 19](https://react.dev/blog/2024/12/05/react-19) 与 [Vite 8](https://vite.dev/blog/announcing-vite8)：采用当前稳定主版本；Node.js 仅参与构建。
- [Node.js 发布策略](https://nodejs.org/en/about/previous-releases)：构建环境采用 Node.js 24 LTS，不采用 Current 分支。
- [Playwright 浏览器矩阵](https://playwright.dev/docs/browsers)：可覆盖 Chromium、Firefox、WebKit 及移动设备配置。
- [Docker resource constraints](https://docs.docker.com/engine/containers/resource_constraints/)：部署者可以在容器整体层设置 CPU、内存和 PID 上限。
- [libgdiplus](https://www.mono-project.com/docs/gui/libgdiplus/)：P1 MVP 在 Linux Worker 内承接固定上游的 GDI+ 调用；具体约束见 ADR-0019。

## 3. 系统上下文与进程架构

```text
Desktop/Mobile Browser
        │ HTTPS / WebSocket
        ▼
┌──────────────────────── Docker container ───────────────────────┐
│  Web/API                                                        │
│  ├─ Identity & Authorization                                    │
│  ├─ Game / Save API                                             │
│  ├─ Session Control Plane                                       │
│  ├─ Realtime Gateway                                            │
│  ├─ Admin / Audit                                               │
│  └─ Worker Manager ─── spawn/monitor/stop ────────┐              │
│          │ gRPC over Unix domain socket           ▼              │
│          ├────────────────────────────── Session Worker A         │
│          └────────────────────────────── Session Worker B         │
│                                                                 │
│  /data: SQLite, immutable games, private sessions, logs, backups │
└─────────────────────────────────────────────────────────────────┘
```

### 3.1 Web/API 进程

Web/API 进程不得加载 Emuera Runtime，也不得持有只能存在于内存中的权威 Session 状态。其内部模块如下：

| 模块 | 职责 | 不负责 |
| --- | --- | --- |
| Identity | 登录、登出、Cookie/OIDC 回调、用户状态 | 资源授权判断 |
| Authorization | 所有资源级访问决策、管理员策略 | 仅依赖前端可见性 |
| Game Package Service | 上传暂存、解包校验、摄取 workspace、current content 启用、只读文件浏览 | 浏览器内编辑或运行游戏 |
| Compatibility Service | 静态扫描、解析验证、生成能力报告 | 绕过 Blocked 能力 |
| Session Control Plane | 创建、关闭、查询、实例级 Worker 名额检查、状态转换 | 解释器执行、按用户拆分资源调度 |
| Realtime Gateway | WebSocket 鉴权、恢复、广播、背压、输入转发 | 修改显示事件语义 |
| Save Service | 在 Session 停止时授权访问其原生存档文件 | 解析存档内容、维护独立历史版本 |
| Admin Service | Worker/Session 基本状态、强制停止、已实现的策略配置 | 资源指标平台、绕过审计 |
| Audit Service | 已实现的关键追加式审计记录 | 通用查询 UI、保存密码或输入全文 |
| Worker Manager | Worker 启动、UDS、心跳、进程退出和故障对账 | 加载 Emuera Runtime、替代持久 lease 事实、只以内存保存 lease |

API 进程内部建议采用分层依赖：

```text
HTTP / WebSocket adapters
          ↓
Application use cases
          ↓
Domain model + authorization policies
          ↓
SQLite repositories / filesystem / Worker lifecycle adapter
```

领域层不得依赖 HTTP、WebSocket、SQLite 或 Unix socket 的具体类型。

API 是运行期间唯一访问 SQLite 的业务进程。HTTP 请求、Realtime Gateway 和 Worker Manager
各自使用短生命周期数据库上下文；不能因为位于同一进程就共享长事务或省略 `state_version`、CAS、
busy timeout 和幂等约束。

### 3.2 API 内 Worker Manager

Worker Manager 是 Session Worker 生命周期和本地路由的唯一管理者，职责包括：

- 启动时扫描持久活动 Session，清理上一 API 实例的 Worker 并完成故障对账；
- 由 Application 用例创建 SessionRoot、分配 `workerId` 和 epoch，再启动 Worker；
- 维护 Worker 心跳、进程退出码和本地 IPC 连接；
- 检查实例级最大活动 Worker 数，并依赖有界 Snapshot/IPC 队列保护应用内存；
- 将 Worker 事件转发给 Realtime Gateway；
- 对关闭和强制终止执行分阶段回收；
- 拒绝所有控制面实例身份或 epoch 不是数据库当前值的 Worker 注册、心跳和输出。

Worker Manager 不解析 ERB，不把进程句柄或连接表当作权威 Session 状态，也不直接处理用户授权。
Application 定义 `IWorkerManager` 等端口；进程和 UDS 实现留在外部适配层，避免 HTTP handler
直接操作 `System.Diagnostics.Process`。

### 3.3 Session Worker 进程

每个 Worker 只加载一个已经物化的 SessionRoot，且在整个生命周期内只服务一个 Session。内部组件如下：

| 组件 | 职责 |
| --- | --- |
| Runtime Host | 初始化、运行和终止 Emuera 解释器 |
| RuntimePaths | 注入 SessionRoot、资源、配置、临时文件与两种存档布局 |
| Console Adapter | 将解释器绘制调用转换为结构化显示操作 |
| Snapshot Store | 维护当前 ConsoleSnapshot、序号和实时发送所需的有界批次队列 |
| Input Coordinator | 生成 prompt、验证输入、去重并唤醒 Runtime |
| Capability Guard | 禁止 DLL、进程、非允许网络和不安全路径操作 |
| Worker IPC | 注册、心跳、事件发送、命令接收；控制通道断开即触发退出 |

Runtime 主循环不得被浏览器发送速度阻塞。Console Adapter 将事件写入有界内部队列；队列接近上限时合并相邻文本/样式事件，超过上限时生成新快照并丢弃已被快照覆盖的旧增量。

P0-06 的历史进程切片已落在旧控制面实现和
`CloudEmuera.Worker/WorkerConnectionLoop`：当时的控制面在自己的私有 runtime 目录监听
单一 UDS，使用一次性 bootstrap JSON 启动一个带不可变
`sessionId/workerId/workerEpoch` 的 Worker；Worker 只使用已经物化的 SessionRoot，并以
`worker.proto` 的 IPC v1 双向流发送结构化 Console、输入结果、心跳和终态。Worker 的
控制/显示发送队列有界且由单一 gRPC writer 串行写出。早期实现的短断恢复行为已由 ADR-0017
取消，正式 Worker 在控制通道断开后退出。UDS stale endpoint 只有在 `lstat` 确认 socket 类型、服务账户
owner、单链接和 `0700` 父目录后，才通过受保护父目录句柄 `unlinkat` 清理；祖先 symlink、
路径越界、权限或属主异常均拒绝。Worker 生命周期日志输出结构化
`sessionId/workerId/workerEpoch`，并过滤 token、SessionRoot、UDS/bootstrap 路径和输入值。
这仍是 IPC 与独立 Worker 隔离的有效证据，但早期控制面拓扑及跨 API 实例接管已由
[`ADR-0015`](adr/0015-api-owned-worker-lifecycle.md) 取代。P1-05 已把可复用实现迁入 API，增加
持久 WorkerLease、epoch 分配、API 生命周期绑定和启动故障对账，并删除旧独立控制面入口；
当前生产拓扑只保留 API Worker Manager。

### 3.4 进程启动顺序

1. init 独占运行 Migrator；迁移或完整性检查失败则不启动 API；
2. init 启动 API，API 生成新的控制面实例身份并建立受保护 Worker UDS；
3. Worker Manager 清理或终止上一实例遗留 Worker，确认其已失去 SessionRoot 写权限；
4. API 以条件更新把遗留活动 Session 对账为 `CRASHED`、失效 lease 并释放活动 Worker 名额；
5. 数据库、数据目录、Worker Manager 和迁移版本检查成功后，API 开始接收业务流量。

API 正常停止时先停止接入和新输入，再对 Worker 执行有界优雅停止，超时后强制终止。API 异常
退出时，Worker 在控制通道断开后立即开始有界退出；parent-death signal 或专属进程组提供兜底。
控制面停止中断的活动 Session 进入 `CRASHED` 而不是伪装成用户关闭的 `CLOSED`；新 API 在无法
证明某个旧 Worker 已退出时阻止对应 Session 的 open 和存档写，不把缺少额外内核隔离能力作为全局
readiness 失败条件。

## 4. 领域模型与状态约束

### 4.1 标识符

外部可见实体使用不可预测的时间有序 ID，字符串带类型前缀，例如 `usr_`、`game_`、`sess_`、
`wrk_`、`save_`。数据库内部可直接以该字符串为主键。不得把连续整数 ID 暴露为资源定位符。
Game 内容 revision 是内部单调计数，不是外部资源 ID，也不提供历史读取或回滚 API。

时间统一以 UTC 存储为 RFC 3339 字符串或 Unix 毫秒；API 输出 RFC 3339。校验和统一使用 `sha256:<lowercase-hex>`。

### 4.2 Session 聚合

Session 是状态转换和 WorkerLease 的事务边界。核心不变量：

- 一个 Session 固定一个 `gameId`、创建时源内容摘要和运行时清单快照，创建后不可更改；
- 活动状态最多有一个当前 WorkerLease；
- 当前租约 epoch 只能递增，不能复用；
- `CLOSED` 和 `CRASHED` 都是无 Worker、可重新开启的持久状态，不是 Session 资源终态；
- 重新开启必须经 `STARTING` 创建新 lease，不能把 `CRASHED` 直接改为 `RUNNING`；
- 状态更新必须同时匹配 `sessionId + stateVersion + expectedState`；
- Session 创建受实例级存储上限约束；`CLOSED/CRASHED → STARTING` 在同一事务中检查全局活动 Worker 上限；
- 创建后每次开启都复用相同 SessionRoot，不再次复制 Game current content。

### 4.3 Session 状态转换表

| 当前状态 | 事件 | 下一状态 | 执行者 | 事务/副作用 |
| --- | --- | --- | --- | --- |
| 无 | CreateAccepted | CREATING | API | 幂等键落库并执行已实现的 SessionRoot 存储预算流程 |
| CREATING | SessionRootPublished | CLOSED | API | 固定源摘要/manifest，Session 创建完成但不占 Worker |
| CLOSED/CRASHED | OpenRequested | STARTING | API | 检查实例级 Worker 上限，创建新 epoch 和 WorkerLease |
| STARTING | WorkerReady | RUNNING | API Worker Manager | 与浏览器连接数无关 |
| STARTING/RUNNING | CloseRequested | STOPPING | API | 拒绝后续新输入 |
| STOPPING | WorkerStopped | CLOSED | API Worker Manager | 清除活动租约并释放全局 Worker 名额 |
| STARTING/RUNNING | HeartbeatExpired/UnexpectedExit | CRASHED | API Worker Manager | 失效租约并释放全局 Worker 名额 |
| STARTING/RUNNING/STOPPING | ControlPlaneExited | CRASHED | API 启动对账 | 确认旧 Worker 退出后失效租约并释放全局 Worker 名额 |
| STARTING/RUNNING | AdminForceStop | STOPPING | API | 写审计，随后强制停止；静止态无 Worker 可停 |

浏览器连接数不是 Session 状态。Realtime Gateway 在内存中维护每个 Session 的有效连接数和最近
连接时间，通过 WebSocket close/heartbeat 最终发现断流；零连接时 Session 仍为 `RUNNING`，不写
SQLite、不停止 Worker、不释放全局 Worker 名额。若 API 崩溃，新 API 不恢复旧运行时；它先确认旧 Worker
已退出，再把遗留活动状态收敛为 `CRASHED`。

`CLOSED` 只表示最近一次运行正常结束，`CRASHED` 只表示最近一次运行异常结束；两者都保留同一
Session ID 和 SessionRoot。重新开启是冷启动新 Worker，用户通过游戏自己的加载功能使用原生存档，
不恢复旧解释器内存。Session close 与 Session delete 是不同用例；MVP 不因关闭或崩溃自动删除目录。

### 4.4 WorkerLease

WorkerLease 至少包含：

```text
sessionId, workerId, epoch, status, pid,
ipcEndpoint, acquiredAt, heartbeatAt, expiresAt,
runtimeVersion, protocolVersion
```

新 Worker 创建必须在数据库事务内执行 `epoch = previousEpoch + 1`。API Worker Manager 向 Worker
签发只对 `controlPlaneInstanceId + sessionId + workerId + epoch` 有效的启动令牌。API 和 Worker
在处理控制命令、输入、心跳和输出时都要匹配当前实例与 epoch。存档不经过 IPC 提交；只有当前
Worker 能写当前 SessionRoot。

## 5. 持久化设计

### 5.1 SQLite 使用约束

- 启用 WAL、外键和 busy timeout；
- 运行期间只有 API 业务进程访问 SQLite；Migrator 只在 API 启动前独占运行，Worker 不访问数据库；
- 业务写入使用短事务，不在事务内执行解压、哈希大文件或等待 IPC；
- 迁移由 Migrator 持有文件锁后执行，迁移期间 API 和 Worker 尚未启动；
- 所有带状态的聚合使用 `state_version` 做乐观并发控制；
- 数据库时间由应用注入的 UTC 时钟产生，便于测试；
- 定期执行完整性检查和受控 WAL checkpoint；备份必须使用 SQLite Online Backup API 或一致性文件系统快照。

### 5.2 逻辑表设计

字段类型以 SQLite 表达，细节可由 ORM 迁移生成。

P1-01 已将本节的首版元数据基线固化为 `InitialMetadata` migration：所有 `*_at` 列为
`INTEGER` Unix epoch milliseconds（应用边界使用 `DateTimeOffset`），状态/角色/结果为
稳定的大写 `TEXT`，布尔值为受 `CHECK` 约束的 `INTEGER`，JSON 使用 `json_valid` 校验。
ID 使用 `TEXT` 类型前缀值，内容摘要使用 `sha256:` 加 64 位小写十六进制，路径为受约束的
`/` 分隔相对 DataRoot 路径。所有首版外键都显式使用 `ON UPDATE RESTRICT` 和
`ON DELETE RESTRICT`。

#### quota_profiles

```text
id TEXT PK
name TEXT UNIQUE NOT NULL
max_active_sessions INTEGER NOT NULL
max_game_package_bytes INTEGER NOT NULL
max_session_bytes INTEGER NOT NULL
max_output_bytes_per_second INTEGER NOT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
state_version INTEGER NOT NULL DEFAULT 0
```

#### users

```text
id TEXT PK
login_name TEXT UNIQUE NOT NULL
normalized_login_name TEXT UNIQUE NOT NULL
email TEXT NULL
normalized_email TEXT NULL
password_hash TEXT NULL
security_stamp TEXT NOT NULL
role TEXT NOT NULL                 -- PLAYER | ADMIN
status TEXT NOT NULL               -- ACTIVE | DISABLED
access_failed_count INTEGER NOT NULL DEFAULT 0
lockout_end INTEGER NULL
quota_profile_id TEXT NOT NULL
preferences_json TEXT NOT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
state_version INTEGER NOT NULL DEFAULT 0
```

该表通过自定义 EF Core 映射作为 ASP.NET Core Identity 的用户存储；若后续启用多角色、外部登录、恢复令牌或 MFA，再增加标准化的 `user_roles`、`user_logins`、`user_tokens` 和 `user_claims` 表，不把认证票据明文写入 `users`。

P1-02 通过新 migration 为 `users` 增加 `email`、`normalized_email`、`must_change_password` 和
`password_changed_at`；email 是唯一登录凭据，login_name 只作为唯一显示/管理标识。不得修改
P1-01 的 `InitialMetadata` migration。

#### instance_state

```text
id INTEGER PK                         -- 固定为 1
bootstrap_status TEXT NOT NULL        -- BOOTSTRAP_REQUIRED | COMPLETED
initialized_at INTEGER NULL
initial_admin_user_id TEXT NULL FK users
state_version INTEGER NOT NULL DEFAULT 0
```

该单例是实例是否曾初始化的权威事实。BOOTSTRAP_REQUIRED 时，API 从部署环境读取管理员
username、email 和临时 password，在一个 SQLite 写事务中创建默认 quota、首个管理员、审计与
完成标记，并设置首次登录强制改密。完成状态不可逆，后续启动忽略 bootstrap 变量，不根据当前
管理员数量重新推导；管理员全部缺失、禁用或遗失密码时也不得重跑。

#### auth_sessions

```text
id TEXT PK
user_id TEXT NOT NULL FK users
security_stamp TEXT NOT NULL
created_at INTEGER NOT NULL
last_seen_at INTEGER NOT NULL
idle_expires_at INTEGER NOT NULL
absolute_expires_at INTEGER NOT NULL
revoked_at INTEGER NULL
revoke_reason TEXT NULL
is_persistent INTEGER NOT NULL
```

浏览器 Cookie 包含受 Data Protection 保护的最小 claims 和随机 session ID；每次认证都复核
该表与 User 当前状态。注销、禁用、角色或密码变化会撤销对应记录，API 重启不丢失有效会话。

#### games

```text
id TEXT PK
owner_user_id TEXT NOT NULL FK users
name TEXT NOT NULL
visibility TEXT NOT NULL           -- PRIVATE | SERVER_SHARED
status TEXT NOT NULL               -- ACTIVE | BLOCKED | DELETED
workspace_status TEXT NOT NULL     -- NONE | DRAFT | VALIDATING
workspace_path TEXT NULL
current_content_path TEXT NULL
content_digest TEXT NULL
content_revision INTEGER NOT NULL DEFAULT 0
manifest_json TEXT NOT NULL
runtime_config_json TEXT NOT NULL
compatibility_summary_json TEXT NOT NULL
activated_by TEXT NULL FK users
activated_at INTEGER NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
state_version INTEGER NOT NULL DEFAULT 0
UNIQUE(owner_user_id, name)
```

Game 不包含版本标签、版本集合或历史内容引用。`content_revision` 只协调当前内容原子替换和
Session 创建；同一摘要可以属于不同 Game，不建立全局唯一内容身份。workspace 可以与 current
content 同时存在，owner 上传候选 workspace 时新 Session 仍复制 current content。

#### sessions

```text
id TEXT PK
owner_user_id TEXT NOT NULL FK users
game_id TEXT NOT NULL FK games
source_content_digest TEXT NOT NULL
source_content_revision INTEGER NOT NULL
runtime_manifest_json TEXT NOT NULL
runtime_version TEXT NOT NULL
session_root_path TEXT NOT NULL UNIQUE
name TEXT NOT NULL
state TEXT NOT NULL
state_version INTEGER NOT NULL
worker_epoch INTEGER NOT NULL DEFAULT 0
waiting_for_input INTEGER NOT NULL DEFAULT 0
current_prompt_id TEXT NULL
last_output_sequence INTEGER NOT NULL DEFAULT 0
close_reason TEXT NULL
created_at INTEGER NOT NULL
started_at INTEGER NULL
last_activity_at INTEGER NOT NULL
closed_at INTEGER NULL
FOREIGN KEY(game_id) REFERENCES games(id)
```

P1-01 已固定 `close_reason/closed_at` 列名；在可重开模型中，它们表示当前静止状态的最近一次 Worker
停止原因和时间，而不是 Session 资源删除或终态时间。进入 `CLOSED/CRASHED` 时设置，成功进入新
`STARTING` 时清空；历史由 `audit_events` 保留。若未来重命名列，必须使用新增 migration，不回改
首版 migration。

常用索引：`(owner_user_id, created_at DESC, id DESC)`、`(state, last_activity_at)`、`(game_id)`、
`(game_id, source_content_digest)`。

#### worker_leases

```text
session_id TEXT PK FK sessions
worker_id TEXT UNIQUE NOT NULL
epoch INTEGER NOT NULL
status TEXT NOT NULL               -- STARTING | ACTIVE | STOPPING | EXPIRED
pid INTEGER NULL
ipc_endpoint TEXT NOT NULL
runtime_version TEXT NOT NULL
protocol_version INTEGER NOT NULL
acquired_at INTEGER NOT NULL
heartbeat_at INTEGER NOT NULL
expires_at INTEGER NOT NULL
UNIQUE(session_id, epoch)
```

#### idempotency_records

```text
actor_user_id TEXT NOT NULL
scope TEXT NOT NULL                -- SESSION_CREATE 等
idempotency_key TEXT NOT NULL
request_digest TEXT NOT NULL
status TEXT NOT NULL              -- IN_PROGRESS | SUCCEEDED | FAILED
response_status INTEGER NOT NULL
response_json TEXT NOT NULL
resource_id TEXT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
completed_at INTEGER NULL
error_code TEXT NULL
expires_at INTEGER NOT NULL
PRIMARY KEY(actor_user_id, scope, idempotency_key)
```

同一键但请求摘要不同返回 `409 IDEMPOTENCY_KEY_REUSED`。

`IN_PROGRESS` 是可恢复的持久事实，不能用 `response_json = '{}'` 判断；`SUCCEEDED/FAILED` 保存
版本化成功 DTO 或安全错误 DTO，terminal 记录至少保留客户端重试窗口。旧版本空 JSON 哨兵在
`20260811175156_AddIdempotentSessionLifecycle` 前滚 migration 中转换为 `IN_PROGRESS`，其他旧响应
转换为 `SUCCEEDED`。

#### session_creation_operations

```text
id TEXT PK                         -- scop_...
session_id TEXT UNIQUE NOT NULL FK sessions
actor_user_id TEXT NOT NULL FK users
status TEXT NOT NULL               -- PREPARED | COPYING | ROOT_PUBLISHED | COMMITTED | FAILED
staging_path TEXT UNIQUE NOT NULL
reserved_bytes INTEGER NOT NULL
expected_file_count INTEGER NOT NULL
expected_content_bytes INTEGER NOT NULL
attempt_count INTEGER NOT NULL
last_error_code TEXT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
completed_at INTEGER NULL
state_version INTEGER NOT NULL
```

#### session_root_mutation_leases

```text
session_id TEXT PK FK sessions
operation_id TEXT UNIQUE NOT NULL  -- mut_...
actor_user_id TEXT NOT NULL FK users
purpose TEXT NOT NULL               -- SAVE_IMPORT | SAVE_RENAME | SAVE_DELETE | SAVE_COPY
acquired_at INTEGER NOT NULL
expires_at INTEGER NOT NULL
```

停止态 mutation lease 与 `worker_leases` 在同一 `BEGIN IMMEDIATE` 互斥裁决；P1-06 只交付端口，
存档 HTTP 用例留给 P1-10。创建 operation 和生命周期幂等命令由 API 启动/周期恢复器处理，不能只
依赖 API 进程内任务。

#### audit_events

```text
id TEXT PK
occurred_at INTEGER NOT NULL
actor_user_id TEXT NULL
actor_type TEXT NOT NULL            -- USER | ADMIN | SYSTEM
action TEXT NOT NULL
resource_type TEXT NOT NULL
resource_id TEXT NOT NULL
request_id TEXT NULL
result TEXT NOT NULL
reason_code TEXT NULL
metadata_json TEXT NOT NULL
```

审计表只追加，不通过普通业务 API 修改或删除。

P1-03 新增 `game_package_ingestions` 作为内部摄取状态和全局 staging 字节预留表。它不代表
Game，也不是对外的上传资源；READY 候选只有在 P1-04 校验 owner、digest 和期限并以 CAS
进入 CONSUMING 后才能绑定 Game workspace。字段、状态、释放预算和遗留项恢复规则见
[`tasks/P1-03-secure-game-package-ingestion-plan.zh-CN.md`](tasks/P1-03-secure-game-package-ingestion-plan.zh-CN.md)。
摄取文件树仅通过受保护 dirfd 和 `openat(O_NOFOLLOW)` 访问；终态先由数据库 CAS 线性化，再以
`cleanup_completed_at` 记录安全后序清理完成。API 的启动及周期 reaper 会回收跨重启遗留项，
消费方只取得已校验的 content 目录句柄，不取得可重新解析的 staging 路径。每个 staging 根的
`lease.json` 绑定 ingestion ID 与目录 inode，所有清理前必须验证；竞争状态转换同时匹配 status
和 `state_version`。

P1-01 另外建立 EF history 表 `schema_migrations`，并保留 EF Core SQLite provider 使用的
内部 `__EFMigrationsLock` 表；产品迁移仍必须先持有 CloudEmuera 的
`<database>.migration.lock` 文件锁。旧 `game_versions` 表由 ADR-0010 的后续 migration 迁移并
删除；`game_files`、`compatibility_diagnostics`、`game_content_operations` 和短期
`worker_command_results` 留给后续任务。存档没有独立内容表；其 Game 和源摘要由所属 Session
决定，管理操作写入 `audit_events`。

### 5.3 文件系统布局

```text
/data/
├── cloudemuera.db
├── keys/                                   # ASP.NET Core Data Protection key ring
├── games/
│   ├── staging/{uploadId}/                 # 未信任，禁止 Worker 使用
│   └── {gameId}/
│       ├── workspace/                      # Game 的内部摄取/校验工作区
│       ├── content/                        # 当前只读 Session 复制来源
│       ├── runtime-manifest.json
│       └── operations/{operationId}/       # 冻结校验/启用暂存
├── sessions/{sessionId}/
│   ├── root/                               # Worker 可见 GameRoot
│   └── metadata/
│       ├── runtime-manifest.json
│       └── last-crash.json
├── run/                                    # UDS；重启可重建，不备份
├── logs/
└── backups/
```

`/data/run` 必须设置为仅 API 和 Worker 服务身份可访问；也可以实际放在容器 tmpfs 中，但对外路径保持配置化。

### 5.4 SessionRoot 构造

Session 管理方在首次启动 Worker 前构造持久、独占的运行目录：

```text
root/CSV/          # Game 当前内容的 Session 私有副本
root/ERB/          # Game 当前内容的 Session 私有副本
root/resources/    # Game 当前内容的 Session 私有副本
root/sound、font/  # 存在时原样复制
root/<其他目录>/   # 所有合法未知目录也原样复制
root/emuera.config # Game 当前配置的 Session 私有副本
root/save*.sav     # 根目录模式，Emuera 直接读写
root/global.sav    # 根目录模式，Emuera 直接读写
root/sav/          # UseSaveFolder:YES 时，Emuera 直接读写
root/tmp/          # Session 私有临时内容
```

Session 管理方按 Game 当前 manifest 复制完整文件树，不为 CSV/ERB/resources 建立特殊分类，也不
静默丢弃未知合法内容。基线实现使用普通字节复制；底层文件系统支持时可以尝试 reflink，但必须
保持写时复制语义并在不支持时回退普通复制。禁止硬链接，因为它会让 Session 与 Game current
content 或其他 Session 共享可写 inode。上传阶段已经拒绝软链接、硬链接、FIFO、设备和 socket，
复制阶段仍需再次核对实际条目，防止检查与复制之间被替换。

复制成功前先在同一 sessions 父目录下建立 staging root；只有文件数、总字节数、逐项类型和清单
摘要全部通过后，才原子重命名为最终 SessionRoot。失败只清理本次 staging，不触碰已存在
SessionRoot。Worker 正常启动参数只包含自己的完整副本及必要 IPC/系统路径，不传入 Game 库目录
或其他 Session；同 UID 部署不构成抵御恶意 Worker 的内核强制隔离保证。

SessionRoot 位于挂载数据目录中，本身就是存档的唯一权威副本。Worker 重启复用同一路径；正常退出和崩溃都不触发复制、generation 发布或第二套存档提交协议。

同一容器内 API 与 Worker 可以使用相同的非 root UID。Worker launcher 仍必须只接受经过验证的
SessionRoot、Worker binding 和 IPC 参数，不向 Worker 传递 Game workspace/current 路径。该应用级边界
不把“其他目录不可枚举”或“抵御恶意 Worker”作为安全保证。

## 6. 游戏包与 Game 内容设计

P1-04 的单一 workspace、逐文件清单、持久 operation、Validator、内容启用崩溃对账、授权和逻辑删除
细节见
[`tasks/P1-04-simple-game-library-plan.zh-CN.md`](tasks/P1-04-simple-game-library-plan.zh-CN.md)，
对应决策由
[`ADR-0010`](adr/0010-single-game-content-without-version-entities.md) 冻结。

### 6.1 上传流水线

P1-03 的 ZIP 子集、暂存预留、路径规范化、双阶段预算、一次性候选内容和故障恢复细节见
[`tasks/P1-03-secure-game-package-ingestion-plan.zh-CN.md`](tasks/P1-03-secure-game-package-ingestion-plan.zh-CN.md)，
相关决策由 [`ADR-0008`](adr/0008-secure-zip-ingestion-policy.md) 冻结。

```text
Upload → Quarantine → Archive scan → Safe extract → File scan
       → Encoding/case analysis → Game workspace
       → Runtime validation → Atomic activation of current content
```

1. API 流式接收上传内容到随机命名临时文件，同时计算 SHA-256 并执行压缩前大小限制；
2. 解包器逐项规范化路径，先校验后落盘；
3. 拒绝绝对路径、`..`、NUL、设备文件、FIFO、硬链接、游戏包携带的符号链接和大小写/Unicode 规范化冲突；
4. 同时限制条目数、单文件大小、总展开大小、目录深度和压缩比；
5. 对 ERB/CSV/配置文件检测 BOM，并以确定性顺序尝试 UTF-8 与 Shift-JIS；模糊结果产生诊断，不依赖系统 locale；
6. 静态扫描禁止能力、资源引用和文件名大小写；
7. 在受限验证 Worker 中执行解析验证，验证超时或超资源则失败；
8. 启用时计算规范清单摘要，将冻结 workspace 原子替换为 Game current content，改为只读，再在
   数据库事务中更新 Game 的摘要、清单和 `content_revision`。

若文件移动成功而数据库事务失败，后台清理器根据“数据库无引用且超过安全期”回收孤儿目录。不得在请求失败时立即递归删除尚未核对的路径。

### 6.2 Game 摄取 workspace 与只读查看

- current content 不可原地编辑；
- 上传新包时把安全展开结果写入唯一内部 workspace，并用它整体替换先前候选；
- 浏览器只可浏览目录、只读查看受支持文本和下载文件，不提供写文件、创建、重命名、删除或搜索 API；
- 编码转换只发生在已定义的摄取兼容步骤中，不提供用户交互式转换；
- 启用生成新的内容摘要和内部 revision；Session 只固定 `gameId + sourceContentDigest`，完整内容
  已复制到自己的 SessionRoot；
- Game 不保留用户可见历史版本、版本标签或回滚入口。

### 6.3 运行时清单

每个 Game current content 记录：

```json
{
  "schemaVersion": 1,
  "contentDigest": "sha256:...",
  "upstreamRuntimeCommit": "...",
  "cloudEmueraIntegrationVersion": "...",
  "compatibilityProfile": "v18-compatible",
  "textEncodings": { "ERB/A.ERB": "shift_jis" },
  "capabilities": ["text", "button", "image"],
  "blockedCapabilities": ["CALLSHARP"],
  "caseMapping": {},
  "diagnosticSummary": { "errors": 0, "warnings": 2 }
}
```

存在错误或 Blocked 能力时默认禁止启用；管理员只能对明确允许覆盖的诊断项作有审计的例外，不能覆盖平台级禁止项。

## 7. Session 生命周期详细流程

### 7.1 创建 Session

客户端必须发送 `Idempotency-Key`。处理流程：

1. API 验证用户、Game 可见性、ACTIVE/BLOCKED 状态、current content 摘要和兼容策略；
2. 在短事务中执行已实现的 SessionRoot 存储预算流程，插入 `CREATING` Session 和幂等记录；创建
   不占用活动 Worker 名额；
3. 按事务固定的 Game content revision/digest，把完整合法普通文件树复制到同父目录 staging；
4. 校验 manifest、文件类型、字节数和摘要后原子发布 SessionRoot，并持久化源 manifest 快照；
5. API 以 CAS 把 Session 置为 `CLOSED` 并返回资源。前端若要“一步开始”，在创建成功后另行调用
   open；两个操作使用不同幂等键和事务边界。

创建失败不得留下可启动的半成品 SessionRoot。故障恢复依据持久 operation 和目录 identity 完成
发布或安全清理，不能因 HTTP 超时重复创建 Session。

### 7.2 开启或重新开启 Session

开启接口要求独立的 `Idempotency-Key`：

1. API 验证 Session 授权、状态为 `CLOSED` 或 `CRASHED`、部署安全策略和 SessionRoot 绑定；
2. 对 `CRASHED` Session 先确认旧 Worker 已退出且失去 SessionRoot 写权限；
3. 在 `BEGIN IMMEDIATE` 短事务中检查实例级活动 Worker 上限，以 CAS 递增 epoch、写入绑定当前控制面实例的
   `STARTING` lease；
4. 事务提交后，Worker Manager 再次验证 SessionRoot 的目录 identity、owner 和 manifest marker，
   构造同 UID 非 root 子进程启动参数并启动 Worker；不得重新复制或合并 Game current content；
5. Worker 使用绑定控制面实例、Session、Worker 和 epoch 的启动令牌注册，加载 Runtime，发送
   `worker.ready`；
6. Worker Manager 验证实例身份和 epoch，并以 CAS 将 Session 更新为 `RUNNING`；浏览器连接数不
   参与该状态转换；
7. 若同步等待超时，API 返回 `202`，客户端查询状态；相同幂等键不得再启动一个 Worker。

启动失败将 Session 置为 `CRASHED`，记录阶段化错误码并释放活动 Worker 名额。重新开启只冷启动 Emuera；
用户通过游戏原生菜单加载 SessionRoot 中的存档，不提供指令级内存恢复。

### 7.3 关闭 Session

关闭接口同样要求幂等键：

1. API 以条件更新把非终态改为 `STOPPING`；
2. Realtime Gateway 立即拒绝新输入，并广播 `session.stopping`；
3. Worker Manager 发送 `worker.stop(graceDeadline, finalAutosavePolicy)`；
4. Worker 停止创建新 prompt，等待当前安全点，刷新存档，发送最终事件并退出；
5. 超过宽限期后 Worker Manager 先发送终止信号，再在硬超时后强制结束；
6. Worker Manager 确认进程退出、失效租约，把 Session 置为 `CLOSED` 并释放活动 Worker 名额；存档已经位于持久 SessionRoot，无退出提交阶段；
7. API 广播静止状态并关闭该 Session 的 WebSocket 订阅。

重复关闭 `CLOSED` Session 返回原状态。关闭只结束 Worker 和活动 Worker 名额，不删除、重建、提交或回滚
SessionRoot；之后可通过 open 再次启动同一 Session。用户关闭、管理员终止、实例容量/数据故障和控制面
退出使用不同 `closeReason`，其中异常结束进入 `CRASHED`。

### 7.4 心跳与崩溃判定

Worker 以配置间隔发送单调时钟产生的心跳，其中包含当前序号和 prompt 状态。API Worker Manager
收到后更新有界内存视图，并按较低频率用短事务批量持久化，避免每次心跳写 SQLite。MVP 不要求
采集 CPU、RSS、FD、磁盘或输出速率时序指标。

只有 API Worker Manager 可以根据子进程退出或心跳截止时间判定 `CRASHED`。判定时必须再次检查
`controlPlaneInstanceId + sessionId + workerId + epoch + stateVersion`，防止旧 Worker 或迟到回调
把新 Worker 标记为崩溃。

进入 `CRASHED` 后不自动重启，避免损坏游戏或存档形成崩溃循环。旧 Worker 退出屏障完成后，
用户可以显式 open 同一 Session；新的 epoch fencing 旧消息，并继续使用原 SessionRoot。

## 8. 实时显示、重连与输入

### 8.1 显示模型

P1-07 的详细状态拓扑、能力矩阵、输入/计时语义、IPC 升级和验收切片见
[`tasks/P1-07-emuera-structured-runtime-plan.zh-CN.md`](tasks/P1-07-emuera-structured-runtime-plan.zh-CN.md)。
Worker 输出的是结构化操作，不是 HTML 字符串。下列能力是最小公共词汇；固定上游中可由浏览器
安全表达的行更新、HTML Island、Shape/CBG、字体布局、动画和媒体语义也必须按 P1-07 的封闭状态
模型表达，不能作为 Phase 2 扩展推迟：

```text
TextRun(text, foreground, background, fontStyle)
LineBreak
Button(labelNodes, value, tooltip, enabled)
Image(assetId, sourceRect, size, altText)
Sprite(assetId, frame, position, zIndex)
Background(assetId, mode)
Audio(action, assetId, channel, volume, loop)
Prompt(promptId, inputType, timeoutAt, defaultValue, constraints)
Clear(scope)
```

所有颜色、尺寸、枚举、文本长度、资源 ID 和层级深度在 Worker 与浏览器两端校验。资源只使用由
Session 创建时保存的 Game runtime manifest 解析出的 `assetId`，不能使用游戏提供的任意 URL。

### 8.2 序号与快照

P1-08 的 API mirror、订阅竞态、逐连接双预算队列与降级细节见
[`tasks/P1-08-complete-snapshot-reconnect-bounded-output-plan.zh-CN.md`](tasks/P1-08-complete-snapshot-reconnect-bounded-output-plan.zh-CN.md)，
待评审决策见 [`ADR-0020`](adr/0020-api-snapshot-mirror-and-bounded-realtime-output.md)。API 对每个活动
Worker 只维护一份最新完整状态，不保留可按客户端游标补发的生产历史；控制面 wait probe 不复制
`DisplayBatch`，其余事件也受消息数和字节预算限制。

- 每个被接受的显示操作或原子批次分配一个严格递增的 `sequence`；
- Snapshot 表示应用完 `snapshotSequence` 后的完整有界显示树和当前 prompt；
- 实时发送队列仅保存尚未发送的有界批次；溢出时以较新的 Snapshot 替代，不维护历史增量窗口；
- 快照 JSON 按需惰性编码：批次发布只失效编码缓存，首个订阅或 resync 需要时编码并缓存；无浏览器
  连接的 Session 持续输出不产生全量编码成本；
- sequence 使用 64 位整数，数据库仅保存观测值，不参与热路径分配；
- Worker 重启必须使用新 epoch；客户端以最新完整 `(epoch, snapshotSequence)` Snapshot 替换本地显示状态。

### 8.3 完整快照恢复算法

Realtime Gateway 对一个 Session 执行以下恢复：

1. 验证 WebSocket 身份和 Session 权限；
2. 从 API 为当前 Worker epoch 维护的不可变镜像读取 `Snapshot(N)`，其中包含当前 prompt；
3. 若 Hub 尚未取得首个 Snapshot，返回 `SNAPSHOT_NOT_READY`，客户端带抖动退避后重新 resume；否则注册
   有界连接队列并再次比较当前 epoch/sequence；若已经前进则直接标记需要重新同步；
4. Gateway 发送 Snapshot，客户端以其完整替换本地显示树；
5. Gateway 从序号大于 `N` 的下一批实时事件开始转发；
6. 若读取与转发衔接期间检测到序号缺口或连接队列溢出，放弃待发增量并读取较新的完整 Snapshot。

MVP 不接受 `lastSequence` 作为历史补发承诺，也不维护 ack 驱动的重放日志。恢复正确性只要求客户端
最终取得一个内部一致的完整 Snapshot，并且在发现缺口时确定性地重新同步。

### 8.4 慢客户端策略

每个连接有字节数和事件数双重上限：

- 优先批量发送连续小事件；
- 队列达到软上限时通知客户端 `resync.required`；
- 达到硬上限时丢弃该连接的待发批次，发送最新快照；
- 多次无法消费快照时以策略错误关闭 WebSocket，但不关闭 Session；
- 任一客户端都不能反向阻塞 Worker 或其他客户端。

### 8.5 输入一致性

Worker 在生成输入请求时创建不可重复的 `promptId`。输入命令必须带：

```text
sessionId, workerEpoch, promptId, clientMessageId, value
```

处理顺序：鉴权 → Session 状态 → epoch → 输入格式 → 去重键 → 当前 prompt → 原子抢占 prompt。Worker 在一个串行化输入协调器内完成最后三步，并缓存该 `clientMessageId` 的确定结果。

可能结果：

| 结果 | 含义 |
| --- | --- |
| ACCEPTED | 本消息首次赢得当前 prompt |
| DUPLICATE | 同一消息已处理，附原结果 |
| STALE_PROMPT | prompt 已过期或已被其他客户端回答 |
| INVALID_FORMAT | 值不符合 prompt 约束 |
| SESSION_NOT_ACCEPTING_INPUT | Session 正在停止或不是活动状态 |
| STALE_EPOCH | 客户端连接的是旧 Worker |
| FORBIDDEN | 当前用户没有控制权限 |

去重缓存只采用当前 Worker 内的有界内存 LRU，不写入 Session metadata，也不跨 Worker 重启保留。
即使缓存淘汰，已经完成的 prompt 也会因 `promptId` 不再当前而拒绝再次执行。

### 8.6 WebSocket 消息封装

所有消息使用统一信封：

```json
{
  "protocolVersion": 1,
  "type": "display.batch",
  "messageId": "msg_...",
  "sessionId": "sess_...",
  "workerEpoch": 4,
  "sequence": 1052,
  "correlationId": "msg_...",
  "payload": {}
}
```

未知必需消息类型返回协议错误；未知可选字段忽略。握手时双方交换支持的协议版本和显示能力。服务端不得因为客户端声明不支持某能力就把不安全原始内容透传给客户端。

客户端 `messageId` 在一个连接内使用最近 4096 个 ID 的有界重复检测窗口；窗口淘汰后的旧 ID 不提供永久重放
保护，客户端仍必须在整个连接生命周期内不复用已发送 ID。

P1-09 已按 [`ADR-0021`](adr/0021-freeze-realtime-websocket-v1.md) 冻结正式边界：入口为
`GET /api/v1/realtime`，协商 `cloudemuera.realtime.v1`，每次 `session.resume` 都重新捕获当前
`(workerEpoch, snapshotSequence)` 并先发送完整 `session.snapshot`；不接受 `lastSequence` 历史补发，
`resync.required` 与替换 Snapshot 由单 writer 作为一个 work group 发送。`session.input` 必须带
`workerEpoch + promptId + clientMessageId`，浏览器不得发送 `SYSTEM`，API 通过共享
`ISessionCommandGate` 和持久 binding 校验后使用 IPC correlation 等待 Worker 回执；prompt、格式、去重和
timeout 仍由 Worker 决定。连接、订阅、接收消息、控制队列、pending input 和最终 envelope 都受数量/字节
双上限，连接断开不改变 Session 或 Worker 生命周期。

## 9. HTTP API 设计

### 9.1 通用约定

- 基础路径：`/api/v1`；
- JSON 字段使用 `camelCase`，枚举值使用大写下划线；
- 创建、开启、关闭、Game 内容启用、导入等可重试写操作要求 `Idempotency-Key`；
- 乐观更新使用 `If-Match: "<stateVersion>"`；
- 列表使用稳定游标分页，不使用不稳定的大偏移分页；
- 错误体统一包含 `code`、`message`、`requestId`、可选 `details`；
- 资源不存在和无权访问默认都返回 `404`，降低资源枚举风险；
- 大文件上传下载采用流式处理，不整体载入内存。

### 9.2 主要端点

| 方法与路径 | 用途 | 权限/说明 |
| --- | --- | --- |
| `POST /auth/login` | 以 email/password 本地登录 | 不接受 username；速率限制、防暴力破解 |
| `POST /auth/logout` | 注销当前会话 | 使服务端会话失效 |
| `GET /games` | 列出可见游戏 | 私有所有者或共享游戏 |
| `POST /games` | 创建 Game | 玩家 |
| `PUT /games/{id}/package` | 上传/替换内部摄取 workspace | 所有者，流式、幂等 |
| `GET /games/{id}/files` | 浏览 workspace 或 current | 授权后访问 |
| `GET /games/{id}/file?path=...` | 只读查看或下载 workspace/current 文件 | 严格规范化路径 |
| `POST /games/{id}:validate` | 验证 workspace | 返回持久 operation |
| `POST /games/{id}:activate` | 原子启用 workspace 为 current | 幂等、要求无阻断错误 |
| `GET /sessions` | 列出自己的 Session | 支持状态过滤 |
| `POST /sessions` | 创建持久 SessionRoot | 幂等、预留存储预算，成功后为 `CLOSED` |
| `GET /sessions/{id}` | Session 详情 | 所有者/管理员 |
| `POST /sessions/{id}:open` | 启动或重新启动 Worker | 幂等、`CLOSED/CRASHED`、检查实例级 Worker 上限、递增 epoch |
| `POST /sessions/{id}:close` | 优雅关闭 Worker | 幂等，不删除 SessionRoot |
| `GET /sessions/{id}/saves` | 列出存档 | 所有者 |
| `PUT /sessions/{id}/saves/{path}` | 导入/替换原生存档 | Session 停止、严格路径校验 |
| `GET /sessions/{id}/saves/{path}` | 下载原生存档 | 所有者、流式响应 |
| `PATCH /sessions/{id}/saves/{path}` | 重命名 | Session 停止、目标名校验 |
| `DELETE /sessions/{id}/saves/{path}` | 删除 | Session 停止、要求显式确认 |
| `GET /admin/workers` | Worker 基本状态、PID、心跳和最近错误 | 管理员 |
| `POST /admin/sessions/{id}:force-stop` | 强制停止 | 管理员，必须填写原因并审计 |
| `GET /health/live` | 进程存活 | 不检查昂贵依赖 |
| `GET /health/ready` | 接流量能力 | DB、数据目录、Worker Manager、启动对账 |
| `GET /version` | 构建/运行时版本 | 不暴露敏感路径 |

WebSocket 入口为 `GET /api/v1/realtime`。连接建立后通过 `session.resume` 订阅一个或多个已授权 Session；每次订阅和恢复都重新鉴权。

## 10. API—Worker IPC

### 10.1 传输与身份

IPC 默认使用 Unix domain socket，协议定义独立于传输。socket 文件权限限制到容器服务 UID；
Worker 使用一次性启动令牌注册，令牌绑定 Session、Worker、epoch 和过期时间。MVP 不增加独立的
跨实例服务身份挑战协议。

禁止把 UDS 暴露到容器端口或共享宿主目录。对敏感命令同时验证对端身份和消息字段，不把“能连接 socket”视为完整授权。

P0-06 的具体契约位于 [`src/CloudEmuera.Ipc/Protos/worker.proto`](../src/CloudEmuera.Ipc/Protos/worker.proto)，
当前 `protocolVersion=1`。注册同时校验 IPC 版本、`RuntimeBaseline.CloudEmueraIntegrationVersion`
和固定 upstream commit；API Worker Manager 以 256-bit bootstrap token 和完整 binding 校验 Worker，
Worker 通过 `SocketsHttpHandler.ConnectCallback` 只连接 UDS，不提供 TCP fallback。bootstrap
目录/文件权限为 `0700`/`0600`，文件拒绝链接、特殊文件、异常 hardlink 和非当前服务账户所有者。

### 10.2 控制消息

所有控制命令包含：

```text
protocolVersion, commandId, issuedAt, deadline,
sessionId, workerId?, workerEpoch, type, payload
```

主要命令/事件：

- `worker.start` / `worker.start.accepted` / `worker.ready`；
- `worker.stop` / `worker.stopped`；
- `worker.forceStop`；
- `worker.register` / `worker.registered`；
- `worker.heartbeat`；
- `session.resume` / `session.snapshot` / `display.batch`；
- `session.input` / `session.input.result`；
- `worker.fenced` / `worker.crashed`。

接收者按 `commandId` 去重，并在短期结果表中返回原结果。超过 `deadline` 的未执行命令拒绝；已经执行完成的命令仍可返回缓存结果。

### 10.3 背压

控制消息和显示数据使用独立逻辑通道或优先级队列，心跳、停止和 fencing 不能被大量显示事件饿死。每条消息有最大尺寸；大型静态资源不经过实时通道。存档由授权文件 API 在 Session 停止时直接流式访问。

## 11. 存档设计

P1-10 的 HTTP 契约、逻辑路径允许列表、基本格式嗅探、停止态 mutation lease、持久 operation、
dirfd/fsync 文件算法、崩溃恢复矩阵和验收切片见
[`tasks/P1-10-session-native-save-file-api-plan.zh-CN.md`](tasks/P1-10-session-native-save-file-api-plan.zh-CN.md)。

### 11.1 原生语义与物理隔离

Runtime 只调用 Emuera 原生序列化与反序列化逻辑。SessionRoot 就是存档的持久工作目录，不存在运行目录之外的 SaveArtifact 或逐次保存 generation：

- `UseSaveFolder:NO`：Emuera 直接读写 `SessionRoot/save*.sav` 和 `SessionRoot/global.sav`；
- `UseSaveFolder:YES`：Emuera 直接读写 `SessionRoot/sav/*`。

布局由 Session 创建时复制内容中的 `emuera.config` 决定，不由用户、API 或 Worker 启动参数另行
选择。宿主可把检测结果记录在 Session 的 manifest 快照中用于提前校验，但若它与运行时实际读取
的 `UseSaveFolder` 不一致，Worker 必须拒绝启动，不能同时搜索两处或改写配置。

`global.sav` 只在一个 Session 内是“全局”。每个 SessionRoot 都是独立的普通目录，因此两个用户或同一用户的两个 Session 不共享 `global.sav`、slot 文件、目录 inode 或可写父目录。

### 11.2 生命周期与直接读写

创建 Session 时建立空的持久 SessionRoot。此后：

1. API Worker Manager 把该目录的规范绝对路径交给唯一 Worker；
2. Emuera 在该目录中按原生行为直接打开、覆盖、读取或删除存档；
3. Worker 正常退出或崩溃后保留目录原状；
4. 同一 Session 再次启动 Worker 时复用原目录，不执行启动物化或退出提交；
5. 删除 Session 时才按明确产品策略删除或归档整个目录。

CloudEmuera 不包装每次原生保存，不改变上游 `FileMode`、flush、覆盖和失败语义。如果进程在 Emuera 写文件期间被终止，磁盘内容以文件系统和上游当时状态为准；系统不得把这描述成已提供事务性 generation。需要恢复历史时，对静止的整个 `/data` 或 SessionRoot 做宿主机快照/备份。

### 11.3 管理操作

活动 Session 的原生文件由 Worker 独占，API 不与其并发修改。列出和下载可以在明确接受非事务性读取语义后开放；MVP 的上传、替换、重命名和删除只允许 Session 处于 `CLOSED` 或其他无 Worker 状态，并在执行前后复核租约不存在。Session 间直接传输存档不提供专用 API。

管理操作只处理允许列表内的 `save*.sav`、`global.sav` 以及当前布局允许的 `sav/` 条目：

- 下载：每次验证 Session 所有权，使用安全下载名并流式读取当前文件；
- 上传/替换：先取得并持续续租停止态 mutation lease，再写入当前 SessionRoot 外的隔离暂存文件；验证大小、目标名和基本原生格式，记录发布前目标 identity，发布前再次核对后才移动到目标路径；不尝试证明其与 Game 摘要语义兼容，也不形成历史 generation；
- 重命名/删除：要求显式确认、再次检查无活动 Worker，并写审计日志；
- 自动保存：完全由游戏和 Emuera 控制；CloudEmuera 不另建自动存档调度器；
- 备份：面向整个挂载数据目录或 SessionRoot，由运维备份策略负责。

P1-10 已将上述操作接入 `/api/v1/sessions/{id}/saves`。公开路径只使用逻辑 save root，相对于
`emuera.config` 决定的布局，不接受物理 `sav/` 前缀。写操作以 `save_file_operations` 持久记录
`PREPARED/STAGED/PUBLISHED/COMMITTED/FAILED`，使用 `metadata/save-operations/{operationId}` marker、
停止态 mutation lease（覆盖暂存、校验和发布并持续续租）和按 Session 隔离的幂等回放；普通请求不会
回收过期 lease，恢复器只能在核对 marker、目标发布前 identity 和文件事实后清理或收敛操作；终态记录
至少保留幂等窗口，过期后由 recovery reaper 在确认 staging/lease 已清理后回收。启动恢复完成前，写存档
API 和 readiness 均保持关闭。该 operation 表只记录 API 管理命令，不记录 Emuera
直接产生的每次原生保存。

若发现没有对应 operation 的遗留 mutation lease（包括历史 `mut_*`），恢复器会 fail closed，不按
`expires_at` 静默删除。离线修复必须先停 API/Worker、备份 SQLite 与对应 SessionRoot，核对旧 Worker
已退出并证明没有未完成文件事实；`sfop_*` 保留 operation/staging 现场交由恢复矩阵处理，只有确认是
无文件事实的历史 `mut_*` 才能在停机维护事务中删除并记录审计。P1-10 不提供在线强制释放入口，
无法证明时保持 `SAVE_OPERATION_RECOVERY_REQUIRED`。

## 12. 安全设计

### 12.1 信任边界

不安全数据包括游戏包、文件名、游戏生成的显示内容、存档、浏览器输入和客户端游标，必须防御
损坏格式、路径逃逸和浏览器注入。部署者及已认证参与者被假定不会刻意提交并运行恶意 ERB 来攻击
同实例其他用户；API 资源授权仍逐操作执行，但 Worker 执行面不是敌对租户隔离边界。

### 12.2 Worker 基础进程边界

MVP 控制为：

- 整个生产容器以非 root UID 运行，API 与 Worker 可以使用相同 UID；
- Worker 保持每 Session 一个独立子进程，只接收自己的 SessionRoot、binding 和私有 UDS 信息；
- 容器不得挂载 Docker socket、宿主密钥或无关宿主目录；
- Snapshot、IPC/WebSocket 队列、ZIP 展开和 DataRoot 空间使用具有实例级上限；
- 部署者可在 Docker 层为整个容器配置 CPU、内存和 PID 上限；
- close、API 停止或控制通道断开都必须在期限内结束 Worker，必要时使用进程组强制终止。

MVP 不承诺面向敌对租户的内核级 Worker 隔离，也不因缺少额外内核隔离能力拒绝 readiness。因此同 UID
Worker 从内核视角可能读取 DataRoot 内其他资源；实例不得面向敌对租户开放。未来改变信任模型时必须
以新 ADR 恢复强制隔离和相应测试。

### 12.3 Web 安全

- 生产环境仅使用 HTTPS，Cookie 设置 `Secure`、`HttpOnly`、合适的 `SameSite`；
- Cookie 认证的写操作使用 CSRF 防护；WebSocket 校验 Origin 和登录态；
- CSP 默认禁止脚本来源扩张、对象嵌入和任意媒体 URL；
- Emuera HTML 先解析为内部节点，再由前端组件生成 DOM；禁止 `innerHTML` 直灌；
- 文本、tooltip、文件名和错误详情按上下文编码；
- 资源响应使用清单中声明的 MIME、`nosniff` 和下载/内联白名单；
- 登录、上传、创建 Session 和输入实施轻量实例级速率限制。

全新实例不开放 `/setup` 或注册 API。仅当持久状态为 BOOTSTRAP_REQUIRED 时，API 从 `.env`
读取 `CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME`、`CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL` 和
`CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD`，在一个 SQLite 写事务内提交管理员、现有默认 profile、完成
标记和审计；并发启动最多一个成功。管理员使用 email 登录且首次登录强制改密。COMPLETED 后
不再读取或验证这些变量；“没有 ACTIVE ADMIN”不是重新 bootstrap 的条件。

### 12.4 路径安全

所有用户路径先按 `/` 解析为逻辑相对路径，拒绝空字节、绝对路径、盘符、父级段和平台保留名。最终访问使用基于目录句柄的逐段打开，并禁止跟随符号链接；只做字符串前缀比较不构成安全校验。SessionRoot 复制过程没有链接例外，遇到任何链接或特殊文件都失败。文件名同时执行 Unicode 规范化和大小写碰撞检查。

Session 原生存档 API 复用 `EmueraSavePathPolicy`：根目录布局只允许 `global.sav`/`saveN.sav`，
`sav/` 布局允许安全目录段以及原生 save/global/辅助文件名；API 不把物理 `sav/` 目录暴露为前缀。
Linux 实现使用 protected dirfd、`O_NOFOLLOW`、固定 inode/owner/mode、`renameat2(RENAME_NOREPLACE)`、
checked `unlinkat` 和文件/目录 `fsync`，不把用户路径拼接为授权依据。

### 12.5 密钥与隐私

密码只保存现代自适应哈希；服务凭据从容器 secret 或仅服务身份可读文件加载，不写入数据库、日志或 runtime manifest。默认日志只记录输入类型、长度和结果，不记录输入全文。崩溃报告在落盘前移除 Cookie、Authorization、启动令牌及用户输入缓冲。

## 13. 可观测性与运维

### 13.1 日志

采用结构化日志，公共字段包括：

```text
timestamp, level, service, eventName, requestId,
sessionId?, workerId?, workerEpoch?, userIdHash?, durationMs?, result
```

API 请求、Session 创建/连接/关闭、Worker 注册/崩溃、Game 内容启用和存档删除都有稳定事件名。
日志按大小和时间轮转，保留策略可配置。

### 13.2 基本诊断

管理员接口只需按需返回 Session 状态、Worker ID/PID/epoch、最近心跳、当前 prompt、Snapshot 大小、
队列是否发生溢出以及最近错误。上传拒绝、兼容诊断和存档失败通过稳定错误码与结构化日志查询。
MVP 不提供专用遥测平台、进程级 CPU/RSS/FD/磁盘时序指标或容量规划面板。

### 13.3 健康检查

- liveness：当前 API 事件循环可响应；
- readiness：数据库可读写、`/data` 可写且空间高于安全阈值、Worker IPC 可用、启动对账完成、迁移完成；
- Worker Manager 健康：本地 IPC 可用、进程监视和启动对账循环在运行；无法回收的旧 Worker 只使对应 Session 不可 open/修改存档；
- version：Web/API、Worker 协议、上游 Runtime commit、源码集成版本、schema 版本。

单个 Worker 崩溃不应使整个 API liveness 失败，但会反映在 Session 状态和最近错误中。

### 13.4 审计

保留已实现的身份、关键资源变更、管理员强停、Game 内容启用和存档删除审计。管理员强停必须包含
原因；审计元数据不能包含密码、令牌或输入全文。MVP 不建设通用审计浏览，也不要求普通连接和
只读请求全部进入审计表。

## 14. 故障恢复与对账

### 14.1 API 重启

API 每次启动生成新的 `controlPlaneInstanceId`，从 SQLite 读取活动 Session 和 lease，并检查上一实例
记录的 PID/start identity 或进程组：

- 旧 Worker 已退出：以 CAS 失效 lease、释放活动 Worker 名额并把 Session 置为 `CRASHED`；
- 旧 Worker 仍存在：先 fence、终止并等待退出，再执行上述状态更新；
- 无法确认身份、终止失败或无法证明 SessionRoot 写权限已经释放：保持 lease，阻止该 Session 的
  open 和存档写操作；不因单个 Session 异常使整个实例 readiness 失败；
- DB 已 `CLOSED/CRASHED` 但发现旧 Worker：停止并审计异常，确认退出前不得授予新写租约。

新 API 不接受上一实例的 Worker 注册，也不恢复旧 Runtime 路由。完成故障对账后，用户可以显式
open 同一 `CRASHED` Session；新 Worker 使用更大的 epoch 和原 SessionRoot 冷启动。

### 14.2 API—Worker 控制通道中断

Worker 的控制通道断开后立即停止接受新运行时工作并开始有界退出，不恢复旧控制流；
parent-death signal 或专属进程组作为异常清理兜底。该机制不提供跨 API 实例接管。

### 14.3 数据目录故障

发现 `/data` 不可写、空间低于保留阈值或 SQLite 出现完整性错误时：

- readiness 失败；
- 禁止新建 Session、启用 Game 内容和通过 API 修改存档；
- 已运行 Worker 可继续到安全点，但任何持久化失败都必须明确反馈，不能声称保存成功；
- 管理员获得高优先级告警；
- 不自动删除用户数据来尝试恢复空间。

### 14.4 容器/宿主重启

MVP 不恢复指令级内存状态。重启后所有先前活动 Session 在对账后标记 `CRASHED`，其 SessionRoot
原样保留；旧 Worker 写权限释放后，用户可以重新开启同一 Session，并通过 Emuera 原生功能加载
其中的存档。不得要求为继续该进度创建新 Session，也不得把冷启动显示成从中断指令恢复。

## 15. 前端设计

前端按领域分为游戏库/包检查与只读查看、Session 列表、游戏控制台、存档管理和基本管理员页面。

游戏控制台维护：

```text
connectionState
sessionId + workerEpoch
snapshotSequence + lastAppliedSequence
bounded rendered model
currentPrompt
pending clientMessageId → result
```

渲染器只接收已验证的内部节点。按键和按钮最终都生成同一种 `session.input` 消息；发送后保持待定状态，直到收到确定结果。重连时不凭本地 DOM 推断权威状态，而是用服务端完整快照替换本地显示树。

移动端需处理软键盘造成的 visual viewport 改变、安全区域、触摸目标尺寸和自动滚动；自动滚动仅在用户已接近底部时发生，避免打断查看历史。所有按钮可通过键盘聚焦，颜色不是唯一状态提示，图片具有可用的替代文本或装饰标识。

## 16. 配置设计

配置来源优先级：内置安全默认值 < 配置文件 < 环境变量。敏感项不允许通过会被 `/version` 或日志输出的普通配置展示。

配置组包括：

- 身份提供商、email-only 登录、会话 Cookie、仅首次读取的 bootstrap 管理员 username/email/
  password，以及允许的 WebSocket Origin；
- 数据路径、数据库参数和备份窗口；P1-01 的数据库 CLI 与布局如下：

  ```bash
  CloudEmuera.Migrator migrate --data-root <path> [--database cloudemuera.db]
  CloudEmuera.Migrator check --data-root <path> [--database cloudemuera.db]
  ```

  `migrate` 独占 `<database>.migration.lock`，仅在存在待执行 migration 时把 SQLite
  Online Backup 写入 `<data-root>/backups/`，然后执行全部编译进程的 migration；`check`
  只读验证 migration history、foreign-key check 和 quick check。退出码 `0/10/11/12/13/14/15`
  分别表示成功、配置非法、锁竞争、数据库高于当前 binary、备份失败、migration 失败和完整性
  检查失败。业务 API 和 Worker 不调用 `Database.Migrate()`。
- `CloudEmuera:Capacity:*` 实例级容量：活动 Worker、游戏包/SessionRoot/暂存字节、单个存档文件和 DataRoot 最低
  剩余空间；历史 `CloudEmuera:MinDataRootFreeBytes` 仅兼容读取一个周期并输出弃用 warning，
  新部署使用带 `Capacity` 前缀的键；`CloudEmuera:Capacity:MaxSaveFileBytes` 默认 64 MiB，且不得超过
  `MaxSessionRootBytes`；
- 上传/解压/文件数量/编码限制；
- 实例级最大活动 Worker 数、上传/展开/文件数量、存档文件和 DataRoot 最低剩余空间上限；
- Snapshot、IPC 和 WebSocket 队列上限；容器整体 CPU/内存/PID 限制属于部署选项；
- 心跳、启动、停止、强制终止超时；
- 存档文件大小、停止态管理操作和 SessionRoot 备份保留；
- Runtime 基线、兼容配置和禁止能力；
- 日志级别、轮转和已实现审计保留。

人工开发使用仓库根 `.env` 与 `./data`。自动化身份测试必须生成独立临时 env/DataRoot，使用唯一
Compose project 和动态端口，并通过 `--env-file` 与 API service 的显式 environment mapping 传入
bootstrap 配置；不得读取、修改或清理人工 `.env`、`./data` 和开发容器。所有示例均使用
`CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password`，应用不提供密码文件替代协议。

启动时完成配置模式校验；不安全组合或单位错误直接失败，并给出不含密钥的明确诊断。

## 17. 测试策略

### 17.1 测试层级

| 层级 | 重点 |
| --- | --- |
| 领域单元测试 | 状态机、授权、实例级 Worker 上限、幂等、epoch、输入抢占 |
| Runtime 兼容测试 | v18 与当前 EM+EE 的解析、变量、输入、存档和命令 |
| 协议契约测试 | HTTP、WebSocket、IPC 版本和未知字段兼容 |
| 文件系统安全测试 | 穿越、链接、Unicode/大小写碰撞、TOCTOU、压缩炸弹 |
| 组件集成测试 | SQLite + 文件系统 + API Worker Manager + Worker |
| 浏览器测试 | 重连、输入、移动视口、键盘与可访问性 |
| 故障注入 | API/Worker 退出、IPC 中断、孤儿回收失败、磁盘满、慢客户端 |
| 容量测试 | 代表性游戏手工/自动负载、持续 PRINT、快照恢复与有界队列 |
| 视觉回归 | 字体测量、换行、按钮命中、图片和 Sprite |

### 17.2 必测并发性质

- 相同幂等键并发创建只产生一个 Session；
- 实例级活动 Worker 只剩一个名额时，并发 open 最多一个成功；
- 同一 `CLOSED/CRASHED` Session 并发 open 最多产生一个有效 Worker；
- close/crash 后 open 保持同一 SessionRoot identity、源摘要与 manifest，只增加 epoch；
- 两个客户端回答同一 prompt 只有一个 `ACCEPTED`；
- Worker A epoch 过期后，其心跳、输出和输入结果均被拒绝，且它不能获得新 Worker 的 SessionRoot 写权限；
- Snapshot 生成期间持续输出，客户端要么连续应用后续批次，要么检测缺口并重新取得完整 Snapshot；
- 关闭和输入并发时，结果要么输入先被接受，要么明确被停止状态拒绝；
- Worker 写存档时退出后，系统保留 SessionRoot 现场且不产生虚假的“已提交”状态；其他 Session 不受影响。

### 17.3 需求验收映射

| 验收项 | 主要设计落点 | 核心测试 |
| --- | --- | --- |
| AC-001/004/014 | 独立 Worker、SessionRoot、授权 | 双用户双 Session 隔离测试 |
| AC-002/003 | 有界快照、API 生命周期绑定、持久 SessionRoot | 断网、API 重启和同 Session 重开测试 |
| AC-005 | promptId/clientMessageId | 重复与并发输入测试 |
| AC-006/007 | 关闭流程、心跳、崩溃判定和可重开状态 | 超时关闭、kill Worker、同 Session 重开 |
| AC-008/009 | 双兼容测试集、运行时清单 | 基准游戏回归 |
| AC-010 | 安全解包与路径访问 | 恶意归档语料库 |
| AC-011 | 响应式控制台与授权下载 | 桌面/移动浏览器矩阵 |
| AC-012 | 有界队列、快照降级 | 持续输出和慢客户端压力测试 |
| AC-013 | 持久 SessionRoot、原生直接读写 | 两种原生存档布局与重启加载测试 |

完整需求追踪在实现任务中以需求编号标注测试用例；每个 `AUTH/GAME/SESS/PLAY/SAVE/OPS/COMP/SEC/NFR` 条目至少关联一个设计组件和一个验证方法。

## 18. 交付分解

### 18.1 Phase 0：运行时切分

1. 固定上游 Runtime commit，直接导入源码并维护来源/修改记录；
2. 提取 `IGameConsole`、`RuntimePaths`、时钟、文件、图像和音频抽象；
3. 建立无 UI Worker，可运行到 INPUT 并接受输入；
4. 支持根目录和 `sav/` 两种原生存档；
5. 建立 v18 与当前 EM+EE 双测试集；
6. 证明一个进程只运行一个 Session，且不依赖 API 进程内状态。

### 18.2 Phase 1：端到端 MVP

1. SQLite schema、Game 摄取 workspace/内容启用和本地账户；
2. API Worker Manager、Worker IPC、epoch 和可反复开启的 Session 状态机；
3. 完整 Emuera 结构化 Console/Input/绘图/媒体、完整 Snapshot WebSocket 恢复和当前 Worker 内输入去重；
4. SessionRoot 隔离和停止态原生存档文件管理；
5. Web 游戏包检查/只读查看、Session 控制台、存档界面；
6. 管理员 Worker 基本查看与强制停止；
7. 单容器进程管理、健康检查、备份和升级说明；
8. 完成 AC-001 至 AC-014 自动化或可重复验收。

### 18.3 Phase 2 预留接口

已实现的审计和备份机制继续保留；不为资源指标平台、敌对租户隔离或多主机调度预留未使用协议。
未来挂起恢复
必须新增状态和显式能力协商，不能把“当前没有浏览器连接”偷换为不占 Worker 的“挂起”。

## 19. 待决设计记录

以下事项在进入对应实现前必须形成 ADR：

1. `ADR-0001`：从本地账户切换到单一 OIDC Provider 的触发条件和迁移方式；
2. `ADR-0017`：可信参与者自托管 MVP 的信任边界、容量、实时、存档和运维简化边界（已完成）；
3. `ADR-003`：ConsoleSnapshot 的序列化格式、大小上限和压缩策略；
4. `ADR-004`：Emuera HTML、Sprite、CBG 和音频的 MVP 允许列表；
5. `ADR-0008`：安全 ZIP 摄取边界、上传/展开配额和 staging 预算（已完成）；
6. `ADR-0009`：草稿发布事务与不可变版本身份（已被 ADR-0010 取代）；
7. `ADR-0010`：单一 Game 内容模型和旧 GameVersion schema 迁移（已完成）；
8. `ADR-0015`：API 直接管理 Session Worker 生命周期（已完成）；
9. `ADR-0016`：Session 是可反复开启的持久 SessionRoot（已完成）；
10. 待编号：实例级活动 Worker、上传/展开、存档文件、Snapshot/队列和最低剩余空间默认上限；
11. 待编号：合法可纳入 CI 的 v18 与 EM+EE 代表性游戏集；
12. 待编号：字体文件保留、服务和授权策略；
13. 待编号：SessionRoot 备份恢复点目标、保留期和升级回滚流程。
14. `ADR-0018`：Emuera 完整结构化交互状态、能力矩阵、计时语义和 IPC major 升级（P1-07 首个切片）。
15. `ADR-0020`：API 快照镜像、订阅竞态和逐连接有界输出（P1-08，已接受）。
16. `ADR-0021`：Realtime WebSocket v1、快照恢复与输入回执边界（P1-09，已接受）。

## 20. 设计完成定义

进入 MVP 编码前至少应满足：

- 本文档通过前端、API、Runtime、运维和安全联合评审；
- 所有待决项已指定负责人和截止阶段，不阻塞当前迭代；
- Session 状态转换、HTTP/WebSocket/IPC schema 形成机器可校验定义；
- SQLite 首版迁移和文件布局经过崩溃一致性评审；
- 非 root 生产容器、敏感挂载禁用、Worker 有界回收和实例级容量上限完成可重复验证；
- v18 与当前 EM+EE 最小兼容测试集可在 CI 运行；
- AC-001 至 AC-014 均有可执行的验收方案。
