# P0-05 持久 SessionRoot 与 Emuera 原生双布局存档实现计划

状态：DONE

计划日期：2026-08-05

完成日期：2026-08-05

对应开发步骤：`P0-05 — Emuera 原生存档双布局`

前置条件：P0-01～P0-04 已完成

后续步骤：P0-06 单 Session Worker 与 IPC 冒烟链路

需求映射：SAVE-001/002、SAVE-004/005、SAVE-010～015、AC-008/009、AC-013/014、ADR-0007

## 1. 任务结论

P0-05 要证明真实 Emuera 可以在一个持久、Session 私有的实际 GameRoot 中完成原生
保存和加载。实现采用 ADR-0007 的简化模型：

- Session 管理方创建 `SessionRoot`；
- 固定 GameVersion 的完整合法普通文件树复制到 SessionRoot，不按目录用途分类；
- 游戏内容、`emuera.config`、临时内容和存档都是 SessionRoot 内的私有普通条目；
- Worker 把 SessionRoot 直接交给解释器；
- Emuera 使用原生 reader/writer 直接读写文件；
- 同一 Session 重启复用原目录；
- 不存在 SaveArtifact、generation、退出复制或保存提交通知。

本步骤完成后，仓库可以声称两套受控 fixture 都通过固定上游解释器完成了原生
`SAVEDATA/LOADDATA` 与 `SAVEGLOBAL/LOADGLOBAL` 往返，并证明根目录和 `sav/` 两种
布局不会写入 GameVersion 或其他 Session。

## 2. 关键语义

### 2.1 谁决定存档布局

游戏版本中的 `emuera.config` 是唯一权威：

- `UseSaveFolder:NO` 或未配置：根目录布局；
- `UseSaveFolder:YES`：`sav/` 布局。

这里的 `UseSaveFolder` 是固定上游的配置属性名；当前上游英文配置文件实际使用的键名是
`Use sav folder`（日文配置键也由同一上游接受）。fixture 和检查器必须与这个实际键名
保持一致，不能把仅被 CloudEmuera 检查器识别、但上游不会加载的自定义键当成有效配置。

`RuntimeSaveLayout` 只能表示宿主从该配置检查得到的结果，不能成为用户选项。Runtime
初始化时必须把实际加载的上游配置与宿主记录值比较；不一致时返回稳定的初始化错误，
不得修改配置、同时查找两个目录或迁移旧文件。

### 2.2 谁写存档

Emuera 决定：

- 原生二进制/文本格式及版本头；
- slot 文件名和 Global 文件名；
- 哪些变量进入普通存档或 Global 存档；
- 文件打开、覆盖、flush、错误和加载兼容语义。

CloudEmuera 只决定 SessionRoot 的所有权、完整复制、配额和 Worker 独占关系。本
task 不包装原生 writer，不解析或重新编码 `.sav`，也不增加原子 generation。

### 2.3 存档的权威位置

```text
/data/sessions/{sessionId}/root/
├── CSV/                                           # 独立副本
├── ERB/                                           # 独立副本
├── resources/                                     # 独立副本、可选
├── sound/、font/                                  # 独立副本、可选
├── any-game-dir/                                  # 未知合法目录也保留
├── emuera.config                                  # 独立副本
├── tmp/                                           # Session 私有
├── save00.sav                                     # Root 模式
├── global.sav                                     # Root 模式
└── sav/
    ├── save00.sav                                 # SavDirectory 模式
    └── global.sav
```

只有配置选中的一套存档位置是有效位置。未选中的位置即使存在文件也不能被 Runtime
自动加载。SessionRoot 本身位于持久挂载中，因此不需要从临时视图“提交”出去。

## 3. 范围

### 3.1 本步骤必须实现

