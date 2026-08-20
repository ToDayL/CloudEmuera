# Emuera 结构化运行时兼容性报告（P1-07）

日期：2026-08-12
固定上游：`2175f8a629257efb08214e093704b3a3d3d06d05`
Runtime integration：`headless-p0.5.1`
结构化 IPC：`cloudemuera.ipc.v4` / protocol `4`
能力矩阵：`p1-07`
能力集合摘要：`7f0003c99e6f86f383b6cb018a894338bead34c502ab9a71a87d8eb2e9e2c86e`

本报告描述固定上游与 CloudEmuera headless adapter 之间的入口级边界。机器可读的完整记录在
[`docs/runtime-capabilities.json`](runtime-capabilities.json)，结构由
[`docs/runtime-capabilities.schema.json`](runtime-capabilities.schema.json) 约束，并由
[`scripts/verify-emuera-capabilities.sh`](../scripts/verify-emuera-capabilities.sh) 作为验证门。

## 分类结论

| capabilityId | 分类 | 结构化结果 | 失败策略 |
| --- | --- | --- | --- |
| `console.text-lines` | Supported | 行、临时行、替换/删除、样式和整行裁剪 | 超限拒绝并保留旧状态 |
| `console.buttons` | Supported | Button label/value/enabled/generation | 非法值或来源拒绝 |
| `console.styles-layout` | Supported | 颜色、逻辑字体、字号/行高和对齐 | 非法几何/字体参数拒绝 |
| `console.html` / `console.html-island` | Supported | allowlist AST / drawable | 未知标签、属性、URL 或超限 fail closed |
| `console.images-sprites` | Supported | manifest asset、source/destination rect、frame、z-index | 缺失或越界资源拒绝 |
| `console.shapes` | Supported | 有界 shape primitive | 未知 primitive 或几何超限拒绝 |
| `console.backgrounds` / `console.cbg-clears` | Supported | 可归约 background/scene clear 操作 | 未知 ID/范围拒绝 |
| `console.cbg-dynamic` | Supported | libgdiplus 有界 surface 转为 PNG RasterDrawable；manifest Sprite 保留动画帧 | 尺寸、总内存、payload、scene 与 envelope 超限拒绝 |
| `input.all-types` | Supported | 全部 prompt 类型、来源、payload 和约束 | stale/conflict/非法来源返回确定结果 |
| `input.timing-timeout` | Supported | 单调 deadline、UTC 展示时间、TimeUpMes、ISTIMEOUT | 超时按封闭 action 原子结束 |
| `window.metadata-readback` | Supported | title、逻辑 viewport 和显示行回读状态 | 非法 viewport 拒绝 |
| `audio.channels` | Supported | channel、revision、loop、volume、start policy | 资源/容量失败可见且不访问设备 |
| `resource.manifest-assets` | Supported | Session manifest logical assetId | 路径穿越、链接逃逸和外部资源拒绝 |
| `host.diagnostics` | Compatible | 与玩家 scrollback 分离的 error/warning/debug 记录 | 不伪装成游戏输出 |
| `host.desktop-shims` | Blocked | 编译所需的窗口、鼠标、热键、宿主日志和桌面 tooltip shim | `HOST_SHIM` |
| `host.external-capabilities` | Blocked | 插件、CALLSHARP、外部网络/DLL 和桌面输入 | `SECURITY_BOUNDARY` |
| `lifecycle.runtime` | Supported | headless 初始化、取消、等待和有界退出 | runtime 失败带稳定诊断 |

除表中明确批准的宿主/安全能力外，影响游戏可见 Console/Input/Media 状态的
入口均标为 `Supported`。矩阵不使用 `Planned`，也不把普通文本或静默 no-op 当作支持。

## 运行时语义证据

- `ConsoleStateStore.ApplyTransaction` 在单一临界区执行校验、大小预算、状态替换、sequence 分配和
  历史发布；失败事务不消费 sequence。
- scrollback 按完整 `ConsoleLine` 裁剪；scene、background、media 和当前 prompt 不按历史年龄
  静默丢弃，裁剪信息进入 `TruncationMetadata`。
- `InputCoordinator` 以单调 timestamp 作为 timeout 权威；输入、timeout、cancel 和 close 竞争时
  只允许一个 prompt 终态，重复 `clientMessageId` 以 fingerprint 校验后幂等返回。
- HTML 使用 executable-free AST；wire message 不含 raw HTML、CSS、URL、绝对路径或像素所有权。
- `StructuredRuntimeAudioPort` 把音频资源映射到 manifest-style asset ID 并更新 channel revision；
  Worker 生产路径不再构造 `NoOpRuntimeAudioPort`。
- 固定上游动态 Graphics 在 Linux Worker 内由 `System.Drawing.Common 6.0.0 + libgdiplus` 执行；
  CBG 输出为带 PNG 签名校验和组合字节上限的 `RasterDrawable`，静态动画 Sprite 仍保留各帧 asset、
  source rect、offset 与 duration。该实现不向 RuntimeAdapter/IPC 暴露 `System.Drawing` 类型。
- eraTW 实机兼容（2026-08-18）：`AppContents` 资源注册表在 Linux 上用平台分隔符组合父图片路径，
  `SPRITECREATED`/`SPRITEHEIGHT` 等脚本门恢复真值，`Look.ERB` 的立绘恢复下发；`DRAWLINEFORM`/
  `DRAWLINE` 按 `Config.DrawableWidth` 展开为整行；`HTML_PRINT` 图片的 `ypos` 保留在
  `SpriteNode.Destination`，浏览器按桌面覆盖层语义渲染，不再撑高文本行。

## 自动化验证映射

| 层级 | 证据 |
| --- | --- |
| RuntimeAdapter | `StructuredConsoleContractTests`：事务原子性、整行裁剪、HTML allowlist、媒体 revision、OneInput、来源和单调 timeout |
| IPC | `StructuredIpcContractTests`：v3 handshake digest、完整 transaction/snapshot round-trip、版本/摘要/未知结构拒绝 |
| Worker mapper | `StructuredConsoleWireMapperTests`：scrollback、scene、media、window、prompt 双向保真 |
| 真实解释器 | `HeadlessRuntimeFixtureTests`：v18-core、em-ee-core、输入、存档、取消、clock、动态 Graphics/CBG、动画 Sprite、诊断和 Blocked 入口 |
| 静态能力门 | `verify-emuera-capabilities.sh`：19 个能力、入口唯一映射、精确测试类/方法证据、源码存在性、baseline/protocol/digest/manifest 一致性和生产 audio port |

dev Docker 中的真实生产链路已用 v3 UDS Worker 集成测试验证（19/19）；浏览器 DOM/Canvas/WebAudio 的实际绘制和视觉回归属于 P1-11/P1-15；本阶段只冻结并验证浏览器
消费所需的结构化状态，不把浏览器渲染结果反向写入 Worker 语义。
