# P1-05：API Worker Manager、持久租约与可重开 Session 详细开发方案

状态：PLANNED

日期：2026-08-11

关联需求：SESS-001、SESS-002、SESS-005、SESS-009～012、OPS-001、OPS-002、
AC-003、AC-006、AC-007

关联决策：[`ADR-0015`](../adr/0015-api-owned-worker-lifecycle.md)、
[`ADR-0016`](../adr/0016-reopenable-session-root-lifecycle.md)、
[`ADR-0007`](../adr/0007-session-root-native-save-ownership.md)

前置任务：P0-06、P1-01、P1-02、P1-04

后续任务：P1-06 Session HTTP 纵切、P1-07 Snapshot、P1-08 Realtime、P1-09 Save、
P1-12 沙箱与资源治理、P1-13 部署收尾

## 1. 目标结果

P1-05 把 P0-06 已证明可行的单 Session Worker 进程、UDS gRPC、启动凭据和进程监视能力迁入
API，建立可供 P1-06 调用的持久 Worker 生命周期内核。完成后产品运行拓扑为：

```text
Browser ──HTTP/WebSocket──> CloudEmuera.Api ──gRPC/UDS──> Worker(session A)
                                  │          ├──────────> Worker(session B)
                                  │          └──────────> Worker(session N)
                                  └── SQLite + DataRoot

CloudEmuera.Migrator ── SQLite（仅 API 启动前）
```

必须同时满足以下结果：

1. 生产解决方案、容器和启动脚本不再包含独立 `CloudEmuera.Supervisor` 进程；API 是运行期唯一
   访问 SQLite 的业务进程，Worker 不引用或打开数据库。
2. 每次 open 只把 `CLOSED` 或已完成回收的 `CRASHED` Session 转为 `STARTING`，原子递增 epoch、
   占用活动配额并建立唯一 WorkerLease；提交事务后才校验目录并启动进程。
3. close 只停止 Worker并释放租约，SessionRoot、Game 绑定、源摘要、manifest、原生存档和用户文件
   均保持不变；同一 Session 可再次 open。
4. Worker 注册、心跳、ready、输出、输入结果和终止事件都绑定
   `controlPlaneInstanceId + sessionId + workerId + epoch`，旧实例或旧 epoch 消息不能改变状态。
5. API 正常退出时有界停止全部 Worker；API 被强制终止时，Worker 由 parent-death 机制退出。新 API
   只有在证明旧写者消失后才把遗留活动 Session 对账为 `CRASHED` 并进入 ready。
6. Worker Manager 不把长生命周期 `DbContext`、请求作用域或仅存在于内存的状态当作事实来源；
   SQLite CAS 是状态和配额的持久裁决，进程注册表只承担当前实例的路由与监视。

## 2. 范围与非目标

### 2.1 本阶段必须实现

- 可重开领域状态机、活动/静止状态分类和状态不变量；
- WorkerLease schema 加固、活动配额原子获取、epoch fencing 和短事务状态存储；
- API 内 Worker Manager、UDS 控制端、进程 launcher、注册/ready/heartbeat/exit 监视；
- Worker IPC v2、控制面实例 fencing、有界断连和 Linux parent-death 保证；
- API 启动对账、正常停机、强制崩溃后的恢复屏障；
- P0-06 Supervisor 可复用代码和测试迁移，以及 Supervisor 产品项目/入口删除；
- 领域、持久化、协议、真实多进程、竞态和故障注入测试；
- 日志、指标、readiness 和安全配置基线。

### 2.2 明确留给后续任务

- P1-06 才公开 Session create/list/detail/open/close HTTP API、授权、CSRF、HTTP 幂等键和审计；
- P1-05 不负责从 Game current content 创建 SessionRoot。测试以预建且标记正确的 `CLOSED`
  SessionRoot fixture 驱动；`CREATING → CLOSED` 的物化纵切由 P1-06 完成；
- P1-07/P1-08 才实现浏览器 Snapshot、WebSocket 恢复、输出回放和 prompt 接入；P1-05 只保留
  Worker 控制流的有界内存路由能力；
- P1-09 才公开原生存档 API；本阶段只提供“活动 Worker 持有目录写权”的互斥事实；
- P1-12 才完成 namespace、cgroup、seccomp、rlimit 和正式资源画像；本阶段不得宣称 Worker 已
  完整沙箱化，但必须交付防孤儿进程所需的最小 parent-death 边界；