1. 把 P0-04 的可销毁 `UpstreamRuntimeFileView` 执行路径改为实际持久 SessionRoot；
2. 创建或重构 SessionRoot builder，使其从发布 manifest 完整复制普通文件树；
3. 由 `emuera.config` 检查并固定 `RuntimeSaveLayout`；
4. 让 `Program.ExeDir` 指向 SessionRoot，CSV/ERB/resources 等指向其中的内容入口；
5. 让固定上游原生保存代码直接写入 SessionRoot；
6. 扩展两套合成 fixture，真实执行普通存档和 Global 存档保存/加载；
7. 用同一 SessionRoot 的两个顺序 Runtime 实例证明退出后重新加载；
8. 证明不同用户/Session 不共享可写 inode，完整目录、普通存档和 Global 存档互不影响；
9. 更新文件 I/O 审计、上游修改账本和兼容性报告；
10. 保持 P0-04 input-roundtrip 场景及全库检查通过。

### 3.2 明确非目标

- 不建立 `SaveArtifact` 类型、表、目录或 API；
- 不计算每次保存的摘要，不维护 generation 或 current pointer；
- 不在 Worker 退出时扫描、复制或发布存档；
- 不修改 Emuera 的存档字节格式和变量选择规则；
- 不保证 Worker 在原生 writer 覆盖文件的中间被强杀时目标文件仍有效；
- 不实现用户存档的 Web/API 列表、上传、下载、重命名或删除；P1-09 处理停止态文件管理；
- 不实现 mount namespace、cgroup、seccomp 或 Worker 进程启动；这些属于 P0-06 及后续；
- 不为已知目录选择 mount/链接，也不静默丢弃未知目录；
- 不允许源或 SessionRoot 中出现软链接、硬链接异常、FIFO、设备或 socket；
- 不兼容任意第三方真实游戏，只对 manifest 中两个合成 profile 作阻断验收。

## 4. 架构与所有权

```text
Session manager / compatibility harness（上层编排）
    ├── 创建/分配 SessionRoot，绑定 GameVersion 并执行授权检查
    ├── 校验发布 manifest、复制配额和其他已分配 SessionRoot
    ├── 调用 RuntimeAdapter.SessionRootLayoutBuilder
    ├── 持久化 Session 元数据并管理生命周期
    └── 启动 EmueraRuntimeHost(paths)
                         │
                         ▼
RuntimeAdapter.SessionRootLayoutBuilder（受控文件系统物化）
    ├── 完整复制到 staging root
    ├── 复核摘要/配额/类型并拒绝链接和特殊文件
    └── 原子发布 /data/.../sessions/{id}/root
                         │
                         ▼
EmueraRuntimeHost → UpstreamRuntimeSession → 固定上游 Emuera
                                                │
                                                ├── 读取/写入完整游戏副本
                                                └── 直接读写 SessionRoot 原生存档
```

依赖方向保持：

```text
RuntimeAdapter ← EmueraRuntime ← future Worker
```

上层负责 Session 的创建、授权、GameVersion 绑定、配额决策、数据库元数据和生命周期；
`RuntimeAdapter` 的 builder 负责这些决策之后的受控文件系统物化、路径约束和原子发布，
不创建或管理 Session 实体，也不选择版本或执行授权。固定上游配置检查和解释器接线放在
`EmueraRuntime`。不得让 RuntimeAdapter 引用上游类型，也不得让 Worker 为某个 fixture
写特殊路径分支。

## 5. SessionRoot builder 设计

### 5.1 建议 API

在现有 `SessionRootLayoutBuilder` 上做兼容性重构，或增加职责明确的
`SessionRuntimeDirectoryBuilder`。它必须接收经过验证的发布 manifest，而不是自行猜测
哪些顶层目录重要。建议入口：

```text
Build(gameVersionRoot, sessionRoot, publishedManifest, copyLimits, allocatedOtherSessionRoots)
    -> SessionRootLayout(RuntimePaths, SaveLayout, CopiedManifestDigest)
```

调用方不能传入独立的可写 save root。`RootSaveRoot` 和 `SavDirectoryRoot` 若为保持
P0-02 API 兼容继续存在，必须分别等于 `SessionRoot` 和 `SessionRoot/sav`。

### 5.2 构造顺序

