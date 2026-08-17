# P1-06：幂等 Session 创建、开启与关闭纵切详细开发方案

状态：已完成（2026-08-12）

范围说明：本文保留纵切实现的历史设计；其中敌对租户沙箱、细粒度资源治理及 Worker 断线恢复表述
已由 [`ADR-0017`](../adr/0017-trusted-self-hosted-mvp-simplification.md) 取代，完成范围以 P1-S01 为准。

设计日期：2026-08-12

对应开发步骤：`P1-06 — 幂等 Session 创建、开启与关闭纵切`

关联需求：SESS-001、SESS-005～008、SESS-011/012、GAME-008/010、SAVE-001/005/011～015、
AUTH-001～003、OPS-002/004/005、SEC-003/005/009、NFR-002/004/006/011/013、
AC-001、AC-003、AC-004、AC-006、AC-007、AC-013、AC-014

关联决策：[`ADR-0007`](../adr/0007-session-root-native-save-ownership.md)、
[`ADR-0010`](../adr/0010-single-game-content-without-version-entities.md)、
[`ADR-0015`](../adr/0015-api-owned-worker-lifecycle.md)、
[`ADR-0016`](../adr/0016-reopenable-session-root-lifecycle.md)

前置任务：P0-05、P1-01～P1-05

后续任务：P1-07 Emuera 结构化运行时、P1-08 Snapshot、P1-09 Realtime/Input、P1-10 Save、
P1-11 Session UI、P1-12 管理员控制、P1-13 基础进程边界与实例级上限

## 1. 目标结果

P1-06 在 P1-05 已完成的 API Worker Manager 和持久租约内核上增加正式产品用例，形成从 HTTP、
授权、幂等、SQLite、SessionRoot 物化到 Worker 生命周期的第一个 Session 纵切。完成后必须得到：

1. 用户可以从一个有权读取且存在 current content 的 Game 创建多个持久 Session；每次创建只复制
   一次 Game 当时的完整 current content，成功状态为 `CLOSED`，不占活动 Worker 配额。
2. `POST /sessions`、`:open`、`:close` 使用互相独立的幂等 scope。相同 actor/scope/key 与相同
   规范请求只执行一次；相同键改变请求返回稳定冲突；请求断开或 API 重启后仍能查询或回放结果。
3. `open` 只调用 P1-05 `SessionRuntimeCoordinator`，不在 HTTP handler 中重写 epoch、配额、lease、
   进程启动或失败补偿；`close` 同样通过当前 Worker 路由和 coordinator 完成。
4. `CLOSED/CRASHED` Session 可用相同 ID 和 SessionRoot 重开。Game current 后续替换不会改变其
   Game 绑定、源摘要、源 manifest、运行时清单、目录 identity 或已有原生存档。
5. 创建、open、close、输入门禁和后续存档写共享可审查的线性化点。并发结果不产生双 Worker、
   混合内容树、重复副作用或 API/Worker 同时写 SessionRoot。
6. 创建复制和 HTTP 生命周期操作具有持久恢复事实；不能用请求作用域 `DbContext`、进程内 Task、
   锁或 `ResponseJson == "{}"` 哨兵充当唯一事实。
7. Session 列表和详情只返回产品元数据，不泄露 DataRoot、SessionRoot、UDS、PID、启动令牌、
   物理 inode 或内部异常文本。

## 2. 范围与非目标

### 2.1 本阶段必须实现

- 创建、列表、详情、open、close 五类 HTTP API，统一契约、错误和 ETag；
- Session Application 用例、持久幂等命令端口、创建操作恢复器和 API adapter；
- Game current snapshot、copy lease、完整 SessionRoot 物化、原子发布和安全清理；
- Worker 不可写的 Session 容器 owner marker，以及 P1-05 root inspector 的绑定加固；
- 创建存储预算、活动配额错误映射、open 与停止态文件写租约互斥端口；
- create/open/close 的 CSRF、资源授权、速率限制、审计、结构化日志和指标；
- 正常、失败、并发、HTTP 断开、API 重启、磁盘/SQLite 故障及恶意文件树自动化测试。

### 2.2 明确留给后续任务

- P1-07 先补全 Emuera Console/Input/Media 语义与 Worker 结构化协议；
- P1-08 才实现完整 ConsoleSnapshot 的实时队列、恢复和输出背压；
- P1-09 才公开 WebSocket、浏览器输入和 `promptId/clientMessageId` 协议。P1-06 只冻结
  `STOPPING` 后输入门禁与 close 线性化契约；
- P1-10 才公开存档 list/upload/rename/delete API。P1-06 交付停止态 SessionRoot mutation lease
  端口并验证它与 open 互斥，不提前定义存档格式；
- P1-11 才实现正式 Session 浏览器界面。本阶段不把现有占位 UI 当验收入口；
- P1-12 才公开管理员 Worker 列表和 force-stop；用户 close 不接受 `force=true`；
- P1-13 只补生产非 root、敏感挂载和实例级上限；依据 ADR-0017 不建设敌对租户沙箱；
- 不实现 Session delete、rename、clone、GameVersion、自动重启、指令级恢复或跨 API 接管；
- 不在 close 时复制/提交 SessionRoot，也不创建 SaveArtifact、generation 或通用“最终自动存档”。

## 3. 已有基线与必须修正的缺口

### 3.1 可直接复用

