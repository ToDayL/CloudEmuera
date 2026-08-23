# ADR-0029：内置字体目录与 Worker 权威物理排版

状态：已接受（字体文件 SHA-256 在实现导入提交中固化）

日期：2026-08-23

## 背景

当前 Session 只持久化字号和行高。浏览器在开启 Session 时用本机 fallback 字体执行
`CanvasRenderingContext2D.measureText`，把半角/全角近似宽度传给 Worker；headless Runtime 加载
`emuera.config` 后只覆盖窗口宽度、字号和行高，未覆盖 `Config.FontName`，也没有加载一份由
CloudEmuera 控制的字体。不同服务器、浏览器和用户系统因字体 fallback 不同，会得到不同的换行、
PRINTC 列宽和按钮命中区域。

固定上游的桌面排版不是浏览器自动换行。它在 `Config.SetConfig` 后使用 `Config.DefaultFont`、
`StringMeasure` 和 `Config.DrawableWidth`，把逻辑输出转换为一个或多个物理 `ConsoleDisplayLine`，并为
每个 `ConsoleButtonString` 计算 `PointX`/`Width`。`Config.ButtonWrap` 还决定超出可绘制宽度的按钮是
整体移到下一行还是拆分。当前 `HeadlessEmueraConsole` 只保留了逻辑行和 PRINTC 的 Shift-JIS 补空格，
尚未执行这一步物理布局。

用户已确认首期忽略游戏包和游戏脚本请求的字体，只允许 CloudEmuera 分发、校验并支持的字体。首期
字体为 Sarasa Fixed SC 与霞鹜文楷 Mono 的 Light、Regular、Medium 三种字重。

## 决定

### 1. 字体来源与身份

CloudEmuera 直接随产品分发固定字体，不依赖宿主机、容器基础镜像或玩家设备字体。每个 face 以未修改的
上游 TTF 作为规范源：Worker 直接加载该 TTF，浏览器加载在构建期由它无损转换出的完整 WOFF2。初始目录
包含六个具体 face：

| 不可变 face ID | UI 名称 | 上游版本基线 |
| --- | --- | --- |
| `sarasa-fixed-sc-1.0.40-light` | Sarasa Fixed SC Light | Sarasa Gothic v1.0.40 |
| `sarasa-fixed-sc-1.0.40-regular` | Sarasa Fixed SC Regular | Sarasa Gothic v1.0.40 |
| `sarasa-fixed-sc-1.0.40-medium` | Sarasa Fixed SC Medium | Sarasa Gothic v1.0.40 |
| `lxgw-wenkai-mono-1.522-light` | 霞鹜文楷 Mono Light | LXGW WenKai v1.522 |
| `lxgw-wenkai-mono-1.522-regular` | 霞鹜文楷 Mono Regular | LXGW WenKai v1.522 |
| `lxgw-wenkai-mono-1.522-medium` | 霞鹜文楷 Mono Medium | LXGW WenKai v1.522 |

默认值为 `sarasa-fixed-sc-1.0.40-regular`。face ID 同时包含上游版本；未来升级字体必须新增 ID，不能在
原 ID 下替换字节。实现导入提交必须记录来源 URL/tag、上游 commit、文件 SHA-256、内部 family/
subfamily、字重、字形覆盖和许可证。删除仍可能被持久 Session 引用的 face 属于数据迁移，不是普通
资产清理。

两套字体均按 SIL Open Font License 1.1 分发。仓库和生产制品必须带各自完整许可证与版权声明；
[`verify-third-party.sh`](../../scripts/verify-third-party.sh) 必须校验字体字节、转换产物和声明。WOFF2
只能由固定 TTF 在构建期确定性生成，不做字符子集化，不修改 name table，不作为可安装桌面字体分发，
也不依赖外部字体 CDN。目录分别记录 `runtimeTtfSha256` 与 `webWoff2Sha256`，并以规范 TTF 的版本和摘要
定义 face 身份；转换工具及版本必须锁定。

### 2. Session 配置与生命周期

Session 新增必填 `fontFaceId`。创建页和停止态配置页只能从服务端字体目录选择；不能提交字体文件、
CSS family、宿主字体名或任意路径。更新字体与字号/行高一样，只允许 `CLOSED` 或已完成旧 Worker
回收的 `CRASHED` Session；运行态更新返回现有 Session 配置冲突错误。

SQLite migration 为旧 Session 写入默认 face ID。创建/配置幂等摘要、HTTP DTO、审计详情、
`SessionRuntimeLease`、Worker launch spec 和 bootstrap 都携带 face ID。Worker 只接受目录中的 ID，
并复验该 ID 对应文件的 digest；未知、缺失或被篡改的 face 在 Runtime 初始化前 fail closed，不回退到
系统字体。

