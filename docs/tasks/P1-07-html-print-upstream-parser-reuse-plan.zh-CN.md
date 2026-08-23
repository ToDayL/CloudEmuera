# P1-07 补充：HTML_PRINT 上游解析复用详细实现方案

状态：已实施（2026-08-17）

字体排版替代说明（2026-08-23）：HTML `font face` 仍由上游解析，但 P1-S04/ADR-0029 将其映射为当前
Session 的产品内置 face；不再解析 Session manifest 或部署/宿主字体。

设计日期：2026-08-17

对应开发步骤：P1-07 兼容性补充；实施完成后并入 P1-07 的 HTML 能力基线

关联需求：PLAY-001～003、PLAY-010、COMP-001～003、COMP-007、COMP-009、NFR-008、NFR-013

关联决策：[`ADR-0004`](../adr/0004-runtime-rich-content-allowlist.md)、
[`ADR-0005`](../adr/0005-vendored-emuera-source.md)、
[`ADR-0018`](../adr/0018-emuera-structured-interaction-model.md)、
[`ADR-0019`](../adr/0019-libgdiplus-mvp-graphics-compatibility.md)、
[`ADR-0024`](../adr/0024-html-print-upstream-parser-authority.md)

上游基线：`2175f8a629257efb08214e093704b3a3d3d06d05`

## 1. 目的与结论

原 headless `HTML_PRINT` 曾由 CloudEmuera 在 RuntimeAdapter 中重新实现 Emuera 伪 HTML 语法。固定
上游真正的语法、状态转换和错误行为位于 `Upstream/Emuera/UI/Game/HtmlManager.cs`；本次实施已移除
独立 parser，改为从该状态机抽取 `UpstreamHtmlFragment`，因此不会再与上游在闭合顺序、行尾省略、
属性词法、字符实体、按钮分段、`div` 递归和错误分类上漂移。

本方案把固定上游 `HtmlManager` 重构为两个阶段：

```text
原始 HTML_PRINT 字符串
        │
        ▼
上游语法与状态归约（唯一语法权威）
        │  UpstreamHtmlFragment：不含 DOM、URL、Graphics、Font 或 IPC 类型
        ├──────────────────────────────┐
        ▼                              ▼
原版桌面 materializer              CloudEmuera headless translator
ConsoleDisplayLine/StringMeasure    ConsoleNode/ConsoleLine
```

实施后的生产路径不得再用第二套 parser 判断某段 HTML 是否符合 Emuera 语法。CloudEmuera 的职责固定为：

1. 在解析前施加资源消耗上限；
2. 调用固定上游解析状态机；
3. 把上游语义结果转换为封闭、可校验的结构化 Console 类型；
4. 解析逻辑资源名并执行安全、容量和协议验证；
5. 保证原始 HTML、浏览器 URL、脚本、事件属性和桌面对象不跨出 Worker。

该方案追求固定上游的**语法和可观察状态兼容**。浏览器字体 shaping 与 Windows GDI 的像素级结果属于
单独的布局兼容问题；不得用“已复用 parser”宣称所有平台上的逐像素绘制完全一致。

## 2. 当前基线与问题证据

### 2.1 已经具备的复用条件

- `CloudEmuera.EmueraRuntime.UpstreamHeadless.csproj` 已编译 `Upstream/Emuera/UI/Game/**/*.cs`，因此
  `HtmlManager`、`ConsoleButtonString`、`ConsoleImagePart`、`ConsoleShapePart`、`PrintStringBuffer`
  等固定上游代码已经位于 headless 上游程序集中；不需要复制另一份源码或引入外部包。
- `HTML_TAGSPLIT`、`HTML_STRINGLEN`、`HTML_SUBSTRING`、`HTML_STRINGLINES`、
  `HTML_GETPRINTEDSTR`、`HTML_TOPLAINTEXT` 和 `HTML_ESCAPE` 等解释器功能已经直接调用上游
  `HtmlManager`。只有 headless 可见输出 `HeadlessEmueraConsole.PrintHtml` 绕过了它。
- RuntimeAdapter 已有 `TextNode`、`ButtonNode`、`SpriteNode`、`ShapeNode`、`DivNode`、
  `ConsoleTextStyle`、`ConsoleLineAlignment`、`ConsoleBoxModel` 和统一 limits/validator，可作为稳定输出边界。

### 2.2 不能继续维护双 parser 的原因

固定上游不是通用 XML/HTML 栈模型：

