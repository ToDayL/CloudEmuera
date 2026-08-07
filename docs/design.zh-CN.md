# CloudEmuera 详细设计

| 项目 | 内容 |
| --- | --- |
| 文档状态 | 草案 v0.2 |
| 日期 | 2026-08-03 |
| 输入文档 | [requirements.zh-CN.md](./requirements.zh-CN.md) v0.2 |
| 目标阶段 | Phase 0、Phase 1（MVP），并为 Phase 2 预留扩展点 |
| 目标读者 | 架构、前端、后端、运行时、运维、安全与测试开发者 |

## 1. 文档目的

本文档把需求文档中的产品要求和总体架构细化为可实施的软件设计，定义进程边界、模块职责、领域状态、持久化模型、文件布局、HTTP/WebSocket/IPC 协议、关键一致性算法、安全控制、故障处理和测试策略。

本文档不替代需求文档。若两者冲突，以带编号的需求为准，并通过设计评审修改本文档。本文中的“决定”表示 MVP 实现约束；“建议”表示可在不破坏接口语义的前提下调整；“待决”表示实现前仍需产品或技术评审确认。

## 2. 范围与设计假设

### 2.1 MVP 边界

MVP 由一个 Docker 容器承载，容器内包含以下独立进程：

- 一个 Web/API 进程；
- 一个 Worker Supervisor 进程；
- 每个活动 Session 一个 Session Worker 进程；
- 一个负责拉起和回收上述进程的 init/进程管理器。

系统只依赖一个挂载到 `/data` 的持久化目录，不依赖外部数据库、对象存储、消息队列或远程文件系统。SQLite 是权威元数据存储，文件系统是游戏内容、完整 SessionRoot、原生存档、日志和备份的权威内容存储。

### 2.2 暂定决策

需求文档第 17 节仍有未决项。为使接口设计可继续推进，本文采用以下可替换假设：

| 事项 | MVP 暂定方案 | 可替换边界 |
| --- | --- | --- |
| 身份认证 | 本地账户，使用安全的密码哈希和 HttpOnly Session Cookie | `IIdentityProvider` 可替换为单一 OIDC Provider |
| 多客户端输入 | 同一 `promptId` 的第一个有效输入生效 | 后续可在 Realtime Gateway 前增加控制权租约 |
| Session 空闲 | 断连不自动关闭，持续占用活动配额 | 管理员策略只能通过显式配置启用 |
| 存档删除 | 无活动 Worker 时显式确认后直接删除 | 历史恢复由 SessionRoot 外部备份提供 |
| HTML/媒体兼容 | 仅开放结构化允许列表中已测试的节点 | 能力由运行时清单和兼容性矩阵声明 |
| 跨服务器迁移 | MVP 不定义可移植整包格式 | 数据备份不依赖该格式 |

配额数值、测试游戏集、字体授权和具体媒体兼容等级不在本文中硬编码，统一从部署配置和运行时能力清单读取。

### 2.3 关键设计原则

1. **控制面与执行面分离**：API 管理身份、元数据和连接，Worker 独占解释器状态。
2. **一个活动 Session 一个进程**：隔离 Emuera 全局状态，并允许独立限制和终止资源。
3. **数据库记录意图，Supervisor 确认事实**：Session 状态是持久化事实；进程是否存活由 Supervisor 观测并回写。
4. **所有跨进程消息均可重试**：控制命令有 `commandId`，输入有 `clientMessageId`，状态变化有版本号。
5. **epoch fencing 优先于连接状态**：任何旧 Worker 即使恢复连接，也不能影响当前 Session。
6. **内容不可变，工作区私有**：GameVersion 发布后不可修改；每个 Session 只有自己的可写区域。
7. **显示数据结构化**：浏览器不执行游戏提供的 HTML、脚本或任意 URL。
8. **有界缓存**：输出历史、连接队列、日志和去重记录均有明确上限及降级方式。

### 2.4 技术选型结论

以下是 MVP 的主选技术栈。除标记为“按发布锁定”的工具外，主版本属于架构基线，不能在普通依赖升级中擅自切换。

