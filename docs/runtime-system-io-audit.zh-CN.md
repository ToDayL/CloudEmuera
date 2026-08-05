# P0-04 上游 System.IO 调用点审计

状态：完成（2026-08-05）

范围：`CloudEmuera.EmueraRuntime.UpstreamHeadless` 实际编译的固定上游源码，以及
`InitializeAsync → Process.Initialize → Process.DoScript` 的 P0-04 路径。P0-05
原生存档读写不在本审计的支持声明内。

## 结论

游戏、配置和资源内容只通过 `IRuntimeFileSystem` 进入运行时。
`UpstreamRuntimeFileView` 将允许的逻辑文件复制到 session 独占、可销毁的临时视图；
固定上游 loader 只能获得该视图内的 config、CSV、ERB、resources、sound、font 和
data 根。`UpstreamRuntimeSession` 在设置 `Program.*Dir` 前验证所有根具有同一个
私有父目录，拒绝任何越界根。

因此仍使用 `System.IO` 的固定上游代码只操作兼容视图，不接触原始 GameRoot、宿主
工作目录或任意绝对路径。port-only 测试在物理 GameRoot 为空时仍能由真实上游
loader 执行 ERB，证明内容不能绕过文件端口进入解释器。

## 调用点分类

| 分类 | 调用点 | P0-04 处理 |
| --- | --- | --- |
| 配置 | `ConfigData` 的 config/default/fixed 读写 | `Program.ExeDir` 指向私有 `config/`；输入 config 由文件端口复制 |
| CSV/ERB 预载 | `Preload`、`EraStreamReader`、`EncodingHandler` | 只读取私有 `csv/`、`erb/`；枚举结果由文件端口以 ordinal 顺序物化 |
| Loader 枚举 | `Config.GetFiles`、`ErbLoader`、`ErhLoader`、`ConstantData` | 根固定为私有视图；不存在原始 GameRoot 路径 |
| 诊断源码行 | `Process` 的 `File.ReadLines` | 文件名来自已加载 label，根固定为私有 `erb/` 或 `csv/` |
| 资源图片 | `AppContents.LoadContents` | headless 编译路径禁用；PNG/Sprite 由 `IRuntimeImagePort` 解析 metadata |
| 音频 | `PLAYSOUND`/`PLAYBGM` 的宿主文件探测 | headless 编译路径禁用探测，统一调用 `IRuntimeAudioPort` |
| data/ERD | `VariableEvaluator` 的固定文件名 | `Program.DatDir` 指向私有 `data/`；P0-04 fixture 不声明持久化支持 |
| 文本文件函数 | `SAVETEXT`、`LOADTEXT`、`ENUMFILES`、`EXISTFILE` | `Utils.GetValidPath` 将相对路径锚定到私有 `config/`；绝对路径被拒绝 |
| 原生存档 | `save*.sav`、`global.sav` 相关调用 | 当前也被限制在私有视图；正式双布局和端口提交语义推迟到 P0-05 |
| 动态 Graphics/CBG | `GCREATE*`、`GLOAD`、`GSAVE`、`GDRAW*`、`CBG*` 等 | 初始化 capability gate 明确返回 `UnsupportedCapability`，不会实例化 Bitmap/Graphics |
| 桌面/插件 | WinForms Console、Hotkey、Rikaichan、Clipboard、PluginManager、WMP、NAudio | 不进入 headless 编译项目，或由 inert stub/fail-closed 边界替代 |

## 自动化证明

- `UpstreamLoaderConsumesGameContentFromFilePortOnly`：物理 GameRoot 为空，内容仅存在于
  测试文件端口，真实 `Process` 仍成功运行。
- `GraphicsFunctionFailsClosedBeforeAnyGdiObjectCanBeCreated`：动态 Graphics 在初始化期
  返回稳定 Unsupported 结果。
- `UnsupportedIdentifierInPrintedTextIsNotMisclassified`：capability gate 不会把普通输出
  文本误判成函数调用。
- `HeadlessAssemblyDoesNotReferenceDesktopFrameworks`：正式与上游 headless 程序集均不
  引用 WinForms、WPF、WMP 或 NAudio。
- 双 fixture 主验收验证 INPUT、RESULT、HTML、Sprite、QUIT 和原始 GameRoot 不变。

## 后续边界

P0-05 不得把私有视图中的临时存档当成正式实现。它需要把 `save*.sav`、
`global.sav` 和 `sav/` 双布局单独接到 Save area，增加原子提交、重载和失败恢复测试。
