# ADR-0018：Emuera 完整结构化交互状态、能力矩阵、计时语义和 IPC major 升级

状态：已接受

日期：2026-08-12

## 背景

P0 的 Console/Input 契约只覆盖了文本、按钮、基础图片和有限输入。固定上游
`2175f8a629257efb08214e093704b3a3d3d06d05` 还会改变行布局、临时行、背景、Shape/CBG、HTML
Island、Sprite、窗口元数据、输入来源、计时结果和音频状态。若这些入口继续被压平成普通文本、
被丢字段或由桌面 shim 静默吞掉，浏览器端无法恢复正确状态，也无法区分兼容失败和正常输出。

P1-07 还需要让 API/Worker 在协议层拒绝旧 major、能力集合不一致和未知结构，确保新增语义不会在
边界处被无声降级。

## 决定

### 1. 状态拓扑和原子归约

Worker 内的正式状态固定为：

```text
ConsoleSnapshot
├── snapshotSequence
├── scrollback: ConsoleLine(lineId, alignment, temporary, nodes[])
├── backgroundLayers[]
├── canvasScene(drawables[], hitRegions[])
├── mediaState(channels[])
├── currentPrompt?
├── windowMetadata
└── truncationMetadata
```

`ConsoleTransaction` 以一个 sequence 原子应用。事务失败不消费 sequence、不部分修改状态；
scrollback 只按最旧的完整行裁剪，scene、background、media 和 prompt 是当前状态，超限时拒绝
新事务。所有 ID、文本、节点、几何、HTML 子树、媒体 channel 和事务操作数都有
`ConsoleContractLimits`/`StructuredIpcLimits` 上限。

操作词汇固定为 `AppendLine`、`AppendInline`、`ReplaceLine`、`DeleteLines`、`ClearScrollback`、
`SetWindowMetadata`、背景/scene/hit-region upsert/remove/clear、媒体 channel set/stop、
`OpenPrompt` 和 `ClosePrompt`。未知 operation、节点、枚举或资源身份在 RuntimeAdapter 和 IPC
两端 fail closed。

### 2. 富内容和资源边界

- 文本只携带无控制字符的 `TextNode` 和封闭 `ConsoleTextStyle`；字体是逻辑 manifest family，
  不是服务器字体名。
- 图片/Sprite 只携带 Session manifest 的 `assetId`、经过验证的 source/destination rect、
  frame、z-index、opacity 和 alt/decorative 标记。
- Shape 只允许 rectangle、ellipse、line、polygon、space；坐标、尺寸和点数均有硬上限。
- HTML 先解析成 `ConsoleHtmlNode` AST，只允许固定标签、文本、换行和有限样式/manifest asset
  属性；禁止 raw HTML、CSS、script、事件、URL、iframe、object、data/blob/javascript scheme。
- Background、CBG clear、HTML Island 和 media 都是当前状态操作，不保存无限像素或重绘历史。
- Worker 不打开音频设备。`IRuntimeAudioPort` 将播放转换为逻辑 asset/channel/revision 状态，
  `startPolicy` 明确区分立即请求和等待用户手势；浏览器 autoplay 结果不会反向改变解释器结果。

### 3. 输入和计时

`ConsoleInputType` 固定包含 EnterKey、AnyKey、Integer、Text、AnyValue、IntegerButton、
TextButton、PrimitivePointerKey 和 WaitOnly。`ConsolePrompt` 保存默认值、OneInput、system input、
stop-message-skip、来源 allowlist、UTC 展示时间、deadline、DisplayTime、TimeUpMes 和封闭
timeout action。

Worker 使用 `IRuntimeClock` 的单调 timestamp 决定超时；UTC opened/deadline 仅用于展示和诊断。
输入、超时、取消和显式关闭在 `InputCoordinator` 的同一临界区竞争，只能有一个终态。超时按
`ReturnDefaultValue`、`ContinueWithoutValue` 或 `CancelRuntime` 执行；`StructuredGameConsole.IsTimeOut`
在本次超时后为 true，下一次 prompt 或有效输入会重置。浏览器断开、读取 Snapshot 和 wall-clock
跳变都不会重置单调 deadline。

### 4. 能力矩阵和 Blocked 准则

固定上游、headless adapter 和测试证据以
[`runtime-capabilities.json`](../runtime-capabilities.json) 登记，并由 schema 和
`scripts/verify-emuera-capabilities.sh` 校验。每个显式入口只能映射一个能力项；Supported 项必须
同时具备 adapter、IPC、fixture 和测试证据。

MVP 只批准三类 Blocked 能力：

1. `HOST_SHIM`：WinForms 窗口、全局鼠标/热键、宿主日志文件、桌面 tooltip 等不属于浏览器状态的
   编译或宿主能力；
2. `SECURITY_BOUNDARY`：CALLSHARP、插件、任意 DLL、外部进程、任意网络和不受限 graphics；
3. `UNSUPPORTED_DYNAMIC_GRAPHICS`：在 ADR-0019 接受有界 libgdiplus surface 之前使用的历史分类；
   P1-07 当前矩阵不得再用它阻塞 MVP Graphics/CBG。

Blocked 调用必须产生稳定兼容性诊断并停止相关运行路径。诊断、warning 和 debug 输出与玩家
scrollback 分离；诊断不能伪装成玩家文本。

### 5. IPC major 和清单

P1-07 引入新的 `cloudemuera.ipc.v3`/C# namespace，协议版本为 `3`。v3 使用显式
`ConsoleTransaction`、完整 `ConsoleSnapshot`、Prompt timing、Input source/payload、媒体和
capability digest；旧 v2 不原位重解释字段，也不能通过丢弃 v3 operation 进入 RUNNING。

registration、registration result、ready 和 runtime manifest 都必须携带相同的 capability set
digest。当前矩阵版本为 `p1-07`，上游 commit 和 digest 由 `RuntimeBaseline`、v3 handshake 和
Session runtime manifest 同步声明。任何 protocol/version/digest/upstream mismatch 均拒绝。

## 备选方案

1. 继续使用 v2 `Clear + Append` 和字符串 HTML：实现简单，但会丢失行/场景/媒体语义并扩大 XSS 和
   路径泄露风险，拒绝。
2. 传输浏览器 HTML、URL 或像素 bitmap：可以暂时复刻桌面 UI，但无法建立可审计的资源和安全边界，
   拒绝。
3. 把每个 Session 的解释器放进 API 进程：会破坏 Worker 隔离、生命周期和崩溃恢复约束，拒绝。

## 后果

RuntimeAdapter 和 v3 wire model 变得更严格、更冗长，但 P1-08/P1-09/P1-11 可以直接消费稳定的
Snapshot/transaction/prompt/media 语义，不需要重新猜测上游行边界。字体的逻辑 family 和布局参数
已冻结，浏览器实际 shaping、DOM/Canvas/WebAudio 渲染留给后续任务。动态 Graphics 依据 ADR-0019
转换为有界 Raster/Scene 状态；桌面 shim 和外部能力不会被误报为兼容。

## 验证

- RuntimeAdapter：结构化事务原子性、整行裁剪、HTML allowlist、输入来源/OneInput、单调超时和
  media revision 测试；
- IPC：v3 protobuf round-trip、snapshot validation、unknown/mismatch fail-closed 测试；
- Worker：双向 v3 mapper 测试、生产 Worker 使用结构化 audio port 检查；
- 真实 headless fixture：v18-compatible 与 EM+EE 的既有 input/save/runtime bridge 场景；
- dev Docker 中运行 `scripts/verify-emuera-capabilities.sh`、定向测试和 `scripts/check.sh`。