- `b/i/u/s` 由独立 `FontStyle` 位控制，闭合时检查并翻转对应位；它不要求严格后进先出；
- `font` 使用独立列表，闭合一个 `font` 就弹出一层；
- `button/nonbutton` 使用当前按钮状态，并在闭合时触发一次分段；
- `p/nobr` 使用开始、已闭合等独立标记，且仅这两类允许在 fragment 行尾省略闭合标签；
- `br`、字符串中的换行、按钮闭合和 fragment 结束都会影响上游按钮/行分段；
- `div` 会递归进入新的局部解析状态，同时继承部分外层状态；
- 属性由上游 `LexicalAnalyzer`/`WordCollection` 解析，不等价于普通 HTML 属性扫描器。

当前 `EmueraHtmlParser` 使用严格 `TagFrame` 栈。继续逐个添加例外会复刻越来越多
`HtmlManager` 的隐含行为，仍无法证明与上游完全相同。

### 2.3 上游实际允许的闭合省略范围

固定上游源码明确只允许 fragment 尾部省略 `</p>` 和 `</nobr>`。`button/nonbutton`、`font`、
`b/i/u/s`、`clearbutton` 和 `div` 仍需满足各自闭合条件。测试和文档不得把“允许省略闭合标签”扩大为
浏览器 HTML 的任意自动补全规则。

## 3. 范围与非目标

### 3.1 必须覆盖

- `HTML_PRINT str[, option]` 的普通输出和 `toPrintBuffer` 两条路径；
- `HTML_PRINT_ISLAND` 使用的上游 Emuera 伪 HTML 语法；
- `p/nobr/b/i/u/s/br/font/button/nonbutton/clearbutton/img/shape/div`；
- 注释、实体、单引号属性、大小写、空白、换行、数值和颜色；
- 上游接受、拒绝及错误位置可观察行为；
- 按钮 generation、value 类型、title、`pos`、clearbutton 和 selection color；
- 图片普通/hover/mapping 资源及 `height/width/ypos`；
- Shape 和 `div` 的 MixedNum、盒模型、层级、相对/绝对布局；
- 输入长度、标签数、递归深度、输出节点数、文本长度和几何范围上限；
- RuntimeAdapter、IPC、API mirror、Web renderer 已冻结结构化契约的无损保持。

### 3.2 非目标

- 不把 raw HTML 传给 API 或浏览器；
- 不使用浏览器 `innerHTML` 模拟 Emuera 容错；
- 不把 WinForms、`Graphics`、`Font`、`Bitmap`、`StringMeasure` 或上游 UI 对象加入 RuntimeAdapter；
- 不顺带引入上游没有的 `strong/em/strike` 等别名；只有固定上游实际接受时才可支持；
- 不在本任务中替换所有字体 shaping 或解决所有 Windows/Linux 字体差异；
- 不改变 `HTML_TAGSPLIT` 等上游 helper 的脚本可见返回值，除非差分测试证明当前 headless 已偏离固定上游；
- 不把安全拒绝伪装成上游语法错误，也不把上游语法错误静默显示为正常富文本。

## 4. 开始实施前的决策更新

ADR-0004 当前把 RuntimeAdapter 中的 `EmueraHtmlParser` 指定为语法权威，并规定某些 malformed 输入整体
回退为普通文本。复用上游后，语法权威和错误来源发生变化。实现代码前必须新增一份 ADR（建议
`ADR-0024：HTML_PRINT 上游解析权威与安全翻译边界`），并让它明确替代 ADR-0004 的以下部分：

1. 固定上游 `HtmlManager` 是 `HTML_PRINT`/`HTML_PRINT_ISLAND` 语法、闭合和属性解释的唯一权威；
2. RuntimeAdapter 仍是跨进程结构化数据的安全权威，但不再重新解析 Emuera 源字符串；
3. 上游语法错误保持上游错误行为；CloudEmuera 容量/资源/协议拒绝使用独立稳定 reason code；
4. 缺失或不允许的图片资源如何映射为原版 alt 文本、兼容诊断或阻断错误；
5. parser 复用不改变 PLAY-001/003：raw HTML、URL 和可执行 DOM 仍不得跨边界；
6. `HTML_PRINT_ISLAND` 是否与桌面原版一样使用 `HtmlManager`。固定上游代码如此执行，因此默认答案应为是；
7. 像素排版兼容与语法兼容分开验收。

ADR 接受前可以编写 characterization test 和内部原型，但不得删除生产 parser 或改变对外错误语义。

## 5. 目标依赖与程序集边界

依赖方向保持：

```text
CloudEmuera.RuntimeAdapter
          ▲
          │ 结构化 Console 契约
CloudEmuera.EmueraRuntime.UpstreamHeadless
          ▲
          │ runtime host
CloudEmuera.EmueraRuntime / Worker
```

具体规则：

