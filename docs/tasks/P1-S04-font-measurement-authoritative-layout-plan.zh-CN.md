# P1-S04：内置字体测量与 Worker 权威物理排版详细方案

状态：DONE

设计日期：2026-08-23

对应开发步骤：`P1-S04 — 内置字体测量与 Worker 权威物理排版`

关联需求：SESS-013、PLAY-013、PLAY-014、COMP-007、COMP-010、OPS-002、SEC-008、NFR-006/007/013

关联决策：[`ADR-0018`](../adr/0018-emuera-structured-interaction-model.md)、
[`ADR-0019`](../adr/0019-libgdiplus-mvp-graphics-compatibility.md)、
[`ADR-0023`](../adr/0023-session-presentation-assets-and-csp.md)、
[`ADR-0029`](../adr/0029-bundled-font-authoritative-headless-layout.md)

前置任务：P1-S03、P1-14

阻断后续：P1-15 的字体、换行、按钮命中与视觉验收

## 1. 目标结果

本任务把字体选择、字体测量、PRINTC 字段宽度、物理断行和按钮横坐标收敛到 Worker 一处权威实现。
完成后：

1. CloudEmuera 随产品分发 Sarasa Fixed SC 和霞鹜文楷 Mono 的 Light/Regular/Medium 六个具体 face；
2. Session 创建页和停止态配置页选择具体 `fontFaceId`，选择随 Session 持久化并进入 Worker binding；
3. Worker 在 `Config.SetConfig` 前加载对应 TTF、覆盖 `Config.FontName`，用 `Config.DefaultFont`、
   `StringMeasure` 和 `Config.DrawableWidth` 执行原版物理布局；
4. Snapshot/Realtime 输出物理 `ConsoleLine`、positioned segment、每个按钮的 `positionX` 和
   `measuredWidth`，并保留原版 `ButtonWrap` 与超宽元素策略；
5. 浏览器按需加载由同一规范 TTF 无损生成、度量等价的完整 WOFF2 后按后端坐标绘制，禁用 CSS
   自动换行，不参与断行裁决；
6. PRINTC/PRINTLC 保留 Shift-JIS 半角格、N/N+1、实际像素修正和 `PRINTC并列数量` 主动 flush 语义；
7. 游戏包字体、宿主字体和用户字体完全不参与首期 Runtime 排版。

## 2. 非目标与明确边界

- 不加载、服务或选择 SessionRoot/game package 内的 TTF/OTF/TTC/WOFF；现有 presentation manifest
  中游戏字体投影在本任务后删除或保持不可达，不能被 Runtime/Web 使用；
- 不支持用户上传字体、管理员在线安装字体、外部 CDN、CSS 任意 family 或操作系统 font discovery；
- 不追求与 Windows GDI/TextRenderer 像素完全一致；发布权威是固定 Linux 镜像、固定 libgdiplus 和
  固定 TTF 的可重复结果，差异由兼容基线记录；
- 不让浏览器在 resize/orientation change 后要求活动 Worker 热重排；窄视口横向滚动；
- 不把 `PRINTC文字数量` 改为 Unicode 字符数，也不把 `PRINTC并列数量` 解释成字段宽度；
- 不引入 HarfBuzz/Skia 迁移；长期替换仍需独立 ADR 和全量视觉基线；
- 不在本任务改变输入槽、显示 commit、Session 状态机、Worker epoch 或 Snapshot 裁剪原则。

## 3. 当前实现差距

### 3.1 Session 与启动链路

- `sessions` 只有 `font_size`、`line_height`，HTTP create/configure DTO 和 `SessionView` 没有字体 ID；
- `openSession()` 用浏览器 fallback font 测量 `A`/`汉`，把 `halfWidthPx/fullWidthPx` 送入 open；
- `SessionRuntimeLease`、Worker launch spec、bootstrap、`EmueraRuntimeOptions` 仍携带近似宽度；
- `UpstreamRuntimeSession.InitializeAsync()` 在 `ConfigData.LoadConfig()` 后只覆盖 WindowX、FontSize、
  LineHeight，没有加载私有字体，也没有覆盖 FontName。

### 3.2 Headless Console

- `HeadlessEmueraConsole.StrMeasure` 和 `PrintBuffer` 返回 `null`；
- headless `TextRenderer.MeasureText` stub 使用 `text.Length * fontSize * 2/3`，不是字体实测；
- pending buffer 直接投影 `TextNode`/`ButtonNode`，`FlushPendingLine()` 只形成逻辑行；
- `FormatPrintCValue()` 已保留 Shift-JIS byte count 和左右补空格，但未执行原版按
  `StringMeasure` 超宽逐空格回退；
- 普通文本的 CSS 自动换行仍可能形成后端不知道的新物理行；只有部分 HTML button 已携带 PointX。

### 3.3 浏览器

