# P1-08 完整 Snapshot 重连与有界输出详细方案

状态：完成

日期：2026-08-13

关联需求：SESS-004、PLAY-002、PLAY-004～006、PLAY-010/012、AC-002/012

关联决策：ADR-0003、ADR-0017、ADR-0018、ADR-0019、ADR-0020（Accepted）

前置任务：P1-05、P1-06、P1-07

后续任务：P1-09 WebSocket 与输入去重、P1-11 浏览器 Console、P1-15 E2E 与视觉回归

## 1. 目标

把 P1-07 的完整结构化 Console 状态接入 API 侧实时输出核心，使任何连接都能以当前
`(workerEpoch, snapshotSequence)` 的完整 Snapshot 建立或替换本地基线，并在持续输出、竞态和慢消费
条件下保持 Worker、API 与逐连接内存有界。

本任务完成 transport-neutral 的状态镜像、序列化、批处理、订阅和背压核心及其集成测试。P1-09 才
开放正式 WebSocket endpoint、鉴权握手和浏览器输入消息；P1-11 才实现 DOM/Canvas/WebAudio 渲染。

## 2. 非目标

- 不按客户端 ack 或 `lastSequence` 补发历史增量；
- 不持久化 Snapshot、实时批次或输入去重记录；
- 不建立 snapshot/subscribe 无丢失屏障；
- 不在 API 重启后连接旧 Worker，API 重启仍使原 Worker 有界退出并把 Session 对账为 `CRASHED`；
- 不实现 WebSocket 身份协议、输入转发、控制权租约、浏览器 reducer 或视觉渲染；
- 不修改 P1-07 的 prompt timeout 裁决；读取/编码/重连不能重置 deadline；
- 不以丢弃 prompt、scene、raster、media 或把富内容转成文本来满足大小上限。

## 3. 当前实现与缺口

### 3.1 已有基础

- `ConsoleStateStore.ApplyTransaction` 在同一锁内完成校验、sequence 分配和不可分割归约；
- `ConsoleSnapshot` 已包含 scrollback、background、canvas scene、hit region、media、prompt、window 和
  truncation，估算状态默认受 4 MiB Worker 内存预算约束；
- Worker v3 `DisplayBatch` 能携带显式 Snapshot 和连续 transaction，单 envelope 上限 12 MiB；
- Worker output pump 独立于解释器线程。gRPC writer 变慢时 output pump 可等待，而解释器继续更新有界
  state/history；历史被压缩后下一次读取自动降级为 Snapshot；
- Worker Manager 已执行 binding、control-plane、capability digest 和 epoch 校验。

### 3.2 必须修复

- `ApiWorkerSession.displayBatches` 是无界 `List<DisplayBatch>`，每批又执行 clone；
- API 只有 `lastDisplaySequence`，没有可直接用于新连接的最新完整状态；
- Worker 初始状态没有“第一显示消息必须是 Snapshot”的正式不变量；
- 缺口目前被当作协议错误，但没有连接级缺口/resync 状态；
- 尚无实际 JSON 字节预算、批处理器、逐连接双预算队列和 `CloudEmuera.Realtime.Tests`；
- Snapshot 的 protobuf mapper 存在，但浏览器 JSON DTO 与 golden schema 尚未建立。

## 4. 分层与代码归属

```text
Emuera / StructuredGameConsole
        │ authoritative transaction + Snapshot
        ▼
Worker output pump ── v3 gRPC/UDS ──► Worker Manager
                                           │ validated ordered batch
                                           ▼
                                  SessionOutputHub (API)
                                  ├─ latest immutable Snapshot
                                  ├─ JSON payload mapper/cache
                                  ├─ transaction batcher
                                  └─ bounded subscription queues
                                           │ transport-neutral frames
                                           ▼
                              P1-09 WebSocket endpoint / P1-11 Store
```

职责安排：

- `CloudEmuera.RuntimeAdapter`：抽取纯 `ConsoleSnapshotReducer` 和显式 sequenced apply 入口；不引用 IPC、
  ASP.NET 或 JSON；