- 中立上游 IR 与 parser budget 类型放在 `Upstream/Emuera/UI/Game/` 或
  `UpstreamHeadless/Html/`，由 `UpstreamHeadless` 程序集拥有；
- RuntimeAdapter 不得引用 `UpstreamHeadless`；
- 上游语法阶段不得引用 RuntimeAdapter、IPC、API 或 Web 类型；
- Cloud translator 位于 `UpstreamHeadless`，可以同时看到中立上游 IR 和 RuntimeAdapter；
- `HeadlessEmueraConsole` 只负责编排、pending line 和 transaction 提交，不再包含标签语法规则；
- IPC/API/Web 不增加 raw fragment 字段，只消费已有封闭节点。

## 6. 上游抽取设计

### 6.1 两阶段重构，不复制 parser

把 `HtmlManager.html2DisplayLine` 拆成以下内部职责：

```text
ParseFragment(str, parseOptions, budget) -> UpstreamHtmlFragment
MaterializeForDesktop(fragment, StringMeasure, EmueraConsole) -> ConsoleDisplayLine[]
MaterializeButtonsForDesktop(fragment, EmueraConsole) -> ConsoleButtonString[]
```

`ParseFragment` 必须直接承接当前 `html2DisplayLine/tagAnalyze/cssToButton` 的状态机；不得把
`tagAnalyze` 复制到新文件后留下旧实现。原版桌面入口 `Html2DisplayLine/Html2ButtonList` 改为调用同一个
`ParseFragment`，然后 materialize。这样可以用测试证明桌面入口的语法行为没有因抽取而改变。

推荐先做机械重构：保持分支、异常类型、错误消息和执行顺序，暂不“清理”命名、goto、状态对象或词法
分析代码。行为锁定后再做局部整理。

### 6.2 中立 IR

建议新增 internal 类型，最终命名可以调整，但必须能无损表达以下信息：

```text
UpstreamHtmlFragment
├── alignment                 # LEFT/CENTER/RIGHT
├── noWrap
├── segments[]                # 保留按钮闭合和 br 造成的分段
│   ├── UpstreamHtmlSegment?
│   │   ├── interactive/value(int|string)?/title?/position?
│   │   └── parts[]
│   │       ├── Text(text, style)
│   │       ├── Image(resource/srcb/srcm/raw dimensions)
│   │       ├── Shape(type/raw MixedNum/color/bcolor)
│   │       └── Div(layout/box/child fragment)
│   └── null 或显式 Break       # 原版 br/换行边界
└── wrapperCloseState         # 仅在 materializer 确有需要时保留
```

IR 约束：

- 文本 style 使用上游颜色值、FontStyle、font name、button color 等普通值的快照，不能持有可变
  `HtmlAnalzeState`；
- 图片保留 `MixedNum` 的数值和 `isPx`，不要在 parser 中提前读取 manifest 或计算目标 rect；
- 按钮 value 必须保留上游判定的 integer/string 类型和原始规范值；
- `clearbutton` 直接反映到 segment 的 interactive/title 结果，不需要作为跨边界节点；
- `br` 必须是显式边界，不能只生成一个普通文本 `"\n"`；
- `div` child 使用相同 IR 递归表达，不提前构造 `ConsoleDisplayLine[]`；
- IR 不公开到其他程序集；它是 vendored parser 和 Cloud translator 之间的内部接缝，不是新产品协议。

如果实现者发现 `wrapperCloseState`、`LastButtonTag` 或其他状态对 desktop materializer 仍有影响，应扩展
IR 保存结果，而不是让 materializer重新读取或重新解析源字符串。

### 6.3 Parser options

需要明确区分原版两个入口：

- `DisplayLines`：对应 `Html2DisplayLine`，允许后续执行上游测量、折行和对齐；
- `PrintBufferParts`：对应 `Html2ButtonList`，只把解析出的 segment 追加到现有打印缓冲，不提前提交行。

`HTML_PRINT` 的 option 必须选择相同模式。`toPrintBuffer=true` 时不得因为 Cloud translator 方便而强制
flush、丢失当前 PRINT/PRINTC 缓冲、提前应用对齐或增加空行。

### 6.4 Budget 注入

原版 parser 不是为不受信任的大输入设计的。抽取时增加可选 budget，但 budget 只能限制资源消耗，
不能改变正常输入语法：

```text
maxInputUtf16Units
maxTagCount
maxDivDepth
maxSegments
maxParts
maxTextUtf16Units
```

