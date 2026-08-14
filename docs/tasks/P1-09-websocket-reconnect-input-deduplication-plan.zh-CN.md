# P1-09 WebSocket 快照恢复与有界输入去重详细方案

状态：已实施

日期：2026-08-14

关联需求：AUTH-001/002/003/005、SESS-003/004/006/007/010/011、PLAY-004～008、
PLAY-010～012、OPS-002/004、SEC-004/006/009、NFR-001/007/008/010/013、AC-002/005/006/012

关联决策：ADR-0003、ADR-0015、ADR-0017、ADR-0018、ADR-0020；实现前新增并接受 ADR-0021
冻结 WebSocket v1 协议

前置任务：P1-02、P1-05～P1-08、P1-S01

后续任务：P1-11 浏览器 Session Console、P1-12 管理员控制、P1-15 MVP 验收

## 1. 目标结果

P1-09 把 P1-08 已完成的 transport-neutral `SessionOutputHub` 接到正式浏览器 WebSocket，并把浏览器
输入安全、可重试地路由到 P1-07 已完成的 Worker `InputCoordinator`。完成后必须得到：

1. 已认证浏览器可通过 `GET /api/v1/realtime` 建立版本化 WebSocket v1 连接；upgrade、每次
   `session.resume` 和每次 `session.input` 都重新验证当前登录态和 Session 权限。
2. 每次订阅或重连先取得当前 `(workerEpoch, snapshotSequence)` 完整 Snapshot，再接收连续增量；
   缺口或队列溢出使用较新完整 Snapshot 替换，不按 `lastSequence` 补历史。
3. 同一连接可有界地订阅多个 Session，同一 Session 可被同一用户的多个连接观察；连接数不进入
   Session 状态，不影响 Worker 生命周期。
4. 输入必须携带 `workerEpoch + promptId + clientMessageId`。API 只做授权、活动 binding、协议和
   生命周期门禁，prompt、格式、timeout 和首个有效输入的最终裁决仍由 Worker 完成。
5. 当前 Worker 内同一 `clientMessageId` 只执行一次；相同消息重试得到 `DUPLICATE` 及原规范化值，
   不同内容复用同一 ID 得到 `CONFLICT`。两个客户端并发回答同一 prompt 只有一个 `ACCEPTED`。
6. 输入与 close 在同一个 Session 命令门上线性化；`BeginStopping` 之后进入门的输入稳定返回
   `SESSION_NOT_ACCEPTING_INPUT`，不能在 WebSocket handler 中仅凭一次内存状态查询放行。
7. 接收缓冲、控制消息、待处理输入、订阅数和输出队列都有消息数与字节数硬上限。慢连接只关闭自身，
   不阻塞 Worker、Hub publisher、其他客户端或 Session。

## 2. 范围与非目标

### 2.1 本任务交付

- 原生 ASP.NET Core WebSocket endpoint、子协议协商、严格 Origin/Cookie gate 和应用握手；
- WebSocket v1 envelope、封闭消息 union、JSON Schema、source-generated JSON context 和 golden JSON；
- `session.resume`、`session.unsubscribe`、完整 Snapshot、display batch、resync、Session 流终止消息；
- `session.input` 的 text/button/pointer/keyboard 载荷、IPC correlation 和确定结果；
- API 内实时路由、输入/close 线性化、每连接有界调度、心跳和关闭策略；
- 瞬时连接注册表、全局/逐 Session 连接上限和不参与 Session 状态的连接诊断；
- Realtime 单元/集成测试、真实 Kestrel + Worker 链路测试及 P1-11 类型生成输入。

### 2.2 明确不做

- 不保存 Snapshot、显示增量、WebSocket 连接、订阅或输入回执到 SQLite；
- 不实现 `lastSequence`/ack 历史补发、增量环形日志或 snapshot/subscribe 无丢失屏障；
- 不跨 Worker epoch 保留输入去重，不在 API 建第二份输入去重缓存；
- 不增加多客户端控制权租约、主持人或“当前标签页”概念；
- 不因浏览器断开、心跳超时、零订阅或慢消费关闭 Worker或改变 Session 状态；
- 不在 API 重写 prompt 约束、默认值、OneInput、timeout、`ISTIMEOUT` 或系统输入语义；
- 不把原始 HTML、路径、URL、大型静态资源或任意 CLR 多态类型放入实时协议；
- 不实现 P1-11 的 React store、DOM/Canvas/WebAudio 渲染、软键盘或可视化重连体验；
- 不恢复 API 重启前的连接或旧 Worker。API/IPC 断开行为继续遵循 P1-S01：Worker 有界退出，Session
  对账为 `CRASHED` 后由用户显式 reopen。

## 3. 已有基线与实现缺口

### 3.1 可直接复用

- `RealtimeOriginValidator` 已验证 WebSocket upgrade、精确 `PublicOrigin`、实时 Cookie Session，且
  `AuthorizeResumeAsync` 每次调用中央 `IResourceAuthorizer`；
- `SessionOutputHub` 已维护每 Worker epoch 一份完整镜像，提供惰性编码 Snapshot、连续 batch、
  byte/count 双预算 subscription、缺口重同步和慢消费者判定；
- `RealtimeSnapshot`、`RealtimeTransactionBatch`、`RealtimeResyncRequired` 已覆盖 P1-07 完整显示状态；
- Worker v3 IPC 已有 `SubmitInput`/`InputResult`，`InputCoordinator` 已实现 prompt 原子抢占、
  `clientMessageId` 有界回执、重复/冲突、格式、取消和 timeout 竞争；
- Worker Manager 已校验 `controlPlaneInstanceId + sessionId + workerId + epoch`，P1-S01 已确保控制流
  断开后 Worker 不重注册而是有界退出；