1. 规范化并验证 GameVersionRoot、SessionRoot 和其他已分配 SessionRoot；
2. 拒绝 SessionRoot 位于 GameVersionRoot 内、两者重叠或与其他 SessionRoot 重叠；
3. 校验 manifest 与 GameVersionRoot 当前条目集合、类型、大小和摘要完全一致；
4. 检查源中不存在符号链接、硬链接异常、FIFO、设备或 socket；
5. 读取源 `emuera.config` 并得到 `RuntimeSaveLayout`；
6. 在最终目录的同一父目录创建随机 staging root，不直接写最终路径；
7. 按 ordinal 规范相对路径复制 manifest 中的所有目录和普通文件，包括未知内容；
8. 复制时累计文件数和实际字节，任何一项超过预留配额立即失败；
9. 逐文件关闭后校验目标长度和 SHA-256，并复核源未在复制期间变化；
10. 可选 reflink 必须检测真实支持且保持 CoW；不支持或失败时回退普通流式复制；
11. 确认源与目标以及两个 Session 目标之间不共享可写 inode；绝不使用硬链接；
12. 创建仅属于 Session 的 `tmp/`，并在需要时创建 `sav/`；
13. 写入不供 Runtime 修改的绑定 metadata，再将 staging 原子重命名为最终 SessionRoot；
14. 返回确定的 `RuntimePaths` 和无敏感绝对路径的诊断摘要。

任何步骤失败时，不得递归删除一个预先存在的 SessionRoot。仅可清理由本次调用创建、
名称随机且仍能证明位于指定 staging 父目录的条目。最终 SessionRoot 要么完整出现，要么
不存在。

### 5.3 幂等与重启

同一 SessionRoot 再次 Build 时：

- 已有完整游戏副本、私有配置和存档保持不变；
- 不从 GameVersion 补复制缺失条目或覆盖运行后修改的内容；
- 通过绑定 metadata 确认它最初来自同一 GameVersion 和 manifest；
- 绑定另一个版本、metadata 缺失/损坏或根是链接时失败；
- 不根据新传入 GameVersion 偷换已绑定版本；
- 不清理 `save*.sav`、`global.sav`、`sav/` 或游戏创建的合法原生辅助存档文件。

### 5.4 完整复制与未知内容规则

是否进入 SessionRoot 只由发布 manifest 决定：

- manifest 中的合法普通文件和目录全部复制，不要求 CloudEmuera 理解用途；
- manifest 之外的服务端 metadata、上传暂存、扫描报告和版本控制目录不复制；
- 软链接、硬链接异常、FIFO、设备、socket 和其他特殊文件不能进入发布 manifest；
- 复制阶段发现清单外条目或类型变化时失败，不能跳过后继续；
- 不保留宿主所有者、setuid/setgid、ACL 或不受控 xattr；目标使用明确的安全 mode；
- 是否保留 mtime 不参与 Runtime 正确性，摘要和规范相对路径才是内容身份。

完整复制不替代路径沙箱：P0-06 仍需确保 Worker 只能看到自己的 SessionRoot 和必要系统
路径。但它消除了 Runtime 对 GameVersion 源、链接目标以及已知目录分类的依赖。

## 6. 布局检测与一致性

### 6.1 配置检查器

增加一个只负责固定上游配置键的窄检查器，例如 `EmueraSaveLayoutInspector`。它接收
受控配置流或已经校验的 GameVersion 配置路径，不接受任意用户绝对路径。它必须：

- 使用与 fixture manifest 一致的 UTF-8/Shift-JIS 严格解码；
- 按固定上游的键名（英文配置文件为 `Use sav folder`）和布尔值规则识别 `UseSaveFolder`；
  布尔值必须与 `ConfigItem.tryStringToBool` 一致，接受 `YES/TRUE/前`、
  `NO/FALSE/後` 和整数值；
- 未声明时使用上游默认 `NO`；
- 重复且冲突、非法布尔值、解码失败时拒绝；
- 不顺带实现完整 `emuera.config` parser；
- 以契约测试与固定上游 `ConfigData` 的实际结果交叉验证。