- console CSS 使用系统 fallback 列表、`white-space: pre-wrap` 和 `overflow-wrap: anywhere`；
- 默认 TextNode 处于普通 inline flow，后续按钮仍可能被浏览器度量推动；
- presentation manifest 的字体来自 SessionRoot 游戏资产，与本任务的产品字体目录不是同一概念；
- 没有 font load barrier；Snapshot 可能在字体加载前用 fallback 首绘并发生 layout shift。

## 4. 字体资产目录与供应链

### 4.1 仓库布局

建议新增：

```text
assets/runtime-fonts/
├── catalog.json
├── runtime-ttf/
│   ├── sarasa-fixed-sc/1.0.40/{Light,Regular,Medium}.ttf
│   └── lxgw-wenkai-mono/1.522/{Light,Regular,Medium}.ttf
├── web-woff2/
│   └── {sha256}.woff2
└── licenses/
    ├── Sarasa-Gothic-LICENSE.txt
    └── LXGW-WenKai-OFL.txt
```

实际文件名可保留上游名称；代码永远通过 `catalog.json` 和 face ID 解析，不根据路径、文件名或 UI
label 推断 family。`runtime-ttf` 保存未修改的规范源，供 Worker 加载；`web-woff2` 保存由对应 TTF 在
构建期确定性、无损转换出的完整字体，以 WOFF2 内容摘要命名，只供浏览器传输。生产 publish 把目录
原样复制到只读 `/app/runtime-fonts`；开发镜像读取相同仓库资产。字体不复制到 Game current 或
SessionRoot，不计入用户内容配额。

`catalog.json` 每项至少包含：

```json
{
  "faceId": "sarasa-fixed-sc-1.0.40-regular",
  "displayName": "Sarasa Fixed SC Regular",
  "family": "sarasa-fixed-sc",
  "weight": 400,
  "sourceVersion": "1.0.40",
  "runtimeTtfPath": "runtime-ttf/sarasa-fixed-sc/1.0.40/Regular.ttf",
  "runtimeTtfSha256": "<64 lowercase hex>",
  "runtimeTtfByteLength": 0,
  "webWoff2Path": "web-woff2/<sha256>.woff2",
  "webWoff2Sha256": "<64 lowercase hex>",
  "webWoff2ByteLength": 0,
  "runtimeFamilyName": "<从 TTF name table 与 PrivateFontCollection 双重验证>",
  "licenseId": "OFL-1.1",
  "licenseFile": "licenses/Sarasa-Gothic-LICENSE.txt"
}
```

### 4.2 导入与验证脚本

新增 `scripts/import-runtime-fonts.sh` 和 `scripts/verify-runtime-fonts.sh`。导入脚本只接受 ADR 固定的
tag/release artifact，解包到临时目录，明确选择六个 TTF，再以锁定版本的转换工具生成完整 WOFF2 和
catalog；相同 TTF、工具版本与参数必须生成相同 WOFF2。正常构建和测试永不联网。验证至少检查：

- 目录无额外字体、链接、特殊文件、路径大小写/Unicode 冲突；
- TTF/WOFF2 各自的 SHA-256、长度、signature、name table family/subfamily、OS/2 weight 与 catalog 一致；
- WOFF2 解码后的 `cmap`、`head.unitsPerEm`、`hhea`、`hmtx`、`maxp`、`OS/2`、`GDEF`、`GPOS`、
  `GSUB` 及轮廓/定位相关表与规范 TTF 等价；不得子集化、丢 glyph 或修改内部名称；
- `PrivateFontCollection.AddFontFile` 后只发现预期 family，创建 `Config.DefaultFont` 不发生 fallback；
- ASCII、全角空格、常用简繁中日文、假名、ASCII/CJK 混排 fixture 均有 glyph；缺字测试行为固定；
- 三个字重在用于断行的代表性字符集上 advance 满足既定等宽关系；
- LICENSE/OFL 全文、版权和 `THIRD-PARTY-NOTICES.md` 条目存在；霞鹜文楷的 Web 转换同时核对其保留
  字体名称附加许可，产物不作为可安装桌面字体发布；
- 生产 publish 中的 TTF/WOFF2 分别与 catalog digest 一致，API 绝不暴露 Runtime TTF。

`verify-third-party.sh` 和 `check.sh` 调用字体验证。首次导入字体使用独立逻辑提交并带 DCO signoff。

## 5. 领域、持久化与 HTTP 契约

### 5.1 值对象与目录端口

Application 增加 `RuntimeFontFaceId` 值对象/验证器和只读 `IRuntimeFontCatalog`：

```text
ListAvailable() -> RuntimeFontFace[]
Require(faceId) -> face metadata or FONT_FACE_NOT_FOUND
OpenVerified(faceId) -> verified file handle/path owned by trusted catalog
```

Domain 不读取文件或 System.Drawing。Application/Infrastructure 只流转 face ID；Worker/Runtime 集成层
负责 TTF 加载。face ID 只允许 catalog exact match，不做大小写折叠、模糊匹配或 alias 持久化。