- P1-06 已把 `BeginStopping` 定义为 close/input 线性化点，并提供持久 binding、状态和当前 Worker 路由。

### 3.2 必须补齐

- 尚未调用 `UseWebSockets`，也没有 `/api/v1/realtime` 生产 endpoint；
- `CloudEmuera.Contracts` 只有显示 payload，没有外层 envelope、握手、resume、input、result 和错误 DTO；
- `ICurrentWorkerRouter` 只返回生命周期 `IWorkerProcessHandle`，不能原子取得 Hub 与受控输入 dispatcher；
- `ApiWorkerSession.SendInputAsync` 只写命令，不按 IPC `correlationId` 等待结果，并固定为 keyboard；
- 通用 `pendingEvents` probe 会被预算淘汰，不能作为并发输入请求的确定回执通道；
- realtime input 尚未和 lifecycle close 共用命令门；“先查 RUNNING 再发送”会与 `BeginStopping` 竞争；
- 没有连接级订阅上限、pending input 上限、公平发送、心跳超时、严格 JSON/fragment/二进制处理与
  WebSocket close code 映射。
- 没有瞬时连接注册表、全局/逐 Session 连接门控和包含外层 envelope 的最终发送字节预算。

## 4. 分层和组件归属

```text
Browser WebSocket
       │ v1 strict JSON envelopes
       ▼
RealtimeEndpoint / RealtimeConnection (API adapter)
       ├─ RealtimeAuthorizationGate ── Identity + ResourceAuthorizer
       ├─ RealtimeConnectionRegistry ── transient counts + admission
       ├─ RealtimeConnectionWriter ── control priority + fair session readiness
       └─ RealtimeSessionRegistry (API-local adapter)
                    │ atomically resolves exact current binding
                    ├─ SessionOutputHub.Subscribe()
                    └─ RealtimeInputDispatcher
                              │ shared SessionCommandGate
                              ├─ persistent RUNNING/binding validation
                              └─ Worker v3 SubmitInput / correlated InputResult
```

职责：

- `CloudEmuera.Contracts/Realtime`：公开 envelope 和消息 DTO、稳定 error/result code、JSON source context；
- `CloudEmuera.Application/Sessions`：传输无关的输入命令/结果、持久 binding 检查端口和可复用的
  `ISessionCommandGate`；不得引用 ASP.NET、WebSocket、Hub 或 protobuf 类型；
- `CloudEmuera.Infrastructure/Sessions`：在短 SQLite 读取中确认 `RUNNING`、当前 lease、epoch、worker、
  control-plane 与 stateVersion；不等待 IPC；
- `CloudEmuera.Api/Realtime`：连接状态机、严格 JSON、授权、订阅、多路复用、发送调度和错误映射；
- `CloudEmuera.Api/Workers`：原子实时路由、IPC 命令 correlation、pending request 上限和 Worker 结果映射；
- `CloudEmuera.Worker`/`RuntimeAdapter`：继续是 prompt、格式、去重、首个有效输入和 timeout 的唯一权威。

WebSocket handler 不枚举 `WorkerManager.Workers`，不直接操作 `Process`/gRPC，不自行读取
`SessionOutputHub` 私有状态，也不把内存 route 当成持久 Session 状态的替代品。

`RealtimeConnectionRegistry` 只保存 `connectionId/actorUserId/sessionIds/connectedAt/lastActivityAt`
和有界队列统计。它原子维护全局连接数与逐 Session 已订阅连接数，只用于 admission、诊断和后续
P1-12 管理视图，不保存 Cookie、prompt、输入或 Worker 状态，不写 SQLite，也不参与 Session 状态机。

## 5. WebSocket v1 连接与消息封装

### 5.1 Endpoint 和子协议

```http
GET /api/v1/realtime
Origin: https://configured-public-origin
Cookie: <protected auth session>
Sec-WebSocket-Protocol: cloudemuera.realtime.v1
```

- endpoint 必须 `RequireAuthorization()`，upgrade 前调用 `RealtimeOriginValidator.IsUpgradeAllowedAsync`；
- upgrade 必须位于 `UseAuthentication/UseAuthorization` 之后，并要求 Worker Manager、启动对账和
  Session operation recovery readiness 可接收实时流量；API draining 时以 `503` 拒绝新连接；
- 缺失/错误 Origin、已撤销或禁用账户、强制改密账户均不得建立可用连接；
- 客户端必须提出且服务端必须回显 `cloudemuera.realtime.v1`，不静默选择未知版本；
- 非 WebSocket 请求返回 `400`，未认证返回 `401`，Origin/账户策略失败返回 `403`，无共同子协议返回
  `426`；响应均 `Cache-Control: no-store`；
- upgrade 在 registry 中原子取得全局连接名额后才能 accept；失败或 accept 抛错必须释放预留。逐
  Session 上限在 resume 注册 subscription 时原子检查，因为 upgrade 时尚不知道目标 Session；
- WebSocket 不要求 `X-CSRF-TOKEN`：SameSite Cookie、精确 Origin、v1 subprotocol 与每次资源操作重新
  鉴权共同构成跨站请求边界；这不改变普通 HTTP 写操作的 CSRF 要求；
- 不启用 `permessage-deflate`。大型 raster 已受 Snapshot 上限约束，压缩会增加状态和资源边界复杂度。

### 5.2 统一 envelope

所有方向的应用消息都是单个 UTF-8 JSON object：

```json
{
  "protocolVersion": 1,
  "type": "session.input",
  "messageId": "msg_01K...",
  "correlationId": null,
  "sessionId": "sess_01K...",
  "workerEpoch": 4,
  "sequence": null,
  "payload": {}
}
```

