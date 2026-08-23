# ADR-0031：Session 展示文本反斜杠转换为半角日元符号

状态：已接受

日期：2026-08-24

## 背景

大量历史 Era 游戏把 U+005C 反斜杠当作日元货币符号使用，依赖日文 Windows 字体把同一码位绘制成
日元字形。CloudEmuera 分发的字体按 Unicode 区分反斜杠与日元符号，因此这些游戏会显示错误字形。

直接修改脚本/CSV、运行时字符串或浏览器最终 DOM 都不合适：前者会改变路径、转义、比较和按钮输入值；
后者发生在 Worker 权威字体测量之后，会使物理分行、segment 宽度和按钮命中盒与实际字形分叉。

## 决定

- 新增持久 Session 选项 `convertBackslashToYen`，默认 `true`；账户启动默认值、Session 创建和停止态
  配置都可修改，活动 Session 不可修改。
- 转换固定为 U+005C `\` → U+00A5 `¥`（半角 YEN SIGN），不使用 U+FFE5 `￥`。
- 转换位于 Worker headless Console 的展示投影边界，在可见文本进入 `StringMeasure` 和物理布局前执行。
- 普通文本、HTML 文本、按钮标签、可见 tooltip、超时消息和窗口标题采用该展示映射；按钮提交值、
  用户输入、prompt 默认值、游戏字符串、文件路径、资源 ID 和解析输入保持原值。
- 数据库迁移为既有 Session 和既有账户默认值写入开启状态。该布尔值进入创建幂等摘要、HTTP 投影、
  Worker lease/launch spec 和一次性 bootstrap；不升级结构化 Console 或 Realtime 协议，因为线上的文本
  仍是普通 Unicode 文本，选项只决定 Worker 生成内容。一次性 bootstrap schema 升级到 v6，使缺少该
  语义的旧 API/Worker 组合 fail closed。

## 备选方案

1. 只在浏览器渲染时替换：拒绝。会破坏 ADR-0029 的 Worker 权威排版。
2. 摄取时重写 ERB/CSV：拒绝。无法区分货币显示与路径/转义/字符串语义。
3. 修改字体 cmap 让 U+005C 永远显示日元：拒绝。用户无法关闭，且真实反斜杠也无法显示。
4. 始终转换且不提供选项：不采用。绝大多数游戏受益，但少数游戏确实需要显示反斜杠。

## 后果与验证

转换后的 U+00A5 字形参与 Worker 测量、换行和按钮盒计算，浏览器按同一字体及几何直接绘制。专项测试
必须证明默认开启、关闭后保留 U+005C、按钮提交值不变、设置持久化及旧数据迁移默认开启；完整验证使用
dev Docker 的 RuntimeCompatibility、API/Infrastructure/Web 测试和 `./scripts/check.sh`。