- 不支持多 API、多主机、滚动无损重启、API 重启后接管存活 Worker或自动重启崩溃 Session；
- 不恢复解释器指令点。重开始终是同一 SessionRoot 上的冷启动，玩家用游戏原生菜单加载存档。

## 3. 状态模型与持久不变量

### 3.1 正式状态转换

```text
CREATING ──物化成功──> CLOSED
                         │
                  open  │
                         v
CRASHED ──open──> STARTING ──ready──> RUNNING
                    │                    │
                    ├──失败────────────> CRASHED
                    │                    ^
                    └──close──> STOPPING┴──异常退出
                                     │
                                  正常退出
                                     v
                                   CLOSED
```

领域层只允许：

- `CREATING → CLOSED`；
- `CLOSED/CRASHED → STARTING`；
- `STARTING → RUNNING/STOPPING/CRASHED`；
- `RUNNING → STOPPING/CRASHED`；
- `STOPPING → CLOSED/CRASHED`。

重复 open/close 的幂等结果由应用用例处理，不通过“同状态转换”绕过领域规则。自然游戏结束属于
正常 Worker 结束，收敛为 `CLOSED`；进程崩溃、控制面停止、心跳超时、启动失败或强制终止收敛为
`CRASHED`。

### 3.2 状态分类

| 分类 | 状态 | WorkerLease | 占活动配额 | SessionRoot 存在 | 可 open |
| --- | --- | --- | --- | --- | --- |
| 物化中 | `CREATING` | 无 | 否 | 建设中 | 否 |
| 静止正常 | `CLOSED` | 无 | 否 | 是 | 是 |
| 静止异常 | `CRASHED` | 无 | 否 | 是 | 回收屏障完成后是 |
| 启动中 | `STARTING` | 必须有 | 是 | 是 | 否 |
| 运行中 | `RUNNING` | 必须有 | 是 | 是 | 否 |
| 停止中 | `STOPPING` | 必须有，直至确认退出 | 是 | 是 | 否 |

浏览器连接数不是 Session 状态。Realtime Gateway 在内存中维护连接数与最近连接时间；零连接时
Session 仍为 `RUNNING`，不能释放 Worker或活动配额。`CLOSED/CRASHED` 必须同时满足“无有效
lease、无旧 Worker 写者”；只改数据库状态而未完成进程退出不能算静止。

### 3.3 字段语义

- `worker_epoch`：每次成功获取 open lease 时加一，永不回退；启动失败也消耗该 epoch。
- `state_version`：每次 Session 状态或可观察运行字段改变时加一，供 CAS 和后续 HTTP ETag 使用。
- `started_at`：最近一次 Worker ready 的时间；重新 open 后以本轮 ready 时间覆盖。
- `closed_at`：本轮运行失去 Worker并进入 `CLOSED/CRASHED` 的时间；open 获取 lease 时清空。
- `close_reason`：本轮停止/失败的稳定原因码；open 获取 lease 时清空。
- `waiting_for_input/current_prompt_id`：进入 `STARTING`、`STOPPING`、`CLOSED` 或 `CRASHED` 时清空。
- `last_output_sequence`：P1-05 保持单调不减；重开不得复位，后续 Snapshot 以 epoch + sequence
  明确恢复边界。bootstrap 必须把当前值作为 `InitialOutputSequence` 交给 Worker，Worker 的首个新
  输出从更大 sequence 开始；不能因冷启动从 0 重放而违反 Session 级单调性。
- Game、source digest/revision、runtime manifest 和 `session_root_path` 在 open/close 中不可修改。

## 4. 组件与依赖边界

### 4.1 Domain

修改 `CloudEmuera.Domain/Sessions`：

- 更新 `SessionStateMachine`；
- 增加 `IsActive`、`IsQuiescent`、`CanOpen` 等纯状态分类，避免 API、EF 查询和测试各自维护集合；
- 用领域测试冻结全部允许/禁止转换，删除 `ClosedIsATerminalState` 旧断言。

Domain 不知道进程、PID、UDS、EF Core 或活动配额 SQL。

### 4.2 Application

在 `CloudEmuera.Application/Sessions/Runtime` 定义不含 ASP.NET、EF 和 protobuf 类型的端口：

- `ISessionRuntimeStore`：获取 open lease、记录进程身份、确认 ready、续租、开始停止、完成
  closed/crashed、启动对账；