约束：

- `protocolVersion/type/messageId/payload` 必填；`correlationId` 只用于响应对应请求；
- Session 消息必须含 `sessionId`；显示和输入消息必须含非零 `workerEpoch`；Snapshot 的 `sequence`
  等于 `snapshotSequence`，batch 的 `sequence` 等于 `lastSequence`；
- connection 级 hello/ping/pong 不含 Session、epoch、sequence；
- 标识符采用现有有界 ASCII identifier 规则。为保持连接内内存有界，服务端保留最近 4096 个客户端
  `messageId` 做重复检测；窗口内不得重复，窗口淘汰后的旧 ID 不再提供永久重放保护，客户端不得复用任何
  已发送 ID；
- 未知可选字段忽略；未知 `type`、缺失必填字段、错误 discriminator、重复 JSON property、非法 UTF-8、
  非有限数字或超过深度/大小上限的消息拒绝；
- 不使用 `JsonElement` 在连接生命周期内保留整棵输入 DOM。先用 `Utf8JsonReader` 完成结构、重复键、
  深度和 discriminator 校验，再 source-generated 反序列化到封闭 DTO；
- 服务端 envelope 通过 `IBufferWriter<byte>` 把已有的 P1-08 payload bytes 包入外层，不反序列化再序列化。

### 5.3 最终帧字节预算

P1-08 的 `SnapshotMaxBytes` 只约束 payload，P1-09 还必须约束最终 WebSocket message：

- Snapshot/batch 使用 `Utf8JsonWriter.WriteRawValue(validatedPayload, skipInputValidation: true)` 嵌入已经
  由 P1-08 source-generated serializer 产生并校验的 JSON；其他来源不得使用 skip validation；
- `EnvelopeMaxBytes` 是 envelope 固定字段、最大标识符和稳定 reason 的可证明上界，默认 2 KiB；
- `ServerMessageMaxBytes = checked(SnapshotMaxBytes + EnvelopeMaxBytes)`，启动时校验且不得溢出；
- 编码完成后以实际 UTF-8 字节再次检查。超过 payload 预算是 Hub/配置错误，超过 envelope 上界是
  WebSocket 协议实现错误，两者都 fail closed，不能截断 Snapshot 或删除字段；
- 大小计算覆盖 resync marker 和 Snapshot replacement group 中的每条独立 WebSocket message；静态资源
  仍不经过实时通道。

### 5.4 消息集合

| 方向 | type | 用途 |
| --- | --- | --- |
| C→S | `client.hello` | 声明协议版本、显示 capability set/digest |
| S→C | `server.hello` | 选择 v1、给出 connectionId、server time、心跳和容量参数 |
| C→S | `connection.pong` | 回应应用心跳 |
| S→C | `connection.ping` | 检测失效浏览器连接，不影响 Session |
| C→S | `session.resume` | 授权并订阅当前 Worker epoch |
| S→C | `session.resume.result` | 返回订阅结果及当前 epoch |
| C→S | `session.unsubscribe` | 只释放当前连接的订阅 |
| S→C | `session.snapshot` | 完整替换显示状态 |
| S→C | `display.batch` | 连续 transaction batch |
| S→C | `resync.required` | 通知随后 Snapshot 必须完整替换 |
| S→C | `session.stream.ended` | 当前 epoch 输出流结束，要求显式 resume/reopen |
| C→S | `session.input` | 向指定 epoch/prompt 提交输入 |
| S→C | `session.input.result` | 确定输入结果 |
| S→C | `protocol.error` | 可恢复的请求/消息协议错误 |

不为每个 display batch 要求客户端 ack。`session.resume` 可以带 `lastEpoch` 供 UI 诊断，但服务端不接收
`lastSequence` 作为补发承诺，也不根据 `lastEpoch` 跳过 Snapshot。

## 6. 应用握手、认证与连接状态机

### 6.1 状态机

```text
HTTP Upgrade
  └─ accepted ─► AwaitingClientHello
                    ├─ valid hello ─► Ready
                    └─ timeout/error ─► Closing

Ready
  ├─ resume ─► add/replace bounded SessionSubscription
  ├─ input  ─► bounded dispatch; connection remains Ready
  ├─ auth revoked / heartbeat timeout / fatal protocol error ─► Closing
  └─ peer close ─► Closing ─► Closed
```

服务端在 upgrade 后等待 `client.hello`，默认 5 秒。hello 声明客户端支持的 protocol version 和 P1-07
display capability digest；服务端回复 `server.hello`。客户端缺少当前 Runtime manifest 的必需
Supported capability 时，连接仍可用于显示明确错误，但对应 `session.resume` 返回
`CLIENT_CAPABILITY_MISMATCH`，不得降级成纯文本或原始 HTML。

### 6.2 重新认证

upgrade 时的 `HttpContext.User` 不能成为长连接永久授权。新增 `RealtimeAuthorizationGate`：

1. 从连接固定的 `userId + authSessionId + securityStamp` 重新调用 `ValidateSessionAsync`；
2. 取得当前用户状态和 `mustChangePassword`；
3. resume 使用 `ResourceAction.SessionResume`，input 使用 `ResourceAction.SessionControl`；
4. `NotFoundOrHidden` 对客户端统一映射为 `SESSION_NOT_FOUND`，不区分不存在与越权；
5. `PasswordChangeRequired` 返回 `PASSWORD_CHANGE_REQUIRED` 并关闭连接；
6. auth session 已撤销、用户禁用、安全戳变化时发送通用 `AUTHENTICATION_EXPIRED` 后以策略错误关闭，
   同时释放全部订阅；不关闭任何 Worker。

