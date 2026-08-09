# ADR-0013：逻辑删除的 Game 释放名称，活动名称保持唯一

状态：Accepted

日期：2026-08-10

关联：[`ADR-0010`](0010-single-game-content-without-version-entities.md)、GAME-010

## 背景

删除 Game 是 GAME-010 要求的“可恢复逻辑删除”：`games` 行置为 `DELETED`（保留审计与恢复现场），
内容目录不在普通请求中删除。但 `ux_games_owner_name` 唯一索引把 `DELETED` 行也计入，导致删除
后的游戏名称被永久占用——用户想重建同名游戏时 `CreateAsync` 抛出
“A game with the same name already exists. An error occurred while saving the entity changes…”，
看起来像“删除没有清理数据库”。

## 选项

- A1（采用）：唯一索引改为部分索引 `WHERE status != 'DELETED'`，只有非删除游戏占用名称；
  删除即释放名称，且活动/禁用游戏名称仍唯一。逻辑删除的审计/恢复语义不变。
- A2：名称永久保留。与用户“删除应释放名称”的预期不符，且缺乏可解释价值。
- A3：删除时物理删除行。违反 GAME-010 的可恢复逻辑删除与审计要求。

## 决定

- `ux_games_owner_name` 改为部分唯一索引（`status != 'DELETED'`），新增迁移
  `AddGameNameReuseAfterDelete`。
- `CreateAsync`/`UpdateAsync` 在写入前显式检查“同 owner 同名且非 DELETED”，命中返回
  `GAME_NAME_CONFLICT`（409）与清晰中文消息“同名游戏已存在。”，不再透出 EF 底层异常文本。
- 删除仍是逻辑删除：行、workspace/current 内容、审计记录保留；只是名称可复用。

## 后果

- 删除后可立即重建同名游戏；活动/禁用游戏之间名称仍唯一（含并发，由部分索引兜底）。
- 旧库通过迁移自动生效，无需人工清理数据。

## 验证

- PersistenceConstraint：两个 `DELETED` 同名行可共存、新 `ACTIVE` 可复用该名称、两个
  `ACTIVE` 同名仍被拒绝；索引 SQL 含 `status != 'DELETED'`。
- GameLibraryService：删除后同名重建成功；改名为已删除游戏名成功、改为活动游戏名返回
  `GAME_NAME_CONFLICT` 且消息为“同名游戏已存在。”。
- HTTP 集成：创建 → 同名 409 → 删除 → 同名重建 201。
- `./scripts/check.sh` 全绿。
