# ADR-0011：游戏包单根目录自动展平与固定文件名大小写不敏感查找

状态：Accepted

日期：2026-08-10

关联：[`ADR-0008`](0008-secure-zip-ingestion-policy.md)、[`ADR-0010`](0010-single-game-content-without-version-entities.md)、P1-03/P1-04

## 背景

实测一批现实游戏包后发现两个会导致“无法通过验证”的兼容性问题：

1. **单根目录打包**：很多 era 游戏以“一个顶层文件夹内放 ERB/CSV/emuera.config”的形式分发
   （例如 `eraJK私人改造+修正 90%汉化版本/`）。CloudEmuera 的摄取把 ZIP 内容原样展开到
   workspace，结构检查在 workspace 根目录找不到 `ERB/*.ERB`，产生阻断诊断
   `ERB_ENTRYPOINT_MISSING`。
2. **大小写敏感查找**：上游 Emuera 在 Linux 上按精确文件名读取 `CSV/GAMEBASE.CSV`、
   `CSV/_Rename.csv`、`CSV/_Replace.csv` 和 `emuera.config`。Windows 文件系统大小写不敏感，
   游戏包常以 `GameBase.csv` 等大小写变体发布；Linux 上精确查找失败，运行时预检报
   `CSV/GAMEBASE.CSV is missing`。

这两个问题都不需要游戏包上传者改包即可解决，且不引入“版本”或“历史回滚”语义。

## 选项

### A. 单根目录展平

- A1：摄取时检测“唯一顶层目录”并剥离该前缀再校验/展开/建 manifest（本 ADR 采用）。
- A2：绑定到 workspace 后再移动文件。绑定后移动会改变已写入的 workspace 与已生成的
  manifest/文件索引，恢复路径更复杂；摄取期展平让 manifest、文件索引、诊断路径和运行时
  布局从一开始就一致。
- A3：要求上传者改包。增加人工负担且与 Windows 时代分发习惯不符。

展平只应用于“全部条目共享同一个顶层目录”且“该目录下确实有文件”的情况；多个顶层条目
（如 `__MACOSX/` + 游戏目录）不展平，避免误判。

### B. 固定文件名大小写不敏感

- B1：在 SessionRoot 物化（`SessionRootLayoutBuilder`）时，为缺失的固定文件名创建
  大小写规范化别名副本（本 ADR 采用）。SessionRoot 是私有/一次性副本（真实 Session 与
  Validator 快照都是），在副本内补一个别名文件不会影响 Game 内容、manifest 摘要或
  其它 Session。
- B2：直接修改上游 `GameBase.LoadGameBaseCsv` 等做大小写不敏感发现。需要改 `Upstream/`
  源码并维护 MODIFICATIONS，影响面更大。
- B3：把运行时文件系统端口改成全局大小写不敏感。会与已记录的 `RESOURCE_CASE_MISMATCH`
  语义冲突，且上游部分读取走真实 OS 路径，绕过端口。

## 决定

- 摄取期展平：`GamePackageIngestionService` 在 ZIP 结构检查后、路径策略校验前检测
  单根目录前缀；若存在，剥离该前缀后再进入 `Preflight`/`ExtractAsync`。manifest 的
  `fileCount`、`directories`、逐文件路径、内容摘要全部基于展平后的布局。安全边界不变：
  所有展平后的路径仍走原有路径策略（穿越/绝对路径/保留名/Unicode/大小写碰撞/深度/链接）。
- 固定名别名：`SessionRootLayoutBuilder` 物化副本时，若精确的固定名（`CSV/GAMEBASE.CSV`、
  `CSV/_Rename.csv`、`CSV/_Replace.csv`、`emuera.config`）缺失但存在唯一的大小写变体，则
  把该变体再复制一份为精确固定名；有多个大小写变体（歧义）时不创建别名。别名计入
  `SessionRootCopyLimits`，文件权限与其它复制文件一致。
- 不做：不修改 `Upstream/` 源码；不改变 Game 内容本身的文件名；不提供版本/回滚语义。

### C. 非致命脚本警告与可选入口

用户游戏（eraJK 系）在展平+别名后仍暴露两个 headless 会话比上游更严格的问题：

- 上游 `ParserMediator` 通过 `PrintWarning` 输出非致命脚本警告（例如系统函数
  `@COM1000`/`@COM_ABLE1000` 带参数），原版 Emuera 会继续运行；CloudEmuera 的 headless
  会话把一切 `RuntimeMessages` 都当成致命脚本诊断，导致合法游戏无法验证。
- 上游把 `@SYSTEM_TITLE` 视为可选（缺失时用 GAMEBASE 数据渲染标准标题画面，
  `callFunction("SYSTEM_TITLE")` 返回 false 后继续）；headless 会话却硬性要求该标签存在。

决定：headless 控制台把 `PrintWarning` 单独记入 `RuntimeWarnings`，只有 `PrintError` 进入
`RuntimeMessages`（错误才阻断）；初始化警告作为 `runtime_warning` 非阻断诊断返回给
Validator/UI。`@SYSTEM_TITLE` 缺失不再阻断，行为与上游一致。

## 后果

- 用户无需改包即可导入常见的“单顶层文件夹”游戏包；`GameBase.csv` 等大小写变体在
  Linux 上可被真实 Emuera 解析器加载。
- 展平使 content digest 基于展平布局计算，与 workspace/current 实际布局一致。
- 别名文件是 SessionRoot 私有副本内的附加文件，不改变 manifest digest、绑定元数据或
  其它 Session 的隔离；副本清理（validator 快照删除、SessionRoot 回收）自动覆盖。
- 安全与一致性验证：展平只减少深度/前缀，不绕过任何路径校验；歧义大小写不创建别名。

## 验证

- 摄取展平：单根目录 ZIP 的 manifest 不含顶层前缀且文件数正确；多顶层条目、单顶层文件、
  歧义大小写不展平/不创建别名；穿越/碰撞等拒绝测试不回归。
- 会话别名：`GameBase.csv` 的 SessionRoot 同时包含原文件与 `GAMEBASE.CSV` 别名；
  精确名存在时不产生重复；`emuera.config` 变体可被 `RuntimePaths.ValidateSessionRoot` 接受。
- 端到端：单根目录 + `GameBase.csv` 组合包经摄取→绑定→验证→启用通过真实 parser；
  真实用户游戏（eraJK，含 `COMF*` 系统函数警告、无 `@SYSTEM_TITLE`）展平后验证通过，
  警告以非阻断诊断返回；既有 `./scripts/check.sh`、fixture 校验、`git diff --check` 全绿。