如果复用固定上游 `ConfigData` 可以避免重复规则，允许在 `EmueraRuntime` 中提供只读
inspection API，但必须受上游 static gate 保护且不能产生 SessionRoot 写入。

### 6.2 Runtime 二次校验

`UpstreamRuntimeSession` 在 `ConfigData.Instance.LoadConfig()` 并完成上游静态配置赋值
后、执行任何 ERB 前，读取实际 `Config.UseSaveFolder`：

```text
false <-> RuntimeSaveLayout.Root
true  <-> RuntimeSaveLayout.SavDirectory
```

不一致返回例如 `save_layout_mismatch` 的稳定 fatal diagnostic，初始化状态为
`InitializationFailed`。错误消息包含逻辑布局名，不包含宿主绝对路径。

## 7. Runtime 接线改造

### 7.1 移除可销毁执行视图

P0-04 的 `UpstreamRuntimeFileView` 会把内容复制到 `Temporary` 并在 Host dispose 时删除。
它不能承载持久存档。P0-05 应：

- 从正式 Host 初始化路径移除该 view；
- 直接把 `RuntimePaths.SessionRoot` 作为上游 executable root；
- 把复制后的 `SessionRoot/CSV`、`ERB`、resources/sound/font 作为上游读取根；
- 把 `SessionRoot/tmp` 或其受控子目录作为 debug/data 临时根；
- 保留取消、deadline、static RuntimeGate 和迟到初始化清理语义；
- 若测试仍需要 port-only loader 证明，可保留独立测试 helper，但不得作为正式保存路径。

`UpstreamRuntimeSession.ValidatePrivateViewRoots` 应改名并重写为 SessionRoot 校验，要求
所有传入根都是 SessionRoot 内的普通目录且任何父段都不是链接。不能简单删除校验。

### 7.2 上游改动边界

优先只修改 headless glue，不改 `VariableEvaluator.SaveTo`、`SaveGlobal`、`LoadFrom`、
`LoadGlobal` 和 `EraDataReader/Writer`。这些方法必须继续由真实 ERB 指令调用。

允许的固定上游最小改动仅限：

- 暴露 headless 配置完成后的 `UseSaveFolder` 只读值；
- 让 `Program.ConfigureHeadless` 接受实际 SessionRoot 及分离临时目录；
- 禁止 `createSavDirAndMoveFiles` 在 headless 中弹出迁移对话框。新 Session 不需要桌面版
  根存档迁移；若选中 `sav/` 而根级旧存档存在，应产生明确诊断而不是自动移动。

每项 `Upstream/` 修改必须在 `MODIFICATIONS.md` 登记文件、目的、ADR-0007 和覆盖测试，
并保留显著 CloudEmuera 注释。

### 7.3 生命周期

保存/加载场景使用两个完全释放的 Host：

```text
Build SessionRoot
  -> Host A Initialize/Run
  -> ERB 原生 SAVEDATA + SAVEGLOBAL
  -> QUIT + Dispose
  -> 保留 SessionRoot
  -> Host B Initialize/Run（同一路径，新 Console/Runtime static state）
  -> ERB 原生 LOADGLOBAL + LOADDATA
  -> 输出加载值
  -> QUIT + Dispose
```

Host dispose 只能释放内存、static gate 和无状态临时内容，不得删除 SessionRoot、配置或
存档。

## 8. Fixture 与场景设计

### 8.1 资产修改

保持现有 `scenario.json` 和 input-roundtrip transcript 行为不变。每个 fixture 新增
`save-scenario.json`，并在现有 ERB 中加入不会被旧输入触发的保存/加载分支：

- v18 fixture：保留输入 `7` 的旧路径；另用两个保留输入值触发 save 与 load；
- EM+EE fixture：保留输入 `4` 的旧路径；另用两个保留输入值触发 save 与 load；
- save 分支设置一个普通 SAVEDATA 变量和一个 GLOBAL 变量，执行 `SAVEDATA 0, ...`、
  `SAVEGLOBAL`，输出稳定的 save-complete 标记后 QUIT；
