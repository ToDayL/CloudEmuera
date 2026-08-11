# CloudEmuera 可验证开发计划

状态：Draft v0.2

更新日期：2026-08-11
依据：`requirements.zh-CN.md`、`design.zh-CN.md`

## 1. 计划目标

本计划把 CloudEmuera 从已验证的工程骨架推进到可重复验收的单机 MVP。所有步骤都必须产生可检查的代码或文档，并提供自动化命令和明确通过条件。步骤默认按编号顺序执行；只有依赖已满足且验证边界完全独立时才能并行。

状态标记：`DONE` 已完成、`NEXT` 下一步、`TODO` 未开始、`BLOCKED` 外部条件阻塞。

下文中尚不存在的测试项目和脚本属于对应步骤的必交付物；该步骤只有在这些命令真实存在、可独立执行且返回正确退出码后才能标记为 `DONE`。

## 2. 全局质量门

每个步骤合并前都必须满足：

```bash
./scripts/check.sh
./scripts/verify-third-party.sh
git diff --check
```

涉及开发容器或挂载路径时额外执行：

```bash
./scripts/verify-dev-user.sh
```

共同完成条件：

- 测试或验证脚本明确映射需求编号；
- 新协议具有版本字段、未知字段兼容测试和失败响应测试；
- 新持久化结构具有 migration、升级测试和失败回滚说明；
- 新文件入口具有路径穿越、链接、配额和异常中断测试；
- 并发状态更新具有竞争测试，而不只测试串行正常路径；
- 验证失败必须返回非零退出码，不能依赖人工阅读日志判断。

## 3. Phase 0：运行时切分与兼容性证明

### P0-00 — 工程与开发环境基线（DONE）

需求映射：OPS-003、NFR 可维护性基线。

交付物：.NET/React 工程骨架、锁文件、开发镜像、宿主 UID/GID 映射、固定上游源码基线、Apache-2.0 许可证。上游源码最初以 submodule 固定，P0-04 前迁移为仓库内普通源码文件，commit 不变。

验证：

```bash
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
./scripts/dev-up.sh
curl --fail http://localhost:28647/health/live
curl --fail http://localhost:5173/
./scripts/dev-down.sh
```

通过条件：后端 Release 构建零警告零错误；4 个领域测试和前端测试通过；API/Web 容器与 bind mount 文件均为宿主 UID/GID；上游提交与固定值一致；两个 HTTP 端点返回成功。

### P0-01 — 兼容测试资产与运行时基线（DONE）

需求映射：COMP 兼容性、AC-008、AC-009、ADR-006。

交付物：

- `ADR-006`，确定可合法进入 CI 的合成 v18 与 EM+EE 测试资产；
- 最小游戏集，覆盖启动、PRINT、INPUT、变量、分支、根目录存档和 `sav/` 存档；
- 每个资产的来源、许可证、SHA-256 和预期 transcript；
- `scripts/verify-runtime-fixtures.sh`。

验证：

```bash
./scripts/verify-runtime-fixtures.sh
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=FixtureContract'
./scripts/check.sh
./scripts/verify-third-party.sh
git diff --check
```

通过条件：资产清单与磁盘内容校验一致；测试无网络运行；至少一个 v18 风格和一个当前 EM+EE 风格合成游戏被发现；缺失、被篡改或许可证信息不完整时验证必定失败。2026-08-04 已在开发容器中完成上述命令，fixture validator 报告 2 个 profile、14 个 payload 文件，`FixtureContract` 26 项测试通过。

2026-08-07 复核：在 Linux 开发容器重跑上述验证，fixture validator 报告 2 个 fixture、18 个文件（P0-04/05 增补资产后由 14 个 payload 文件增长），`FixtureContract` 27 项通过；`scripts/check.sh`、`scripts/verify-third-party.sh` 与 `git diff --check` 均通过。

### P0-02 — 平台端口与 RuntimePaths（DONE）

需求映射：SAVE-002、SAVE-011～014、Phase 0 运行时要求。

交付物：`IGameConsole`、`RuntimePaths`、文件系统、时钟、图像和音频端口；路径值对象和 SessionRoot 布局构造器；不引用 WinForms/GDI+ 的 RuntimeAdapter 核心。

验证：

```bash
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=RuntimePaths|Category=Architecture'
```

通过条件：根目录与 `sav/` 两种布局映射测试通过；绝对路径、`..`、符号链接逃逸和跨 Session 路径被拒绝；架构测试证明 Domain/Application/RuntimeAdapter 核心不引用 WinForms；伪时钟可确定性推进超时输入。P0-05 依据 ADR-0007 把 Game current content 完整复制到持久 SessionRoot，不增加链接例外。