### 5.2 SQLite migration

新增非空 `sessions.font_face_id TEXT`，默认迁移值为
`sarasa-fixed-sc-1.0.40-regular`，并增加长度/非空 CHECK。SQLite 不能对二进制目录建立外键；创建、更新、
open、recovery 和 readiness 分别经 catalog 复验。

迁移测试覆盖空库、旧库回填、rollback、未知历史值故障注入和 schema snapshot。恢复已有持久幂等响应时
兼容旧 `SessionView` JSON：要么 migration 同步重写成功响应，要么反序列化缺失字段时明确补默认并在
下一次成功命令写回；不能因为新增字段让旧幂等键 500。

### 5.3 HTTP API

Session schemaVersion 升级，契约改为：

```text
POST /api/v1/sessions
  { gameId, name, fontFaceId, fontSize, lineHeight }

PUT /api/v1/sessions/{id}/configuration
  { name, fontFaceId, fontSize, lineHeight }

SessionResponse
  { ..., fontFaceId, fontSize, lineHeight }

GET /api/v1/runtime-fonts
  { schemaVersion, defaultFaceId,
    items[{faceId,displayName,family,weight,webAssetDigest,webAssetByteLength,webAssetUrl}] }

GET /api/v1/runtime-fonts/assets/{webAssetDigest}.woff2
  Content-Type: font/woff2
  ETag: "sha256-..."
  Cache-Control: public, max-age=31536000, immutable
```

字体目录/文件无用户私密内容，可以与登录页同源公开读取；若现有 API 约定要求认证，也可统一要求登录，
但必须允许 console 在 Snapshot 前取得。WOFF2 路径必须与 catalog 的内容摘要完全匹配，使用强 ETag、
一年 `public immutable`、`nosniff`、准确长度和唯一 `font/woff2` MIME；错误摘要、额外路径段和非 catalog
asset fail closed。因为 URL 随内容变化，发布新字体不会污染旧缓存。CSP 保持 `font-src 'self'`。

create/configure 在写数据库前验证 face ID。幂等摘要加入 face ID；同 key 不同字体返回 conflict。配置
审计只记录旧/新 face ID、字号和行高，不记录文件路径。运行态修改与现有字号/行高规则一致。

## 6. Worker bootstrap 与 Runtime 初始化

### 6.1 删除客户端度量

删除以下字段和逻辑：

- HTTP open body `textMetrics`；
- `SessionTextMetrics`；
- launch spec/bootstrap/options 的 `HalfWidthPx`、`FullWidthPx`；
- Web `measureSessionTextMetrics()` 和 fallback font list；
- `BarDisplayWidth` 的 half/full heuristic。

open 只提交 `browserWidth`。HTTP 应拒绝遗留 `textMetrics`，避免客户端误以为仍有权威作用。

### 6.2 增加字体 binding

`SessionRuntimeLease`、`WorkerLaunchSpec`、bootstrap JSON/protobuf 增加 `fontFaceId` 与 catalog digest。
bootstrap schema 增 major/minor；API 不能传任意字体路径。Worker 从自身只读字体根解析 ID，校验
catalog digest、文件 digest、普通文件/no-follow 和路径位于根内，再创建 Runtime options。

API/Worker catalog digest 不一致时注册/启动 fail closed，稳定 reason 为 `font_catalog_mismatch`；单 face
缺失/损坏为 `runtime_font_unavailable`。日志只记录 face ID/digest 前缀，不记录绝对安装路径。

### 6.3 初始化顺序和清理

`UpstreamRuntimeSession.InitializeAsync()` 在取得 runtime gate 后：

1. reset 上一 Session 的 `FontFactory` 和 `GlobalStatic.Pfc`；
2. 加载并验证选中 TTF；
3. `ConfigData.LoadConfig()`；
4. 覆盖 WindowX/FontSize/LineHeight/FontName；
5. `Config.SetConfig()`；
6. 立即取得 `Config.DefaultFont` 并确认 `Name`/size/GraphicsUnit/face digest；
7. 创建真实 `StringMeasure` 和 layout engine；
8. 再进入 Preload/parser/Process 初始化。

若严格按用户要求需要“加载字体发生在 Config.SetConfig 前”，第 2～4 步可以交换 LoadConfig 与字体加载，
但不可把 Pfc/FontFactory 初始化推迟到 SetConfig 后。最终代码注释和测试必须直接断言顺序。

Dispose/failure/cancellation 所有路径都释放 layout engine、StringMeasure、字体对象/Pfc，并清空静态 cache，
保证同 Worker 测试进程内后续 Session 不继承前一个 face。

## 7. 字体名与游戏请求映射

### 7.1 默认字体