- `CloudEmuera.Contracts`：浏览器可消费的 Snapshot/transaction/resync DTO 与 JSON source generation；
- `CloudEmuera.Worker`：保证每个 runtime/epoch 的首个显示 batch 带完整 Snapshot；
- `CloudEmuera.Api/Realtime`：Hub、batcher、byte-bounded subscription queue、DTO mapper 和诊断计数；
- `CloudEmuera.Api/Workers`：验证 Worker envelope 后只向 Hub 发布，不保存生产历史列表；
- `CloudEmuera.Realtime.Tests`：纯并发/容量/序列化测试；真实 Worker 交接仍放在
  `CloudEmuera.Worker.IntegrationTests`。

## 5. RuntimeAdapter reducer 重构

### 5.1 新入口

新增无副作用 API：

```text
ConsoleSnapshotReducer.Apply(
    ConsoleSnapshot baseline,
    SequencedConsoleTransaction transaction,
    ConsoleHistoryOptions options) -> ConsoleSnapshot

ConsoleSnapshotReducer.ApplyBatch(
    ConsoleSnapshot baseline,
    IReadOnlyList<SequencedConsoleTransaction> transactions,
    ConsoleHistoryOptions options) -> ConsoleSnapshot
```

约束：

- 第一条 sequence 必须为 `baseline.snapshotSequence + 1`，后续严格连续；
- candidate 完整验证和预算检查成功后才返回新 Snapshot；失败不修改 baseline；
- reducer 复用 `ConsoleStateStore` 当前 operation 语义、整行裁剪和 truncation 累计规则；
- `ConsoleStateStore.ApplyTransaction` 改为调用同一内部 reducer，再负责本地 sequence 分配与短期 history；
- 从 wire 构造的 Snapshot 先以注入 limits 完整验证，不能只依赖公共构造器的默认限制；
- `long.MaxValue`、未知 operation、非法资源、NaN/Infinity 和预算溢出继续 fail closed。

重构后的核心性质由共享测试覆盖，避免 API mirror 与 Worker state 在 Replace/Delete/Clear、scene range、
media revision 或 truncation 上漂移。

## 6. Worker 初始快照与 IPC 顺序

### 6.1 首批不变量

每次新 Worker epoch 的 output pump 从 `forceSnapshot = true` 开始。第一次观察 Console 时，无论当前
sequence 是否等于持久 `initialOutputSequence`，都发送：

```text
DisplayBatch(isSnapshot=true, snapshot=Snapshot(N), transactions=[])
```

若读取时已经存在 transaction，则允许同一首批携带 `Snapshot(N)` 和连续的 `N+1..M`。第一份快照写入
成功后才清除 `forceSnapshot`。发送失败不推进 `lastSentSequence`。

### 6.2 正常输出

- 同一 Worker 只有 output pump 调用 display send，保持单生产者；
- 每次发送成功后才推进 `lastSentSequence`；
- history 窗口失配时发送较新 Snapshot，不尝试拼接缺失 transaction；
- control message 继续优先于 display，display 写慢不能阻塞 Runtime 线程；
- display protobuf 的 `CalculateSize()` 在进入 gRPC 前再次检查 12 MiB 上限。

P1-08 不新增 request-snapshot IPC 命令。API 已有状态镜像，连接级重同步不应把所有浏览器的速度反馈到
Worker 控制流。

## 7. SessionOutputHub 状态机

每个当前 Worker binding 对应一个 Hub：

```text
AwaitingInitialSnapshot
  ├─ valid snapshot(epoch, N) ─► Live(epoch, N)
  └─ delta/error             ─► Faulted

Live(epoch, N)
  ├─ delta starts N+1        ─► reduce / Live(epoch, M)
  ├─ newer valid snapshot M  ─► replace / Live(epoch, K)
  ├─ duplicate/older batch   ─► ignore + bounded diagnostic
  └─ gap/invalid batch       ─► Faulted + cancel Worker connection

Disposed/Faulted
  └─ reject publish/subscribe; dispose all subscriptions
```

