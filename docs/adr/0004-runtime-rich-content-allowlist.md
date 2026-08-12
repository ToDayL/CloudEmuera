# ADR-004：Runtime 富内容最小 allowlist

状态：Accepted  
日期：2026-08-04

## 背景

上游 Emuera 的部分输出带有 HTML 风格标记，而 CloudEmuera Console 不能把浏览器 HTML、CSS、URL 或事件处理器泄漏给 Web/API 层。P0-03 只需要覆盖 P0-01 fixture 使用的粗体和斜体，并为按钮、图片保留独立的结构化入口。

## 决定

- `EmueraHtmlParser` 只接受 fragment，线性扫描并限制输入长度、标签数和嵌套深度；不构造 DOM、不调用浏览器解析器、不使用正则表达式解析。
- parser 实例注入的 `ConsoleContractLimits` 同时用于解析过程、解析结果节点校验和 fail-closed fallback；不使用固定的默认 limits 绕过调用方的批次、文本、标签或嵌套上限。
- 第一批 allowlist 为无属性 `<b>`、`<i>`、`<br>`；同时批准语义等价的无属性 `<strong>`、`<em>`、`<u>`、`<s>` 和 `<strike>`。标签名按 ASCII 大小写不敏感处理；允许 `<br/>` 作为 `<br>` 的等价写法。
- 允许标签带任何未知属性、事件属性、`style`、`src`、`href`、`srcset` 或 URL-like 值时，整个 fragment fail-closed 为一个普通 `TextNode`（超出安全文本预算则以稳定契约异常拒绝）。未知标签、错误闭合、未闭合、超深或超限 fragment 同样不部分解析。
- 实体解码后的普通文字只生成受限 `TextNode`；样式栈只生成 `ConsoleFontStyle`。HTML 不创建按钮、图片、媒体、链接、脚本或 URL 字段。
- 图片必须由 `ImageNode(ConsoleAssetId, ...)` 创建，asset id 只能是受限的 manifest 逻辑键；按钮必须由 `ButtonNode` 创建，label 只允许 `TextNode`。Sprite、CBG、音频和 CSS 仍为 Deferred/Experimental，不进入 P0-03 显示树。

## 备选方案

- 使用 `innerHTML` 后删除危险节点：浏览器解析和属性兼容行为过大，容易在删除逻辑前产生副作用，且会引入 DOM/UI 依赖。
- 接受任意 HTML 并交给 Web renderer 做消毒：把上游安全边界推迟到另一个进程，无法证明 RuntimeAdapter 的公共契约没有 URL/事件能力。
- 让 `<img>`/`<a>` 直接映射为节点：会把资源加载和导航语义混入本地 Console，超出 P0-03 的需求。

## P1-07 扩展

ADR-0018 将本 ADR 的 fragment allowlist 扩展到结构化 HTML Island：允许的标签、文本、换行、有限
样式和 manifest `assetId` 仍先解析为 executable-free AST，再进入 `HtmlIslandNode` 或
`HtmlIslandDrawable`。扩展不改变本 ADR 的边界：原始 HTML/CSS、脚本、事件属性、链接、任意 URL、
`data:`/`blob:` 资源和宿主路径仍然 fail closed；HTML Island 不能取得浏览器或 Worker 的对象所有权。

P1-07 的 `ButtonNode`、`HitRegion` 和 `ConsolePrompt` 分别承载按钮/工具提示文本和输入语义，
不通过 HTML 属性重新引入事件处理器。工具提示的桌面专用自定义格式、图片和 WinForms 定时参数
属于 `HOST_SHIM`，headless 执行时产生稳定的阻断诊断；安全的按钮/命中区域 tooltip 仍作为有界
结构化文本传输。

## 后果

P0-01 的 `<b>`/`<i>` 场景有稳定结构化表示，危险输入只能成为文本或明确失败。允许列表扩展时必须增加嵌套、大小写、错误闭合和攻击属性测试，并同步更新本 ADR 与威胁矩阵；浏览器 CSP 和 renderer 安全验收不在本 ADR 范围内。
