# P1-07：Emuera 运行时语义与完整结构化交互协议详细开发方案

状态：已完成

设计日期：2026-08-12

对应开发步骤：`P1-07 — Emuera 运行时语义与完整结构化交互协议`

关联需求：SESS-004、PLAY-001～004、PLAY-007～012、COMP-001～009、SEC-001/004/006、
NFR-008/011/013/014、AC-005、AC-008、AC-009、AC-011、AC-012

关联决策：[`ADR-0003`](../adr/0003-console-snapshot-bounds.md)、
[`ADR-0004`](../adr/0004-runtime-rich-content-allowlist.md)、
[`ADR-0005`](../adr/0005-vendored-emuera-source.md)、
[`ADR-0018`](../adr/0018-emuera-structured-interaction-model.md)、
[`ADR-0017`](../adr/0017-trusted-self-hosted-mvp-simplification.md)

前置任务：P0-01～P0-05、P1-05、P1-06

后续任务：P1-08 完整 Snapshot/背压、P1-09 WebSocket/输入、P1-11 浏览器 Console、P1-15 MVP 验收

## 1. 目标结果

P1-07 把 Phase 0 的受控 Console/Input 切片扩展为 MVP 的正式 Emuera 运行时兼容边界。完成后必须得到：

1. 固定上游 Emuera.EM+EE 中所有可能影响浏览器游戏体验的 Console、Input、计时、图片、绘图、
   字体、HTML 和音频入口均被审计并进入机器可校验能力矩阵；不存在未分类入口。
2. 除需求和安全决策明确禁止的 DLL、外部进程、不受限网络、桌面插件等能力外，MVP 运行路径中的
   Emuera 功能都有语义等价的结构化表示，不使用无声 no-op、丢字段或普通文本替代成功结果。
3. RuntimeAdapter 形成封闭、平台无关、可归约且有硬上限的 Console 状态模型；Worker IPC 可以无损
   携带该模型，不传原始 HTML、宿主路径、任意 URL、桌面对象或像素所有权对象。
4. Emuera 的输入类型、默认值、OneInput、系统输入、计时输入、超时显示和 `ISTIMEOUT` 语义完整接入
   `promptId/clientMessageId` 协调器；浏览器断开不会暂停或重置 Worker 的计时。
5. v18-compatible 与当前 EM+EE fixture 对每个 Supported 能力具有真实解释器断言、稳定结构化
   transcript、IPC round-trip 和主要失败路径；能力矩阵本身成为 CI 和发布门。
6. P1-08/P1-09/P1-11 能只消费本阶段冻结的状态和事件语义，不必重新推断上游 Console 行为。

## 2. 完整支持的定义

“完整支持 Emuera”在本项目中不是复刻 WinForms 窗口或允许任意主机能力，而是：

- 对固定上游 commit `2175f8a629257efb08214e093704b3a3d3d06d05` 做入口级审计；
- 对所有会改变游戏可见状态、输入结果、计时结果或媒体状态的入口提供结构化等价语义；
- 对平台无关且不违反安全边界的能力标记为 `Supported` 并自动化验证；
- 只把需求明确禁止或浏览器平台本质上不能安全提供的宿主能力标记为 `Blocked`；
- Blocked 能力必须在 Game 验证/加载时产生稳定诊断，执行时 fail closed，不能悄悄成功；
- `Compatible`/`Experimental` 只能描述 MVP 范围外的非标准分支或已批准偏差，不能用来推迟
  PLAY-002、计时输入或固定上游中的浏览器可表达功能。

完整性由“入口覆盖 + 状态等价 + 失败可见 + 自动化证据”共同证明，不能仅以游戏能启动或代码能
编译作为完成标准。

## 3. 范围与非目标

### 3.1 本阶段必须实现

- 新 ADR，冻结完整能力矩阵、结构化状态拓扑、HTML/绘图允许列表、媒体状态和计时输入语义；
- RuntimeAdapter 的 Console/Input/Resource 契约、校验、大小估算、归约器和 Snapshot 原型；
- headless Emuera Console、图片/字体/音频 bridge 与真实解释器接线；
- API—Worker protobuf 新协议版本、双向 mapper、验证器、限制和版本握手；
- Worker 中真实结构化媒体/显示端口，移除 MVP 路径上的 `NoOpRuntimeAudioPort`；
- v18 与 EM+EE fixture、能力清单生成/校验脚本、协议和运行时兼容测试；
- Runtime baseline、manifest capability、兼容诊断、上游修改账本和中英文文档同步。

### 3.2 留给后续任务

- P1-08 实现 API 侧实时队列、Snapshot 请求/替换、背压和缺口重同步；P1-07 只保证 Worker 内状态
  可原子读取、可序列化、可归约且有界；
