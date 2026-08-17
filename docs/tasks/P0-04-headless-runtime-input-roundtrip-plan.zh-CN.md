# P0-04 无 UI Runtime 运行到 INPUT：详细设计与实现计划

状态：DONE（2026-08-05）

术语迁移：本文中“运行一个 GameVersion”按 ADR-0010 解释为“运行一个已物化的 Game 内容树”；
Host 单实例限制不建立 GameVersion 产品实体。

范围说明：本文是历史 Runtime 验证记录；其中关于未来 Worker namespace/资源隔离的表述已由
[`ADR-0017`](../adr/0017-trusted-self-hosted-mvp-simplification.md) 取代。
编写日期：2026-08-04
对应开发步骤：`P0-04 — 无 UI Runtime 运行到 INPUT`
前置条件：P0-00、P0-01、P0-02、P0-03 已完成
后续步骤：P0-05 Emuera 原生存档双布局

实施结果：`CloudEmuera.EmueraRuntime.UpstreamHeadless` 直接编译固定上游
loader/parser/Process/变量和指令源码，正式 Host 已删除 fixture-only AST executor。
两套 fixture 各 18 项场景断言通过，RuntimeBridge 专项 16 项、
RuntimeCompatibility 全量 19 项通过；纯计算无限循环 deadline、初始化 deadline
后的静态 gate/view 回收、四种按钮调用及伪变量回归均有真实 ERB 测试。文件调用点及 P0-05 延后边界见
[`runtime-system-io-audit.zh-CN.md`](../runtime-system-io-audit.zh-CN.md)。

## 1. 任务结论

P0-04 已把固定版本的真实 Emuera.EM+EE 解释器接到 RuntimeAdapter，并在 Linux 无 UI 环境完成两套 fixture 的 INPUT 往返。

P0-03 已经固定结构化 Console、prompt、输入竞争与有界快照语义；P0-04 在此基础上形成了一个进程内、无网络、无数据库、无浏览器的最小 runtime host，并证明：

1. 固定上游 commit 的真实 CSV/ERB loader 和执行器确实被调用；
2. v18 与 EM+EE fixture 均能加载、输出、停在真实 `INPUT`；
3. 输入通过 P0-03 的 `promptId` 契约返回解释器，随后执行到 `QUIT`；
4. 结构化输出归一化后的可见文本与 P0-01 基线完全一致；
5. 整个流程在没有 WinForms、显示服务器、桌面事件循环、系统音频设备和 API 进程的 Linux 容器中完成。

本步骤完成后，仓库才可以声称“能够用解释器运行这两套受控 ERB 到输入并继续执行”。它仍不代表任意真实游戏已兼容，也不包含原生存档的保存/加载闭环。

## 2. 目标与验收摘要

主验收命令：

```bash
./scripts/test-runtime-compat.sh --scenario input-roundtrip
```

该命令必须：

- 直接构建 `src/CloudEmuera.EmueraRuntime` 中固定 upstream commit 的内置源码；
- 构建 Linux `net10.0` headless runtime，不构建或加载 WinForms 桌面入口；
- 依次运行 `v18-core` 和 `em-ee-core`；
- 等待每套游戏产生真实 prompt，提交 `scenario.json` 中的整数输入；
- 等待解释器正常执行到 fixture 的 `QUIT`；
- 比较实际可见文本与 `expected-transcript.txt`，并校验结构化 HTML、图片节点、prompt 类型和关键计算结果；
- 任一来源校验、构建、加载、输入、transcript、诊断或退出检查失败时返回非零退出码。

全局质量门保持：

```bash
./scripts/check.sh
./scripts/verify-runtime-fixtures.sh
./scripts/verify-third-party.sh
git diff --check
```

## 3. 需求映射与范围

### 3.1 本步骤直接完成的切片

| 需求 | P0-04 的证明 |
| --- | --- |
| COMP-001 | 构建和报告均绑定固定 upstream commit 与非空 CloudEmuera integration version |
| COMP-002/003 | v18 与当前 EM+EE 合成资产由真实解释器加载和执行 |
| COMP-004 | fixture 中 PRINT、INPUT、变量、分支、函数、HTML 和图片命令有可重复结果 |
| COMP-006 | harness 输出机器可读的 supported/unsupported/failed 诊断，不静默吞掉能力 |
| SESS-003/004 | runtime 等待输入时不依赖浏览器连接；取消可终止等待和解释器 |
| PLAY-001 | 解释器可见输出进入 P0-03 `IGameConsole`，而不是桌面控件 |
| PLAY-007/008 | INPUT 使用 Console 分配的 promptId；stale/重复输入沿用 P0-03 结果 |
| AC-008/009 | 两个受控 profile 完成加载、输入和主要显示场景中的 P0-04 部分 |
| NFR-011 | 真实 runtime 兼容测试独立于浏览器 E2E、API 和数据库 |