- P1-04 `IGameContentCopyLeaseStore` 已按 `gameId + revision + digest` 持有 current dirfd，并使
  activation 在活动 lease 期间不能回收 retired 内容；
- `SessionRootLayoutBuilder` 已能复制完整普通文件树、拒绝链接/特殊文件/硬链接、执行数量与字节
  上限、校验 digest、物化固定大小写别名并原子发布；
- P1-05 `ISessionRuntimeStore` 已以 `BEGIN IMMEDIATE` 原子占用活动配额、递增 epoch、创建 lease；
- `SessionRuntimeCoordinator` 已负责 open/close 的进程副作用和失败补偿；
- `WorkerManager` 已持有本 API 实例内的 Worker 路由、进程身份和 P1-05 binding；
- P1-02 `IResourceAuthorizer`、Cookie/CSRF、审计上下文和隐藏式 `404` 语义可以复用。

### 3.2 P1-06 必须修正

1. 当前 `ApiIdempotencyStore` 与 GameLibrary exception/DTO 耦合，只把空 JSON 当“处理中”，catch
   时删除记录，不能表示已线性化长操作、稳定失败或重启恢复。它必须重构为通用持久命令端口。
2. `SessionRootLayoutBuilder` 的 `.cloudemuera-session.json` 位于 Worker 可写 root 内，且主要绑定
   Game content identity。它可作为 Runtime 布局提示，不能单独证明“这是该 Session 的原目录”。
3. `SessionRootRuntimeInspector` 尚未核对 Worker 不可写的 Session owner marker。P1-06 必须让
   inspector 同时核对 DB binding、受保护 marker、root device/inode、内部布局标记和配置布局。
4. P1-05 coordinator 的 close 入口需要调用者提供 binding/process handle；HTTP 层不能自行扫描
   `WorkerManager.Workers`。需增加 Application 级当前 Worker 路由端口和单一生命周期 facade。
5. 请求取消 token 当前可贯穿已经获取 lease 的后序启动。P1-06 必须明确“提交前可取消，提交后由
   服务生命周期继续收敛”，不能让浏览器断开留下永久 `STARTING/STOPPING`。
6. open 目前只检查 WorkerLease。为 P1-10 的停止态存档写竞争，需要共同的 SessionRoot mutation
   lease 事实，使两个事务按同一互斥协议裁决。

这些调整补齐已接受 ADR 的实现边界，不改变进程拓扑、Session 状态集合或存档所有权，因此本阶段
不新增 ADR。若实现中需要新增创建失败持久状态、允许 Worker 修改受保护 metadata、引入总 Session
数量上限或把创建/open 合并为一个命令，必须先另立 ADR。

## 4. 核心不变量与线性化点

### 4.1 持久不变量

| 不变量 | 持久裁决 |
| --- | --- |
| 一个成功创建键只对应一个 Session | `idempotency_records` 唯一键 + `resource_id` |
| Session 创建时固定一次 Game 内容 | Session 的 game/revision/digest/manifest 字段，创建后不可更新 |
| 半成品不能启动 | 只有 root 发布并核验后才允许 `CREATING → CLOSED` |
| 一个活动 Session 只有一个 Worker | `worker_leases.session_id` 主键 + P1-05 CAS |
| 每次 open epoch 增大 | P1-05 open 事务内 `worker_epoch + 1` |
| Worker 与停止态文件写者互斥 | open 同时检查 mutation lease；mutation 获取同时检查 WorkerLease |
| close 不删除或替换 root | close 更新 Session/lease，不触碰创建操作和目录发布接口 |
| Game 更新不改变既有 Session | open 不读取 `games.current_*`，只读取 Session snapshot/root |
| HTTP 断开不撤销已提交操作 | 持久命令状态 + 后台 executor/reconciler |

### 4.2 每个用例的线性化点

| 用例 | 线性化点 | 线性化前取消 | 线性化后行为 |
| --- | --- | --- | --- |
| create | 同一 `BEGIN IMMEDIATE` 中插入幂等命令、`CREATING` Session 和 create operation | 可取消且不得留下行/目录 | 后台完成或失败清理并写终态响应 |
| open | P1-05 事务把 `CLOSED/CRASHED → STARTING`、递增 epoch 并插入 WorkerLease | 可取消 | 必须启动成功或补偿为 `CRASHED` |
| close | P1-05 `BeginStopping` 把 `STARTING/RUNNING → STOPPING` | 可取消 | 必须确认退出并到 `CLOSED/CRASHED` |
| stopped mutation | 插入 mutation lease，且同一事务确认无 WorkerLease | 可取消 | 文件操作完成/失败后释放；过期由恢复器处理 |

进程内 per-session executor 只减少重复副作用和简化同实例排序，不是正确性来源。所有跨重启正确性
仍由 SQLite 行、目录 identity 和进程退出屏障证明。

## 5. HTTP 契约

### 5.1 通用约定

- 基础路径 `/api/v1`；JSON 为 `camelCase`，枚举为稳定大写下划线；
- 所有端点要求认证；写端点同时要求有效 CSRF header；
- create/open/close 必须有 `Idempotency-Key`，1～256 字符，拒绝控制字符；三个 scope 相互独立；
- `Idempotency-Key` 不进入响应、普通日志或审计 metadata；日志只记录其 SHA-256 短前缀；
- Session 响应带 `ETag: "<stateVersion>"`。open/close 不强制 `If-Match`：运行 heartbeat、prompt 和
  sequence 可推进 `stateVersion`，强制客户端匹配会使紧急 close 不必要地失败；命令由状态 CAS 裁决；