`ConfigData.FontName` 无条件写入 catalog 的 `runtimeFamilyName`，因此游戏 `emuera.config` 的字体名称不会
进入 FontFactory。结构化输出中的默认逻辑 family 固定为 `session-default`，不把内部 TTF family 名发给
浏览器；Snapshot `WindowMetadata` 增加 `fontFaceId` 和 `fontAssetDigest` 形成闭合绑定。

### 7.2 SETFONT/HTML/CHKFONT

- `SetFont(name)` 和 HTML `font face` 保留样式作用域，但 family 归一为 `session-default`；
- 首次遇到不同请求名时记录 `game_font_ignored` warning，按 Session 有界去重，不能刷屏；
- `CHKFONT` 对当前 face ID、内部 family 和受控 aliases 返回 1，对其他名称返回 0；
- SessionRoot 字体文件不扫描、不加入 Pfc、不进入浏览器字体 catalog；
- 动态 Graphics 的文字绘制也使用同一 resolver，不能通过 `GSETFONT` 等入口回到系统字体。

能力矩阵把“内置 Session 字体、样式、权威排版”标为 Supported，把“任意游戏字体”明确标为 Blocked/
Compatible-with-fallback（按现有矩阵枚举选择并给出可见诊断），不能继续宣称任意 manifest font Supported。

## 8. HeadlessConsoleLayoutEngine

### 8.1 所有权与输入输出

layout engine 属于单个 `EmueraConsole`，构造时捕获：

```text
Config.DefaultFont
StringMeasure
Config.DrawableWidth
Config.LineHeight
Config.ButtonWrap
Config.CompatiLinefeedAs1739
```

它不访问浏览器、HTTP、数据库或临时客户端 metrics。输入是当前 print buffer 的 styled inline parts、
显式 line break、alignment、temporary/nobr/lineEnd 和 button metadata；输出是一个或多个物理行：

```text
PhysicalConsoleLine
  logicalLineOrdinal
  physicalIndex
  isLogicalStart
  alignment
  temporary
  layoutWidth
  lineHeight
  segments[]

PositionedInlineSegment
  positionX
  measuredWidth
  children[]
  action? { value, tooltip, enabled, generation }
```

`positionX >= 0`，`measuredWidth >= 0`，`positionX + measuredWidth` 可超过 layoutWidth 仅用于上游允许的
不可拆分超宽元素。所有数值有限且受现有几何/事务/节点预算约束。

### 8.2 复用固定上游算法

优先让 engine 使用 `PrintStringBuffer.setWidthToButtonList`、`ButtonsToDisplayLines`、
`getDivideIndex`、`ConsoleDisplayLine.SetAlignment` 的同一生产实现。若因可见性必须抽取 helper，应把共同
算法移到上游目录内的无 WinForms 类，由 desktop/headless 两侧共同调用；禁止复制后各自演化。

必要的固定上游修改必须：

- 在修改文件保留 CloudEmuera 显著注释；
- 更新 `src/CloudEmuera.EmueraRuntime/MODIFICATIONS.md`；
- 保留原始 `ButtonWrap` 条件顺序、二分边界、UTF-16 divide index、PointX rebase 和 alignment rounding；
- 用桌面算法 golden fixture 与 headless 输出逐字段比较。

### 8.3 物理行与逻辑行

一个逻辑 flush 可产生多条物理行。第一条 `isLogicalStart=true`，后续为 false；每条都有独立稳定 lineId，
例如逻辑 ID 加物理 index。`PRINT` 的未结束 buffer 只能 append 到最后一条尚可追加的逻辑行；一旦物理
layout 已 commit，后续 append 必须重新布局受影响的最后一组物理行，并用一个 Console transaction 原子
Replace/Delete/Append，不能让浏览器短暂看到旧拆分和新尾部并存。

`CLEARLINE n`、临时行替换、日志行计数和 Snapshot 裁剪必须明确其单位：上游要求逻辑行的操作先映射到
该逻辑行当前拥有的全部物理 lineId；可见裁剪仍删除最旧完整物理行组，不能留下半个逻辑 wrapped group。
这一点需与 ADR-0003/0018 的完整行裁剪共同更新。

### 8.4 ButtonWrap/超宽策略

逐项锁定原版：

1. `nobr=true`：不物理折行，保留 PointX，允许整行超过 DrawableWidth；
2. segment 放得下：加入当前物理行；
3. 放不下且 `ButtonWrap=true`、当前行非空、当前 segment 是可点击按钮：整个按钮移到下一行；
4. `ButtonWrap=false`：按钮可在最大 fitting UTF-16 index 拆为两个同 action segment；
5. 当前行为空而单按钮仍超宽：无论 ButtonWrap 都尝试拆分，避免永远移行；
6. 普通非按钮默认可拆；`CompatiLinefeedAs1739` 打开时按原条件对待；
7. 图片/Shape/不可分节点找不到 divide index：单独保留，可能溢出；
8. 每次换行后剩余 segment 从 x=0 重算，再应用 center/right 对齐；
9. 拆分按钮的两段都提交相同输入 value/generation，但一次 prompt 仍只接受一个输入。