### 3.2 本步骤只建立边界、不宣称完成

- AC-008/009 中的原生保存/加载由 P0-05 完成；P0-04 只验证 fixture 的存档语义标记，不创建伪存档文件。
- SESS-003 的跨 API 重启和真正断线重连由 P0-06/Phase 1 完成；这里仅证明解释器等待输入不要求 UI/API 存活。
- 图片只需完成 fixture 的静态资源解析和结构化 `ImageNode` 输出；Sprite 全量行为、像素渲染、CBG、字体测量和动画不在本步骤支持范围。
- 音频命令必须走端口并明确返回 `Unsupported`，但 fixture 不要求真实播放。
- `ConsoleSnapshot` 仍是进程内状态；IPC 版本、worker epoch 和网络恢复屏障后续实现。

### 3.3 明确非目标

- 不实现 Supervisor、Worker IPC、WebSocket、API、数据库或浏览器 renderer；
- 不实现 Session 进程隔离、namespace/cgroup/seccomp；P0-04 只要求无 UI 的进程内 runtime host；
- 不实现 `save*.sav`、`global.sav`、`sav/` 在持久 SessionRoot 中的原生写入或重载；
- 不通过解析 ERB 文本、逐行模拟指令或直接回放 expected transcript 冒充解释器执行；
- 不把 `System.IO` 绝对路径、WinForms/GDI+ 对象、原始 HTML 或上游内部可变对象暴露到 RuntimeAdapter 公共 API；
- 不修改 P0-01 fixture 来绕过解释器问题，除非能够证明 fixture 语法与固定上游不一致，并同步更新 manifest 哈希、README 和原因记录；
- 不要求兼容未列入 fixture 的鼠标、剪贴板、调试窗口、插件、CALLSHARP、网络或任意 DLL 能力。

## 4. 已接受的架构决策

ADR-0005 已决定把固定 Emuera.EM+EE 源码作为普通 Git 文件直接纳入 `src/CloudEmuera.EmueraRuntime/Upstream`。P0-04 必须遵循该 ADR：不恢复 submodule、patch queue 或构建时 staging；直接在内置源码上实现 headless 接线，同时保留来源、许可证和修改记录。

实现开始时应补充 ADR-0005 的能力表，或新增独立 runtime integration ADR，记录：

- headless runtime、RuntimeAdapter 和未来 Worker 的依赖方向；
- Console/Input/File/Clock/Image/Audio 六类平台调用的替换策略；
- 无 UI 构建不得引用 WinForms、WPF、COM/WMP、NAudio 或桌面主程序；
- fixture 图片采用 metadata/逻辑资源节点，不进行服务端像素绘制；
- Supported/Experimental/Unsupported 能力表；
- 上游升级冲突、integration version 和回退办法。

如果实际接线表明需要改变 P0-02/P0-03 的公共契约，应先写明固定上游调用点、失败用例和兼容影响，再做最小扩展；不得在内置 runtime 中另建一套平行 Console 或文件接口。

## 5. 内置源码组织与修改规则

固定来源锚点为：

```text
repository: https://gitlab.com/EvilMask/emuera.em.git
commit:     2175f8a629257efb08214e093704b3a3d3d06d05
tree:       a3c96867e3a5b5d5f90877a4e7c6f8056d5f5b9b
path:       src/CloudEmuera.EmueraRuntime/Upstream
```

`UPSTREAM.md` 保存来源和许可证校验值，`MODIFICATIONS.md` 保存 CloudEmuera 修改账本。P0-04 对上游文件的每项修改必须：

1. 保留原版权与许可证声明；
2. 在文件中显著标记 CloudEmuera 修改；
3. 在 `MODIFICATIONS.md` 记录文件/范围、目的、需求或 ADR 和测试；
4. 通过普通 Git diff 审查，不生成等价 patch 文件；
5. 不改变 `UPSTREAM.md` 中的原始 commit/tree，除非执行经过评审的上游升级。

首次导入和每次上游升级应为独立提交，CloudEmuera headless 修改随后按逻辑拆分提交。这样既能直接编辑，又能通过 Git 对比导入边界。`scripts/verify-third-party.sh` 校验来源元数据、许可证和无嵌套 Git 元数据，但不会把合法源码修改误报为“脏 submodule”。