`Publish` 的原子区只做 binding/sequence 校验、共享 reducer、最新 Snapshot swap 和订阅者列表快照；
JSON 编码、等待和网络 I/O 都在锁外。每个 batch 发布时记录 epoch、first/last sequence、实际编码字节和
resync 计数，但不记录游戏文本或输入值。

新的 epoch 创建新 Hub，旧 Hub 立即完成所有 reader 并发出 `epoch-replaced` 终态；不能在原 Hub 上把
sequence 清零或混合两个 Worker 的状态。

## 8. 浏览器 DTO 与 JSON 约束

P1-08 定义 payload，不定义 P1-09 的外层 WebSocket envelope。至少包括：

```text
RealtimeSnapshot(workerEpoch, snapshotSequence, consoleState)
RealtimeTransactionBatch(workerEpoch, firstSequence, lastSequence, transactions[])
RealtimeResyncRequired(workerEpoch, observedSequence, reason)
```

P1-08 帧流以带 reason 的完整替换 Snapshot 表达重同步；`RealtimeResyncRequired` DTO 保留给 P1-09 的
WebSocket envelope，不在 P1-08 运行期产生独立 payload。当前协议版本由 P1-S02/ADR-0025 规定为 v2。

要求：

- JSON 使用 camelCase、显式 enum 字符串和 UTF-8；不启用多态类型名；
- 所有 P1-07 union 用封闭 discriminator，未知值由客户端/服务端拒绝；
- raster 用 base64 PNG；资产继续只用 manifest `assetId`，不出现路径、URL、raw HTML 或宿主字体；
- `deadlineUnixMilliseconds` 原样复制，序列化时可附新的 `serverNowUnixMilliseconds` 展示基准，但不得
  修改 deadline 或让 Worker 重建 prompt；
- Snapshot 编码后检查 12 MiB，transaction batch 检查 256 KiB 目标和 12 MiB 单消息硬上限。快照编码
  按需惰性执行：批次发布只失效编码缓存，首个订阅或 resync 需要时编码并缓存，
  `RealtimeHubStatistics.SnapshotEncodingCount` 提供观测；无订阅者的 Session 持续输出不付出全量
  JSON 编码成本；
- serializer 使用 source-generated context。golden JSON 与 TypeScript schema 生成输入放在
  `CloudEmuera.Contracts`，避免 API 手写第二套字段。

## 9. 批处理

`RealtimeBatcher` 接受已归约、连续的 transaction 引用，遇到以下条件 flush：16 ms、64 条、
256 KiB，或 Snapshot/epoch/resync/完成边界。使用可注入 `TimeProvider`，测试不等待真实时钟。

字节阈值以最终 UTF-8 payload 实测为准；预分组使用单 transaction 精确序列化尺寸累加一个固定包装
开销上界（256 B），累计上界达到目标即先 flush 已有批次再开始新批次，因此任何 flush 出的普通批次
实际尺寸不超过目标，入列复杂度保持线性。单条 transaction 超过目标时独立编码发送；超过 12 MiB 则
Hub fault，不拆分一个原子 transaction。

同一编码批次由多个连接共享只读 byte buffer；引用释放后可回收。不得为每个订阅者重复序列化。

## 10. 逐连接队列与重同步

### 10.1 队列模型

不用普通 `Channel<T>` 的 count-only 容量作为最终边界。实现 `BoundedRealtimeQueue`，在同一锁内维护：

- deque 中的共享 payload 引用；
- queued message count 与 encoded byte count；
- `needsResync`、completion 和单个 waiter；
- expected epoch/sequence；
- 最近 Snapshot 替换失败时间与次数。

默认软上限 32 条/1 MiB，硬上限 64 条/2 MiB。配置验证保证所有值为正、soft < hard，并设置绝对最大
值防止错误配置绕过部署级边界。

### 10.2 溢出行为

普通 enqueue 后将跨越 soft limit 时，不再加入该 payload，清空旧增量并原子设置 `needsResync`。
reader 被直接唤醒，不需要把 marker 塞进已满队列。hard limit 是防御性断言；任何路径达到它都执行相同
清空操作并记录 `hard-overflow`。