- desktop 原入口可以使用兼容默认值；Cloud 入口必须由 `ConsoleContractLimits` 显式转换；
- 在递归进入 `div` 前检查深度，避免先递归后由 RuntimeAdapter validator 拒绝；
- 在创建 segment/part 和追加文本时累计预算，避免先分配无界对象图；
- budget 异常使用新的内部异常类型，translator 映射为稳定 Cloud reason code；不得伪装为上游 `CodeEE`；
- 解析前还要执行输入总长检查，保证 fallback/诊断路径不会再次复制超限字符串。

### 6.5 上游文件修改规则

预期需要修改或新增：

- `Upstream/Emuera/UI/Game/HtmlManager.cs`：抽取共同 parser 与 desktop materializer；
- 可选 `Upstream/Emuera/UI/Game/HtmlSemanticModel.cs`：中立 IR 和 budget；
- `Upstream/Emuera/UI/Game/PrintStringBuffer.cs`：若 materializer 需要接收 IR segment；
- `UpstreamHeadless/UpstreamHtmlTranslator.cs`：Cloud 安全转换；
- `UpstreamHeadless/HeadlessEmueraConsole.cs`：切换生产调用路径；
- `CloudEmuera.EmueraRuntime/MODIFICATIONS.md`：记录原因、范围和验证；
- `RuntimeBaseline`/integration version：按 ADR-0005 更新。

所有 `Upstream/` 修改必须保留显著 CloudEmuera modification 注释。不要把大段上游代码移动到
RuntimeAdapter，否则会破坏来源边界和未来上游升级的可比性。

## 7. Headless 安全翻译

### 7.1 Translator 接口

建议把生产入口收敛为一个单用途组件：

```text
UpstreamHtmlTranslator.Translate(
    UpstreamHtmlFragment fragment,
    HtmlTranslationContext context)
        -> TranslatedHtmlFragment
```

`HtmlTranslationContext` 至少包含：

- `ConsoleContractLimits`；
- 当前 font size/line height/default foreground/focus color；
- manifest-backed sprite resolver；
- 当前按钮 generation；
- 逻辑 viewport/layout 参数；
- 输出模式 `DisplayLines` 或 `PrintBufferParts`。

`TranslatedHtmlFragment` 至少保存：

- 一组显式行/inline 操作或带 break 的 node segment；
- 每行 alignment/noWrap；
- 已完成 generation stamping 的嵌套按钮；
- 转换诊断；
- 估算节点数、文本量和字节数，便于提交前复核。

Translator 必须是纯转换器：不分配 sequence、不提交 transaction、不写 Console state，也不持有
Worker/IPC 对象。提交仍由 `HeadlessEmueraConsole` 完成。

### 7.2 类型映射

| 上游 IR | RuntimeAdapter | 转换规则 |
| --- | --- | --- |
| Text/style | `TextNode(ConsoleTextStyle)` | 颜色、装饰、逻辑字体、button color 无损映射；控制字符和长度由 validator 检查 |
| Break | 行提交边界 | 对 display 模式提交当前物理行；对 print-buffer 模式保留上游 buffer 语义 |
| interactive segment | `ButtonNode` | 保留 int/string value、title、enabled、position、当前 generation；children 可含安全 text/sprite/shape/div |
| nonbutton segment | 普通 children 或 disabled `ButtonNode` | 以既有结构化契约能够保留 title/position 的形式映射；不得虚构可点击输入 |
| Image | `SpriteNode` | 通过 manifest resolver 得到 assetId/source rect/frame；按上游 MixedNum/负尺寸规则计算 destination |
| Shape | `ShapeNode` | 只映射固定上游实际创建成功的 shape；保留 fill/button color 和原始几何语义 |
| Div | `DivNode` | 递归翻译 child fragment；保留 bounds、depth、background、relative/absolute 和 box model |
| p/nobr | `ConsoleLine.Alignment/NoWrap` | 作为行元数据，不生成 DOM 标签 |

不要通过 `AltText` 或 `ToString()` 重新解析上游显示对象。若 IR 缺少字段，应扩展 IR；字符串 round-trip
会重新引入转义、大小写、数值格式和闭合差异。

### 7.3 图片资源和缺失资源

上游 `ConsoleImagePart` 会把 `src/srcb/srcm` 当作 Emuera 资源名，而不是浏览器 URL。Cloud translator：

1. 永远只把它交给 Session manifest resolver；
2. resolver 成功时生成 `SpriteNode`，协议只携带 `assetId`；
3. resolver 失败时按新 ADR 冻结的固定行为生成安全 `TextNode` alt 表示或兼容诊断；
4. 即使值形似 `https://`、`data:` 或宿主绝对路径，也绝不能把它交给网络、文件系统通用路径或 DOM；
5. hover/mapping 资源分别解析并校验尺寸，不能因主图合法就信任附属资源；
6. MixedNum、负尺寸、默认宽高比、ypos 和动画 frame 继续使用已有受测转换逻辑。

