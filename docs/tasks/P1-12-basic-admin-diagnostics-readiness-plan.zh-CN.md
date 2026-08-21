# P1-12 基本管理、诊断与就绪检查实施方案

状态：已完成

日期：2026-08-21

实施记录：已完成真实管理运行时查询、Worker/Session/Realtime 聚合诊断、可恢复且幂等的管理员强制停止、
live/ready/version 契约、SQLite/DataRoot/recovery 就绪探针、关联日志与敏感字段过滤，并补齐管理页、
Game Block 入口和跨层回归测试。最终仓库验证结果记录在本文末尾。

关联需求：AUTH-002/004、SESS-002/007/009～012、OPS-001、OPS-003～006、SEC-009、
NFR-002～005、NFR-010、AC-003/006/007

关联决策：ADR-0015、ADR-0016、ADR-0017、ADR-0020、ADR-0021、ADR-0026

前置任务：P1-02、P1-04～P1-11、P1-S01、P1-S03

后续任务：P1-13 基础 Worker 进程边界与实例级上限、P1-14 生产容器与备份升级、P1-15 MVP 验收

## 1. 目标结果

P1-12 面向由部署者为自己及其信任参与者运行的单机自托管实例，交付一套最小但闭环的故障发现、
定位和人工恢复能力。它不是监控平台，也不把可信参与者重新当作敌对租户。完成后必须得到：

1. 管理员能够从一个真实的运行状态页查看 API、持久 Session、当前 Worker 和 Realtime 的基本事实，
   包括 Worker ID/PID/epoch、最近心跳、最近错误、Snapshot 大小和队列溢出诊断；页面不再使用硬编码
   示例数据。
2. 管理员能够对 `STARTING`、`RUNNING` 或 `STOPPING` Session 发起带原因、CSRF、幂等和审计的强制
   停止。操作必须复用 Session command gate、当前 binding 和 Worker 退出屏障；HTTP handler 不直接
   持有或终止 `Process`。
3. 强制停止只在确认目标 Worker 退出后释放 lease 和 SessionRoot 写权，并把 Session 收敛为
   `CRASHED/admin_force_stopped`。Session ID、SessionRoot、原生存档和 Game 内容保持不变；完成退出屏障后
   可用更大的 epoch 重新 open。
4. `/health/live`、`/health/ready` 和 `/api/v1/version` 具有固定、可测试且不泄漏内部路径的语义。
   readiness 同时覆盖数据库、migration、DataRoot、启动 operation recovery 和 Worker 对账；单个无法
   确认写者的异常 Session 只隔离自身，不使整个实例失去 ready。
5. API、Worker 生命周期和 Realtime 关键路径使用结构化关联日志。相关上下文可按 `requestId`、
   `sessionId`、`workerId` 和 `workerEpoch` 串联，同时默认不记录密码、Cookie、token、输入值、
   Snapshot/game text、SessionRoot 或本地 IPC 路径。
6. 保留已有身份、Game、Session、存档和管理员操作审计；P1-12 只补足强停审计和现有审计回归，
   不增加通用审计查询 API/UI。
7. 管理员阻止 Game 的已有能力得到完整入口和回归验证：Blocked Game 不能创建或重新开启 Session，
   但既有 SessionRoot 和存档不被修改或删除。

P1-12 的产品价值是“出问题时看得见、停得住、保得住数据、能够重新开启并能从日志追查”，不是为
资源计量、告警运营或容量规划建立基础设施。

## 2. 决策优先级与范围

### 2.1 约束优先级

实现时按以下顺序解释已有文档：

1. 当前需求文档；
2. ADR-0017 的可信参与者简化边界；
3. ADR-0015/0016 的 API-owned Worker、epoch、退出屏障和可重开 SessionRoot；
4. ADR-0020/0021/0026 的 API 输出镜像、瞬时连接诊断和 committed display 状态；
5. P1-05/P1-09 的已实施细节。

P1-05 早期文本中“任一遗留 Worker 无法确认就使全局 not-ready”的表述已被 ADR-0017 取代。当前规则是：
对应 Session 保留 lease 并禁止 open/存档写，管理状态显示 `WRITE_FENCE_UNCONFIRMED`；只要控制面整体完成
启动对账，其他 Session 仍可服务，实例可以 ready。数据库、migration、DataRoot 或恢复器整体未就绪仍然
使 ready 失败。

### 2.2 本任务交付

- 管理员运行状态 HTTP 读模型和 React 页面；
- 管理员 Session force-stop HTTP 命令、幂等、审计和 UI；
- 数据库/migration/DataRoot readiness 及现有 health/version 契约补全；
- 请求、Session/Worker 和 Realtime 结构化日志关联；
- 集中的日志敏感字段策略和回归测试；
- Game block 管理入口和 create/open 强制门回归；
- 后端、真实多进程、前端和 OpenAPI 契约测试。

### 2.3 明确不做

- 不引入 OpenTelemetry、Prometheus、Grafana、时序数据库或专用遥测代理；
- 不采集或展示每 Worker CPU、RSS、FD、磁盘 IO、网络、输出速率历史；
- 不建设趋势图、容量预测、告警规则、邮件/消息通知或 SLA 页面；
- 不增加通用审计列表、搜索、导出或审计管理 UI；
- 不提供浏览器实时日志、任意进程信号、任意 PID kill、IPC 命令控制台或在线 SQLite/文件修复入口；
- 不自动重启 `CRASHED` Worker，不自动删除异常 SessionRoot，不把强停伪装成正常 close；
- 不持久化 WebSocket 连接、Snapshot、队列或进程资源指标；
- 不建立按用户资源配额、调度、计费或敌对租户沙箱；
- 不在 health endpoint 中执行昂贵全库完整性检查、递归 DataRoot 扫描或 Worker 探测；
- 不更改 Realtime/IPC major，不从 WebSocket 增加隐藏管理命令；
- 不为只读管理状态增加新的数据库表或 migration；现有 `sessions`、`worker_leases`、
  `audit_events` 和内存注册表足以形成投影。