reader 取得 resync 状态后：

1. 从 Hub 读取最新 Snapshot；
2. 发送完整替换；
3. 把 expected sequence 设置为 Snapshot sequence；
4. 检查 resync 期间 Hub 是否前进；若前进则保持 `needsResync`，否则恢复 live；
5. 30 秒内三次无法完成替换则返回 `slow-consumer` 给 P1-09 关闭连接。

一个连接的清空、编码失败或关闭不改变 Hub 镜像，不等待其他连接，也不关闭 Session。

## 11. Snapshot/subscribe 竞态算法

`SubscribeAsync` 不宣称无丢失屏障：

1. 读取 `Snapshot(N)` 的不可变引用；
2. 创建并注册空 queue，expected=`(epoch,N)`；
3. 再读 Hub 当前 `(epoch,current)`；
4. 若不同，设置 `needsResync`；否则把 `Snapshot(N)` 交给 caller；
5. 每次出队 batch 检查 `firstSequence == expected + 1`，不满足时丢弃并 resync；
6. Snapshot 永远完整替换，不能 merge 到旧 epoch 或旧 scene。

当前实现把订阅注册与快照读取放在同一 Hub 锁内，常见路径不存在窗口；第二次比较保留为注册拆分到
传输层时的防御性契约。即使竞态窗口中只有一条输出且此后静默，步骤 3 也必须发现 sequence 已前进，
不能等下一条消息才暴露缺口。

## 12. 与 Worker Manager 生命周期集成

- `ApiWorkerSession` 构造时创建 `SessionOutputHub`，初始 epoch 来自 binding；
- `ReceiveAsync` 完成 v3 envelope 校验后调用 `hub.PublishDisplayBatch`；
- Hub fault 记录稳定 reason code，通过现有 connection cancellation 让 Worker 有界退出并进入
  `CRASHED`，不得只丢消息后继续；
- heartbeat 的 `outputSequence` 不用于补洞，只用于观测和发现 Worker 自报值倒退/超前异常；
- runtime completed/failed/stopped 时先 flush batcher，再完成所有订阅；
- dispose 清空 payload 引用、取消 waiter，并从 manager registry 移除 Hub；
- 测试用事件观察改为有界 probe/订阅，不保留生产 `DisplayBatches` 历史属性。

## 13. 配置与诊断

新增 `RealtimeOutputOptions`，由配置绑定并在启动时 validate：

```text
SnapshotMaxBytes                12 MiB
BatchTargetBytes               256 KiB
BatchMaxTransactions           64
BatchMaxDelay                  16 ms
ConnectionQueueSoftBytes       1 MiB
ConnectionQueueHardBytes       2 MiB
ConnectionQueueSoftMessages    32
ConnectionQueueHardMessages    64
MaxSnapshotResyncAttempts      3
SnapshotResyncWindow           30 s
```

诊断只记录稳定字段：sessionId、workerId、epoch、first/last sequence、payloadBytes、queueMessages、
queueBytes、resyncReason 和 closeReason。不得记录文本节点、按钮值、prompt 默认值或用户输入。

## 14. 测试方案

### 14.1 RuntimeAdapter

- 每种 operation 的 external-sequence reducer 与 `ConsoleStateStore` 结果等价；
- batch 连续、缺口、重复、倒退、`long.MaxValue` 和失败原子性；
- scrollback 裁剪、truncation 累计、scene/background/media/prompt/window 完整保留；
- randomized `snapshot + transactions == authoritative snapshot` 属性测试。

### 14.2 Contracts

- 完整 Snapshot 和每种 transaction 的 golden JSON；
- enum/discriminator、camelCase、64-bit sequence、deadline 和 Unicode round-trip；
- raster base64、12 MiB 边界、超限和 serializer 失败；
- payload 不含 raw HTML、URL、宿主路径或意外 CLR 类型元数据。

### 14.3 Realtime core