安全边界禁止的是“获得 URL/路径能力”，不是要求第二套 parser 重新定义 `src` 的词法。

### 7.4 字体

上游 `font face` 是游戏提供的字体名。translator 只能映射到 Session manifest 或部署策略允许的逻辑
font family；不得触发任意宿主字体/文件查找。建议区分：

- 已授权字体：保留逻辑 family；
- 上游可接受但宿主未授权字体：使用确定 fallback，并产生兼容诊断；
- 超长、控制字符或非法标识：Cloud 安全拒绝。

字体策略失败不是上游语法错误，必须使用独立 reason code。

### 7.5 原子性与提交

- 整个 fragment 必须先完成上游解析、Cloud 转换和 RuntimeAdapter 验证，再修改 Console state；
- 任一转换失败不得提交部分节点、消费 sequence 或遗留半行 pending state；
- `toPrintBuffer=false` 保持原版先 flush 既有 buffer、再提交 HTML lines 的边界；
- `toPrintBuffer=true` 只追加到当前 pending buffer，等待后续 `PRINTL/NewLine/PrintFlush`；
- 多行 fragment 可以产生多个连续 transaction，但构造阶段必须先全部成功；如现有 Console 契约支持
  单事务多行，优先一次原子提交；
- generation 在成功转换时从当前 console 捕获，递归 `DivNode` 内按钮使用同一 generation。

## 8. 错误与诊断模型

至少区分三类失败：

| 类别 | 来源 | 行为 |
| --- | --- | --- |
| 上游语法/语义错误 | `HtmlManager`/`CodeEE` | 保持固定上游错误类型、消息语义和脚本执行结果 |
| Cloud 安全/资源拒绝 | manifest、font policy、路径/资源、未知 IR 类型 | 稳定 compatibility/security reason code；无 raw fragment 日志 |
| Cloud 容量拒绝 | parser budget、Console limits、IPC limits | 稳定 limit reason code；不部分提交、不复制超限全文 |

不得把所有失败统一包装为 `NotSupportedException("outside allowlist")`，否则会丢失原版可观察错误，并使
兼容性报告无法区分语法缺陷与部署资源问题。

建议新增或确认以下稳定 reason code：

```text
EMUERA_HTML_SYNTAX_ERROR
EMUERA_HTML_INPUT_LIMIT
EMUERA_HTML_TAG_LIMIT
EMUERA_HTML_DEPTH_LIMIT
EMUERA_HTML_OUTPUT_LIMIT
EMUERA_HTML_RESOURCE_NOT_FOUND
EMUERA_HTML_RESOURCE_NOT_ALLOWED
EMUERA_HTML_FONT_FALLBACK
EMUERA_HTML_TRANSLATION_UNSUPPORTED
```

日志只记录 code、session/worker/epoch、输入长度、标签/节点计数和不可逆摘要；默认不记录完整 fragment、
按钮 value、title、物理路径或资源原始内容。

## 9. HTML_PRINT_ISLAND

固定上游 `EmueraConsole.PrintHTMLIsland` 调用 `HtmlManager.Html2DisplayLine`，因此 island 的语法兼容入口
也应使用同一个 `ParseFragment`。当前 `SafeHtmlIslandParser` 是另一套通用安全 AST parser，不能继续作为
固定上游语法权威。

推荐映射：

- 上游 fragment 先转成相同的安全 `ConsoleNode`/行语义；
- 再转换为 `HtmlIslandNode` 或给 `HtmlIslandDrawable` 使用的封闭子树；
- island 的当前状态/upsert/clear 语义保持 ADR-0018；
- 如果 island 契约不能表达上游 button、shape、div 或多行状态，应先扩展封闭 AST/scene 契约并升级
  对应 IPC/realtime schema，不得丢字段或退回 raw HTML；
- 删除生产 `SafeHtmlIslandParser` 前，分类其测试：通用 XSS/limit 向量迁移到 translator/validator，
  上游不存在的标签测试删除或改为“按上游拒绝”。

如果产品决定 HTML Island 保留一个不同于原版的安全子集，必须在新 ADR 中明确标为兼容偏差，不能仍把
它登记为固定上游完全 Supported。

## 10. 布局与测量边界

### 10.1 语法抽取不能丢失的布局语义

- `p align`；
- `nobr`；
- 显式 `br` 和字符串换行；
- `button pos`；
- `img/shape` 目标尺寸和垂直范围；
- `div` bounds、box model、depth、relative/absolute；
- `toPrintBuffer` 与既有 PRINT/PRINTC 缓冲关系。

