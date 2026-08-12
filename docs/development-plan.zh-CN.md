# CloudEmuera 可验证开发计划

状态：Draft v0.2

更新日期：2026-08-12
依据：`requirements.zh-CN.md`、`design.zh-CN.md`

## 1. 计划目标

本计划把 CloudEmuera 从已验证的工程骨架推进到可重复验收的单机 MVP。所有步骤都必须产生可检查的代码或文档，并提供自动化命令和明确通过条件。步骤默认按编号顺序执行；只有依赖已满足且验证边界完全独立时才能并行。

状态标记：`DONE` 已完成、`NEXT` 下一步、`TODO` 未开始、`BLOCKED` 外部条件阻塞。

范围说明：本文的历史步骤可能保留已完成系统的旧独立控制面、内核隔离、细粒度配额或断线恢复描述；
这些表述已由 [`ADR-0017`](adr/0017-trusted-self-hosted-mvp-simplification.md) 和 P1-S01 取代，
当前完成门以本文最新状态和简化计划为准。

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

2026-08-05 完成记录：冻结 IPC protocol v1 与 `worker.proto` 字段号/保留字段，旧控制面
仅监听权限为 `0600` 的 Unix domain socket，Worker bootstrap 父目录/文件分别为
`0700`/`0600`，启动令牌为 256-bit CSPRNG base64url 且不进入 argv、环境变量或日志。
stale socket 清理验证 Unix socket 类型、服务账户 owner、单链接和 `0700` 父目录后，使用
父目录句柄 `unlinkat`；Worker 生命周期日志包含 session/worker/epoch 关联字段并
脱敏 token、路径和输入。真实 `CloudEmuera.Worker` 子进程直接消费 P0-05 SessionRoot，完成
v18 与 EM+EE 两套 fixture 的注册、版本握手、ready、结构化 DisplayBatch、INPUT、重复输入、
completed、stopped 和退出；另覆盖错误 token、独立控制客户端进程退出、stop 等待输入、同
Worker 第二次 start、错误 binding、控制流断开退出、两个 Worker 并行隔离和 UDS
恶意路径。IPC 契约测试 9 项、ProcessIsolation 测试 18 项，均在 Linux dev Docker 中通过；
P1-05 的持久 WorkerLease、epoch 分配、控制面重启对账和旧资源隔离限制仍未实现。

2026-08-11 架构修订：上述结果保留为独立 Worker、UDS、binding 和进程隔离的历史证据；
[`ADR-0015`](adr/0015-api-owned-worker-lifecycle.md) 已取代旧独立控制面生产拓扑及 API 退出后
Worker 继续运行的目标。P1-05 将复用实现迁入 API，并按
[`ADR-0016`](adr/0016-reopenable-session-root-lifecycle.md) 实现可反复开启的持久 Session。

## 4. Phase 1：端到端单机 MVP

2026-08-12 起，后续范围按 ADR-0017 的可信参与者自托管边界执行。已经实现但需要降复杂度的
浏览器文件写入产品面、用户分层容量消费、Worker 断流恢复占位和运维占位，统一按独立计划
[`tasks/P1-S01-simplify-implemented-systems-plan.zh-CN.md`](tasks/P1-S01-simplify-implemented-systems-plan.zh-CN.md)
分步处理；身份/授权、关键审计、Migrator、持久幂等和 operation recovery 不在删除范围。

### P1-S01 — 已实现系统简化（DONE）

关联决策：[`ADR-0017`](adr/0017-trusted-self-hosted-mvp-simplification.md)。

完成内容：移除浏览器游戏文件写入面和服务端全文搜索；将活动 Worker、包摄取与 SessionRoot 预算收敛为
实例级容量选项；控制流断开后 Worker 停止并有界退出；保留基本健康、版本、Worker/Session 状态和关键
审计，移除专用遥测、细粒度进程资源面板及通用审计浏览入口。`QuotaProfile` 表、外键和历史数据暂保留，
但运行期不再按用户读取或调度；安全 ZIP 摄取、parser-only Validator、current 原子启用、SessionRoot
独立副本、原生存档、身份授权、幂等、恢复和 WorkerLease/epoch 约束继续有效。