- P1-09 定义浏览器 WebSocket envelope、鉴权、订阅、恢复和输入转发；
- P1-11 实现 React DOM、Canvas 2D、WebAudio、字体加载、移动软键盘和视觉交互；
- P1-15 执行跨浏览器视觉/音频及代表游戏最终验收；
- 不实现 WinForms 窗口、系统托盘、系统全局热键、剪贴板自动读取、Rikaichan、原生插件、
  `CALLSHARP`、任意 DLL、外部程序或不受限网络；
- 不实现视频桌面串流、指令级 Runtime 快照、跨 Worker 计时恢复或跨 API 接管；Worker 崩溃仍按
  P1-05/P1-06 进入 `CRASHED`。

## 4. 当前基线与明确缺口

### 4.1 可直接复用

- `StructuredGameConsole` 已在一个临界区内分配 sequence、维护当前 prompt 和有界增量；
- `InputCoordinator` 已提供当前 prompt 原子抢占、`clientMessageId` 有界回执、取消/超时/输入竞争；
- `ConsoleStateStore` 已实现节点/文本/估算字节/历史上限及 Snapshot+deltas 归约；
- IPC v2 已传输 `AppendNodes/ClearConsole/OpenPrompt/ClosePrompt`，并校验 envelope、节点数和字符串；
- headless runtime 已执行真实上游 parser/process，支持基础 PRINT、按钮、allowlist HTML、静态图片/
  Sprite metadata、INPUT/INPUTS 和原生存档；
- `IRuntimeClock`、`IRuntimeImagePort`、`IRuntimeAudioPort` 和 Session runtime manifest 已建立稳定边界。

### 4.2 本阶段必须消除

| 当前事实 | P1-07 目标 |
| --- | --- |
| 节点只有 Text/LineBreak/Button/Image | 覆盖行布局、Sprite、背景、Shape/CBG、HTML Island 和媒体状态 |
| 操作只有 append/clear/open/close | 增加行替换/删除、场景和媒体变更等可归约操作 |
| TextStyle 缺少字体族、字号、对齐/布局信息 | 保存上游可见样式与确定布局参数 |
| Image 缺少 source rect/frame/position/z-index | 资源引用与绘制参数分离并完整传输 |
| Prompt 只有 Text/Integer 和相对 timeout | 覆盖全部输入类型、OneInput、默认值、显示时间、超时消息和稳定 deadline |
| headless `IsTimeOut` 恒 false | 与每次输入完成原因一致地维护 `ISTIMEOUT` |
| `DisplayTime/TimeUpMes` 被丢弃 | 倒计时展示状态和到期显示成为结构化语义 |
| delete/temp/alignment/background/CBG/tooltip 多处 no-op 或 Unsupported | 实现或按批准 Blocked 分类并给出稳定诊断 |
| Worker 使用 `NoOpRuntimeAudioPort` | 音频命令进入 Console 媒体状态和 IPC |
| HTML 仅有基础标签 | 按新 ADR 扩展安全子集并保持 fail closed |
| fixture 只证明 Phase 0 slice | 能力矩阵逐项映射真实 fixture/测试证据 |

## 5. 决策与能力矩阵制品

### 5.1 ADR-0018

实现前新增 `ADR-0018-emuera-structured-interaction-model.md`，至少冻结：

- Console 状态由 scrollback、scene、background、media、prompt 和 window metadata 哪些部分组成；
- 节点/操作的封闭类型、ID 生命周期、原子事务和裁剪顺序；
- Emuera HTML、HTML Island、Shape/CBG、Sprite、字体和音频的安全表达范围；
- Worker 单调计时与浏览器 wall-clock 展示的映射；
- capability 分类准则、唯一允许 Blocked 的能力类别和 fail-closed 行为；
- IPC 版本升级、旧 Worker 拒绝策略以及后续 WebSocket schema 的兼容规则。

若实现需要把原始 HTML/CSS、任意 URL、宿主绝对路径或可执行插件传过协议，必须停止并修改 ADR；
不得通过扩大字符串字段绕过结构化边界。

### 5.2 机器可校验能力矩阵

新增以下版本化制品（最终路径可在 ADR 中微调，但职责不可合并）：

```text
docs/runtime-capabilities.schema.json
docs/runtime-capabilities.json
docs/runtime-compatibility-report.zh-CN.md
scripts/verify-emuera-capabilities.sh
```

每条能力至少包含：

```text
capabilityId
upstreamCommit
upstreamEntrypoints[]
category                       # console/input/timer/html/image/drawing/font/audio/host
classification                 # Supported/Compatible/Experimental/Blocked
reasonCode
adapterTypes[]
ipcTypes[]
fixtureScenarios[]
testNames[]
securityNotes[]
```

验证脚本必须：

1. 扫描固定上游和 `UpstreamHeadless` 中受审计入口；
2. 要求每个入口恰好映射一个能力项；
3. 拒绝 Supported 项缺少 adapter、IPC、fixture 或测试证据；
4. 拒绝除 ADR 批准类别外的 Blocked 项；
5. 拒绝 headless MVP 路径中未登记的空方法、常量成功/失败返回或通用 `Unsupported()`；
6. 校验 matrix、runtime manifest、RuntimeBaseline 与源码集成版本一致。

