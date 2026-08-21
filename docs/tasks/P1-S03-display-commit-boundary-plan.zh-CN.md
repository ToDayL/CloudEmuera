# P1-S03：显示提交边界与等待状态完整帧实施方案

## 1. 问题与目标

当前 Runtime、Worker、API 和浏览器都把连续 `ConsoleTransaction` 视为可以立即显示的增量。一次
输入结束后的 `ClosePrompt -> CLEARLINE/DeleteLines -> 重印文本/立绘 -> OpenPrompt` 因而可能被以下
任意内部边界拆成多次浏览器提交：

- Worker `ConsoleStateStore.MaxDeltaCount = 1024` 的历史压缩 Snapshot；
- API `BatchMaxDelay = 16ms`；
- API `BatchMaxTransactions = 64`；
- API `BatchTargetBytes = 256KiB`；
- Worker IPC 分片、连接队列背压、Snapshot 编码和 resync 竞态。

这些边界只应限制内存、编码和传输工作量，不应定义用户可见状态。实现目标是：

1. Runtime 始终维护最新完整工作态，但浏览器只观察明确提交的显示帧；
2. 正常游戏循环尽可能只发布包含下一等待状态和 `OpenPrompt` 的完整帧；
3. 16ms、64 条、256KiB 和 1024 条上限继续保护资源，但达到上限只能改变内部表示或传输方式，不能
   提前产生可见 flush；
4. 重连、resync 和慢消费者只能取得最近一次已提交状态，不能取得清屏后、重印完成前的工作态 Snapshot；
5. 所有队列和缓存继续同时受数量与字节硬上限约束，不以消除闪烁为由引入无界帧缓存。

本方案中的“完整帧”表示浏览器一次原子提交后得到完整一致的显示状态。正常小帧可以用一组连续 delta
表达；超过 delta 预算时改用一份完整 Snapshot。它不要求每次等待都无条件发送最大 12MiB Snapshot。

## 2. 核心语义

### 2.1 工作态与已提交态分离

每个 Worker epoch 同时维护两种状态：

```text
workingState
  Runtime 最新状态；可处于关闭 prompt、删除旧行、重印新屏的中间阶段。

committedState
  最近一次允许浏览器观察的完整状态；新连接和 resync 的唯一基线。
```

每个 operation 仍立即、原子地归约到 `workingState` 并分配单调 sequence。operation 是否已对浏览器可见
由 `committedSequence` 单独表示，不能再用“最新 Snapshot sequence”推断。

`committedState` 只保留最新完整帧和有界的发送优化信息，不为每个历史 prompt 永久排队。如果 Runtime
产生 committed frame 的速度超过 output pump 或网络，允许合并掉尚未发送的旧完整帧并退化为最新
CommittedSnapshot；可以少展示动画帧，但任何实际展示的帧都必须完整。

在下一显示提交到来前，浏览器继续显示旧 `committedState`。API 不发送清空、逐行重印或无 prompt 的
中间状态；输入是否仍可接受继续由 Worker 当前输入槽裁决，显示提交协议不改变 ADR-0025 的无目标输入语义。

### 2.2 显式显示提交

新增公共 `DisplayCommit` 语义，至少包含：

- `frameId`：当前 Worker epoch 内单调递增；
- `commitSequence`：该帧包含的最后 sequence；
- `reason`：`WAITING_FOR_INPUT`、`RUNTIME_COMPLETED`、`RUNTIME_FAILED` 或未来明确批准的
  `EXPLICIT_REFRESH`；
- `requiresSnapshot`：本帧是否已丢弃增量表示、必须用完整 Snapshot 发布。

MVP 的主要提交点是成功建立新输入槽的 `OpenPrompt`。包含 `OpenPrompt` 的 transaction 必须是该帧最后
一条 transaction，且 `OpenPrompt` 必须是 transaction 的最后一个 operation。完成该 operation 后：

```text
workingState -> committedState
working sequence -> committedSequence
frameId++
发布一个完整、原子的等待状态帧
```

不能仅由 API 猜测 `ClosePrompt/OpenPrompt` 来维持帧状态。历史压缩可能抹掉 `ClosePrompt` 增量，因此
commit 状态必须由拥有 Console 权威状态的 RuntimeAdapter/Worker 产生，并随 Snapshot 一起传递。