- load 分支先把内存值设为不同哨兵，执行 `LOADGLOBAL`、`LOADDATA 0`，在上游要求的
  `EVENTLOAD`/系统回调中打印实际恢复值，然后 QUIT；
- 断言只能来自加载后可见输出或窄只读 probe，不能由 runner 解析 `.sav` 猜测；
- 不提交预制 `.sav`，所有存档必须由测试中的 Host A 生成。

若 SAVEDATA 变量需要新增 CSV/ERH 声明，应使用固定上游真实语法，并更新 manifest 的
文件列表、编码、SHA-256、coverage 和 README。不能为了减少 fixture 修改而由 C# 直接
调用 `VariableEvaluator.SaveTo`。

### 8.2 save-root 场景

只运行 `v18-core`，要求配置文件使用固定上游键 `Use sav folder:NO`
（上游属性为 `Config.UseSaveFolder = false`）：

1. 从已校验 fixture 准备临时已发布 GameVersion，再通过生产 builder 完整复制到全新 SessionRoot；不得直接把 checkout fixture 当作运行目录；
2. Host A 执行保存分支；
3. 断言 `SessionRoot/save00.sav` 与 `SessionRoot/global.sav` 是非空普通文件；
4. 断言 `SessionRoot/sav/save00.sav` 和 `sav/global.sav` 不存在；
5. 完全释放 Host A；
6. Host B 用同一 SessionRoot 执行加载分支；
7. 精确比较加载 transcript 与期望普通变量及 Global 值；
8. 断言 GameVersion 摘要未改变且 Host dispose 后两个存档仍存在。

### 8.3 save-directory 场景

只运行 `em-ee-core`，要求配置文件使用固定上游键 `Use sav folder:YES`
（上游属性为 `Config.UseSaveFolder = true`），同样先准备临时已发布
GameVersion 并完整复制，步骤与上节相同，但期望文件为：

```text
SessionRoot/sav/save00.sav
SessionRoot/sav/global.sav
```

根级 `SessionRoot/save00.sav` 和 `SessionRoot/global.sav` 必须不存在。

### 8.4 CLI

扩展 `scripts/test-runtime-compat.sh` 与 `RuntimeCompatibilityCli`：

```bash
./scripts/test-runtime-compat.sh --scenario save-root
./scripts/test-runtime-compat.sh --scenario save-directory
```

- `save-root` 固定选择 `v18-core`；
- `save-directory` 固定选择 `em-ee-core`；
- 传入不匹配的 `--fixture` 返回用法错误码 2；
- 场景失败返回 1，参数错误返回 2，成功返回 0；
- 每个 run 输出机器可读 JSON，包含 scenario、fixture、layout、runPhase、status、
  assertionCount、upstream commit 和 integration version；
- report 只能包含逻辑相对存档路径，不泄露临时绝对路径；
- 脚本继续清空 DISPLAY/WAYLAND_DISPLAY 并先执行 fixture/third-party 校验。

## 9. 自动化测试计划

### 9.1 RuntimeAdapter：`Category=SessionRoot`

1. `BuilderCopiesEveryManifestEntryIncludingUnknownDirectories`；
2. `BuilderCreatesOnlyRegularFilesAndDirectories`；
3. `BuilderIsIdempotentAndPreservesRuntimeChangesAndNativeSaves`；
4. `BuilderRejectsExistingRootBoundToAnotherVersion`；
5. `BuilderRejectsOverlappingSessionRoots`；
6. `BuilderRejectsSourceLinkHardLinkOrSpecialFile`；
7. `BuilderRejectsManifestMismatchOrSourceMutationDuringCopy`；
8. `BuilderEnforcesFileCountAndTotalByteLimitsDuringCopy`；
9. `BuilderFailureDoesNotPublishPartialRootOrDeleteExistingSessionData`；
10. `TwoSessionCopiesDoNotShareWritableInodes`；
11. `ReflinkFallbackProducesEquivalentIndependentContent`；
12. `RootLayoutResolvesSaveFilesDirectlyUnderSessionRoot`；
13. `SavLayoutResolvesSaveFilesDirectlyUnderSessionRootSav`；
14. `LayoutComesFromUseSaveFolderAndDefaultsToRoot`；
15. `ConflictingOrInvalidUseSaveFolderFailsClosed`。