### 5.1 integration version 与运行时清单

`RuntimeBaseline.CloudEmueraIntegrationVersion` 当前为 `source-v1`。P0-04 形成首个可运行 headless integration 时将其提升为不可变版本，例如 `headless-p0.4.1`。测试应验证：

- integration version 不为空、不为 `latest`，并与 fixture/report 一致；
- report 中的 upstream commit 与 `RuntimeBaseline`、`UPSTREAM.md` 一致；
- 构建实际包含仓库内 runtime 项目，而不是下载或临时复制的另一份源码；
- 修改账本覆盖 P0-04 改动的内置上游文件。

P0-04 可先生成 harness report，不提前实现 Phase 1 的持久化 `runtime-manifest.json`。

## 6. 建议项目结构与依赖方向

```text
src/CloudEmuera.EmueraRuntime/
├── CloudEmuera.EmueraRuntime.csproj
├── Upstream/                         # 已导入的普通源码文件
├── Headless/
│   ├── EmueraRuntimeHost.cs
│   ├── EmueraRuntimeOptions.cs
│   ├── EmueraRuntimeResult.cs
│   ├── EmueraRuntimeDiagnostic.cs
│   └── EmueraPlatformBridge.cs
├── UPSTREAM.md
└── MODIFICATIONS.md

tests/CloudEmuera.RuntimeCompatibility.Tests/
├── CloudEmuera.RuntimeCompatibility.Tests.csproj
├── HeadlessRuntimeFixtureTests.cs
├── RuntimeSourceContractTests.cs
├── RuntimeScenarioLoader.cs
├── RuntimeTranscriptProjector.cs
└── RuntimeCompatibilityCli.cs

scripts/
└── test-runtime-compat.sh
```

`CloudEmuera.EmueraRuntime.csproj` 目标为 Linux `net10.0`，显式选择 headless 所需源码并排除桌面入口/实现；它单向引用 `CloudEmuera.RuntimeAdapter`。项目和兼容测试都纳入正常 solution/locked restore，不依赖生成目录、动态 ProjectReference 或预构建二进制。

不要把 `EmueraRuntimeHost` 放进 `CloudEmuera.RuntimeAdapter`：真实 runtime 消费 RuntimeAdapter ports，反向引用会形成项目循环。未来 P0-06 Worker 直接引用 `CloudEmuera.EmueraRuntime`。

```text
fixture CLI/tests 或 Worker
        ↓
CloudEmuera.EmueraRuntime（EmueraRuntimeHost + 内置源码）
        ↓
CloudEmuera.RuntimeAdapter（Console/File/Clock/Image/Audio ports）
```

不得出现 `RuntimeAdapter → EmueraRuntime/Worker/Application/API`。如果需要更多原语，应先在 RuntimeAdapter 增加最小平台契约，再由 EmueraRuntime 单向消费；不要让上游 domain 类型进入 RuntimeAdapter 公共契约，也不要复制 P0-03 状态机。

## 7. Headless runtime host 契约

### 7.1 启动选项

`EmueraRuntimeOptions` 至少包含：

- 已构造完成的 `RuntimePaths`；
- `IGameConsole`；
- `IRuntimeFileSystem`；
- `IRuntimeClock`；
- `IRuntimeImagePort`；
- `IRuntimeAudioPort`；
- compatibility profile（只允许 manifest 声明值）；
- 初始化/运行总 deadline；
- 可选的诊断 sink。

它不能接受任意 host `workingDirectory`、未校验绝对文件名、`Form`、`Control`、`SynchronizationContext` 或服务定位器。必需依赖必须在构造时完整提供；不能在解释器深处回退到全局 `File.*`、`DateTime.Now` 或桌面静态对象。

### 7.2 生命周期

建议 API：

```text
Create(options) → InitializeAsync → RunAsync → Completed/Cancelled/Failed
```

必须满足：

- 一个 host 实例只运行一个 GameVersion，不能第二次初始化；
- `InitializeAsync` 完成 CSV、ERB、配置和受支持资源加载；
- `RunAsync` 在专用 runtime task/thread 上运行同步解释器；
- 到 INPUT 时由 `IGameConsole.Read` 阻塞 runtime 线程，输入可从另一线程提交；
- `QUIT` 转换为正常 `Completed`，不能调用 `Environment.Exit`；
- cancellation 同时解除 input wait、停止后续执行并在有限时间内返回；
- 初始化或运行错误成为结构化 diagnostic/result，不弹窗、不挂死、不只写 stdout；
- dispose/reset 清理上游静态状态，使同一测试进程可以顺序运行两个 fixture，且第二个不继承第一个的变量、label、配置、图片或 prompt。

