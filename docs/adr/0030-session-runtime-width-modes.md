# ADR-0030：Session 持久宽度模式与启动时 CSS 宽度上限

状态：已接受

日期：2026-08-23

## 背景

ADR-0029 固定了 Worker 的权威物理排版，并让浏览器在每次开启 Session 时提交 CSS viewport 宽度；
Runtime 原先始终取该宽度与游戏 `emuera.config` 中 `WindowX` 的较小值。这个单一策略无法让用户选择
更宽的布局，也不能为不同 Session 保存宽度偏好。

## 决定

用户启动默认值与每个 Session 持久化 `widthMode` 和可选 `customWidth`。模式为：

- `ORIGIN`：`min(browserCssWidth, configuredWindowX)`；
- `MAX`：`min(browserCssWidth, 2000)`，忽略 `configuredWindowX`；
- `CUSTOM`：`min(browserCssWidth, customWidth)`，忽略 `configuredWindowX`。

`customWidth` 只允许在 `CUSTOM` 模式存在，范围为 240～16384 CSS px。旧用户偏好和旧 Session 迁移为
`ORIGIN` 且 `customWidth=NULL`。创建 Session 时可选择模式；创建后只允许在 `CLOSED` 或已完成旧 Worker
回收的 `CRASHED` 状态修改。设置页保存新建 Session 的默认值，不覆盖既有 Session。

浏览器在每次 open 时提交当时 viewport 的整数 CSS px，服务端保持 240～16384 的边界。API 不保存这次
测量；Worker 读取 `emuera.config` 后按 Session 模式计算一次最终 `Config.WindowX`。运行期间 resize 不触发
重排，较窄视口继续使用横向滚动；要采用新宽度需关闭并重新开启 Session。

## 备选方案

1. 只保存一个像素宽度：拒绝。无法表达保留游戏原始宽度或“尽量铺满”的用户意图。
2. 每次 open 临时选择模式：拒绝。创建页、默认设置和停止态 Session 配置无法形成稳定、可重现的行为。
3. 运行中响应 resize：暂不采用。它会改变 Worker 权威分行、按钮位置和当前 Snapshot，需独立协议设计。
4. Max 不设上限：拒绝。极宽布局会扩大单行几何、渲染和内存成本；首期固定为 2000 CSS px。

## 后果

- Session schema、用户偏好、HTTP 响应、Worker bootstrap 与 Runtime options 增加宽度配置；
- `ORIGIN` 保持原有行为和迁移兼容；`MAX`/`CUSTOM` 明确覆盖游戏 `WindowX`；
- 浏览器 CSS 宽度仍是不可信有界输入，只影响本次 Worker 启动，不改变持久 Session 配置。

## 验证

- Runtime 兼容测试覆盖 Origin、Max 的 2000 上限和 Custom 的用户值/浏览器双重上限；
- API 测试覆盖默认值、创建、停止态修改、运行态拒绝和非法 mode/value 组合；
- migration 测试覆盖旧 Session 回填与 SQLite CHECK；Web 测试覆盖三种表单状态和请求字段。