| 组件 | 主选方案 | 版本基线 | 选择理由 |
| --- | --- | --- | --- |
| 服务端语言与运行时 | C# / .NET | .NET 10 LTS，使用当前安全补丁 | 与 Emuera Runtime 同语言，可直接复用解释器并共享领域/协议类型；LTS 支持至 2028-11-14 |
| Web/API | ASP.NET Core Minimal APIs + Kestrel | 10.x | 原生支持 WebSocket、认证授权、限流、健康检查、静态文件和 OpenAPI；单进程即可服务 API 与 SPA |
| 身份认证 | ASP.NET Core Identity + Cookie Authentication + Data Protection | 10.x | MVP 复用成熟的用户、密码、角色和安全戳能力；Data Protection key ring 持久化到 `/data`，保留替换为 OIDC 的认证端口 |
| API 描述 | ASP.NET Core OpenAPI + 仓库内 JSON Schema | 10.x / schema v1 | HTTP 契约由 OpenAPI 生成；WebSocket 负载用 JSON Schema 单独校验和生成 TypeScript 类型 |
| 浏览器实时通信 | ASP.NET Core 原生 WebSocket | RFC 6455，应用协议 v1 | CloudEmuera 需要自定义 epoch、sequence、snapshot、ack 和背压语义；不采用 SignalR 的 Hub/RPC 抽象 |
| WebSocket 编码 | UTF-8 JSON + `System.Text.Json` source generation | 应用协议 v1 | 易调试、浏览器零额外解码依赖；达到性能瓶颈后才通过新协议版本评估 MessagePack |
| 进程间通信 | gRPC 双向流 + Protocol Buffers over Unix Domain Socket | IPC 协议 v1 | 强类型、代码生成、截止时间和流式通信成熟；UDS 不暴露容器网络端口 |
| 元数据数据库 | SQLite | 3.x，随运行镜像锁定 | 符合单容器和本地备份要求，无外部服务；使用 WAL、外键和 busy timeout |
| 数据访问 | EF Core SQLite Provider；关键 CAS 使用参数化原生 SQL | EF Core 10.x | 普通 CRUD、关系和迁移成本低；状态机、配额预留和 epoch 更新用显式 SQL 保证条件更新可审查 |
| 数据库迁移 | 独立 `CloudEmuera.Migrator` 控制台程序 | 与应用同版本 | 容器初始化阶段执行并持有独占迁移锁；API/Supervisor 不在运行时自动迁移 |
| 后台任务 | ASP.NET Core `BackgroundService` + SQLite 持久任务表 | .NET 10 | 上传验证、孤儿清理等任务需要重启可恢复；不引入外部消息队列 |
| Runtime Host | C# Console Worker + 仓库内 Emuera.EM+EE 固定源码快照 | 清单锁定 commit 与 integration version | 保持原生解释器和存档格式；直接修改内置源码并通过适配层替换 WinForms/GDI+、路径、输入和显示 |
| 图像与字体兼容层 | SkiaSharp + HarfBuzzSharp | 按 Runtime manifest 锁定 | 替代仅限 Windows 的 GDI+ 测量和解码路径，提供 Linux 上可重复的字体 shaping、测量与图片元数据处理 |
| 游戏包格式 | MVP 仅接受 ZIP；使用 `System.IO.Compression` 安全逐项解包 | .NET 10 | 收窄攻击面且不增加原生解压依赖；7z/RAR 等格式需另行评审和协议声明 |
| 文本编码 | `System.Text.Encoding` + `System.Text.Encoding.CodePages` 严格解码器 | .NET 10 | 明确支持 UTF-8 BOM/无 BOM 与 Shift-JIS，并禁用依赖系统 locale 的隐式回退 |
| 容器进程管理 | s6-overlay v3 | 构建时锁定精确版本和 SHA-256 | 作为 PID 1 回收僵尸进程，并分别监督 API 与 Supervisor；Session Worker 仍只由 Supervisor 管理 |
| Worker 沙箱 | NsJail + Linux namespaces + cgroup v2 + seccomp-bpf + rlimit | NsJail 3.x，精确版本/摘要锁定 | 一个经过专门审计的 launcher 统一提供只读 bind mount、网络隔离和资源限制；封装在 `IWorkerSandbox`，首选 rootless user namespace |
| 前端语言和框架 | TypeScript + React | TypeScript 7.x、React 19.x | 适合复杂交互和长期状态界面，生态成熟；不采用 SSR，输出纯 SPA |
| 前端构建 | Vite + Node.js + pnpm | Vite 8.x、Node.js 24 LTS、pnpm 11.x | 仅在构建阶段使用 Node；生产镜像不包含 Node.js，依赖由 lockfile 固定 |
| 路由与服务端状态 | React Router + TanStack Query | 当前兼容主版本，lockfile 固定 | 路由与 HTTP 缓存/失效交给成熟库；游戏实时状态不进入 Query 缓存 |
| 游戏实时状态 | React `useSyncExternalStore` + 自研 Session Store | 内部接口 v1 | 高频增量需要按 sequence 串行应用和有界内存，避免通用全局状态库造成不必要重渲染 |
| 样式与无障碍 | CSS Modules + CSS Custom Properties + React Aria primitives | 按发布锁定 | 保持样式可控，以可访问交互原语实现焦点、键盘和触摸行为；不引入完整视觉组件框架 |
| ERB/CSV 编辑器 | Monaco Editor，按需加载 | 按发布锁定 | 提供大文本、搜索、编码提示和诊断标记；移动端降级为轻量文本编辑器/只读查看 |
| 显示渲染 | React DOM + Canvas 2D + Web Audio API | 浏览器标准 | 文本和按钮使用可访问 DOM；Sprite/背景层使用 Canvas；音频使用浏览器原生 API |
| 日志 | `Microsoft.Extensions.Logging` JSON Console | .NET 10 | 默认写 stdout/stderr 交给容器采集；可选写 `/data/logs` 轮转文件，不绑定专有日志服务 |
| 指标与追踪 | OpenTelemetry .NET + OTLP；Prometheus scrape endpoint 可选 | 按发布锁定 | traces、metrics、logs 使用同一标准模型；未配置外部 Collector 时系统仍可独立运行 |
| 后端测试 | xUnit + ASP.NET Core `WebApplicationFactory` | 与 .NET 10 兼容版本 | 单元、契约和 API 集成测试统一在 .NET 工具链运行 |
| 前端测试 | Vitest + Testing Library | 与 Vite 8 兼容版本 | 组件和状态归约器测试速度快，并复用 Vite 配置 |
| 端到端测试 | Playwright | 按发布锁定 | 同时覆盖 Chromium、Firefox、WebKit 和移动设备配置，匹配浏览器兼容需求 |
| 性能测试 | NBomber（内部/HTTP/WS）+ 自研代表性 ERB workload | 按发布锁定 | C# 场景便于注入 Session、输入和持续输出负载；运行时执行仍使用真实 Worker |
| 容器镜像 | Debian slim 系列的 .NET 10 ASP.NET Runtime 多阶段镜像 | digest 锁定 | 相比 Alpine/musl 对原生库、字体和诊断工具兼容性更稳；最终镜像不包含 SDK/Node |

### 2.5 选型边界与明确不选项

#### 2.5.1 SignalR 与原生 WebSocket

ASP.NET Core 官方通常建议业务应用优先考虑 SignalR，但 CloudEmuera 的实时协议本身就是领域核心，需要显式控制恢复屏障、序号连续性、快照替换、逐连接队列和输入回执。因此 MVP 选择原生 WebSocket，避免同时维护 SignalR 重连语义和 CloudEmuera 重连语义。若将来需要 SSE/long polling 降级，再以独立 ADR 评估 SignalR，不能改变现有应用层游标语义。

#### 2.5.2 EF Core 与关键状态更新

EF Core 用于身份、Game、普通查询和迁移；以下操作必须使用显式事务和参数化条件 SQL，并检查受影响行数：

- 活动配额检查与 `CREATING` Session 插入；
- `state_version` 比较交换；
- Worker epoch 递增和租约替换；
- 第一个 prompt 输入抢占；
- 停止态存档修改权与 Worker 租约的互斥抢占。

SQLite 不提供数据库自动生成的并发 token，故 `state_version` 由应用显式递增。SQLite Provider 的部分迁移需要重建表，因此生产升级必须先创建一致性备份，由 Migrator 单独执行，禁止多个业务进程自动调用 `Migrate()`。

#### 2.5.3 gRPC 与浏览器协议

gRPC/Protobuf 只用于容器内 API、Supervisor 和 Worker。浏览器不使用 gRPC-Web，因为浏览器通道需要双向、低延迟且具备自定义 snapshot/resume 语义；大型资源继续走 HTTP。`.proto` 文件是 IPC 的权威契约，生成的 C# 类型不能直接成为领域模型。

#### 2.5.4 前端状态划分

TanStack Query 只管理 HTTP 资源，例如游戏列表、Session 元数据和存档索引。游戏控制台的 snapshot、delta、prompt 和 ack 由每 Session 独立 Store 管理，按 `(workerEpoch, sequence)` 归约。两者不得复制保存同一份权威实时状态。

#### 2.5.5 不引入的基础设施

MVP 不采用 PostgreSQL、Redis、Kafka/RabbitMQ、对象存储、Kubernetes、Node.js 服务端、SSR/Next.js 或独立反向代理。它们与单容器自托管边界不匹配，并会增加备份和故障域。若未来需求进入多容器/多主机阶段，必须重新评审，而不是预先加入闲置依赖。

### 2.6 版本与依赖治理

- 仓库固定 `.NET SDK 10.0.x` feature band、Node.js 24 LTS、pnpm 主版本，并提交 NuGet/npm lockfile；
- 基础镜像、s6-overlay 和下载的外部二进制同时锁定版本与 SHA-256/digest；
- .NET 与 ASP.NET Core 跟随 .NET 10 每月安全补丁，不锁死到初始 patch；
- React、Vite、EF Core、gRPC、OpenTelemetry 等主版本升级必须通过兼容性、安全和性能回归；
- 生产构建生成 SBOM、NuGet/npm 依赖清单和 Runtime manifest；
- 前端构建产物复制进 ASP.NET Core `wwwroot`，最终运行镜像不保留 npm token、源码映射（除非显式启用）或 Node 工具链；
- 所有第三方库在引入前检查许可证、维护状态和已知安全问题，避免仅为一个简单辅助函数新增运行时依赖。

