# P0-02 平台端口与 RuntimePaths 详细设计及实现计划

状态：Implemented（2026-08-04）

术语迁移：本文记录了 P0-02 的历史实现，文中的 `GameVersionRoot/GameVersion` 是 ADR-0010 前的
复制源名称。目标代码应在 P1-04 重命名为 `GameContentRoot/Game content source`；完整复制、禁止
链接、路径隔离和 SessionRoot 语义不变，不能据旧名称重新引入版本实体。

范围说明：本文是历史平台端口记录；其中关于未来 Worker 身份、mount namespace、降权和沙箱的表述
已由 [`ADR-0017`](../adr/0017-trusted-self-hosted-mvp-simplification.md) 取代，不构成当前 MVP 完成门。

计划日期：2026-08-04

对应开发步骤：`P0-02 — 平台端口与 RuntimePaths`

需求映射：`SAVE-002`、`SAVE-011`～`SAVE-014`、`SEC-002`～`SEC-005`、Phase 0 运行时切分要求

前置任务：`P0-01 — 兼容测试资产与运行时基线（DONE）`

## 1. 为什么下一步是 P0-02

`docs/development-plan.zh-CN.md` 已将 P0-01 标为 `DONE`，并将 P0-02 标为唯一的 `NEXT`。当前仓库已经有两套受控 Runtime fixture 和固定上游提交，但 `CloudEmuera.RuntimeAdapter` 仍只有 `RuntimeBaseline` 常量，Worker 也还没有承载解释器。

不能直接跳到 P0-04：固定上游仍通过 `Program.ExeDir`、`Config.SavDir`、静态 `File`/`Directory` API、`Stopwatch`、WinForms、GDI+ 和系统音频访问平台。若先做 headless harness，再补路径和平台边界，运行时会把进程工作目录当作全局 GameRoot，并可能读取或写入其他 Session 的文件。

因此本 task 先固定两个基础契约：

1. Runtime 只能通过可注入端口访问 Console、文件、单调时钟、图像和音频能力；
2. 所有游戏提供的路径先被归类到当前 Session 的受控区域，再经过词法、规范路径和符号链接检查，才能映射为宿主绝对路径。

P0-02 完成后仍不能宣称解释器已能运行 ERB。它只为 P0-03 的 Console 契约和 P0-04 的上游接线提供稳定边界。

## 2. 目标与完成定义

完成后，`CloudEmuera.RuntimeAdapter` 应具备：

- 不依赖 WinForms、GDI+、WPF、系统音频实现或上游 UI 程序集的平台端口；
- 表达 GameVersion 复制源、当前 Session 的完整 GameRoot、配置、临时目录及两种存档布局的不可变 `RuntimePaths`；
- 只接受规范相对路径的路径值对象；
- 将逻辑路径解析到当前 Session 私有区域的集中式 resolver；
- 对绝对路径、`.`/`..`、NUL、平台分隔符混用、符号链接/重解析点逃逸和跨 Session 物理根进行拒绝；
- 一个构造并验证 SessionRoot 目录计划的 builder；
- 基于 `TimeProvider` 的生产时钟和可确定推进的测试时钟；
- 架构测试，保证核心项目不会重新引入桌面 UI 依赖。

本步骤完成时必须可以执行：

```bash
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=RuntimePaths|Category=Architecture'
./scripts/check.sh
./scripts/verify-runtime-fixtures.sh
./scripts/verify-third-party.sh
git diff --check
```

## 3. 范围边界

### 3.1 本步骤包含

- 在 RuntimeAdapter 中定义平台无关的端口及最小数据契约；
- 实现路径值对象、路径区域、存档布局和不可变 `RuntimePaths`；
- 实现 SessionRoot 布局计划/构造器；
- 实现受 Session 范围约束的本地文件系统适配器；
- 实现符号链接和重解析点检查；
- 实现系统时钟适配器以及只用于测试的手动时钟；
- 添加正常映射、错误路径、链接逃逸、跨 Session、权限与架构测试；
- 在全部验收通过后，将开发计划中的 P0-02 标为 `DONE`，P0-03 标为 `NEXT`。