验证：后端和前端完整检查必须通过；定向测试覆盖只读 Game API、实例 Worker 名额、旧 epoch 拒绝、
控制流断开退出、优雅/强制停止、API 父进程退出和不明确遗留 lease 不影响无关 Session。
2026-08-12 验证记录：`./scripts/check.sh` 通过（Release 0 warning/0 error、Application 13、API
22、Domain 22、GamePackages 51、Infrastructure 98、IPC 9、RuntimeAdapter 142、
RuntimeCompatibility 27、Worker 18、Web 13）；`./scripts/verify-dev-user.sh`、
`./scripts/verify-third-party.sh` 和 `git diff --check` 通过。

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
防护；WebSocket 升级/恢复授权组件每次重新鉴权，真实协议由 P1-09 接线；日志不包含密码、
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

### P1-04 — 简化游戏库、摄取与当前内容启用（DONE）

需求映射：GAME-004～010。

详细设计：
[`tasks/P1-04-simple-game-library-plan.zh-CN.md`](tasks/P1-04-simple-game-library-plan.zh-CN.md)，
架构决策见
[`ADR-0010`](adr/0010-single-game-content-without-version-entities.md)。

交付物：移除 GameVersion 的 schema/代码迁移；单一 Game API；workspace 目录浏览、文本只读查看和
下载；验证与 current content 原子启用；运行时清单；Session 源摘要快照；引用保护和逻辑删除。

验证：

```bash
dotnet test tests/CloudEmuera.Api.IntegrationTests --filter 'Category=GameLibrary'
```

通过条件：公开契约不存在 GameVersion；每个 Game 最多一个 workspace 和一个 current content；
只读浏览/启用不改变既有 SessionRoot；Session 创建与内容替换并发只复制完整旧树或新树；被 Session
引用的 Game 不能删除；升级后旧 schema 数据按明确规则保留或在修改前安全失败。

当前实现记录：schema/实体/授权/RuntimeAdapter 的 GameVersion 清除、旧数据升级、Game CRUD、摄取
绑定、workspace 摄取、目录/文本只读查看/下载、current 启用、文件/诊断索引、持久 operation
与基础对账已经完成。一次性真实 Emuera parser-only Validator 及超时/崩溃/超输出/非法协议、只读
validation snapshot、owner inode marker、内容复制/发布/删除的 Linux dirfd 主路径、持久 copy lease、
`CONTENT_READY` 恢复/续租/retired 清理、强 ETag/创建前置条件、幂等、独立速率限制、诊断 override、
签名搜索游标，以及迁移计划/选择/物理 journal 已完成。当前已通过 `check.sh`、开发用户映射、
第三方声明和 diff check；故障矩阵（真实 SIGKILL 进程终止、DB 提交窗口、lease 过期对账）、只读
遍历 TOCTOU 审计与非 Linux managed fallback 平台差异验证于 2026-08-09 完成并补测试
（Infrastructure 77 项），本步骤标记为 DONE。 2026-08-09 review 收尾：修复签名游标篡改测试的末字符解码抖动并重跑 `check.sh` 稳定通过；
Game 错误码与审计动作对齐计划 §15；API 暴露 OpenAPI 文档端点 `/openapi/v1.json`（契约测试断言
无 GameVersion）；生成式 TypeScript 客户端与 WebSocket JSON Schema 类型生成推迟到 P1-11；
Session 创建/启用并发完整集成测试按 P1-04 计划 §12 属 P1-06，本步交付 copy lease 端口及
rename 钉住测试。
2026-08-09 UI 接入：P1-04 的浏览器 UI（游戏库列表/创建/导入绑定、单一 Game 内容页的
workspace/current 文件浏览、文本只读查看、验证、原子启用、删除与下载）已接入真实
API；P1-11 不再处理 Game 的 UI。同时修复开发容器 Validator 程序集路径解析（Development 下
默认路径指向不存在的目录）与 Debug/Release 解析行为不一致（上游 DEBUG 经过时间系统行被当作
阻断诊断），使开发环境可真实执行验证与启用。新增 Web 单元测试（列表/创建/上传绑定/文件查看/
验证启用）、HTTP 集成测试（GameLibrary 类别，含真实 parser 进程）与 RuntimeBridge 回归测试；
`pnpm --dir src/CloudEmuera.Web typecheck/test/build`、定向 .NET 测试与隔离 e2e 身份套件通过。