### 3. Runtime 字体加载顺序

Worker 根据 face ID 从只读内置字体目录解析文件，建立每 Worker 私有的
`PrivateFontCollection`，只加载当前选择的受支持 face。顺序固定为：

```text
验证 face ID / digest / TTF metadata
  → ConfigData.LoadConfig()
  → 覆盖 WindowX、FontSize、LineHeight、FontName
  → 初始化并加载 PrivateFontCollection
  → 验证实际 FontFamily 与目录声明一致
  → Config.SetConfig(ConfigData.Instance)
  → 取得 Config.DefaultFont
  → 创建 StringMeasure 和 HeadlessConsoleLayoutEngine
```

字体必须在 `Config.SetConfig` 前加载；`Config.FontName` 使用从实际 TTF 验证出的内部 family 名，不能用
UI label 或 face ID 代替。`FontFactory` 在 headless 模式只从私有集合解析，并验证没有静默 fallback。
Session 释放时清理 `StringMeasure`、`Font` cache 和 `PrivateFontCollection`。

### 4. 忽略游戏字体

SessionRoot 的 `font/`、`resources/` 或其他目录中的 TTF/OTF 不进入字体集合，也不通过 Session
presentation manifest 作为运行时字体发布。`emuera.config` 中的字体名称会被 Session 选择覆盖；
`SETFONT`、HTML `font face`、工具提示字体等游戏内字体请求统一映射到当前 Session face。请求值可以进入
去重后的兼容性诊断，但不能成为文件路径、CSS family 或宿主字体查找输入。

`CHKFONT` 只把当前选择 face 的规范内部名和明确 alias 报告为可用；其他游戏/系统字体返回不可用。
粗体、斜体、下划线和删除线仍作为样式语义保留。首期字体没有原生 italic 时，布局 advance 使用目录中
声明的 upright face，浏览器可以在固定盒内绘制 synthetic oblique；不得让 synthetic 样式重新决定
物理分行。各字重的 advance 一致性必须由导入验证和视觉测试证明。

### 5. Worker 权威排版

新增 `HeadlessConsoleLayoutEngine`。它复用或直接调用固定上游 `PrintStringBuffer`、
`ConsoleButtonString`、`ConsoleDisplayLine` 的布局规则，不另写一套浏览器式断行算法。每次可见 flush：

1. 把当前逻辑 print buffer 转为带样式的上游 inline/button parts；
2. 用真实 `StringMeasure` 测量各 part，计算 `PointX` 和 `Width`；
3. 以 `Config.DrawableWidth` 和 `Config.ButtonWrap` 形成物理行；
4. 对每条物理行执行原版左/中/右对齐重排；
5. 映射为结构化物理 `ConsoleLine`，输出每个 segment/button 的 `positionX` 和 `measuredWidth`；
6. 物理行作为 Snapshot 裁剪、替换、删除和浏览器渲染的最小可见单位，同时保留逻辑行起点元数据，
   供 `CLEARLINE`、临时行和统计语义使用。

协议新增封闭的 positioned inline segment。segment 包含 `positionX`、`measuredWidth`、安全 children，
以及可选的 button action（value、tooltip、enabled、generation）。没有 action 的普通文字也是 segment，
避免其后的按钮位置继续受浏览器字体流式布局影响。结构变化升级 structured IPC 和 Realtime major；旧
协议 fail closed。

`ButtonWrap` 行为以固定上游为权威：能放下则留在当前行；允许整体换行的按钮在当前行已有内容且放不下
时整体移到下一行；按钮本身宽于整行、按钮拆分已启用或非按钮 part 需要折行时按上游二分测量结果拆分；
不可拆分且超宽的元素保留在单独物理行并允许水平溢出。`nobr` 禁止物理拆分。换行后的剩余 parts 从
`PointX=0` 重新计算，随后再应用行对齐。

### 6. PRINTC/PRINTLC 兼容语义

`PRINTC文字数量` 保持 Shift-JIS 字节/半角格语义，默认 `N=25`：ASCII 通常计 1，大多数 CJK 通常计
2；不可编码字符使用固定上游相同的 replacement fallback。不得改为 `string.Length`、Unicode scalar
数量、grapheme 数量或 East Asian Width 近似。

字段目标像素宽度为 `StringMeasure.GetDisplayLength(new string(' ', N), Config.DefaultFont)`：

- PRINTC 右对齐先按 Shift-JIS byte count 在左侧补到 N，再逐个移除前导空格，直到实际所选样式字体
  的测量宽度不超过目标；
- PRINTLC 左对齐保留原版 `N + 1` 的初始右侧补空格差异，再按实际字体宽度移除尾随空格；
- 输入已经达到阈值时不强制截断；
- `PRINTBUTTONC`/`PRINTBUTTONLC` 使用同一格式化路径，但补白不扩大可点击标签的可视下划线；按钮
  的命中盒仍由排版后的权威 segment 宽度决定；