### 3.2 本步骤不包含

- 不修改固定上游基线源码；依据 ADR-0005，源码后来迁入 `src/CloudEmuera.EmueraRuntime/Upstream`，真实调用点接线属于 P0-04；
- 不启动解释器，不运行 ERB，不比较真实 transcript；
- 不定义 P0-03 的最终 Console event、sequence、promptId、HTML allowlist 或 Snapshot；
- 不实现原生存档序列化或导入/导出；P0-05 让 Emuera 在持久 SessionRoot 中直接保存和加载；
- 不在 RuntimeAdapter 中执行 Linux mount namespace、bind mount、降权、cgroup 或 seccomp；builder 只生成并验证布局，实际挂载由未来 Supervisor 平台适配器执行；
- P0-05 把完整合法 GameVersion 文件树复制到 SessionRoot；复制过程不允许任何符号链接或特殊文件；
- 不把路径字符串校验描述成完整沙箱。TOCTOU 防护、Worker 身份和 mount namespace 仍是纵深防御的必需部分。

## 4. 上游事实与兼容约束

实现者开始编码前应核对固定提交 `2175f8a629257efb08214e093704b3a3d3d06d05`，并在测试/注释中记录以下依据：

- `Emuera/Program.cs` 的 `SetDirPaths` 从单个 ExeDir 派生 `csv/`、`erb/`、`debug/`、`dat/`、`resources/`、`sound/` 和 `font/`；
- `Emuera/Runtime/Config/Config.cs` 根据 `UseSaveFolder` 将 `SavDir` 指向 GameRoot 或 GameRoot 下的 `sav/`；
- 原生变量代码把 `global.sav` 和 `save*.sav` 写到 `Config.SavDir`；
- 固定上游大量直接调用 `File`、`Directory`、`Stopwatch`、`System.Drawing`、WinForms 和系统音频。

这些事实意味着 P0-02 的接口不能只提供一个新的工作目录字符串；它必须把“引擎可见逻辑路径”“当前 Session 的物理私有路径”和“访问能力”分开表示。另一方面，本步骤也不应提前大规模改写上游代码，内置源码的真实调用点迁移留给 P0-04。

## 5. 核心设计

### 5.1 项目与命名空间

生产代码放在现有 `src/CloudEmuera.RuntimeAdapter`，建议布局：

```text
src/CloudEmuera.RuntimeAdapter/
├── Ports/
│   ├── IGameConsole.cs
│   ├── IRuntimeFileSystem.cs
│   ├── IRuntimeClock.cs
│   ├── IRuntimeImagePort.cs
│   └── IRuntimeAudioPort.cs
├── Paths/
│   ├── RuntimeRelativePath.cs
│   ├── RuntimeFileArea.cs
│   ├── RuntimeFilePath.cs
│   ├── RuntimeSaveLayout.cs
│   ├── RuntimePaths.cs
│   ├── SessionRootLayout.cs
│   ├── SessionRootLayoutBuilder.cs
│   └── RuntimePathException.cs
├── FileSystem/
│   ├── LocalRuntimeFileSystem.cs
│   └── PhysicalPathGuard.cs
├── Time/
│   └── TimeProviderRuntimeClock.cs
└── RuntimeBaseline.cs
```

测试继续使用 `tests/CloudEmuera.RuntimeAdapter.Tests`，建议新增 `Paths/`、`Architecture/` 和 `Time/` 子目录。测试专用 fake 放在测试项目，不进入生产程序集。

RuntimeAdapter 不新增对 Application、Infrastructure、Worker 或上游 Emuera 项目的引用。`Application.Abstractions.IClock` 服务数据库/应用 UTC 时间；本 task 新增的 `IRuntimeClock` 服务解释器的单调计时和可取消等待，两者不要合并，也不要让 RuntimeAdapter 反向依赖 Application。

### 5.2 路径值对象

`RuntimeRelativePath` 是所有游戏提供路径的唯一入口。建议作为只读 record struct 或等价不可变类型，并通过 `Parse`/`TryParse` 创建；禁止公开可绕过验证的构造器。