- `ISessionWorkerControl`：启动、请求停止、强制终止和取得当前本地进程事实；
- `ISessionRootRuntimeInspector`：以受保护目录句柄复核 SessionRoot identity、marker、manifest 和
  类型，返回启动所需的只读描述；
- `SessionRuntimeCoordinator`：编排 open/close 的事务与外部副作用补偿，供 P1-06 调用；
- 稳定结果码：`session_not_openable`、`active_session_quota_exceeded`、`worker_start_failed`、
  `worker_registration_timeout`、`worker_stale_epoch`、`worker_exit_unconfirmed`、
  `session_root_invalid`、`control_plane_draining`。

应用端口不得泄漏 `DbContext`、`Process`、gRPC stream 或绝对物理路径给 HTTP 层。

### 4.3 Infrastructure

Infrastructure 实现 `ISessionRuntimeStore` 和 SessionRoot 安全复核：

- 每个方法创建独立短生命周期连接/`DbContext`；
- 需要计数与 CAS 的操作使用 `BEGIN IMMEDIATE`，事务内不得访问文件、等待 IPC 或等待进程；
- 所有写入都以完整 binding 和预期 `state_version` 为条件；
- SessionRoot 继续复用 dirfd、`O_NOFOLLOW`、owner/类型/marker/manifest 校验，不以字符串前缀
  判断目录归属；
- 不把 Worker 控制通道或进程对象放进 Infrastructure persistence。

### 4.4 API Worker Host

在 `CloudEmuera.Api/Workers` 建立 API 内部单例边界：

- `ApiControlPlaneIdentity`：每次 API 启动生成不可复用的 `ctl_...` 身份，记录当前 PID、Linux
  boot ID 和进程 start ticks；
- `WorkerManager`：每 Session 串行副作用、当前 binding 注册表、进程监视、命令路由和 draining；
- `WorkerProcessLauncher`：直接启动 Worker，不经 shell，写一次性 bootstrap，采集 PID identity；
- `WorkerControlGrpcService`：处理 Worker 双向流并在服务边界校验 UDS transport、token、实例与
  binding；
- `WorkerIpcHostedService`：在 API 进程内启动独立的 UDS-only gRPC listener。它共享 API DI 中的
  `WorkerManager`，不拥有数据库或第二套业务 Host；控制服务不得映射到公开 TCP listener；
- `WorkerReconciliationHostedService`：ready 前回收旧 Worker、失效遗留 lease并收敛 Session；
- `WorkerManagerShutdownService`：应用停止时先置 draining，再有界停止/强杀 Worker，最后持久化
  `CRASHED`。

进程事实和流对象可以缓存于内存，但每次注册/终态写入必须重新经过持久 binding CAS。按 Session
使用 keyed async lock 只减少本实例副作用竞争，不替代数据库约束。

### 4.5 Worker

Worker 保持单 Session 独立进程和 `RuntimeAdapter ← EmueraRuntime ← Worker` 依赖，不引用 API、
Application、Infrastructure、EF Core 或 SQLite。P1-05 只调整控制面握手、父进程死亡和断连策略，
不把 Runtime 合并进 API。

## 5. SQLite migration 与事务契约

### 5.1 migration 原则

新增标准 EF migration `AddApiOwnedWorkerLifecycle`，禁止回改已发布的 P1-01/P1-04 migration。
前置 migration `20260811120000_RemoveDetachedSessionState` 已把历史 `DETACHED` 行映射为
`RUNNING` 并从目标 CHECK 删除该状态；P1-05 migration 必须以此为升级起点。升级前继续由
Migrator 独占备份；migration 失败不得留下部分 schema。

### 5.2 sessions 约束

重建 SQLite `sessions` 表以替换以下约束并保留全部数据：

- `closed_at` 在 `CLOSED/CRASHED` 必须非空，在其他状态必须为空；
- `CLOSED/CRASHED/CREATING` 必须 `waiting_for_input = 0` 且 prompt 为空；
- 增加 `(owner_user_id, state)` 索引，供事务内活动配额计数；
- 保留 `(id, worker_epoch)` alternate key 和所有 Game/owner 外键。

P1-05 重建 `sessions` 时继续使用不含 `DETACHED` 的 CHECK，并断言不存在未知状态。已有
`CRASHED` 行以 `last_activity_at` 作为缺失 `closed_at` 的确定性回填值，不得改成 `CLOSED`。旧
`CLOSED`、SessionRoot path、digest、manifest 和 epoch 原值保持。