编译器或平台要求存在但不影响游戏语义的 inert shim 也必须在矩阵中以 `HostShim` 说明调用条件和
无可见副作用证据，不能与游戏可见功能的 no-op 混为一谈。

## 6. 结构化 Console 状态模型

### 6.1 状态拓扑

`ConsoleSnapshot` 扩展为下列逻辑状态：

```text
ConsoleSnapshot
├── snapshotSequence
├── scrollback
│   └── ConsoleLine(lineId, alignment, temporary, nodes[])
├── backgroundLayers[]
├── canvasScene
│   ├── drawables[]
│   └── hitRegions[]
├── mediaState
│   └── channels[]
├── currentPrompt?
├── windowMetadata
└── truncationMetadata
```

- `scrollback` 是可访问 DOM 的文本/按钮主内容；以稳定 `lineId` 支持临时行替换和删除，不再用平铺
  Node 列表猜测行边界；
- `backgroundLayers` 和 `canvasScene` 是可归约的有界场景，不把每次重绘保存为无限历史；
- `mediaState` 保存每个逻辑 channel 当前资源、播放/停止、loop、volume 和 revision，供重连恢复；
- `currentPrompt` 独立于普通历史裁剪；
- `windowMetadata` 只保存 title、逻辑 viewport、默认颜色/字体等游戏可见状态，不保存宿主窗口句柄。

### 6.2 节点和值对象

建议的封闭类型：

| 类型 | 必要字段 | 约束 |
| --- | --- | --- |
| `TextRunNode` | text、foreground/background、font、size、decorations | 无控制字符；字体为 manifest logical family |
| `ButtonNode` | label runs、value、tooltip、enabled、generation | label 只含安全 inline 节点；value 有长度上限 |
| `InlineImageNode` | assetId、sourceRect、destinationSize、alt/decorative | rect 在资源边界内；无 URL/path |
| `SpriteNode` | assetId、frame/sourceRect、position、size、zIndex、opacity | 数值有限、维度/层级有硬上限 |
| `ShapeNode` | shape kind、geometry、fill/stroke、zIndex | 仅 ADR allowlist 的有限 primitive |
| `HtmlIslandNode` | 已解析的安全子树、layout box | 不含原始 HTML/CSS/URL |
| `HitRegion` | regionId、geometry、inputValue、enabled、tooltip | 与 drawable 分离；输入仍走 prompt |
| `BackgroundLayer` | layerId、assetId、mode、opacity、depth | mode 使用封闭枚举 |

所有坐标统一为 Emuera 逻辑像素；禁止 NaN/Infinity，规定整数/定点范围和舍入方式。颜色统一为非预乘
RGBA；透明度范围固定。资源尺寸在 `IRuntimeImagePort` 返回 metadata 后验证，不能信任脚本声明。

### 6.3 操作模型

建议的封闭操作：

```text
AppendLine / AppendInline
ReplaceLine / DeleteLines / ClearScrollback
SetWindowMetadata
UpsertBackground / RemoveBackground / ClearBackgrounds
UpsertDrawable / RemoveDrawable / ClearSceneRange / ClearScene
UpsertHitRegion / RemoveHitRegion / ClearHitRegions
SetMediaChannel / StopMediaChannel / StopAllMedia
OpenPrompt / ClosePrompt
```

一个上游调用若同时改变多个部分，必须生成一个 `ConsoleTransaction`，事务内操作以同一个 sequence
原子应用；客户端不能看到背景已更新但 hit region 尚未更新的半状态。IPC `DisplayBatch` 可以批量传输
多个连续 transaction，但不能改变 transaction 边界。

稳定 ID 由 Worker adapter 分配并限定在当前 Worker epoch；调用方不得提供跨 Session/epoch ID。
替换/删除未知 ID 必须稳定拒绝或按 ADR 明确为收敛 no-op，不能由 renderer 自行猜测。

### 6.4 裁剪和大小预算

在 ADR-0003 的总估算字节上限内增加分项上限：

- scrollback 行数、节点数、文本 UTF-16 单位；
- scene drawable/hit region/background layer 数；
- 单 geometry 点数、HTML 子树深度和节点数；
- media channel 数；
- 单 transaction 操作数和 IPC 序列化后字节数。

裁剪顺序必须确定：先删除最旧完整 scrollback 行；scene/background/media 作为当前状态不能按历史
年龄删除，超限的新变更必须拒绝并产生诊断；prompt 永不因普通显示裁剪丢失。裁剪产生新的 sequence
和累计 metadata，Snapshot 仍能单独归约为完整状态。

## 7. 资源、字体、绘图与音频

### 7.1 资源身份