规范格式必须满足：

- 使用 `/` 作为内部唯一分隔符；
- 非空、非绝对路径、非 URI，不允许 drive prefix、UNC、设备路径或以分隔符开头；
- 不允许空段、`.`、`..`、NUL、反斜杠、控制字符；
- 不允许尾随 `/`，目录由调用语义而非尾随分隔符表达；
- 不允许任何段在 trim 后改变含义；至少拒绝 Windows 会特殊处理的尾随空格/点和保留设备名；
- 限制总长度、段数和单段长度，常量集中定义；测试中固定边界值；
- 比较使用 ordinal。不得按当前 culture 比较或在 Linux 上假设文件名大小写规则；
- 转换到平台路径时逐段传给 `Path.Combine`，不能直接拼接未经检查的原始字符串。

不要调用 `Path.GetFullPath(root, userValue)` 后就认为安全；值对象的词法拒绝必须先发生。

`RuntimeFilePath` 由 `RuntimeFileArea + RuntimeRelativePath` 构成。建议区域：

```csharp
public enum RuntimeFileArea
{
    GameContent,  // CSV、ERB、resources、sound、font 等，只读
    Configuration,
    Save,
    Temporary
}
```

调用者不能以宿主绝对路径调用 `IRuntimeFileSystem`。诊断可包含规范逻辑路径，但默认不暴露物理 `/data/...` 路径。

### 5.3 RuntimePaths

`RuntimePaths` 是构造后不可变的 Session 级对象。构造时接收已经由宿主信任边界提供的绝对根，并立即规范化；至少表达：

- `SessionRoot`：解释器的实际 GameRoot；
- `GameVersionRoot`：不可变发布内容的物理源；
- `SessionWorkspaceRoot`：当前 Session 私有物理区；
- `CsvRoot`、`ErbRoot`、`ResourceRoot`、可选 `SoundRoot`/`FontRoot`；
- `ConfigurationRoot`；
- `TemporaryRoot`；
- `RootSaveRoot`：兼容属性，简化模型中解析为持久 `SessionRoot`；
- `SavDirectoryRoot`：兼容属性，简化模型中解析为持久 `SessionRoot/sav/`；
- `SaveLayout`：`Root` 或 `SavDirectory`。

构造不变量：

- 所有根都是规范绝对路径；相对根直接失败；
- `SessionRoot`、`SessionWorkspaceRoot` 不能是同一路径，也不能位于 GameVersionRoot 内；
- 所有可写根必须严格位于当前 `SessionWorkspaceRoot` 内；
- Runtime 实际内容根必须位于当前 `SessionRoot` 内；`GameVersionRoot` 只作为首次完整复制的不可变来源；
- 两个 Session 的 `SessionWorkspaceRoot`/可写根不得相同或互为祖先；跨 Session 检查由 builder 接收已分配根时完成；
- 路径包含关系按平台明确的比较器计算，并用带终止分隔符的规范路径判断，不能用裸 `StartsWith`；
- 所有 Runtime 根及其已存在祖先不能是符号链接/重解析点；SessionRoot 复制过程不存在链接例外；
- 对外不提供“随意组合绝对路径”的 helper。

保存映射必须有单一入口，例如 `ResolveSavePath(RuntimeRelativePath logicalPath)`：

- `Root` 布局把原生根级 `save*.sav`、`global.sav` 及固定上游确认需要的同类存档文件解析到实际 `SessionRoot`；根级布局不接受包含目录段的路径；
- `SavDirectory` 布局将相对于引擎 `sav/` 的路径解析到实际 `SessionRoot/sav/`；
- 两种布局映射同名 `global.sav` 时得到不同 Session 的物理路径；
- 宿主显式把 `SessionRoot` 注入为 `Program.ExeDir`，不依赖进程当前目录；
- P0-02 只验证路径映射，不声称 Emuera 已经通过该映射保存。

保存文件名的允许集合必须以固定上游真实调用为依据。不要仅允许两位数字 slot，也不要无边界接受任意扩展名。若上游允许文本/图像存档辅助文件，应明确列入规则和测试。