2026-08-04 验证记录：在 Linux 开发容器（.NET 10 SDK）中完成 `RuntimePaths|Architecture` 专项测试 53 项、RuntimeAdapter 测试程序集全量 79 项（其中 FixtureContract 26 项）；覆盖生产 `TimeProviderRuntimeClock` deadline smoke test、手动时钟 deadline 累计 elapsed，以及 `sav/` 嵌套目录的创建、读取、写入、元数据和枚举一致性。架构测试直接检查程序集引用，禁止 `System.Drawing`、`System.Drawing.Common`、NAudio 和 `CloudEmuera.Application`，并检查公共 API 的字段、事件、构造函数和继承关系。`scripts/check.sh` 通过（Release 构建 0 警告/0 错误，后端与 Web 检查通过），`scripts/verify-runtime-fixtures.sh`、`scripts/verify-third-party.sh` 和 `git diff --check` 均通过。符号链接与 FIFO 逃逸用例在 Linux 上实际创建并拒绝；Windows/其他平台仍覆盖词法、规范路径和程序集架构约束，实际重解析点发布门保留给 Linux CI。

2026-08-07 复核：`RuntimePaths|Architecture` 专项 48 项通过。2026-08-04 记录的 53 项为当时分类快照，P0-03～05 增补并重分类部分测试后该类计数变化；RuntimeAdapter 测试程序集全量当前为 137 项。

### P0-03 — 结构化 Console 与输入端口（DONE）

需求映射：PLAY-001～003、PLAY-007～010。

交付物：文字、样式、换行、按钮、图片和输入提示的最小结构化事件模型；单调 sequence；`promptId` 输入接口；有界内存 ConsoleSnapshot 原型。

验证：

```bash
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=ConsoleContract'
```

通过条件：固定运行时调用生成稳定 transcript；sequence 严格递增；未知/危险 HTML 不能产生脚本或任意 URL；历史超过上限后仍能生成一致快照；过期 prompt 被拒绝。

2026-08-04 验证记录：在 Linux 开发容器（.NET 10 SDK）中完成 `ConsoleContract` 专项 36 项、RuntimeAdapter 全量 115 项、Domain 4 项；覆盖并发 Emit 精确 `1..N`、估算字节与 receipt cache 上限、generator 分配和调用者 ID 拒绝、旧 ID 淘汰后的复用/迟到输入回归、rollover 后 snapshot+deltas 归约、HTML 注入 limits 与属性/URL 攻击、invalid/valid 输入并发、取消/超时/有效输入竞态及 timeout 后迟到输入。`./scripts/check.sh` 通过（Release 构建 0 警告/0 错误，Web typecheck、测试和 production build 通过），`./scripts/verify-third-party.sh` 与 `git diff --check` 通过。

2026-08-07 复核：`ConsoleContract` 专项 37 项通过（2026-08-04 记录为 36 项，后续增补 1 项）。

### P0-04 — 无 UI Runtime 运行到 INPUT（DONE）

需求映射：COMP、SESS-003/004、PLAY-001、AC-008/009。

交付物：直接集成的固定上游解释器源码、CloudEmuera headless integration project、来源/修改记录；无 UI runtime harness；从测试 Game 内容树启动、输出、停在 INPUT、接受输入并继续执行的流程。

验证：

```bash
./scripts/test-runtime-compat.sh --scenario input-roundtrip
```

通过条件：v18 与 EM+EE 两套资产均在无显示服务器环境运行；输出 transcript 与基线一致；相同输入得到确定结果；运行期间不存在 WinForms 窗口、桌面会话或 API 进程内状态依赖。

2026-08-05 完成记录：Ubuntu 24.04 开发容器、.NET SDK 10.0.302，
`DISPLAY`/`WAYLAND_DISPLAY` 均为空；固定 upstream commit
`2175f8a629257efb08214e093704b3a3d3d06d05`、tree
`a3c96867e3a5b5d5f90877a4e7c6f8056d5f5b9b`、integration version
`headless-p0.4.1`。真实上游 `ErbLoader`、parser、`Process`、`VariableEvaluator`
和指令系统执行 `v18-core` 与 `em-ee-core`，各 18 项场景断言通过；完整 transcript、
integer prompt、RESULT 分支、以 `verifiedByVisibleOutput` 明示的 score=3 证据、
bold/italic、Sprite、连续 sequence、无存档
副作用和 GameContent 摘要均通过。旧 fixture-only AST executor 已删除。
RuntimeCompatibility 19 项（其中 RuntimeBridge 16 项）、RuntimeAdapter 116 项、
Domain 4 项和 Web 1 项
测试通过；Release 构建 63 条固定上游源码 warning/0 错误，Web typecheck 与
production build 通过。
文件输入使用 `IRuntimeFileSystem` 私有兼容视图，物理 GameRoot 为空的测试通过；
System.IO 调用点审计见 `docs/runtime-system-io-audit.zh-CN.md`。
审查补强覆盖纯计算无限循环在硬超时内返回 `DeadlineExceeded`、四个
`PrintButton`/`PrintButtonC` 重载保留提交值，以及禁止从输出文本伪造 runtime
变量。`./scripts/check.sh` 再次通过。
初始化 deadline 也会协作取消 preload/ERH/ERB 装载，并统一回收迟到完成的
session 与私有 file view；回归测试确认超时后第二个 host 可以立即成功初始化，
静态 runtime gate 不泄漏。