由于上游存在 `GlobalStatic` 和其他 static cache，必须增加跨 fixture 污染测试。若无法可靠 reset，则兼容 CLI 应每个 fixture 启动独立子进程并以机器可读 report 汇总；不能依赖测试顺序掩盖污染。

### 7.3 结果与诊断

`EmueraRuntimeResult` 至少区分：

- `Completed`；
- `Cancelled`；
- `InitializationFailed`；
- `ScriptFailed`；
- `UnsupportedCapability`；
- `DeadlineExceeded`。

诊断包含稳定 code、阶段、fixture 相对路径/ERB 位置（若上游提供）、安全消息和是否致命。不得记录输入全文、宿主绝对路径、堆栈中的敏感环境变量或任意文件内容。未知/未支持桌面能力必须 fail closed，并产生例如 `unsupported_runtime_capability` 的稳定 code。

## 8. 上游接线要求

### 8.1 启动与配置

从桌面 `Program.Main` 中抽取可复用初始化路径，不得调用：

- `Application.*`、`MainWindow`、`Screen`、`MessageBox` 或 `Dialog`；
- `ProfileOptimization` 的进程全局副作用；
- 多开窗口检查；
- WMP/COM、系统托盘、剪贴板、鼠标/键盘状态；
- 基于 executable 位置推导 GameRoot 的逻辑。

Encoding provider 和 invariant culture 应在 host 初始化中显式、幂等地设置。CSV、ERB、resources 和 config 必须来自 `RuntimePaths`/file port。配置错误通过 diagnostic 返回，不能弹出“是否继续”对话框。

### 8.2 文件访问

对 fixture 执行路径上的所有上游 `File`、`Directory`、`FileStream`、`StreamReader` 调用做调用点清单，并映射到 `IRuntimeFileSystem`。至少覆盖：

- config 读取；
- `GAMEBASE.CSV` 和其他 CSV 的存在性检查、枚举及读取；
- ERB/ERH 的确定性枚举和读取；
- resources CSV/PNG 的存在性、枚举和读取；
- parser/runtime 日志禁用或映射到受控 temp 路径。

P0-04 路径上不允许直接接受 host absolute path。文件枚举顺序必须在 adapter 边界按规范相对路径使用 ordinal 排序，避免不同文件系统产生不同解析结果。

新增一个“封锁默认宿主文件系统”的测试 port：除测试声明路径外任何访问立即失败并记录 logical path。这样可以证明成功不是因为遗漏的 `System.IO` 恰好读取了 checkout 文件。

### 8.3 Console 输出映射

上游常用输出映射到 P0-03 类型：

| 上游语义 | RuntimeAdapter 操作 |
| --- | --- |
| 普通 PRINT/PRINTFORM | `AppendNodesOperation(TextNode...)` |
| PRINTL/换行 | 文本节点后接 `LineBreakNode` 或等价原子批次 |
| 字体粗体/斜体/颜色 | 封闭的 `ConsoleTextStyle`，不传 `System.Drawing.Font/Color` |
| HTML_PRINT | 固定上游 `HtmlManager.ParseFragment` 经 headless translator 将 Emuera 伪 HTML 显示语义转换为结构化文本、按钮、图片、形状、对齐和 nowrap 节点；禁用 raw HTML passthrough |
| 数值/字符串按钮 | `ButtonNode`，保留输入值和 tooltip 的安全语义 |
| PRINT_IMG fixture Sprite | 经 image port 验证后的 `ImageNode(ConsoleAssetId, ...)` |
| CLEARLINE/清屏 | P0-03 已有清除操作；若语义不足，先补 reducer 契约测试 |
| 系统警告/错误 | 结构化 diagnostic，并按是否对玩家可见决定 Console 文本 |

不要逐次 Emit 单个字符。上游 print buffer 应在其原子刷新边界生成批次，以保持顺序且避免事件爆炸。不得为了 transcript 对比而丢弃结构化节点；文本投影只存在于测试 projector。

### 8.4 INPUT 映射

fixture 的 `INPUT` 必须映射为 integer `ConsolePrompt`：

- prompt 模板不得自带 promptId，由 `StructuredGameConsole` 分配；
- runtime 只接受返回值中匹配当前 prompt 的已验证整数；
- `RESULT` 使用固定上游的真实赋值路径，不由 harness 直接修改变量；
- invalid/stale/duplicate 输入不能唤醒解释器；
- cancellation/deadline 通过 P0-02 clock/cancellation 边界退出；
- 本步骤禁止通过 stdin、`Console.ReadLine()`、WinForms TextBox 或按键模拟喂入值。