### 5.4 SessionRootLayout 与 builder

`SessionRootLayout` 是数据对象，不直接执行 mount。建议包含：

- 解释器可见目标（`root/CSV`、`root/ERB`、`root/resources`、`root/sav`、`root/emuera.config`）；
- 对应物理源；
- `ReadOnly`/`ReadWrite` 访问模式；
- 当前存档布局；
- 创建后得到的 `RuntimePaths`；
- 稳定、无宿主秘密的诊断描述。

`SessionRootLayoutBuilder` 负责：

1. 验证 GameVersionRoot 已存在，且需要的 CSV/ERB 目录与配置源符合预期；
2. 验证 SessionRoot 和 workspace 属于调用者传入的当前 Session 分配，不接受从游戏内容读出的根路径；
3. 创建持久 `root/`、`root/sav/` 和 `root/tmp/`；
4. 按发布 manifest 把全部合法普通文件和目录复制到 `root/`，包括配置和未知游戏目录；绝不让 Runtime 写发布版本中的原件；
5. 验证复制后的逐项类型、大小、摘要及总配额，并确认没有与源或其他 Session 共享可写 inode；
6. 在创建后重新执行物理路径/链接检查；
7. 幂等地返回同一布局；若已有 SessionRoot 的绑定身份或 manifest 不匹配则失败，不覆盖、不删除其中的运行内容。

本步骤不应在测试里要求 root 权限或真实 bind mount。P0-05 将 builder 收敛为 staging 后完整复制并原子发布 SessionRoot；未来 Supervisor 用 mount namespace 隐藏原始 GameVersion 和未分配 Session 路径。

### 5.5 文件系统端口与本地适配器

`IRuntimeFileSystem` 应只接受 `RuntimeFilePath` 或更窄的专用路径类型。接口至少覆盖 P0-04 已知会用到的能力：

- 文件存在和目录存在；
- 打开只读流；
- 以明确模式打开可写流（create new、truncate、append 等，不用含糊布尔参数）；
- 创建目录；
- 以确定顺序枚举一个受控区域；
- 获取有限元数据（长度、最后修改时间、entry kind）；
- 移动/替换/删除仅限可写区域；
- 所有阻塞调用接受取消信号或清楚记录同步解释器边界。

权限规则：

- `GameContent` 永远只读；任何 create/write/move/delete 立即抛出稳定的 `RuntimeFileAccessException`；
- `Configuration` 只能在 Session 私有配置区写；
- `Save` 根据 `SaveLayout` 映射到唯一私有 save root；
- `Temporary` 只能映射到当前 Session 的临时根；
- 跨区域 move/replace 默认拒绝，只有以后明确的原子保存流程可以引入窄接口；
- API 不接受任意 glob；若需要枚举，pattern 也必须是受控值对象或由 adapter 内部产生。

`LocalRuntimeFileSystem` 在每次实际 I/O 前调用 `PhysicalPathGuard`，不能只在 `RuntimePaths` 构造时检查一次。

### 5.6 物理路径和符号链接防护

`PhysicalPathGuard` 至少执行：

1. 从受信根逐段组合规范相对路径；
2. 计算候选完整路径并验证仍位于允许根；
3. 检查允许根到候选路径之间每个已存在节点的 `LinkTarget`/`ReparsePoint`；
4. 读取时要求最终节点存在并不是拒绝类型；
5. 创建新文件时检查最近的已存在父目录；创建/打开后再次检查；
6. Linux CI 中实际创建指向根外、另一 Session 和根内的符号链接，全部按本 task 的保守策略拒绝；不能因链接最终仍在根内就允许；
7. 错误只报告逻辑路径和原因码，不泄漏其他 Session 的规范绝对路径。

纯托管的“检查后再按路径打开”仍有 TOCTOU 窗口。本 task 必须在 XML 文档和设计注释中明确限制：它是应用层防误用与纵深检查，不替代未来 Worker 的独立身份、只读 mount、mount namespace 和基于目录句柄/no-follow 的强化实现。

### 5.7 时钟端口

