# ADR-0010：单一 Game 内容模型，不建立 GameVersion 实体

状态：Accepted

日期：2026-08-09

2026-08-12 修订说明：ADR-0017 保留单一 workspace/current、Validator、manifest/digest 和原子启用
内部模型，但取消浏览器内文件编辑、创建、重命名、删除与搜索产品面。workspace 现作为包摄取和
检查工作区；本文编辑器相关历史表述不再是 MVP 要求。

取代：[`ADR-0009`](0009-draft-publication-and-immutable-version-identity.md)

## 背景

早期设计把一个游戏拆成稳定 Game 和多个不可变 GameVersion，Session 固定到明确版本。该模型能
保留历史内容，但给自托管 MVP 带来版本列表、版本标签、版本引用保护、重复摘要身份、跨版本授权
和物理内容回收等额外概念。产品现在明确要求游戏库中的每个条目就是一个简单 Game，不提供版本
创建、版本列表、历史版本选择或回滚。

取消 GameVersion 不能让运行中的 Session 随 Game 编辑而变化。CloudEmuera 已按 ADR-0007 在
Session 创建时完整复制游戏内容到私有、持久 SessionRoot；因此 SessionRoot 本身可以保存创建
时的游戏快照，无需在游戏库中继续保留可寻址版本实体。

## 决定

- 领域模型和公开 API 删除 GameVersion。Game 同时拥有元数据、可编辑 workspace、当前可运行
  content、内容摘要、运行时清单、兼容性摘要和安全状态。
- 每个 Game 最多有一个 workspace 和一个 current content：
  - workspace 是 owner 的可编辑普通文件树；
  - current content 是新 Session 的只读复制来源；
  - 编辑 current content 时先复制到 workspace，current content 在新内容验证并原子启用前不变；
  - 成功启用后新 content 原子替换 current content，旧库内容不形成用户可见或可恢复版本。
- `content_revision` 只是 Game 内部的单调并发计数，不是资源 ID、版本实体或历史选择能力。API 不
  提供 revision 列表、按 revision 读取、回滚或基于 revision 创建 Session。
- Session 只外键引用 `game_id`，并记录创建时的 `source_content_digest`、`runtime_version` 和
  `runtime_manifest_json` 快照。SessionRoot 完成物化后是运行内容的权威副本；Game 后续编辑、
  重新上传、阻止或删除都不改写已有 SessionRoot。
- Session 创建从 Game 当前 content 的受保护目录句柄复制完整合法普通文件树。复制前在 SQLite
  中记录期望 `game_id + content_revision + content_digest`，复制后再次核对；Game 启用新内容与
  Session 复制通过 revision CAS 和持久 operation 协调。
- Game 可以同时存在可运行 current content 和待编辑 workspace。Game 的 `status` 管理 ACTIVE、
  BLOCKED、DELETED；`workspace_status` 管理 NONE、DRAFT、VALIDATING。只有 ACTIVE 且拥有 current
  content 的 Game 可以创建新 Session；BLOCKED 保留内容和既有 Session，但禁止新建。
- P1-03 READY ingestion 绑定到 Game workspace，而不是创建 GameVersion。上传可以创建新 Game 的
  workspace，也可以显式替换既有 Game 的 workspace；仍需 owner/digest/expiry CAS、CONSUMING
  续租和数据库提交对账。
- 启用内容使用持久 `game_content_operations` 跨越文件系统和 SQLite 的故障窗口。冻结 workspace、
  Validator、canonical manifest、只读权限、fsync、原子目录交换和数据库 CAS 的正确性不依赖
  HTTP 请求或 API 进程内状态。
- `game_files` 和 `compatibility_diagnostics` 直接以 `game_id + workspace/content scope` 归属。
  current content 的逐文件清单与摘要不可原地修改；workspace 编辑通过强 ETag 和 Game
  `state_version` 防止覆盖。
- 删除 Game 时若存在任意 Session 引用则拒绝；否则先逻辑删除。MVP 不提供历史版本恢复，也不在
  DELETE 请求中递归删除内容；物理回收遵循安全期、备份和受保护目录规则。
- 既有 `game_versions` schema 和 RuntimeAdapter 中的 `GameVersionRoot` 属于需要迁移的旧实现，
  不再代表产品概念。使用新增 migration 转换数据并删除表；代码分别重命名为 Game content/source
  术语。已提交 migration 文件保持不变。

## 备选方案

### 保留 GameVersion 但在 UI 隐藏

可以减少后端修改，但授权、外键、回收和错误语义仍然围绕版本存在，后续代码会持续泄漏该概念，
不符合简化目标，因此不采用。

### 直接原地编辑 Game current content

实现最少，但 Session 创建可能复制到半编辑内容，校验失败也会破坏最后可运行状态。采用独立
workspace 和原子启用，使外部仍只有一个 Game，同时保持一致性。

### Game 编辑时同步更新所有 SessionRoot

会改变运行中和已关闭 Session 的脚本、配置与存档兼容边界，并制造跨 Session 大事务；违反私有
SessionRoot 和运行隔离，因此禁止。

### 每次 Session 创建都永久保留隐藏内容版本

可用于回溯，但实质上重新建立版本存储和引用管理。SessionRoot 已保存所需快照，数据库只记录
摘要/清单快照，不再保留隐藏版本内容。

## 后果

- 用户只管理 Game，不再理解版本标签、版本列表、发布版本删除或选择 Session 版本。
- 已有 Session 仍保持创建时内容；新的 Session 总是使用 Game 当时的 current content。
- 游戏库不提供历史回滚。错误启用后的恢复依赖重新上传、workspace 修复或整个 `/data` 的运维
  备份，而不是产品内版本历史。
- 一个 Game 短时保存 workspace、current 和 operation work 三棵文件树，需要空间预检和有界复制。
- P1-01 的表和测试、P1-02 授权类型、P1-03 交接文档、RuntimeAdapter 命名以及未来 P1-06 Session
  创建都需要迁移；旧 migration 保留作为升级起点。

## 验证

- API/OpenAPI/前端契约不存在 GameVersion 资源、ID、列表、标签或路由。
- Game current 存在时编辑只改 workspace；启用前后已有 SessionRoot 摘要和内容不变。
- Session 创建与 Game 内容启用并发时，Session 要么完整复制旧 digest，要么完整复制新 digest，
  不能混合两棵树。
- 多个既有 game_versions 的升级 fixture 按迁移规则保留当前 Game 内容、workspace 和每个 Session
  的源摘要/manifest；无法无损判定的数据库在修改前失败并保留备份。
- 全仓产品代码除 migration compatibility/升级测试外不再包含 `GameVersion`、`game_version_id`、
  `gver_` 或 `/game-versions`。
