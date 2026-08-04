# P0-03 结构化 Console 与输入端口详细设计及实现计划

状态：DONE（2026-08-04 已实现并通过验收）  
编写日期：2026-08-04  
对应开发步骤：`P0-03 — 结构化 Console 与输入端口`  
前置条件：P0-00、P0-01、P0-02 已完成  
后续步骤：P0-04 无 UI Runtime 运行到 INPUT

## 1. 为什么下一步是 P0-03

本计划编写时，`docs/development-plan.zh-CN.md` 已将 P0-02 标为 `DONE`，P0-03 是唯一的 `NEXT`。当时 RuntimeAdapter 已有平台无关的 `IGameConsole` 外壳、单调时钟、受控文件系统和图像/音频端口，但 Console 类型仍只是带 `kind`、`text` 字符串的临时 envelope；它没有可验证的显示语义、sequence、快照上限或输入竞争规则。

P0-04 将把固定上游 Emuera 接到这些端口并首次真实执行 ERB。若在接线上游时才决定 Console 契约，解释器调用映射、HTML 安全策略、重连状态和输入唤醒会相互纠缠，难以区分“上游兼容错误”和“Console 模型错误”。因此 P0-03 必须先用合成调用固定一个完全独立于 WinForms、浏览器和 IPC 的内存模型。

本步骤完成后，仍不能宣称仓库已经能执行 ERB。真实解释器启动、内置源码接线、fixture transcript 对比属于 P0-04；本步骤只提供 P0-04 可以接入的稳定 Console 与输入边界。

## 2. 目标与验收摘要

P0-03 完成后，`CloudEmuera.RuntimeAdapter` 应具备：

- 类型化的文字、样式、换行、按钮、图片和输入提示模型；
- 对 Emuera HTML 最小子集的 fail-closed 解析，输出只能是结构化节点；
- 由单一 Console 实例分配的 64 位严格递增 sequence；
- 可配置且有界的显示树、增量历史和输入去重记录；
- 可以返回“连续增量”或“快照加后续增量”的恢复读取原型；
- 唯一 `promptId`、唯一 `clientMessageId`、首个有效输入胜出、重复结果稳定的输入协调器；
- 可由 P0-04 同步解释器线程调用、由另一个线程提交输入的 `IGameConsole` 实现；
- 无 UI 类型、任意 URL、原始 HTML 或无限集合泄漏到公共契约。

主验证命令：

```bash
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=ConsoleContract'
```

全局质量门：

```bash
./scripts/check.sh
./scripts/verify-third-party.sh
git diff --check
```

## 3. 需求映射与本步骤边界

### 3.1 直接完成的需求切片

| 需求 | P0-03 落点 |
| --- | --- |
| PLAY-001 | Console 只输出类型化节点和操作，不输出可执行 HTML |
| PLAY-003 | 最小 HTML allowlist 解析；脚本、事件属性、URL 和未知危险结构不能成为节点 |
| PLAY-004 | 每个已接受操作或原子批次获得严格递增的 `long sequence` |
| PLAY-005 | 内存快照与短期增量都有配置上限 |
| PLAY-007 | 每次 prompt 生成唯一 `promptId`；输入必须有 `clientMessageId` |
| PLAY-008 | stale prompt 被拒绝；重复消息返回确定结果，不二次唤醒 Runtime |
| PLAY-010 | 超限时折叠快照并裁剪已被快照覆盖的增量 |
| PLAY-011 | 并发回答同一 prompt 时仅首个有效输入成功 |
| NFR-008 | 输出入口不等待浏览器；相邻文本可在一个原子批次中归约 |

### 3.2 部分完成、由后续步骤闭环的需求

- PLAY-002：本步骤实现文字、颜色、字体样式、换行、按钮、tooltip、图片；Sprite、背景图层和音频已有端口但不进入本步骤的显示树，须由 ADR-004 和后续兼容任务明确支持等级。
- PLAY-006：本步骤实现纯内存的 `ReadSince`/恢复选择和连续性证明；订阅先于读取的 Realtime Gateway 恢复屏障属于 Phase 1。
- PLAY-009：本步骤提供不依赖 hover、键盘事件或桌面控件的语义化按钮/prompt 数据；桌面、移动端交互由 Web 任务验收。
- PLAY-012：本步骤限制 Worker 侧增量和快照内存；逐连接队列、WebSocket resync 和断开策略属于 Realtime Gateway。
- COMP-002/003：本步骤只用 P0-01 fixture 中已声明的 HTML/显示场景设计合成契约测试；真实双 runtime 执行从 P0-04 开始。