至少另加一个 `INPUTS` 的适配层单元测试，即使 P0-01 fixture 暂未走到该指令，以防 bridge 把所有输入错误地强制解析成整数；不要求为此扩大 fixture transcript。

### 8.5 图片与音频

fixture 中 Sprite CSV 与 2x2 PNG 必须真实由上游资源流程发现，但 headless 构建不能实例化 `Bitmap`/`Graphics`。应在内置源码中把“资源索引/矩形/名称”与“桌面像素对象”分开：

- 使用 file port 和 image port 校验资源存在、media type、尺寸与长度；
- Sprite 名称解析为受限逻辑 `ConsoleAssetId`；
- `PRINT_IMG` 发出结构化图片节点；
- 不支持的裁剪、动态 graphics、CBG 等产生明确 capability diagnostic；
- 无音频 fixture 时仍以单元测试证明音频调用只到 `IRuntimeAudioPort`，`Unsupported` 不触碰设备。

不得简单删除 `PRINT_IMG`，否则 AC-008/009 的主要显示场景没有被验证。

### 8.6 时间与退出

上游 timeout、wait/sleep 和 elapsed 逻辑必须使用 `IRuntimeClock` 的单调时间。fixture 没有定时 INPUT，也要增加 bridge 单元测试证明 timeout 不依赖 wall clock。`QUIT`、初始化失败和 cancellation 均不得杀死测试进程。

## 9. fixture scenario runner 与 transcript

### 9.1 只消费已有资产契约

`RuntimeScenarioLoader` 读取 P0-01 manifest 后，必须先复用 fixture validator，再只运行 manifest 中声明的 profile。不能接受 scenario 内任意绝对路径或未在 manifest 中列出的资源。

`input-roundtrip` 对每个 profile 执行：

1. 构造独占临时 SessionRoot；
2. 以只读方式映射 fixture GameContent，以私有方式提供 config/temp/save area；
3. 初始化真实 runtime；
4. 等待 scenario 中 INPUT 之前的可见文本及一个 OpenPrompt；
5. 断言 prompt 类型为 integer、sequence 连续；
6. 使用实际 promptId 和唯一 clientMessageId 提交 scenario 值；
7. 等待 `QUIT`/Completed；
8. 对最终结构化状态生成可见文本投影；
9. 与 `expected-transcript.txt` 做精确行比较；
10. 校验关键节点、诊断、变量结果和文件副作用；
11. 清理临时目录。

### 9.2 transcript 投影规则

建立唯一的 `RuntimeTranscriptProjector`，规则必须文档化并测试：

- `TextNode` 追加规范文本；`LineBreakNode` 结束一行；
- `<b>/<i>` 样式不产生额外字符，但须另外断言 style 节点存在；
- `ImageNode` 不凭空生成文件名或 alt text；图片存在另行断言；
- prompt 本身不自动写一行，只有 ERB 的显式输出进入文本；
- 统一内部换行为 `\n`，仅移除文件末尾一个约定换行；
- 不 trim 行、不折叠空格、不忽略额外警告行、不使用 contains 代替整体比较。

若上游初始化报告会输出非 fixture 文本，应将其作为 diagnostic channel，而不是在比较时用宽泛正则过滤。任何允许忽略的非玩家事件必须枚举稳定 code。

### 9.3 变量与执行真实性

`expectVariable score=3` 必须证明变量由 ERB 真实执行得到。优先通过 runtime 提供只读、测试可用的窄 probe 按上游变量标识读取终值；若局部变量生命周期使退出后不可读，则利用 fixture 已有的 `V18-SCORE=3`/`EMEE-SCORE=3` 输出进行交叉证明，并在 report 中把该 assertion 标记为 `verifiedByVisibleOutput`。不得由 harness 写入 score，也不得只扫描 ERB 源码推断结果。

### 9.4 文件副作用

本场景必须断言：

- 除受控 config/temp/log（若启用）外没有写入；
- `save00.sav`、`global.sav` 和 `sav/*` 在 P0-04 不应因语义标记自动出现；
- GameContent 在运行前后摘要一致；
- 所有临时内容位于本次 scenario 的私有目录。

## 10. 自动化测试计划

