# CloudEmuera 可验证开发计划

状态：Draft v0.1  
更新日期：2026-08-04  
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
curl --fail http://localhost:8080/health/live
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

### P0-02 — 平台端口与 RuntimePaths（DONE）

需求映射：SAVE-002、SAVE-011～014、Phase 0 运行时要求。

交付物：`IGameConsole`、`RuntimePaths`、文件系统、时钟、图像和音频端口；路径值对象和 SessionRoot 布局构造器；不引用 WinForms/GDI+ 的 RuntimeAdapter 核心。

验证：

```bash
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=RuntimePaths|Category=Architecture'
```

通过条件：根目录与 `sav/` 两种布局映射测试通过；绝对路径、`..`、符号链接逃逸和跨 Session 路径被拒绝；架构测试证明 Domain/Application/RuntimeAdapter 核心不引用 WinForms；伪时钟可确定性推进超时输入。

2026-08-04 验证记录：在 Linux 开发容器（.NET 10 SDK）中完成 `RuntimePaths|Architecture` 专项测试 53 项、RuntimeAdapter 测试程序集全量 79 项（其中 FixtureContract 26 项）；覆盖生产 `TimeProviderRuntimeClock` deadline smoke test、手动时钟 deadline 累计 elapsed，以及 `sav/` 嵌套目录的创建、读取、写入、元数据和枚举一致性。架构测试直接检查程序集引用，禁止 `System.Drawing`、`System.Drawing.Common`、NAudio 和 `CloudEmuera.Application`，并检查公共 API 的字段、事件、构造函数和继承关系。`scripts/check.sh` 通过（Release 构建 0 警告/0 错误，后端与 Web 检查通过），`scripts/verify-runtime-fixtures.sh`、`scripts/verify-third-party.sh` 和 `git diff --check` 均通过。符号链接与 FIFO 逃逸用例在 Linux 上实际创建并拒绝；Windows/其他平台仍覆盖词法、规范路径和程序集架构约束，实际重解析点发布门保留给 Linux CI。

### P0-03 — 结构化 Console 与输入端口（DONE）

需求映射：PLAY-001～003、PLAY-007～010。

交付物：文字、样式、换行、按钮、图片和输入提示的最小结构化事件模型；单调 sequence；`promptId` 输入接口；有界内存 ConsoleSnapshot 原型。

验证：

```bash
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=ConsoleContract'
```

通过条件：固定运行时调用生成稳定 transcript；sequence 严格递增；未知/危险 HTML 不能产生脚本或任意 URL；历史超过上限后仍能生成一致快照；过期 prompt 被拒绝。

2026-08-04 验证记录：在 Linux 开发容器（.NET 10 SDK）中完成 `ConsoleContract` 专项 36 项、RuntimeAdapter 全量 115 项、Domain 4 项；覆盖并发 Emit 精确 `1..N`、估算字节与 receipt cache 上限、generator 分配和调用者 ID 拒绝、旧 ID 淘汰后的复用/迟到输入回归、rollover 后 snapshot+deltas 归约、HTML 注入 limits 与属性/URL 攻击、invalid/valid 输入并发、取消/超时/有效输入竞态及 timeout 后迟到输入。`./scripts/check.sh` 通过（Release 构建 0 警告/0 错误，Web typecheck、测试和 production build 通过），`./scripts/verify-third-party.sh` 与 `git diff --check` 通过。

### P0-04 — 无 UI Runtime 运行到 INPUT（NEXT）

需求映射：COMP、SESS-003/004、PLAY-001、AC-008/009。

交付物：直接集成的固定上游解释器源码、CloudEmuera headless integration project、来源/修改记录；无 UI runtime harness；从测试 GameVersion 启动、输出、停在 INPUT、接受输入并继续执行的流程。

验证：

```bash
./scripts/test-runtime-compat.sh --scenario input-roundtrip
```

通过条件：v18 与 EM+EE 两套资产均在无显示服务器环境运行；输出 transcript 与基线一致；相同输入得到确定结果；运行期间不存在 WinForms 窗口、桌面会话或 API 进程内状态依赖。

### P0-05 — Emuera 原生存档双布局（TODO）

需求映射：SAVE-004、SAVE-010～015、AC-013/014。

交付物：根目录 `save*.sav/global.sav` 与 `sav/` 布局适配；Session 私有物化目录；临时文件加原子替换；原生保存/加载兼容 harness。

验证：

```bash
./scripts/test-runtime-compat.sh --scenario save-root
./scripts/test-runtime-compat.sh --scenario save-directory
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=SaveIsolation|Category=CrashConsistency'
```

通过条件：两种布局均能保存、退出、重新启动并加载原值；两个用户和同一用户两个 Session 的物理路径及内容互不影响；中途终止提交后旧 generation 完整可读且不存在半写目标文件。