Linux 测试必须真实创建软链接、硬链接和 FIFO 输入并确认复制失败，不能只比较字符串。
测试清理只删除自己创建且验证过的临时 staging/root。

### 9.2 RuntimeAdapter：`Category=SaveIsolation`

1. 两个 Session 绑定同一 GameVersion 时 root slot 和 Global 物理路径不同，并且
   `stat` 证明源文件、两个 Session 副本不共享 inode；
2. 一个 Session 写入后另一个 Session 对应路径仍不存在；
3. 同一用户的两个 Session、模拟不同用户目录的两个 Session 也不共享存档；
4. root 模式不能通过 `sav/...` 访问存档，sav 模式不能把存档解析到根；
5. `..`、绝对路径、大小写混淆、链接替换和跨 Session 路径被拒绝；
6. GameVersion 内容摘要在所有操作前后相同；
7. Host dispose/cancel/deadline 和模拟 Worker 终止不删除 SessionRoot；
8. 可销毁 Runtime 临时数据不包含权威存档副本；
9. 修改 Session A 中复制来的未知文件不改变 GameVersion 或 Session B；
10. SessionRoot 中每个初始 manifest 条目均存在且摘要与发布源一致。

### 9.3 RuntimeCompatibility：真实解释器

1. `RootLayoutNativeSaveAndRestartLoadRoundTrips`；
2. `SavDirectoryNativeSaveAndRestartLoadRoundTrips`；
3. `SaveLayoutMismatchFailsBeforeErbExecution`；
4. `JapaneseSaveBooleanValuesMatchPinnedUpstream`（`前`/`後` 交叉验证）；
5. `NativeSaveFilesSurviveHostDispose`；
6. `SequentialHostsDoNotLeakLoadedVariablesThroughStatics`；
7. `TwoSessionsKeepNativeGlobalValuesIndependent`；
8. `GameVersionRemainsByteIdenticalAfterNativeSaveAndLoad`；
9. `UnselectedSaveLayoutIsNotReadAsFallback`；
10. `CancellationPreservesPersistentSessionRoot` 与
    `DisposingInitializedHostPreservesRootForSimulatedWorkerTermination`；
11. 原有 RuntimeBridge、deadline、button、visible-output 和 input-roundtrip 测试全部回归。

### 9.4 架构与负面证明

增加静态或行为测试证明：

- solution 中没有 `SaveArtifact` 生产类型、`save_artifacts` schema 或 `save.committed` IPC；
- Runtime 结果不携带存档内容；
- fixture runner 没有自行构造 `.sav` 字节或直接调用上游保存方法；
- 正式 Host 不再把存档放入会在 dispose 时删除的 `UpstreamRuntimeFileView`；
- SessionRoot 中没有软链接、硬链接共享或特殊文件；
- builder 不含 CSV/ERB/resources 白名单式复制分支，所有 manifest 普通条目走同一规则。

## 10. 建议文件变更

实现者应以实际代码为准，但预计涉及：

