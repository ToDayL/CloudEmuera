# ADR-0026：显示提交边界与原子 Realtime v3 帧

状态：Accepted

日期：2026-08-21

关联：P1-S03、SESS-004、PLAY-002、PLAY-004～008、AC-002/005/012、ADR-0018、ADR-0020、ADR-0025

## 背景

Runtime 的 `ConsoleTransaction` 会在一次输入结束后经历关闭 prompt、清屏/删行、重印和重新打开
prompt。原实现把 API 的定时、数量和字节 batcher 边界误当成浏览器提交边界，导致浏览器依次看到清空和
逐步重印的中间状态。历史压缩、IPC 分片和队列 resync 还可能把 working Snapshot 暴露给新连接。

P1-S03 是临时的显示闪烁修复任务，不改变原规划的 P1-12 编号。API 不能通过猜测
`ClosePrompt/OpenPrompt` 修补这个问题，因为只有 Runtime 持有 prompt 和归约后的权威状态。

## 决定

### 1. Runtime 权威地产生显示提交

`ConsoleStateStore` 同时保留最新 working state 和最近一次 committed state。每个 Worker epoch 的
`DisplayCommit` 包含：

- 单调递增的 `frameId`；
- 最后一个 operation sequence 组成的 `commitSequence`；
- `WAITING_FOR_INPUT`、`RUNTIME_COMPLETED`、`RUNTIME_FAILED` 或受控的 `EXPLICIT_REFRESH` reason；
- `requiresSnapshot` 与完整的 committed Snapshot/有界连续 delta。

成功归约的 `OpenPrompt` 必须是所属 transaction 的最后一个 operation；该 transaction 完成后自动提交
`WAITING_FOR_INPUT`。正常完成或可显示失败先提交最终无 prompt 状态，再发送 terminal 事件。API/Worker
被强制终止而不能形成最终状态时保留上一 committed frame，不伪造中间帧。

终止事件的传输也必须有界可靠：Worker 发送 `RuntimeCompleted`/`RuntimeFailed` 后，在现有双向 IPC 上等待
API 返回带原始 `messageId` 的 `terminal_ack`（复用受校验的 `StopWorker` 控制消息），收到确认后才发送
`WorkerStopped` 并退出；等待超过 shutdown grace period 时仍有界退出。这样 Worker 立即关闭 gRPC 请求流
不会把已经写入但尚未被 API 读取的终止事件吞掉。

16ms、64 条、256KiB 和 `MaxDeltaCount` 只影响内部 batching、历史压缩和 delta 是否可用；它们不能
直接创建浏览器可见提交。delta 表示不完整或超过目标时，在同一个 commit 上改用一份完整 Snapshot。

### 2. Worker 使用 committed cursor 和 IPC v5

Worker output pump 只读取 `ReadCommittedSince(lastFrameId, lastSequence)`。一个 committed frame 不拆成
多个可见 IPC 消息；如果 cursor 落后或 IPC envelope 太大，发送该 frame 的完整 Snapshot。Heartbeat
同时报告 working sequence、committed sequence 和 committed frame id。

结构化 Worker 协议从 v4 升级到 v5，protobuf package/namespace 与能力摘要一起升级。`DisplayFrame` 是
生产 Worker 显示消息，严格区分 Snapshot/delta、frame metadata、连续 transaction 和 Prompt 边界；
旧 `DisplayBatch` 字段仅为源码/历史夹具兼容保留，API Worker Manager 不将其路由到浏览器。

### 3. API 只提升 committed mirror

`SessionOutputHub` 维护 working mirror 与 committed mirror。working 更新不触发订阅 resync、不会清空
已提交队列，也不能成为新连接基线。只有合法 `DisplayFrame` 才能原子提升 committed mirror；订阅、
queue overflow 和 resync 只读取该 mirror，并按 `committedFrameId` 检测替换是否过期。

小帧编码为一个 `display.frame`；大帧/历史压缩/IPC 超限编码为一个 `session.snapshot`，携带
`committedFrameId`。Snapshot 编码仍 single-flight 且受原有 UTF-8 硬上限约束，超限 fail closed 并回收
Worker。删除 `DeferOutputUntilNextPrompt` 及 API 猜测边界的特殊分支；遗留 transport batcher 不再是
生产 Worker 的可见语义。

### 4. 浏览器 Realtime v3 原子归约

WebSocket 子协议升级为 `cloudemuera.realtime.v3`，payload schema 为
`p1-s03-display-commit`。浏览器只接受完整 `session.snapshot` 或 `display.frame`：先在私有 candidate
上归约整个 frame，frameId/epoch/sequence 不连续或任一操作失败时保持上一 committed state 并请求
resync；成功后只通知一次，Scrollback、Canvas 和 Prompt 从同一 state 更新。v2/v1 envelope、旧
`display.batch` 和不带 `committedFrameId` 的 Snapshot fail closed。

## 备选方案

1. 在 API 看到 `ClosePrompt` 后延迟输出直到 `OpenPrompt`：实现短，但 API 无权判断 Runtime 边界，历史压缩
   会使猜测失效，拒绝。
2. 在浏览器 debounce 或隐藏中间 DOM：不能修复 resync/新连接/多客户端语义，且会把工作态留在客户端，拒绝。
3. 无限缓存到下一个 prompt，或将大帧拆成多个可见 multipart：会复制未提交状态并破坏内存/重连边界，拒绝。

## 后果

- 正常等待循环一次只产生一个可见显示提交，可能跳过中间动画但不会显示不完整画面。
- API 仍需持有每个活动 Worker 的完整 committed mirror；成本由 Snapshot 和连接队列预算限制。
- Realtime/IPC 是破坏性 major 升级，旧客户端和旧 Worker 必须明确失败，不能混合部署。
- 无 prompt 且永不结束的 Runtime 不会因 timer 自动显示；由 Runtime 资源/故障策略有界处理。

## 验证

- RuntimeAdapter 覆盖 OpenPrompt 最后 operation、working/committed 隔离、历史压缩 Snapshot 降级和终态提交；
- IPC v5 validator 覆盖 frame metadata、连续序列、Snapshot/delta 互斥、Prompt 边界和 heartbeat 单调关系；
- Realtime/API 覆盖原子 clear/reprint/OpenPrompt、超大 delta Snapshot 降级、resync 与旧协议拒绝；
- Web codec/store 覆盖 v3 生成契约、单次 frame 归约、frame gap 和 committed snapshot；
- 在开发容器执行 targeted tests、Web frozen install/typecheck/test 和 `./scripts/check.sh`。