所有 runtime 兼容测试标记 `Category=RuntimeCompatibility`；纯 bridge 单元测试可再标记 `Category=RuntimeBridge`。测试不得访问网络、显示服务器、真实音频设备或依赖任意 sleep 猜测时序。

### 10.1 来源与构建契约

1. `UPSTREAM.md` 的 repository、commit、tree、许可证路径和哈希完整且与 RuntimeBaseline 一致；
2. 仓库不存在 `.gitmodules`、gitlink、`patches/emuera` 或内置源码下的嵌套 `.git`；
3. 缺来源记录、修改账本或许可证被篡改时 `verify-third-party.sh` 失败；
4. `CloudEmuera.EmueraRuntime` 作为正常 solution project 在 clean checkout 直接构建，不下载/生成第二份源码；
5. headless project 目标是 `net10.0`，输出不引用 `System.Windows.Forms`、WPF、WMPLib、NAudio；
6. headless runtime 可在没有 `DISPLAY`/`WAYLAND_DISPLAY` 的 Linux 容器加载；
7. compatibility report 的 upstream commit 与 integration version 精确匹配。

### 10.2 host 生命周期

1. 一次初始化与运行成功，重复初始化/运行被稳定拒绝；
2. 缺 CSV、ERB 或 config 返回 InitializationFailed 及稳定 code；
3. ERB 语法错误返回 ScriptFailed，不弹窗、不挂死；
4. INPUT 等待期间取消能在确定 deadline 内返回 Cancelled；
5. 初始化 deadline 和运行 deadline 分别覆盖；
6. QUIT 返回 Completed，不退出测试进程；
7. 顺序运行两个 fixture 无变量、配置、资源和 prompt 污染；反序再跑结果相同；
8. dispose 后迟到输入被拒绝。

### 10.3 Console/Input bridge

1. PRINT、PRINTL、PRINTFORM 的文本和换行映射准确且批次有界；
2. `<b>`/`<i>` 产生允许样式，原始 HTML 不泄漏；
3. 数字 INPUT 产生 integer prompt，scenario 输入经真实 `RESULT` 分支；
4. INPUTS 保留字符串，不经整数解析；
5. stale、重复、冲突、invalid 输入使用 P0-03 既有语义且不二次恢复 runtime；
6. prompt sequence 前后严格连续；
7. CLEAR/样式 reset 等 fixture 路径上的状态变化正确；
8. 大量相邻 PRINT 不退化成无界逐字符事件。

### 10.4 File/Clock/Media bridge

1. CSV、ERB、resource 枚举是 ordinal 确定顺序；
2. Shift-JIS v18 和 UTF-8/BOM EM+EE 按 manifest/上游规则正确读取，不依赖系统 locale；
3. 所有 fixture 文件访问经 file port，未声明访问被封锁 fake 捕获；
4. GameContent 写入、`..`、绝对路径和链接逃逸仍被拒绝；
5. 图片 metadata 和 Sprite 名称正确，PRINT_IMG 产生 ImageNode；
6. 缺图片/错误尺寸/未知 Sprite 产生稳定诊断而非 GDI+ 异常；
7. 音频 Unsupported 不访问设备；
8. timeout 使用 manual monotonic clock，不读 UTC 判断 elapsed。

### 10.5 两套真实 fixture 端到端

对 `v18-core`：

- 启动顺序包含 `V18-START`、`V18-READY`、`V18-INPUT`；
- prompt 是 integer，输入 `7`；
- 走 `V18-BRANCH-HIGH`，score 为 3；
- `<b>` 映射为 bold 节点；
- `V18_SPRITE` 映射为有效图片节点；
- 完整 transcript 与基线逐行相同，结果 Completed。

对 `em-ee-core`：

- 启动包含 `EMEE-START`、`EMEE-INPUT`；
- prompt 是 integer，输入 `4`；
- 固定上游真实执行 `EXISTFUNCTION("REPORT")` 并输出 probe；
- score 为 3，`<i>` 映射为 italic 节点；
- `EMEE_SPRITE` 映射为有效图片节点；
- 完整 transcript 与基线逐行相同，结果 Completed。

两套均额外断言：无 fatal/unknown diagnostic、没有存档副作用、GameContent 未改变、事件 sequence 连续、最终无 open prompt。

### 10.6 负向 CLI 测试

1. 未知 `--scenario` 返回非零并打印支持值；
2. fixture ID 不存在或未列入 manifest 时返回非零；
3. transcript 任意一行缺失、多出、换序或空格变化时返回非零并给出有限 diff；
4. prompt 类型或输入值与 scenario 不匹配时返回非零；
5. runtime 超时/挂起时主动取消，返回非零且清理子进程/临时目录；
6. 测试进程环境显式移除 `DISPLAY`、`WAYLAND_DISPLAY` 后仍通过；
7. 可选 `--fixture <id>` 只允许 manifest 中的 ID，默认仍必须运行全部 profile。