### 3.3 明确非目标

- 不修改固定上游源码；依据 ADR-0005，源码后来迁入 `src/CloudEmuera.EmueraRuntime/Upstream`，接线仍属于 P0-04；
- 不启动解释器，不读取 ERB/CSV，不声称 fixture transcript 已由 runtime 产生；
- 不修改 `src/CloudEmuera.Ipc/Protos/worker.proto`，不定义 WebSocket JSON schema、`sessionId` 或 `workerEpoch`；
- 不实现 API、Supervisor、Worker 进程、Web renderer 或浏览器 DOM；
- 不实现 Sprite、CBG、字体测量、自动换行像素算法或真实音频播放；
- 不允许原始 HTML、CSS、宿主文件路径、`Uri` 或浏览器 URL 进入显示节点；
- 不持久化 ConsoleSnapshot 或输入去重日志；P0-03 的状态全部属于单 Worker 内存；
- 不用 channel/队列容量阻塞解释器主循环。跨进程发送队列在 P0-06 以后实现。

## 4. 实现前置决策记录

详细设计第 19 节要求对应能力进入实现前形成 ADR。实现 session 应先提交：

1. `docs/adr/0003-console-snapshot-bounds.md`：记录 P0-03 内存表示、上限维度、裁剪规则、恢复结果和未来序列化/压缩的兼容边界；本步骤不选定 WebSocket 线格式和压缩算法。
2. `docs/adr/0004-runtime-rich-content-allowlist.md`：记录 MVP 第一批 HTML 标签/属性、图片资源引用规则，以及 Sprite/CBG/audio 的 Deferred 或 Experimental 状态。
3. 更新 `docs/adr/README.md` 的索引（若索引在实现时仍未列单项）。

ADR 必须与本计划一致；若评审决定扩大 allowlist 或改变快照语义，应先更新本计划和威胁/测试矩阵，不能只在代码中悄悄扩大能力。

## 5. 建议代码布局

生产代码仍放在 `src/CloudEmuera.RuntimeAdapter`，不新增项目引用。建议按职责拆分：

```text
src/CloudEmuera.RuntimeAdapter/
├── Console/
│   ├── ConsoleNode.cs
│   ├── ConsoleOperation.cs
│   ├── SequencedConsoleEvent.cs
│   ├── ConsoleSnapshot.cs
│   ├── ConsoleHistoryOptions.cs
│   ├── ConsoleStateStore.cs
│   ├── ConsoleResumeResult.cs
│   ├── EmueraHtmlParser.cs
│   └── StructuredGameConsole.cs
├── Input/
│   ├── ConsolePrompt.cs
│   ├── ConsoleInputCommand.cs
│   ├── ConsoleInputResult.cs
│   ├── InputCoordinator.cs
│   └── IPromptIdGenerator.cs
└── Ports/
    └── IGameConsole.cs
```

测试放在：

```text
tests/CloudEmuera.RuntimeAdapter.Tests/
├── Console/
│   ├── ConsoleNodeValidationTests.cs
│   ├── ConsoleSequenceTests.cs
│   ├── ConsoleSnapshotTests.cs
│   ├── ConsoleResumeTests.cs
│   ├── EmueraHtmlParserTests.cs
│   └── ConsoleTranscriptTests.cs
└── Input/
    ├── InputCoordinatorTests.cs
    └── StructuredGameConsoleInputTests.cs
```

文件名可在保持边界清晰时调整。不要将所有 record、存储、解析和协调逻辑堆进一个 `IGameConsole.cs`。

## 6. 结构化显示模型

### 6.1 基本约束

使用封闭类型层次或等价的可穷举 discriminated model。不得继续以任意 `Kind` 字符串表达生产操作。建议公开抽象基类 `ConsoleNode` 和以下叶节点：