每次 resume/input 都执行上述检查；另外在应用 ping 周期至多每 60 秒复核一次登录态，使管理员禁用、
改密或 logout 能最终终止无输入的旧长连接。周期复核不替代资源操作边界的即时检查。

## 7. Session resume 与 Snapshot 恢复

### 7.1 原子实时路由

API 外层新增 `IRealtimeSessionRegistry`，由 Worker Manager 实现：

```text
TrySubscribe(sessionId)
  -> RealtimeSessionRoute(binding, subscription, capabilityDigest)

TryDispatchInput(sessionId, expectedEpoch, command)
  -> Task<RealtimeInputResult>
```

`TrySubscribe` 在 Worker Manager registry 锁内定位唯一 current `ApiWorkerSession`、捕获不可变 binding 并
调用该 Worker 的 Hub `Subscribe`。返回后 Worker 被移除、epoch 替换或 Hub 完成是正常竞态，表现为
subscription completed；不能切换到新 Worker。`TryDispatchInput` 同样必须再次匹配 current binding，
不能使用 resume 时长期保存的进程引用绕过 epoch fencing。

### 7.2 resume 算法

1. 严格验证 envelope、sessionId 和 capability 声明；
2. 重新验证实时身份与 `SessionResume` 权限；
3. 通过 registry 原子取得当前 binding 与 Hub subscription；无活动 Worker 返回
   `SESSION_NOT_RUNNING`，但不把 `CLOSED`/`CRASHED` 误报为连接失败；
4. 在连接的 subscription map 中按 sessionId 原子替换旧 subscription，达到连接上限则返回
   `SUBSCRIPTION_LIMIT_EXCEEDED`；
5. 发送 `session.resume.result(ACCEPTED, currentEpoch)`；
6. subscription 第一帧必须是 `session.snapshot`，客户端以完整状态替换本地 store；
7. 之后只发送 `firstSequence == expected + 1` 的 `display.batch`；P1-08 已在不连续时返回替换 Snapshot；
8. resync 时把 `resync.required` 与紧随其后的 `session.snapshot` 作为一个 writer work item，保持顺序；
9. subscription 完成后先排空 P1-08 明确保留的 pending frame，再发 `session.stream.ended` 并从连接移除。

同一连接再次 resume 同一 Session 是幂等替换，不建立两个 Hub subscription。新订阅成功进入 map 后才
dispose 旧订阅；失败保留旧订阅。epoch 变化后不自动跟随新 Worker，客户端必须显式 resume，使新 epoch
再次经过身份、授权、capability 和完整 Snapshot 边界。

### 7.3 多 Session 与多客户端

- 每连接默认最多订阅 4 个 Session，绝对上限 16；配置必须在启动时校验；
- registry 默认允许全实例 256 个 WebSocket 连接、同一 Session 16 个已订阅连接；达到全局上限时
  upgrade 失败，达到逐 Session 上限时只拒绝该次 resume。两者都是部署级容量保护，不是用户配额；
- 每个 Session subscription 继续使用 P1-08 独立 byte/count 队列，因此连接总显示内存上界是
  `MaxSubscriptionsPerConnection × QueueHardLimit`；
- 多个连接可订阅同一 Hub，复用其不可变编码 payload；一个连接的 resync/关闭不影响其他连接；
- `unsubscribe`、页面刷新或网络断开只 dispose subscription，不写 SQLite、不更改 RUNNING、不取消 prompt；
- 连接/订阅创建、替换、unsubscribe、peer close、异常和 cancellation 的所有路径都必须恰好一次更新
  registry；关闭连接只移除 Hub subscription，绝不调用 `SessionOutputHub.Complete()`。

## 8. 输入协议和一致性

### 8.1 请求 DTO

```json
{
  "protocolVersion": 1,
  "type": "session.input",
  "messageId": "msg_01K...",
  "sessionId": "sess_01K...",
  "workerEpoch": 4,
  "payload": {
    "promptId": "prompt_01K...",
    "clientMessageId": "cmsg_01K...",
    "source": "BUTTON",
    "value": "0",
    "pointer": null,
    "key": null
  }
}
```

- 浏览器只可发送 `KEYBOARD`、`BUTTON`、`POINTER`；`SYSTEM` 是 Runtime 内部来源，WebSocket 必须拒绝；
- `pointer` 只允许在 source 含 POINTER 时出现，坐标、button 和 pressed 沿用 RuntimeAdapter 边界；
- `key` 只允许在 source 含 KEYBOARD 时出现，keyCode 0..255，修饰键为布尔值；
- `promptId/clientMessageId` 最大 128 字符，value 最大 16 KiB，与 `ConsoleContractLimits` 同源，不复制
  魔数；结构错误返回 `INVALID_COMMAND`，prompt 相关语义继续交给 Worker；
- 客户端生成一次 `clientMessageId` 后，网络重试必须复用同一 ID 和完全相同 payload；不得因未收到
  回执而生成新 ID。

### 8.2 处理与线性化顺序

每条输入按以下顺序处理：

1. 连接握手完成且消息结构有效；
2. 重新验证登录态和 `SessionControl` 权限；
3. 获取共享 `ISessionCommandGate` 的 session gate；
4. 在短 SQLite 读取中要求 Session 为 `RUNNING`，当前 lease 精确匹配
   `controlPlaneInstanceId + sessionId + workerId + epoch + stateVersion`；
5. 在 Worker Manager registry 内再次匹配 current route 和请求 epoch；
6. 为 IPC command 注册有界 correlation waiter，然后把 `SubmitInput` 写入 Worker 控制队列；
7. command 已成功入队即释放 session gate，不在 gate 内等待 Worker 结果；
8. Worker 串行执行去重键、当前 prompt、格式和原子抢占并返回 `InputResult`；
9. API 按 IPC `correlationId` 完成唯一 waiter，并映射为 WebSocket result。