新建 `IRuntimeClock`，同时提供 UTC 时间、单调时间戳和可取消等待，建议语义：

```csharp
public interface IRuntimeClock
{
    DateTimeOffset UtcNow { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
```

生产实现包装注入的 `TimeProvider`，默认使用 `TimeProvider.System`。禁止以 `UtcNow` 相减驱动输入超时；超时必须使用单调 timestamp。测试实现 `ManualRuntimeClock` 由测试显式 `Advance`，推进时间时按截止顺序完成等待，支持取消，不调用 `Thread.Sleep` 或真实 wall clock。

至少用一个小型 `RuntimeTimeout`/`RuntimeDeadline` helper 证明调用者可以在 fake clock 上确定性等待和超时。不要在 P0-02 实现完整输入协调器。

### 5.8 Console、图像和音频端口

本 task 只固定依赖方向和能力边界，不提前完成 P0-03 的业务模型。

`IGameConsole` 建议采用单向操作与请求/响应两个入口，而不是暴露 WinForms 控件：

```csharp
public interface IGameConsole
{
    void Emit(GameConsoleOperation operation);
    GameConsoleInput Read(GameConsolePrompt prompt, CancellationToken cancellationToken);
}
```

P0-02 可将 `GameConsoleOperation`、`GameConsolePrompt` 和 `GameConsoleInput` 定义为最小抽象基类/封闭 envelope，使接口可编译但不承诺最终事件字段。P0-03 必须在不引入 UI 类型的前提下定义其具体、可序列化语义。不要在本步骤加入 `Control`、`Form`、`Font`、`Color`、`Bitmap` 或 `Graphics`。

`IRuntimeImagePort` 接受受控的 `RuntimeFilePath` 和平台无关请求，返回平台无关元数据/句柄，例如宽高、媒体类型和稳定资源 ID。像素缓冲如确有需要使用 `ReadOnlyMemory<byte>` 及显式像素格式，不能使用 `System.Drawing.Image`。

`IRuntimeAudioPort` 接受 play/stop/volume/loop 等平台无关命令及逻辑资源路径，不调用 `System.Media`、WMP 或 NAudio。P0-02 可以提供 no-op 适配器供无音频测试，但必须可观察调用，不能把“不支持”静默伪装成已播放。

图像和音频端口的本 task 测试重点是程序集签名不泄漏桌面类型、路径必须经过 file port、fake 可记录调用；真实浏览器媒体事件和 allowlist 属于 P0-03/ADR-004。

## 6. 异常和诊断契约

为路径/文件错误定义少量稳定 reason code，例如：

- `invalid_relative_path`；
- `path_outside_area`；
- `read_only_area`；
- `symbolic_link_rejected`；
- `cross_session_path`；
- `layout_conflict`；
- `entry_not_found`；
- `unsupported_runtime_file`。

生产异常应携带规范逻辑路径、区域和 reason code；物理路径只允许进入受控 debug 日志，并必须避免包含另一 Session 信息。测试断言 reason code，不依赖操作系统本地化异常消息。

参数错误、权限拒绝和 I/O 故障要区分；不要把 `IOException` 全部转换成“路径不安全”，也不要吞掉底层异常后继续运行。

## 7. 具体代码改动

### 7.1 RuntimeAdapter 项目

修改 `src/CloudEmuera.RuntimeAdapter/CloudEmuera.RuntimeAdapter.csproj` 仅在确有必要时添加设置。优先使用 BCL，不为简单路径处理新增 NuGet 包。保持 `net10.0`、nullable、warnings-as-errors 和 locked restore。

新增第 5 节所列生产文件。公共类型必须有简洁 XML 文档，尤其说明：

- 输入是逻辑路径还是受信宿主根；
- 读写权限；
- 符号链接和 TOCTOU 限制；
- 时钟是 wall clock 还是 monotonic；
- P0-02 的端口不代表上游已经接线。

### 7.2 RuntimeAdapter 测试项目

在现有测试项目新增：