### P1-05 — API Worker Manager、租约、epoch 与可重开状态机（DONE）

需求映射：SESS-001/002/005/009～012、OPS-001/002、AC-003/006/007、ADR-0015/0016。

详细设计：[`tasks/P1-05-api-worker-manager-session-lifecycle-plan.zh-CN.md`](tasks/P1-05-api-worker-manager-session-lifecycle-plan.zh-CN.md)。

交付物：把 P0-06 可复用的 Worker 启动、UDS、安全校验和进程监视迁入 API Worker Manager；删除
旧独立控制面产品进程/入口；API 成为唯一运行期 SQLite 业务访问者；实现 WorkerLease 获取/
续租/回收、epoch fencing、心跳超时、实例级活动 Worker 名额、API 退出时有界 Worker 回收，以及
`CLOSED/CRASHED → STARTING` 的同 Session 重开状态机。Session 创建只物化一次 SessionRoot，
open/close 只获取或释放 Worker。

实现记录：API 已成为运行期唯一 SQLite 业务访问者；Worker Manager、UDS gRPC、bootstrap
凭据、PID/boot ID/start ticks、parent-death、心跳续租、进程退出监视、启动对账和 readiness
屏障已迁入 API，旧独立控制面项目、solution 引用和部署入口已删除。SQLite migration 增加
control-plane/process identity、活动状态约束和 owner/state 索引；SessionRoot 只在 open 前复核，
close/crash 不删除目录，重开递增 epoch 并从上次持久输出序列继续。P1-05 不新增公开 Session
HTTP API；P1-06 通过 Application coordinator 接入。

验证：

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Application.Tests --no-restore --configuration Release \
  --filter 'Category=SessionLifecycle'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore --configuration Release \
  --filter 'Category=SessionLifecycle|Category=WorkerLease'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release \
  --filter 'Category=ProcessIsolation'
```

通过条件：同一 `CLOSED/CRASHED` Session 并发 open 只产生一个有效 Worker；只剩一个活动名额时
并发 open 最多一个成功；旧 epoch 的心跳和结果全部被拒绝；kill Worker 后 Session 在心跳窗口内
变为 CRASHED，并可在旧写权限释放后以更大 epoch 复用同一 SessionRoot 重开；正常/强制终止 API
后 Worker 均在期限内退出，新 API 对账为 CRASHED；无法确认旧 Worker 退出时只冻结对应 Session 的
open/存档写，不影响无关 Session 的 ready 和 open。生产解决方案和容器不再启动旧独立控制面，Worker
不访问 SQLite。

2026-08-11 复核修复：运行期身份在注册等待前由 coordinator 持久化；heartbeat、STOPPING 和终态
写入均校验并返回最新 `state_version` binding；API shutdown 使用真实 binding；终止和启动失败无法
确认退出时保持 fail-closed。新增同 epoch 陈旧版本、无 PID `STARTING` 对账、无法确认退出和停机
binding 测试；Application SessionLifecycle 13 项、Infrastructure SessionLifecycle/WorkerLease 88
项、Worker ProcessIsolation 18 项、API 集成 22 项在 Linux dev Docker 中通过。

### P1-06 — 幂等 Session 创建、开启与关闭纵切（DONE）

需求映射：SESS-001、SESS-005～008、SESS-011/012、AC-001、AC-003、AC-006/007。

详细方案：[`tasks/P1-06-idempotent-session-http-lifecycle-plan.zh-CN.md`](tasks/P1-06-idempotent-session-http-lifecycle-plan.zh-CN.md)。

交付物：创建、列表、详情、open、close HTTP API；创建/open/close 独立幂等键；Game + source
content digest/manifest 固定；API Worker Manager 编排；关闭与输入、open 与存档写的并发语义；
持久 create operation、API-only SessionRoot marker、生命周期命令恢复和停止态 mutation lease。

验证：

```bash
./scripts/dev-up.sh
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release \
  --filter Category=SessionLifecycle'