`SessionLifecycleExecutor.CloseAsync` 必须从调用 `BeginStopping` 前到 STOP command 成功入队共用同一
session gate。这样先入门的 input 先进入 Worker command queue；先入门的 close 完成
`BeginStopping` 后，后续 input 读取持久状态并返回 `SESSION_NOT_ACCEPTING_INPUT`。SQLite 状态和 lease
仍是权威，进程内 gate 只负责同一 API 的外部副作用排序，不能替代持久检查。

不得以数据库 `waiting_for_input/current_prompt_id` 作为输入正确性依据；heartbeat 观察值可能滞后，
最终 prompt/timeout/格式判断必须由 Worker 当前 `InputCoordinator` 完成。

### 8.3 IPC correlation

为 `ApiWorkerSession` 增加专用 `SubmitInputAsync(command, deadline)`：

- 生成 IPC command messageId，并先把 `TaskCompletionSource` 注册到以 messageId 为键的 pending map；
- pending map 同时受数量和估算字节上限约束；满时不写 IPC，返回 `INPUT_BACKPRESSURE`；
- 收到 `InputResult` 必须同时匹配 binding、非空 correlationId、原 command、promptId 和
  clientMessageId，才完成 waiter；未知/迟到 correlation 只记有界诊断，不进入通用 event probe；
- write 失败、deadline、Worker exit、Hub/connection dispose 会确定性完成并移除 waiter；
- timeout 表示 API 未取得回执，不表示 Emuera prompt timeout，也不能由 API选择默认值；客户端只能用
  原 `clientMessageId` 重试；
- 通用 `pendingEvents` 保留测试/生命周期观察用途，但输入回执不得依赖其可能淘汰的列表。

### 8.4 WebSocket 输入结果

```json
{
  "protocolVersion": 1,
  "type": "session.input.result",
  "messageId": "msg_01K...",
  "correlationId": "msg_01K_request",
  "sessionId": "sess_01K...",
  "workerEpoch": 4,
  "payload": {
    "promptId": "prompt_01K...",
    "clientMessageId": "cmsg_01K...",
    "status": "ACCEPTED",
    "reasonCode": "accepted",
    "normalizedValue": "0"
  }
}
```

公开 status：

| status | 来源/含义 |
| --- | --- |
| `ACCEPTED` | 本消息首次赢得 prompt |
| `DUPLICATE` | 同 ID、同 fingerprint 已处理；附原规范化值 |
| `CONFLICT` | 同一 clientMessageId 被不同 prompt/payload 复用 |
| `STALE_PROMPT` | prompt 已完成、超时或被其他客户端回答 |
| `NO_ACTIVE_PROMPT` | Worker 当前无 prompt |
| `INVALID_FORMAT` | 值或 source 不符合当前 prompt |
| `INVALID_COMMAND` | 结构上可解码但违反输入契约 |
| `SESSION_NOT_ACCEPTING_INPUT` | 非 RUNNING、正在 close 或 control plane draining |
| `STALE_EPOCH` | 请求 epoch 不是当前 Worker epoch |
| `SESSION_NOT_RUNNING` | 当前无可路由 Worker |
| `FORBIDDEN` | 已订阅后权限失效；随后释放订阅或关闭失效身份连接 |
| `INPUT_BACKPRESSURE` | 本连接或 Worker pending input 已达硬上限 |
| `WORKER_UNAVAILABLE` | IPC 断开、Worker 退出或回执 deadline 到期 |

Worker 的 `CANCELLED/TIMED_OUT` 是 prompt 自身的内部完成原因，正常通过后续 Snapshot/transaction
呈现；若它们作为某个相关 input result 到达则原样映射，但 API 不主动生成。

## 9. 连接内并发、背压与公平性

### 9.1 接收路径

- 单一 receive loop 处理 fragment，使用池化 buffer 拼成一条消息；默认客户端消息上限 64 KiB、JSON
  深度 32，超过上限立即以 `1009` 关闭；
- binary frame 以 `1003` 拒绝；混合 message type、无终止 fragment、非法 UTF-8 或重复属性是 `1002`；
- resume/unsubscribe 串行修改 subscription map；input 投递到有界并发执行器，不能阻塞 pong 和 close；
- 每连接默认最多 32 个 pending input，绝对上限 256。达到上限对该请求返回
  `INPUT_BACKPRESSURE`，不继续读入无界任务；
- peer close 触发连接 cancellation，等待有界时间取消 pumps 和 pending API waiters，再回送 close。

### 9.2 发送路径

同一 `WebSocket` 只能有一个 writer。`RealtimeConnectionWriter` 不再复制一套显示历史：

- control queue 保存 hello、resume/input result、ping/error/stream-ended，默认 64 条/256 KiB 双预算；
- 每个 subscription reader 同时至多发布一个“ready token”，实际 display payload 仍留在 P1-08 queue；
- 单 writer 先处理控制消息，再以 round-robin 每个 ready Session 至多取一个 frame，避免一个高输出
  Session 饿死同连接的其他 Session；
- resync marker + replacement Snapshot 是不可拆分 writer group；Snapshot 和 batch envelope 按已有 bytes
  直接包裹后发送，不长期复制到第二队列；
- control queue 满表示连接无法维持协议控制，关闭该连接；不向 Worker/HUB 反压；
- 单次 `SendAsync` 有 deadline。超时或 `RealtimeSlowConsumerException` 以 `1008 slow-consumer` 关闭连接；
- 多 Session 下连接显示内存上界由订阅数和 P1-08 queue hard limit 相乘，控制/pending input 另有明确
  上限，不能创建无界 `Channel`、Task 或 byte array 列表。