以下事件也是显式边界：

- Runtime 正常完成：先提交最终完整无 prompt 状态，再发送 stream ended；
- Runtime 可显示失败：提交经过允许列表处理的最终错误状态，再结束流；
- Worker/API 强制终止且无法形成最终状态：保留上一 committed frame，直接结束流，不伪造中间提交；
- epoch 切换：清空旧 working/pending 状态；新 epoch 在首个 commit 前返回 `SNAPSHOT_NOT_READY`。

16ms 超时、transaction 数量、编码字节、Worker IPC chunk、心跳和普通 Snapshot 均不是
`DisplayCommit`。

## 3. 有界增量与 Snapshot 降级

### 3.1 不再把 Snapshot 当作可见提交

Snapshot 分为：

```text
WorkingSnapshot
  用于 Worker 历史压缩、IPC追赶和 API working mirror 恢复；不可发送给浏览器。

CommittedSnapshot
  在 DisplayCommit 时冻结；是 resume/resync 和大帧降级的可见基线。
```

IPC Snapshot 必须携带 `frameId`、`committedSequence` 和 `isCommitted`（或等价字段）。API 收到
WorkingSnapshot 时只替换 `workingSnapshot`，不得：

- 调用订阅的 `RequestResync("snapshot-replaced")`；
- 清空浏览器待发的已提交帧；
- 重置可见 sequence；
- 结束当前未提交 frame。

只有新的 CommittedSnapshot 才能更新 `committedSnapshot`。即使 Snapshot 编码期间 working mirror
继续推进，也不产生 `snapshot-raced`；比较对象应是 `committedFrameId/committedSequence`，而不是
working sequence。

### 3.2 1024 条 MaxDeltaCount

`MaxDeltaCount` 继续作为 Worker 内 Runtime 生产者与 output pump 之间的短期增量上限，但行为改为：

1. transaction 始终归约到有界 `workingState`；
2. 未提交 delta 达到 1024 时，丢弃本帧的逐条 delta 表示；
3. 设置 `requiresSnapshotAtCommit = true`；
4. output pump 可以发送 WorkingSnapshot 更新 API 的隐藏 working mirror，也可以等下一 commit 再发送；
5. 到达 `DisplayCommit` 后冻结完整 `committedSnapshot`，发送该 Snapshot，而不是发送中间 Snapshot。

因此 1024 仍限制内存，但不再产生随机可见重同步。调整该数值只影响增量命中率，不影响显示语义。

### 3.3 16ms、64 条和 256KiB

三个阈值保留为内部工作阈值，但删除“达到阈值即发布 `display.batch`”的语义：

| 阈值 | 新行为 | 明确禁止 |
| --- | --- | --- |
| 16ms | 从可见 batcher 删除；如保留，只可唤醒内部 pump、传输隐藏 working checkpoint 或更新诊断，浏览器可见状态不变 | timer callback 调用可见 `Flush()` |
| 64 transactions | 结束内部 chunk，或直接放弃本帧 delta 优化并标记 `requiresSnapshotAtCommit` | 把前 64 条作为一个可见 batch |
| 256KiB | 结束内部编码 chunk，或丢弃本帧 delta 表示并标记 `requiresSnapshotAtCommit` | 独立发送达到 256KiB 的中间画面 |

小帧在 commit 时编码为一个原子 `display.frame` delta，浏览器一次归约并替换 state。若从上一
committed state 到本次 commit 的 delta 超过 64 条或 256KiB，则不拆成多个可见 batch，改发本次完整
CommittedSnapshot。

256KiB 是 delta 优化目标，不是帧硬上限。完整 CommittedSnapshot 继续受 12MiB 实际 UTF-8 硬上限约束。
若完整 Snapshot 超过硬上限，Hub fail closed、Worker 回收并把 Session 对账为 `CRASHED`；不得裁剪必要
节点、拆成可见中间帧或无限缓存。

### 3.4 为什么不在浏览器暂存 multipart frame