```

通过条件：相同幂等键并发创建只产生一个 `CLOSED` Session 和一棵 SessionRoot，且不占活动配额；
同一游戏可创建两个隔离 Session；重复 open/close 不重复执行副作用；关闭后输入被拒绝；关闭后、
Worker 崩溃后和 API 重启对账后都能用同一 Session ID/SessionRoot 重开并加载原生存档；Game current
更新不改变重开 Session 的内容；关闭/输入及 open/存档写竞争结果符合设计允许的线性化结果。

2026-08-12 完成记录：新增 `POST /api/v1/sessions`、列表/详情、`:open`、`:close`，接入
API-owned `SessionLifecycleExecutor`、持久幂等状态、create recovery、ETag、CSRF/授权/限流和
稳定错误映射；创建固定 Game current snapshot，通过 copy lease 和 procfs dirfd 完整复制到同文件系统
staging，再发布私有持久 SessionRoot。新增 `session_creation_operations`、
`session_root_mutation_leases` 和 `20260811175156_AddIdempotentSessionLifecycle` migration；
受保护 marker 与 root inode/manifest/config 联合校验，生命周期恢复不依赖进程内 Task；恢复 readiness
现在会等待 lifecycle reconciliation 并确认没有未决 Session 命令。

验证记录：dev Docker 中真实 Kestrel HTTP + Worker Session lifecycle 集成测试通过；全量
`./scripts/check.sh` 通过（Release 0 warning/0 error、API 22、Infrastructure 98、Application 13、
Worker 18、RuntimeAdapter 142、RuntimeCompatibility 27、Web 13），`./scripts/verify-dev-user.sh`、
`./scripts/verify-third-party.sh` 和 `git diff --check` 通过。最后的 hosted-service 生命周期收尾后，
API 集成测试再次以 22/22 通过。API 重启/Worker 崩溃、root 与 `sav/` 两种布局、SQLite/发布故障
注入和生命周期恢复验收已完成；恶意 Worker 文件隔离已由 ADR-0017 移出产品边界。

依据 ADR-0017，API 与 Worker 同一非 root UID 是接受的个人自托管边界，不再以缺少额外内核隔离
阻塞 P1-06，也不宣称抵御恶意 Worker。

### P1-07 — Emuera 运行时语义与完整结构化交互协议（NEXT）

需求映射：SESS-004、PLAY-001～004、PLAY-007～012、COMP-002～009、AC-005/008/009/011/012，
ADR-0004。进入实现前必须新增 ADR，冻结浏览器可安全表达的完整 Emuera Console/Input/Media 能力矩阵、
事件归约语义和明确禁止的桌面/外部能力；不得以无声 no-op、丢字段或普通文本降级冒充兼容。

详细方案：[`tasks/P1-07-emuera-structured-runtime-plan.zh-CN.md`](tasks/P1-07-emuera-structured-runtime-plan.zh-CN.md)。

交付物：

- 逐项审计固定上游 Emuera.EM+EE 的 Console、Input、HTML、图片/Sprite、背景、Shape/CBG、字体与
  布局、动画和音频调用点，建立 `Supported/Compatible/Experimental/Blocked` 机器可校验能力清单；
  MVP 范围内且不违反 `COMP-008` 安全边界的 Emuera 功能必须为 `Supported`，未知或未分类调用必须
  fail closed 并形成可见兼容性诊断；
- 扩展 RuntimeAdapter 与版本化 Worker IPC，使显示模型完整表达文本与前景/背景色、字体族/字号/
  样式、换行和对齐、按钮及 tooltip、临时行更新/删除、图片 source rect、Sprite frame/position/
  z-index、背景图层、Shape/CBG/HTML Island 的安全绘制命令，以及音频 play/stop/channel/volume/loop；
  所有资源只引用 Session runtime manifest 中的逻辑 `assetId`，不传原始 HTML、任意 URL 或宿主路径；
- 完整映射 Emuera 输入类型和语义，包括 Enter/AnyKey、整数、字符串、AnyValue、按钮、OneInput、
  系统输入及允许的指针/键盘输入；保留默认值、约束、输入来源和首个有效回答语义；
- 完整实现 `TINPUT/TINPUTS/TONEINPUT/TONEINPUTS/TWAIT` 等计时输入：Worker 使用单调时钟裁决，
  prompt 携带稳定绝对 deadline、服务端时间基准、`DisplayTime`、默认值和 `TimeUpMes`；浏览器仅按
  deadline 渲染倒计时，不逐秒向服务端发消息，也不替 Worker 提交默认值；超时时原子关闭 prompt、
  应用默认值/等待语义、更新 `ISTIMEOUT`、产生确定的超时显示操作并拒绝迟到输入；断线不暂停计时；
- ConsoleSnapshot 能完整归约上述可见状态、当前 prompt、背景/绘图层和媒体状态；所有新增节点、操作、
  尺寸、层级、资源、文本和更新目标具有硬上限，Worker/IPC/API/浏览器边界均执行一致校验；
- 扩充 v18 与当前 EM+EE 合成 fixture 和真实解释器兼容场景；每个能力项至少覆盖正常路径、边界条件和
  一个失败路径，并更新 runtime baseline、兼容性报告、ADR-0004、上游 `MODIFICATIONS.md` 及中英文文档。

验证：

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore --configuration Release \
  --filter 'Category=ConsoleContract|Category=TimedInput|Category=RichOutput'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore --configuration Release \
  --filter 'Category=RuntimeBridge|Category=EmueraFeatureMatrix'
./scripts/verify-runtime-fixtures.sh
./scripts/verify-emuera-capabilities.sh
```

