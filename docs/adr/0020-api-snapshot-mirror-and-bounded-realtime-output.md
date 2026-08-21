# ADR-0020：API 快照镜像与有界实时输出

状态：Accepted

日期：2026-08-13

关联：SESS-004、PLAY-002、PLAY-004～006、PLAY-010/012、AC-002/012、ADR-0003、ADR-0017、
ADR-0018、P1-08、ADR-0026

说明：本 ADR 继续约束 API 镜像、惰性 Snapshot 编码和逐连接有界队列；其中“16ms/64/256KiB 产生可见
flush”以及“最新 working Snapshot 可用于 resync”的显示语义已由 [ADR-0026](0026-display-commit-boundary-realtime-v3.md)
取代。生产 Worker 现在只发送 committed `DisplayFrame`，旧批处理文字仅保留为历史实现背景。

## 背景

P1-07 已在 Worker 内建立完整、有界、可原子归约的 `ConsoleSnapshot` 和连续
`ConsoleTransaction(sequence)`。当前 API 的 `ApiWorkerSession` 仍把每个 `DisplayBatch` clone 后永久
追加到进程内 `List`；持续输出会无界增长，而且新浏览器若不重放该列表就无法取得当前完整状态。

ADR-0017 已取消客户端 ack 历史补发、持久增量日志和 snapshot/subscribe 无丢失屏障。P1-08 仍需保证：

- 新连接总是以当前 Worker epoch 的完整快照建立基线；
- 快照取得与实时转发并发时，遗漏的 sequence 必须被检测并通过新快照收敛；
- 慢连接不能阻塞 Worker、其他连接或无限占用 API 内存；
- prompt deadline、scene、background、media 和 truncation 必须与 scrollback 一起恢复。

## 决定

### 1. API 为每个活动 Worker 维护一份完整状态镜像

在 API Worker Manager 与后续 WebSocket endpoint 之间建立 transport-neutral `SessionOutputHub`。Hub
按 `(sessionId, workerEpoch)` 绑定，只接受当前 epoch 的严格连续 Worker 输出，并使用 RuntimeAdapter
共享的纯 `ConsoleSnapshotReducer` 将快照与 transaction 归约成一份最新完整状态。

Worker 启动后的第一份显示消息必须带完整 Snapshot。普通批次首 sequence 必须等于 Hub 当前 sequence
加一；带快照批次以其 `snapshotSequence` 替换镜像，再应用其后连续 transaction。旧 epoch、倒退、重复、
非法或无法归约的批次不进入镜像。非快照缺口是 Worker IPC 协议错误并终止该 Worker；快照可以在输出
历史压缩后成为新的权威基线。

API 不把显示树写入 SQLite。数据库的 `last_output_sequence` 继续只是生命周期观测值；API 或 Worker
重启后由新 epoch 的新完整 Snapshot 建立内存镜像。

### 2. 不保留网络历史增量

移除生产路径无界 `displayBatches` 列表。Hub 只持有：

- 一份不可变最新 Snapshot；
- 可选的最新编码 Snapshot 缓存；
- 当前连接注册表和每连接有界待发队列；
- 少量计数、sequence、epoch 和 resync 状态。

Hub 不接受客户端 `lastSequence` 作为补发游标，不按 ack 保存历史窗口，也不为已断开的客户端保留批次。
RuntimeAdapter 中为 Worker 归约和测试保留的短期 transaction history 不成为浏览器协议承诺。

### 3. 快照/订阅竞态以缺口检测收敛

建立订阅时先取得不可变 `Snapshot(N)`，再注册连接。注册完成后重新比较 Hub 的 epoch/sequence：

- 仍为 `(epoch, N)`：连接进入 live 状态；
- 已变化：不尝试找回竞态窗口中的增量，直接把连接标记为 `resync-required`；
- 后续批次的首 sequence 不是客户端期望的 `lastApplied + 1`：同样进入 `resync-required`。

发送循环看到 `resync-required` 时清空该连接尚未发送的增量，读取当时最新 `Snapshot(M)` 并完整替换
客户端基线。快照发送期间若又有输出，下一批的 sequence 检查会再次决定继续或重同步。该算法不建立
snapshot/subscribe 无丢失屏障，但任何遗漏都会被确定性检测。

### 4. 批处理和逐连接背压