运行时只使用 Session 创建时冻结 manifest 中的逻辑 `assetId`。建立 `IRuntimeAssetResolver` 或等价
端口，把 Emuera 相对资源名解析为：

```text
assetId, mediaType, byteLength, width/height?, frame metadata?, contentDigest
```

resolver 使用 `IRuntimeFileSystem` 和 `RuntimeFilePath`，拒绝绝对路径、父级段、链接逃逸、大小写/
Unicode 歧义和 manifest 外资源。协议只携带 `assetId` 与已验证绘制参数；资源字节由后续授权 HTTP
端点提供，不走 Worker 实时消息。

### 7.2 图片、Sprite 与绘图

- `PrintImg` 必须保留 source rect、目标尺寸和 y-position，不再把 Sprite 压平成整图；
- Sprite frame 解析以 runtime manifest 固定 metadata 为准；无效 frame 明确诊断；
- Background、Shape、HTML Island、CBG bitmap/button map 转换为有限场景操作；
- 动态 Graphics 不把任意原始像素无限推流。优先传确定的绘图命令；确需生成位图时，在 Worker 内以
  有界 surface 生成内容寻址的临时 asset，限制数量/尺寸/总字节，并在 Snapshot 引用淘汰后回收；
- redraw timer 只按脏 scene 和配置上限产生状态 revision，不把相同帧持续写入 scrollback。

### 7.3 字体与布局

- 字体仅从 Session manifest 中授权资源或部署允许字体映射解析，不按任意宿主 font name 搜索；
- 使用设计已确认的 SkiaSharp/HarfBuzzSharp（若实现切片确需新增包，更新 lockfile 与许可证）提供
  Linux 可重复 shaping、测量、行高和裁剪；
- adapter 输出逻辑布局参数和最终测量结果，P1-11 renderer 使用同一基线数据；
- 缺失字体必须使用 manifest 声明的确定 fallback 并产生兼容诊断，不能依赖服务器 locale；
- 覆盖 CJK、全角/半角、组合字符、emoji、粗斜体、固定行高、左右/居中对齐和按钮命中边界。

### 7.4 音频

将 `IRuntimeAudioPort` 从“宿主播放尝试”调整为结构化媒体命令端口，至少表达：

```text
Play(assetId, channel, loop, volume, startPolicy)
Stop(channel or assetId)
SetVolume(channel, volume)
StopAll(scope)
```

Worker 不访问音频设备，只验证资源并更新 `mediaState`/发出 Console transaction。相同 channel 的新
Play 替换旧 revision；Stop 幂等。浏览器自动播放限制留给 P1-11，但协议必须区分“Worker 已请求
播放”和“浏览器因用户手势尚未启动”，不能反向改变解释器结果。

## 8. HTML 安全模型

扩展 `EmueraHtmlParser` 时继续遵守 ADR-0004 的 fail-closed 原则：

- 先把上游支持的 HTML/HTML Island 语法归一化为 tokenizer/AST，再映射封闭节点；
- 标签、属性、枚举值、数字、颜色、嵌套和总长度逐项 allowlist；
- 禁止 script、事件属性、`style` 字符串、CSS expression、自定义元素、iframe/object、导航、
  `src/href/srcset` URL 和 data/blob/javascript scheme；
- 图片/背景属性只能引用 manifest `assetId`，不能携带浏览器 URL；
- 未知、错误闭合、超深、超限或属性冲突必须整段 fail closed，并产生稳定诊断；
- parser、RuntimeAdapter validator、IPC validator 和未来 TypeScript validator 共享测试向量，避免
  “Worker 接受、浏览器解释不同”的差异。

普通未知标记是否显示为文字或拒绝整个运行由 ADR-0018 按上游语义分类；任何路径都不能生成可执行
DOM。P1-07 测试只断言结构化 AST/操作，不使用浏览器 `innerHTML`。

## 9. 输入与计时语义

### 9.1 输入类型

`ConsoleInputType` 至少扩展为：

```text
EnterKey
AnyKey
Integer
Text
AnyValue
IntegerButton
TextButton
PrimitivePointerKey
WaitOnly
```

`ConsolePrompt` 增加：

```text
promptId, inputType, oneInput, systemInput, stopMessageSkip,
promptText?, defaultValue?, constraints, allowedSources,
openedAtUnixMilliseconds,
timeout?, deadlineUnixMilliseconds?, displayTime,
timeoutMessage?, timeoutAction
```

其中 `timeoutAction` 是封闭枚举，例如 `ReturnDefaultValue`、`ContinueWithoutValue`、`CancelRuntime`；
具体命令映射由能力矩阵固定。`AnyValue` 保留原始文本并由上游规则决定数值/字符串分支；Button 与
键盘提交使用同一输入协调器，但 constraints 可以限制来源和长度。`OneInput` 的字符/数值规则必须
与固定上游一致，不能用通用 trim 代替。