### 2.7 建议的解决方案结构

```text
src/
├── CloudEmuera.Domain/            # 领域实体、状态机、策略，无基础设施依赖
├── CloudEmuera.Application/       # 用例、端口、DTO、授权调用
├── CloudEmuera.Infrastructure/    # EF Core、SQLite、文件系统、审计实现
├── CloudEmuera.Api/               # ASP.NET Core HTTP/WS、静态 SPA
├── CloudEmuera.Contracts/         # HTTP/WS schema 与共享常量
├── CloudEmuera.Ipc/               # .proto 与 gRPC 生成代码
├── CloudEmuera.Supervisor/        # Worker 生命周期、沙箱、对账
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

依赖方向必须保持：`Domain ← Application ← adapters`，以及 `RuntimeAdapter ← EmueraRuntime ← Worker`。`RuntimeAdapter` 不引用真实解释器、Worker 或 Web/API；`Worker` 不引用 EF Core；`Supervisor` 不加载 Emuera 解释器；前端只依赖生成的公开契约。

### 2.8 选型依据

- [.NET 官方支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)：.NET 10 是当前活动 LTS，支持至 2028-11-14。
- [ASP.NET Core WebSocket 文档](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)：Kestrel 原生提供 WebSocket 和 Origin 等配置能力。
- [.NET gRPC over UDS 文档](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds?view=aspnetcore-10.0)：官方支持以 Unix Domain Socket 承载进程间 gRPC。
- [EF Core SQLite 限制](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)：明确了并发 token 和表重建式迁移等限制，本文据此采用应用版本号和独立 Migrator。
- [React 19](https://react.dev/blog/2024/12/05/react-19) 与 [Vite 8](https://vite.dev/blog/announcing-vite8)：采用当前稳定主版本；Node.js 仅参与构建。
- [Node.js 发布策略](https://nodejs.org/en/about/previous-releases)：构建环境采用 Node.js 24 LTS，不采用 Current 分支。
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/)：traces、metrics、logs 均为稳定信号。
- [Playwright 浏览器矩阵](https://playwright.dev/docs/browsers)：可覆盖 Chromium、Firefox、WebKit 及移动设备配置。
- [s6-overlay](https://github.com/just-containers/s6-overlay)：提供容器 PID 1、服务依赖、独立监督和有序停止能力。
- [NsJail](https://github.com/google/nsjail)：集中提供 Linux namespace、cgroup、rlimit、只读 mount 和 seccomp-bpf 策略。
- [SkiaSharp](https://github.com/mono/SkiaSharp)：为 .NET 提供跨平台 Skia 2D、图片与字体处理能力。

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
│  └─ Admin / Audit                                               │
│          │ Unix domain socket                                   │
│          ▼                                                      │
│  Worker Supervisor ───── spawn/monitor/limit ─────┐              │
│          │                                        ▼              │
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
| Game Package Service | 上传暂存、解包校验、草稿、发布、文件浏览 | 运行游戏 |
| Compatibility Service | 静态扫描、解析验证、生成能力报告 | 绕过 Blocked 能力 |
| Session Control Plane | 创建、关闭、查询、配额预留、状态转换 | 解释器执行 |
| Realtime Gateway | WebSocket 鉴权、恢复、广播、背压、输入转发 | 修改显示事件语义 |
| Save Service | 在 Session 停止时授权访问其原生存档文件 | 解析存档内容、维护独立历史版本 |
| Admin Service | Worker/Session 观测、强制停止、策略配置 | 绕过审计 |
| Audit Service | 追加式审计记录 | 保存密码或输入全文 |

API 进程内部建议采用分层依赖：

```text
HTTP / WebSocket adapters
          ↓
Application use cases
          ↓
Domain model + authorization policies
          ↓