首版不把大帧拆成多个 WebSocket part 后交给每个浏览器缓存。那会按连接数复制未提交状态，并引入 part
丢失、重连、内存预算和 React 引用泄漏问题。API 已经维护每 Worker 一份完整镜像，更简单且更有界的策略是：

- 小帧：一个原子 delta frame；
- 大帧、历史压缩或任何 delta 不完整：一个完整 CommittedSnapshot；
- 连接队列溢出：丢弃待发 delta，下一次读取最近 CommittedSnapshot。

以后只有在真实 Snapshot 大小和带宽数据证明单消息 Snapshot 不可接受时，才另立 ADR 引入 multipart
Snapshot；multipart 的所有 part 在最终 commit 前也必须不可见。

## 4. 各层改造

### 4.1 RuntimeAdapter

将 `ConsoleStateStore` 扩展为显示提交权威：

- 保留 bounded `workingState`；
- 新增不可变 `CommittedSnapshot`、`CommittedSequence`、`FrameId`；
- 保存“自上一 commit 起的 delta”，同时按 transaction 数和估算/实际编码字节设置降级标志；
- `OpenPrompt` 成功归约后调用 `CommitFrame(WAITING_FOR_INPUT)`；
- 历史压缩只更新 working baseline，不覆盖 committed baseline；
- 提供 `ReadCommittedSince(lastCommittedFrameId/sequence)`，返回 `UpToDate`、原子 delta frame 或完整
  CommittedSnapshot，不返回未提交 transaction；
- 建立 invariant：`committedSequence <= workingSequence`，且 committed Snapshot 的 prompt/sequence
  与 commit reason 一致。

`ConsoleSnapshot` wire model 增加显示提交元数据，或新增明确区分 working/committed 的封装类型；不能用
可空字段和调用方约定暗示语义。

### 4.2 Worker

`OutputPumpAsync` 改为优先发送 committed output：

- 普通 10/20ms pump 只用于抽取和发送隐藏 working checkpoint，或检查是否出现新 commit；
- 一个 commit 的小 delta 作为同一原子 frame 发送；
- delta 超过 64/256KiB、历史已压缩或游标落后时，发送完整 CommittedSnapshot；
- 多个 commit 快于 pump 时不建立无界 committed-frame 队列；丢弃尚未发送的旧 delta，直接发送最新
  CommittedSnapshot；
- `StructuredIpcLimits.MaxTransactions` 只允许拆 working checkpoint，不能拆可见 delta frame；可见 delta
  超过 IPC 单消息限制直接选择 CommittedSnapshot；
- 更新 `lastSentCommittedSequence/frameId` 与 `lastObservedWorkingSequence` 两个游标，不能再用一个
  `lastSentSequence` 混合两种进度；
- heartbeat 分别报告 working sequence、committed sequence 和 current prompt，便于诊断 Runtime 正在
  计算还是发送链路落后。

### 4.3 IPC 与 Contracts

这是破坏性协议语义变更，实施前新增 ADR，并按仓库现行策略升级单一受支持版本。消息至少区分：

- `display.working.checkpoint`：可选、仅 API mirror 消费；
- `display.frame`：包含 frameId、起止 sequence、commit reason 和原子 delta；
- `display.snapshot`：标明 committed frameId/sequence 的完整状态；
- runtime terminal：必须排在最终 committed frame 之后。

终止事件发送后，Worker 在 shutdown grace period 内等待 API 通过双向 IPC 返回 `terminal_ack`；确认后才
发送 `WorkerStopped` 并退出，超时仍按既有有界关闭策略收尾。

validator 必须拒绝：

- frameId 倒退、跨 epoch 混入；
- delta 起点不是上一 committed sequence + 1；
- `WAITING_FOR_INPUT` commit 的最后 operation 不是 `OpenPrompt`；
- Snapshot 的 envelope、payload 与 committed metadata 不一致；
- working checkpoint 被标为浏览器可见；
- 同一 frame 同时携带互相冲突的 delta 和 Snapshot。

Realtime WebSocket 可以保留 `session.snapshot` 和一个新的原子 `display.frame`，但需升级 schema/子协议，
不能把旧 `display.batch` 必需字段静默改成 commit 语义。

### 4.4 API SessionOutputHub

Hub 改为双镜像：