### 10.2 暂不宣称完全相同的像素结果

`PrintStringBuffer.ButtonsToDisplayLines` 依赖 `StringMeasure`、配置、字体和 GDI/libgdiplus 做自动折行。
浏览器 DOM 的字体 shaping 不会天然产生相同像素宽度。因此验收拆成两层：

1. 本任务阻断条件：语法接受/拒绝、结构节点、显式换行、nowrap、对齐、位置和资源几何一致；
2. COMP-007/P1-15：相同字体资产和 viewport 下的宽度、自动折行、按钮命中和视觉截图回归。

如果游戏逻辑通过 `HTML_STRINGLEN`、`HTML_SUBSTRING` 或 printed-line readback 依赖测量结果，继续由固定
上游 helper/布局状态回答，不能让浏览器反向成为解释器权威。需要新增测试证明 helper 结果与最终
结构化行状态没有自相矛盾。

## 11. 测试策略

### 11.1 Characterization tests 先行

改代码前先针对当前固定上游 `HtmlManager` 建立测试表，记录接受、IR/显示对象结果或 `CodeEE`：

- 尾部省略 `p`、`nobr`、两者组合；
- 不允许省略 `b/i/u/s/font/button/nonbutton/clearbutton/div`；
- 非 LIFO 样式闭合，例如 `<b><i>x</b></i>`；
- 重复样式、意外闭合、闭合后继续文字；
- 单/双引号、标签和属性大小写、等号两侧空白；
- 注释、换行、所有合法/非法实体；
- button int/string value、title、pos、empty body、嵌套限制；
- nonbutton/clearbutton/notooltip；
- img 缺失/重复属性、srcb/srcm、px/百分比/零/负数；
- shape 每种上游实际支持参数和失败形式；
- div 每种尺寸别名、盒模型、嵌套/未闭合、child break；
- `toPrintBuffer=true/false` 与前后 PRINT/PRINTC/PRINTL 组合。

这些测试必须调用真实上游入口，不得用当前 RuntimeAdapter parser 生成 expected。

### 11.2 Desktop materializer 回归

对抽取前后建立 golden 结果，至少比较：

- 行数、alignment、logical-line 标志；
- segment/button 数、顺序、value 类型、title、pos；
- text/style；
- image/shape/div 类型及原始参数；
- 异常类型和错误消息键。

若 CI 无法运行 Windows 绘制，只比较测量前的 semantic IR 和不需要 GDI 的 materializer 结果；像素 golden
放入明确的平台 fixture，不要让宿主字体偶然变化破坏语法测试。

### 11.3 Translator 契约测试

- 每种 IR 类型正常、边界和未知 subtype；
- manifest sprite、hover、mapping、动画、缺失和非法资源；
- 字体授权/fallback；
- generation 递归 stamping；
- input/tag/depth/segment/part/text/output/geometry limits；
- 上游 parse 成功而 Cloud policy 拒绝时不产生节点；
- 转换中途失败时 pending buffer、Snapshot、sequence 和 history 均不变；
- 不出现 raw HTML、URL、宿主路径、`System.Drawing` 或上游 UI 类型。

### 11.4 差分与属性测试

建立共享语料 `tests/fixtures/html-print/`，每项记录：

```text
id
fragment
mode
upstreamOutcome             # accepted / CodeEE key
semanticProjection          # 稳定、无像素依赖的规范 JSON
cloudOutcome
notes
```

差分断言：

- 上游接受的安全可表达输入，Cloud 必须接受且 semantic projection 等价；
- 上游拒绝的输入，Cloud 不得当作正常富内容接受；
- Cloud 额外拒绝只能来自已登记的容量、安全或资源 reason code；
- parser 抽取前后的上游 outcome 不变。

可在受限长度内生成标签/属性组合做 property/fuzz 测试，但 expected 必须由固定上游入口决定，不能由
浏览器或第二套 parser 决定。随机种子和失败样本必须可复现。

### 11.5 真实解释器测试

扩展 `CloudEmuera.RuntimeCompatibility.Tests`，至少包含一个 ERB 场景同时验证：

- 合法省略 `p/nobr`；
- 上游特有闭合顺序；
- button 输入后 RESULT/RESULTS；
- img/shape/div；
- `HTML_PRINT` option 的 buffer 行边界；
- `HTML_GETPRINTEDSTR`/`HTML_STRINGLEN` 与可见结构状态；
- syntax error 和 Cloud limit/resource error 的不同诊断。

测试不能只用 transcript 包含字符串作为成功标准；还要断言 `ConsoleSnapshot` 行、节点、样式、按钮、
资源和 sequence。