SQLite repositories / filesystem / Supervisor client
```

领域层不得依赖 HTTP、WebSocket、SQLite 或 Unix socket 的具体类型。

### 3.2 Worker Supervisor 进程

Supervisor 是 Session Worker 生命周期和本地路由的唯一管理者，职责包括：

- 启动时扫描数据库和受管子进程，完成一次状态核对；
- 根据 API 命令创建 SessionRoot、启动 Worker、分配 `workerId` 和 epoch；
- 维护 Worker 心跳、进程退出码和本地 IPC 连接；
- 应用 CPU、内存、进程数、文件描述符、磁盘与输出速率限制；
- 将 Worker 事件转发给 API，并在 API 暂不可用时保留有界缓冲；
- 对关闭和强制终止执行分阶段回收；
- 拒绝所有 epoch 不是数据库当前值的 Worker 注册、心跳和输出。

Supervisor 不解析 ERB，不保存用户认证状态，也不直接向浏览器开放端口。

### 3.3 Session Worker 进程

每个 Worker 只加载一个 GameVersion，且在整个生命周期内只服务一个 Session。内部组件如下：

| 组件 | 职责 |
| --- | --- |
| Runtime Host | 初始化、运行和终止 Emuera 解释器 |
| RuntimePaths | 注入 SessionRoot、资源、配置、临时文件与两种存档布局 |
| Console Adapter | 将解释器绘制调用转换为结构化显示操作 |
| Snapshot Store | 维护当前 ConsoleSnapshot、序号和有界增量环形缓冲 |
| Input Coordinator | 生成 prompt、验证输入、去重并唤醒 Runtime |
| Capability Guard | 禁止 DLL、进程、非允许网络和不安全路径操作 |
| Worker IPC | 注册、心跳、事件发送、命令接收与断线重连 |

Runtime 主循环不得被浏览器发送速度阻塞。Console Adapter 将事件写入有界内部队列；队列接近上限时合并相邻文本/样式事件，超过上限时生成新快照并丢弃已被快照覆盖的旧增量。

P0-06 的实际进程切片已落在 `CloudEmuera.Supervisor/SupervisorHost` 和
`CloudEmuera.Worker/WorkerConnectionLoop`：Supervisor 在自己的私有 runtime 目录监听
单一 UDS，使用一次性 bootstrap JSON 启动一个带不可变
`sessionId/workerId/workerEpoch` 的 Worker；Worker 只使用已经物化的 SessionRoot，并以
`worker.proto` 的 IPC v1 双向流发送结构化 Console、输入结果、心跳和终态。Worker 的
控制/显示发送队列有界且由单一 gRPC writer 串行写出，短断时保留 binding、prompt 和
Console 状态并重新注册。UDS stale endpoint 只有在 `lstat` 确认 socket 类型、服务账户
owner、单链接和 `0700` 父目录后，才通过受保护父目录句柄 `unlinkat` 清理；祖先 symlink、
路径越界、权限或属主异常均拒绝。Supervisor/Worker 生命周期日志输出结构化
`sessionId/workerId/workerEpoch`，并过滤 token、SessionRoot、UDS/bootstrap 路径和输入值。
持久 WorkerLease、epoch 分配和 Supervisor 重启对账仍属于 P1-05，不在本切片内。

### 3.4 进程启动顺序

1. init 启动 Supervisor；
2. Supervisor 打开 SQLite，执行只读兼容性检查并恢复 Worker 管理状态；
3. init 启动 API；
4. API 完成数据库迁移锁检查，连接 Supervisor，执行 Session 对账；
5. API 就绪检查成功后开始接收业务流量。

API 退出不向 Supervisor 发送全局停止信号。容器整体收到终止信号时，init 先停止接入，再要求 Supervisor 对 Worker 执行有界优雅关闭，超时后终止容器。

## 4. 领域模型与状态约束

### 4.1 标识符

外部可见实体使用不可预测的时间有序 ID，字符串带类型前缀，例如 `usr_`、`game_`、`gver_`、`sess_`、`wrk_`、`save_`。数据库内部可直接以该字符串为主键。不得把连续整数 ID 暴露为资源定位符。

时间统一以 UTC 存储为 RFC 3339 字符串或 Unix 毫秒；API 输出 RFC 3339。校验和统一使用 `sha256:<lowercase-hex>`。

### 4.2 Session 聚合

Session 是状态转换、配额占用和 WorkerLease 的事务边界。核心不变量：

- 一个 Session 固定一个 `gameVersionId`，创建后不可更改；
- 活动状态最多有一个当前 WorkerLease；
- 当前租约 epoch 只能递增，不能复用；
- `CLOSED` 是终态；MVP 中 `CRASHED` 不能直接回到 `RUNNING`；
- 状态更新必须同时匹配 `sessionId + stateVersion + expectedState`；
- 活动配额占用与 `CREATING` 的写入必须在同一事务中完成。

### 4.3 Session 状态转换表

| 当前状态 | 事件 | 下一状态 | 执行者 | 事务/副作用 |
| --- | --- | --- | --- | --- |
| 无 | CreateAccepted | CREATING | API | 幂等键落库并预留活动配额 |
| CREATING | StartRequested | STARTING | API/Supervisor | 创建新 epoch 和 WorkerLease |
| STARTING | WorkerReady | RUNNING 或 DETACHED | Supervisor | 依据当前连接数决定状态 |
| RUNNING | LastClientDetached | DETACHED | API | 不终止 Worker |
| DETACHED | FirstClientAttached | RUNNING | API | 仅更新连接派生状态 |
| STARTING/RUNNING/DETACHED | CloseRequested | STOPPING | API | 拒绝后续新输入 |
| STOPPING | WorkerStopped | CLOSED | Supervisor | 清除活动租约并释放配额 |
| STARTING/RUNNING/DETACHED | HeartbeatExpired/UnexpectedExit | CRASHED | Supervisor | 失效租约并释放配额 |
| 任意非终态 | AdminForceStop | STOPPING | API | 写审计，随后强制停止 |

`RUNNING` 与 `DETACHED` 由有效实时连接数派生，但仍写入数据库供查询。连接计数不作为 Worker 存活依据。若 API 在切换状态时崩溃，对账任务按连接和心跳事实修正状态。

### 4.4 WorkerLease

WorkerLease 至少包含：

```text
sessionId, workerId, epoch, status, pid,
ipcEndpoint, acquiredAt, heartbeatAt, expiresAt,
runtimeVersion, protocolVersion
```

新 Worker 创建必须在数据库事务内执行 `epoch = previousEpoch + 1`。Supervisor 向 Worker 签发只对 `sessionId + workerId + epoch` 有效的启动令牌。API、Supervisor 和 Worker 在处理控制命令、输入、心跳和输出时都要匹配当前 epoch。存档不经过 IPC 提交；只有当前 Worker 能写当前 SessionRoot。

## 5. 持久化设计

### 5.1 SQLite 使用约束

- 启用 WAL、外键和 busy timeout；
- 业务写入使用短事务，不在事务内执行解压、哈希大文件或等待 IPC；
- 迁移由一个进程持有文件锁后执行，Supervisor 在迁移期间保持既有 Worker，但不修改不兼容表；
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

#### games

```text
id TEXT PK
owner_user_id TEXT NOT NULL FK users
name TEXT NOT NULL
visibility TEXT NOT NULL           -- PRIVATE | SERVER_SHARED
status TEXT NOT NULL               -- ACTIVE | DELETED
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
state_version INTEGER NOT NULL DEFAULT 0
UNIQUE(owner_user_id, name)
```

#### game_versions

```text
id TEXT PK
game_id TEXT NOT NULL FK games
version_label TEXT NOT NULL
status TEXT NOT NULL               -- DRAFT | VALIDATING | PUBLISHED | BLOCKED | DELETED
content_digest TEXT NULL (非 NULL 值唯一)
content_path TEXT NOT NULL
manifest_json TEXT NOT NULL
runtime_config_json TEXT NOT NULL
compatibility_summary_json TEXT NOT NULL
created_by TEXT NOT NULL FK users
created_at INTEGER NOT NULL
published_at INTEGER NULL
state_version INTEGER NOT NULL
UNIQUE(game_id, version_label)
```

#### sessions

```text
id TEXT PK
owner_user_id TEXT NOT NULL FK users
game_id TEXT NOT NULL FK games
game_version_id TEXT NOT NULL
-- FOREIGN KEY(game_version_id, game_id) REFERENCES game_versions(id, game_id)
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
FOREIGN KEY(game_version_id, game_id) REFERENCES game_versions(id, game_id)
```

常用索引：`(owner_user_id, created_at DESC)`、`(state, last_activity_at)`、`(game_version_id)`。

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
response_status INTEGER NOT NULL
response_json TEXT NOT NULL
resource_id TEXT NULL
created_at INTEGER NOT NULL
expires_at INTEGER NOT NULL
PRIMARY KEY(actor_user_id, scope, idempotency_key)
```

同一键但请求摘要不同返回 `409 IDEMPOTENCY_KEY_REUSED`。

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

P1-01 另外建立 EF history 表 `schema_migrations`，并保留 EF Core SQLite provider 使用的
内部 `__EFMigrationsLock` 表；产品迁移仍必须先持有 CloudEmuera 的
`<database>.migration.lock` 文件锁。`game_files`、`compatibility_diagnostics` 和短期
`worker_command_results` 留给后续任务，首版不创建。存档没有独立内容表；其所有权和
GameVersion 关联由所属 Session 决定，管理操作写入 `audit_events`。

### 5.3 文件系统布局

```text
/data/
├── cloudemuera.db
├── keys/                                   # ASP.NET Core Data Protection key ring
├── games/
│   ├── staging/{uploadId}/                 # 未信任，禁止 Worker 使用
│   └── {gameId}/{gameVersionId}/content/   # 发布后不可变
├── sessions/{sessionId}/
│   ├── root/                               # Worker 可见 GameRoot
│   └── metadata/
│       ├── runtime-manifest.json
│       └── last-crash.json
├── run/                                    # UDS；重启可重建，不备份
├── logs/
└── backups/
```

`/data/run` 必须设置为仅 API、Supervisor 和 Worker 服务身份可访问；也可以实际放在容器 tmpfs 中，但对外路径保持配置化。

### 5.4 SessionRoot 构造

Session 管理方在首次启动 Worker 前构造持久、独占的运行目录：