## 11. `test-runtime-compat.sh` 行为

脚本采用仓库现有 bash 风格：`set -euo pipefail`、从脚本位置解析 repo root、不依赖调用者 cwd。建议参数：

```text
--scenario input-roundtrip   必填；P0-04 唯一支持场景
--fixture <fixture-id>       可选，本地诊断单一 fixture
--no-restore                 可选，已完成 locked restore 时使用
```

脚本顺序：fixture contract → vendored source provenance → locked restore → Release build → compatibility CLI。默认输出每个 fixture 的阶段、耗时、结果、runtime commit/integration version 和断言数；不能输出用户输入全文或 host 私有绝对路径。

从宿主机执行时，脚本通过 `scripts/lib/dev-env.sh` 自动在 UID/GID 映射的 dev Docker
中执行上述 .NET 命令；在 dev 容器内执行时直接复用当前容器，避免依赖宿主机 SDK。

所有子进程必须有有界超时和终止清理，失败时保留最近有限诊断，而不是无限 dump。

## 12. 具体文件变更清单

### 12.1 必须新增或修改

- 遵循 ADR-0005、`UPSTREAM.md` 和 `MODIFICATIONS.md`，登记所有内置源码修改；
- 新增 `CloudEmuera.EmueraRuntime.csproj`、Headless host 与 platform bridge；
- 新增 `test-runtime-compat.sh`；
- 必要时最小扩展 RuntimeAdapter 平台契约，并由 EmueraRuntime 单向消费；
- 新增独立 RuntimeCompatibility 测试/CLI 项目及 lock file；
- 将测试项目纳入合适的 solution/build 流程，或在文档中明确由兼容脚本单独构建；不得形成无人执行的测试项目；
- 更新 `RuntimeBaseline.CloudEmueraIntegrationVersion`；
- 必要时最小扩展 P0-02/P0-03 契约及对应回归测试；
- 验收完成后将开发计划 P0-04 标为 DONE、P0-05 标为 NEXT，并记录真实测试数、平台、commit、integration version。

### 12.2 原则上不修改

- `UPSTREAM.md` 中的原始 commit/tree（除非另立上游升级任务）；
- P0-01 fixture payload、scenario、expected transcript 和 manifest；
- API、Application、Infrastructure、Supervisor、IPC、Web；
- P0-05 的持久 SessionRoot 和原生存档直写逻辑；
- P0-03 已决定的 prompt ID 所有权、HTML allowlist 和 snapshot 上限。

## 13. 推荐实现顺序

1. 阅读 ADR-0005，建立固定上游调用点清单，并为本任务初始化 `MODIFICATIONS.md` 条目；
2. 先实现来源/许可证 contract 失败测试，保证内置源码边界可校验；
3. 新建最小 `CloudEmuera.EmueraRuntime.csproj`，仅做到 Linux 编译和 assembly reference 禁止项通过；
4. 抽取不依赖桌面 `Program.Main` 的初始化与退出入口；
5. 接 RuntimePaths/file port，先完成 CSV/ERB/config 的 parse-only smoke test；
6. 接结构化 Console 的文本、换行和诊断，让 fixture 执行到 INPUT 前；
7. 接 integer/string input bridge，使 runtime 在另一线程提交输入后继续；
8. 接 HTML style、Sprite/image metadata 和音频 Unsupported；
9. 实现 host lifecycle、cancellation、deadline、static reset/进程隔离策略；
10. 实现 scenario loader、transcript projector 和 machine-readable report；
11. 完成 v18 fixture，再完成 EM+EE fixture；不得为第二套复制一条独立 runner；
12. 加入负向、反序、重复运行、无 DISPLAY 和文件封锁测试；
13. 运行专项、全量及全局质量门；
14. 只有全部验收通过后更新开发计划状态。

每一步都应保持 Git diff 小而可审查，并同步修改账本。不要先大规模改名/搬移整个上游目录；先让编译失败清单和 fixture 实际调用路径决定最小切口。

## 14. 验证命令

实现 session 至少运行：

```bash
./scripts/test-runtime-compat.sh --scenario input-roundtrip
dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --filter 'Category=RuntimeBridge'
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests
./scripts/check.sh
./scripts/verify-runtime-fixtures.sh
./scripts/verify-third-party.sh
git diff --check
```