通过条件：能力清单中不存在未分类、无声 no-op 或 MVP 范围内的非 `Supported` 项；固定 v18 与当前
EM+EE fixture 对所有支持的输入、计时、显示、绘图和媒体语义生成稳定结构化 transcript；计时输入的
输入/超时/取消竞争恰有一个终态，超时默认值、`ISTIMEOUT`、`TimeUpMes`、迟到输入和断线期间继续计时
均与原版语义一致；完整 Snapshot 经序列化、IPC 往返和归约后状态等价；危险 HTML、URL、路径、资源
引用和超限绘图确定性拒绝。只有 `COMP-008` 明确禁止的 DLL、外部进程和不受限网络等能力可以标记为
`Blocked`，且加载时必须向用户报告。

### P1-08 — 完整 Snapshot 重连与有界输出（TODO）

需求映射：SESS-004、PLAY-002、PLAY-004～006、PLAY-010/012、AC-002/012、ADR-0017。

交付物：序列化 P1-07 的完整 Console/Prompt/绘图/媒体 Snapshot 并实施大小上限；实时批次队列；
重连总是取得完整 Snapshot；批处理、背压、序号缺口检测和快照重新同步。不实现历史增量环形缓冲、
ack 补发或 snapshot/subscribe 无丢失屏障。

验证：

```bash
dotnet test tests/CloudEmuera.Realtime.Tests --filter 'Category=Snapshot|Category=Backpressure'
```

通过条件：重连用完整 Snapshot 替换文本、当前 prompt、背景/绘图层和媒体状态；计时 prompt 保留
原 deadline 而不因快照或重连重新计时；快照生成期间持续输出时要么连续应用后续批次，要么检测
缺口并重新同步；慢客户端不会导致无界内存；队列溢出确定性降级到新快照。

### P1-09 — WebSocket 快照恢复与有界输入去重（TODO）

需求映射：AUTH-005、SESS-004、PLAY-006～008、PLAY-011、AC-002/005。

交付物：版本化 WebSocket envelope、鉴权握手、完整快照恢复、当前 Worker 内有界
`promptId/clientMessageId` 去重和多客户端首个有效输入规则。不实现 ack 历史补发、持久输入去重或
控制流断开即退出的收敛由 P1-S01 完成。

验证：

```bash
dotnet test tests/CloudEmuera.Realtime.Tests --filter 'Category=Reconnect|Category=InputDeduplication'
```