## 3. 当前基线与实现缺口

### 3.1 可直接复用

- `Program.cs` 已公开 `/health/live`、`/health/ready` 和 `/api/v1/version`；ready 已接入身份 bootstrap、
  Worker 对账、Session creation operation recovery 和 save operation recovery。
- `SqliteSessionRuntimeStore` 已持久化当前 WorkerLease、PID/start identity、heartbeat、epoch、状态和稳定
  close reason，并以 CAS 拒绝 stale binding。
- `SessionRuntimeCoordinator.CloseAsync`、`ISessionWorkerControl` 和 `SessionLifecycleExecutor` 已提供
  begin-stop、短/长 deadline、kill、进程退出确认和终态收敛基础。
- `WorkerManager.Workers`、`ApiWorkerSession` 已提供当前进程 ID、注册/ready/exit、最近心跳、最近输出
  sequence、丢弃 pending event 数和 `SessionOutputHub`。
- `SessionOutputHub` 已提供 epoch、状态、sequence、订阅数、resync/fault/encoding 计数；P1-12 只增加
  无业务副作用的诊断快照，不改变输出归约或连接背压算法。
- `RealtimeConnectionRegistry` 已提供全局连接/订阅数和逐 Session 瞬时订阅事实；它不写 SQLite。
- `audit_events`、`HttpAuditContext` 和多个已实施业务审计动作可复用。
- 管理员 Game block API 已存在；P1-12 不重新设计 Game 状态，只补 UI 和端到端门控证明。
- Web 已有 `/admin` 路由和视觉骨架，但当前健康卡片与 Worker 行是硬编码数据。

### 3.2 必须补齐

- 独立的管理员诊断 Application port/Contracts DTO 和 owner-neutral 查询；普通 `SessionView` 继续不暴露
  PID、lease、进程或内部诊断。
- 把数据库权威事实、Worker Manager 内存事实、Hub 和连接注册表按 binding 安全合并的查询算法。
- Snapshot 当前编码字节数、soft/hard overflow 等只读诊断访问器；不得为管理查询保存历史 frame 或
  每连接 payload。
- 正式 `POST /api/v1/admin/sessions/{sessionId}:force-stop` 端点、持久幂等和管理员审计。
- `Force=true` 成功取得并结束目标 binding 后统一收敛为 `CRASHED/admin_force_stopped`；当前“Worker
  在短期限内正常响应时可能写 CLOSED”的内部行为必须修正，避免违反 AC-007 和误导玩家。
- 数据库/migration/DataRoot health check，以及结构化的 ready 响应；现有 live 不增加依赖。
- 统一 requestId 生成/传播、日志 scope、稳定 event ID 和敏感字段过滤。
- 真实数据驱动的管理页、force-stop 确认交互、Game block 管理入口和错误/刷新状态。

## 4. 分层与组件归属

```text
React AdminPage
      │ GET /api/v1/admin/workers
      │ POST /api/v1/admin/sessions/{id}:force-stop
      ▼
Admin HTTP adapter ── auth/admin + CSRF + idempotency + requestId
      ▼
CloudEmuera.Application.Administration
  ├─ IAdminRuntimeQuery
  └─ IAdminSessionCommandService
      ├─ persistent Session/WorkerLease reader
      ├─ ISessionCommandGate / SessionLifecycleExecutor
      ├─ IAdminRuntimeDiagnostics (API adapter)
      └─ audit/idempotency stores
             │
             ├─ WorkerManager + SessionOutputHub
             └─ RealtimeConnectionRegistry
```

### 4.1 Domain

Domain 不新增“健康”“连接”“管理员 Worker”实体。只在现有 Session 状态机测试中明确
`AdminForceStop` 的结果是 `CRASHED`，并保持：

- 目标 binding 必须是当前 binding；
- 进入 `STOPPING` 后拒绝新输入；
- 未确认进程退出不得清 lease 或授予新写权；
- `CRASHED` 保留 SessionRoot 且允许退出屏障后的显式 reopen。

### 4.2 Application

新增 `CloudEmuera.Application/Administration`：

- `IAdminRuntimeQuery.GetAsync(AdminRuntimeQuery, CancellationToken)`：读取管理员运行状态；
- `IAdminSessionCommandService.ForceStopAsync(CurrentActor, AdminForceStopCommand, CancellationToken)`：
  执行授权后的强停用例；
- `IAdminRuntimeDiagnostics.GetSnapshotAsync(...)`：抽象 API 宿主瞬时 Worker/Hub/Realtime 事实，使 Application 不依赖
  `Process`、ASP.NET Core 或具体内存注册表；
- `IAdminRuntimeStore.ReadAsync(...)`：由 Infrastructure 读取 Session、Game、owner、lease 和最近异常；
- `AdminForceStopCommand(sessionId, idempotencyKey, reason)`；
- 管理查询和命令结果使用 Application record，不直接复用 EF row 或 API DTO。

管理员授权仍由 API/Application 的 `CurrentActor.IsAdmin` 和 `IResourceAuthorizer` 强制执行。查询不得先读取
目标资源再判断角色；非管理员统一返回 `404` 或现有管理员端点约定的 `403`，并由契约测试冻结一致行为。

### 4.3 Infrastructure

新增 `SqliteAdminRuntimeStore`，使用短生命周期 `CloudEmueraDbContext` 和 `AsNoTracking`：

- 查询当前活动状态 `STARTING/RUNNING/STOPPING` 及其 lease；
- 查询最近 `CRASHED` Session，默认 20 条、绝对上限 100 条，按 `closed_at DESC, id DESC`；
- 一次投影 owner username、Game name、Session 状态/版本、close reason 和 lease 字段，避免 N+1；
- 不返回 `session_root_path`、`ipc_endpoint`、process boot ID、start ticks、manifest 或用户 email；
- 查询只读，不刷新 heartbeat、不修改 lease、不触发 recovery。