- 相同幂等命令的终态重放返回原 HTTP status 与原版本化响应；处理中返回 `202`，不返回 `409`；
- 不存在、无权访问或引用其他用户私有资源统一 `404 SESSION_NOT_FOUND/GAME_NOT_FOUND`；
- 所有错误使用 `{ code, message, requestId, details? }`，`details` 只包含字段级安全信息。

### 5.2 创建

```http
POST /api/v1/sessions
Idempotency-Key: <opaque-key>
X-CSRF-TOKEN: ...
Content-Type: application/json

{
  "gameId": "game_...",
  "name": "一周目"
}
```

规则：

- `name` 去首尾空白后 1～200 Unicode scalar；拒绝 NUL/控制字符，不要求同一用户唯一；
- `gameId` 必须是调用者可读的 `ACTIVE` Game，且 current digest/revision/path/manifest 完整；
- shared Game 可以创建私有 Session；创建者成为唯一 owner，不继承 Game owner；
- 不提供 `autoOpen`。UI 若要“一步开始”，创建成功后使用新的幂等键调用 `:open`；
- 完成返回 `201 Created`，`Location: /api/v1/sessions/{id}`；超过 HTTP 等待预算返回 `202 Accepted`
  及相同 Location，后台操作继续；
- create 尚在执行时详情可见为 `CREATING`；若创建在 root 发布前确定失败，安全清理后删除未成功
  Session 行，原幂等键仍回放稳定失败，新的创建必须使用新键。

### 5.3 列表

```http
GET /api/v1/sessions?gameId=game_...&state=CLOSED&cursor=...&limit=50
```

- 只列当前用户 Session；管理员跨用户视图留给 P1-12；
- 可选 `gameId` 和单个 `state` 过滤；`limit` 默认 50、最大 100；
- 排序固定为 `(createdAt DESC, id DESC)`；签名 cursor 绑定 user/filter/末尾 tuple/版本，拒绝篡改；
- 查询投影不得加载 manifest JSON、目录路径或 WorkerLease 大对象；目标满足 NFR-006；
- 返回 `items + nextCursor`，允许短暂展示 `CREATING`，不依据浏览器连接数虚构状态。

### 5.4 详情

```http
GET /api/v1/sessions/{sessionId}
```

响应最小模型：

```json
{
  "schemaVersion": 1,
  "id": "sess_...",
  "name": "一周目",
  "game": { "id": "game_...", "name": "示例游戏" },
  "sourceContentDigest": "sha256:...",
  "sourceContentRevision": 3,
  "runtimeVersion": "...",
  "state": "CLOSED",
  "stateVersion": 8,
  "workerEpoch": 2,
  "waitingForInput": false,
  "createdAt": "2026-08-12T00:00:00Z",
  "startedAt": null,
  "lastActivityAt": "2026-08-12T00:00:01Z",
  "closedAt": "2026-08-12T00:00:01Z",
  "closeReason": "requested"
}
```

`currentPromptId`、root path、manifest 全文、PID 和 lease endpoint 不在普通详情中。P1-09 通过授权
实时协议取得 prompt；P1-12 通过管理员 DTO 取得受控进程诊断。

### 5.5 开启

```http
POST /api/v1/sessions/{sessionId}:open
Idempotency-Key: <opaque-key>
X-CSRF-TOKEN: ...

{}
```

- `CLOSED/CRASHED`：进入正式 open；`CRASHED` 必须已经无 lease 且通过旧写者回收屏障；
- `STARTING`：不同键加入当前结果并返回 `202`，不得启动第二个 Worker；
- `RUNNING`：收敛式 no-op，返回 `200` 当前 Session；不增加 epoch；
- `STOPPING`：返回 `409 SESSION_TRANSITION_IN_PROGRESS`；
- `CREATING`：返回 `409 SESSION_NOT_READY`；
- 活动配额不足返回 `409 ACTIVE_SESSION_QUOTA_EXCEEDED`；控制面 draining/readiness 失败返回 `503`；
- ready 在同步等待预算内到达时返回 `200`；否则 `202`，后台继续到 `RUNNING/CRASHED`；
- 启动失败若已经消耗 epoch，Session 为 `CRASHED`，幂等响应不得隐瞒该事实。

### 5.6 关闭

```http
POST /api/v1/sessions/{sessionId}:close
Idempotency-Key: <opaque-key>
X-CSRF-TOKEN: ...

{}
```

- `STARTING/RUNNING`：排入该 Session 的串行生命周期 executor，最终调用 coordinator close；
- `STOPPING`：加入当前关闭，返回 `202`；
- `CLOSED`：收敛式 no-op，返回 `200`，状态仍为 `CLOSED`；
- `CRASHED`：无 Worker 可关闭，返回 `200` 且保留 `CRASHED/closeReason`，不得伪装成正常关闭；
- `CREATING`：返回 `409 SESSION_NOT_READY`；
- 请求体不接受 `force`、deadline 或 autosave policy。用户关闭使用服务端安全默认 deadline，要求
  Runtime 停止并刷新已有写入，但不合成通用存档；管理员强停留给 P1-12；
- 超过同步等待预算返回 `202`。只有确认 Worker 退出后才返回/最终记录 `CLOSED`；无法确认退出时
  保持 fail-closed lease/readiness 屏障并返回稳定故障，不授予其他写者。