### 5.3 worker_leases 扩展

在当前 lease 行增加：

| 字段 | 约束与用途 |
| --- | --- |
| `control_plane_instance_id` | 必填 `ctl_` 标识；拒绝旧 API 实例消息 |
| `process_boot_id` | 进程启动后必填，规范 Linux boot UUID |
| `process_start_ticks` | 进程启动后必填，`/proc/<pid>/stat` starttime，抵御 PID 复用 |

`pid/process_boot_id/process_start_ticks` 必须同时为空或同时非空；`ACTIVE/STOPPING` lease 必须具有
完整进程身份。`STARTING` 允许在事务提交到进程启动之间暂时为空。`bootstrap_token` 永不写数据库或
日志。lease 完成后删除当前行，历史通过 Session 状态、稳定 reason 和审计记录表达，不保留一个会
阻塞下一次 open 的 `EXPIRED` 行。

### 5.4 原子获取 open lease

`TryAcquireOpenLease` 是单个 `BEGIN IMMEDIATE` 事务：

1. 读取 Session、用户有效 quota profile 和当前 `state_version`；
2. 要求状态为 `CLOSED/CRASHED`、不存在 lease；
3. 统计同 owner 处于 `STARTING/RUNNING/STOPPING` 的 Session 数；
4. 只有未超过 `max_active_sessions` 才执行
   `worker_epoch = worker_epoch + 1`、`state_version = state_version + 1`、状态 `STARTING`；
5. 清空 closed/prompt 字段，插入相同 epoch、当前 control-plane instance 的 `STARTING` lease；
6. 提交并返回不可变 binding。

并发 open 不依赖“先 count 后 insert”的应用锁。SQLite 写锁、Session CAS、lease 主键/epoch 外键
共同保证同 Session 唯一；配额计数与状态更新必须在同一写事务内，使“最后一个名额”最多被一个
请求获得。

### 5.5 进程启动与 ready

事务提交后依次执行：

1. 安全复核 SessionRoot；
2. 创建权限为 `0700` 的当前实例 runtime/bootstrap 目录和 `0600` 启动文档；
3. 直接启动 Worker，立即读取 boot ID、PID 和 start ticks；
4. 以完整 binding CAS 写入进程身份；若 CAS 失败，立即终止刚启动的 Worker；
5. 等待有期限的注册；注册通过后删除 bootstrap 文件；
6. 发送 `StartRuntime`，等待 Worker `Ready`；
7. 以完整 binding CAS 将 lease 置 `ACTIVE`、Session 置 `RUNNING` 并写最近 `started_at`。

任一步失败都先停止/确认 Worker 退出，再以 binding CAS 删除 lease、置 `CRASHED` 并释放配额。
若无法确认进程退出，则保留活动状态和 lease、将 API readiness 置失败；不得提前发布新写租约。

### 5.6 心跳与续租

- IPC 心跳先验证实例/binding 和进程身份，再更新当前连接的单调时间；
- DB heartbeat 可按配置合并写入，但 `expires_at` 必须始终晚于最大允许丢失窗口；
- DB busy 或写失败不能无限只保存在内存。当持久 lease 无法在到期前续租时进入停止流程；
- watchdog 使用单调时钟判断当前进程存活，使用数据库时间字段供跨重启对账；
- 过期后先终止并确认旧 Worker，再把 Session 置 `CRASHED`。不能先释放 lease 后杀进程。

### 5.7 close 与退出竞争

close 编排的线性化点是把当前 Session/lease CAS 为 `STOPPING`：

1. manager 拒绝该 binding 的新输入命令；
2. 发送带 deadline 的 `StopWorker`；
3. 等待 `WorkerStopped` 和 OS process exit；超时则 kill entire process tree；
4. 只有确认进程退出后才删除 lease；用户 close/自然结束写 `CLOSED`，异常写 `CRASHED`；
5. close 与进程 exit 同时发生时，只有一个匹配 binding 的终态 CAS 成功，另一方读取终态返回。

API 自身停止即使 Worker 优雅退出，也统一记录 `CRASHED/control_plane_stopped`，避免把控制面故障
伪装成用户正常关闭。