### P0-05 — Emuera 原生存档双布局（DONE）

需求映射：SAVE-001/002、SAVE-004/005、SAVE-010～015、AC-013/014。

详细设计：[`tasks/P0-05-native-save-session-root-plan.zh-CN.md`](tasks/P0-05-native-save-session-root-plan.zh-CN.md)，架构决策见 [`ADR-0007`](adr/0007-session-root-native-save-ownership.md)。

交付物：Session 管理方构造的持久私有运行目录；Game 当前完整合法普通文件树的独立副本；由游戏配置决定的根目录 `save*.sav/global.sav` 与 `sav/` 布局；Worker/Emuera 对 SessionRoot 的原生直接保存/加载 harness。

验证：

```bash
./scripts/test-runtime-compat.sh --scenario save-root
./scripts/test-runtime-compat.sh --scenario save-directory
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore \
  --configuration Release --filter 'Category=SessionRoot|Category=SaveIsolation'
```

通过条件：两种布局均由复制后的 `emuera.config` 自动决定，能保存、退出、用同一 SessionRoot 重新启动并加载原值；未知合法目录和文件完整保留；存档只出现在对应 SessionRoot；两个用户和同一用户两个 Session 不共享可写 inode，路径、文件与 Global 值互不影响；Game 库源内容未被修改；不存在 SaveArtifact、generation 或退出时复制/提交步骤。

2026-08-05 完成记录：实现 ADR-0007 的完整普通文件树复制和持久 SessionRoot 直写模型。
Session 管理方/harness 负责 SessionRoot 分配、Game/source digest 绑定、manifest/配额输入和授权
前置条件；RuntimeAdapter builder 只执行安全物化与原子发布，不管理 Session 生命周期。
固定上游配置文件键为 `Use sav folder`，由复制后的 `emuera.config` 决定根目录或
`sav/` 存档布局，并由 Runtime 二次校验 `Config.UseSaveFolder`。两个真实 fixture 均完成
跨 Host 原生普通/Global 存档往返、Session/源版本隔离和未知合法条目完整复制。
`save-root` 与 `save-directory` 各 20 项断言通过；RuntimeAdapter 全量 137 项、
RuntimeCompatibility 全量 26 项、Domain 4 项通过；新增 `前`/`後` 日文配置交叉验证、
双 Session 原生 Global/inode 隔离及取消/模拟终止保留测试。`./scripts/check.sh` 及 Web
构建质量门通过，integration version 为 `headless-p0.5.1`。

### P0-06 — 单 Session Worker 与 IPC 冒烟链路（DONE）

需求映射：SESS-002/010、OPS-004、Phase 0 进程隔离。

详细设计：[`tasks/P0-06-single-session-worker-ipc-plan.zh-CN.md`](tasks/P0-06-single-session-worker-ipc-plan.zh-CN.md)。

交付物：Worker 启动配置、版本握手、启动/输入/输出/关闭 gRPC 消息、Unix Domain Socket 权限、每进程单 Session 强制约束。

验证：

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Ipc.ContractTests --no-restore --configuration Release
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore \
  --configuration Release --filter 'Category=ProcessIsolation'