边界 fixture 必须覆盖 DrawableWidth-1、恰好等于、+1、首 segment 超宽、多样式 segment、图片前后文字、
全角/半角混排、代理对/组合字符和空字符串。

## 9. PRINTC/PRINTLC 详细算法

### 9.1 半角格计数

建立单例 `Encoding.GetEncoding("Shift-JIS")`，使用与固定上游相同的 replacement fallback；它只用于
PRINTC 长度，不替代脚本/文件严格解码器。`GetByteCount(value)` 是半角格数：不做 Unicode normalize，
也不根据当前字体 glyph 宽度反推数量。

### 9.2 像素目标与补白修正

layout engine 初始化或 font/size 改变时缓存：

```text
target = StringMeasure.GetDisplayLength(" " × N, Config.DefaultFont)
```

右对齐：

```text
length = ShiftJisByteCount(value)
if length < N:
  result = spaces(N - length) + value
  while result startsWith space and Measure(result, currentStyleFont) > target:
    remove one leading space
else:
  result = value
```

左对齐（PRINTLC）：

```text
length = ShiftJisByteCount(value)
if length < N + 1:
  result = value + spaces(N + 1 - length)
  while result endsWith space and Measure(result, currentStyleFont) > target:
    remove one trailing space
else:
  result = value
```

这里故意保留 N+1 初始差异和目标仍为 N 个默认字体空格宽度的历史行为。不得为了“对称”而修改。
样式字体构造失败时不能静默返回未布局文本；因为所有字体已受控，应作为 Runtime/font catalog 缺陷
fail closed 并进入测试。

### 9.3 按钮补白

PRINTBUTTONC/LC 的字段在 layout 输入中拆成 leading padding、label、trailing padding 三段。只有 label
携带 action/underline/hover，padding 是普通 positioned segment；整体字段仍参与同一个物理布局。这样
既保留列宽，也不把空格变成额外可点击范围。若原版点击盒实际包含补白与既有 P1-11 约定冲突，兼容
fixture 以当前已接受的“补白在按钮外”行为为准并在报告中注明。

### 9.4 PRINTC并列数量

保留 `Process.ScriptProc`/`Process.SystemProc` 当前 `count % Config.PrintCPerLine == 0` 后调用 `NewLine`
的路径。layout engine 不读取这个值决定列数；它只响应真正到达的 flush/newline。测试分别改变
`PrintCLength` 和 `PrintCPerLine`，证明二者互不替代。

## 10. 结构化协议与状态归约

### 10.1 RuntimeAdapter

新增/调整封闭模型：

- `ConsoleLine.LayoutWidth`、`LineHeight`、`LogicalLineId`、`PhysicalIndex`、`IsLogicalStart`；
- `PositionedInlineSegmentNode(positionX, measuredWidth, children, action?)`；
- `WindowMetadata.FontFaceId`、`WebFontAssetDigest`；Runtime TTF digest 留在受信 catalog/bootstrap 与
  版本诊断中，不要求浏览器取得 Runtime 文件；
- limits 增加单逻辑行最大物理行数、segment 数和 relayout transaction 操作数。

物理行不能含未定位的顶层 Text/Button；validator 拒绝 position 重叠规则之外的非法负数、溢出和未知
action。重叠是否允许由上游 pos 语义决定，不能笼统禁止；相同 x 的 z/order 仍按节点顺序。

### 10.2 IPC v6 / Realtime v4

structured IPC 升至 v6，Realtime 升至 v4，更新 protobuf、capability digest、JSON Schema、C# mapper、
TypeScript generator/decoder、golden fixture 和版本端点。v5/v3 不能接收缺少物理布局字段的新 Snapshot；
握手必须 fail closed，不维护双写兼容层。

`DisplayCommit` 仍是唯一浏览器可见边界。一次最后逻辑行重排涉及的 Replace/Delete/Append 必须位于同一
committed frame；API mirror 和 Web reducer 原子应用，否则请求 resync。

### 10.3 有界输出

segment 的 x/width 和逻辑分组增加估算字节；更新 RuntimeAdapter/IPC/API/Web 同值 limits。一个超长
可拆文本最多产生有界物理行；超过上限时整次 transaction 失败并产生稳定 `console_layout_limit`
诊断，不能提交前半段。Snapshot 裁剪按完整逻辑行组进行，prompt 不因裁剪丢失。

## 11. 浏览器字体加载与渲染

### 11.1 Font loader

新增 `RuntimeFontLoader`：

1. 读取 `/runtime-fonts` catalog；
2. 以 Snapshot/Session 的 faceId+webAssetDigest exact match；
3. `fetch` 同源、内容寻址的 WOFF2 endpoint，校验状态、`font/woff2`、长度，并优先用 Web Crypto、在
   非安全开发 origin 无 `SubtleCrypto` 时使用内置 SHA-256 实现，与 `webAssetDigest` 比对；