管理员 force-stop 复用同一 `STOPPING` 与退出屏障，但可以跳过较长的游戏刷新等待；只有 Worker
在短期限内明确正常响应才可记为 `CLOSED`，超时或强杀一律记为
`CRASHED/admin_force_stopped`。P1-05 交付内部受控命令和审计所需事实，公开授权端点由 P1-06
接入，不能让 HTTP 层直接持有或 kill `Process`。

## 6. IPC v2 与进程存活契约

### 6.1 协议升级

P0-06 的 IPC v1 不支持控制面实例 fencing 和父进程身份，因此本阶段同步升级 bootstrap schema 和
IPC protocol 到 v2。API 与 Worker 同镜像、同版本部署，不提供 v1/v2 滚动兼容；v1 必须以稳定的
`unsupported_protocol_version` 明确拒绝。

保留旧字段号，新增字段只使用未占用且未 reserved 的编号：

- bootstrap：`ControlSocketPath`、`ControlPlaneInstanceId`、`ExpectedParentProcessId`、
  `DisconnectGracePeriodMilliseconds`、`InitialOutputSequence`；
- Worker/command envelope：`control_plane_instance_id`；
- registration：`process_boot_id`、`process_start_ticks`；
- registration result 回显当前 control-plane instance。

清除生产术语中的 `SupervisorEnvelope`、`SupervisorSocketPath` 和
`ValidateSupervisorEnvelope`，改为 `WorkerCommandEnvelope`、`ControlSocketPath` 和 control
envelope validator。proto package/version、bootstrap JSON schema、validator 和测试必须一次提交
保持一致。

### 6.2 注册与消息 fencing

首次注册必须同时匹配：

- 当前协议版本；
- 当前 `controlPlaneInstanceId`；
- `sessionId + workerId + epoch`；
- 一次性 bootstrap token；
- API 实际启动并持久化的 PID/boot ID/start ticks；
- runtime integration version、upstream commit 和 SessionRoot manifest digest。

token 只在首次注册使用，固定时间比较，成功或失败后从内存清除。注册成功后整个 stream 固定为一
个 binding，不允许复用或多路复用。所有后续 envelope 仍携带并校验实例与 binding；旧 stream、旧
epoch、重复 message ID 或 sequence 回退均拒绝且不得写 DB。

### 6.3 UDS 与 bootstrap 安全

迁移并保留 P0-06 的安全性质：

- private runtime directory `0700`、socket/bootstrap `0600`，拒绝 symlink、FIFO、device、错误
  owner/mode 和路径替换；
- socket 位于专用 runtime tree，绝不位于 SessionRoot/Game/DataRoot 可写子树；
- API 仅在 UDS-only listener 映射 WorkerControl；通过公开 TCP 调用相同 gRPC route 必须失败；
- bootstrap 使用安全临时文件、fsync、原子 rename，一次性消费并在注册/超时/退出后清理；
- 日志不输出 token、完整输入、存档内容或不受信任路径原文。

### 6.4 API 异常退出时的 Worker 保证

本阶段 Linux 生产契约采用直接父子关系和 `prctl(PR_SET_PDEATHSIG, SIGKILL)`：

1. API 不经 shell 直接启动 Worker，并在 bootstrap 写入 API 的预期 PID；
2. Worker 在打开 SessionRoot、连接 UDS 或初始化 Runtime 之前安装 parent-death signal；
3. 安装后立即比较 `getppid()` 与预期 PID，覆盖“父进程在 prctl 前已死亡”的竞态；不匹配立即退出；
4. API 正常停止时先走协议优雅关闭；仅父进程异常消失时由内核 SIGKILL 保证不遗留写者；
5. Worker 曾注册后若控制流断开，只允许在配置的短宽限期内连接同一实例；实例变化、明确 stale
   拒绝或宽限期耗尽都退出，不再无限重试。

非 Linux 环境只允许开发诊断，不满足生产 ready。P1-12 引入 namespace/cgroup launcher 时必须
保持“API/受控 launcher 是直接死亡监护者”的契约，并补充 cgroup 整体回收，不能默默破坏本保证。

## 7. API 启动对账与 readiness

API 的业务 HTTP 可启动，但 readiness 在以下屏障完成前保持失败：

1. Migrator 已完成且 schema 版本正确；
2. 新 control-plane instance 和安全 UDS 已建立；
3. 查询所有 `STARTING/RUNNING/STOPPING` 或仍有 lease 的 Session；
4. 对每个 lease 按 boot ID + PID + start ticks 判定原进程：
   - identity 不存在或 PID 已复用：原 Worker 已退出；
   - identity 精确匹配：发送终止、等待退出；
   - `/proc`/权限/身份读取无法给出确定结论：对账失败；