- `PRINTC并列数量` 只控制上游命令生成循环何时主动 `NewLine`/flush，不是字段宽度，也不能被 layout
  engine 当作每行列数重新解释。

### 7. 浏览器渲染

API 提供有界字体目录和按内容摘要寻址的同源只读 WOFF2 响应。响应使用 `font/woff2`、强 ETag、
`Cache-Control: public, max-age=31536000, immutable` 与 `nosniff`。浏览器只请求当前 Session 选择的
face，在订阅/显示 Snapshot 前创建唯一 CSS family 的 `FontFace`，校验 face ID 与 WOFF2 digest，等待
`FontFace.load()` 与 `document.fonts.ready`。页面重载时需要重新注册 `FontFace`，但可复用 HTTP 缓存；
WebSocket 重连不传输字体。加载失败显示阻断错误，不使用 `monospace` 或用户字体继续渲染。

物理行使用固定 `layoutWidth`/`lineHeight`、`position: relative`、`white-space: pre`、
`overflow-wrap: normal`、`word-break: normal`。positioned segment 绝对定位并使用后端提供的
`left`/`width`；浏览器不得再次自动换行、重新测量拆分点或移动按钮。视口后来变窄时使用水平滚动，
不改变已经运行的 Runtime 布局；需要新宽度必须关闭并重新开启 Session。

### 8. 浏览器测量输入退役

删除 Session open 请求中的 `textMetrics`、Worker bootstrap 的 `halfWidthPx/fullWidthPx` 以及 headless
字符宽度近似。浏览器仍提交有界 `browserWidth`，只用于在加载 `emuera.config` 后限制 `WindowX`；
最终权威宽度是 `Config.DrawableWidth`。Bar、DRAWLINE、PRINTC 和普通文字都使用同一 Worker
`StringMeasure`，不能保留另一条半角/全角估算路径。

## 备选方案

1. **继续使用用户/容器字体 fallback**：拒绝。字体与度量不能复现，客户端和 Worker 会分叉。
2. **让浏览器测量并把所有宽度传给 Worker**：拒绝。客户端不是可信排版权威，且无法覆盖运行期间
   动态文本、样式、按钮拆分与离线运行。
3. **Worker 只发逻辑行，浏览器用同一字体自动换行**：拒绝。即使字体度量表相同，libgdiplus 与浏览器
   shaping/rasterizer 仍可能有舍入差异，`ButtonWrap` 也不是 CSS 自动换行。
4. **首期加载游戏包字体**：拒绝。用户已明确首期只使用受支持字体；游戏字体还会扩大授权、解析、
   fallback 和跨端一致性边界。
5. **浏览器直接下载 TTF**：拒绝。完整 CJK TTF 的传输成本过高；浏览器使用由同一规范 TTF 无损生成的
   WOFF2，并通过度量表校验和跨端布局回归证明等价。
6. **生成 WOFF2 字符子集或 unicode-range 分片**：暂不采用。游戏运行期字符集合不可预知，首期必须
   保持完整字形覆盖；后续引入需要独立缺字/fallback、缓存、许可证和度量回归设计。

## 后果

- Session 配置、SQLite、HTTP、Worker bootstrap、structured IPC、Realtime 和 Web renderer 都需要升级；
- 字体包会增加仓库和镜像体积；浏览器仅按需下载当前 face 的 WOFF2，并以内容摘要长期缓存；
- Worker 是换行、PointX、按钮宽度和命中区域的唯一权威，浏览器只负责绘制；
- 游戏指定字体会被有意忽略并可见诊断，这是首期兼容边界，不再宣称支持任意游戏字体；
- 修改固定上游/编译副本时必须更新 `MODIFICATIONS.md`，并以真实字体 fixture、协议契约和视觉回归
  证明 PRINTC、ButtonWrap、物理行与跨浏览器行为。

## 验证

实施与验收步骤见
[`P1-S04-font-measurement-authoritative-layout-plan.zh-CN.md`](../tasks/P1-S04-font-measurement-authoritative-layout-plan.zh-CN.md)。

字体来源与许可基线：

- [Sarasa Gothic v1.0.40 release](https://github.com/be5invis/Sarasa-Gothic/releases/tag/v1.0.40)
- [Sarasa Gothic LICENSE](https://github.com/be5invis/Sarasa-Gothic/blob/main/LICENSE)
- [LXGW WenKai v1.522 release](https://github.com/lxgw/LxgwWenKai/releases/tag/v1.522)
- [LXGW WenKai OFL.txt](https://github.com/lxgw/LxgwWenKai/blob/main/OFL.txt)