现有 `SqliteIdempotencyStore`/`ApiIdempotencyStore` 增加管理员强停 scope。审计继续写现有
`audit_events`，不新增表。

### 4.4 API 宿主诊断适配器

新增 `ApiAdminRuntimeDiagnostics`，在短临界区内分别取得不可变快照后再组合：

- `WorkerManager.Workers.ToArray()`；
- 每个 `ApiWorkerSession` 的 binding、PID、注册/ready/exit、last heartbeat/output、pending/dropped 计数；
- `SessionOutputHub` 的不可变诊断 record；
- `RealtimeConnectionRegistry` 聚合出的全局和逐 Session 订阅数。

禁止在持有 WorkerManager、Hub 或 registry 锁时访问 SQLite、等待编码或执行进程操作。管理查询不是
一致性事务：响应带顶层 `observedAt`，每行以数据库 binding 为权威，并允许以下显式状态：

| 合并结果 | `runtimeConsistency` | 含义 |
| --- | --- | --- |
| DB lease 与同 binding 内存 Worker 一致 | `MATCHED` | 正常活动 Worker |
| DB 有 lease，内存尚无 Worker | `LEASE_ONLY` | 启动窗口、启动失败补偿或遗留隔离 |
| 内存 Worker 已退出但 DB 尚未收敛 | `EXIT_OBSERVED` | watchdog/CAS 正在处理 |
| 内存有不同 epoch/worker | `STALE_IN_MEMORY` | 只报告并由现有 fencing/回收处理，不把它当当前 Worker |
| 静止态无 lease/Worker | `STATIC` | `CLOSED/CRASHED` 正常静止事实 |

查询不得为“看起来更整齐”而修改数据库，也不得把瞬时不匹配升级为新的 Session 状态。

## 5. 管理员运行状态 HTTP 契约

### 5.1 Endpoint

```http
GET /api/v1/admin/workers?recentFailureLimit=20
Cookie: <admin session>
```

沿用详细设计中的 `/admin/workers` 路径，但响应同时包含当前 Worker 和最近异常 Session，避免为了一个
单机诊断页增加多个列表端点。响应 `Cache-Control: no-store`，仅管理员可访问，不接受 owner/game 任意
搜索。`recentFailureLimit` 默认 20，范围 `1..100`。

### 5.2 响应结构

```json
{
  "schemaVersion": 1,
  "observedAt": "2026-08-21T12:00:00Z",
  "instance": {
    "controlPlaneState": "READY",
    "activeWorkerCount": 2,
    "webSocketConnectionCount": 3,
    "subscriptionCount": 2
  },
  "workers": [
    {
      "session": {
        "id": "sess_...",
        "name": "周目二",
        "ownerUsername": "player",
        "gameId": "game_...",
        "gameName": "Example",
        "state": "RUNNING",
        "stateVersion": 8,
        "lastActivityAt": "2026-08-21T11:59:58Z"
      },
      "worker": {
        "workerId": "wrk_...",
        "pid": 123,
        "workerEpoch": 3,
        "leaseStatus": "ACTIVE",
        "heartbeatAt": "2026-08-21T11:59:58Z",
        "heartbeatAgeMilliseconds": 2000,
        "registered": true,
        "ready": true,
        "processExited": false,
        "lastOutputSequence": 1052
      },
      "realtime": {
        "hubState": "LIVE",
        "snapshotSequence": 1052,
        "snapshotBytes": 184320,
        "snapshotSizeStatus": "KNOWN",
        "subscriptionCount": 1,
        "resyncCount": 0,
        "softOverflowCount": 0,
        "hardOverflowCount": 0,
        "faultCount": 0,
        "droppedPendingEventCount": 0
      },
      "runtimeConsistency": "MATCHED"
    }
  ],
  "recentFailures": [
    {
      "sessionId": "sess_...",
      "sessionName": "初见流程",
      "ownerUsername": "player",
      "gameId": "game_...",
      "gameName": "Example",
      "workerEpoch": 2,
      "failedAt": "2026-08-21T11:20:00Z",
      "reasonCode": "heartbeat_timeout"
    }
  ]
}
```

约束：

- 所有枚举均为封闭大写值，DTO 带 `schemaVersion=1`；未知必需值由 Web fail closed；
- `heartbeatAgeMilliseconds` 由服务端以同一 `observedAt` 计算并夹到非负，不由浏览器猜测服务器时间；
- `snapshotBytes` 是当前 Hub Snapshot 成功 source-generated JSON 编码后的实际 UTF-8 字节数；尚无初始
  Snapshot 为 `NOT_READY/null`，编码失败为 `FAILED/null`。读取可复用 Hub single-flight 缓存，不能复制
  payload 到响应或保存历史；
- 管理查询触发的 Snapshot 编码仍受现有 `SnapshotMaxBytes`，但诊断读取本身不得触发
  `FaultReported`、回收 Worker 或改变 Hub 状态；超限只返回 `FAILED/null` 并记录安全 reason。之后正式
  subscription/resync 编码失败时仍沿用既有 Hub fail-closed 路径。UI 默认 10 秒轮询并在页面不可见时
  暂停，避免高频编码；
- soft/hard overflow 是 Hub 生命周期内单调计数，Worker epoch 更换即归零；不落 SQLite；
- `recentFailures.reasonCode` 只返回稳定 code，不返回异常、stderr、路径或游戏文本；
- 当前 Worker stderr 的 `ProcessDiagnostics` 不进入 HTTP 响应。它只作为经过过滤且有界的服务端日志诊断；
- 不返回 connection ID、用户 ID、Cookie、输入、prompt 文本或客户端网络地址。