- `TextNode(text, style)`；
- `LineBreakNode`；
- `ButtonNode(children, value, tooltip, enabled)`；
- `ImageNode(assetId, width?, height?, altText)`。

`ButtonNode.children` 第一版只允许 `TextNode`，或采用能明确限制最大深度的只读节点集合。不要允许按钮递归包含按钮、图片或任意深度的树。

值对象至少包括：

- `ConsoleTextStyle(foreground?, background?, decorations)`；
- `ConsoleColor`：规范化的 8 位 RGBA 或不透明 RGB 值对象，不能接受 CSS 字符串；
- `[Flags] ConsoleFontStyle`：`None/Bold/Italic/Underline/Strike`，拒绝未知位；
- `ConsoleAssetId`：只允许清单键形式的逻辑 ID，不接受 `/`、`\\`、冒号、控制字符、scheme 或宿主路径。

所有集合在构造时 defensive copy，并以 `IReadOnlyList<T>` 暴露。任何调用者之后修改原数组/list 都不能改变已入库事件或快照。

### 6.2 长度和数值校验

限制必须集中在一份 `ConsoleContractLimits` 或 options 中，测试使用小值覆盖边界；生产默认值由 ADR-003 记录，不应散落 magic number。至少限制：

- 单个 text、tooltip、button value、alt text 的 UTF-16 长度；
- 单批节点数、按钮 label 节点数和最大节点深度；
- 图片逻辑尺寸为正且不超过上限；禁止 NaN、Infinity 和负数；
- asset ID 长度与字符集；
- 单个操作的估算占用量不得大于整个历史上限。

超限输入应在进入 state store 前以稳定异常类型和 reason code 拒绝。不要静默截断单个字符串，因为这会改变游戏可见输出和按钮值。

### 6.3 操作与事件

Console state 至少支持以下操作：

- `AppendNodes(nodes)`：原子追加一批节点；
- `ClearConsole`：清空可见显示，但不回退 sequence；
- `OpenPrompt(prompt)`：把 prompt 设为当前 prompt；
- `ClosePrompt(promptId, reason)`：仅关闭匹配的当前 prompt；
- 可选 `ReplaceLastLine(nodes)`，只有能给出真实上游调用点和归约测试时才加入。

`SequencedConsoleEvent` 至少包含 `Sequence` 和 `Operation`。sequence 从 1 开始，0 表示尚无事件；每个被接受的操作或批次只分配一个序号。校验失败的操作、重复输入查询和 stale 输入不得消耗输出 sequence。

Sequence 分配、状态归约和增量写入必须在同一个临界区完成，保证观察者永远不会看到“已有序号但快照尚未应用”的中间态。达到 `long.MaxValue` 时 fail closed，抛出明确的 exhausted 错误，禁止回绕到负数或 0。

### 6.4 Prompt 在显示模型中的位置

当前 prompt 是快照的一等字段，不混入普通历史行：

```text
ConsoleSnapshot
  snapshotSequence
  visibleNodes / visibleLines
  currentPrompt|null
  truncation metadata
```

`OpenPrompt` 和 `ClosePrompt` 仍是有 sequence 的显示操作，以便增量消费者重建同一状态。打开新 prompt 前必须关闭或完成旧 prompt；无条件覆盖是状态错误。

## 7. HTML 最小允许列表

### 7.1 P0-03 allowlist

依据 P0-01 的两个 fixture，首批必须支持 `<b>...</b>` 与 `<i>...</i>`。ADR-004 可同时批准语义等价的 `<strong>`、`<em>`、`<u>`、`<s>`/`<strike>` 和无属性 `<br>`，但每增加一种标签都必须有解析、嵌套、大小写和错误闭合测试。

P0-03 不通过 HTML 标签创建图片或按钮。图片只由 `ImageNode(ConsoleAssetId, ...)` 创建；按钮只由结构化 Console 调用创建。这样原始 HTML 不存在 URL、点击事件或资源加载通道。

### 7.2 解析策略