### 9.3 心跳

浏览器 API 不能主动发送 RFC 6455 ping，因此使用应用 `connection.ping/pong`：

- 默认 20 秒发送 ping，pong deadline 10 秒；任何有效入站消息也可更新 transport activity，但不能伪造
  对指定 ping nonce 的 pong；
- 连续一次 deadline 未收到匹配 pong 即关闭该 WebSocket 并释放订阅；
- Kestrel transport keepalive 可作为网络辅助，但不代替应用 heartbeat 测试；
- heartbeat timeout 不 close Session、不 stop Worker、不清 prompt。

## 10. 错误、终止与版本兼容

### 10.1 session 局部错误

resume/input 的业务失败通过带 request `correlationId` 的结果消息返回，默认保持连接：

- 不存在与越权 resume 都返回 `SESSION_NOT_FOUND`；
- 静止 Session 返回 `SESSION_NOT_RUNNING`；旧 epoch 输入返回 `STALE_EPOCH`；
- Hub 尚未取得首 Snapshot 可返回 `SNAPSHOT_NOT_READY`，客户端按带抖动退避重新 resume；
- `session.stream.ended` 只终止该 epoch subscription。`CLOSED/CRASHED` 的权威状态由 HTTP Session API
  查询，不从 Hub reason 猜测。

### 10.2 connection 级关闭

| WebSocket close code | 场景 |
| --- | --- |
| `1000` | 客户端正常关闭或服务端正常完成连接 |
| `1002` | envelope/schema/fragment/版本协议错误 |
| `1003` | binary 或不支持的数据类型 |
| `1008` | Origin/auth 策略失效、hello/heartbeat timeout、slow consumer |
| `1009` | 单消息超过硬上限 |
| `1011` | 无法保持协议一致性的内部/Hub fault |
| `1012` | API draining/restart |

close reason 必须是短稳定代码，不包含 SessionRoot、异常栈、Cookie、用户输入或游戏文本。能够安全发送时
先发一个 `protocol.error`，但连接正确性不能依赖该错误消息必达。

### 10.3 兼容规则

- v1 `type` 和枚举是封闭集合；客户端忽略未知可选字段，但不得忽略未知必需 type/discriminator；
- 增加可选字段不升级协议；改变字段含义、删除字段或新增必需行为必须发布新子协议和 schema；
- `server.hello` 返回 WebSocket protocol version、Realtime payload schema version、Runtime capability
  digest，不与 HTTP API 或 Worker IPC version 混成一个数字；
- 无共同协议版本时在 upgrade 阶段失败；绝不把 v1 消息按“尽力而为”发送给未知客户端。

## 11. 配置、诊断与隐私

新增 `RealtimeGatewayOptions`，启动时 validate：

```text
ClientHelloTimeout                 5 s
ClientMessageMaxBytes             64 KiB
ClientJsonMaxDepth                 32
MaxConnections                    256     (absolute 4096)
MaxConnectionsPerSession          16      (absolute 256)
MaxSubscriptionsPerConnection     4       (absolute 16)
MaxPendingInputsPerConnection      32      (absolute 256)
MaxPendingInputsPerWorker          128     (absolute 2048)
ControlQueueMaxMessages            64
ControlQueueMaxBytes               256 KiB
EnvelopeMaxBytes                   2 KiB
ServerMessageMaxBytes              SnapshotMaxBytes + EnvelopeMaxBytes
InputResultTimeout                 10 s
WebSocketSendTimeout               10 s
HeartbeatInterval                  20 s
HeartbeatTimeout                   10 s
IdentityRevalidationInterval       60 s
ConnectionShutdownTimeout          5 s
```

`RealtimeOutputOptions` 仍由 P1-08 独立拥有 Snapshot/batch/逐订阅队列限制，P1-09 不复制或改名。

结构化日志/指标只记录：`requestId/connectionId/sessionId/workerId/workerEpoch`、message type、sequence
范围、payload bytes、subscription count、pending input count、resync/close/result stable code 和耗时。
不得记录 Cookie、security stamp、prompt text、按钮值、input value、normalizedValue、Snapshot JSON、
游戏文本或 SessionRoot。`clientMessageId` 默认也不写日志；需要关联时只使用本次 envelope messageId 或
不可逆短哈希。

至少暴露聚合诊断：活动连接/订阅数、resume 成功/失败、Snapshot 恢复时延、resync 次数、队列溢出、
slow-consumer close、输入结果分类、IPC input latency 和 auth-revoked close。不得建立按用户输入内容的
高基数标签。

`GET /api/v1/version` 必须分别公开 HTTP API、Worker IPC、Realtime WebSocket 和 Realtime payload
schema 版本；不得继续用一个含糊数字代表所有协议。

## 12. 测试方案

### 12.1 Contracts 与严格解析

- 每种 v1 消息的 golden JSON 与 JSON Schema；camelCase、64-bit epoch/sequence、Unicode round-trip；
- 缺失字段、重复属性、未知 type/discriminator、非法 enum/identifier/UTF-8、NaN/Infinity、深度和大小；
- unknown optional field 被忽略；协议版本、subprotocol 和 capability mismatch 被稳定拒绝；
- envelope 包装不改变 P1-08 Snapshot/batch payload，且不出现 raw HTML、URL、路径或 CLR type metadata；
- payload 恰好为 SnapshotMaxBytes 时最终 envelope 仍在计算上限内；envelope 超限或 checked overflow
  fail closed，已验证 payload 使用 raw embedding 后字节保持一致。

### 12.2 Upgrade、身份和授权