### 11.6 IPC、API 和 Web 回归

如果本任务不新增结构化字段，现有 v3/realtime round-trip 测试仍必须全量通过。若为保留上游语义必须
新增字段，则必须同步：

- `structured-worker.proto` 与协议 limits；
- Realtime contracts/schema/generated TypeScript；
- Worker mapper、API mirror/reducer；
- Web codec、Safe renderer 和 CSS；
- capability digest、协议测试和浏览器组件测试。

任何未知节点在所有边界继续 fail closed。

## 12. 分阶段实施步骤

### 切片 0：冻结决策和基准

1. 新增 ADR-0024；
2. 记录当前 HTML capability、协议和 integration version；
3. 建立上游 characterization corpus；
4. 给现有失败实例增加最小回归测试；
5. 确认本任务是否同时迁移 HTML Island。

完成条件：未修改生产路径，但能够自动证明当前上游 outcome 和现有 Cloud outcome 的差异。

### 切片 1：机械抽取上游 parser

1. 新增中立 IR 和 parser budget；
2. 将 `html2DisplayLine/tagAnalyze/cssToButton` 的语法阶段抽到 `ParseFragment`；
3. 让原 `Html2DisplayLine/Html2ButtonList` 通过 IR materializer 返回原有类型；
4. 保持 helper 函数和所有错误行为；
5. 跑 characterization 和 desktop materializer golden。

完成条件：Cloud 生产路径尚未切换，固定上游入口抽取前后结果一致。

### 切片 2：实现 Cloud translator

1. 新增 `UpstreamHtmlTranslator` 和 context/result；
2. 完成 text/style/button/image/shape/div/break 映射；
3. 接入 manifest、字体策略、generation 和 limits；
4. 实现稳定错误分类和原子转换；
5. 添加 Architecture 测试禁止上游/UI 类型逃出程序集。

完成条件：translator 契约测试和攻击语料通过，但生产仍可用 feature switch 或测试专用双跑比较。

### 切片 3：双跑差分

仅在测试或显式诊断构建中对同一 fragment 同时执行：

- 上游 parser + translator；
- 旧 `EmueraHtmlParser`。

记录规范 semantic projection 差异，不在生产重复执行，不记录原文。逐项分类为旧 parser bug、上游
抽取 bug、结构化契约缺字段或批准的安全偏差。

完成条件：共享合法语料中没有未解释差异；安全偏差均有 ADR reason code。

### 切片 4：切换 HTML_PRINT 生产路径

1. `HeadlessEmueraConsole.PrintHtml` 改用上游 parser + translator；
2. 分别验证 display 和 print-buffer 模式；
3. 保持 pending line、flush、generation 和 transaction 原子性；
4. 删除生产对 RuntimeAdapter parser 的调用；
5. 跑真实解释器、Worker、API、Web 回归。

完成条件：固定上游合法语料全部由上游权威解析，生产不存在第二次语法判断。

### 切片 5：迁移 HTML Island

1. 用同一上游 parser 生成 island 安全结构；
2. 补足必要的封闭 AST/scene 表达；
3. 迁移 SafeHtmlIslandParser 的安全测试；
4. 切换 `PrintHTMLIsland`，验证 clear/upsert/Snapshot/IPC/Web；
5. 删除未再使用的生产 parser。

如果新 ADR 明确批准 island 偏差，此切片改为登记偏差和能力降级，不能静默跳过。

### 切片 6：清理、能力门和文档

1. 删除或收缩 `EmueraHtmlParser`/`SafeHtmlIslandParser`：保留的类只能做结构验证或测试工具，名称不得
   暗示它仍是 Emuera 语法权威；
2. 更新 `runtime-capabilities.json` 的 adapterTypes/testNames/securityNotes 和 digest；
3. 更新 ADR-0004、ADR-0018、P1-07 计划、设计和兼容报告；
4. 更新 `MODIFICATIONS.md`、integration version、fixture manifest 与第三方验证；
5. 运行完整 dev Docker 验证。

完成条件：源码、能力矩阵和文档只声明一个 HTML_PRINT 语法权威。

## 13. 建议的文件级变更清单