### 5.7 主要错误映射

| HTTP | code | 场景 |
| --- | --- | --- |
| 400 | `VALIDATION_FAILED` | body/name/header 格式错误 |
| 404 | `GAME_NOT_FOUND` / `SESSION_NOT_FOUND` | 不存在或越权 |
| 409 | `IDEMPOTENCY_KEY_REUSED` | 同 actor/scope/key 的规范请求摘要不同 |
| 409 | `GAME_HAS_NO_CURRENT_CONTENT` / `GAME_BLOCKED` | 不能固定创建源 |
| 409 | `SESSION_NOT_READY` / `SESSION_TRANSITION_IN_PROGRESS` | 状态不接受命令 |
| 409 | `ACTIVE_SESSION_QUOTA_EXCEEDED` | open 活动配额不足 |
| 413 | `SESSION_STORAGE_QUOTA_EXCEEDED` | manifest 预估或复制实测超过单 Session 上限 |
| 428 | `IDEMPOTENCY_KEY_REQUIRED` | 可重试写操作缺少幂等键 |
| 429 | `RATE_LIMITED` | HTTP 速率限制，不代表活动配额 |
| 503 | `DATA_ROOT_UNAVAILABLE` / `CONTROL_PLANE_DRAINING` | 持久层或 Worker Manager 不可安全服务 |

## 6. Application 与适配层设计

### 6.1 Contracts

在 `CloudEmuera.Contracts` 定义版本化 request/response/error DTO；不得直接序列化 EF row、Domain
entity、P1-05 runtime binding 或 protobuf 类型。OpenAPI 必须包含 header、201/202/409/413/428/503
响应和 Session 枚举。

### 6.2 Application 用例

建议在 `CloudEmuera.Application/Sessions` 增加：

- `ISessionApplicationService`：`CreateAsync/ListAsync/GetAsync/OpenAsync/CloseAsync`；
- `ISessionRepository`：授权后投影、创建 snapshot、状态读取，不暴露 `DbContext`；
- `ISessionCreationStore`：原子 prepare、阶段 CAS、commit、fail、reconcile；
- `ISessionRootMaterializer`：只接受固定 snapshot 与已打开 copy lease，不重新按 current 字符串寻址；
- `IIdempotentCommandStore`：begin/read/complete success/complete failure；
- `ISessionLifecycleExecutor`：按 Session 串行执行本 API 实例的 open/close，并提供有界等待；
- `ICurrentWorkerRouter`：按完整 persisted binding 取得当前进程 handle；
- `ISessionRootMutationLeaseStore`：供 P1-10 获取停止态文件写权；
- `ISessionOperationRecovery`：启动和周期恢复 create/幂等 pending/mutation lease。

HTTP handler 只做认证上下文、CSRF、header/body 绑定、调用用例和错误映射，不直接使用
`CloudEmueraDbContext`、`SessionRuntimeCoordinator`、`WorkerManager`、`Directory` 或 `Process`。

### 6.3 生命周期 facade

`SessionLifecycleExecutor` 是 coordinator 上方的薄编排层：

1. 以 sessionId 进入进程内 keyed executor；
2. 重新授权并读取当前持久状态；
3. 对 open 生成 workerId/options 后调用 `SessionRuntimeCoordinator.OpenAsync`；
4. 对 close 从 `ICurrentWorkerRouter` 获取与 DB 当前 lease 完整匹配的 binding/handle，再调用
   `SessionRuntimeCoordinator.CloseAsync`；
5. 将 coordinator 稳定结果码映射为 Application 结果，并完成幂等命令与审计；
6. keyed executor 不跨 Session 串行，不持有 SQLite 事务，不在 handler 中暴露进程对象。

需调整 coordinator 的取消边界：请求 token 只传到 lease/STOPPING 事务提交前；一旦状态已提交，
后序检查、spawn/stop、kill、退出确认和补偿使用 operation/host token，并在补偿关键段使用
`CancellationToken.None`。HTTP 最多等待配置时间，等待超时只改变响应为 202，不取消 operation。

## 7. 持久化与 migration

### 7.1 幂等记录加固

新增 migration 扩展 `idempotency_records`：

```text
status TEXT NOT NULL                 -- IN_PROGRESS | SUCCEEDED | FAILED
error_code TEXT NULL
updated_at INTEGER NOT NULL
completed_at INTEGER NULL
```

约束：

- `IN_PROGRESS` 时 `completed_at/error_code` 为 NULL；
- `SUCCEEDED` 时 response JSON 是版本化成功 DTO；
- `FAILED` 时 response JSON 是安全错误 DTO，`error_code` 非空；
- `updated_at >= created_at`，terminal 时 `completed_at >= updated_at`；
- cleanup 不删除未完成记录；terminal 至少保留 24 小时，配置只能延长不能低于客户端重试窗口。

规范请求摘要使用 source-generated JSON 的固定模型计算，包含 `schemaVersion + actor + scope + route
resourceId + normalized body`，不包含 CSRF、Cookie、requestId 或 header 顺序。旧 `ResponseJson == {}`
记录迁移为 `IN_PROGRESS`，其他记录迁移为 `SUCCEEDED`；启动恢复器随后裁决遗留 Session scope。

### 7.2 Session 创建操作

新增 `session_creation_operations`：