```text
src/CloudEmuera.RuntimeAdapter/Paths/RuntimePaths.cs
src/CloudEmuera.RuntimeAdapter/Paths/SessionRootLayout.cs
src/CloudEmuera.RuntimeAdapter/Paths/SessionRootLayoutBuilder.cs
src/CloudEmuera.RuntimeAdapter/Paths/RuntimeSaveLayout.cs
src/CloudEmuera.EmueraRuntime/Headless/EmueraRuntimeHost.cs
src/CloudEmuera.EmueraRuntime/Headless/UpstreamRuntimeFileView.cs   # 移出正式路径或删除
src/CloudEmuera.EmueraRuntime/UpstreamHeadless/UpstreamRuntimeSession.cs
src/CloudEmuera.EmueraRuntime/UpstreamHeadless/HeadlessPlatformStubs.cs
src/CloudEmuera.EmueraRuntime/Upstream/.../Config.cs                # 仅在必要时最小 hook
src/CloudEmuera.EmueraRuntime/MODIFICATIONS.md
tests/CloudEmuera.RuntimeAdapter.Tests/Paths/*SessionRoot*Tests.cs
tests/CloudEmuera.RuntimeAdapter.Tests/Paths/*SaveIsolation*Tests.cs
tests/CloudEmuera.RuntimeCompatibility.Tests/*Save*Tests.cs
tests/CloudEmuera.RuntimeCompatibility.Tests/RuntimeCompatibilityCli.cs
tests/CloudEmuera.RuntimeCompatibility.Tests/RuntimeScenarioRunner.cs
tests/fixtures/runtime/*/ERB/START.ERB
tests/fixtures/runtime/*/save-scenario.json
tests/fixtures/runtime/manifest.json
tests/fixtures/runtime/README.md
scripts/test-runtime-compat.sh
docs/runtime-system-io-audit.zh-CN.md
docs/adr/0005-vendored-emuera-source.md
docs/development-plan.zh-CN.md
```

删除或重命名公共类型前先用 `rg` 检查调用点。P0-02 已完成测试若依赖分离
`writable/root-saves` 或只读 GameContent 映射，应更新为 ADR-0007 的完整 SessionRoot
副本语义，不保留两套平行模型。

## 11. 实施顺序

### 阶段 A：先固定失败测试

1. 为完整复制、SessionRoot 直写路径和配置驱动布局添加 RuntimeAdapter 测试；
2. 添加 save-root/save-directory CLI 参数与预期失败骨架；
3. 添加“Host dispose 后存档仍存在”和“布局不匹配失败”回归；
4. 确认测试因当前临时 view/未执行保存而失败，不因测试自身路径错误失败。

### 阶段 B：重构目录模型

1. 让 builder 通过 staging 完整复制并原子发布实际持久 SessionRoot；
2. 收敛 RuntimePaths 的 save/config/tmp 根；
3. 增加布局 inspection 与 Runtime 二次校验；
4. 更新现有路径、完整复制、特殊文件拒绝和文件系统测试。

### 阶段 C：Runtime 直接接线

1. 从正式路径移除 disposable file view；
2. 更新 headless Program 路径注入；
3. 保持 deadline、取消和 static gate 清理；
4. 确认 INPUT-only P0-04 场景仍通过。

### 阶段 D：真实保存/加载场景

1. 扩展两个 fixture 的原生保存与加载 ERB 分支；
2. 更新 manifest、哈希和 fixture validator；
3. 实现两阶段 Host runner 与 JSON report；
4. 加入 Session/Global 隔离和 GameVersion 摘要断言。

### 阶段 E：文档与收尾

1. 更新 `MODIFICATIONS.md` 和上游 I/O 审计；
2. 若 integration 行为变化，提升 `CloudEmueraIntegrationVersion`，不要修改 upstream commit；
3. 执行专项和全局质量门；
4. 只有全部通过后把 P0-05 标为 DONE、P0-06 标为 NEXT，并记录实际计数与环境。

## 12. 验证命令

所有 .NET 构建和测试通过 dev Docker 执行。主验收：

从宿主机执行下列脚本时，脚本会通过 `scripts/lib/dev-env.sh` 自动调用 UID/GID 映射的
dev Docker；若已在 dev 容器内，则直接执行内部命令。

```bash
./scripts/test-runtime-compat.sh --scenario save-root
./scripts/test-runtime-compat.sh --scenario save-directory
```

专项测试：

```bash
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore \
  --configuration Release --filter 'Category=SessionRoot|Category=SaveIsolation'

docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore \
  --configuration Release --filter 'Category=RuntimeBridge|Category=NativeSave'
```