| 文件/目录 | 预期动作 |
| --- | --- |
| `docs/adr/0024-*.md` | 新决策：上游 parser 权威、安全翻译和错误语义 |
| `docs/adr/README.md` | 登记 ADR |
| `Upstream/Emuera/UI/Game/HtmlManager.cs` | 抽取共同语法阶段，保留原 desktop API |
| `Upstream/Emuera/UI/Game/HtmlSemanticModel.cs` | 可选：中立 IR、options、budget |
| `Upstream/Emuera/UI/Game/PrintStringBuffer.cs` | 必要时接收 materialized segments，保持折行行为 |
| `UpstreamHeadless/UpstreamHtmlTranslator.cs` | 新增安全 translator |
| `UpstreamHeadless/HeadlessEmueraConsole.cs` | 切换 HTML_PRINT/Island 编排 |
| `RuntimeAdapter/Console/EmueraHtmlParser.cs` | 退出生产语法权威；迁移后删除或改为结构 validator |
| `RuntimeAdapter/Console/*Node*.cs` | 仅在上游语义确实缺字段时扩展 |
| `tests/fixtures/html-print/` | 新增差分语料和规范 projection |
| RuntimeAdapter/Compatibility/Worker/Web tests | 增加 translator、真实解释器和 round-trip 证据 |
| `docs/runtime-capabilities.json` | 更新 HTML/Island 能力证据和 digest |
| `CloudEmuera.EmueraRuntime/MODIFICATIONS.md` | 记录上游修改 |
| Runtime baseline/fixture manifests | 更新 integration version |

实现者必须先检查工作树中的并行修改，尤其是 HTML node、IPC/realtime schema 和 Web renderer；不得覆盖
用户或其他 agent 的未提交变更。

## 14. 验证命令

所有构建和测试必须通过 dev Docker；不要直接使用宿主 `dotnet`/`pnpm`。

定向验证建议：

```bash
./scripts/dev-up.sh

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore --configuration Release \
  --filter "Category=ConsoleContract|Category=RichOutput|Category=Architecture"'

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore --configuration Release \
  --filter "Category=RuntimeBridge|Category=EmueraFeatureMatrix"'

bash -lc 'source scripts/lib/dev-env.sh && docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release \
  --filter "Category=ConsoleProtocol|Category=Snapshot"'

./scripts/verify-runtime-fixtures.sh
./scripts/verify-emuera-capabilities.sh
./scripts/verify-third-party.sh
./scripts/check.sh
git diff --check
```

实施完成后运行 `./scripts/dev-down.sh` 停止环境。

## 15. 完成定义

只有以下条件全部满足，才能把本补充任务标记为 DONE：

1. ADR-0024 已接受，明确固定上游 parser 是唯一语法权威；
2. desktop `Html2DisplayLine/Html2ButtonList` 与 headless 使用同一个 `ParseFragment`；
3. 上游 parser 抽取前后的 characterization outcome 无未解释变化；
4. `HTML_PRINT` 普通/print-buffer 两条生产路径不再调用第二套 Emuera 语法 parser；
5. 合法省略 `p/nobr`、交错样式状态及其他固定上游边缘行为有真实解释器回归；
6. 上游语法错误、Cloud 安全拒绝和容量拒绝分类稳定且不部分提交；
7. raw HTML、URL、宿主路径、桌面/UI 对象不进入 RuntimeAdapter、IPC、API 或 Web；
8. 图片、字体、Shape、div 和按钮经 manifest/limits/validator 安全转换且字段无损；
9. HTML Island 已迁移到同一权威，或有 ADR 批准并在能力矩阵中显式登记的兼容偏差；
10. helper 函数、printed readback、pending line、sequence、Snapshot 和 reconnect 状态没有回归；
11. 能力矩阵、digest、integration version、`MODIFICATIONS.md`、ADR 和设计文档同步；
12. 第 14 节定向与完整验证全部通过。

## 16. 实施者注意事项

- 首先写 characterization test，再移动上游代码；不要凭浏览器 HTML 常识改写上游行为。
- 不要把 `Html2DisplayLine(fragment, null, headlessConsole)` 直接作为最终实现：`StringMeasure`、折行、
  `div` 和图片错误路径仍可能解引用桌面/GDI 状态。
- 不要从 `ConsoleImagePart.AltText`、`ConsoleShapePart.ToString()` 或 `ConsoleDivPart.ToString()` 反解析；
  这只是序列化展示，不是稳定结构接口。
- 不要在 RuntimeAdapter 中引用上游 IR；translator 应消化这一边界。
- 不要为了通过现有测试保留双 parser 的交集语义；测试 expected 应改由固定上游 characterization 决定。
- 不要把所有未解析资源视为 XSS。资源名只有在被错误地赋予 URL/路径能力时才构成该风险；安全 fallback
  可以保留原版可见行为。
- 修改 `Upstream/` 必须更新 `MODIFICATIONS.md`、integration version 和固定上游验证记录。
- 如果抽取暴露新的产品/安全决策，例如缺失图片应显示 alt 还是终止运行，先停下并补 ADR，不要在
  translator 中用临时代码固化行为。