- 匿名、无 Origin、错误 Origin、非 WebSocket、撤销/过期 Cookie、disabled user、security stamp 改变；
- 精确 Origin 和 v1 subprotocol 成功；伪造 forwarded host 不能绕过配置 Origin；
- readiness 未完成、API draining、全局连接预留达到上限以及 accept 失败释放预留；
- 每次 resume 和 input 都调用 live session validation 与中央 authorizer；
- 跨用户 Session 返回与不存在相同结果；强制改密禁止 resume/input；
- 连接建立后 logout/禁用/改密由下一操作即时发现、无操作连接由周期复核发现。

### 12.3 Resume、竞态和背压

- 初次连接、刷新、断网后重连都先收到完整 Snapshot，包含 prompt、deadline、background、scene、media、
  window 和 truncation；
- Snapshot 取得期间持续输出最终连续或收到 resync + 更新 Snapshot；静默单条竞态也被发现；
- 断线期间 timed prompt 继续运行，重连保留原 deadline，不重新计时；已 timeout 时不恢复旧 prompt；
- 同一连接重复 resume 原子替换；多个 Session 公平发送；多个连接独立订阅同一 Session；
- 逐 Session 连接上限只拒绝超限 resume；registry 在 unsubscribe、异常、peer close 和 cancellation 后
  计数归零，且从不写 Session/SQLite；
- old epoch 完成且不能混入 new epoch；reopen 后必须重新授权并取得新 Snapshot；
- soft/hard queue overflow、三次 Snapshot replacement 失败和 send timeout 只关闭慢连接；
- 1,000 次 reconnect/unsubscribe、连接上限、持续 100,000 batch 后内存达到稳定平台且引用释放。

### 12.4 输入与生命周期

- ACCEPTED、DUPLICATE（同值）、CONFLICT（同 ID 异值）、STALE_PROMPT、NO_ACTIVE_PROMPT、
  INVALID_FORMAT、INVALID_COMMAND 的端到端映射；
- 两连接并发回答同一 prompt 只有一个 ACCEPTED，另一结果稳定；
- input 与 Worker timeout 在所有可控切点竞争，Runtime 只观察一个权威结果；API 不产生默认值；
- 输入先于 close gate 入队时允许完成；`BeginStopping` 先线性化时输入稳定为
  SESSION_NOT_ACCEPTING_INPUT；close 完成后输入不进入 Worker；
- old epoch、Worker 替换、IPC 断开、结果 deadline、迟到/伪造 correlation、pending map overflow；
- API 回执丢失后以相同 clientMessageId 重试只执行一次；缓存淘汰后已完成 prompt 仍 STALE_PROMPT；
- pointer/key 边界、browser SYSTEM source 拒绝、OneInput 规范化、16 KiB 边界；
- 输入结果、日志和测试失败输出不泄漏敏感 value。

### 12.5 真实 Kestrel/Worker 与浏览器 smoke

- 使用真实 cookie 登录、Kestrel WebSocket、UDS Worker：open → resume → prompt → input → output；
- 断开浏览器而 Worker 持续运行，再连接取得完整 Snapshot；
- 切断 Worker IPC 后 Worker 有界退出、Hub stream ended、Session 对账为 CRASHED，连接不假装恢复旧
  runtime；reopen 后 epoch 增大并取得新 Snapshot；
- API 正常 draining 以 1012 关闭连接并有界停止 Worker；API kill 后不恢复旧连接；
- 最小 Playwright smoke 只验证协议接通与刷新恢复，完整 renderer/移动/视觉断言留给 P1-11/P1-15。

测试必须使用 dev Docker、临时 DataRoot、独立 Compose project/端口，不读取或修改人工 `.env`、
`./data`。

## 13. 实施切片

### 切片 1：冻结协议和失败测试

- 新增 ADR-0021，冻结 v1 subprotocol/envelope、消息集合、resume/input 线性化点、结果/关闭码及
  不补历史、不持久去重、不跨 epoch 恢复的边界；接受后再实现协议代码；
- 在 `CloudEmuera.Contracts` 增加 v1 envelope/hello/resume/input/result/error DTO 与 source context；
- 增加 JSON Schema、golden fixtures、严格解析器和协议/大小失败测试；
- 增加 `RealtimeGatewayOptions` 及启动校验；
- 同步 OpenAPI 外的 WebSocket schema 发布位置和 P1-11 类型生成入口。

### 切片 2：输入应用端口和 close 线性化

- 抽取 `ISessionCommandGate`，由 lifecycle open/close 与 realtime input 共用；
- 增加持久 RUNNING/current binding 校验端口及 SQLite 实现；
- 增加传输无关输入 command/result；覆盖 input/close、epoch 和 SQLite failure 竞态；
- 不改 Worker 的 prompt、timeout 或 receipt 算法。

### 切片 3：Worker realtime registry 与 IPC correlation

- Worker Manager 实现原子 subscribe/dispatch route；
- 为 `ApiWorkerSession` 增加专用 bounded pending input map 和 correlation completion；
- 扩展 text/button/pointer/key 映射并拒绝 browser SYSTEM source；
- Worker disconnect/dispose 完成所有 waiter，覆盖迟到/伪造结果和 backpressure。

### 切片 4：WebSocket endpoint、身份和连接状态机

- 接入 `UseWebSockets`、`/api/v1/realtime`、精确 Origin、v1 subprotocol 和 hello deadline；
- 实现瞬时 connection registry、readiness/draining gate、全局与逐 Session admission，并保证所有
  失败/关闭路径释放计数；
- 实现 live authorization gate、resume/unsubscribe 和 generic hidden error；
- 实现 fragmentation/strict UTF-8/duplicate-property/size/depth gate；
- 覆盖 upgrade、logout/disable/change-password、跨用户和协议错误。