回归与质量门：

```bash
./scripts/test-runtime-compat.sh --scenario input-roundtrip
./scripts/verify-runtime-fixtures.sh
./scripts/verify-third-party.sh
./scripts/check.sh
git diff --check
```

若修改开发容器挂载或身份，再执行：

```bash
./scripts/verify-dev-user.sh
```

## 13. 完成定义

只有同时满足以下条件，P0-05 才能标记为 DONE：

- v18 根目录模式和 EM+EE `sav/` 模式都由真实配置自动识别；
- 两套 fixture 都由真实 ERB 指令调用原生 writer，随后由新 Host 调用原生 reader 恢复；
- 普通存档变量和 Global 变量都通过加载后的可见输出证明；
- 文件只出现在配置选中的当前 SessionRoot 位置；
- 同 GameVersion 的跨用户、跨 Session 和同用户双 Session 均无共享可写文件；
- GameVersion 在运行前后字节摘要一致；
- SessionRoot 在 QUIT、dispose、cancel 和 Worker 模拟崩溃后不被 Host 清理；
- manifest 中所有合法普通条目完整复制，未知目录保留，链接/特殊文件/摘要变化 fail closed；
- 两个 Session 与 GameVersion 之间不共享可写 inode；
- P0-04 input-roundtrip、deadline、输入和结构化 Console 回归全部通过；
- 生产设计与测试中没有 SaveArtifact、generation、退出复制或提交事件；
- 所有上游改动已登记，固定来源和许可证校验通过；
- 全局质量门返回 0，文档记录实际命令、环境和测试数量。

## 14. 交接给 P0-06

P0-05 向 P0-06 交付一个只需要 `SessionRoot + runtime options` 就能运行的 Host。P0-06
负责跨进程启动和 IPC，不再传 GameVersionRoot、独立 save root 或 artifact root 给
Worker。启动消息应只包含受控 SessionRoot 标识/路径、绑定版本、runtime manifest 和
epoch；Supervisor 在启动前完成绑定版本、manifest、权限与目录身份复核。

P0-06 不得重新加入存档提交通知。Worker 对 SessionRoot 的独占写权限随租约和进程
生命周期管理；未来 P1-09 文件管理操作必须先确认没有活动 Worker。

## 15. 实际完成记录

实现按本方案完成。Session 管理方/兼容性 harness 负责创建并分配 SessionRoot、绑定
GameVersion、提供已校验 manifest、复制配额和其他已分配 SessionRoot；
`RuntimeAdapter.SessionRootLayoutBuilder` 仅负责在这些前置条件下安全完整复制、摘要复核、
配额控制和原子发布，不负责 Session 生命周期、数据库元数据或授权决策。

固定上游实际识别的配置文件键为 `Use sav folder`，运行时二次校验使用上游
`Config.UseSaveFolder`。v18 根目录和 EM+EE `sav/` fixture 均通过真实上游原生
`SAVEDATA/LOADDATA`、`SAVEGLOBAL/LOADGLOBAL` 完成跨 Host 保存/加载；integration version
更新为 `headless-p0.5.1`，upstream commit 未变。

2026-08-05 在 Linux 开发容器中完成以下验证：

- `./scripts/test-runtime-compat.sh --scenario save-root`：20 项断言通过；
- `./scripts/test-runtime-compat.sh --scenario save-directory`：20 项断言通过；
- RuntimeAdapter `SessionRoot|SaveIsolation` 专项 26 项、程序集全量 137 项通过；
- RuntimeCompatibility `NativeSave|RuntimeBridge` 专项 23 项、程序集全量 26 项通过；
- Domain 4 项通过，完整 .NET 测试合计 167 项通过；
- `前`/`後` 日文配置均通过固定上游实际解析交叉验证；同一 GameVersion 的双 Session
  原生 Global 值、root/sav 布局、Unix inode 独立和取消/模拟终止保留测试通过；
- `./scripts/check.sh`、fixture/third-party 校验、Web typecheck/test/build 和
  `git diff --check` 通过。