```text
id TEXT PK                              -- scop_...
session_id TEXT UNIQUE NOT NULL FK sessions
actor_user_id TEXT NOT NULL FK users
status TEXT NOT NULL                    -- PREPARED | COPYING | ROOT_PUBLISHED | COMMITTED | FAILED
staging_path TEXT NOT NULL UNIQUE
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

Session 行继续保存 `game_id/source_content_revision/source_content_digest`、固定的
`session_root_manifest_digest/save_layout` 和 `session_root_path`。完整 runtime manifest 不再写入
`sessions` 表，而是在 SessionRoot 发布时写入受保护的 `metadata/runtime-manifest.json`；该文件仍有
独立的有界存储上限，不复用普通元数据 JSON 的 1 MiB 上限：

- Game canonical manifest 快照与 runtime config；
- compatibility profile、上游 commit、CloudEmuera integration/runtime version；
- source digest/revision、保存布局；
- 物化后的 SessionRoot manifest digest 与固定大小写别名清单。

不得只保存一个指向 `games.manifest_json` 的引用。若组合 JSON 超过 SessionRoot runtime manifest
文件上限，创建在 prepare 前失败，不截断、不丢字段；其实际 UTF-8 字节数同时计入 SessionRoot 创建预留。

### 7.3 SessionRoot mutation lease

新增 `session_root_mutation_leases`：

```text
session_id TEXT PK FK sessions
operation_id TEXT UNIQUE NOT NULL
actor_user_id TEXT NOT NULL FK users
purpose TEXT NOT NULL                   -- SAVE_IMPORT | SAVE_RENAME | SAVE_DELETE | SAVE_COPY
acquired_at INTEGER NOT NULL
expires_at INTEGER NOT NULL
```

- 获取 mutation lease 的 `BEGIN IMMEDIATE` 同时要求 Session 为 `CLOSED/CRASHED`、不存在
  WorkerLease、旧 Worker 写权限已确认释放；
- P1-05 open lease 事务增加“不存在 mutation lease”条件；
- lease 只建立未来 P1-10 的互斥事实，P1-06 不公开存档 API；
- API 重启后只有核对 staging/operation ownership 的恢复器可以续作或释放，不能仅因过期就与未知
  文件替换并发。

### 7.4 索引与查询

- 保留 `(owner_user_id, created_at DESC)`，分页再以 `id` 作稳定 tie-break；必要时 migration 改为
  `(owner_user_id, created_at DESC, id DESC)` 覆盖索引；
- 增加 create operation `(status, updated_at)` 和 idempotency `(status, updated_at)` reaper 索引；
- 所有新增状态、时间、路径、ID、计数和 JSON 都有 CHECK；migration/ModelSnapshot 同步；
- 不修改历史 migration，使用新的前滚 migration。

## 8. SessionRoot 物化与身份

### 8.1 目录布局

```text
/data/sessions/
├── .staging/{sessionId}-{operationToken}/
│   ├── root/
│   └── metadata/
│       └── session-root.json
└── {sessionId}/
    ├── root/                         # Worker 唯一可见、可写的 GameRoot
    └── metadata/                     # API-only，Worker mount 中不可见/不可写
        ├── session-root.json
        └── runtime-manifest.json