5. 确认退出后，以旧 binding CAS 删除 lease并把 Session 置
   `CRASHED/control_plane_restarted`；
6. 清理仅属于已死亡实例且 identity 验证通过的 bootstrap/socket 残留；
7. 再次查询，确认不存在未归属当前实例的活动 lease，才开放 ready 和 Session open。

PID 数字相同但 boot ID/start ticks 不同视为 PID 已复用，绝不能 kill 新进程。发现数据库状态与 lease
不变量冲突、SessionRoot identity 不匹配或无法确认旧写者消失时，API 保持 live 但 not ready，记录
稳定原因和指标，管理员修复前不能 open Session或执行存档写操作。

## 8. 生命周期算法与补偿表

### 8.1 open 故障窗口

| 故障点 | 已持久事实 | 必须补偿/重启行为 |
| --- | --- | --- |
| 获取 lease 前失败 | 无变化 | 直接返回，不消费 epoch/配额 |
| lease 提交后、校验根目录前崩溃 | `STARTING`，PID 空 | 新 API 对账为 `CRASHED` |
| 根目录/bootstrap/launch 失败 | `STARTING`，PID 空 | binding CAS 删除 lease并 `CRASHED` |
| Worker 启动后、PID 入库前 API 崩溃 | `STARTING`，PID 空 | Worker 由 parent-death 退出；新 API 对账 |
| PID 入库 CAS 失败 | Worker 已启动 | 先 kill/确认退出，再收敛；不得留下未知进程 |
| 注册超时/token 或版本错误 | 完整进程 identity | 终止并确认，随后 `CRASHED` |
| Runtime 初始化失败/ready 超时 | 已注册 Worker | 有界 stop/kill，随后 `CRASHED` |
| ready 到达但状态 CAS 已过期 | stale Worker | 拒绝 ready并终止，不能覆盖新状态 |

### 8.2 运行与停止故障窗口

| 场景 | 期望结果 |
| --- | --- |
| heartbeat stream 中断后同实例及时重连 | 保持相同 binding，不增加 epoch |
| 断连超过宽限或 lease 续租截止 | 停止 Worker，确认退出后 `CRASHED` |
| Worker 自然完成 | 确认 exit 后 `CLOSED/runtime_completed` |
| Worker 非零退出/信号退出 | `CRASHED`，保存安全退出摘要，不记录敏感 stderr |
| 用户 close 与 Worker crash 竞争 | 进程正常响应 stop 才 `CLOSED`；否则 `CRASHED`；单一终态 CAS |
| API SIGTERM | draining、停止所有 Worker、Session 统一 `CRASHED/control_plane_stopped` |
| API SIGKILL | parent-death 清理 Worker；新 API 对账 `CRASHED` |
| 旧 epoch 事件在重开后到达 | 稳定拒绝，不改变新 epoch 状态/sequence/prompt |
| 无法确认旧 Worker 退出 | 保持 lease/配额和 not-ready，不启动替代 Worker |

## 9. 配置、日志与指标

新增 `WorkerManager` 配置节，所有时间和数量有上下限校验：

- Worker assembly/dotnet 路径与 private runtime directory；
- 最大本机 Worker 数（是 quota 之外的系统总上限）；
- registration、runtime-ready、heartbeat、lease、disconnect、graceful-stop、kill-wait 时限；
- bootstrap、命令和事件有界队列容量。

生产默认值必须满足：`heartbeat interval < persisted heartbeat cadence < lease duration`，断连宽限小于
lease duration；测试通过短配置运行，不用长时间 sleep。

结构化日志至少包含 control-plane instance、Session/Worker ID 的安全标识、epoch、状态转换、稳定
reason、PID identity 摘要和 correlation ID。禁止记录 token、输入值、原始游戏输出或任意文件正文。

指标至少包括：当前 Worker 数、各状态 Session 数、open/ready 时延、heartbeat age、lease 续租失败、
强制终止、stale message、启动对账失败和 Worker 非零退出。P1-05 不要求新增管理 UI。

## 10. 逐步实施切片

每个切片应保持可构建、可回归，提交遵循仓库 Conventional Commits 与 DCO；不把整个任务压成一个
不可审查提交。

### 切片 1：领域状态机与契约冻结