### 切片 5：输出调度、resync 和心跳

- 将 P1-08 subscription 接到单 writer；实现 control priority、ready token 和 Session round-robin；
- 实现 resume result → initial Snapshot、resync group、stream ended 和 epoch replacement；
- 实现应用 ping/pong、send deadline、connection cancellation 和 close code；
- 覆盖快慢客户端、多 Session、多连接、重复 resume、overflow 和释放。

### 切片 6：真实链路、容量、文档与交接

- 完成 Kestrel + auth + SQLite + Worker UDS 纵切和最小 Playwright smoke；
- 运行 reconnect/input/close/timeout/IPC disconnect 故障注入与稳定平台测试；
- 生成 P1-11 可消费的 TypeScript 协议类型输入，但不实现 renderer；
- 同步中英文 requirements/design/development plan、配置示例和协议版本信息；
- 执行完整 dev Docker 检查、dev-user、third-party 和 `git diff --check`。

切片按顺序合并。Contracts schema 冻结后，严格解析测试和 Worker correlation 可并行准备，但
`SessionCommandGate`、Worker Manager registry、`Program.cs` 和共享 schema 必须由单一集成修改协调。

## 14. 验证命令

所有命令必须通过仓库 dev Docker 运行；开发计划中的裸 `dotnet test` 示例在实施时同步修正为：

```bash
./scripts/dev-up.sh
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Realtime.Tests --no-restore --configuration Release \
  --filter 'Category=Reconnect|Category=InputDeduplication|Category=WebSocketProtocol|Category=Authorization|Category=Backpressure'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release \
  --filter 'Category=Realtime|Category=Authorization|Category=SessionLifecycle'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release \
  --filter 'Category=Realtime|Category=Input|Category=WorkerDisconnect'
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
```

## 15. 完成条件

1. `/api/v1/realtime` 只接受实时有效登录、精确 Origin 和共同 v1 subprotocol；
2. 每次 resume/input 都重新检查登录态与资源权限，跨用户资源不可枚举；
3. 每个订阅首个显示基线是当前 epoch 的完整 Snapshot，缺口/overflow 只以更新 Snapshot 收敛；
4. Snapshot 恢复完整覆盖 P1-07 状态且不改变 prompt deadline；
5. 没有 ack history、持久 Snapshot、持久 input receipt 或跨 epoch 去重；
6. 同一 clientMessageId 重试只执行一次；异值复用冲突；多客户端只有一个输入获胜；
7. Worker 是 prompt、输入格式、OneInput、默认值和 timeout 的唯一裁决者；
8. input/close 共享线性化门，BeginStopping 后的新输入不写入 Worker；
9. IPC input result 使用专用 correlation map，不依赖可淘汰的通用 event probe；
10. 每连接订阅、接收、控制、pending input、发送和每 subscription 输出均有经测试的硬上限；
11. 慢/断开的浏览器不阻塞 Worker/其他客户端、不改变 Session 状态或清除 prompt；
12. Worker IPC 断开后按 P1-S01 有界退出并对账 CRASHED，API 重启不恢复旧实时连接；
13. 协议 schema/golden fixtures、配置、日志隐私和 P1-11 类型生成输入同步；
14. ADR-0021 已接受，`/version` 分别公开 Realtime WebSocket/payload 版本；
15. connection registry 的全局/逐 Session 计数在所有成功、失败和取消路径准确归零且不持久化；
16. 最终 WebSocket message（含 envelope）受实际 UTF-8 字节硬上限保护；
17. 第 14 节定向测试与 `./scripts/check.sh` 全部通过。

## 16. 后续任务交接

- P1-11 必须把 `session.snapshot` 作为 store 完整替换，把 `display.batch` 只按连续 sequence 归约；收到
  `resync.required` 后不得继续显示旧基线上的增量；
- P1-11 生成 `clientMessageId` 后必须持有到确定回执，重试复用同一 ID；不得以新 ID 自动重放未决输入；
- P1-11 使用 Snapshot 中原始 `deadlineUnixMilliseconds` 和 `server.hello.serverNow` 估算剩余时间，
  不因重连重新开始倒计时；
- P1-12 管理员 force-stop 复用 Session command gate 和 Worker lifecycle，不从 WebSocket 增加隐藏管理命令；
- P1-15 验证四浏览器、移动网络切换、后台标签页 timer throttling 与代表游戏，但不能改变本文的协议、
  fencing、权限或有界队列语义。

## 17. 实施记录

2026-08-14：修复 `SessionLifecycleExecutor` 具体类型单例注册，确保 lifecycle executor 与 realtime command
gate 共享同一实例；完成 ADR-0021、v1 envelope/source-generated context/JSON Schema、golden JSON、原生
`/api/v1/realtime` endpoint、live authorization、连接与订阅 admission、单 writer 公平调度、应用心跳、
Snapshot/resync 恢复、`SNAPSHOT_NOT_READY`、Worker correlated input receipt、输入/close 共享 command gate、
TypeScript 类型入口和状态机/背压/Worker pending-map 自动化测试。验证使用仓库 dev Docker：Realtime 41 项、
API Realtime/Authorization/SessionLifecycle 过滤集 9 项、Worker Realtime/Input/WorkerDisconnect 过滤集 4 项，
并通过真实 Kestrel + Cookie + UDS Worker 的 open → resume → Snapshot → prompt → input → output、浏览器断线
重连 Snapshot、控制流断开后有界退出并对账 `CRASHED`、reopen epoch 增长和 `1012 api_draining` close 纵切。
API 重启、完整 renderer 和移动/视觉验收仍按 P1-S01、P1-11、P1-15 的后续范围执行。