```

staging 与 final 必须位于同一文件系统父目录。发布时先 fsync 文件、目录和 metadata，再把整个
staging container 原子 rename 为 `{sessionId}`，最后 fsync `sessions/`。禁止先创建 final root
再逐文件填充。

### 8.2 受保护 owner marker

`metadata/session-root.json` schema v1 至少绑定：

```text
schemaVersion, sessionId, ownerUserId, gameId,
sourceContentRevision, sourceContentDigest,
sourceManifestDigest, materializedManifestDigest,
saveLayout, runtimeVersion,
rootDevice, rootInode, createdAt
```

marker 本身、metadata 目录和 session container 必须为当前 API 服务身份所有、无链接、单链接普通
文件/目录、Linux 私有权限。root device/inode 在发布前后复核；rename 不改变该 identity。

root 内现有 `.cloudemuera-session.json` 继续供 Runtime layout 校验，但 Worker 可写，因此它只是一项
需要与受保护 marker 对照的证据，不是授权根。P1-05 inspector 的 `SessionRuntimeBinding` 应携带
足够的 session/game/source/manifest identity，open 前同时匹配 DB、protected marker、root inode、
root 内标记和 `emuera.config` save layout。任一不一致返回 `SESSION_ROOT_INVALID`，不自动修复或
从 Game current 重建。

### 8.3 复制算法

1. 在 `BEGIN IMMEDIATE` 中读取可见且 ACTIVE Game 的 current revision/digest/path、manifest、
   runtime config 和调用者 quota；校验预计 bytes/file count；插入幂等记录、`CREATING` Session、
   create operation 和存储 reservation；提交后这些字段冻结。
2. 用 `gameId + frozen revision + digest + SESSION_CREATE + sessionId` 获取 copy lease 和 current
   dirfd。若 activation 已先赢得 mutation lock，获取失败；不能转而复制新 revision。
3. 从 copy lease 的 fd 路径调用受控 materializer。逐项复核普通类型、nlink、规范路径、大小写/
   Unicode 冲突、文件数、实际 bytes 和 digest；长复制周期续租并更新 operation heartbeat。
4. 不使用硬链接。reflink 只有在验证写时复制独立语义后才可作为优化，失败回退普通复制。
5. 生成固定别名、root 内布局标记和 API-only marker；重新扫描目标并计算 materialized digest；源
   manifest 的每个条目必须完整出现，额外项只能是设计声明的固定别名/内部标记。
6. fsync 全树并原子发布 container；CAS `COPYING → ROOT_PUBLISHED`。
7. 短事务复核 Session/CreateOperation/idempotency request digest，把 `CREATING → CLOSED`、
   operation `COMMITTED`、幂等记录 `SUCCEEDED`、审计成功和存储结算一起提交。
8. 释放 copy lease。后序回收只能删除已核验 ownership 的 staging；不得触碰 final SessionRoot。

### 8.4 存储预算

- `quota_profiles.max_session_bytes` 是单 Session 上限，不得被解释为 Session 总数量上限；
- prepare 使用 manifest bytes 加固定 metadata/alias 安全余量预留，复制过程以实际 bytes 再强制；
- 同一事务汇总活动 create reservation，结合事务前读取的 DataRoot free bytes 与配置安全余量，防止
  并发创建都承诺同一空间；磁盘事实仍可能变化，因此每次写/fsync 的 ENOSPC 都是可恢复失败；
- 成功后 reservation 结算/释放，但 Session 运行期实例级边界继续由 P1-13 验证；
- 创建数量不限；只能因单 Session 大小、DataRoot 安全空间、速率限制或安全策略拒绝。

## 9. 创建故障恢复

启动恢复器在 Worker reconciliation 完成、readiness 开放前处理 create operation；周期 reaper 处理
超时项。处理必须持有 operation CAS/lease，不能让两个恢复任务同时清理。

| DB 状态 | 文件事实 | 恢复动作 |
| --- | --- | --- |
| PREPARED/COPYING | 只有受控 staging | 校验 marker/token 后清理并按同一 frozen source 重试；源已不可取得则稳定失败 |
| PREPARED/COPYING | staging 归属异常/含链接 | 不递归删除，标记安全故障、readiness/告警按风险失败 |
| ROOT_PUBLISHED | final marker/root identity 完整 | 完成 `CREATING → CLOSED` 和幂等成功 |
| ROOT_PUBLISHED | final 不完整但 staging 可证明 | 恢复发布或安全清理后失败；不得启动 |
| COMMITTED | Session CLOSED、幂等仍 pending | 根据 Session snapshot 补写成功响应，不重新复制 |
| 任意阶段 | final 与另一个 Session/marker 绑定 | fail closed、保留现场、管理员告警 |

创建在 root 发布前发生确定失败时：先把 operation 标记为 `FAILED`，保留 `CREATING` Session 作为
清理锚点；完成受保护清理后，在一个短事务中写幂等 `FAILED`/安全错误响应和失败审计，依次删除
create operation 与未成功的 Session 行，并释放 reservation/copy lease。成功 Session 不因幂等记录
到期而删除。若清理不能证明安全，保留 operation/Session 行并阻止 open，不能用普通递归删除碰运气。

## 10. open/close 幂等与竞争

### 10.1 幂等命令状态机

```text
ABSENT ──Begin──> IN_PROGRESS ──CompleteSuccess──> SUCCEEDED
                           └────CompleteFailure──> FAILED