### 9.2 Worker 权威计时

每个计时 prompt 在打开时同时记录：

- `IRuntimeClock` 的单调 deadline：唯一 timeout 裁决依据；
- UTC `openedAt/deadline`：协议展示和诊断基准；
- Snapshot 时的 `serverNowUnixMilliseconds + remainingMilliseconds`：客户端校正显示漂移。

wall clock 回拨/前跳不能提前或延后 Worker timeout。浏览器根据快照采样显示倒计时，不每秒发 event，
也不在本地到零时提交默认值；本地显示到零后进入“等待服务器结果”状态，直到收到 ClosePrompt/后续
transaction。重连只更新剩余显示，不创建新 prompt、不改变单调 deadline。

### 9.3 原子终态与上游反馈

输入、timeout、Runtime cancellation 和 close 在 `InputCoordinator` 同一临界区争夺 prompt：

```text
OPEN -> INPUT_ACCEPTED | TIMED_OUT | CANCELLED | SESSION_STOPPING
```

只有赢家：

1. 清除当前 prompt；
2. 写入恰好一个带原因的 `ClosePrompt` transaction；
3. 完成解释器 waiter；
4. 缓存客户端输入回执（若有）；
5. 使迟到输入得到 `STALE_PROMPT` 或 `SESSION_NOT_ACCEPTING_INPUT`。

对 `TINPUT/TINPUTS/TONEINPUT/TONEINPUTS`，timeout 按上游默认值/无值语义继续解释器，并使
`GlobalStatic.Console.IsTimeOut`/`ISTIMEOUT` 为 true；有效输入使其为 false。`DisplayTime` 为 true
时 prompt UI 显示剩余时间，到期以结构化行替换为 `TimeUpMes`；为 false 但存在 `TimeUpMes` 时按上游
规则追加。`TWAIT`/wait-only 到期继续执行而不伪造用户输入。

新的 prompt 打开时重置与该次输入相关的 timeout 状态；普通输出、浏览器断线或 Snapshot 读取不重置。

## 10. sequence、事务和 Snapshot 一致性

- sequence 在 Worker epoch 内严格递增；Worker 重开由更大 epoch 区分，不尝试跨 Worker 连续；
- 每个 `ConsoleTransaction` 分配一个 sequence，事务内多个操作原子归约；
- 输入接受和 timeout 产生的 ClosePrompt/显示变化必须与 waiter 完成具有可证明顺序；
- `ConsoleStateStore.ApplyTransaction` 在一个锁内完成校验、预算计算、状态替换、sequence 分配和历史发布；
- 失败 transaction 不消费 sequence、不部分修改状态；
- Snapshot 是应用完 `snapshotSequence` 后 scrollback/scene/background/media/prompt/window 的完整副本；
- Snapshot 和 delta round-trip 必须满足：`Reduce(snapshot N, events N+1..M) == Snapshot M`；
- Clear/裁剪不重置 sequence；`long.MaxValue` 继续 fail closed；
- P1-07 保留 Worker 内有界 delta 以做归约测试，但不承诺浏览器历史补发，网络策略仍由 ADR-0017/P1-08 决定。

## 11. IPC 协议升级

### 11.1 版本策略

当前 IPC 为 v2。P1-07 采用新的 major protocol（预期 v3，最终编号由 ADR-0018 冻结），原因是 Prompt、
Console 状态和媒体语义发生结构性扩展。规则：

- 新 `.proto` 使用新 package/C# namespace，旧 tag 保留或 `reserved`，不原位改变旧字段含义；
- API 与 Worker 必须协商同一 major；不允许 v2 Worker 以丢弃新操作的方式进入 RUNNING；
- bootstrap、registration、ready 和 runtime manifest 同时声明 protocol 与 capability set digest；
- mapper 必须双向完整，未知 oneof/枚举 fail closed；未知可选字段按 protobuf 规则保留兼容；
- envelope、transaction、节点、geometry 和 snapshot 分别有硬大小/数量上限；验证在发送前和接收后执行；
- 版本升级同步更新 Worker/API 集成测试、容器启动握手、RuntimeBaseline 和版本端点。

### 11.2 Wire 模型

`DisplayBatch` 改为携带连续 `ConsoleTransaction`；Snapshot 使用显式完整状态消息，不再通过
`Clear + Append + OpenPrompt` 伪装。至少包括：

```text
ConsoleTransaction(sequence, operations[])
ConsoleSnapshot(snapshotSequence, scrollback, scene, backgrounds,
                mediaState, currentPrompt, windowMetadata, truncation)
PromptTiming(deadlineUnixMilliseconds, serverNowUnixMilliseconds,
             remainingMilliseconds)
```

无 timeout 的 prompt 以 `deadlineUnixMilliseconds=0`、`remainingMilliseconds=0` 表示；该状态仍可
通过 heartbeat 上报 opened/server-now 展示时间，但不能伪造正的剩余时间。