兼容测试项目必须是普通 solution project；第二条命令和 `test-runtime-compat.sh` 均应直接引用同一内置 runtime project，不能依赖 staging MSBuild 属性。

最终验证记录必须写明：

- Linux 发行环境及 .NET SDK 版本；
- `DISPLAY`/`WAYLAND_DISPLAY` 均为空；
- upstream commit、原始 tree 和 integration version；
- 两个 fixture 各自的断言数与结果；
- RuntimeCompatibility、RuntimeAdapter、Domain 和全局检查的实际测试数；
- Release build warning/error 数；
- fixture 与只读 GameContent 在运行前后未改变。

## 15. 完成定义

只有同时满足以下条件，P0-04 才能标记为 DONE：

- ADR-0005、来源/修改记录与实际依赖方向和能力表一致；
- 内置源码来源 commit/tree、许可证和 integration version 可自动校验；
- 仓库无 submodule、gitlink、patch queue 或构建时 staging；
- headless assembly 不引用或加载 WinForms、WPF、WMP/COM、NAudio，不要求显示/音频设备；
- 真实 Emuera CSV/ERB loader 和 interpreter 执行两套 fixture，而不是源文本模拟或 transcript 回放；
- 两套 fixture 均启动、输出、产生真实 INPUT prompt、接受 scenario 输入并正常执行到 QUIT；
- v18 的分支/score/bold/Sprite 与 EM+EE 的 EXISTFUNCTION/score/italic/Sprite 均得到独立断言；
- 完整可见 transcript 与两份基线精确一致；
- Console 操作、promptId、输入、sequence 和快照继续服从 P0-03 契约；
- fixture 执行路径的文件、时钟、图片和音频调用经过 P0-02 端口，无绝对路径回退；
- cancellation、deadline、错误脚本、缺文件、stale 输入和无 DISPLAY 负向测试通过；
- GameContent 未被修改，P0-04 未伪造存档副作用；
- 兼容专项、RuntimeAdapter 回归和全局质量门全部通过；
- 开发计划更新为 P0-04 DONE、P0-05 NEXT，并记录可复核验证数据。

## 16. 交给 P0-05 的明确接口

P0-04 应向 P0-05 交付一个可配置 `RuntimePaths`、可运行到 `QUIT` 的 `EmueraRuntimeHost`。P0-05 只在此基础上把可销毁兼容视图替换为持久 SessionRoot，扩展原生 save/global 流程和两种配置驱动布局；不能重新引入桌面路径、另写解释器 runner 或增加 SaveArtifact 提交层。

P0-04 的 file bridge 需要保留 P0-05 所需的 create/write/move/replace 能力，但本步骤不提前宣称原生存档兼容。兼容 report 中 `expectSavePath` 应明确标为 `deferredToP0-05` 或 `semanticMarkerObserved`，绝不能标为 `passed`。

## 17. 主要风险与防偏提示

- **只让上游“能编译”**：验收对象是 loader + interpreter 的真实执行与输入往返，不是空壳 assembly。
- **丢失上游来源边界**：直接修改不等于重新授权；必须保留许可证、文件内变更说明、独立导入历史和 `MODIFICATIONS.md`。
- **保留隐藏桌面依赖**：即使 Linux 没走到某个分支，headless assembly 的引用和 fixture 执行路径也必须检查；不能靠“不触发”掩盖 WinForms/COM。
- **把整个 EmueraConsole 原样搬过来**：应以 fixture 调用路径为最小切口，并把语义映射到 P0-03，不复制窗口状态机。
- **用 transcript 回放伪装解释器**：必须证明固定上游 parser/loader/interpreter 类型参与执行，并由 ERB 输入改变 RESULT、分支和 score。
- **图片静默删除**：fixture 的 PRINT_IMG 必须产生受控 ImageNode；不支持的图形能力产生明确 diagnostic。
- **绕过 file port**：成功测试应使用封锁型 file fake/adapter 捕获任何未声明访问，不能只做源码 grep。
- **静态状态串场**：两个 fixture 正序、反序和重复运行都要一致；必要时每 fixture 独立进程。
- **无限等待**：初始化、INPUT 和结束均有 deadline；CLI 失败必须回收 runtime 子进程和临时 SessionRoot。
- **提前完成 P0-05**：本步骤只观察存档语义标记，不创建或加载原生存档。
- **扩大兼容声明**：通过两套合成 fixture 只证明其声明的能力；report 和文档不得写成“所有 Emuera 游戏均可运行”。