4. 用已验证的 `ArrayBuffer` 创建 CSS family `cloudemuera-runtime-<digest-prefix>` 的 `FontFace`，显式声明
   weight 和 upright style，不再发起第二次 URL 字体请求；
5. await `font.load()`，加入 `document.fonts`，再 await `document.fonts.load()`/`ready`；
6. 用 test string 确认可用；
7. 成功后才 mount `ScrollbackRenderer` 和接受按钮点击。

加载失败、digest mismatch、catalog 缺项或浏览器拒绝字体时展示可重试阻断页；禁止 fallback 首绘。只
加载当前 Session 选择的一个 face，不预取其余五个；设置页预览只在选中某项时加载对应 face。应用内按
digest 复用 `FontFace` promise，unmount 不重复创建；页面重载会重新注册 `FontFace`，但正文应命中 HTTP
缓存。WebSocket 重连只恢复 Snapshot/事件，不请求或传输字体。测试清理可以显式 delete FontFace。

### 11.2 CSS/DOM

每条物理行：

```css
.console-line {
  position: relative;
  width: var(--runtime-layout-width);
  height: var(--runtime-line-height);
  white-space: pre;
  overflow-wrap: normal;
  word-break: normal;
  font-family: var(--runtime-font-family);
  font-synthesis: none;
}
```

每个 segment 使用 `position:absolute; left:Xpx; width:Wpx; top:0; height:lineHeight`。按钮不使用 margin/
padding/border 改变盒模型；可见 focus outline 用不参与 layout 的 outline/box-shadow。普通 segment 和按钮
children 不再设置 `left: positionX` 的 relative 偏移，也不依赖 preceding inline width。

如要绘制 synthetic oblique，使用 segment 内部 transform 且保持外部 width；粗体使用目录映射的 face，
不能让 CSS `font-weight` 选择用户字体。点击盒、focus box 和 pointer hit box采用后端 measuredWidth；
最小触摸目标只能加透明 overlay 且不得覆盖相邻按钮，无法安全扩展时保留原版命中盒并由 a11y 报告记录。

### 11.3 视口与缩放

console 内容区宽度至少为 Snapshot layoutWidth；设备更窄时横向滚动。禁止 CSS `transform:scale`、浏览器
自适应字号和 `text-size-adjust` 改变 Runtime 像素。系统页面 zoom 属于浏览器缩放，DOM CSS 像素整体
缩放但相对位置不重排。

开启 Session 时 `browserWidth` 继续限制 `Config.WindowX`；后续 resize 不热更新。UI 在设置页提示“字体、
字号、行高或布局宽度仅在下次开启生效”。

## 12. Session UI

创建页与配置页先查询字体目录，使用按 family 分组的六项 select/radio：

- Sarasa Fixed SC — Light / Regular / Medium；
- 霞鹜文楷 Mono — Light / Regular / Medium。

显示预览字符串 `ABC 123　中文 日本語`，预览也必须先加载选中 face 的 WOFF2。默认 Sarasa Regular；
初次打开页面不得为六个选项同时下载字体。表单提交
face ID；字体目录失败时禁用创建/保存，不能提交默认猜测。配置页读取当前 face；若历史 face 在当前
catalog 缺失，显示阻断提示并允许停止态选择现有 face 修复。

运行态 select、字号、行高一起禁用。保存成功精确更新 Session query cache；同幂等 key 重试保持 face
不变。Open 前不再运行 canvas measure。

## 13. 安全、许可与运维

- 字体是构建期受信第三方资产，但仍分别校验 TTF/WOFF2 的格式、长度、digest、metadata 和度量等价；
  运行期不解析用户字体；
- API 字体 endpoint 只通过 catalog 中精确匹配的 WOFF2 内容摘要打开固定普通文件，不字符串 join 用户
  路径；设置 `nosniff`、`Content-Security-Policy: font-src 'self'` 与一年 `public immutable` 缓存；
- production image 不需要 apt 安装任何字体包；测试应在空 fontconfig cache/删除常见系统字体的镜像层
  仍通过，证明无宿主依赖；
- readiness 校验 catalog schema、六个文件 digest 和 libgdiplus 可加载；损坏字体使 ready 失败，live
  仍只表示进程存活；
- `/api/v1/version` 增加 font catalog digest，日志和兼容报告记录 face ID；
- 冷备份无需复制字体，它属于镜像版本；恢复旧 DataRoot 时镜像必须仍提供 Session 引用 face，否则
  对应 Session open 明确失败并可在停止态改选；
- 许可证全文进入源码和镜像，第三方声明列出版本、来源、规范 TTF 未修改、Web WOFF2 为完整格式转换及
  OFL-1.1/保留字体名称审查结论。