输入命令除字符串 value 外增加受限 source/pointer/key payload union；字段必须与当前 prompt 类型
匹配。输入结果不得默认回传输入全文；只有解释器/客户端正确性确需的规范值才返回，并遵守日志脱敏。

## 12. Headless Emuera 接线

### 12.1 Console adapter

把 `UpstreamHeadless/HeadlessEmueraConsole.cs` 从方法级简化 shim 重构为薄 adapter：

- 上游样式、generation、行、临时行和按钮对象先转换为独立 translator 输入；
- translator 只依赖 RuntimeAdapter，不持有 Worker/IPC 类型；
- `Print/PrintC/PrintButton/PrintHtml/PrintImg/deleteLine/Clear*` 等逐项映射能力矩阵；
- Background/Shape/HTML Island/CBG/tooltip/redraw 进入结构化 scene/metadata 操作；
- `GetDisplayLines/PopDisplayingLines/GetLinePointY` 等上游回读由同一结构化状态或专用本地 layout state
  回答，不能返回常量空数组/0 破坏解释器分支；
- Debug/System/OutputLog 等非玩家主 Console 能力单独分类：需要文件/诊断端口的实现端口，不需要的
  HostShim 证明没有游戏可见调用路径。

### 12.2 输入 adapter

`WaitInput(InputRequest)` 必须逐字段转换，不再只读取 `InputType/HasDefValue/Timelimit`。解释器 waiter
完成后按原始 `InputRequest` 类型调用正确的 `Process.Input*` 路径；timeout 不通过异常终止 Runtime，
而按原版继续执行并维护 `IsTimeOut`。

`ReadAnyKey` 区分 EnterKey/AnyKey；PrimitiveMouseKey 通过受限 payload 表达坐标/键/按钮，坐标以
逻辑 viewport 为界。浏览器暂未连接时 prompt 仍保持打开或按 Worker deadline 超时。

### 12.3 媒体和平台 bridge

- `HeadlessAudioBridge` 产生结构化媒体 transaction，不尝试主机播放；
- 图片/字体 bridge 通过受限 asset resolver 和跨平台 metadata/layout port；
- 移除游戏可见路径中的 inert `SystemSound`、text measure 常量返回和 redraw no-op；
- 仍需保留的 WinForms 编译 shim 缩到独立文件并由 Architecture/CapabilityMatrix 测试禁止运行时调用。

修改 `Upstream/` 时按 ADR-0005 在每个文件保留显著说明，更新 `MODIFICATIONS.md`、integration version、
fixture baseline 和第三方验证。

## 13. 兼容诊断和运行时清单

Game validator 和 Runtime 初始化必须消费同一能力矩阵：

- 静态可识别的 Blocked 调用使启用失败，错误含 capabilityId 和源位置；
- 动态才可识别的 Blocked 调用在执行时产生稳定 `RUNTIME_CAPABILITY_BLOCKED` 并 fail closed；
- manifest 保存 capability matrix schema/version/digest、实际使用能力和资源/字体映射；
- Session 固定创建时 manifest；Game 后续能力报告变化不改变既有 Session，但新 Worker 必须确认自身
  runtime 仍支持该 manifest 声明的 capability set；
- 缺失资源、frame、字体 fallback、超限 geometry 和非法 HTML 使用不同稳定 reason code；
- 日志记录 capabilityId、session/worker/epoch 和安全摘要，不记录 HTML 全文、输入全文或物理路径。

## 14. 测试设计

所有测试名称或 trait 必须映射需求/AC，并在 dev Docker 中执行。

### 14.1 RuntimeAdapter 契约测试

- 每个节点、状态和 operation 的正常、边界、未知枚举、NaN/Infinity、超限和非法资源测试；
- transaction 原子性、并发 sequence 精确 `1..N`、失败不消费 sequence；
- Replace/Delete/Clear、scene/background/media 的归约和幂等规则；
- 分项预算、scrollback 整行裁剪、当前 scene/prompt 不被静默裁剪；
- Snapshot 序列化前后等价及 snapshot+deltas 归约性质；
- parser 的嵌套、实体、属性、URL、错误闭合、超深/超长和共享攻击语料；
- prompt 各输入类型、source constraints、OneInput、默认值和 receipt 上限。

### 14.2 计时和并发测试

- 手动单调时钟推进 TINPUT/TINPUTS/TONEINPUT/TONEINPUTS/TWAIT；
- input/timeout/cancel/close 在同一 barrier 竞争，恰好一个终态；
- timeout 返回默认值或 wait-only 继续，不抛成 Runtime failure；
- `ISTIMEOUT` 在 timeout/有效输入/下一 prompt 间正确变化；
- `DisplayTime`/`TimeUpMes` 的 replace/append 差异；
- wall clock 跳变不改变 Worker deadline；Snapshot/断线/重连读取不重置计时；
- timeout 后迟到输入、重复输入、冲突 message ID 返回稳定结果。