### 5.3 读取竞态

组合顺序固定为：

1. 取得 `observedAt`；
2. 读 SQLite 权威 Session/lease 投影；
3. 取得 Worker/Hub/registry 瞬时快照；
4. 仅按 `(sessionId, workerId, epoch, controlPlaneInstanceId)` 合并；
5. 对查询期间已经变化的 binding 标记 consistency，不重试成跨系统事务。

若单个 Hub 在读取时 dispose，保留数据库行并返回 `hubState=DISPOSED`；若数据库整体不可读，端点返回
`503 SERVICE_NOT_READY`，不能返回仅基于内存拼出的“健康”视图。

## 6. 管理员强制停止

### 6.1 HTTP 契约

```http
POST /api/v1/admin/sessions/{sessionId}:force-stop
Idempotency-Key: <opaque 8..128 chars>
X-CSRF-TOKEN: <token>
Content-Type: application/json

{
  "reason": "游戏持续无响应，按参与者请求终止"
}
```

成功返回最新普通 `SessionResponse`，状态必须是 `CRASHED`、`closeReason=admin_force_stopped`。若操作已
线性化但仍在等待退出，可按现有生命周期命令约定返回 `202`；同一幂等键查询/重试最终回放相同终态。

`reason` 去除首尾空白后必须为 1～500 个 Unicode scalar，拒绝 NUL、CR/LF 和控制字符。它只用于审计和
管理员确认界面，不写普通 Session `close_reason`，不进入结构化日志 message；日志只记录
`reasonProvided=true`。

稳定错误映射：

| HTTP | code | 条件 |
| --- | --- | --- |
| 400 | `VALIDATION_FAILED` | reason/body 非法 |
| 400 | `IDEMPOTENCY_KEY_REQUIRED` | 缺少或非法幂等键 |
| 400 | `CSRF_VALIDATION_FAILED` | CSRF 失败 |
| 401 | `UNAUTHENTICATED` | 未登录 |
| 403 | `PASSWORD_CHANGE_REQUIRED` | 账户仍需改密 |
| 404 | `SESSION_NOT_FOUND` | Session 不存在或按防枚举策略不可见 |
| 409 | `SESSION_NOT_ACTIVE` | 目标不处于活动生命周期 |
| 409 | `SESSION_TRANSITION_IN_PROGRESS` | 另一生命周期命令已线性化 |
| 409 | `IDEMPOTENCY_KEY_REUSED` | 同键异请求 |
| 409 | `STALE_WORKER_EPOCH` | 恢复时发现 Session 已有更新 Worker |
| 503 | `SERVICE_NOT_READY` | 控制面整体尚未就绪 |
| 503 | `WORKER_EXIT_UNCONFIRMED` | 无法证明目标 Worker 已退出，写 fence 保留 |

错误 message 使用安全固定文案；进程、SQLite 或路径异常只通过 requestId 与服务端日志关联。

### 6.2 授权和可见性

处理顺序：认证 → 必须改密检查 → 管理员角色 → CSRF → body/idempotency 校验 → Session 当前事实。
普通玩家不能通过拥有 Session 获得强停权限。不存在 Session 与无管理员权限遵循现有资源防枚举约定。

允许目标状态：

- `STARTING/RUNNING`：正常进入强停；
- `STOPPING`：相同幂等命令等待/回放；不同命令返回 `409 SESSION_TRANSITION_IN_PROGRESS`；
- `CLOSED/CRASHED`：不发进程命令。相同幂等键回放；新命令返回 `409 SESSION_NOT_ACTIVE`，避免把一个
  历史静止态伪造成“刚刚强停成功”；
- `CREATING`：返回 `409 SESSION_NOT_ACTIVE`，由 creation recovery 负责。

### 6.3 幂等和审计顺序

管理员强停使用独立 scope `ADMIN_SESSION_FORCE_STOP`，request digest 包含规范化 `sessionId + reason`。
同一键异值返回 `409 IDEMPOTENCY_KEY_REUSED`。执行顺序：

1. 在短事务中预留幂等记录，并读取当前 Session/binding；
2. 在执行外部进程副作用前追加 `ADMIN_SESSION_FORCE_STOP_REQUESTED` 审计，包含管理员、Session、目标
   `workerId/epoch`、受限 reason 和 requestId；该记录证明操作意图已被控制面接受；
3. 经共享 `ISessionCommandGate` 线性化 `BeginStopping`；从此拒绝新输入，后续过程使用
   `CancellationToken.None`，不因浏览器断开中止；
4. 请求短 deadline stop；无论 Worker 是否在 deadline 内协作退出，管理员 force-stop 的业务终态统一为
   `CRASHED/admin_force_stopped`；未退出则 kill process tree 并再次等待；
5. 只有 OS exit identity 已确认后才 CAS 完成 Session、删除/失效 lease 和释放活动名额；
6. 追加 `ADMIN_SESSION_FORCE_STOP_COMPLETED` 或 `ADMIN_SESSION_FORCE_STOP_FAILED` 审计并完成幂等记录。

如果 API 在步骤 2～6 间崩溃，requested 审计和 IN_PROGRESS 幂等记录保留。启动 Worker 对账先确认旧写者
退出并把 Session 收敛为 `CRASHED`；幂等 recovery 按以下事实完成：

- Session 已是 `CRASHED/admin_force_stopped` 且 epoch 等于目标：完成成功回放；
- 相同目标 binding 仍为活动：重新进入受控终止；
- Session 已以更大 epoch reopen：不得终止新 Worker，把旧命令完成为 `409 STALE_WORKER_EPOCH`；
- 无法证明目标 Worker 退出：保持 IN_PROGRESS/失败诊断和对应 Session 写 fence，不释放 lease。

不得只依赖“POST 本身看起来幂等”而省略持久记录，也不得在 kill 成功后因 HTTP cancellation 丢失审计。