- 更新 ADR/需求映射对应的 Domain 状态机和状态分类；
- 补齐 `CLOSED/CRASHED → STARTING`、`CREATING → CLOSED`、禁止跨级转换测试；
- 在 Application 定义 runtime store/control/inspector 端口、binding 和稳定结果码；
- 不接 HTTP，不启动进程。

### 切片 2：SQLite migration 与原子 store

- 添加 migration、model snapshot、约束和索引；
- 实现 open lease 原子获取、进程 identity CAS、ready、heartbeat、begin-stop 和终态收敛；
- 覆盖并发同 Session open、最后活动名额、旧 epoch、DB busy、migration 数据升级；
- 用 fake process control 验证应用 coordinator 的副作用顺序和补偿。

### 切片 3：IPC v2 与 Worker 安全退出

- 升级 proto/bootstrap schema，清理 Supervisor 术语；
- 增加 control-plane fencing、注册进程 identity 和严格 validator；
- 实现 Worker parent-death、预期父 PID 检查和有界断连；
- 先用协议/Worker 组件测试冻结字段号、大小限制、旧版拒绝和竞态。

### 切片 4：迁移 UDS、launcher 与 Worker Manager

- 将 `UnixSocketSecurity`、bootstrap writer、process monitor 和 bounded channel 从 Supervisor 迁入
  API Worker Host，先保持行为等价；
- 建立 UDS-only hosted service、注册表、启动/停止/kill 和 process-exit 回调；
- 接入 coordinator/store，确保进程回调不使用已释放的请求 scope；
- 转移 P0-06 多进程和 UDS 恶意路径测试，删除测试中的 Supervisor fixture 命名。

### 切片 5：启动对账、readiness 与 API shutdown

- 实现 boot ID/PID/start ticks probe、PID reuse 防护和遗留 lease 对账；
- ready 屏障接入现有 API readiness，新增 draining 状态；
- 实现 SIGTERM 有界收敛、registration/heartbeat/kill timeout；
- 增加 SIGKILL API、kill Worker、崩溃窗口和无法确认退出测试。

### 切片 6：删除 Supervisor 产品面

- 删除 `src/CloudEmuera.Supervisor` 项目、solution 引用及其 lock file；
- 删除 Supervisor 容器服务、健康检查、环境变量、启动脚本和发布产物；
- 保留迁移后的 API/Worker IPC 测试，不删除 P0-06 的历史设计记录；
- 架构测试扫描生产项目引用、compose 和镜像，禁止 Supervisor 入口回归；
- 更新 AGENTS、设计、开发计划、协议注释和运维文档的当前拓扑。

### 切片 7：验收与 P1-06 交接

- 完成真实多 Session 并发、重开同一 SessionRoot 和全量故障矩阵；
- 确认 Application coordinator 可被 P1-06 授权/幂等 HTTP 用例调用；
- 明确 P1-06 创建物化接口、P1-07 event sink 和 P1-09 写租约查询的扩展点；
- 运行完整质量门并记录测试数量、平台和已知限制。

## 11. 测试矩阵

### 11.1 Domain

- 全部允许转换逐项为真，未列出的组合全部为假；
- `CLOSED/CRASHED` 可 open，活动态/`CREATING` 不可 open；
- `STOPPING` 仍活动且占配额，`CRASHED` 只有回收完成后静止；
- close/reopen 不改变 Session identity、Game/source/manifest/root 字段。

### 11.2 Persistence 与并发

- 空库 migration、P1-04 带数据升级、`CRASHED.closed_at` 回填、失败 migration 回滚；
- 同 Session 32 个并发 acquire 只有一个 lease/epoch 获胜；
- 两个不同 Session 争最后一个 owner quota 只有一个成功；
- 旧 state version、worker ID、epoch、control-plane instance 的 heartbeat/ready/exit 全部 CAS 失败；
- PID identity 全空/全有约束、PID reuse、boot ID 变化、lease/session 状态不一致被拒绝；
- heartbeat、close、process exit 和 open 竞争不丢更新，事务期间不等待外部副作用。

### 11.3 IPC 与 Worker

- v2 正常注册；v1、未知版本、错 token、错实例、错 PID identity、错 manifest 拒绝；
- 注册后 token 被清理，stream 不能改绑，多余/超大/乱序 envelope 被拒绝；
- 同实例短断连可重连，超过宽限自行退出，新实例明确拒绝后立即退出；
- 父 PID 在 `prctl` 前死亡、安装后死亡两条路径均在打开 Runtime 前/后按契约退出；
- 公开 TCP 无法调用 WorkerControl，UDS owner/mode/symlink/path 替换攻击继续覆盖。

