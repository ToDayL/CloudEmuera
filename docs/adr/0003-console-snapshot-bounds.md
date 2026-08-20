# ADR-003：ConsoleSnapshot 有界内存与恢复语义

状态：Accepted  
日期：2026-08-04

2026-08-12 修订说明：ADR-0017 不删除本文已经实现的 Worker 内有界增量数据结构，但网络重连不再
承诺按客户端游标补发历史增量；Realtime Gateway 总是以完整 Snapshot 建立新基线。

## 背景

P0-03 需要让一个 Worker 内的 Console 同时服务同步解释器和可能断线的读取者。显示内容、短期增量和输入回执都必须有硬上限；恢复读取还必须能证明返回结果可以归约到同一份状态。本 ADR 只约束 RuntimeAdapter 的内存模型，不定义 WebSocket/IPC 线格式。

## 决定

- `ConsoleStateStore` 以不可变节点集合、当前 prompt、当前 `long` sequence、快照基线和基线后的连续事件列表表示状态。所有 Apply、序号分配和历史发布在同一临界区完成。
- 默认上限由 `ConsoleContractLimits` 与 `ConsoleHistoryOptions` 集中声明：可见顶层节点 4096、可见文本 UTF-16 单位 262144、增量 1024 条、估算状态/历史 4 MiB、输入回执 2048 条。单按钮标签为最多 512 个平铺展示节点，与单行和单批节点上限一致；节点最大深度为 16，与 IPC 校验边界一致。按钮值、tooltip、替代文本、prompt 文本与默认值均最多 16384 个 UTF-16 单元，与输入值上限一致，且低于 IPC 的单字符串上限。这样允许原版 Emuera 的样式切换和字符串值完整映射，同时仍由节点、文本和状态总预算约束。调用者可注入更小的值进行契约测试，但所有值必须为正。
- Prompt ID 不保存完整生命周期历史：`StructuredGameConsole` 只接受无 ID 的 prompt template，并通过每个生成器独有的随机会话前缀加单调计数器分配 ID。调用者提供的 ID 在生产入口拒绝；因此旧 ID 淘汰、receipt 淘汰或断线都不会改变其唯一性。生成器契约要求每次调用返回 fresh ID。
- “估算字节”使用固定对象开销加字符串 UTF-16 字节数和类型字段开销的保守确定性函数；不序列化 JSON，也不读取进程总内存。
- 超过节点、文本或状态估算上限时，从最旧顶层节点开始删除完整节点；单个合法节点若自身无法放入预算则拒绝。当前 prompt 不参与普通显示节点裁剪，且 `WasTruncated`/`DroppedNodeCount` 累计记录。
- `ClearConsole` 仍产生有序事件，但在应用后立即将空状态推进为新基线并释放旧增量引用。sequence 不重置。
- 增量窗口必须连续。窗口外、游标为 0 或游标早于基线时，返回基线快照及其后的连续事件；窗口内返回精确的 `last+1..current`；当前游标返回 `UpToDate`。负数和未来游标拒绝。
- 达到 `long.MaxValue` 时拒绝下一次操作，不允许回绕。快照和增量只存在于 Worker 内存，后续 Realtime Gateway 可组合 `(workerEpoch, sequence)` 并自行选择序列化/压缩方式。

## 备选方案

- 使用无限 `List` 保存完整 transcript：实现简单，但不满足 Worker 内存边界，且无法定义断线恢复成本。
- 每次读取都完整 JSON 序列化：可直接估算 payload，却把 UI/协议 concerns 带进 RuntimeAdapter，并造成不必要的 CPU/分配。
- 仅返回最近事件而不返回快照：游标落后或发生裁剪时无法证明最终状态，客户端只能猜测。

## 后果

RuntimeAdapter 可以在没有浏览器、IPC 或数据库的情况下提供可测试的恢复原型。裁剪会使旧事件不可重放，因此恢复结果明确降级为快照；正式网络协议、订阅先于恢复屏障、压缩和跨 Worker fencing 由后续阶段决定。