### 6.4 与普通 close 的区别

| 属性 | 玩家 close | 管理员 force-stop |
| --- | --- | --- |
| 权限 | Session owner/admin | 仅 admin |
| 等待策略 | 先给游戏正常刷新/退出窗口 | 短 stop 窗口后 kill |
| 终态 | 正常为 `CLOSED` | 始终为 `CRASHED` |
| close reason | `user_closed`/正常原因 | `admin_force_stopped` |
| reason | 不要求自由文本 | 必填且审计 |
| SessionRoot | 保留 | 保留 |
| reopen | 同 ID、epoch+1 | 退出屏障后同 ID、epoch+1 |

## 7. Game Block 管理闭环

P1-12 不改变 `Game.status=BLOCKED` 或现有 block API。需要完成：

- Game 详情页只对管理员显示“阻止运行/解除阻止”，操作带 If-Match、CSRF、确认和现有审计；
- owner 即使是普通玩家也不能解除管理员 Block；
- `POST /sessions` 在复制 SessionRoot 前读取并 CAS 复核 Game 仍为 ACTIVE；
- `POST /sessions/{id}:open` 必须通过 Session 的 `gameId` 重新读取 Game 当前 status；Blocked 时拒绝，
  即使该 SessionRoot 创建于 Block 之前；
- Block 不停止当前已运行 Worker，不删除或修改既有 SessionRoot/存档。管理员若要立即停止，另行使用
  force-stop；
- Unblock 后既有 `CLOSED/CRASHED` Session 可按原快照 reopen，不把新的 Game current content覆盖进去。

UI 必须明确说明“阻止后禁止创建/重新开启，不自动结束当前运行，也不删除存档”。不增加安全例外策略
中心或批量强停功能。

## 8. Health、readiness 与 version

### 8.1 Liveness

`GET /health/live` 保持匿名、轻量，只证明 Kestrel/API 事件循环能响应：

- 不访问 SQLite、DataRoot、Worker、网络或 recovery 状态；
- 正常返回 `200 { "status": "LIVE" }`；
- 数据库损坏、磁盘满、单个/全部 Worker 崩溃时仍应 live；
- 不输出异常详情、路径、版本或配置。

### 8.2 Readiness 组件

`GET /health/ready` 聚合以下稳定命名检查：

| check | Healthy 条件 | 失败 reason code |
| --- | --- | --- |
| `identity_bootstrap` | bootstrap 已完成 | `identity_bootstrap_pending/failed` |
| `database` | 可打开 DB、外键启用、短 `BEGIN IMMEDIATE` + no-op update + rollback 成功 | `database_unavailable/not_writable` |
| `schema` | migration history 等于当前 binary 支持版本，不高不低 | `schema_migration_required/schema_newer_than_binary` |
| `data_root` | 根存在、owner/type 合法、受保护临时文件 create/fsync/unlink 成功 | `data_root_unavailable/not_writable` |
| `data_root_space` | 可用空间不低于 `MinDataRootFreeBytes` | `data_root_low_space` |
| `worker_runtime` | UDS 建立且全局启动对账循环已完成 | `worker_reconciliation_pending/failed` |
| `session_operation_recovery` | creation/lifecycle recovery 首轮完成 | 稳定现有 code |
| `save_operation_recovery` | save recovery 首轮完成 | 稳定现有 code |

数据库检查不得调用 `Database.Migrate()`；schema 只读 `schema_migrations` 并与编译进 binary 的 migration
集合比较。昂贵 `quick_check`/`foreign_key_check` 继续由 Migrator `check` 执行，不放进每次 HTTP health。

DataRoot probe 只在受保护的固定 health 子目录使用随机文件，逐段 no-follow，写少量固定字节并执行 file/
directory fsync 后删除；不得扫描或清理用户目录。并发 probe 严格有界并可使用短 TTL single-flight，避免
每个编排器轮询都产生写放大。

响应：

```json
{
  "status": "READY",
  "checks": [
    { "name": "database", "status": "HEALTHY", "reason": "ready" }
  ]
}
```

任一整体组件失败返回 `503 NOT_READY`。checks 使用固定顺序和稳定 reason，不返回 exception message、
SQLite SQL、DataRoot/DB/UDS 路径或 free byte 精确值。详细异常只进入经过过滤的服务端日志。

单个 Session 的 `WRITE_FENCE_UNCONFIRMED` 不使 `worker_runtime` 失败：它出现在管理员运行状态和日志，
并只阻止该 Session 的 open、force-stop completion 和存档写。Worker Manager 自身 monitor/reconciliation
循环停止或 UDS 不可用才是全局 not-ready。

### 8.3 Version

`GET /api/v1/version` 保持匿名、`Cache-Control: no-store`，返回：

- product/version/commit（存在构建元数据时）；
- .NET runtime；
- HTTP API schema；
- Realtime envelope 和 payload schema；
- Worker IPC major；
- Runtime integration version 和固定 upstream commit；
- 当前数据库 schema compatibility version。

不返回 DataRoot、数据库文件、UDS、Worker assembly、容器用户名、环境变量、capacity 数值或 bootstrap
配置。Contracts 使用 source-generated JSON/golden fixture 冻结；Web 设置页或管理状态页可以展示版本，
但不需要轮询。

## 9. 结构化日志与敏感字段策略

### 9.1 输出格式和 requestId

- 生产使用 `Microsoft.Extensions.Logging` JSON Console 写 stdout/stderr；开发可保持易读 console，测试
  直接捕获结构化 state，不解析渲染文本；
- 每个 HTTP 请求由服务端生成/采用 ASP.NET Core `TraceIdentifier` 作为唯一 requestId，并在
  `X-Request-ID`、统一错误体和日志中复用；不信任或直接采用任意客户端 header；