通过条件：浏览器断开后取得一致完整 Snapshot 与当前 prompt；断线期间计时输入继续推进，重连只显示
剩余时间，输入与 timeout 竞争只有一个权威结果；当前 Worker 内重复消息只执行一次并返回同一结果；
两个客户端并发回答只有一个 ACCEPTED；Worker IPC 断开后有界退出且 Session 进入 CRASHED；越权
恢复失败。API 重启不恢复旧 Worker 实时连接。

### P1-10 — Session 原生存档文件 API（TODO）

需求映射：SAVE-001～009、SAVE-015、AC-004/007/013/014。

交付物：按 Session 列出和下载原生存档；在 Session 无活动 Worker 时上传、替换、重命名和删除；
路径/大小/基本格式校验；用户和 Session 授权。文件直接位于 SessionRoot，不建立 SaveArtifact 表、
generation、Session 间直接传输 API、签名 URL 或内容级 Game 摘要兼容证明。

验证：

```bash
dotnet test tests/CloudEmuera.Saves.IntegrationTests
```

通过条件：跨用户和跨 Session 访问失败；上传校验大小、路径和基本原生约束；活动 Worker 存在时
所有修改操作被拒绝；操作与 Worker 启动竞争时只有一方成功；API 不解析或改写 Emuera 原生内容。

### P1-11 — 浏览器 Session 控制台和存档界面（TODO）

需求映射：SESS-004～006、PLAY-001～003、PLAY-006～009、PLAY-011、SAVE-003、COMP-007、
AC-005/008/009/011。

交付物：登录、游戏包检查与只读文件查看、Session 列表、Session 创建/open/close/重开、结构化
Console、输入控件、完整 Snapshot 重连状态和存档管理页面；实现 P1-07 定义的全部结构化文本、布局、
按钮、图片/Sprite、背景、Shape/CBG、动画和 WebAudio 渲染，以及全部输入类型、服务端 deadline
倒计时、超时提示和禁用/迟到状态；移动安全区、软键盘、触摸与键盘处理。删除或隐藏 P1-04 已实现的
浏览器文件写入、创建、重命名、删除和搜索入口。

验证：

```bash
pnpm --dir src/CloudEmuera.Web test
pnpm --dir e2e test
```

通过条件：组件测试覆盖每一种 P1-07 节点/操作、输入类型、媒体状态及错误分支；Playwright 在桌面和
移动 viewport 完成登录、发布、创建 Session、普通与计时输入、倒计时期间断线重连、超时继续执行、
Sprite/背景/绘图/音频、存档下载；客户端时钟偏差不改变 Worker 裁决结果；基础键盘导航、媒体控制和
可访问性扫描无阻断错误。

### P1-12 — 基本管理、诊断与就绪检查（TODO）

需求映射：OPS-001、OPS-003～006、AC-007。

交付物：结构化关联日志、Worker/Session 基本状态和最近错误、管理员强制停止、live/ready/version
语义及敏感字段过滤。保留现有关键审计写入，不实现通用审计浏览、专用遥测平台、进程级 CPU/RSS/FD/
磁盘指标或容量规划面板。

验证：

```bash
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
```

通过条件：日志包含 request/session/worker/epoch 关联字段但不含秘密或默认输入全文；数据库、DataRoot、
迁移或启动对账未就绪时 ready 失败但 live 语义正确；管理员终止可审计；基本状态能反映 Worker 崩溃、
Snapshot 大小和队列溢出，且不出现专用遥测或通用审计浏览入口。

### P1-13 — 基础 Worker 进程边界与实例级上限（TODO）

需求映射：SEC、OPS-002、AC-010、ADR-0017。

交付物：生产容器非 root；不挂 Docker socket/宿主密钥/无关路径；Worker 只接收已验证 SessionRoot
与启动 binding；实例级最大活动 Worker、上传/展开/文件数量、存档、Snapshot/队列和最低剩余空间
上限；容器整体 CPU/内存/PID 限制文档。不实现额外的敌对租户内核隔离或进程级资源治理，也不设置
相应 readiness 门。

验证：