```text
workingSnapshot / workingSequence
committedSnapshot / committedSequence / committedFrameId
```

处理规则：

1. 所有合法 IPC transaction/checkpoint 严格归约到 working mirror；
2. working Snapshot 替换不触发订阅 resync；
3. 收到 commit 后验证 working state、frame 元数据与 prompt，原子提升 committed mirror；
4. 小 delta frame 编码一次并共享给订阅；大帧只失效 committed Snapshot JSON 缓存，按需 single-flight
   编码一次；
5. `Subscribe`、queue overflow、sequence gap 和 resync 始终读取 committed mirror；
6. resync 编码期间只比较 committedFrameId。working sequence 推进不能触发 `snapshot-raced`；
7. 下一 committed frame 到达才允许再次 resync，且旧已编码 committed Snapshot仍是合法完整基线；
8. 删除 `RealtimeBatcher` 的可见 timer/数量/目标字节 flush；若保留该类，只负责 commit 内选择 delta
   或 Snapshot，方法命名不得继续暗示定时可见 flush。

连接队列的 32/64 条、1/2MiB 预算继续生效。溢出时清空待发已提交 delta，并请求最新
CommittedSnapshot；绝不读取 working mirror。

### 4.5 Web

浏览器 store 只接收两种可见原子更新：

- `session.snapshot`：完整替换为某个 committed frame；
- `display.frame`：在私有 candidate 上完整归约，全部成功后一次替换 state。

不再接收或渲染 working checkpoint。`display.frame` 的 envelope sequence 必须等于 commit sequence；
frameId、epoch、first/last sequence 任一不连续就保持当前已提交画面并等待 resync，不能部分应用。

React `SessionStoreState` 增加 `committedFrameId`。一次 frame 最多调用一次 `notify()`；Canvas、Scrollback
和 Prompt 从同一个新 state render。`resyncing` 可以显示连接状态提示，但不得清空或换成 working state。

## 5. 非 prompt 场景

“只在 OpenPrompt 发布”是正常游戏循环的默认，但不能让无 prompt Runtime 永久占用未提交状态。因此提交
原因必须显式，而不能恢复为定时 flush：

- 首次启动：在首个 `OpenPrompt` 前保持加载态；
- 正常结束：`RUNTIME_COMPLETED` 提交最终完整帧；
- 可显示错误：`RUNTIME_FAILED` 提交最终错误帧；
- 明确的上游显示/刷新语义：经过兼容性测试后可映射为 `EXPLICIT_REFRESH`；
- 单纯运行超过某个 wall-clock 时间、超过 64 条或超过 256KiB：不提交；只降级为下次 commit Snapshot；
- 无限输出且永不到达任何明确安全点：由现有输出/CPU/Session 故障策略有界终止或管理员处理，不能
  通过展示随机中间态伪装成正确帧。

`EXPLICIT_REFRESH` 的映射必须另有真实上游证据和 fixture；第一版不得把每次 `RefreshStrings`、每个
Worker pump tick 或每个 ERB statement 自动当作安全提交点。

## 6. 实施顺序

### 阶段 A：冻结决定和建立回归证据

1. 新建 ADR，取代 ADR-0020 中“16ms/64/256KiB 产生可见 flush”和“最新 working Snapshot 用于 resync”
   的部分；同步 requirements/design/development plan 的术语。
2. 固化当前问题 fixture：`ClosePrompt -> DeleteLines(34) -> AppendLine(34) -> OpenPrompt`，在删除阶段
   注入第 1025 条 transaction/WorkingSnapshot。
3. 测试证明当前实现会观察到 514 -> 480 -> ... -> 514，作为修复前失败证据。

### 阶段 B：Runtime/Worker committed frame

1. 在 RuntimeAdapter 落地 working/committed 双状态和 `DisplayCommit`；
2. 将 1024 overflow 改为 `requiresSnapshotAtCommit`；
3. Worker 改用 committed cursor，完成 IPC 协议升级；
4. 验证高速 Runtime 在 prompt 前生成任意数量 transaction 时，Worker 对外没有可见 frame。

### 阶段 C：API 双镜像与阈值语义