- HTTP middleware 建立 `requestId/method/route` scope；进入 Session 用例后增加 `sessionId`，取得 binding
  后增加 `workerId/workerEpoch`；无关请求不伪造空 Worker 字段；
- Worker Manager 保留 `controlPlaneInstanceId` 但不把 bootstrap token、完整 PID identity 或路径写日志；
- 使用稳定 `EventId + eventName + result/reasonCode + durationMs`，异常对象只在服务端 Error 日志中附加。

至少冻结这些事件族：

```text
http.request.completed
health.ready.failed
session.open/close/force_stop.started|completed|failed
worker.spawned|registered|ready|heartbeat_timeout|exit|fenced|kill_unconfirmed
realtime.resume|resync|queue_overflow|slow_consumer_close
game.blocked|unblocked
```

不得把高频 heartbeat 逐条记 Information；正常 heartbeat 只更新状态，超时、stale 或 persistence failure 才
记录。不得把 Snapshot/每条 display frame 正常路径逐条记 Information。

### 9.2 禁止字段

默认所有级别均不得记录：

- password/password hash、Cookie、Authorization、CSRF、security stamp、bootstrap token；
- input value/normalized value、button value、pointer/key payload、prompt/game text；
- Snapshot JSON、display payload、存档内容、上传包内容；
- SessionRoot、Game/DataRoot 绝对路径、UDS/bootstrap 路径；
- 未过滤的 query、header、request/response body、stderr/stdout；
- email、客户端 IP、connection ID、clientMessageId 等非排障必需的高基数/隐私标识。

允许字段限于稳定 ID、安全枚举、计数、字节数、sequence、epoch、状态、时长和不可逆短哈希。
`ProcessDiagnostics` 继续做长度和敏感值替换；P1-12 必须把它从拼接 message 改为受控结构化字段或仅在
Debug 下输出过滤摘要，防止 Worker stderr 把游戏文本带入 Information 日志。

### 9.3 集中策略

新增小型 `SensitiveLogPolicy`/`SafeDiagnosticText`，只接受明确 allowlist 字段和最大长度，不尝试依赖
“在最终字符串里搜索 password”作为主要防线。HTTP body、Realtime value 和 Worker原始输出在调用 logger
之前就不得进入参数。日志测试使用 canary secret/input/path，覆盖成功、验证失败、异常和 timeout 路径。

## 10. 审计

P1-12 保留已有 append-only `audit_events`，不公开查询端点。必须验证：

- 身份 bootstrap、用户变更、Game create/update/block/unblock/activate、Session 生命周期、存档
  import/rename/delete 等已实施审计没有因管理读模型重构丢失；
- force-stop 至少有 REQUESTED 和 terminal COMPLETED/FAILED 记录；
- action、resource、actor、result、reasonCode、requestId 使用稳定值；
- metadata JSON 有 schema/version 或稳定字段，只包含目标 workerId/epoch、受限管理员 reason 和结果 code；
- 审计中不保存密码、token、Cookie、输入、Snapshot、绝对路径或异常堆栈；
- 管理状态 GET、health、version 和普通只读请求不写审计。

审计写失败时不得继续执行尚未开始的强停副作用；若 terminal 审计失败发生在 Worker 已退出之后，
Session/lease 正确性优先，记录 Error 并由 requested 记录与幂等 recovery 补齐 terminal 事实，不能重新启动
或再次 kill Worker。

## 11. Web 管理界面

### 11.1 运行状态页

用真实 API 替换现有硬编码 `/admin`：

- 顶部四个摘要：API/ready、Worker Manager、活动 Worker、Realtime 连接/订阅；
- 主表显示 Session/owner/Game、状态、Worker ID/PID/epoch、心跳年龄、Hub/Snapshot、订阅数和最近错误；
- 最近异常区显示最近 20 个 `CRASHED` Session 的稳定原因和时间，并提供进入 Session 详情/存档页；
- 默认 10 秒刷新，页面 hidden 时暂停；提供显式刷新按钮和“观测于”时间；
- 请求失败保留上一份成功数据并明确显示“数据可能过期”，不把失败改画成健康；
- `LEASE_ONLY/STALE_IN_MEMORY/WRITE_FENCE_UNCONFIRMED` 用文字和图标提示，不只依赖颜色；
- 小屏改为卡片/横向可读布局，不要求展示完整内部 ID，可复制但默认缩写；
- 非管理员路由和 API 都拒绝访问，前端隐藏不是授权边界。

不绘制 CPU/内存图表，不显示完整异常、路径或进程 stderr。

### 11.2 Force-stop 交互

- 只对可强停状态显示危险操作；
- modal 明确说明进度可能未保存、Session 将标记 CRASHED、SessionRoot/存档不会删除；
- reason 必填，客户端和服务端使用相同长度/控制字符约束；
- 首次提交生成幂等键并持有到确定回执，网络重试复用同一键；
- pending 时禁用重复按钮但允许关闭 modal，后台请求不据此取消服务端操作；
- 成功后刷新运行状态和对应 Session Query cache；失败显示稳定错误，不自动换新幂等键重试；
- 操作后不自动 reopen，管理员/owner 必须显式检查状态和存档再开启。

### 11.3 Game Block

管理员 Game 详情显示 block/unblock action、影响说明和确认。Block 后 UI 立即使创建/open 入口不可用，
但最终裁决仍在 API。普通 owner 只能看到被阻止状态和说明，不能看到解除按钮。

## 12. 测试方案

所有测试名称或 trait 注释映射 OPS/AUTH/SESS/AC 编号。

### 12.1 Contracts/OpenAPI

- admin runtime response camelCase、封闭枚举、nullable 字段和 `schemaVersion=1` golden JSON；
- force-stop request/response、必需 Idempotency-Key、CSRF 和统一错误体；
- health live/ready JSON、稳定 check 顺序/reason；version 各协议字段；
- OpenAPI 包含管理员端点且不包含 audit、metrics、任意 kill/IPC 控制端点；
- 响应不出现 path、token、process identity、input、Snapshot payload 或 connection 明细。