- 初始必须 Snapshot，连续批次更新 mirror；重复、gap、旧 epoch 和非法 batch；
- batcher 的 16 ms/64 条/256 KiB 三种 flush 路径及单个大 transaction；
- subscribe 步骤每个竞态切点都最终连续或 resync；
- soft/hard count 与 byte overflow、marker 唤醒、共享 buffer 生命周期；
- 一个永久慢 reader、一个快速 reader与持续 publisher 并发，快 reader 连续且 publisher 不等待；
- 三次 Snapshot 替换失败产生 `slow-consumer`，Session/其他 reader 不受影响；
- 惰性编码：无订阅者发布不编码、首个订阅编码一次、缓存复用、镜像变化后重新编码
  （`SnapshotEncodingCount`）；
- 读取路径编码超限 fail-closed：Hub 进入 Faulted、订阅者收到 Completed、`FaultReported` 上报；
- `snapshot-raced` 不关闭订阅者；golden JSON 冻结完整 Snapshot 与 transaction batch；
- 首个消息为 delta 直接 Faulted；旧 Hub 完成订阅后新 Hub 独立要求自己的首快照；
- 12 MiB 上限与序列化超限抛错；
- Hub dispose、epoch replacement、runtime completion 与 enqueue/read 竞争无泄漏或死锁。

### 14.4 真实 Worker/API

- Worker 首个显示消息在空输出、启动即输出和启动即 prompt 三种场景都是完整 Snapshot；
- Worker history 压缩后发送较新 Snapshot，API mirror 与 Worker Snapshot 等价；
- 真实 Kestrel Worker Manager 持续输出不再增长 `DisplayBatch` 历史；
- API 接收停顿时 Worker Runtime 仍推进，恢复后用 Snapshot 收敛；
- Worker epoch 切换完成旧订阅并由新 Snapshot 建立基线。

### 14.5 容量断言

测试不只看功能结果，还要在每次 enqueue/publish 后断言：

- Hub 只保留一份 Snapshot 和一个可复用编码缓存；
- 每连接 `queuedMessages <= hardMessages`、`queuedBytes <= hardBytes`；
- 已断开连接和旧 epoch payload 最终可释放；
- 10 万条小 transaction、反复 1,000 次重连和 raster 大 payload 场景达到稳定平台，不随历史线性增长。

## 15. 验证命令

所有命令通过 dev Docker 运行：

```bash
./scripts/dev-up.sh
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore --configuration Release \
  --filter 'Category=Snapshot|Category=ConsoleContract'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Realtime.Tests --no-restore --configuration Release \
  --filter 'Category=Snapshot|Category=Backpressure|Category=Concurrency'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release \
  --filter 'Category=Snapshot|Category=Backpressure'
./scripts/check.sh
```

实现阶段已完成，并已执行本节命令与对应的开发容器验证。

## 16. 实施切片

### 切片 1：冻结 ADR、配置与测试骨架

- 接受 ADR-0020 并同步 design/development plan；
- 新增 `CloudEmuera.Realtime.Tests`、traits 和 solution entry；
- 定义 `RealtimeOutputOptions`、payload DTO、golden fixture 目录；
- 先写队列上限、sequence 和竞态失败测试。

### 切片 2：共享 reducer

- 从 `ConsoleStateStore` 抽取纯 reducer；
- 增加 external sequence/batch API 与属性测试；
- 保持现有 Worker 行为和 P1-07 契约测试不变。

### 切片 3：首快照与 API mirror

- Worker 每 epoch 强制首 Snapshot；
- 实现 Hub 状态机并接入 Worker Manager；
- 删除生产无界 `displayBatches`，迁移测试观察接口；
- 覆盖初始、gap、Snapshot 替换、epoch 和 Worker fault。

### 切片 4：JSON payload 与批处理

- 完成 Contracts DTO、source-generated serializer、mapper 和 golden JSON；
- 实现 actual-byte batcher 和共享 buffer；
- 覆盖完整 P1-07 状态及 raster 边界。

### 切片 5：有界订阅与 resync

- 实现双预算 queue、subscribe 竞态检查和 resync 状态机；
- 增加快慢连接、溢出、重同步失败、dispose 并发测试；
- 暴露给 P1-09 的最小 subscription 接口。

### 切片 6：真实链路、容量与文档