- 输入被当作 fragment，不使用浏览器，不构造 DOM，不调用 `innerHTML`；
- 普通文本和实体解码为 `TextNode`，样式栈转换为 `ConsoleTextStyle`；
- 标签名按明确的 ASCII 大小写规则处理；
- 允许标签的未知属性也必须拒绝或整体降级为文本，不能“忽略属性但保留潜在语义”；
- `script`、`style`、`iframe`、`object`、`embed`、`svg`、`math`、`img`、`audio`、`video`、`a`，任何 `on*` 属性、`style`、`src`、`href`、`srcset` 和 URL-like 值都不得生成结构化可执行能力；
- malformed、未知或超深内容采用 ADR-004 规定的一种稳定 fail-closed 结果：推荐将整个 fragment 作为纯文本节点并附内部诊断；不得部分解析后把剩余内容解释为标记；
- 解析器必须线性推进并受最大输入长度、最大标签数和最大嵌套深度限制，避免退化回溯或递归栈耗尽；
- 禁止用正则表达式替代完整的 token/state 解析；若引入解析依赖，必须锁定版本、更新 lockfile/NOTICE，并证明其输出仍经过本地 allowlist 映射。

本步骤的“安全”含义是危险输入只能成为普通文本或明确失败，且任何输出类型都没有脚本、CSS 或 URL 字段；不是声称完成浏览器 CSP 或 renderer 安全验收。

## 8. Sequence、快照与有界历史

### 8.1 状态存储职责

`ConsoleStateStore`（名称可调整）拥有：

- 当前 sequence；
- 当前有界显示状态；
- 当前 prompt；
- 快照基线序号；
- 基线之后的连续增量 ring/deque；
- 当前节点数、文本单位数和估算字节数等计数。

所有 mutation 只能通过 `Apply(ConsoleOperation)`。读取返回不可变副本或真正不可变对象，不能把内部 list 暴露给调用者。

### 8.2 上限配置

`ConsoleHistoryOptions` 至少有：

- `MaxVisibleNodes`；
- `MaxVisibleTextLength`；
- `MaxDeltaCount`；
- `MaxEstimatedBytes`；
- `MaxInputReceiptCount`（可由 Input options 独立持有）。

每项必须为正，组合必须经过校验。测试不可依赖生产大上限，应注入 2～5 的小容量制造 rollover。

“估算字节”使用文档化、确定性的保守计量函数，例如固定对象开销加字符串 UTF-16 字节数；不要在每次输出时完整 JSON 序列化，也不要使用进程相关的 `GC.GetTotalMemory` 作为契约。

### 8.3 裁剪与压缩规则

当追加后超过可见节点、文本或估算字节上限时：

1. 以完整行/顶层节点为单位从最旧可见内容开始裁剪；
2. 绝不拆分 surrogate pair，不改变按钮 value，不留下半个结构节点；
3. 保留当前 prompt，即使普通显示内容需要进一步裁剪；
4. 更新 `WasTruncated` 和累计 `DroppedNodeCount`（或等价元数据）；
5. 生成/推进新快照基线，使已不能从旧状态重放的增量不再对外宣称可用；
6. 删除所有被新快照覆盖的旧增量，再从新基线保持连续增量。

若单个合法节点本身超过配置总预算，构造/Apply 必须拒绝；不能为了“最新内容优先”突破硬上限。

`ClearConsole` 应产生一个事件、清空可见节点并推进快照状态；历史空间可立即回收，但 sequence 不重置。关闭 prompt 不应删除此前可见输出。

### 8.4 恢复读取

提供类似 `ReadSince(long lastSequence)` 的纯内存 API，返回封闭结果：

- `UpToDate(currentSequence)`；
- `DeltaBatch(fromExclusive, toInclusive, events)`；
- `SnapshotWithDeltas(snapshot, eventsAfterSnapshot)`。

规则：

- `lastSequence == currentSequence` 返回 UpToDate；
- 游标位于仍连续保存的窗口内，返回恰好 `last+1..current`；
- 游标为 0、早于基线或发生裁剪，返回最新一致快照及其后的连续增量；
- `lastSequence < 0` 或大于 current 视为非法游标，不猜测或回退；
- 读取与 `Apply` 使用同一同步边界，返回点必须对应一个原子状态；
- 返回后产生的新事件可由后续读取获得。本步骤不实现 Gateway 的“先订阅后恢复”屏障。

测试必须把返回结果重新归约，证明其最终状态与 store 的当前 snapshot 深度相等，而不仅比较事件数量。