1. Hub 分离 working/committed mirror；
2. 移除 16ms/64/256KiB 的可见 flush；
3. 小 commit 发送一个 delta frame，超预算 commit 发送完整 Snapshot；
4. queue overflow/resync 只读取 committed Snapshot；
5. 删除诊断性 `DeferOutputUntilNextPrompt` 开关及其特殊分支，避免两套语义并存。

### 阶段 D：Web 原子 frame 与端到端验证

1. 升级协议生成类型和 decoder；
2. reducer 原子应用一个完整 frame；
3. 增加 render commit 计数和 snapshot/frame reason 诊断；
4. 用真实游戏 Session 验证 CLEARLINE、立绘重绘、TINPUT 和断线重连；
5. 完整运行 `./scripts/check.sh`。

各阶段必须以同一次协议版本升级保持 API、Worker 和 Web fail closed，不能部署会把 working Snapshot 当作
旧版可见 Snapshot 的混合组合。

## 7. 验证矩阵

### RuntimeAdapter

- 正常：删除/重印 34 行后 OpenPrompt，只产生一个 committed frame；
- 边界：未提交 transaction 分别达到 16ms、64 条、256KiB 和 1024 条；committed state 不变；
- 降级：第 65 条、超过 256KiB、历史压缩均设置 snapshot-at-commit，最终 Snapshot 等价于完整归约；
- 失败：OpenPrompt 非最后 operation、重复 commit、frameId/sequence 倒退必须拒绝；
- 终态：无 prompt 正常完成产生一份最终 committed Snapshot。

### Worker/IPC

- output pump 在长帧中多次运行，只发送 working checkpoint，不发送可见 frame；
- IPC 64 条 chunk 和 256KiB chunk 不改变 committed cursor；
- output pump 落后超过 1024 后，下一 commit 发送完整 committed Snapshot；
- epoch 重开、shutdown、runtime fault 和控制通道断开不会泄露 working state；
- 协议 golden、capability digest 和旧版本拒绝测试同步更新。

### API

- WorkingSnapshot(480 行) 更新 working mirror，但订阅仍保持 committed 514 行；
- OpenPrompt commit 后一次更新为新 514 行；订阅观察不到 480/487/500/505；
- timer、64 条和 256KiB 三条路径的可见发送计数均为零；
- 大 frame commit 只发送一份 Snapshot，小 frame commit 只发送一个原子 delta frame；
- working sequence 在 committed Snapshot 编码期间推进不产生 `snapshot-raced`；
- 新 committed frame 在编码期间到达时，旧完整 frame 或新完整 frame均合法，最终收敛且不显示 working；
- queue soft/hard overflow、慢连接和多连接共享 Snapshot 编码保持有界。

### Web/集成/E2E

- 每个 frame 只发生一次 store notify 和一次 Scrollback/Canvas/Prompt 共同 render commit；
- resync banner 不替换当前画面，replacement Snapshot 必须带 committed frameId；
- 真实 `CLEARLINE + 重印 + OpenPrompt` 无空屏或逐行增长；
- 30ms TINPUT 循环只观察完整等待帧，输入继续按到达时当前槽处理；
- 断线发生在 working frame 中间时，重连取得上一 committed frame；下一 commit 后一次切换；
- terminal、Worker crash、reopen epoch 和移动端滚动行为回归。

## 8. 完成条件与非方案

完成必须同时满足：

1. 1024、16ms、64 条和 256KiB 四个边界均不再产生可见中间状态；
2. resume/resync 永远不返回 WorkingSnapshot；
3. 正常等待循环每个 commit 最多一次浏览器 state/React 提交；
4. 所有 working、delta、Snapshot 和连接队列都有数量及字节上限；
5. `./scripts/check.sh` 通过，协议、ADR、需求、设计和兼容性报告同步更新。

以下做法不作为解决方案：

- 单纯增大或删除 1024 上限；
- 把 16ms 改成更长时间、把 64/256KiB 改成更大数值；
- 前端 debounce、CSS 隐藏、保留旧 DOM 后猜测画面是否稳定；
- Snapshot 到达后立即 `resync.required`；
- 为避免完整 Snapshot 而发送多个可见 multipart 中间状态；
- 无限缓存直到下一个 prompt。
