# ADR-0021：冻结 Realtime WebSocket v1 与输入回执边界

状态：Accepted

日期：2026-08-14

关联：AUTH-001/002/003/005、SESS-003/004/006/007/010/011、PLAY-004～008、PLAY-010～012、
OPS-002/004、SEC-004/006/009、NFR-001/007/008/010/013、AC-002/005/006/012、ADR-0003、
ADR-0015、ADR-0017、ADR-0018、ADR-0020、P1-09

## 背景

P1-08 已完成 API 内的 `SessionOutputHub`、完整 `ConsoleSnapshot` 镜像和有界增量队列，但浏览器仍
没有正式的连接、恢复和输入协议。连接必须能在断网后重新建立完整基线，同时不能把实时连接误写成
Session 状态、把 API 重启前的 Worker 当成可恢复运行时，或在 API 中复制一份输入去重权威。

P1-09 还需要把 Worker v3 的 `SubmitInput`/`InputResult` 接到浏览器：网络重试必须复用
`clientMessageId`，同一 Worker 内相同 fingerprint 只执行一次，异值复用返回冲突；输入与 close 必须
共享 Session command gate，且最终 prompt、格式、OneInput、默认值和 timeout 语义继续由 Worker 决定。

## 决定

### 1. 使用原生 WebSocket v1，不引入历史补发

- endpoint 固定为 `GET /api/v1/realtime`，必须协商
  `cloudemuera.realtime.v1`，应用 envelope 的 `protocolVersion` 为 `1`；`/version` 分开公开
  WebSocket 版本和 `p1-09` payload schema 版本。
- 应用消息是严格 UTF-8 JSON envelope。`Utf8JsonReader` 先检查重复键、深度、有限数字、标识符、大小和
  封闭消息类型，再由 source-generated context 反序列化到 DTO；未知可选字段忽略，未知 type、非法
  discriminator、二进制帧和混合 fragment 按稳定 close code 拒绝。
- 每次 `session.resume` 都重新取得当前 Worker 的 `(workerEpoch, snapshotSequence)`，先发完整
  `session.snapshot`，再发连续 `display.batch`。不接受 `lastSequence` 补发承诺、不保存 ack 历史、不把
  Snapshot 或输入回执写入 SQLite；Hub 尚未取得首个 Snapshot 时返回 `SNAPSHOT_NOT_READY`，客户端退避后
  重试，而不是让连接无限等待首帧。
- `resync.required` 与替代 Snapshot 由单 writer 作为不可拆分 work group 发送；同一连接最多订阅配置数个
  Session，同一 Session 可以被多个连接订阅，断开/取消订阅只释放 Hub subscription，不改变 Session 或
  Worker 生命周期。

### 2. upgrade 和每次资源操作均实时鉴权

upgrade 要求认证 Cookie、精确配置 Origin、共同子协议、启动 readiness 和未 draining 的 API。连接不
长期信任 upgrade 时的 `HttpContext.User`：每次 resume/input 调用当前身份会话校验、取得当前用户状态，
再调用中央 `IResourceAuthorizer` 的 `SessionResume`/`SessionControl`。不存在和越权统一为
`SESSION_NOT_FOUND`；会话撤销、账户禁用或安全戳变化产生 `AUTHENTICATION_EXPIRED` 并以 `1008` 关闭；
强制改密产生 `PASSWORD_CHANGE_REQUIRED` 并关闭。周期 revalidation 只用于发现无操作连接的身份变化，
不替代资源操作边界的即时检查。

### 3. API 做路由和 fencing，Worker 做输入最终裁决

`WorkerManager` 在自身 registry 锁内捕获当前 Worker 与 Hub subscription。每次输入再次校验当前进程、
`controlPlaneInstanceId`、Session、Worker、epoch、stateVersion 和持久 `RUNNING` lease；旧 route 不会
跟随新 epoch。浏览器只允许 `KEYBOARD`、`BUTTON`、`POINTER`，拒绝 `SYSTEM`。

输入请求必须带 `workerEpoch`、`promptId`、`clientMessageId` 和完整 source/value 载荷。API 为每个 IPC
command 建立数量/估算字节双预算的 correlation map，先登记 pending 再写 IPC；回执必须同时匹配
correlation、binding、promptId 和 clientMessageId。未知/迟到/伪造回执只进入有界诊断计数，不进入通用
event probe。写失败、Worker dispose、IPC deadline 和连接取消都会完成并移除 waiter。

