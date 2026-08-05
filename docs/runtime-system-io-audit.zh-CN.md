# P0-05 上游 System.IO 调用点审计

状态：完成（2026-08-05）

范围：`CloudEmuera.EmueraRuntime.UpstreamHeadless` 实际编译的固定上游源码，以及
`InitializeAsync → Process.Initialize → Process.DoScript` 的持久 SessionRoot 路径。

## 结论

Session 管理方先通过 `SessionRootLayoutBuilder` 按发布 manifest 将完整普通文件树复制到
持久 SessionRoot。固定上游 loader 直接收到该 SessionRoot 及其 `CSV`、`ERB`、资源和
`tmp` 子目录；运行时只使用这份副本，不接触原始 GameVersion 或其他 SessionRoot。
`UpstreamRuntimeSession` 在设置 `Program.*Dir` 前重新校验 SessionRoot 内所有条目都是
普通文件/目录，并拒绝链接、特殊文件和共享硬链接。

因此仍使用 `System.IO` 的固定上游代码只操作 SessionRoot，原生 writer/reader 直接在
该目录的根级或 `sav/` 位置读写存档。`IRuntimeFileSystem` 仍负责宿主预检和受控的
逻辑文件操作，但不再物化一个会在 Host dispose 时删除的正式运行视图。

## 调用点分类

| 分类 | 调用点 | P0-04 处理 |
| --- | --- | --- |
| 配置 | `ConfigData` 的 config/default/fixed 读写 | `Program.ExeDir` 指向 SessionRoot；配置是复制后的私有普通文件 |
| CSV/ERB 预载 | `Preload`、`EraStreamReader`、`EncodingHandler` | 只读取 SessionRoot 内的 `CSV`、`ERB`；源与目标不共享 inode |
| Loader 枚举 | `Config.GetFiles`、`ErbLoader`、`ErhLoader`、`ConstantData` | 根固定为 SessionRoot；不存在原始 GameVersion 路径 |
| 诊断源码行 | `Process` 的 `File.ReadLines` | 文件名来自已加载 label，根固定为 SessionRoot 内的 `ERB` 或 `CSV` |
| 资源图片 | `AppContents.LoadContents` | headless 编译路径禁用；PNG/Sprite 由 `IRuntimeImagePort` 解析 metadata |
| 音频 | `PLAYSOUND`/`PLAYBGM` 的宿主文件探测 | headless 编译路径禁用探测，统一调用 `IRuntimeAudioPort` |
| data/ERD | `VariableEvaluator` 的固定文件名 | `Program.DatDir` 指向 SessionRoot/tmp；不成为第二套存档提交目录 |
| 文本文件函数 | `SAVETEXT`、`LOADTEXT`、`ENUMFILES`、`EXISTFILE` | 相对路径仍受上游和 Runtime 路径边界约束；绝对路径不作为宿主入口 |
| 原生存档 | `save*.sav`、`global.sav` 相关调用 | `UseSaveFolder:NO` 写 SessionRoot 根；`YES` 写 `SessionRoot/sav/`；不复制/提交 |
| 动态 Graphics/CBG | `GCREATE*`、`GLOAD`、`GSAVE`、`GDRAW*`、`CBG*` 等 | 初始化 capability gate 明确返回 `UnsupportedCapability`，不会实例化 Bitmap/Graphics |
| 桌面/插件 | WinForms Console、Hotkey、Rikaichan、Clipboard、PluginManager、WMP、NAudio | 不进入 headless 编译项目，或由 inert stub/fail-closed 边界替代 |

## 自动化证明

- `NativeSaveRoundTripsAcrossTwoHosts`：两个完全释放的真实 Host 复用同一 SessionRoot，
  由 ERB 原生 writer 保存，再由新 Host 的 reader 加载并通过 EVENTLOAD 输出值。
- `SessionRootLayoutBuilderTests`：完整 manifest 复制、未知目录保留、原子 staging、幂等
  保留修改/存档、配额和链接/硬链接/FIFO fail closed。
- `GraphicsFunctionFailsClosedBeforeAnyGdiObjectCanBeCreated`：动态 Graphics 在初始化期
  返回稳定 Unsupported 结果。
- `UnsupportedIdentifierInPrintedTextIsNotMisclassified`：capability gate 不会把普通输出
  文本误判成函数调用。
- `HeadlessAssemblyDoesNotReferenceDesktopFrameworks`：正式与上游 headless 程序集均不
  引用 WinForms、WPF、WMP 或 NAudio。
- 双 fixture 主验收验证 INPUT、RESULT、HTML、Sprite、QUIT 和原始 GameRoot 不变。

## 后续边界

P0-06 负责把已经创建并绑定的 SessionRoot 交给独立 Worker，并通过租约管理其独占写权。
P0-05 不建立 SaveArtifact、generation 或退出提交协议；停止态文件管理仍由后续步骤负责。