```text
tests/CloudEmuera.RuntimeAdapter.Tests/
├── Paths/
│   ├── RuntimeRelativePathTests.cs
│   ├── RuntimePathsTests.cs
│   ├── SessionRootLayoutBuilderTests.cs
│   ├── PhysicalPathGuardTests.cs
│   └── LocalRuntimeFileSystemTests.cs
├── Time/
│   ├── ManualRuntimeClock.cs
│   └── RuntimeClockTests.cs
├── Ports/
│   └── RuntimePortContractTests.cs
└── Architecture/
    └── RuntimeArchitectureTests.cs
```

复用现有 xUnit 项目，不建立第二个同名测试程序集。临时目录由每个测试独占，测试结束清理；不得修改 `tests/fixtures/runtime`。

### 7.3 开发计划

只有全部验收命令成功后才修改 `docs/development-plan.zh-CN.md`：

- P0-02：`NEXT → DONE`；
- P0-03：`TODO → NEXT`；
- 在 P0-02 通过条件后记录实际测试数量、平台和验证日期；
- 若符号链接用例只在 Linux 执行，明确记录其他平台覆盖方式。

不要在实现开始时预先推进状态。

## 8. 测试设计

所有新增测试使用下列 trait 之一：

```csharp
[Trait("Category", "RuntimePaths")]
[Trait("Category", "Architecture")]
```

时钟与端口行为测试归入 `RuntimePaths`，使开发计划已有 filter 能完整覆盖本 task。

### 8.1 RuntimeRelativePath

至少覆盖：

1. `CSV/GAMEBASE.CSV`、`ERB/START.ERB`、`sav/save00.sav` 等合法路径得到稳定 `/` 形式；
2. 空字符串、空白、`.`、`..`、`a/../b`、`a//b`、尾随 `/` 被拒绝；
3. `/etc/passwd`、`C:/...`、`C:\\...`、UNC、设备路径、`file:` URI 被拒绝；
4. 反斜杠、NUL、控制字符、尾随空格/点和保留设备名被拒绝；
5. 长度、段数、单段长度的边界值前一位成功、超限失败；
6. Turkish locale 等非默认 culture 下结果不变；
7. 大小写不同不会因 current culture 被合并。

### 8.2 RuntimePaths 与两种存档布局

至少覆盖：

1. 根目录布局把 `save00.sav`、另一个合法 slot 和 `global.sav` 解析到当前持久 `SessionRoot`；
2. `sav/` 布局把相同逻辑文件解析到当前 `SessionRoot/sav/`；
3. 映射结果都位于当前 SessionWorkspaceRoot，且不写 GameVersionRoot；
4. 两个不同 Session 对同一 GameVersion、同一 `global.sav` 得到不同物理路径；
5. 相同 Session 的重复构造和解析结果确定；
6. 相对根、重叠根、GameVersion 内的可写根、另一个 Session 的 workspace 被拒绝；
7. 路径前缀陷阱（如 `/session/1` 与 `/session/10`）不被误判为包含；
8. 根级模式中的子目录、非允许存档扩展和任意绝对路径失败；
9. `sav/` 模式仍拒绝 `../`、绝对路径和跨区路径。

### 8.3 Layout builder

至少覆盖：

1. 对最小 GameVersion 创建完整、确定的目录和映射计划；
2. CSV、ERB、resources、配置和未知合法目录都是当前 Session 的独立普通文件或目录；
3. 同一输入执行两次幂等且不删除已有存档；
4. 缺失 GameVersion/CSV/ERB、错误文件类型或冲突目录时失败；
5. 发布版本中的 `emuera.config` 不被直接作为可写目标；
6. 源或目标中出现符号链接、硬链接异常、FIFO/设备等条目时失败；Linux CI 必须执行链接用例；
7. builder 失败不递归删除调用者原有目录或其他 Session 数据；
8. 生成的诊断不包含另一个 Session 的绝对路径。

### 8.4 文件系统和逃逸攻击

至少覆盖：