### 14.3 真实 Runtime 兼容测试

每个 matrix Supported 项至少被 v18 或 EM+EE fixture 的真实 ERB 路径调用；公共核心能力两者都覆盖：

- PRINT family、样式、对齐、临时行、删除/clear、按钮 generation/tooltip；
- HTML/HTML Island、图片/Sprite、背景、Shape/CBG、动画/redraw；
- 全部输入类型和计时命令，断言 RESULT/RESULTS/ISTIMEOUT 和可见 transcript；
- 字体 shaping/测量、资源 frame/rect、缺失 fallback；
- audio play/stop/loop/volume/channel；
- Blocked 调用的 validator 与 runtime fail-closed 诊断。

测试不能只检查输出中出现某段文字；必须同时检查解释器变量/分支的可见证据、结构化状态和媒体/场景
状态。若合法真实游戏不能提交 CI，继续使用原创最小 fixture，并把授权代表游戏人工验收留给 P1-15。

### 14.4 IPC 与真实 Worker 测试

- 新协议所有消息/节点/枚举/限制双向 round-trip；
- v2/v3 mismatch、capability digest mismatch、未知 oneof/enum 和超限 payload 拒绝；
- Worker 真实运行输出 rich transaction 和显式 Snapshot，sequence 连续；
- prompt timing 经 IPC 保留 immutable deadline，heartbeat 的 prompt 状态一致；
- 输入/timeout 后恰好一个 ClosePrompt，迟到 Worker/output 继续受 epoch fencing；
- 控制通道断开时计时器和 Runtime 一起取消并有界退出，不在孤儿 Worker 中继续。

### 14.5 容量与故障测试

- 高频 PRINT/临时行/动画不会使 Worker 内存或 IPC 队列无界增长；
- 超大 HTML、geometry、动态 bitmap、字体和媒体 channel 达到各自硬上限时确定拒绝；
- asset 被替换、摘要不匹配、资源解码失败不产生半 scene/media 状态；
- transaction 转换、序列化或发送失败时状态与 sequence 不部分提交；
- Runtime 初始化或执行 timeout 仍回收资源、计时 waiter、临时 surface 和静态 bridge 状态。

### 14.6 验证命令

```bash
./scripts/dev-up.sh
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore --configuration Release \
  --filter "Category=ConsoleContract|Category=TimedInput|Category=RichOutput"'
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore --configuration Release \
  --filter "Category=RuntimeBridge|Category=EmueraFeatureMatrix"'
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release \
  --filter "Category=ConsoleProtocol|Category=TimedInput"'
./scripts/verify-runtime-fixtures.sh
./scripts/verify-emuera-capabilities.sh
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
```

对应 trait 和 `verify-emuera-capabilities.sh` 属于本阶段交付物；实现时若真实项目名变化，必须同步修改
为 solution 中可执行的命令。

2026-08-12 dev Docker 验证结果：RuntimeAdapter 149/149、v3 IPC 契约 13/13、Worker 集成
19/19、固定上游 RuntimeCompatibility 27/27；`verify-runtime-fixtures.sh` 报告 schema 1、2 个
fixture、18 个文件，`verify-emuera-capabilities.sh` 报告 19 个能力、177 个唯一入口；完整
`scripts/check.sh` 通过（Release 0 warning/0 error，Web typecheck、13 个测试和 production build
均通过），`verify-dev-user.sh`、`verify-third-party.sh` 与 `git diff --check` 通过。

## 15. 实施切片

### 切片 1：ADR、上游审计和能力矩阵

- 新增 ADR-0018、schema、matrix、兼容报告和 verifier；
- 枚举固定上游 Console/Input/Timer/Media/UI 入口与 headless shim；
- 给每项分配 capabilityId、分类、测试和目标 adapter/IPC 类型；
- CI 先允许显式 `Planned` 过渡状态，仅限当前切片分支；切片完成后 schema 删除 `Planned`，避免长期
  把计划状态当兼容等级。

### 切片 2：核心状态、事务与预算

- 重构 line-based scrollback、scene/background/media/window/prompt Snapshot；
- 实现封闭节点/操作、transaction、稳定 ID、validator 和 size estimator；
- 迁移 ADR-0003 测试，证明原子性、裁剪、sequence 和归约性质；
- 保留临时兼容 mapper 仅供旧测试迁移，不进入生产 API。

### 切片 3：完整输入与计时

- 扩展 Prompt/Input/constraints/result；
- 实现单调 deadline、展示时间采样和 prompt 终态竞争；
- 完成 InputRequest 全字段 translator、默认值、`ISTIMEOUT`、`TimeUpMes`、ReadAnyKey/AnyValue/
  PointerKey；
- 新增手动时钟和真实 ERB 计时矩阵。