### 12.2 授权与输入校验

- 未登录、强制改密、PLAYER、ADMIN 分别访问 admin GET/POST；
- PLAYER 即使拥有目标 Session 也不能 force-stop；
- 不存在和越权行为不泄漏 Session；
- 缺失/非法 CSRF、idempotency key、空/超长/控制字符 reason；
- 同键同请求回放，同键异 reason 冲突。

### 12.3 管理读模型

- `STARTING/RUNNING/STOPPING` 与 lease/内存 Worker 正常合并；
- `CRASHED` 最近错误按稳定顺序和 limit 返回；
- lease-only、exit-observed、stale epoch、Hub awaiting/faulted/disposed；
- heartbeat age 使用注入 TimeProvider，wall clock 倒退不产生负数；
- Snapshot 未编码、成功编码、超限/失败；soft/hard overflow 和 dropped event 计数；
- 多连接/订阅聚合正确，连接断开后减少，不写 SQLite；
- 查询竞态不抛出、不修改 Session、不接受 stale Worker 为当前事实；
- SQLite 不可用时返回 503，不基于内存伪造成功。

### 12.4 Force-stop 领域、持久化与并发

- RUNNING/STARTING 强停：先 STOPPING/拒绝输入，再确认退出，最终 CRASHED；
- Worker 在短 deadline 内协作退出仍是 `CRASHED/admin_force_stopped`；
- stop 超时后 kill process tree；kill 后退出确认前不清 lease；
- kill/identity 无法确认时保留写 fence，不能 reopen 或写存档；
- force-stop 与 input、owner close、Worker 自然退出、heartbeat timeout、并发 open 逐切点竞争；
- 两个管理员不同 idempotency key 并发最多一个目标 binding 执行副作用；
- stale target epoch 不能终止新 Worker；
- HTTP cancellation 发生在 BeginStopping 前/后：前者无副作用，后者后台完成；
- REQUESTED 审计早于进程副作用，COMPLETED/FAILED 与最终事实一致；
- API 在 requested、begin-stop、cooperative exit、kill、DB completion、terminal audit 各窗口崩溃后的恢复；
- SessionRoot inode/manifest、Game current 和存档字节在强停前后不被 API 修改；随后同 Session reopen
  使用更大 epoch。

### 12.5 Health/version

- live 在 DB 不可用、DataRoot 只读/低空间、recovery pending、Worker crash 时仍为 200；
- ready 在 database/schema/DataRoot/space/全局 Worker runtime/recovery 各组件失败时为 503；
- 单个 ambiguous lease 只隔离该 Session，ready 仍成功；全局 monitor/UDS 失败则 not-ready；
- DataRoot probe 的 create/fsync/unlink、链接/owner/type 异常和并发 single-flight；
- health 不调用 migrate/quick_check、不递归扫描、不泄漏 exception/path；
- version 与 Contracts、Realtime、IPC、RuntimeBaseline 和 migration compatibility 常量一致。

### 12.6 日志和审计隐私

为 password、Cookie、CSRF、bootstrap token、input/button value、prompt/game text、Snapshot、SessionRoot、
UDS 和 Worker stderr 注入唯一 canary，捕获结构化 logger state 和渲染文本，证明所有成功/失败/异常路径
均不出现 canary。验证：

- requestId 在响应 header、错误体、HTTP 日志、审计一致；
- Session/Worker 相关事件具有正确 ID/epoch，无关请求不伪造字段；
- heartbeat/display 正常热路径不产生 Information 日志风暴；
- force-stop reason 只在受限 audit metadata，不出普通日志；
- read admin/health/version 不产生 audit；force-stop 和 Game block 产生预期 audit。

### 12.7 Web 与可访问性

- AdminPage loading/success/stale/error/empty/lease-only/recent-failure；
- 轮询、页面 hidden 暂停、unmount cleanup，不并发堆积请求；
- PLAYER 不渲染 admin route/action；直接访问仍由 RequireAdmin 处理；
- force-stop modal 焦点圈闭、Escape/取消、必填 reason、pending、同键 retry、成功 cache invalidation；
- Game block 说明和 action；
- 移动宽度、键盘操作、状态不只依赖颜色。

### 12.8 真实多进程与验收

在 dev Docker 启动真实 API/Worker：

1. 创建并 open Session，管理页看到真实 PID/epoch/heartbeat/Snapshot；
2. 让游戏等待输入，从管理员端 force-stop；验证 WebSocket 结束、进程退出、Session CRASHED、目录与
   存档保留、审计存在；
3. reopen 同一 Session，确认 PID/workerId 改变、epoch 增大并可加载原生存档；
4. 注入 Worker 忽略短 stop，证明 kill 和退出屏障；
5. 注入慢连接/小队列阈值，管理状态出现 overflow，而其他连接和 Session 不受影响；
6. 模拟单个旧 Worker identity 无法确认，证明该 Session 被 fence、其他 Session/ready 正常；
7. Block Game，证明新 create/reopen 被拒绝、运行中 Worker不被自动停止、Unblock 后旧 SessionRoot 可用。

## 13. 实施切片与提交边界

每个切片一个逻辑提交，提交使用 `git commit -s` 和规定的 Conventional Commit 格式。

### 切片 1：冻结管理契约与失败测试

- 新增 Administration Application contracts/ports、HTTP DTO、JSON source-generation、OpenAPI/golden；
- 冻结 admin workers response、force-stop request/result 和错误码；
- 先写非管理员、敏感字段和 placeholder UI 失败测试。

建议提交：`feat(admin): define runtime diagnostics contracts`

### 切片 2：只读管理投影