## 14. 实施切片

### Slice 1：字体导入与 ADR 供应链门

- 导入六个规范 TTF，锁定转换工具生成六个完整 WOFF2，纳入许可、双摘要 catalog、metadata/度量等价
  verifier；
- production/dev publish 分别保持 TTF 与 WOFF2 字节稳定；
- `verify-third-party.sh` 和镜像 smoke test。

通过条件：离线验证六 face、许可证、TTF/WOFF2 双 digest 与度量等价；任何 1 byte 篡改、family mismatch、
glyph/advance/positioning table 差异或非确定性转换都失败。

### Slice 2：Session persistence/API/UI 选择

- migration、DTO/application/runtime lease、font catalog 与内容寻址 WOFF2 API；
- create/configure 幂等与审计；
- Web 字体选择和预览。

通过条件：旧库回填，六项均可选择，运行态拒绝，未知 ID/同 key 异值稳定失败；初次页面只下载当前或
正在预览的 face，刷新命中 HTTP cache，WebSocket 重连无字体正文传输。

### Slice 3：Worker 字体 binding 与真实 StringMeasure

- bootstrap/catalog digest；
- Pfc/FontFactory/Config.FontName 初始化和释放；
- 删除客户端 textMetrics 和 headless heuristic；
- 游戏字体请求统一 fallback/诊断。

通过条件：无系统字体容器中六 face 均可初始化；Config.SetConfig 顺序、无 fallback、跨 Session cache
隔离和取消清理通过。

### Slice 4：Headless layout engine 与 PRINTC

- 复用上游 PrintStringBuffer/ConsoleDisplayLine 物理布局；
- PRINTC 实测修正；
- ButtonWrap、超宽、alignment、逻辑/物理行原子重排；
- 更新 MODIFICATIONS 和能力矩阵。

通过条件：真实 Runtime fixture 的物理行、x、width 与固定算法 golden 一致，PRINTC 全语义覆盖。

### Slice 5：协议 v6/v4 与 Web 权威渲染

- positioned segment、物理行 metadata、font binding；
- IPC/Realtime schema/generator/reducer；
- Font load barrier、绝对坐标、无 CSS wrap、水平滚动。

通过条件：旧 major fail closed；API mirror/reconnect Snapshot 等价；字体未加载时不首绘，加载后跨浏览器
换行与按钮盒一致。

### Slice 6：视觉、故障和完整质量门

- 六 face × 代表字号/行高 × desktop/mobile 浏览器 visual fixtures；
- catalog 损坏、字体 404、慢字体、Worker 初始化取消、超限物理行；
- 完整 check/production/acceptance 预跑。

通过条件：COMP-007/010 追踪矩阵完整，无系统/用户字体依赖，P1-15 可直接纳入发布验收。

## 15. 自动化测试矩阵

| 层级 | 正常路径 | 边界/失败路径 |
| --- | --- | --- |
| Font verifier | 六 face TTF/WOFF2 metadata/hash/metric/license | 篡改、错 family、表差异、非确定转换、缺 license、额外文件 |
| Persistence | 创建/更新/旧库回填 | 未知 ID、运行态、幂等异值、migration rollback |
| Runtime init | 六 face Config.DefaultFont/StringMeasure | Pfc fallback、digest mismatch、取消、连续不同 face |
| PRINTC | ASCII/CJK/混排左右对齐 | N=1/25、N+1、不可编码字符、超长、样式宽度修正 |
| Layout | fit、wrap、align、PointX | ±1px、ButtonWrap 两值、nobr、不可拆超宽、代理对 |
| Structured store | 多物理行原子 commit/reconnect | relayout 中失败、limits、裁剪完整逻辑组 |
| IPC/Realtime | v6/v4 roundtrip/golden | 旧 major、缺 x/width、非法 geometry、digest mismatch |
| Web | WOFF2 barrier/按需加载/缓存/绝对坐标/点击 | 404、digest 错、慢加载、fallback、重复传输、窄视口、focus/hit overlap |
| E2E/visual | 六 face 菜单与长行 | Chromium/Firefox/WebKit、mobile、zoom、重连 |

关键断言：

- 同一 fixture 在安装不同宿主字体、不同 locale/fontconfig cache 时输出完全相同的物理行 JSON；
- WOFF2 解码后的布局相关表与规范 TTF 等价，同一 face 的 Worker golden 与三浏览器 DOM 几何一致；
- 同一页面内多 Session 使用相同 face 只创建一个加载 promise；WebSocket 重连不请求字体，页面刷新在
  缓存有效时不传输字体正文，变更 digest 后只下载新 WOFF2；
- 浏览器 DOM 行数严格等于 Worker physical lines，不能因 CSS 多一行；
- 每个按钮 DOM `left/width` 等于协议值，点击只提交一次正确 value；
- glyph ink 可允许已批准的跨 rasterizer 视觉阈值，但断行、segment x/width、按钮命中不得有容差；
- PRINTC 的 Shift-JIS byte count 与原版基线逐例一致；
- `PrintCPerLine` 变化只改变 flush 点，`PrintCLength` 变化只改变字段格式化目标。