## 9. 输入模型与协调器

### 9.1 Prompt

`ConsolePrompt` 至少包含：

- 非空唯一 `PromptId`；
- `InputType`：第一版至少 `Text`、`Integer`；
- 可选提示文本；
- 可选默认值；
- 可选 timeout/default 行为；
- 类型化 constraints，而不是任意字典。

整数格式必须使用 invariant culture，明确是否允许符号和前导空白；建议只接受规范十进制字符串，并在结果中保留原始字符串供 P0-04 适配上游。文字输入限制最大长度和控制字符策略。constraint 失败返回 `InvalidFormat`，不得关闭 prompt。

`promptId` 由 `StructuredGameConsole` 调用注入的 `IPromptIdGenerator` 产生。`Read` 和 `Emit(OpenPromptOperation)` 只接受没有 ID 的 prompt template；生产调用者不能自行提供或复用 promptId。生产实现可使用加密随机/UUID 风格值；测试使用返回唯一固定序列的确定性生成器。禁止由 sequence 单独推导可跨 Worker 猜测的 ID。

### 9.2 输入命令与结果

P0-03 的本地命令只包含：

```text
ConsoleInputCommand(promptId, clientMessageId, value)
```

`sessionId`、`workerEpoch`、鉴权和 Session state 属于 P0-06 及 Realtime Gateway，不应伪造进本地端口。

结果为封闭枚举/record，至少包括：

- `Accepted`：首次成功抢占当前 prompt；
- `Duplicate`：相同 `clientMessageId` 已处理，并携带原始确定结果；
- `StalePrompt`：prompt 不是当前 prompt，或已完成/超时；
- `InvalidFormat`：值不满足当前 prompt；
- `NoActivePrompt`；
- 可选 `Cancelled/TimedOut`，用于 Runtime 等待侧，而非客户端重复提交的模糊失败。

相同 `clientMessageId` 携带不同 payload 时不得按普通 Duplicate 掩盖冲突；返回明确 `MessageConflict` 或稳定异常，并记录测试。回执对象不得包含未受限异常文本。

### 9.3 并发与去重顺序

`InputCoordinator.Submit` 在一个同步边界内执行：

1. 校验 command 的字段长度和格式；
2. 查询 bounded receipt cache；
3. 验证当前 promptId；
4. 验证输入 constraint；
5. 原子抢占 prompt；
6. 保存确定回执；
7. 只唤醒一次正在 `Read` 的 Runtime。

无效格式不能抢占 prompt；两个有效并发提交只有一个 `Accepted`，另一方得到 `StalePrompt`（若使用不同 message ID）或 `Duplicate`（相同 ID）。不得出现两个 Task/线程都拿到输入。

去重缓存按 `clientMessageId` 有明确容量，至少保存 message ID、命令 fingerprint 和原始结果。淘汰后，对已经结束的 prompt 再提交仍因 promptId 非当前而失败，不能再次执行。不要在 P0-03 引入数据库或全局静态 cache。

### 9.4 同步 `IGameConsole` 边界

固定上游解释器预计需要同步阻塞式输入，因此 P0-03 可以保留：

```text
Emit(ConsoleOperation)
Read(ConsolePrompt, CancellationToken) -> GameConsoleInput
```

但应将 P0-02 的任意字符串 envelope 替换为上述类型化契约。`StructuredGameConsole` 组合 state store 与 input coordinator：

- Runtime 线程调用 `Read` 时原子打开 prompt，并阻塞等待输入、取消或 timeout；
- 外部线程通过独立 `SubmitInput(command)` 提交，不直接调用 Runtime；
- `Read` 返回/抛出前必须关闭对应 prompt 并产生有序 prompt-close 操作；
- cancellation 和 timeout 竞态只有一个终态，迟到输入得到 StalePrompt；
- `Read` 不捕获 `SynchronizationContext`，不要求 WinForms message pump；
- timeout 使用 P0-02 的 `IRuntimeClock`/`RuntimeDeadline`，测试由 manual clock 推进，不使用真实 sleep。

若实现选择 async 内核加同步薄包装，必须证明不会在单线程 context 上死锁。不得用 `.Result`/`.Wait()` 包裹一个捕获 context 的异步实现。