### P0-06 — 单 Session Worker 与 IPC 冒烟链路（TODO）

需求映射：SESS-002/010、OPS-004、Phase 0 进程隔离。

交付物：Worker 启动配置、版本握手、启动/输入/输出/关闭 gRPC 消息、Unix Domain Socket 权限、每进程单 Session 强制约束。

验证：

```bash
dotnet test tests/CloudEmuera.Ipc.ContractTests
dotnet test tests/CloudEmuera.Worker.IntegrationTests --filter 'Category=ProcessIsolation'
```

通过条件：契约序列化和未知字段测试通过；错误协议版本被明确拒绝；第二个 Session 不能进入同一 Worker；API 测试进程退出不导致 Worker 立即退出；关闭请求后 Worker 在限定时间内退出。

## 4. Phase 1：端到端单机 MVP

### P1-01 — SQLite 首版 schema 与迁移（TODO）

需求映射：核心领域模型、GAME-004/010、SESS-005/007、SAVE-005、OPS-005。

交付物：users、games、game_versions、sessions、worker_leases、save_artifacts、idempotency_records、audit_events migration；并发 token、唯一索引和 UTC 时间约定。

验证：

```bash
dotnet test tests/CloudEmuera.Infrastructure.Tests --filter 'Category=Migration|Category=PersistenceConstraint'
```

通过条件：空库可升级到最新版本；已有样例库升级后数据不丢失；重复内容哈希、租约唯一性和幂等键约束由数据库强制；失败 migration 不留下部分 schema。

### P1-02 — 本地身份、资源授权与审计（TODO）

需求映射：AUTH-001～005、OPS-005、AC-004。

交付物：本地账户、密码哈希、cookie/session 安全配置、玩家/管理员策略、资源所有权授权器、敏感操作审计；`ADR-001` 记录未来 OIDC 触发条件。

验证：

```bash
dotnet test tests/CloudEmuera.Api.IntegrationTests --filter 'Category=Authentication|Category=Authorization'
```

通过条件：匿名访问受保护端点返回 401；跨用户读取/修改返回 403 或不可枚举的 404；管理员操作写入审计；WebSocket 升级和恢复重新鉴权；日志不包含密码或输入全文。

### P1-03 — 安全游戏包摄取（TODO）

需求映射：GAME-001～003、GAME-007、AC-010。

交付物：流式上传、配额、隔离暂存、安全解包、编码检测、内容哈希和诊断模型；恶意归档语料库。

验证：

```bash
dotnet test tests/CloudEmuera.GamePackages.Tests --filter 'Category=ArchiveSecurity|Category=Encoding'
```

通过条件：正常 Shift-JIS/UTF-8 包可摄取；路径穿越、绝对路径、符号/硬链接、大小写或 Unicode 碰撞、压缩炸弹和超配额输入被拒绝；失败输入不会在目标目录留下文件。

### P1-04 — 草稿编辑与不可变发布（TODO）

需求映射：GAME-004～010。

交付物：Game/GameVersion API、目录浏览、文本读取编辑、搜索、草稿和发布流水线、运行时清单、引用保护和逻辑删除。

验证：

```bash
dotnet test tests/CloudEmuera.Api.IntegrationTests --filter 'Category=GameVersioning'
```

通过条件：发布产生内容寻址的只读版本；编辑已发布文件只能创建草稿/新版本；活动 Session 固定版本内容不变；仍被引用版本不能物理删除；并发发布不会产生两个相同版本身份。

### P1-05 — Supervisor、租约、epoch 与状态机（TODO）

需求映射：SESS-001/002/005/009/010、OPS-001/002、AC-006/007。

交付物：Worker Supervisor、WorkerLease 获取/续租/回收、epoch fencing、心跳超时、活动配额和状态转换持久化。

验证：

```bash
dotnet test tests/CloudEmuera.Supervisor.IntegrationTests
dotnet test tests/CloudEmuera.Domain.Tests --filter 'Category=Concurrency'
```

通过条件：同一 Session 并发启动只产生一个有效 Worker；只剩一个活动名额时并发请求最多一个成功；旧 epoch 的心跳和结果全部被拒绝；kill Worker 后 Session 在心跳窗口内变为 CRASHED。

### P1-06 — 幂等 Session 创建与关闭纵切（TODO）

需求映射：SESS-001、SESS-005～008、AC-001、AC-006。

交付物：创建、列表、详情、关闭 HTTP API；幂等键；GameVersion 固定；Supervisor 编排；关闭与输入并发语义。

验证：

```bash
dotnet test tests/CloudEmuera.Api.IntegrationTests --filter 'Category=SessionLifecycle'
```