## 16. 验证命令

所有命令在 dev Docker 中运行：

```bash
./scripts/verify-runtime-fonts.sh
./scripts/verify-third-party.sh

source scripts/lib/dev-env.sh
docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore \
  --configuration Release --filter 'Category=FontLayout|Category=PrintCCompatibility'
docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore \
  --configuration Release --filter 'Category=PhysicalLayout|Category=ConsoleContract'
docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.Ipc.ContractTests --no-restore \
  --configuration Release --filter 'Category=StructuredLayoutV6'
docker compose -f docker/compose.dev.yml run --rm web \
  sh -c 'pnpm install --frozen-lockfile && pnpm typecheck:web && pnpm test:web && pnpm build:web'

./scripts/test-session-ui-e2e.sh
./scripts/test-production-image.sh
./scripts/check.sh
git diff --check
```

`check.sh` 中必须真实包含 `verify-runtime-fonts.sh`；专项脚本失败返回非零。若新增视觉更新命令，baseline
更新必须人工显式执行，普通 check 不可自动覆盖 golden。

## 17. 完成定义

- 六个 face 以固定版本、TTF/WOFF2 双 SHA-256、度量等价证明、完整 OFL/版权声明随源码和生产镜像分发；
- Session 创建/停止态配置可选择 face，旧 Session 有确定默认值，未知/损坏 face 不 fallback；
- `Config.FontName` 在 `Config.SetConfig` 前由已加载 face 覆盖，`Config.DefaultFont`/`StringMeasure` 是
  唯一文字测量来源；
- Headless 输出完整物理行、segment/button positionX 和 measuredWidth，ButtonWrap/超宽/对齐与上游
  fixture 一致；
- PRINTC/PRINTLC 的 Shift-JIS、N/N+1、实际宽度修正和 PrintCPerLine flush 全部有真实解释器测试；
- 浏览器只按需加载当前 face 的内容寻址完整 WOFF2 后才渲染，以一年 immutable HTTP cache 复用；
  WebSocket 重连不传字体，禁用自动换行，字体失败阻断而非 fallback；
- 游戏字体与系统字体不能影响 Runtime 或浏览器结果，相关请求有有界兼容诊断；
- IPC v6、Realtime v4、HTTP schema、migration、能力矩阵、MODIFICATIONS 和中英文需求追踪同步；
- `./scripts/check.sh`、字体/第三方验证、生产镜像、UI E2E 和 `git diff --check` 全部通过。

## 18. 实施与验证记录（2026-08-23）

已完成六个固定版本 Runtime face 的 TTF/WOFF2 资产目录、离线摘要与许可校验；Session `fontFaceId` 持久化、
HTTP 目录/字体资源接口、Worker catalog binding 和真实 `PrivateFontCollection`/`Config.DefaultFont` 测量链路；
基于实际字体宽度的物理行、positioned segment、ButtonWrap/超宽元素/对齐处理；PRINTC/PRINTLC 的 Shift-JIS
半角格、N/N+1 和实际像素字段修正；Realtime/IPC v6/v4 物理行协议；以及 Web 字体加载屏障、绝对坐标渲染和
Session 字体选择预览。游戏字体请求保持受控诊断，不参与 Runtime 排版。

验证结果：

- `./scripts/verify-runtime-fonts.sh`：6 faces、6 TTF、6 WOFF2 通过；
- `./scripts/verify-third-party.sh`、`./scripts/verify-emuera-capabilities.sh`、生成契约校验通过；
- `FontLayout|PrintCCompatibility` 专项测试 8/8；Realtime 49/49；RuntimeAdapter 182/182；
  RuntimeCompatibility 96/96；Worker 26/26；Infrastructure 125/125；API 集成 49/49；
- `./scripts/check.sh` 通过，包含 Release 构建、锁定还原、Web 类型检查、Web 99/99 测试和生产构建；
  真实 Chromium 在 `http://web` 非安全上下文中验证了无 `SubtleCrypto` 时的字体摘要回退与 FontFace 加载；
  P1-S04 像素级 E2E 还验证了物理行中的文字实际出现在 PNG 截图像素中，避免仅凭 DOM 可见性误判。

补充兼容性约束：`PRINT_COLORBAR` 是游戏 ERB helper，`BAR_LENGTH` 的格数取决于
helper 使用的字符 advance，不能仅按 Unicode code point 数量推断。固定字体验证表明
LXGW WenKai Mono 的 U+2585（`▅`）原始 advance 接近整格，需要在 Worker 和 Web 两端
按选中的 face 应用半格兼容修正；Sarasa Fixed SC 等 face 的原始 advance 保持不变。