### 切片 4：文本、行、HTML、字体和布局

- 接通 style/font/size/alignment、temporary/replace/delete/clear 和上游回读；
- 扩展 HTML/HTML Island 安全 parser；
- 接入跨平台字体 shaping/measurement 和确定 fallback；
- 加入 Unicode、行高、按钮 generation/tooltip 和攻击语料测试。

### 切片 5：图片、Sprite、背景、Shape/CBG 和动画

- 实现 asset resolver、source rect/frame/position/z-index；
- 接通 scene/background/hit region 和有界动态 surface；
- 实现 redraw revision/脏区语义，消除相关 Unsupported/no-op；
- 用真实 fixture 验证绘制状态和输入 hit region。

### 切片 6：结构化音频

- 重构 audio port 为媒体命令和 channel state；
- Worker 移除 `NoOpRuntimeAudioPort`，接入 Snapshot/transaction；
- 覆盖 play/replace/loop/volume/stop/stop-all、资源失败和 channel 上限；
- 更新浏览器交接契约，明确 autoplay acknowledgment 不影响 Runtime。

### 切片 7：IPC 新版本与真实 Worker

- 新增协议 major、显式 Snapshot/transaction/prompt timing/resource/media 消息；
- 完成 mapper、validator、handshake/capability digest 和旧版本拒绝；
- 改造 Worker output pump/heartbeat/input，覆盖真实进程 round-trip 和 fencing；
- 删除生产路径旧协议兼容与 Clear+Append 伪 Snapshot。

### 切片 8：能力门、文档和交接

- 使 matrix 无 Planned/未分类项，Supported 项证据完整；
- 更新 RuntimeBaseline、manifest、compatibility report、ADR-0004/0005、`MODIFICATIONS.md`、
  requirements/design/development plan 和中英文对应内容；
- 运行全量 dev Docker、fixture、capability、dev-user、third-party 和 diff 检查；
- 为 P1-08/P1-09/P1-11 提供 golden Snapshot/transaction/prompt/media fixtures。

切片按顺序合并；切片 4～6 可在切片 2/3 的契约冻结后并行开发，但共享 proto、状态模型和上游文件的
修改必须由单一集成分支协调，避免不同能力自行定义不兼容节点。

## 16. 完成定义

P1-07 只有在以下条件全部满足后才能标记 DONE：

1. ADR-0018 已接受，能力矩阵 schema、分类规则和结构化状态模型冻结；
2. 固定上游所有相关入口均有且仅有一个能力映射，没有未分类、Planned 或未解释 shim；
3. 除批准的宿主/安全 Blocked 类别外，MVP 运行能力全部为 Supported；
4. headless 游戏可见路径不存在无声 no-op、常量占位结果或通用 Unsupported；
5. Console/Input/Scene/Media/Prompt 状态封闭、有界、可验证、可原子归约；
6. 全部 Emuera 输入和计时语义正确，timeout 不被误报为 Runtime failure，`ISTIMEOUT` 和默认值一致；
7. 图片/Sprite/背景/Shape/CBG/HTML Island/字体/动画/音频均有结构化表示和真实解释器证据；
8. IPC 新版本无损 round-trip，旧版本和 capability mismatch fail closed；
9. v18 与当前 EM+EE fixture 生成稳定 transcript，Supported 能力测试覆盖矩阵完整；
10. 正常、边界、主要失败、并发、恶意内容、容量和故障路径均有自动化测试；
11. Runtime manifest、compatibility report、baseline、upstream 修改记录和中英文文档同步；
12. 本文第 14.6 节全部命令通过。

## 17. 后续任务交接

- P1-08 必须把本文完整 Snapshot 当作不可拆分基线，并对 transaction sequence 做缺口检测；不得只
  恢复 scrollback 而遗漏 prompt、scene、background 或 media；
- P1-09 只负责认证、订阅、WebSocket schema 和输入转发；不得重新实现 timeout 或在 API 代替 Worker
  裁决默认值；
- P1-11 必须逐项渲染能力矩阵 Supported 节点，浏览器不支持某项时显示明确错误并阻止宣称会话完全
  可用；不能退回 `innerHTML` 或任意媒体 URL；
- P1-15 用授权代表游戏和四浏览器矩阵补最终视觉/音频证据，但不能替代 P1-07 的真实解释器与协议测试；
- 未来升级上游时，新增/改变的入口必须先更新能力矩阵和 ADR，再更新 integration version；缺少映射
  的上游变化必须使 CI 失败。

## 18. 完成记录

2026-08-12：P1-07 已完成。新增 ADR-0018、结构化 Console/Input/Media RuntimeAdapter、v3
Worker IPC、双向 mapper、能力矩阵与静态验证门；真实 headless fixture 和生产 Worker UDS 链路已
验证。P1-08/P1-09/P1-11 继续消费本阶段冻结的 Snapshot、transaction、prompt timing 和 media
语义。