客户端 `messageId` 重复检测使用最近 4096 个 ID 的连接级有界窗口；这是内存边界，客户端仍必须在整个连接
生命周期内不复用任何已发送 ID。

Realtime input 先取得与 lifecycle open/close 共享的 `ISessionCommandGate`，在门内完成持久状态和当前 binding
校验并把命令写入 Worker queue，随后释放门再等待 IPC 回执。这样 `BeginStopping` 先线性化时新输入稳定返回
`SESSION_NOT_ACCEPTING_INPUT`，而已入队输入的 IPC 等待不会阻塞 close 的持久线性化；Worker 内部仍只有一个
首个有效 prompt 输入获胜。

### 4. 所有实时资源有硬上限且单连接单 writer

默认/绝对上限冻结为：全局连接 `256/4096`、单 Session 连接 `16/256`、单连接订阅 `4/16`、单连接 pending
input `32/256`、Worker pending input `128/2048`、控制队列 `64 条/256 KiB`、客户端消息 `64 KiB`、JSON
深度 `32`、envelope 上界 `2 KiB`。最终服务端消息为 `checked(SnapshotMaxBytes + EnvelopeMaxBytes)`，并以
实际 UTF-8 字节再次校验；不截断 payload。

一个连接只有一个 `RealtimeConnectionWriter`。控制消息优先，订阅 reader 同时只保留一个 ready frame，
多个 Session 以 round-robin 公平发送。控制队列满、Snapshot 重同步连续失败、发送 deadline、协议/身份
错误只关闭当前连接（分别映射 `1008/1011/1002/1009/1012`），不向 Worker 或其他连接施加反压。
应用 `connection.ping/pong` 检测浏览器失联；heartbeat timeout 不停止 Worker、不清 prompt、不改变
Session 状态。

## 备选方案

1. 使用 SignalR 或自建 ack/history 服务：会引入 Hub 状态、历史淘汰和连接恢复语义，违背 ADR-0017/0020
   的无历史边界，拒绝。
2. 在 API 中复制输入去重缓存或把回执写入 SQLite：会产生第二权威、跨 epoch 语义和持久敏感数据，拒绝。
3. 断线关闭 Worker、把无连接标记为挂起：违背 ADR-0015，且无法保证计时 prompt 连续，拒绝。
4. 用无界 Task/Channel 或只按消息数限制：大型 Snapshot、raster 和 input value 可绕过内存预算，拒绝。

## 后果

- 浏览器重连逻辑简单且确定：总是以当前 epoch 的完整 Snapshot 替换本地状态，不承诺恢复断线期间的
  每一条历史增量。
- API 多持有 transient connection/subscription 和单 Worker Hub mirror，但每个队列、pending map、最终
  message 和全局 admission 都有双维度预算。
- input timeout 只表示 API 未取得 Worker 回执，不会由 API 生成默认值或重新开始 Runtime prompt 计时；
  客户端必须使用原 `clientMessageId` 重试。
- WebSocket schema、TypeScript 类型入口和协议 golden JSON 必须与新的消息集合一起演进；改变必需字段或
  语义时发布新子协议，不在 v1 静默降级。

## 验证

- `CloudEmuera.Realtime.Tests` 覆盖 v1 golden envelope、重复键、SYSTEM source、深度/大小、最终 envelope
  字节上限和 connection registry admission；既有 P1-08 测试继续覆盖 Snapshot/resync/backpressure。
- Worker/API 测试必须覆盖 correlated receipt、pending overflow、伪造/迟到 correlation、同 ID duplicate/
  conflict、epoch fencing、Worker dispose，以及 input/close gate 竞态。
- 真实 Kestrel + Cookie + UDS Worker smoke 必须覆盖 upgrade、resume Snapshot、断线重连、input 回执、
  slow consumer、heartbeat 和 API draining close；所有验证使用 dev Docker。

## 实施记录

2026-08-14：P1-09 已按本 ADR 接入 `RealtimeEndpoint`、严格 v1 parser/source context、瞬时 connection
registry、单 writer/fair scheduler、live authorization、Snapshot/resync stream、应用 heartbeat、
Worker correlated input map 与共享 Session command gate；协议 schema、golden fixture 和 P1-11
TypeScript 类型入口已纳入仓库。