## 10. 稳定 transcript 原型

P0-03 的 transcript 是结构化调用的规范化测试表示，不是新的网络协议，也不是 P0-01 的 `expected-transcript.txt` 替代品。

测试应编排与两个 fixture 对应的合成调用，例如：

1. append `V18-BOOT`/`EMEE-BOOT`；
2. 解析 `<b>V18-HTML</b>` 或 `<i>EMEE-HTML</i>`；
3. append 一个带 value 的按钮；
4. 打开 integer prompt；
5. 提交输入后关闭 prompt并继续 append 分支文本。

规范化输出应固定节点类型、样式、文本、promptId 和 sequence；测试 ID generator 固定，禁止随机值进入 golden assertion。不要声称这些调用由 ERB 产生。P0-04 应复用相同 serializer/assertion helper，把真实 runtime 调用结果与 P0-01 transcript 场景关联。

本步骤不必提交新的大型 golden 文件；优先使用清晰的对象断言或短小 approved text。若采用 JSON，属性顺序和 schema version 必须显式固定，且不能误称为正式 WebSocket schema。

## 11. 测试计划

所有新增测试使用 `[Trait("Category", "ConsoleContract")]`。测试名或注释标注关联需求编号。

### 11.1 节点与操作校验

- 文字、颜色、style flags、换行、按钮、tooltip、image asset ID 正常构造；
- null、超长文本、未知 style bit、非法颜色、非法尺寸、空 asset ID 被拒绝；
- asset ID 中的 `http:`, `https:`, `data:`, `javascript:`, 绝对路径、反斜杠和 `..` 被拒绝；
- 按钮禁止递归按钮/超深节点，value 与 label 各自受限；
- 构造后修改输入集合不会改变节点、事件或 snapshot。

### 11.2 HTML allowlist 与安全

- P0-01 使用的 `<b>`、`<i>` 产生对应样式文本；
- 批准的嵌套、大小写、实体、`<br>` 行为稳定；
- malformed closing、未闭合、超深、超长、过多标签 fail closed；
- `script/style/iframe/svg/math/img/a` 不产生能力节点；
- `onclick/onerror/style/src/srcset/href` 和大小写/空白/实体混淆不能绕过；
- `javascript:`、`data:`、协议相对 URL、外部 HTTP URL 不出现在任何输出字段；
- 随机文本/标签 fuzz 或 property test 不抛出未控制异常、不超出节点上限、不产生非 allowlist 类型。

### 11.3 Sequence 与原子性

- 首个成功操作为 1，后续严格递增且无重复；
- 一个 `AppendNodes` 批次只使用一个 sequence；
- 校验失败不消费 sequence；
- Clear、OpenPrompt、ClosePrompt 均参与 sequence；
- 多线程并发 Emit 后，序号集合恰为 `1..N`，按序归约结果确定且内部无损坏；
- 模拟接近 `long.MaxValue`，最后一个合法值可用，再次写入明确失败且不回绕。

### 11.4 Snapshot、裁剪与恢复

- 未超限时 snapshot 与全量事件归约结果相同；
- 分别触发节点数、文字量、增量数和估算字节上限；
- rollover 后所有内部计数仍不超过硬上限；
- 裁剪以完整节点为单位，当前 prompt 保留，truncation metadata 正确；
- Clear 后内容为空、sequence 保持递增、旧内存可回收；
- last=current 返回 UpToDate；窗口内返回连续 delta；窗口外返回 snapshot+deltas；
- 负数和未来游标被拒绝；
- 将每种恢复结果应用到空 reducer，所得状态等于读取时 snapshot；
- 读取与并发 Emit 重复运行，不出现缺号、倒序或 snapshotSequence 大于 currentSequence；
- 持续大量小 PRINT 的测试验证保留对象/计数有界，不使用不稳定的绝对进程内存断言。

### 11.5 输入协调