1. 可读取 GameContent，不能写、替换或删除；
2. Save/Temporary/Configuration 只能在各自私有区域执行允许操作；
3. 目录枚举按 ordinal 规范逻辑路径排序；
4. 根外符号链接被拒绝；
5. 指向另一个 Session 的符号链接被拒绝；
6. 指向允许根内部的链接也拒绝，完整复制没有链接例外；
7. 中间父目录是链接时失败，而不只检查最终文件；
8. 创建不存在文件时，最近存在父目录在根内才允许；
9. 检查后替换父目录为链接的竞争模拟至少返回失败，不写到测试根外；若纯托管实现无法完全封闭窗口，在测试名和文档明确剩余限制；
10. 跨区域 move/replace 失败；
11. 只读拒绝使用稳定 reason code；
12. 取消信号和真实 I/O 错误不会被吞掉。

### 8.5 时钟

至少覆盖：

1. fake clock 不推进时等待不完成；
2. 推进到截止时间时同步完成；
3. 多个截止时间按时间及注册顺序稳定完成；
4. 取消等待返回取消且不在后续 Advance 中再次完成；
5. wall clock 调整不改变单调 elapsed 计算；
6. 负 delay 和溢出得到明确行为；
7. 测试不使用 `Thread.Sleep`，重复运行无时间抖动。

### 8.6 端口与架构

至少覆盖：

1. `CloudEmuera.Domain`、`CloudEmuera.Application`、`CloudEmuera.RuntimeAdapter` 编译后的 AssemblyRef 不含 `System.Windows.Forms`、`PresentationFramework`、`WindowsBase`；
2. RuntimeAdapter 公共 API 的参数、返回值、属性和泛型闭包不来自 `System.Drawing`、WinForms、WPF、NAudio 或 Windows Media Player；
3. RuntimeAdapter 不引用 Worker、API、Infrastructure 或固定上游桌面可执行项目；
4. `IGameConsole` fake 能记录操作并通过取消终止输入等待；
5. 图像端口只接收受控资源路径并返回平台中立元数据；
6. 音频 no-op/recording fake 对 unsupported 产生可观察结果；
7. file、clock、image、audio、console 端口都可以由测试 fake 替换，不访问全局静态对象。

架构测试应读取已编译程序集元数据或反射公共 API，而不是只用源码字符串搜索。源码扫描可以作为补充，但不能是唯一证明。

## 9. 实现顺序

建议下一 session 严格按以下顺序实现：

1. 再次核对固定上游的目录派生、两种 `SavDir` 和实际存档文件名规则，记录可追踪源码位置；
2. 先添加架构测试，使当前空 RuntimeAdapter 通过，并固定禁止依赖集合；
3. 实现 `RuntimeRelativePath`、区域类型和全部词法失败测试；
4. 实现 `RuntimePaths`、包含关系 helper 和两种保存布局测试；
5. 实现 `PhysicalPathGuard`，在 Linux 上先写链接逃逸失败测试；
6. 实现 `IRuntimeFileSystem` 与 `LocalRuntimeFileSystem` 的读写权限矩阵；
7. 实现 `SessionRootLayout`/builder 及幂等、冲突和跨 Session 测试；
8. 实现 `IRuntimeClock`、生产适配器和测试 fake，证明确定性 timeout；
9. 添加 Console、图像、音频端口最小契约及 recording fakes；不展开 P0-03 事件模型；
10. 执行专项测试和全局质量门，修复所有警告；
11. 全部通过后更新开发计划状态及实际验证记录。

## 10. 验收标准

本 task 只有同时满足以下条件才算完成：

- 五类平台端口均存在、可由 fake 替换，公共 API 不泄漏桌面/Windows 媒体类型；
- `RuntimeRelativePath` 是游戏路径进入文件系统的必经入口；
- `RuntimePaths` 不依赖 current directory 或上游静态 `Program.ExeDir`；
- 根目录与 `sav/` 两种存档映射测试通过；
- 两个 Session 的同名 slot 和 `global.sav` 映射到不同物理区域；
- GameVersion 区域不可通过 file port 写入；
- 绝对路径、`..`、前缀陷阱、链接逃逸和跨 Session 路径自动失败；
- Layout builder 幂等且不覆盖/删除未知或其他 Session 内容；
- fake clock 可无真实等待地确定推进 timeout；
- 架构测试检查编译程序集和 RuntimeAdapter 公共 API；
- 不修改本步骤当时的固定上游源码，不引入未登记的 Runtime integration 变更；
- P0-01 fixture 验证仍通过；
- 专项测试、全局检查、第三方校验和 diff 检查全部成功；
- 只有在上述条件满足后，开发计划推进到 P0-03。