```

通过条件：契约序列化和未知字段测试通过；错误协议版本被明确拒绝；第二个 Session 不能进入同一 Worker；API 测试进程退出不导致 Worker 立即退出；关闭请求后 Worker 在限定时间内退出。

2026-08-05 完成记录：冻结 IPC protocol v1 与 `worker.proto` 字段号/保留字段，Supervisor
仅监听权限为 `0600` 的 Unix domain socket，Worker bootstrap 父目录/文件分别为
`0700`/`0600`，启动令牌为 256-bit CSPRNG base64url 且不进入 argv、环境变量或日志。
stale socket 清理验证 Unix socket 类型、服务账户 owner、单链接和 `0700` 父目录后，使用
父目录句柄 `unlinkat`；Supervisor/Worker 生命周期日志包含 session/worker/epoch 关联字段并
脱敏 token、路径和输入。真实 `CloudEmuera.Worker` 子进程直接消费 P0-05 SessionRoot，完成
v18 与 EM+EE 两套 fixture 的注册、版本握手、ready、结构化 DisplayBatch、INPUT、重复输入、
completed、stopped 和退出；另覆盖错误 token、独立控制客户端进程退出、stop 等待输入、同
Worker 第二次 start、错误 binding、Supervisor stream 短断重连、两个 Worker 并行隔离和 UDS
恶意路径。IPC 契约测试 9 项、ProcessIsolation 测试 18 项，均在 Linux dev Docker 中通过；
P1-05 的持久 WorkerLease、epoch 分配、Supervisor 重启对账和沙箱限制仍未实现。

2026-08-11 架构修订：上述结果保留为独立 Worker、UDS、binding 和进程隔离的历史证据；
[`ADR-0015`](adr/0015-api-owned-worker-lifecycle.md) 已取代独立 Supervisor 生产拓扑及 API 退出后
Worker 继续运行的目标。P1-05 将复用实现迁入 API，并按
[`ADR-0016`](adr/0016-reopenable-session-root-lifecycle.md) 实现可反复开启的持久 Session。

## 4. Phase 1：端到端单机 MVP

### P1-01 — SQLite 首版 schema 与迁移（DONE）

需求映射：核心领域模型、GAME-004/008/010、SESS-002/005/007/010、SAVE-005/015、
OPS-005、NFR-011。

详细设计：[`tasks/P1-01-sqlite-initial-schema-migration-plan.zh-CN.md`](tasks/P1-01-sqlite-initial-schema-migration-plan.zh-CN.md)。

交付物：quota_profiles、users、games、旧 `game_versions`、sessions、worker_leases、
idempotency_records、audit_events migration；SessionRoot 路径、并发 token、唯一索引和
UTC 时间约定；独占 Migrator、迁移前一致性备份和失败回滚。

验证：

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore \
  --configuration Release --filter 'Category=Migration|Category=PersistenceConstraint'
```

通过条件：空库可升级到最新版本；已有样例库升级后数据不丢失；重复内容哈希、租约唯一性和幂等键约束由数据库强制；失败 migration 不留下部分 schema。

2026-08-07 完成记录：`InitialMetadata`（`20260807071428_InitialMetadata`）固化八张业务表、
`schema_migrations`、旧 GameVersion/Session 与 lease epoch 外键、RESTRICT 删除/更新、
JSON/digest/path/enum/布尔 CHECK、审计只追加 trigger；未创建 Save 或默认 Identity 表。统一
connection factory 启用 foreign keys、WAL、synchronous NORMAL 和有界 busy timeout；独立
Migrator 负责 `<database>.migration.lock`、Online Backup、完整性检查和稳定退出码。
Infrastructure 全量 36 项、`Migration|PersistenceConstraint` 定向 33 项和
`MigrationProcess` 真实进程冒烟 3 项均在 Linux dev Docker 通过；构建为 Release 0 警告/0 错误。
另以独立临时目录验证了 migration 失败回滚、备份前置屏障、锁竞争、取消/损坏数据库、FIFO/
sidecar symlink、statx 文件类型封送、锁文件替换竞态、descriptor-backed SQLite 打开、busy
timeout、独立 connection 约束和 epoch fencing。Linux 数据库/锁/备份操作使用受保护目录句柄、
`openat(O_NOFOLLOW)`、`/proc/self/fd/<fd>`、`flock`、`linkat`/`unlinkat` 和 inode identity check，
随后执行
`./scripts/check.sh`、`./scripts/verify-dev-user.sh`、`./scripts/verify-third-party.sh` 和
`git diff --check` 均通过。

### P1-02 — 本地身份、资源授权与审计（DONE）

需求映射：AUTH-001～006、OPS-004/005、SEC-009、NFR-011/015～018、AC-004。

详细设计：[`tasks/P1-02-local-identity-authorization-audit-plan.zh-CN.md`](tasks/P1-02-local-identity-authorization-audit-plan.zh-CN.md)。

交付物：本地账户、email-only 登录、可撤销 Cookie session、密码哈希与锁定、CSRF/Data
Protection、玩家/管理员策略、资源所有权授权器、敏感操作审计、仅未初始化实例从 `.env`
原子创建首个管理员并强制首次改密，以及接入真实身份的登录和管理员用户页面；`ADR-0001`
记录未来 OIDC 触发条件。自动化校验使用独立临时 env/DataRoot/Compose project/端口，不读取或
修改同一 checkout 的人工 `.env`、`./data` 或开发容器。

验证：

```bash
./scripts/test-identity.sh --suite api
./scripts/test-identity-e2e.sh
```