- promptId 和 clientMessageId 必填且受长度/字符规则限制；
- text/integer 正常输入，invalid format 不关闭 prompt；
- 旧 prompt、未知 prompt、无当前 prompt 返回明确结果；
- 同一命令重复提交返回相同原结果且 Runtime 只收到一次；
- 同 message ID 不同 payload 返回 conflict；
- 两个不同客户端消息并发回答，精确一个 Accepted；
- invalid 与 valid 并发时，valid 仍可赢得 prompt；
- receipt cache 超限后按策略淘汰，但已结束 prompt 不能再次执行；
- cancellation、timeout、有效输入三方竞态只有一个完成原因；
- fake clock 推进 timeout，无 wall-clock sleep；迟到输入为 StalePrompt；
- `Read` 等待期间可以持续读取 snapshot，且取消不会遗留 current prompt。

### 11.6 Transcript 与架构回归

- v18/em-ee 两组合成调用产生稳定结构化 transcript；
- HTML 文本内容与 fixture 预期关键文本对应，但测试清楚标注 `synthetic`；
- RuntimeAdapter 公共 API 继续不含 `System.Drawing`、WinForms、WPF、NAudio、Worker/Application 类型；
- 新公共类型不含 `Uri`、raw HTML passthrough 或可变集合；
- 现有 RuntimePaths、FixtureContract 和架构测试全部保持通过。

## 12. 具体文件变更清单

### 12.1 必须修改/新增

- 新增第 5 节列出的 Console/Input 生产类型和测试；
- 重写 `Ports/IGameConsole.cs`，移除或废弃 P0-02 的临时 `kind/text` envelope；当前尚无真实 runtime 消费者，优先直接替换，避免长期保留两个契约；
- 在现有架构测试中增加新公共 API 的危险字段/类型检查；
- 新增 ADR-003、ADR-004，并维护 ADR 索引；
- 已更新 `docs/development-plan.zh-CN.md`：P0-03 `NEXT → DONE`，P0-04 `TODO → NEXT`，并记录日期、平台和实际测试数。

### 12.2 原则上不修改

- `src/CloudEmuera.EmueraRuntime/Upstream/**`（P0-04 才开始真实解释器接线）；
- `src/CloudEmuera.Ipc/**`；
- Web、API、Domain、Application、Infrastructure 项目；
- P0-01 fixture payload 和 expected transcript，除非发现资产本身错误并单独说明。

不为本步骤引入 ASP.NET、JSON schema、数据库、channel 框架或桌面/媒体 NuGet 包。若 HTML parser 确需第三方包，必须先在 ADR-004 解释为何小型受限 parser 不足，并同步依赖锁和第三方声明。

## 13. 推荐实现顺序

1. 编写 ADR-003/ADR-004，固定上限维度、恢复结果和最小 allowlist；
2. 先写 `ConsoleNodeValidationTests` 与 HTML 攻击用例，建立失败基线；
3. 实现不可变节点、颜色/style/asset ID 和集中 limits；
4. 实现类型化 operations 和 sequenced envelope，替换临时 `kind/text`；
5. 实现 HTML tokenizer/parser，只映射 ADR 批准节点；
6. 实现单线程 `ConsoleStateStore.Apply` 和 snapshot reducer；
7. 加入同步保护、sequence overflow、并发 Emit 测试；
8. 加入各维度上限、裁剪、delta window 与 `ReadSince`；
9. 实现 prompt、constraints、ID generator 和 receipt cache；
10. 实现并发 input coordinator，再组合成 `StructuredGameConsole.Read/SubmitInput`；
11. 使用 manual clock 完成 timeout/cancel/input 竞态测试；
12. 添加 v18 与 EM+EE 合成 transcript；
13. 扩展架构测试，运行专项、RuntimeAdapter 全量和全局质量门；
14. 所有条件满足后更新开发计划状态和验证记录。

不要先写一个“大而全”的 `StructuredGameConsole` 再补测试；节点安全、state reducer、历史裁剪和输入协调应能分别测试。

## 14. 验证命令

实现 session 至少运行：

```bash
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=ConsoleContract'
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests
./scripts/check.sh
./scripts/verify-third-party.sh
git diff --check
```

若宿主没有 .NET SDK，使用仓库开发容器执行同一命令；最终记录必须说明实际运行环境。涉及新增 NuGet 包时还需执行 locked restore，并检查 `packages.lock.json` 与第三方声明。