```text
root/CSV/          # GameVersion 的 Session 私有副本
root/ERB/          # GameVersion 的 Session 私有副本
root/resources/    # GameVersion 的 Session 私有副本
root/sound、font/  # 存在时原样复制
root/<其他目录>/   # 所有合法未知目录也原样复制
root/emuera.config # GameVersion 配置的 Session 私有副本
root/save*.sav     # 根目录模式，Emuera 直接读写
root/global.sav    # 根目录模式，Emuera 直接读写
root/sav/          # UseSaveFolder:YES 时，Emuera 直接读写
root/tmp/          # Session 私有临时内容
```

Session 管理方按已发布 manifest 复制完整文件树，不为 CSV/ERB/resources 建立特殊分类，也不静默丢弃未知合法内容。基线实现使用普通字节复制；底层文件系统支持时可以尝试 reflink，但必须保持写时复制语义并在不支持时回退普通复制。禁止硬链接，因为它会让 Session 与 GameVersion 或其他 Session 共享可写 inode。上传阶段已经拒绝软链接、硬链接、FIFO、设备和 socket，复制阶段仍需再次核对实际条目，防止检查与复制之间被替换。

复制成功前先在同一 sessions 父目录下建立 staging root；只有文件数、总字节数、逐项类型和清单摘要全部通过后，才原子重命名为最终 SessionRoot。失败只清理本次 staging，不触碰已存在 SessionRoot。Worker 的 mount namespace 隐藏原始 GameVersion 和其他 Session，只暴露它自己的完整副本及必要系统路径。

SessionRoot 位于挂载数据目录中，本身就是存档的唯一权威副本。Worker 重启复用同一路径；正常退出和崩溃都不触发复制、generation 发布或第二套存档提交协议。

同一容器内的 API/Supervisor 可拥有管理权限，Worker 必须降权，清除不需要的 capabilities，并应用 `no_new_privs`。不同 Session 的工作目录不能通过路径枚举互访。

## 6. 游戏包与版本设计

### 6.1 上传流水线

```text
Upload → Quarantine → Archive scan → Safe extract → File scan
       → Encoding/case analysis → Runtime validation → Draft
       → Publish transaction → Immutable content
```

1. API 流式接收上传内容到随机命名临时文件，同时计算 SHA-256 并执行压缩前大小限制；
2. 解包器逐项规范化路径，先校验后落盘；
3. 拒绝绝对路径、`..`、NUL、设备文件、FIFO、硬链接、游戏包携带的符号链接和大小写/Unicode 规范化冲突；
4. 同时限制条目数、单文件大小、总展开大小、目录深度和压缩比；
5. 对 ERB/CSV/配置文件检测 BOM，并以确定性顺序尝试 UTF-8 与 Shift-JIS；模糊结果产生诊断，不依赖系统 locale；
6. 静态扫描禁止能力、资源引用和文件名大小写；
7. 在受限验证 Worker 中执行解析验证，验证超时或超资源则失败；
8. 发布时计算规范清单摘要，将内容移动到最终目录，改为只读，再在数据库事务中将版本置为 `PUBLISHED`。

若文件移动成功而数据库事务失败，后台清理器根据“数据库无引用且超过安全期”回收孤儿目录。不得在请求失败时立即递归删除尚未核对的路径。

### 6.2 草稿与编辑

- 发布版本不可原地编辑；
- “编辑已发布版本”先创建引用该版本的新草稿；
- 写文件使用 `If-Match`/文件版本避免覆盖并发修改；
- 保存时保留原始编码，用户明确转换编码时才更新编码元数据；
- 文本搜索限制在允许的文本文件、当前 GameVersion 和资源配额内；
- 发布生成新的内容摘要，Session 永远固定发布版本 ID。

### 6.3 运行时清单

每个发布版本记录：

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

存在错误或 Blocked 能力时默认禁止发布；管理员只能对明确允许覆盖的诊断项作有审计的例外，不能覆盖平台级禁止项。

## 7. Session 生命周期详细流程

### 7.1 创建 Session

客户端必须发送 `Idempotency-Key`。处理流程：

1. API 验证用户、GameVersion 可见性、版本状态和兼容策略；
2. 在 `BEGIN IMMEDIATE` 短事务中读取活动配额，插入 `CREATING` Session 和幂等记录；
3. API 向 Supervisor 发送带 `commandId` 的 `worker.start`；
4. Supervisor 以 `commandId` 去重，在事务中递增 epoch 并写入 `STARTING` 租约；
5. Supervisor 首次完整复制 GameVersion 或校验已有 SessionRoot、施加限制并启动 Worker；
6. Worker 使用启动令牌注册，加载 Runtime，发送 `worker.ready`；
7. Supervisor 验证 epoch 并将 Session 更新为 `RUNNING` 或 `DETACHED`；
8. API 返回 Session 资源。若同步等待超时，返回 `202`，客户端查询状态，不重复创建。

启动失败将 Session 置为 `CRASHED`，记录阶段化错误码并释放配额。诊断信息不得包含其他用户路径或密钥。

### 7.2 关闭 Session

关闭接口同样要求幂等键：

1. API 以条件更新把非终态改为 `STOPPING`；
2. Realtime Gateway 立即拒绝新输入，并广播 `session.stopping`；
3. Supervisor 发送 `worker.stop(graceDeadline, finalAutosavePolicy)`；
4. Worker 停止创建新 prompt，等待当前安全点，刷新存档，发送最终事件并退出；
5. 超过宽限期后 Supervisor 先发送终止信号，再在硬超时后强制结束；
6. Supervisor 确认进程退出、失效租约，把 Session 置为 `CLOSED` 并释放配额；存档已经位于持久 SessionRoot，无退出提交阶段；
7. API 广播终态并关闭该 Session 的 WebSocket 订阅。

重复关闭 `CLOSED` Session 返回原终态。用户关闭、管理员终止、资源限制和容器关闭使用不同 `closeReason`。

### 7.3 心跳与崩溃判定

Worker 以配置间隔发送单调时钟产生的心跳，其中包含 CPU、RSS、文件数、输出速率、当前序号和 prompt 状态。Supervisor 收到后更新内存态，并按较低频率批量持久化，避免每次心跳写 SQLite。

只有 Supervisor 可以根据子进程退出或心跳截止时间判定 `CRASHED`。判定时必须再次检查 `sessionId + workerId + epoch`，防止旧 Worker 把新 Worker 标记为崩溃。

## 8. 实时显示、重连与输入

### 8.1 显示模型

Worker 输出的是结构化操作，不是 HTML 字符串。MVP 节点建议包括：

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

所有颜色、尺寸、枚举、文本长度、资源 ID 和层级深度在 Worker 与浏览器两端校验。资源只使用由 GameVersion 清单解析出的 `assetId`，不能使用游戏提供的任意 URL。

### 8.2 序号与快照

- 每个被接受的显示操作或原子批次分配一个严格递增的 `sequence`；
- Snapshot 表示应用完 `snapshotSequence` 后的完整有界显示树和当前 prompt；
- 环形缓冲保存 `(snapshotSequence, currentSequence]` 的增量；
- sequence 使用 64 位整数，数据库仅保存观测值，不参与热路径分配；
- Worker 重启必须使用新 epoch；sequence 可从持久记录后继续，但客户端以 `(epoch, sequence)` 作为完整游标。