通过条件：并发启动最多原子创建一个 bootstrap 管理员，完成后永久忽略 bootstrap 变量且不会因
管理员缺失重跑；登录只接受 email，首次登录强制改密；人工与自动化环境隔离；
匿名访问受保护端点返回 401；跨用户私有资源返回不可枚举的 404；管理员只获得显式
策略能力且操作写入审计；注销、禁用、改密和角色变化会撤销旧会话；Cookie 写请求具备 CSRF
防护；WebSocket 升级/恢复授权组件每次重新鉴权，真实协议由 P1-08 接线；日志不包含密码、
Cookie、session ID 或输入全文；现有前端完成真实登录、强制改密和注销闭环。

实施记录（2026-08-09）：已交付真实 email-only Cookie 登录、首次改密、注销、管理员用户
创建/资料编辑/启停/角色调整/临时密码重置页面，以及服务端 CSRF、会话撤销、审计与资源授权
边界。`IdentityApiContractTests` 覆盖匿名拒绝、CSRF、首次改密、管理员创建、非管理员管理端点
拒绝和禁用即时撤销；`e2e/tests/identity.spec.ts` 覆盖 bootstrap 管理员和玩家的浏览器闭环。
验证入口保持隔离的临时 env、DataRoot、Compose project 和动态端口。

复验与加固（2026-08-09）：修复 COMPLETED 实例在移除/改变 bootstrap 变量后重启错误降级、
授权 action/kind/descriptor 不一致仍可能放行、审计缺少 HTTP request correlation 等问题；补齐适配
既有 `users` 表的 ASP.NET Core Identity store、分批且单轮有界的 auth session 清理、Data
Protection owner/type/link/mode 校验。新增 P1-01 带数据升级、并发最后管理员、审计写失败回滚、
统一认证失败、登录限流、Cookie 跨 host 重建、logout 重放、WebSocket Origin/resume、key ring
恶意路径和桌面/移动 API 重启 E2E。`./scripts/check.sh` 实际通过 262 个 .NET 测试、6 个 Web
测试、类型检查和 production build，Release 构建为 0 warning/0 error。
开发启动脚本同时改为在 API 启动前独占运行 Migrator；登录页会区分未迁移/未初始化、限流、服务
故障与真实凭据错误，避免把 `503 SERVICE_NOT_READY` 误报为邮箱或密码错误。

### P1-03 — 安全游戏包摄取（DONE）

需求映射：GAME-001～003、GAME-007、AC-010。

详细设计：[`tasks/P1-03-secure-game-package-ingestion-plan.zh-CN.md`](tasks/P1-03-secure-game-package-ingestion-plan.zh-CN.md)，架构决策见 [`ADR-0008`](adr/0008-secure-zip-ingestion-policy.md)。

交付物：流式上传、配额、隔离暂存、安全解包、编码检测、内容哈希和诊断模型；恶意归档语料库。

验证：

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.GamePackages.Tests --no-restore \
  --configuration Release --filter 'Category=ArchiveSecurity|Category=Encoding'