2026-08-04 验证记录：Linux 开发容器（.NET 10 SDK）中 `ConsoleContract` 34 项和 RuntimeAdapter 全量 113 项通过；仓库 `./scripts/check.sh` 通过（后端 Release 0 警告/0 错误、Domain 4 项、Web typecheck/测试/build），`./scripts/verify-third-party.sh` 和 `git diff --check` 通过。

测试不得访问网络、显示服务器、系统音频设备或真实 wall-clock sleep。并发测试使用 barrier/TaskCompletionSource 等确定性同步，不用任意 `Thread.Sleep` 猜测时序。

## 15. 完成定义

只有同时满足以下条件，P0-03 才能标记为 `DONE`：

- ADR-003、ADR-004 已提交并与实现一致；
- `IGameConsole` 不再使用任意字符串 kind 作为生产显示协议；
- 文字、样式、换行、按钮、图片和 prompt 都有不可变、受限、类型化表示；
- P0-01 fixture 使用的 `<b>`/`<i>` 有安全的结构化解析；危险/未知 HTML 不产生脚本、CSS、URL 或媒体能力；
- sequence 对所有成功操作严格递增，批次原子，并发下无重复/缺口；
- 快照、可见内容、delta 和 receipt cache 都有可测试硬上限；
- 窗口内恢复返回完整连续 delta，窗口外返回可归约到当前状态的 snapshot+deltas；
- 每个 prompt 唯一，stale/duplicate/conflict/invalid 结果明确；并发回答只有一个 Accepted；
- timeout/cancel/input 竞态由单调 fake clock 和确定性同步测试覆盖；
- 合成 v18/EM+EE 调用产生稳定 transcript，但文档未误称真实 ERB 已运行；
- `ConsoleContract` 专项、RuntimeAdapter 全量和全局质量门全部成功；
- P0-02 的路径、端口、fixture 与架构回归无退化；
- 开发计划更新为 P0-03 `DONE`、P0-04 `NEXT`，并记录可复查的验证结果。

## 16. 交给 P0-04 的接口与注意事项

P0-04 只应编写薄的上游映射：把 Emuera 的 print/style/button/HTML/input 调用转换为本步骤的 nodes、operations 和 prompt，不应绕过 `StructuredGameConsole` 直接写 transcript 或自行分配 sequence。

P0-04 若发现上游需要本步骤未覆盖的操作，应先：

1. 标出固定上游 commit 的真实调用点；
2. 新增 P0-03 层的失败契约测试；
3. 判断是安全的语义扩展，还是 ADR-004 中尚未批准的能力；
4. 以最小类型化扩展实现，不能退回 raw HTML、任意 URL 或桌面对象。

`sessionId + workerEpoch` fencing、IPC schema 和浏览器 resume barrier 仍由 P0-06/Phase 1 提供。P0-03 的 `sequence` 是单 Worker Console 内的正确性基础；后续 envelope 必须组合 `(workerEpoch, sequence)`，不能假设跨 Worker 重启 sequence 单独全局唯一。

## 17. 主要风险与防偏提示

- **把 snapshot 当事件列表**：快照必须是应用到某个 sequence 后的完整有界状态，而不是旧事件的另一个无界副本。
- **只限制 delta 数量**：可见树、单事件、字符串、节点数和 receipt cache 同样需要硬上限。
- **HTML 黑名单**：安全边界必须是允许列表到封闭节点的映射；不断追加危险标签黑名单不可验收。
- **输入先抢占后校验**：invalid input 不能让合法客户端失去 prompt。格式校验必须发生在原子抢占之前。
- **重复消息重新验证当前 prompt**：去重查询应先于 current-prompt 判断，才能给已接受消息的网络重试返回稳定原结果；payload 冲突必须另行拒绝。
- **超时使用 UTC**：timeout 只能基于 P0-02 单调时钟；`UtcNow` 只可用于展示 `timeoutAt`，不能决定是否过期。
- **测试伪装真实兼容**：P0-03 transcript 来自 synthetic calls。只有 P0-04 harness 执行 P0-01 ERB 后才能宣称 runtime 兼容。
- **提前冻结网络 schema**：本步骤模型应可序列化，但 IPC/WebSocket 的版本信封和 source-generated JSON 契约留给相应任务，避免内部模型直接成为外部协议。