## 11. 风险与实现注意事项

- **安全声明过度**：路径规范化和链接检查不能单独防御有权限并发修改目录的恶意进程。必须保留未来 mount namespace、Worker 降权和 no-follow 文件打开策略。
- **根路径比较**：裸 `StartsWith` 会把 `/session/1-other` 当成 `/session/1` 子路径；必须采用规范根加目录边界的比较。
- **平台差异**：Windows drive/UNC/保留名在 Linux 上也要词法拒绝，因为同一 GameVersion 可能跨平台流转；链接测试则以 Linux CI 为发布门。
- **大小写和 Unicode**：本 task 不解决上传阶段的 case/Unicode collision（GAME-002/SEC-005 的完整实现），但 resolver 不能偷偷做 culture-sensitive 折叠。
- **端口过度设计**：P0-02 的 Console/媒体类型只固定平台隔离。不要在没有 P0-03/ADR-004 依据时设计完整浏览器协议。
- **端口过窄**：文件接口仍需覆盖固定上游已知的读取、枚举和保存调用，否则 P0-04 会被迫绕过它。新增方法前应给出对应上游调用点和权限测试。
- **重复时钟**：应用 `IClock` 与 Runtime 单调时钟用途不同。不要让 RuntimeAdapter 为复用一个 `UtcNow` 属性而依赖 Application。
- **配置可写性**：发布版本的 `emuera.config` 不可直接修改；Session 必须使用私有工作副本。
- **根级存档位置**：SessionRoot 本身是私有可写目录，因此 Emuera 可直接创建根级 `save*.sav`；只有其中的 GameVersion 内容入口指向只读源。
- **错误泄密**：安全错误不可回显其他 Session 或宿主数据根的绝对路径。

## 12. 交接给后续任务的结果

P0-03 应在 `IGameConsole` 边界内定义结构化输出、输入 prompt、sequence 和 Snapshot，不修改路径安全规则。

P0-04 应在绑定 `RuntimeBaseline.UpstreamCommit` 的内置源码项目中，把上游静态路径、文件、时钟、图像、音频和 Console 调用直接接到本 task 的端口，并在 `MODIFICATIONS.md` 登记；不得为赶通 ERB 而重新直接调用宿主 API。

P0-05 应把本步骤早期建立的分离 `writable/root-saves` 兼容映射收敛为“完整复制 GameVersion 后持久 SessionRoot 直写”模型，运行 P0-01 fixture，并通过 Emuera 原生逻辑证明 save/load。它不增加链接/mount 分类、SaveArtifact、generation 或退出提交层。若真实上游行为暴露契约缺口，应以新增失败测试和最小兼容扩展修正。

## 13. 本次实现与复核记录

- `RuntimeDeadline` 保存起始 timestamp 和 `TimeSpan` timeout，过期判断基于累计 monotonic elapsed，不再通过相邻 timestamp 反推时间单位；新增生产时钟 smoke test 和手动时钟 deadline 测试。
- `SavDirectory` 为目录段定义独立的安全规则，`LocalRuntimeFileSystem` 对嵌套目录的创建、目录查询、文件读写、元数据、移动、删除和枚举使用一致的解析与 allowlist。
- 架构测试直接检查 Domain、Application、RuntimeAdapter 的程序集引用，禁止 GDI+、NAudio、Windows Media Player 和 Application 反向依赖；公共 API 检查扩展到继承、字段、事件、构造函数和泛型参数。
- Linux 开发容器验证：`RuntimePaths|Architecture` 53 项、RuntimeAdapter 全量 79 项、Domain 4 项；`scripts/check.sh`、`scripts/verify-runtime-fixtures.sh`、`scripts/verify-third-party.sh` 和 `git diff --check` 均通过。