```bash
./scripts/test-production-image.sh
./scripts/test-instance-limits.sh
```

通过条件：生产进程非 root且没有敏感宿主挂载；正常 Worker 启动参数不包含 Game workspace/current
或其他 SessionRoot；实例级上限产生稳定错误且队列不无界增长；文档明确同 UID 不提供恶意 Worker
隔离，缺少额外内核隔离能力不影响 ready。

### P1-14 — 单容器生产进程管理与恢复（TODO）

需求映射：MVP 单容器、AC-003/006/007、OPS-003。

交付物：保留已实现的 Migrator 前置检查；API 及其 Worker 子进程启动/停止编排；非 root 用户；
轻量 PID 1、信号转发、parent-death/进程组回收兜底；数据目录停机备份与恢复说明。不新增完整 s6
服务树、在线备份编排或滚动升级。

验证：

```bash
./scripts/test-production-image.sh
./scripts/test-process-recovery.sh
```

通过条件：全新数据卷可启动；进程均非 root；SIGTERM 在期限内停止 Worker 并把活动 Session 标记
CRASHED；强制终止 API 或断开控制通道后 Worker 立即开始退出或被回收；新 API 确认旧写权限释放后完成
CRASHED 对账，同一 Session 可复用原 SessionRoot 重开；单个无法确认的遗留 Worker 只阻止对应
Session 的 open/存档写；生产镜像不存在旧独立控制面服务；备份恢复后元数据和文件校验一致。

### P1-15 — MVP 验收、安全与性能门（TODO）

需求映射：AC-001～014、设计第 17 章全部测试层级。

交付物：需求—测试追踪矩阵；一键验收脚本；Emuera 能力矩阵及结构化交互端到端验收；兼容性、文件
格式/Web 安全、故障注入、容量、移动端和视觉回归报告；明确“可信参与者/可信游戏、无恶意 Worker
隔离”的已知限制清单。

验证：

```bash
./scripts/acceptance-mvp.sh
```

通过条件：AC-001～014 每项至少有一个自动化测试或版本化、可重复的验收场景；脚本从干净 checkout
和空数据卷运行成功；失败报告指出具体 AC 编号；无超出 ADR-0017 已接受信任边界的未解释高危
问题；P1-07 能力矩阵无未分类或 MVP 范围内的非 `Supported` 项，浏览器端到端结果与真实解释器
结构化 transcript 一致；容量结果满足实例级边界或有批准的 ADR 偏差。

## 5. Phase 2/3 进入条件

Phase 2 的资源治理、备份和管理员体验只在 P1-15 通过后展开；Emuera 的 MVP 输入、显示、绘图和
媒体兼容不得推迟到 Phase 2。Phase 3 的
SUSPENDED/RESUMING 和解释器快照必须另立设计与兼容性证明；在此之前不得因当前没有浏览器连接
而释放 Worker，也不得宣称 Worker 崩溃后能从同一指令恢复。

## 6. 近期执行队列

1. ~~完成 P1-04 剩余安全加固：真实 parser-only Validator、dirfd/fsync 内容存储、崩溃恢复/续租、
   copy lease 端口和旧 DataRoot 迁移工具。~~ 已于 2026-08-09 完成，P1-04 标记为 DONE。
2. ~~按 ADR-0015/0016 完成 P1-05：把 Worker 管理迁入 API，移除旧独立控制面，并实现持久
   SessionRoot 的 open/close/reopen 生命周期。~~ 已于 2026-08-11 完成，进入 P1-06。
3. ~~按 ADR-0017 完成 P1-S01：收窄 Game、实例容量、Worker 控制流和基本诊断边界。~~ 已于
   2026-08-12 完成。
4. ~~完成 P1-06 幂等 Session 创建、开启、关闭与恢复验收。~~ 已于 2026-08-12 完成。
5. 按 P1-07 先完成 Emuera 运行时语义、能力矩阵和完整结构化交互协议，再进入 P1-08～P1-15。

每完成一步，更新本文件状态，并在对应 ADR、测试报告或提交说明中记录实际执行命令与结果。未通过当前步骤的验证，不进入依赖它的下一步骤。