```

通过条件：正常 Shift-JIS/UTF-8 包可摄取；路径穿越、绝对路径、符号/硬链接、大小写或 Unicode 碰撞、压缩炸弹和超配额输入被拒绝；失败输入不会在目标目录留下文件。

实施记录（2026-08-09）：交付传输无关的非 seek 流摄取服务、classic ZIP 中央/local header
预检、Stored/Deflate allowlist、NFC/portable path 与碰撞检查、声明/实际双重配额、CRC32、逐文件
SHA-256 与规范 content digest、UTF-8 BOM/无 BOM 和 CP932 检测、一次性 READY/CONSUMING
消费、追加式成功/拒绝审计，以及 SQLite 持久 staging 预算和过期 reaper。新增标准 EF migration
`AddGamePackageIngestions`、模型快照、API DI 接线和 locked `CloudEmuera.GamePackages.Tests` 项目。
整改复核后，摄取、分析、消费和清理全部锚定受保护 dirfd，使用 `openat(O_NOFOLLOW)`、`renameat`
和安全后序 `unlinkat`，不再以 `GetFullPath + StartsWith` 或递归 `Directory.Delete` 作为边界。
READY 消费在 CAS 前校验 manifest/content 句柄，CONSUMED/FAILED/ABANDONED 以
`cleanup_completed_at` 跨重启收敛；API 注册启动及周期 reaper，超时 CONSUMING 有 watchdog。
结构化诊断补齐 messageKey/arguments、NFC、UTF-16/32、NUL/control 与截断汇总；Unicode Path
extra `0x7075` 按 legacy-name CRC 和严格 UTF-8 验证。

44 项游戏包测试覆盖非 seek UTF-8/CP932 正常包、路径/碰撞/链接、ZIP64、加密、未知 method、
overlap、truncated Deflate、FIFO/socket/device、Unicode Path extra、声明/实际配额、内容摘要、
TOCTOU、取消、模拟磁盘写满、rename/CAS/audit 故障、过期 CONSUMING、终态清理重试、reaper 与
消费竞争、Abandon/Complete CAS、lease 归属篡改、可配置中央目录边界及并发预算唯一胜者；API
后台注册 3 项与 Migration/PersistenceConstraint 34 项回归通过。
`./scripts/check.sh` 通过 locked restore、Release 0 warning/0 error、全部 .NET 与 Web 测试、Web
typecheck 和 production build；`./scripts/verify-third-party.sh` 与 `git diff --check` 通过。

P1-01 的 `game_versions` 是 ADR-0010 之前已完成的旧 schema。不得回改已提交 migration；P1-04
必须新增升级 migration，把内容元数据合并进 `games`、把 Session 改为 Game + 源摘要快照，并删除
旧表和产品代码。

### P1-04 — 简化游戏库、工作区编辑与当前内容启用（DONE）

需求映射：GAME-004～010。

详细设计：
[`tasks/P1-04-simple-game-library-plan.zh-CN.md`](tasks/P1-04-simple-game-library-plan.zh-CN.md)，
架构决策见
[`ADR-0010`](adr/0010-single-game-content-without-version-entities.md)。

交付物：移除 GameVersion 的 schema/代码迁移；单一 Game API；workspace 目录浏览、文本读取编辑和
搜索；验证与 current content 原子启用；运行时清单；Session 源摘要快照；引用保护和逻辑删除。

验证：

```bash
dotnet test tests/CloudEmuera.Api.IntegrationTests --filter 'Category=GameLibrary'
```

通过条件：公开契约不存在 GameVersion；每个 Game 最多一个 workspace 和一个 current content；
编辑/启用不改变既有 SessionRoot；Session 创建与内容替换并发只复制完整旧树或新树；被 Session
引用的 Game 不能删除；升级后旧 schema 数据按明确规则保留或在修改前安全失败。

当前实现记录：schema/实体/授权/RuntimeAdapter 的 GameVersion 清除、旧数据升级、Game CRUD、摄取
绑定、workspace 编辑/丢弃、目录/文本/下载/搜索、只读 current 启用、文件/诊断索引、持久 operation
与基础对账已经完成。一次性真实 Emuera parser-only Validator 及超时/崩溃/超输出/非法协议、只读
validation snapshot、owner inode marker、内容复制/发布/删除的 Linux dirfd 主路径、持久 copy lease、
`CONTENT_READY` 恢复/续租/retired 清理、强 ETag/创建前置条件、幂等、独立速率限制、诊断 override、
签名搜索游标，以及迁移计划/选择/物理 journal 已完成。当前已通过 `check.sh`、开发用户映射、
第三方声明和 diff check；故障矩阵（真实 SIGKILL 进程终止、DB 提交窗口、lease 过期对账）、只读
遍历 TOCTOU 审计与非 Linux managed fallback 平台差异验证于 2026-08-09 完成并补测试
（Infrastructure 77 项），本步骤标记为 DONE。 2026-08-09 review 收尾：修复签名游标篡改测试的末字符解码抖动并重跑 `check.sh` 稳定通过；
Game 错误码与审计动作对齐计划 §15；API 暴露 OpenAPI 文档端点 `/openapi/v1.json`（契约测试断言
无 GameVersion）；生成式 TypeScript 客户端与 WebSocket JSON Schema 类型生成推迟到 P1-10；
Session 创建/启用并发完整集成测试按 P1-04 计划 §12 属 P1-06，本步交付 copy lease 端口及
rename 钉住测试。
2026-08-09 UI 接入：P1-04 的浏览器 UI（游戏库列表/创建/导入绑定、单一 Game 内容页的
workspace/current 文件浏览、文本编辑、搜索、验证、原子启用、丢弃草稿、删除与下载）已接入真实
API；P1-10 不再处理 Game 的 UI。同时修复开发容器 Validator 程序集路径解析（Development 下
默认路径指向不存在的目录）与 Debug/Release 解析行为不一致（上游 DEBUG 经过时间系统行被当作
阻断诊断），使开发环境可真实执行验证与启用。新增 Web 单元测试（列表/创建/上传绑定/文件编辑/
验证启用）、HTTP 集成测试（GameLibrary 类别，含真实 parser 进程）与 RuntimeBridge 回归测试；
`pnpm --dir src/CloudEmuera.Web typecheck/test/build`、定向 .NET 测试与隔离 e2e 身份套件通过。

### P1-05 — API Worker Manager、租约、epoch 与可重开状态机（NEXT）

需求映射：SESS-001/002/005/009～012、OPS-001/002、AC-003/006/007、ADR-0015/0016。

详细设计：[`tasks/P1-05-api-worker-manager-session-lifecycle-plan.zh-CN.md`](tasks/P1-05-api-worker-manager-session-lifecycle-plan.zh-CN.md)。

交付物：把 P0-06 可复用的 Worker 启动、UDS、安全校验和进程监视迁入 API Worker Manager；删除
独立 Supervisor 产品进程/入口；API 成为唯一运行期 SQLite 业务访问者；实现 WorkerLease 获取/
续租/回收、epoch fencing、心跳超时、活动配额、API 退出时有界 Worker 回收，以及
`CLOSED/CRASHED → STARTING` 的同 Session 重开状态机。Session 创建只物化一次 SessionRoot，
open/close 只获取或释放 Worker。

验证：

```bash
dotnet test tests/CloudEmuera.Api.IntegrationTests --filter 'Category=WorkerLifecycle'
dotnet test tests/CloudEmuera.Domain.Tests --filter 'Category=Concurrency'
```

通过条件：同一 `CLOSED/CRASHED` Session 并发 open 只产生一个有效 Worker；只剩一个活动名额时
并发 open 最多一个成功；旧 epoch 的心跳和结果全部被拒绝；kill Worker 后 Session 在心跳窗口内
变为 CRASHED，并可在旧写权限释放后以更大 epoch 复用同一 SessionRoot 重开；正常/强制终止 API
后 Worker 均在期限内退出，新 API 对账为 CRASHED；无法回收旧 Worker 时 ready 失败且不授予新
写租约。生产解决方案和容器不再启动独立 Supervisor，Worker 不访问 SQLite。

### P1-06 — 幂等 Session 创建、开启与关闭纵切（TODO）

需求映射：SESS-001、SESS-005～008、SESS-011/012、AC-001、AC-003、AC-006/007。

交付物：创建、列表、详情、open、close HTTP API；创建/open/close 独立幂等键；Game + source
content digest/manifest 固定；API Worker Manager 编排；关闭与输入、open 与存档写的并发语义。

验证：

```bash
dotnet test tests/CloudEmuera.Api.IntegrationTests --filter 'Category=SessionLifecycle'
```

通过条件：相同幂等键并发创建只产生一个 `CLOSED` Session 和一棵 SessionRoot，且不占活动配额；
同一游戏可创建两个隔离 Session；重复 open/close 不重复执行副作用；关闭后输入被拒绝；关闭后、
Worker 崩溃后和 API 重启对账后都能用同一 Session ID/SessionRoot 重开并加载原生存档；Game current
更新不改变重开 Session 的内容；关闭/输入及 open/存档写竞争结果符合设计允许的线性化结果。

### P1-07 — Snapshot、恢复屏障与有界输出（TODO）

需求映射：PLAY-004～006、PLAY-010/012、AC-002/012、ADR-003。

交付物：`ADR-003`；ConsoleSnapshot 序列化；短期增量环形缓冲；恢复屏障；批处理、背压和快照降级策略。

验证：

```bash
dotnet test tests/CloudEmuera.Realtime.Tests --filter 'Category=Snapshot|Category=Backpressure'
./scripts/test-realtime-load.sh
```

通过条件：快照生成期间持续输出仍无缺口、重复或乱序；慢客户端不会导致无界内存；超过增量窗口时确定性降级到新快照；负载报告证明内存保持在 ADR 上限内。

### P1-08 — WebSocket 恢复与输入去重（TODO）

需求映射：AUTH-005、PLAY-006～008、PLAY-011、AC-002/005。

交付物：版本化 WebSocket envelope、鉴权握手、断线恢复、ack、`promptId/clientMessageId` 去重和多客户端首个有效输入规则。

验证：

```bash
dotnet test tests/CloudEmuera.Realtime.Tests --filter 'Category=Reconnect|Category=InputDeduplication'
```

通过条件：浏览器断开后从最后 ack 恢复且无丢失窗口；重复消息只执行一次并返回同一结果；两个
客户端并发回答只有一个 ACCEPTED；同一 API 实例内 Worker IPC 短断可在有界宽限期恢复，超时则
Session 进入 CRASHED；越权恢复失败。API 重启不恢复旧 Worker 实时连接。

### P1-09 — Session 原生存档文件 API（TODO）

需求映射：SAVE-001～009、SAVE-015、AC-004/007/013/014。

交付物：按 Session 列出和下载原生存档；在 Session 无活动 Worker 时上传、替换、重命名、复制和删除；路径/大小/基本格式校验；用户、游戏和 Session 隔离。文件直接位于 SessionRoot，不建立 SaveArtifact 表或 generation 存储。

验证：

```bash
dotnet test tests/CloudEmuera.Saves.IntegrationTests
```

通过条件：跨用户和跨 Session 访问失败；上传校验大小、路径和版本；活动 Worker 存在时所有修改操作被拒绝；操作与 Worker 启动竞争时只有一方成功；显式复制产生独立物理文件；API 不解析或改写 Emuera 原生内容。

### P1-10 — 浏览器 Session 控制台和存档界面（TODO）

需求映射：SESS-005/006、PLAY-002/009、SAVE-003、AC-011。

交付物：登录、Session 列表、Session 创建/open/close/重开、结构化 Console、输入控件、重连状态
和存档管理页面；移动安全区和键盘处理。
游戏库、工作区编辑与发布等 Game 管理 UI 已随 P1-04 完成，不再属于 P1-10。

验证：

```bash
pnpm --dir src/CloudEmuera.Web test
pnpm --dir e2e test
```

通过条件：组件测试覆盖状态与错误分支；Playwright 在桌面和移动 viewport 完成登录、发布、创建 Session、输入、断线重连和存档下载；基础键盘导航和可访问性扫描无阻断错误。

### P1-11 — 管理、可观测性与就绪检查（TODO）

需求映射：OPS-001、OPS-003～006、AC-007。

交付物：结构化关联日志、Worker/Session 指标、管理员列表和强制停止、审计查询、live/ready/version 语义；敏感字段过滤。

验证：

```bash
dotnet test tests/CloudEmuera.Operations.IntegrationTests
./scripts/test-observability.sh
```

通过条件：日志包含 request/session/worker/epoch 关联字段但不含秘密或默认输入全文；依赖未就绪时 ready 失败但 live 语义正确；管理员终止可审计；指标能反映 Worker 崩溃和输出速率。

### P1-12 — Worker 沙箱与资源限制（TODO）

需求映射：SEC、安全非功能需求、OPS-002、AC-010、ADR-002/005。

交付物：`ADR-002` 和 `ADR-005`；namespace/cgroup/seccomp/只读挂载策略；CPU、内存、进程、磁盘和输出配额；不满足能力时拒绝 ready。

验证：

```bash
./scripts/test-worker-sandbox.sh
./scripts/test-worker-limits.sh
```

通过条件：Worker 不能读取其他 Session、写 Game workspace/current content、访问宿主路径、创建
禁止的进程或任意联网；CPU/内存/磁盘超限产生明确状态；目标 Docker 配置缺少必需沙箱能力时服务不进入 ready。

### P1-13 — 单容器生产进程管理与恢复（TODO）

需求映射：MVP 单容器、AC-003/006/007、OPS-003。

交付物：生产镜像中的 Migrator 前置检查、API 及其 Worker 子进程启动/停止编排；非 root 用户；
信号转发、parent-death/进程组/cgroup 回收兜底；数据目录迁移、备份与恢复说明。

验证：

```bash
./scripts/test-production-image.sh
./scripts/test-process-recovery.sh
```

通过条件：全新数据卷可启动；进程均非 root；SIGTERM 在期限内停止 Worker 并把活动 Session 标记
CRASHED；强制终止 API 后 Worker 在断连宽限期内退出或被回收；新 API 确认旧写权限释放后完成
CRASHED 对账，同一 Session 可复用原 SessionRoot 重开；遗留 Worker 无法回收时 ready 失败；生产
镜像不存在独立 Supervisor 服务；备份恢复后元数据和文件校验一致。

### P1-14 — MVP 验收、安全与性能门（TODO）

需求映射：AC-001～014、设计第 17 章全部测试层级。

交付物：需求—测试追踪矩阵；一键验收脚本；兼容性、安全、故障注入、性能、移动端和视觉回归报告；已知限制清单。

验证：

```bash
./scripts/acceptance-mvp.sh
```

通过条件：AC-001～014 每项至少有一个自动化测试或版本化、可重复的验收场景；脚本从干净 checkout 和空数据卷运行成功；失败报告指出具体 AC 编号；无未解释的高危安全问题；性能结果满足需求文档边界或有批准的 ADR 偏差。

## 5. Phase 2/3 进入条件

Phase 2 的资源治理、备份、管理员体验和更完整媒体兼容，只在 P1-14 通过后展开。Phase 3 的
SUSPENDED/RESUMING 和解释器快照必须另立设计与兼容性证明；在此之前不得因当前没有浏览器连接
而释放 Worker，也不得宣称 Worker 崩溃后能从同一指令恢复。

## 6. 近期执行队列

1. ~~完成 P1-04 剩余安全加固：真实 parser-only Validator、dirfd/fsync 内容存储、崩溃恢复/续租、
   copy lease 端口和旧 DataRoot 迁移工具。~~ 已于 2026-08-09 完成，P1-04 标记为 DONE。
2. 按 ADR-0015/0016 完成 P1-05：把 Worker 管理迁入 API，移除独立 Supervisor，并实现持久
   SessionRoot 的 open/close/reopen 生命周期。

每完成一步，更新本文件状态，并在对应 ADR、测试报告或提交说明中记录实际执行命令与结果。未通过当前步骤的验证，不进入依赖它的下一步骤。