Worker IPC 接收、Hub 状态归约和连接发送相互解耦。连续小 transaction 在 API 内按以下任一条件 flush：

- 16 ms 延迟；
- 64 个 transaction；
- 256 KiB 实际 UTF-8 编码负载。

单个合法 transaction 超过批次字节目标时独立发送，仍受 Snapshot/协议总上限约束。批处理不能跨
epoch、Snapshot 或 resync 边界。

每个连接使用同时按消息数和实际编码字节计数的队列，默认软上限为 32 条或 1 MiB，硬上限为 64 条
或 2 MiB。达到软上限即停止追加普通增量并标记 `resync-required`；达到硬上限清空待发增量，只保留
该状态标记。标记不依赖在已满 Channel 中再插入一条消息，因此不会丢失唤醒。

Snapshot 不复制进每个连接队列；发送循环从 Hub 读取共享的不可变最新 Snapshot，按需编码并缓存。批次
发布本身不重新编码快照：镜像每次变化时只失效编码缓存，编码发生在首个订阅或 resync 需要快照时，
结果由所有并发订阅者共享，直到下一次镜像变化。这样无浏览器连接的 Session 持续输出时不会为无人读取
的 JSON 付出全量编码成本。完整 JSON Snapshot 默认最大 12 MiB，与 Worker v3 envelope 上限对齐；超限
是协议/配置错误，不通过裁剪 scene、prompt 或 media 静默降级。编码失败（含首次按需编码时超限）由
Hub fail closed，并通过故障报告让 Worker Manager 回收 Worker、把 Session 对账为 `CRASHED`，而不是把
不可读镜像留在 API 内。所有阈值由部署级 `RealtimeOutputOptions` 配置，配置必须为正且满足软上限小于
硬上限、批次上限小于等于队列/快照上限。

### 5. 慢连接失败不改变 Session

同一连接在 30 秒窗口内连续三次无法取得或编码 Snapshot 替换时，Hub 向上层报告 `slow-consumer`。
P1-09 的 WebSocket endpoint 用策略错误关闭该连接；不关闭 Worker、不改变 Session 状态，也不影响其他
连接。快照在两次读取之间被更新的输出超越（`snapshot-raced`）不计入失败：客户端确实收到了完整有效的
替换，只需在镜像稳定后取得更新的基线，持续重同步是有界的，不能把繁忙 Hub 上正常的快连接误判为慢
消费者。连接数和背压状态仅在 API 内存维护。

### 6. 浏览器协议边界

P1-08 在 `CloudEmuera.Contracts` 定义完整 Snapshot、transaction batch 和 `resync-required` 的
transport-neutral DTO、显式 JSON 字段名及 source-generated serializer context，并用 golden JSON
冻结所有 P1-07 节点。P1-08 的帧流以带 reason 的完整替换 Snapshot 表达重同步，`RealtimeResyncRequired`
DTO 保留给 P1-09 的 WebSocket envelope 使用，运行时不产生独立 resync payload。P1-09 再加入
WebSocket envelope、鉴权握手、ping/pong 和输入回执；不得在 P1-09 重新定义显示状态或背压算法。
替代说明（2026-08-20）：当前 envelope 版本和输入回执语义以 ADR-0025 的 Realtime v2 为准。

## 备选方案

1. API 永久保存 Worker 增量并按客户端游标补发：重连更平滑，但内存、ack 和淘汰语义复杂，违反
   ADR-0017，拒绝。
2. 每次重连通过 IPC 命令临时请求 Worker Snapshot：可以避免 API 镜像，但请求与正常输出仍需排序，
   Worker 忙或控制流拥塞会拖慢所有重连，且 API 无法立即判断自身队列是否一致，拒绝。
3. 每个连接各自归约完整状态：实现局部简单，但把大 Snapshot 按连接倍增并重复消耗 CPU，拒绝。
4. 用无界 Channel 或仅按消息数限制：大 raster/HTML payload 可绕过内存预算，拒绝。
5. 队列溢出立即关闭连接：正确但刷新体验差；有限次数完整快照替换能在同样有界的前提下恢复，作为
   首选降级。

## 后果

- API 会为每个活动 Worker 多持有一份完整显示状态；该成本受部署级活动 Worker 数和 Snapshot 上限共同
  限制，不随历史长度或连接数增长。