### 11.4 真实多进程 WorkerLifecycle

- `CLOSED → STARTING → RUNNING → STOPPING → CLOSED` 后同 SessionRoot、同存档再次 open，epoch +1；
- kill Worker 后在 heartbeat/exit deadline 内 `CRASHED`，再次 open 使用相同 root 和更大 epoch；
- 并发 open 实际只产生一个 OS Worker；并发不同 Session 不串流、不共享 bootstrap；
- registration/ready timeout、Runtime 初始化失败、非零退出、stop 超时/强杀均正确补偿；
- 在 open 每个故障注入点终止 API，重启后无孤儿 Worker且状态可确定收敛；
- API SIGTERM 与 SIGKILL 后所有直接子 Worker 在期限内消失；SessionRoot inode/文件内容保留；
- 模拟无法读取旧进程 identity 或无法 kill 时 API not-ready，且 open 不创建新 lease/进程。

### 11.5 架构回归

- solution、生产 Dockerfile/compose/script 不包含 Supervisor 项目或进程；
- API 不引用 `CloudEmuera.EmueraRuntime` 或 Upstream；Worker 不引用 EF/SQLite/Application；
- Migrator 不与 API 并发运行；运行期只有 API 打开业务 SQLite；
- production code 不残留 `SupervisorEnvelope`/`SupervisorSocketPath` 等旧拓扑术语；历史 ADR/任务
  文档允许保留并标注已取代。

## 12. 验证命令

所有命令通过 dev Docker，先注入宿主 UID/GID：

```bash
./scripts/dev-up.sh

source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Domain.Tests --no-restore \
  --configuration Release --filter 'Category=Concurrency|Category=SessionLifecycle'

docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore \
  --configuration Release --filter 'Category=Migration|Category=WorkerLease'

docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore \
  --configuration Release --filter 'Category=WorkerLifecycle|Category=IpcSecurity'

docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore \
  --configuration Release --filter 'Category=WorkerLifecycle'

./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
./scripts/dev-down.sh
```

实际测试项目/Category 若在实现中调整，必须同步本节和
`docs/development-plan.zh-CN.md`，不能留下不可执行命令。

## 13. 完成定义

P1-05 只有在以下条件全部满足时才能标记 DONE：

1. 领域状态机允许同一 `CLOSED/CRASHED` Session 重开，并有穷举自动化测试；
2. 活动配额、epoch 和 lease 由数据库短事务/CAS 保证，并发最后名额测试稳定通过；
3. API Worker Manager 能真实启动、注册、监视、停止和强杀多个独立 Worker；
4. 所有 Worker 事件都经过 control-plane instance + binding fencing，旧 epoch 无法改变新运行；
5. 正常/强制 API 终止均无遗留 Worker；启动对账无法证明安全时 ready 失败且不授予新写租约；
6. close/crash/restart 后 SessionRoot identity 和内容保持，并可用更大 epoch 再次 open；
7. Supervisor 产品项目、部署入口和运行配置已删除，API 不加载 Runtime，Worker 不访问 SQLite；
8. 正常、边界、主要失败、竞态、恶意 UDS/bootstrap 和故障注入路径都有自动化覆盖；
9. `./scripts/check.sh`、用户权限校验、第三方校验和 `git diff --check` 全部通过；
10. 文档、migration、model snapshot、IPC 版本、配置示例和历史任务替代说明同步更新。

## 14. P1-06 交接契约

P1-05 完成后，P1-06 只能在其上增加产品用例，不重新实现 Worker 生命周期：

- create 负责授权、幂等地物化 SessionRoot并完成 `CREATING → CLOSED`；
- open 在授权和 HTTP 幂等检查后调用 `SessionRuntimeCoordinator.OpenAsync`；
- close 在授权和 HTTP 幂等检查后调用 `SessionRuntimeCoordinator.CloseAsync`；
- HTTP 取消只取消尚未线性化的请求，不能中断已经提交的 Worker 生命周期补偿；
- API 返回可以超时为“处理中”，但后台 coordinator 必须继续收敛到确定状态；
- Save 与 Realtime 后续模块只消费当前 binding/lease 和事件端口，不直接操作进程或自行写
  Session 状态。