### 8.3 无丢失窗口的恢复算法

Realtime Gateway 对一个 Session 建立恢复屏障：

1. 验证 WebSocket 身份和 Session 权限；
2. 订阅 Worker 实时流，并先把新事件暂存到有界连接队列；
3. 请求 `resume(lastEpoch, lastSequence)`；
4. 若 epoch 一致且增量仍完整，Worker 返回连续增量；否则返回 `Snapshot(N)`；
5. Gateway 发送恢复数据，再发送暂存队列中序号大于恢复终点的事件；
6. 去除重复序号后进入实时转发；
7. 客户端确认 `ackSequence`，用于监测消费进度，不用于删除 Worker 的唯一副本。

步骤 2 先于步骤 3，保证快照读取与订阅之间没有丢失窗口。若暂存队列溢出，Gateway 丢弃该次恢复结果并重新请求较新的 Snapshot，而不是继续发送不连续事件。

### 8.4 慢客户端策略

每个连接有字节数和事件数双重上限：

- 优先批量发送连续小事件；
- 队列达到软上限时通知客户端 `resync.required`；
- 达到硬上限时丢弃该连接的待发增量，发送最新快照；
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

去重缓存采用内存 LRU，并把最近的已接受输入结果以有界日志写入 Session metadata；其保留窗口至少覆盖 WebSocket 自动重试最大时长。即使缓存淘汰，已经完成的 prompt 也会因 `promptId` 不再当前而拒绝再次执行。

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

## 9. HTTP API 设计

### 9.1 通用约定

- 基础路径：`/api/v1`；
- JSON 字段使用 `camelCase`，枚举值使用大写下划线；
- 创建、关闭、发布、导入等可重试写操作要求 `Idempotency-Key`；
- 乐观更新使用 `If-Match: "<stateVersion>"`；
- 列表使用稳定游标分页，不使用不稳定的大偏移分页；
- 错误体统一包含 `code`、`message`、`requestId`、可选 `details`；
- 资源不存在和无权访问默认都返回 `404`，降低资源枚举风险；
- 大文件上传下载采用流式处理，不整体载入内存。

### 9.2 主要端点

| 方法与路径 | 用途 | 权限/说明 |
| --- | --- | --- |
| `POST /auth/login` | 本地登录 | 速率限制、防暴力破解 |
| `POST /auth/logout` | 注销当前会话 | 使服务端会话失效 |
| `GET /games` | 列出可见游戏 | 私有所有者或共享游戏 |
| `POST /games` | 创建 Game | 玩家 |
| `POST /games/{id}/versions:upload` | 上传为草稿 | 所有者，流式 |
| `GET /game-versions/{id}/files` | 浏览目录 | 授权后访问 |
| `GET/PUT /game-versions/{id}/files/{path}` | 查看/编辑草稿文本 | 严格规范化路径 |
| `POST /game-versions/{id}:validate` | 启动验证 | 返回任务状态 |
| `POST /game-versions/{id}:publish` | 发布不可变版本 | 幂等、要求无阻断错误 |
| `GET /sessions` | 列出自己的 Session | 支持状态过滤 |
| `POST /sessions` | 创建 Session | 幂等、预留活动配额 |
| `GET /sessions/{id}` | Session 详情 | 所有者/管理员 |
| `POST /sessions/{id}:close` | 优雅关闭 | 幂等 |
| `GET /sessions/{id}/saves` | 列出存档 | 所有者 |
| `PUT /sessions/{id}/saves/{path}` | 导入/替换原生存档 | Session 停止、严格路径校验 |
| `GET /sessions/{id}/saves/{path}` | 下载原生存档 | 所有者、流式响应 |
| `PATCH /sessions/{id}/saves/{path}` | 重命名 | Session 停止、目标名校验 |
| `DELETE /sessions/{id}/saves/{path}` | 删除 | Session 停止、要求显式确认 |
| `GET /admin/workers` | Worker 状态与资源 | 管理员 |
| `POST /admin/sessions/{id}:force-stop` | 强制停止 | 管理员，必须填写原因并审计 |
| `GET /health/live` | 进程存活 | 不检查昂贵依赖 |
| `GET /health/ready` | 接流量能力 | DB、数据目录、Supervisor |
| `GET /version` | 构建/运行时版本 | 不暴露敏感路径 |

WebSocket 入口为 `GET /api/v1/realtime`。连接建立后通过 `session.resume` 订阅一个或多个已授权 Session；每次订阅和恢复都重新鉴权。

## 10. API—Supervisor—Worker IPC

### 10.1 传输与身份

IPC 默认使用 Unix domain socket，协议定义独立于传输。socket 文件权限限制到服务账户；连接建立时还要使用容器启动时生成的短期服务凭据完成挑战响应。Worker 使用一次性启动令牌注册，令牌绑定 Session、Worker、epoch 和过期时间。

禁止把 UDS 暴露到容器端口或共享宿主目录。对敏感命令同时验证对端身份和消息字段，不把“能连接 socket”视为完整授权。

P0-06 的具体契约位于 [`src/CloudEmuera.Ipc/Protos/worker.proto`](../src/CloudEmuera.Ipc/Protos/worker.proto)，
当前 `protocolVersion=1`。注册同时校验 IPC 版本、`RuntimeBaseline.CloudEmueraIntegrationVersion`
和固定 upstream commit；Supervisor 以 256-bit bootstrap token 和完整 binding 校验 Worker，
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

### 11.1 原生语义与物理隔离

Runtime 只调用 Emuera 原生序列化与反序列化逻辑。SessionRoot 就是存档的持久工作目录，不存在运行目录之外的 SaveArtifact 或逐次保存 generation：

- `UseSaveFolder:NO`：Emuera 直接读写 `SessionRoot/save*.sav` 和 `SessionRoot/global.sav`；
- `UseSaveFolder:YES`：Emuera 直接读写 `SessionRoot/sav/*`。

布局由该游戏版本的 `emuera.config` 决定，不由用户、API 或 Worker 启动参数另行选择。宿主可把检测结果记录在 manifest 中用于提前校验，但若它与运行时实际读取的 `UseSaveFolder` 不一致，Worker 必须拒绝启动，不能同时搜索两处或改写配置。

`global.sav` 只在一个 Session 内是“全局”。每个 SessionRoot 都是独立的普通目录，因此两个用户或同一用户的两个 Session 不共享 `global.sav`、slot 文件、目录 inode 或可写父目录。

### 11.2 生命周期与直接读写

创建 Session 时建立空的持久 SessionRoot。此后：

1. Supervisor 把该目录的规范绝对路径交给唯一 Worker；
2. Emuera 在该目录中按原生行为直接打开、覆盖、读取或删除存档；
3. Worker 正常退出或崩溃后保留目录原状；
4. 同一 Session 再次启动 Worker 时复用原目录，不执行启动物化或退出提交；
5. 删除 Session 时才按明确产品策略删除或归档整个目录。

CloudEmuera 不包装每次原生保存，不改变上游 `FileMode`、flush、覆盖和失败语义。如果进程在 Emuera 写文件期间被终止，磁盘内容以文件系统和上游当时状态为准；系统不得把这描述成已提供事务性 generation。需要恢复历史时，对静止的整个 `/data` 或 SessionRoot 做宿主机快照/备份。