通过条件：相同幂等键并发创建只产生一个 Session；同一游戏可创建两个隔离 Session；重复关闭成功且不重复执行副作用；关闭后输入被拒绝；关闭/输入竞争结果符合设计允许的两种线性化结果。

### P1-07 — Snapshot、恢复屏障与有界输出（TODO）

需求映射：PLAY-004～006、PLAY-010/012、AC-002/003/012、ADR-003。

交付物：`ADR-003`；ConsoleSnapshot 序列化；短期增量环形缓冲；恢复屏障；批处理、背压和快照降级策略。

验证：

```bash
dotnet test tests/CloudEmuera.Realtime.Tests --filter 'Category=Snapshot|Category=Backpressure'
./scripts/test-realtime-load.sh
```

通过条件：快照生成期间持续输出仍无缺口、重复或乱序；慢客户端不会导致无界内存；超过增量窗口时确定性降级到新快照；负载报告证明内存保持在 ADR 上限内。

### P1-08 — WebSocket 恢复与输入去重（TODO）

需求映射：AUTH-005、PLAY-006～008、PLAY-011、AC-002/003/005。

交付物：版本化 WebSocket envelope、鉴权握手、断线恢复、ack、`promptId/clientMessageId` 去重和多客户端首个有效输入规则。

验证：

```bash
dotnet test tests/CloudEmuera.Realtime.Tests --filter 'Category=Reconnect|Category=InputDeduplication'
```

通过条件：断开后从最后 ack 恢复且无丢失窗口；重复消息只执行一次并返回同一结果；两个客户端并发回答只有一个 ACCEPTED；API 重启后可重新发现 Worker 并恢复连接；越权恢复失败。

### P1-09 — SaveArtifact 管理与隔离 API（TODO）

需求映射：SAVE-001～009、SAVE-015、AC-004/007/013/014。

交付物：启动物化、原子提交、列出、上传、下载、重命名、复制和软删除 API；校验和与 generation；用户/游戏/Session 隔离。

验证：

```bash
dotnet test tests/CloudEmuera.Saves.IntegrationTests
```

通过条件：跨用户和跨 Session 访问失败；上传校验大小、路径和版本；并发提交不会覆盖较新 generation；Worker 提交中止后旧存档可下载；显式复制产生独立物理文件。

### P1-10 — 浏览器游戏库、Session 控制台和存档界面（TODO）

需求映射：GAME-006、SESS-005/006、PLAY-002/009、SAVE-003、AC-011。

交付物：登录、游戏库、上传/编辑/发布、Session 列表、结构化 Console、输入控件、重连状态和存档管理页面；移动安全区和键盘处理。

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

通过条件：Worker 不能读取其他 Session、写 GameVersion、访问宿主路径、创建禁止的进程或任意联网；CPU/内存/磁盘超限产生明确状态；目标 Docker 配置缺少必需沙箱能力时服务不进入 ready。

### P1-13 — 单容器生产进程管理与恢复（TODO）

需求映射：MVP 单容器、AC-003/006/007、OPS-003。

交付物：生产镜像中的 API、Supervisor、Migrator 启动/停止编排；非 root 用户；信号转发；数据目录迁移、备份与恢复说明。

验证：

```bash
./scripts/test-production-image.sh
./scripts/test-process-recovery.sh
```

通过条件：全新数据卷可启动；进程均非 root；SIGTERM 在期限内停止并刷新状态；单独重启 API 不终止 Worker；重启 Supervisor 可对账；备份恢复后元数据和文件校验一致。

### P1-14 — MVP 验收、安全与性能门（TODO）

需求映射：AC-001～014、设计第 17 章全部测试层级。

交付物：需求—测试追踪矩阵；一键验收脚本；兼容性、安全、故障注入、性能、移动端和视觉回归报告；已知限制清单。

验证：

```bash
./scripts/acceptance-mvp.sh
```

通过条件：AC-001～014 每项至少有一个自动化测试或版本化、可重复的验收场景；脚本从干净 checkout 和空数据卷运行成功；失败报告指出具体 AC 编号；无未解释的高危安全问题；性能结果满足需求文档边界或有批准的 ADR 偏差。

## 5. Phase 2/3 进入条件

Phase 2 的资源治理、备份、管理员体验和更完整媒体兼容，只在 P1-14 通过后展开。Phase 3 的 SUSPENDED/RESUMING 和解释器快照必须另立设计与兼容性证明；在此之前不得让 `DETACHED` 释放 Worker，也不得宣称 Worker 崩溃后能从同一指令恢复。

## 6. 近期执行队列

1. 执行 P0-04/P0-05，证明无 UI 输入和两种原生存档；
2. 执行 P0-06，形成首个跨进程端到端切片。

每完成一步，更新本文件状态，并在对应 ADR、测试报告或提交说明中记录实际执行命令与结果。未通过当前步骤的验证，不进入依赖它的下一步骤。