- RuntimeAdapter 需要抽取一个可由 Worker store 和 API Hub 共同使用的纯 reducer，避免两套显示语义。
- Snapshot JSON 编码可能短时产生额外缓冲；实现必须复用同一编码结果并用实际字节数检查，不能只依赖
  对象估算。
- 新连接和慢连接可能连续看到多个完整 Snapshot，但最终状态一致；MVP 不保证逐个呈现重同步期间的
  中间动画或临时行。

## 验证

- 属性测试证明 `Reduce(Snapshot(N), transactions N+1..M) == Snapshot(M)`，非法/缺口 transaction
  不部分修改 Hub；
- snapshot/subscribe 竞态在每个切点注入输出，结果要么连续应用，要么进入 resync 并取得更新快照；
- 小阈值压力测试证明每连接消息数、编码字节和每 Session 镜像始终在上限内，慢连接不阻塞快连接或
  Worker 接收；
- epoch 切换、重复/倒退/缺口、快照编码超限和 serializer 失败均 fail closed；
- golden JSON 覆盖 scrollback、prompt immutable deadline、background、scene/raster、hit region、
  media、window 和 truncation；
- 持续输出与反复重连测试中 API 内存达到稳定平台，最终 Snapshot 与 Worker 权威状态等价。

## 实施记录

2026-08-21：显示提交边界临时修复转入 [ADR-0026](0026-display-commit-boundary-realtime-v3.md)，
删除了诊断性的 `DeferOutputUntilNextPrompt` 开关和 API 猜测 Prompt 边界的特殊分支。API 镜像/队列的
有界性与惰性 Snapshot 编码仍按本 ADR 执行，但浏览器可见提交、resync 基线和协议版本以 ADR-0026 为准。

P1-08 已按本 ADR 落地：RuntimeAdapter 提供公共 `ConsoleSnapshotReducer` 和注入限制的完整校验；
`CloudEmuera.Realtime` 共享 protobuf/runtime mapper；Worker 每个 epoch 的首个显示消息强制携带完整
Snapshot；API `SessionOutputHub` 只保留当前不可变 Snapshot、共享 JSON 缓存和逐连接双预算队列。
生产路径不再保留 `DisplayBatches` 历史属性，控制面 wait probe 不复制 `DisplayBatch`，其余事件同时受
消息数和字节预算限制；Hub 发布、持续 batch、终态 drain 和 dispose 共用生命周期边界。Worker/API
终态和 dispose 会完成订阅并释放快照缓存。WebSocket 外层协议和输入转发仍留给 P1-09。

2026-08-13 评审修订：快照 JSON 改为按需惰性编码（批次发布只失效缓存，首个订阅/resync 时编码并缓存，
`SnapshotEncodingCount` 提供观测）；batcher 字节预算改为单事务精确序列化累加 + 固定包装开销上界，
消除每次入列全量重编码的 O(n²)；`snapshot-raced` 不再计入 slow-consumer 失败，只有无法取得/编码
快照才计数；读取路径编码超限通过 `FaultReported` 通知 Worker Manager 回收 Worker；`Complete` 后
终态优先于残留 resync 标记；控制面丢弃超预算事件时记录计数；`StructuredConsoleWireMapper` 物理移入
`CloudEmuera.Realtime`。修订后验证：`CloudEmuera.Realtime.Tests` 23 项、RuntimeAdapter
`Snapshot|ConsoleContract` 55 项（含 reducer 随机化属性测试）、完整 Worker 集成 19 项、Release
solution build 0 警告 0 错误。

2026-08-13 在开发 Docker 中验证：`CloudEmuera.Realtime.Tests` 11 项、RuntimeAdapter
`Snapshot|ConsoleContract` 54 项、Worker 快照/背压过滤 2 项、完整 Worker 集成 19 项通过；解决方案 Release 构建通过。

2026-08-14 评审修复：同一 Snapshot 的并发惰性编码使用 single-flight，镜像发布只在状态替换处失效缓存；
timer/终态 flush 的编码故障也通过 `FaultReported` 回收 Worker；控制面 pending event 因数量或字节预算
淘汰时累计丢弃计数。新增并发编码回归测试，`CloudEmuera.Realtime.Tests` 24 项通过。