### 11.3 管理操作

活动 Session 的原生文件由 Worker 独占，API 不与其并发修改。列出和下载可以在明确接受非事务性读取语义后开放；MVP 的上传、替换、重命名、复制和删除只允许 Session 处于 `CLOSED` 或其他无 Worker 状态，并在执行前后复核租约不存在。

管理操作只处理允许列表内的 `save*.sav`、`global.sav` 以及当前布局允许的 `sav/` 条目：

- 下载：每次验证 Session 所有权，使用安全下载名并流式读取当前文件；
- 上传/复制：先写入当前 SessionRoot 外的隔离暂存文件，验证大小、目标名和基本原生格式；确认 Session 仍无 Worker后，再移动到目标路径；这只是管理操作的文件替换，不形成历史 generation；
- 重命名/删除：要求显式确认、再次检查无活动 Worker，并写审计日志；
- 自动保存：完全由游戏和 Emuera 控制；CloudEmuera 不另建自动存档调度器；
- 备份：面向整个挂载数据目录或 SessionRoot，由运维备份策略负责。

## 12. 安全设计

### 12.1 信任边界

不受信任输入包括游戏包、文件名、ERB/CSV 内容、游戏生成的显示内容、存档、浏览器输入和客户端游标。API、Supervisor、Worker 彼此也不因同容器部署而省略协议校验。

### 12.2 Worker 沙箱

目标控制：

- 非 root 运行，`no_new_privs`，最小 capabilities；
- 独立进程和文件系统视图，只暴露本 Session 的完整可写副本，不暴露原始 GameVersion 或其他 Session；
- 默认无网络；若实现确需本地 IPC，仅允许指定 UDS；
- seccomp/等价策略禁止创建额外进程、挂载、ptrace 和危险系统调用；
- cgroup v2 或容器内等价控制限制 CPU、内存和进程数；
- `rlimit` 限制文件描述符、文件大小和 core dump；
- 磁盘通过独立目录计量和写入前配额检查控制；
- 达到输出速率限制时先合并/节流，持续违规时按策略终止并审计。

单容器部署是否能提供全部内核隔离能力必须在启动时自检。缺少强制安全能力时，管理员界面明确显示降级状态；面向不受信任多用户开放的部署应拒绝就绪，而不是静默降级。

### 12.3 Web 安全

- 生产环境仅使用 HTTPS，Cookie 设置 `Secure`、`HttpOnly`、合适的 `SameSite`；
- Cookie 认证的写操作使用 CSRF 防护；WebSocket 校验 Origin 和登录态；
- CSP 默认禁止脚本来源扩张、对象嵌入和任意媒体 URL；
- Emuera HTML 先解析为内部节点，再由前端组件生成 DOM；禁止 `innerHTML` 直灌；
- 文本、tooltip、文件名和错误详情按上下文编码；
- 资源响应使用清单中声明的 MIME、`nosniff` 和下载/内联白名单；
- 登录、上传、搜索、创建 Session 和输入分别实施速率限制。

### 12.4 路径安全

所有用户路径先按 `/` 解析为逻辑相对路径，拒绝空字节、绝对路径、盘符、父级段和平台保留名。最终访问使用基于目录句柄的逐段打开，并禁止跟随符号链接；只做字符串前缀比较不构成安全校验。SessionRoot 复制过程没有链接例外，遇到任何链接或特殊文件都失败。文件名同时执行 Unicode 规范化和大小写碰撞检查。

### 12.5 密钥与隐私

密码只保存现代自适应哈希；服务凭据从容器 secret 或仅服务身份可读文件加载，不写入数据库、日志或 runtime manifest。默认日志只记录输入类型、长度和结果，不记录输入全文。崩溃报告在落盘前移除 Cookie、Authorization、启动令牌及用户输入缓冲。

## 13. 可观测性与运维

### 13.1 日志

采用结构化日志，公共字段包括：

```text
timestamp, level, service, eventName, requestId,
sessionId?, workerId?, workerEpoch?, userIdHash?, durationMs?, result
```

API 请求、Session 创建/连接/关闭、Worker 注册/崩溃、游戏发布和存档删除都有稳定事件名。日志按大小和时间轮转，保留策略可配置。

### 13.2 指标

至少暴露：

- API 请求量、错误率和延迟；
- WebSocket 连接数、恢复耗时、重同步次数和待发队列字节数；
- 各 Session 状态数量、创建/启动/关闭/崩溃计数；
- 每 Worker CPU、RSS、文件描述符、磁盘、事件速率、快照大小；
- prompt 等待时间、输入接受/重复/过期计数；
- SQLite busy、事务时长、WAL 大小和备份结果；
- 上传拒绝原因、兼容性诊断数量和存档文件管理失败数。

高基数字段如 `sessionId` 不默认作为通用时序数据库标签；在日志和按需诊断接口中查询。

### 13.3 健康检查

- liveness：当前 API 事件循环可响应；
- readiness：数据库可读写、`/data` 可写且空间高于安全阈值、Supervisor 协议兼容、迁移完成；
- Supervisor 健康：本地 IPC 可用、对账循环在运行；
- version：Web/API、Supervisor、Worker 协议、上游 Runtime commit、源码集成版本、schema 版本。

单个 Worker 崩溃不应使整个 API liveness 失败，但会影响 Session 指标和管理员告警。

### 13.4 审计

审计事件记录谁在何时对什么资源执行了什么操作及结果。管理员强停和安全例外必须包含原因。审计元数据只保存必要字段，既不能依赖普通应用日志，也不能包含密码、令牌或输入全文。

## 14. 故障恢复与对账

### 14.1 API 重启

API 启动后从 SQLite 读取活动 Session，再向 Supervisor 请求当前 `(sessionId, workerId, epoch, pid, heartbeat)` 清单：

- 两侧一致：重建路由；
- DB 活动但 Supervisor 无进程：超过心跳窗口后置 `CRASHED`；
- Supervisor 有 Worker 但 DB epoch 更高：fence 并停止旧 Worker；
- Supervisor 有 Worker 且 DB 一致但 API 无路由：重新注册；
- DB 已 `CLOSED` 但 Worker 仍存在：停止并审计异常。

### 14.2 Supervisor 重启

首选 init 配置保证 Supervisor 不轻易重启；若重启，已有 Worker 不能仅依赖父进程管道存活。Worker 使用稳定的 Session UDS 或重新连接端点，并携带启动令牌派生凭据重新注册。Supervisor 对每个注册都查询当前 epoch。无法重新接管的孤儿 Worker 被安全终止，Session 标记 `CRASHED`。

### 14.3 数据目录故障

发现 `/data` 不可写、空间低于保留阈值或 SQLite 出现完整性错误时：

- readiness 失败；
- 禁止新建 Session、发布版本和通过 API 修改存档；
- 已运行 Worker 可继续到安全点，但任何持久化失败都必须明确反馈，不能声称保存成功；
- 管理员获得高优先级告警；
- 不自动删除用户数据来尝试恢复空间。

### 14.4 容器/宿主重启