- 运行真实 Worker/API 持续输出和 history compaction 场景；
- 加入稳定平台/引用释放容量断言；
- 更新 runtime compatibility、设计、开发计划和 P1-09/P1-11 交接说明；
- 运行完整 dev Docker 验证。

切片按顺序合并。DTO 与 Hub 接口在切片 1 冻结后，切片 2 和 golden fixture 准备可并行，但 Worker
Manager、Contracts 和 solution 文件由单一集成修改协调。

## 17. 完成条件

1. API 生产路径不存在按 Session 永久增长的 `DisplayBatch` 集合；
2. 每个新 Worker epoch 的第一份显示状态是完整 Snapshot；
3. API mirror 与 Worker 权威 Snapshot 对所有 P1-07 状态等价；
4. 新订阅和重同步始终完整替换 scrollback、prompt、background、scene、media、window 和 truncation；
5. prompt deadline 在读取、序列化、重连和 resync 后保持不变；
6. 竞态输出要么连续应用，要么检测缺口并取得更新 Snapshot；
7. 逐连接消息数和实际编码字节始终不超过硬上限；
8. 慢连接不阻塞 Worker、Hub publisher、快连接或改变 Session 状态；
9. 未实现 ack 补发、持久 history、持久 Snapshot 或无丢失订阅屏障；
10. 正常、边界、主要失败、并发和容量测试映射相关需求编号；
11. 第 15 节命令全部通过并记录实际结果；
12. P1-09 能直接消费 subscription/payload 接口而无需重新定义显示或背压语义。

## 18. 已决议问题

1. 保持 12 MiB JSON Snapshot 上限，与 Worker protobuf envelope 对齐；base64 不通过放宽硬上限来
   绕过容量边界，超限 fail closed。
2. `ConsoleSnapshotReducer` 作为公开 RuntimeAdapter 纯类型，限制由调用方显式注入。
3. 完成/崩溃后的最后 Snapshot 只保留到旧 Hub 完成订阅和 dispose；不跨 API 重启或新 epoch。

## 19. 完成记录

2026-08-13：新增共享 reducer、API Snapshot mirror、JSON source-generated DTO、16ms/64 条/256KiB
批处理、消息数/编码字节双预算队列和慢消费者 resync；Worker 首批输出强制 Snapshot；API 终态和
dispose 完成订阅；控制面 wait probe 不再保存 DisplayBatch，并增加字节预算；Hub 完成与发布共用
生命周期 gate；Reducer 对注入的 transaction/node limits 做 fail-closed 校验。开发 Docker 验证结果：
RuntimeAdapter `Snapshot|ConsoleContract` 54 项、`CloudEmuera.Realtime.Tests` 11 项、Worker
快照/背压过滤 2 项、完整 Worker 集成 19 项通过；Release solution build 通过。

2026-08-13 评审修订：快照 JSON 改为惰性编码并按需缓存（`SnapshotEncodingCount`）；batcher 字节预算
改为单事务精确尺寸 + 固定开销上界，消除 O(n²)；`snapshot-raced` 不再计 slow-consumer 失败，读取路径
编码超限 fail-closed 并回收 Worker；`Complete` 后终态优先于残留 resync 标记；控制面丢弃超预算事件
记录计数；mapper 物理移入 `CloudEmuera.Realtime`；新增 golden JSON、惰性编码、编码超限、首消息
delta、快慢 reader 隔离、epoch 完成与 tracker 测试，并补 reducer 随机化属性测试。修订后
`CloudEmuera.Realtime.Tests` 23 项、RuntimeAdapter `Snapshot|ConsoleContract` 55 项、完整 Worker
集成 19 项、Release solution build 通过。

2026-08-14 评审修复：同一 Snapshot 的并发惰性编码使用 single-flight，镜像发布只在状态替换处失效
缓存；timer/终态 flush 的编码故障通过 `FaultReported` 上报并回收 Worker；pending event 因消息数或
字节预算淘汰时累计丢弃计数。新增并发编码回归测试，`CloudEmuera.Realtime.Tests` 24 项通过。