```

- 同键同摘要读取 `SUCCEEDED/FAILED` 时回放；读取 `IN_PROGRESS` 时查询关联 Session/lease 并返回
  202 或帮助完成记录；
- 同键不同摘要始终 409，即使旧记录失败；
- 业务 4xx 可以作为稳定 FAILED 回放；线性化前临时 5xx 可删除/释放 begin 记录允许重试；
- 线性化后任何 5xx 都不能删除记录，必须由 executor/reconciler收敛；
- API 重启时，遗留 open 若 Session 为活动态由 P1-05 先回收为 CRASHED，再把命令完成为失败；
  遗留 close 依据最终 `CLOSED/CRASHED` 完成，不再次向旧进程发送命令。

### 10.2 同 Session 命令排序

- 两个 open：SQLite 第一个获取 lease；第二个加入/读取同一活动事实，不能以新 epoch“修复”第一个；
- open 后 close：per-session executor 允许的结果是 open 先到 RUNNING/CRASHED，随后 close 收敛；
  不要求在 Worker 尚未注册时由 HTTP handler直接 kill；
- close 后 open：`STOPPING` 期间 open 返回冲突；确认退出到静止态后新的 open 才可使用更大 epoch；
- close 与自然退出：以进程退出事实和完整 binding CAS 裁决；最终 CLOSED 可视为 close 成功，最终
  CRASHED 必须保留异常原因；
- close 与未来输入：`BeginStopping` 是门禁线性化点。先被 Worker 接受的输入可以完成；其后的
  输入必须得到 `SESSION_NOT_ACCEPTING_INPUT`，不得因 HTTP 返回较晚继续接受；
- open 与存档写：先插入 WorkerLease 或 mutation lease 的事务获胜，另一方稳定失败/等待；任何时刻
  不允许两个写者都认为自己成功。

## 11. 授权、安全与审计

### 11.1 授权顺序

- create：认证/强制改密检查 → Game `GameRead` → Game ACTIVE/current/安全策略 → prepare；
- list：SQL 首条件固定 `owner_user_id = actor`，不能先全表查询再内存过滤；
- detail/open/close：每次调用及后台真正控制前重新执行 Session `SessionRead/SessionControl`；
- idempotency replay 也必须先认证当前 actor；记录主键绑定 actor，不让注销后的匿名请求读取响应；
- 资源授权失败不泄露 Session 是否存在、owner、Game、状态或幂等记录。

### 11.2 文件安全

- 所有 persisted relative path 按 DataRoot 锚定并以 dirfd/openat/no-follow 访问；
- owner marker、staging token、父目录 device/inode 任一异常时不删除、不覆盖、不自动修复；
- 复制源只来自 copy lease 的已打开 fd；不能事务读 revision 后再按 current 字符串重新打开；
- SessionRoot publish、metadata 和 SQLite 之间的窗口必须有上述恢复表，不依赖 finally；
- API 不把 root path交给客户端，Worker不看 metadata/Game current/其他 SessionRoot。

### 11.3 审计与日志

首次执行而非幂等回放写审计：

- `SESSION_CREATE_REQUESTED/CREATED/CREATE_FAILED`；
- `SESSION_OPEN_REQUESTED/OPENED/OPEN_FAILED`；
- `SESSION_CLOSE_REQUESTED/CLOSED/CLOSE_FAILED`。

审计在可行时与对应持久状态同事务提交。metadata 只含 gameId、source digest、旧/新状态、epoch、
稳定 reason code 和 requestId；不含 idempotency key、name 全文以外的游戏输入、路径、token 或异常。

结构化日志使用 requestId/sessionId/gameId/workerId/epoch/operationId/result；创建复制日志只记录文件
数量和字节，不逐项记录用户路径。指标至少包括 create duration/bytes/failure stage、幂等 replay/
pending、open/close latency/result、活动配额拒绝、mutation 竞争和 recovery 数量。

## 12. 测试设计

所有新增测试名或 trait 注释映射需求/AC。测试不得读取人工 `.env`、`./data` 或现有 Compose project。

### 12.1 Domain/Application

- create/open/close 状态分类与结果映射；CLOSED/CRASHED 的收敛 no-op；
- 幂等同键同摘要 replay、同键不同摘要冲突、三个 scope 独立、terminal failure replay；
- 请求取消发生在 prepare 前无副作用，发生在线性化后 executor 继续；
- 两个 open、open/close、close/natural-exit 的所有允许线性化结果；
- 关闭后输入门禁端口拒绝，先接受输入的结果不被伪造回滚；
- HTTP DTO 不包含物理路径/令牌/PID/internal exception。

### 12.2 Persistence

- migration upgrade、CHECK/FK/index、旧幂等行转换、model snapshot；
- 相同 create key 并发仅一个 Session/create operation/reservation；
- 同一用户同一 Game 不同 key 可创建两个 Session；不限制静止 Session 总数；
- 最后一个活动名额并发 open 只有一个成功；同 Session 并发 open 只有一个 epoch/lease；
- open 与 mutation lease 并发最多一个成功；lease 过期/恢复不能误删未知操作；
- source snapshot 在 Game activate/delete 竞争中为完整旧或完整新 revision，不出现混合；
- list cursor 排序、过滤、篡改、跨用户重用和最大页大小。

### 12.3 文件系统安全与故障注入

- 两个 Session 的普通文件 inode、存档、global.sav、自定义目录完全隔离；禁止硬链接；
- root 与 staging 中的 symlink、FIFO、socket、device、hardlink、Unicode/大小写碰撞被拒绝；
- source 在复制中 rename 后仍通过 lease fd 完成原 revision；retired 不提前清理；
- 在复制中段、fsync 前后、container rename 后、DB commit 前后 kill API，重启结果符合恢复矩阵；
- ENOSPC、quota overrun、digest mismatch、lease expiry、SQLite busy/commit failure 均不留下可启动半树；
- protected marker 被替换、root inode 被交换、root 内 marker 被 Worker 修改时 open fail closed；
- 清理遇到异常 owner/祖先链接时保留现场且不越出 DataRoot。

### 12.4 HTTP 与真实 Worker 集成

- 匿名、强制改密、CSRF 缺失、越权 create/detail/open/close；私有资源统一 404；
- create 201/202/失败回放与 Location；列表/详情 ETag；错误码和 OpenAPI 契约；
- HTTP 客户端在提交后断开，同 key 重试只得到一个 Session/Worker/close；
- 创建两个同 Game Session，真实 Worker 运行后各自写原生根目录和 `sav/` 存档，互不影响；
- close 后输入控制端口拒绝；同 Session reopen epoch 增大并加载原存档；
- kill Worker/API 后对账 CRASHED，再 open 相同 root；Game current 更新后旧 Session 仍运行旧副本；
- 控制面 draining、Worker ready timeout、无法确认退出时的 503/fail-closed 行为。

### 12.5 验证命令

定向和完整验证都从仓库 dev Docker 执行：

```bash
./scripts/dev-up.sh
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release \
  --filter Category=SessionLifecycle'
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
```

若实际测试项目或 trait 与现有仓库不同，实现切片必须同步修正文档为真实可执行命令，不能创建一个
与 solution 未关联的示意项目来满足名称。

## 13. 实施切片

### 切片 1：契约、幂等和 migration

- 定义 Session DTO/error/result；
- 重构通用 idempotency port，新增 status/error/time migration；
- 新增 create operation、mutation lease、覆盖分页索引和约束测试；
- 保持现有 Game API 幂等行为回归通过。

### 切片 2：创建 prepare 与安全物化

- 实现 Game snapshot + reservation + `CREATING` 原子 prepare；
- 接入 copy lease 和 `SessionRootLayoutBuilder`；
- 增加 API-only marker、容器级原子发布和 source/runtime manifest snapshot；
- 覆盖普通、配额、恶意树和 activation 竞争。

### 切片 3：创建恢复与 HTTP create/list/detail

- 实现启动/周期 create reconciler 和安全清理；
- 暴露 create/list/detail、游标、ETag、授权、CSRF、限流和审计；
- 覆盖 HTTP 断开与各文件/DB crash window。

### 切片 4：open facade 与命令恢复

- 为 coordinator 分离提交前/提交后取消 token；
- 增加 current Worker router 和 keyed lifecycle executor；
- open 接入 P1-05 coordinator、mutation lease 检查、幂等 pending/replay 和审计；
- 覆盖并发配额、同 Session 双 open、超时和 API restart。

### 切片 5：close facade 与竞争契约

- close 解析当前完整 binding/handle并调用 coordinator；
- 实现 STARTING/RUNNING/STOPPING/静止态语义；
- 冻结输入门禁与 mutation/open 竞争端口；
- 覆盖 close/input、open/close、natural exit、无法确认退出。

### 切片 6：真实进程验收和文档收尾

- 运行两种原生存档布局、双 Session、close/reopen、crash/reopen 和 Game 更新隔离场景；
- 生成/校验 OpenAPI，核对日志、指标、readiness 和敏感信息；
- 更新 requirements/design/development plan、migration 数量、配置示例和 P1-07/P1-10 交接；
- 完整 `check.sh`、权限、第三方和 diff 检查。

## 14. 完成定义

P1-06 只有在以下条件全部满足后才能标记 DONE：

1. 五类 HTTP API 具有认证、资源授权、CSRF、限流、契约和稳定错误；
2. 相同幂等键在并发、断网和 API 重启后不重复创建 Session、Worker 或 close 副作用；
3. 创建固定并完整复制一个 Game revision，SessionRoot 私有、原子发布、无共享可写 inode；
4. 所有创建 crash window 可安全完成或清理，半成品永远不能 open；
5. protected marker 与 DB/root identity 联合校验，Worker 可写标记不能单独授权 reopen；
6. open/close 只复用 P1-05 coordinator，epoch、配额、lease 和进程回收没有第二套实现；
7. close/crash/API restart 后相同 Session ID/root 可用更大 epoch 重开并加载原生存档；
8. Game current 更新不影响既有 Session，两个用户/Session 的 root/global save 隔离；
9. close/input 与 open/mutation 竞争只出现设计允许的线性化结果；
10. 正常、边界、主要失败、并发、恶意文件树和故障注入均有自动化测试；
11. `./scripts/check.sh`、dev user、third-party 和 `git diff --check` 全部通过；
12. 文档、OpenAPI、migration、ModelSnapshot、配置和后续任务交接同步更新。

## 15. 后续任务交接

- P1-07～P1-09 只消费 Session 当前 binding、状态和 Worker event/input 端口，不直接启动/停止进程；
- P1-09 在 `BeginStopping` 后必须返回 `SESSION_NOT_ACCEPTING_INPUT`，并继续使用 epoch fencing；
- P1-10 必须通过 `ISessionRootMutationLeaseStore` 修改原生存档，不能只在文件操作前做一次状态查询；
- P1-11 的“一步开始”是两个独立 HTTP 命令和两个幂等键，UI 必须展示 create/open 各自失败；
- P1-12 force-stop 复用当前 Worker router/coordinator，使用管理员授权和不同 reason/audit；
- P1-13 可更换复制优化或 Worker 启动实现，但不能改变 private persistent SessionRoot 和 protected
  metadata identity 契约。

## 16. 实际实现记录

当前代码已落地主要纵切：HTTP 端点位于 API-owned Session application service，create 使用
`session_creation_operations` 持久阶段和同文件系统 staging 原子发布；open/close 通过
`SessionLifecycleExecutor` 调用 P1-05 coordinator，HTTP 断开不取消已提交的后续副作用。
`idempotency_records` 已改为显式 `IN_PROGRESS/SUCCEEDED/FAILED`，并由启动/周期恢复器处理未完成
create 与生命周期命令；`session_root_mutation_leases` 已作为 P1-10 的停止态写互斥端口。恢复器
现在必须等待可调度的 lifecycle reconciliation，并再次确认没有未决 Session 命令后才开放 readiness。

已在仓库 dev Docker 中验证：真实 Kestrel HTTP + Worker create/open/close/reopen、epoch 递增、
cursor 第二页、相同 key 的跨 scope 隔离以及 Game Blocked 后 reopen 返回 `GAME_BLOCKED` 的集成
测试通过；全量 `./scripts/check.sh` 通过（Release 0 warning/0 error、API 22、Infrastructure 94、
Application 13、Worker 18、RuntimeAdapter 142、RuntimeCompatibility 27、Web 13）；
`./scripts/verify-dev-user.sh`、`./scripts/verify-third-party.sh` 和 `git diff --check` 通过。
最后的 hosted-service 生命周期收尾后，API 集成测试再次以 22/22 通过。2026-08-12 已完成 API
重启/Worker 崩溃、两种存档布局、SQLite/发布故障注入和恢复验收，P1-06 标记为 DONE。API 与 Worker
同 UID 的边界按 ADR-0017 归类为可信参与者自托管约束，不再作为 P1-06 的未完成项，也不宣称敌对
租户隔离。