MVP 不恢复指令级内存状态。重启后所有先前活动 Session 在对账后标记 `CRASHED`，其 SessionRoot 原样保留；用户可以检查其中的原生存档，或显式基于该目录创建新的独立 Session 副本。不得把旧 Session 显示成仍可从中断指令继续运行。

## 15. 前端设计

前端按领域分为游戏库、游戏版本编辑器、Session 列表、游戏控制台、存档管理和管理员控制台。

游戏控制台维护：

```text
connectionState
sessionId + workerEpoch
lastAppliedSequence + lastAckedSequence
bounded rendered model
currentPrompt
pending clientMessageId → result
```

渲染器只接收已验证的内部节点。按键和按钮最终都生成同一种 `session.input` 消息；发送后保持待定状态，直到收到确定结果。重连时不凭本地 DOM 推断权威状态，而是使用游标恢复或替换为服务端快照。

移动端需处理软键盘造成的 visual viewport 改变、安全区域、触摸目标尺寸和自动滚动；自动滚动仅在用户已接近底部时发生，避免打断查看历史。所有按钮可通过键盘聚焦，颜色不是唯一状态提示，图片具有可用的替代文本或装饰标识。

## 16. 配置设计

配置来源优先级：内置安全默认值 < 配置文件 < 环境变量。敏感项不允许通过会被 `/version` 或日志输出的普通配置展示。

配置组包括：

- 身份提供商与会话 Cookie；
- 数据路径、数据库参数和备份窗口；P1-01 的数据库 CLI 与布局如下：

  ```bash
  CloudEmuera.Migrator migrate --data-root <path> [--database cloudemuera.db]
  CloudEmuera.Migrator check --data-root <path> [--database cloudemuera.db]
  ```

  `migrate` 独占 `<database>.migration.lock`，仅在存在待执行 migration 时把 SQLite
  Online Backup 写入 `<data-root>/backups/`，然后执行全部编译进程的 migration；`check`
  只读验证 migration history、foreign-key check 和 quick check。退出码 `0/10/11/12/13/14/15`
  分别表示成功、配置非法、锁竞争、数据库高于当前 binary、备份失败、migration 失败和完整性
  检查失败。业务 API、Supervisor 和 Worker 不调用 `Database.Migrate()`。
- 上传/解压/文件数量/编码限制；
- 用户活动 Session、Worker CPU/内存/PID/FD/磁盘配额；
- 输出速率、快照、增量和 WebSocket 队列上限；
- 心跳、启动、停止、强制终止超时；
- 存档文件大小、停止态管理操作和 SessionRoot 备份保留；
- Runtime 基线、兼容配置和禁止能力；
- 日志级别、轮转、指标和审计保留。

启动时完成配置模式校验；不安全组合或单位错误直接失败，并给出不含密钥的明确诊断。

## 17. 测试策略

### 17.1 测试层级

| 层级 | 重点 |
| --- | --- |
| 领域单元测试 | 状态机、授权、配额、幂等、epoch、输入抢占 |
| Runtime 兼容测试 | v18 与当前 EM+EE 的解析、变量、输入、存档和命令 |
| 协议契约测试 | HTTP、WebSocket、IPC 版本和未知字段兼容 |
| 文件系统安全测试 | 穿越、链接、Unicode/大小写碰撞、TOCTOU、压缩炸弹 |
| 组件集成测试 | SQLite + 文件系统 + Supervisor + Worker |
| 浏览器测试 | 重连、输入、移动视口、键盘与可访问性 |
| 故障注入 | API/Supervisor/Worker 退出、IPC 中断、磁盘满、慢客户端 |
| 性能测试 | 代表性游戏负载、持续 PRINT、多连接、快照恢复 |
| 视觉回归 | 字体测量、换行、按钮命中、图片和 Sprite |

### 17.2 必测并发性质

- 相同幂等键并发创建只产生一个 Session；
- 活动配额只剩一个名额时，并发创建最多一个成功；
- 两个客户端回答同一 prompt 只有一个 `ACCEPTED`；
- Worker A epoch 过期后，其心跳、输出和输入结果均被拒绝，且它不能获得新 Worker 的 SessionRoot 写权限；
- Snapshot 生成期间持续输出，恢复流仍无缺口且无乱序；
- 关闭和输入并发时，结果要么输入先被接受，要么明确被停止状态拒绝；
- Worker 写存档时退出后，系统保留 SessionRoot 现场且不产生虚假的“已提交”状态；其他 Session 不受影响。

### 17.3 需求验收映射

| 验收项 | 主要设计落点 | 核心测试 |
| --- | --- | --- |
| AC-001/004/014 | 独立 Worker、SessionRoot、授权 | 双用户双 Session 隔离测试 |
| AC-002/003 | 有界快照、恢复屏障、进程分离 | 断网和 API 重启测试 |
| AC-005 | promptId/clientMessageId | 重复与并发输入测试 |
| AC-006/007 | 关闭流程、心跳和崩溃判定 | 超时关闭和 kill Worker |
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

1. SQLite schema、游戏上传/发布和本地账户；
2. Supervisor、Worker IPC、epoch 和 Session 状态机；
3. 结构化 Console、Snapshot、WebSocket 恢复和输入去重；
4. SessionRoot 隔离和停止态原生存档文件管理；
5. Web 游戏库、Session 控制台、存档界面；
6. 管理员 Worker 查看与强制停止；
7. 单容器进程管理、健康检查、备份和升级说明；
8. 完成 AC-001 至 AC-014 自动化或可重复验收。

### 18.3 Phase 2 预留接口

资源指标、审计、备份和策略接口在 MVP 即保留版本字段，但不提前实现多主机调度。未来挂起恢复必须新增状态和显式能力协商，不能把 `DETACHED` 偷换为不占 Worker 的“挂起”。

## 19. 待决设计记录

以下事项在进入对应实现前必须形成 ADR：

1. `ADR-001`：从本地账户切换到单一 OIDC Provider 的触发条件和迁移方式；
2. `ADR-002`：所选 Linux namespace/cgroup/seccomp 沙箱在目标发行版和 Docker 配置下的能力验证、降级与拒绝就绪条件；
3. `ADR-003`：ConsoleSnapshot 的序列化格式、大小上限和压缩策略；
4. `ADR-004`：Emuera HTML、Sprite、CBG 和音频的 MVP 允许列表；
5. `ADR-005`：默认活动 Session、CPU、内存、磁盘、上传和存档配额；
6. `ADR-006`：合法可纳入 CI 的 v18 与 EM+EE 代表性游戏集；
7. `ADR-008`：字体文件保留、服务和授权策略；
8. `ADR-009`：SessionRoot 备份恢复点目标、保留期和升级回滚流程。

## 20. 设计完成定义

进入 MVP 编码前至少应满足：

- 本文档通过前端、API、Runtime、运维和安全联合评审；
- 所有待决项已指定负责人和截止阶段，不阻塞当前迭代；
- Session 状态转换、HTTP/WebSocket/IPC schema 形成机器可校验定义；
- SQLite 首版迁移和文件布局经过崩溃一致性评审；
- Worker 沙箱在目标 Docker/宿主环境完成可重复验证；
- v18 与当前 EM+EE 最小兼容测试集可在 CI 运行；
- AC-001 至 AC-014 均有可执行的验收方案。