- 实现 `SqliteAdminRuntimeStore`、`ApiAdminRuntimeDiagnostics` 和安全合并；
- 扩展 Hub/queue 只读诊断计数与 Snapshot byte size；
- 实现 `GET /api/v1/admin/workers` 和并发/隐私测试。

建议提交：`feat(admin): expose bounded runtime diagnostics`

### 切片 3：强停用例与恢复

- 修正 force terminal state；
- 实现管理员命令、command gate、幂等 scope、REQUESTED/terminal audit 和 crash recovery；
- 接入 POST endpoint，完成真实 Worker/epoch/退出屏障测试。

建议提交：`feat(admin): add audited session force stop`

### 切片 4：Health/version 与日志基线

- 加入 database/schema/DataRoot/space health checks 和结构化 response；
- 冻结 liveness、单 Session 隔离和全局 readiness 语义；
- 补齐 version；建立 requestId scope、JSON console、EventId 和集中敏感字段策略；
- 移除/降级可能携带 Worker stderr/game text 的 Information message。

建议提交：`feat(ops): harden readiness and correlated logging`

### 切片 5：真实管理 UI 与 Game Block

- 扩展 Web API client/type；以真实数据替换硬编码 AdminPage；
- 实现轮询、异常/过期状态、force modal 和 Game block action；
- 完成组件、可访问性和移动布局测试。

建议提交：`feat(web): implement runtime administration`

### 切片 6：故障注入、文档与验收

- 完成多进程 force-stop/reopen、ready、日志 canary、队列 overflow 和 Game block 脚本/测试；
- 更新 OpenAPI、README/部署排障说明、需求追踪和开发计划实施记录；
- 运行完整检查。

建议提交：`test(admin): verify diagnostics recovery workflow`

## 14. 验证命令

所有命令通过 dev Docker 和 `scripts/lib/dev-env.sh`，不直接使用宿主 SDK：

```bash
./scripts/dev-up.sh

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Domain.Tests --no-restore --configuration Release \
  --filter "Category=SessionLifecycle"'

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore --configuration Release \
  --filter "Category=AdminDiagnostics|Category=WorkerLifecycle|Category=Health"'

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release \
  --filter "Category=AdminApi|Category=Health|Category=LoggingPrivacy|Category=OpenApi"'

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release \
  --filter "Category=WorkerLifecycle|Category=AdminForceStop"'

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm web \
  sh -c "pnpm install --frozen-lockfile && pnpm typecheck:web && pnpm test:web && pnpm build:web"'

./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
./scripts/dev-down.sh
```

若实际测试项目或 trait 名称不同，在切片 1 中先按仓库现有测试拓扑修正文档和命令，不能通过宿主
`dotnet`/`pnpm` 绕开容器验证。

## 15. 完成条件

以下全部满足才可把 P1-12 标记 DONE：

1. `/admin` 不再包含硬编码 Worker、版本或健康数据；管理员能查看真实活动和最近异常 Session；
2. 管理响应包含 PID/epoch/heartbeat/reason、Snapshot size、overflow 和瞬时连接聚合，且不暴露敏感
   路径、输入、payload、connection/user 明细；
3. PLAYER 无法读取诊断或强停任何 Session；ADMIN 强停要求 CSRF、reason 和幂等键；
4. 强停复用 command gate/binding/退出屏障，任何协作或 kill 路径最终均为
   `CRASHED/admin_force_stopped`；未确认退出不释放写权；
5. force-stop 在 HTTP 断开、并发、API 崩溃和 stale epoch 下可恢复且不误杀新 Worker；requested 和
   terminal 审计完整；
6. 强停不修改/删除 SessionRoot、Game current 或原生存档；同 Session 可用更大 epoch reopen；
7. live/ready/version 契约完整；database/schema/DataRoot/space/全局 recovery 失败使 ready 失败，单个
   fenced Session 不使全局 not-ready；
8. 结构化日志能关联 request/session/worker/epoch，错误体、header、日志和审计 requestId 一致；
9. canary 测试证明密码、Cookie/token、输入、Snapshot/game text、存档/路径和原始 Worker output 不泄漏；
10. Blocked Game 不能 create/reopen，当前 Worker和既有 SessionRoot/存档不被隐式破坏；
11. 没有新增 audit browser、metrics endpoint、进程资源图表、任意 kill/IPC 命令或自动重启；
12. OpenAPI、Contracts、前端类型、部署排障文档和开发计划同步；
13. `./scripts/check.sh`、`verify-dev-user.sh`、`verify-third-party.sh` 全部通过。

## 16. 后续任务交接

- P1-13 只实现非 root 生产运行、父子进程/PID 1 信号、容器整体限制和实例级上限，不把本任务诊断
  扩张成每 Worker 资源指标平台；
- P1-14 使用 live/ready/version 编写部署、备份和升级说明，不要求在线备份编排或滚动升级；
- P1-15 复用本任务 force-stop/reopen 和 health 故障注入完成 AC-003/007，不从外部 `kill -9` 绕过
  正式管理员用例作为唯一验收；
- 若未来改变为敌对多租户、增加多 API/多主机或需要告警/历史容量，必须新增 ADR，不在本任务 DTO 中
  预留未使用字段或协议。

## 17. 实施验证结果

2026-08-21：以下仓库验证全部通过。

- `./scripts/check.sh`：locked restore、runtime fixture、Release 构建、全量 .NET 测试、OpenAPI 合约、
  Web typecheck/test/build；Web 14 个测试文件、96 个测试通过。
- `./scripts/verify-dev-user.sh`：api、web、e2e 均以宿主 `1000:1000` 运行并正确写入。
- `./scripts/verify-third-party.sh`：固定 Emuera 上游提交、源码树和许可证声明通过。
- 额外定向回归：Application 10/10、API Health/Bootstrap/SessionLifecycle 7/7；开发环境已执行
  `./scripts/dev-down.sh` 清理。
