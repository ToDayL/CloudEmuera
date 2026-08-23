# ADR-0024：HTML_PRINT 使用固定上游解析器并在 headless 边界安全翻译

状态：已接受
日期：2026-08-17

字体映射修订（2026-08-23）：上游继续解析 `font face` 语法，但 ADR-0029 规定 headless translator
统一映射到当前 Session 的产品内置 face，不再从游戏 manifest、宿主或用户字体解析。

## 背景

CloudEmuera 之前在 `RuntimeAdapter` 中维护了一个独立的 `EmueraHtmlParser`。它可以生成安全的
结构化节点，却无法证明与固定 Emuera.EM+EE 的 `HtmlManager` 在伪 HTML 的词法、状态转换、闭合
省略、按钮分段、`div` 递归和错误行为上相同。`HTML_PRINT` 不是浏览器 HTML；继续扩展第二个 parser
会把上游行为复制成一个长期漂移的实现。

固定上游 `HtmlManager` 已经是 headless 程序集的一部分，且脚本 helper（`HTML_TAGSPLIT`、
`HTML_STRINGLEN`、`HTML_SUBSTRING` 等）直接使用它。因此本任务需要把它的解析结果抽到一个只包含
Emuera 语义的内部片段，再由 headless translator 安全地映射为 RuntimeAdapter 节点。

## 决定

1. 固定上游 `HtmlManager` 是 `HTML_PRINT` 和 `HTML_PRINT_ISLAND` 的唯一语法、属性、实体、闭合和
   错误权威。生产路径不再调用 RuntimeAdapter 自己实现的 Emuera HTML parser。
2. 上游解析阶段通过 `ParseFragment` 产生程序集内部的 `UpstreamHtmlFragment`。该片段只保存文本样式、
   按钮值类型/标题/位置、图片逻辑资源名和 MixedNum、Shape 参数、div 盒模型以及显式分段；不包含
   raw HTML、浏览器 URL、Graphics、Font、WinForms 或 IPC 类型。
3. `UpstreamHtmlTranslator` 是唯一的 headless 映射边界。它在提交 Console transaction 之前完成
   parser budget、manifest 资源、逻辑字体、几何和 RuntimeAdapter 节点验证；转换失败不提交部分
   节点、不消费 sequence、不改变 pending line。
4. 上游 `CodeEE`/语法异常继续作为上游运行时错误传播；CloudEmuera 的输入、深度、输出、资源和
   字体拒绝使用独立的兼容性诊断代码，不伪装成上游语法错误。缺失主图片使用安全的逻辑资源名文本
   作为可见 fallback，缺失 hover/mapping 图片只省略对应附属状态，绝不产生 URL 或宿主路径能力。
5. `HTML_PRINT` 的 `toPrintBuffer=false` 和 `true` 分别保留 display-line 与 print-buffer 的边界：
   前者在成功转换后冲刷已有 pending line，后者只追加到 pending buffer；`br`/原始换行始终是显式
   分段。`p`/`nobr` 只按固定上游规则允许 fragment 尾部省略闭合。
6. HTML Island 也从同一 `ParseFragment` 取得语义；Island 的输出继续使用 executable-free 的封闭
   结构化契约，不能传输 raw HTML、CSS、脚本、事件属性、URL 或宿主路径。像素级字体 shaping 和
   Windows GDI 与浏览器布局的差异不由本 ADR 声称已解决。

## 备选方案

### 继续维护 RuntimeAdapter parser

实现初期改动较小，但每个新增标签都会重新复制上游隐含状态，无法保证与固定解释器一致，拒绝。

### 把 fragment 交给浏览器并在前端清理

会把浏览器 HTML、URL、CSS 和事件能力带入运行时边界，也不能保持 Emuera 的词法和错误行为，拒绝。

### 通过 AltText/ToString 往返桌面对象

这会重新解析转义、大小写和数值格式，并可能泄露桌面对象的序列化细节，拒绝。translator 只读取
上游对象的直接语义字段，缺字段时扩展内部片段。

## 后果

- 固定上游语法只有一个来源，桌面入口和 headless 入口共享同一状态机。
- RuntimeAdapter 不依赖上游程序集，raw HTML 和桌面对象不会进入 IPC/API/Web。
- 上游 `System.Drawing` materializer 仍只属于 headless/桌面内部；结构化节点的字体测量和浏览器排版
  需要单独的布局兼容性验证。
- 旧 `EmueraHtmlParser` 的 allowlist 测试必须分类：结构契约/攻击语料迁移到 translator 和节点验证，
  上游不存在的浏览器 HTML 测试不再作为 Emuera 兼容性 expected。

## 验证

- `HtmlManager.ParseFragment` characterization 覆盖闭合省略、交错样式、属性词法、实体、按钮分段、
  图片、Shape、div 和语法错误；抽取前后使用固定上游入口比较 outcome。
- translator 契约测试覆盖 manifest、缺失附属资源、generation、MixedNum、字体/节点/输入/深度限制，
  并证明失败时 pending line 和 sequence 不变。
- 真实 headless ERB、Worker/IPC/realtime round-trip 和完整 dev Docker `scripts/check.sh` 通过。
