# ADR-004：Runtime 富内容安全结构化语义

状态：Accepted  
日期：2026-08-04

> 2026-08-17 更新：HTML_PRINT/HTML_PRINT_ISLAND 的语法权威、解析状态机和闭合行为由
> [ADR-0024](0024-html-print-upstream-parser-authority.md) supersede。本 ADR 仍约束 raw HTML、URL、脚本、
> 事件属性和宿主对象不得越过结构化边界；下文直接描述 `EmueraHtmlParser` 的段落仅保留为历史背景，
> 不再是生产实现要求。

## 背景

上游 Emuera 的 `HTML_PRINT` 输出使用的是一套 HTML 风格的显示标记，而不是浏览器 HTML。CloudEmuera
是原生 HTML 前端，因此应完整消费这套显示语义；但 Console 仍不能把原始 HTML、CSS、URL 或事件处理器
泄漏给 Web/API 层。显示标记必须在 RuntimeAdapter 内转换为有界结构化节点，再由前端使用原生 DOM 元素
渲染。

## 决定

- 固定版本上游 `HtmlManager.ParseFragment` 是唯一语法权威；它的状态机负责 fragment、词法、实体、大小写、
  闭合和 `p/nobr` 尾部省略。headless 只通过 `UpstreamHtmlTranslator` 施加独立的容量、资源和节点契约限制。
- 上游实际支持的 `p align`、`nobr`、`b/i/u/s`、`br`、`font face/color/bcolor`、`button value/title/pos`、
  `nonbutton`、`clearbutton`、`img src/srcb/srcm/height/width/ypos`、`shape type/param/color/bcolor`、私有
  `div` 位置/尺寸/层级/display/盒模型、注释和字符实体均由该状态机解释；不引入浏览器 HTML 别名。
- 未知标签/属性、事件属性、`style`、`href`、`srcset`、URL-like 值、重复/错误闭合、未闭合、超深或超限
  fragment 按上游语法错误或 Cloud 稳定容量/翻译错误分类处理，不由第二套 parser 改写为普通文本。
- 实体解码后的普通文字生成受限 `TextNode`；格式化标签生成 `ConsoleTextStyle`；`p`/`nobr` 的布局语义
  生成 `ConsoleLineAlignment`/`ConsoleLine.NoWrap`；按钮标签可以包含图片、形状和样式文本等结构化展示节点，
  但不允许嵌套行为节点。
- `img` 的 `src`、`srcb`、`srcm` 只能是 Session manifest 的逻辑资源名，由 Worker 解析为 `SpriteNode`
  及其 hover/mapping 资源和裁剪尺寸；它们永远不会成为浏览器 URL 或宿主文件路径。`shape` 只生成有限
  的 `ShapeNode` 几何；`div` 只生成 `DivNode`、有界矩形、盒模型和结构化子节点，不把任意 CSS 字符串
  传过边界。

## 备选方案

- 使用 `innerHTML` 后删除危险节点：浏览器解析和属性兼容行为过大，容易在删除逻辑前产生副作用，且会引入 DOM/UI 依赖。
- 接受任意 HTML 并交给 Web renderer 做消毒：把上游安全边界推迟到另一个进程，无法证明 RuntimeAdapter 的公共契约没有 URL/事件能力。
- 把上游 fragment 直接交给浏览器：会使浏览器解析差异、CSS、导航和事件能力进入运行时边界，无法证明
  游戏输出不会取得宿主或前端对象。

## P1-07 扩展（由 ADR-0024 重新定义）

固定版本上游 `HtmlManager` 先产生内部 `UpstreamHtmlFragment`，再由 headless translator 映射到
结构化文本、按钮、图片、形状、div 和换行节点；HTML Island 与 `HTML_PRINT` 共享该解析入口。
扩展不改变本 ADR 的边界：原始 HTML/CSS、脚本、事件属性、链接、任意 URL、
`data:`/`blob:` 资源和宿主路径仍然 fail closed；HTML Island 不能取得浏览器或 Worker 的对象所有权。

P1-07 的 `ButtonNode`、`HitRegion` 和 `ConsolePrompt` 分别承载按钮/工具提示文本和输入语义，
不通过 HTML 属性重新引入事件处理器。工具提示的桌面专用自定义格式、图片和 WinForms 定时参数
属于 `HOST_SHIM`，headless 执行时产生稳定的阻断诊断；安全的按钮/命中区域 tooltip 仍作为有界
结构化文本传输。

## 后果

P0-01 的 `<b>`/`<i>` 场景以及 eraTW 的标题图片场景都有稳定结构化表示，危险输入只能成为文本或明确失败。
新增上游语义时必须增加正常路径、边界、错误闭合和攻击属性测试，并同步更新本 ADR 与能力矩阵；浏览器
CSP 和 renderer 安全验收不在本 ADR 范围内。
