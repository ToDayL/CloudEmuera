# ADR-0012：游戏包暂存配额按实际大小预留

状态：Accepted

日期：2026-08-10

关联：[`ADR-0008`](0008-secure-zip-ingestion-policy.md)、[`ADR-0011`](0011-single-root-flatten-and-fixed-case-lookup.md)、P1-03

## 背景

摄取流程为每次上传在 `game_package_ingestions.reserved_bytes` 预留“最坏情况”的暂存空间
（默认配额 `MaxArchiveBytes=2GiB + MaxExpandedBytes=4GiB`，即每次上传预留 6GiB），用于在有界
磁盘上约束并发。实测中，一个约 30MB 的游戏包也会预留 6GiB；两次上传后如果用户没有立即绑定
（READY 未消费），`MaxStagingReservedBytes`（默认 12GiB）就被两个 6GiB 占满，第三次及以后的上传
在进入安全扫描前就被 `STAGING_BUDGET_EXHAUSTED` 拒绝，且该错误被折叠成笼统的
“游戏包不安全或不受支持。”，表现为“频繁无法通过安全检查、似乎进入 staging 后无法再次上传”。

## 选项

- A1（采用）：保留开始时“最坏情况”预留以约束并发在途上传；在归档接收并展开后，把预留结算为
  实际字节（`archiveBytes + expandedBytes`）并原子复核预算。既保留并发磁盘安全，又让未消费
  READY 包不再按 6GiB 累积。
- A2：调大 `MaxStagingReservedBytes`。只是推迟问题，且让单机磁盘被大额虚占。
- A3：开始时就按 `Content-Length` 预留。客户端可虚报长度导致过预留或欠预留，破坏磁盘安全边界。

## 决定

- 摄取开始时仍按 `effectiveArchiveLimit + effectiveExpandedLimit` 预留（并发在途上界不变）。
- 在 `Analyze` 完成后、发布 READY 前，通过 `SqliteImmediateTransaction` 原子地把
  `ReservedBytes` 结算为 `archiveBytes + extraction.TotalBytes`，并重新核算全局预留总额；
  若超出 `MaxStagingReservedBytes` 则按 `STAGING_BUDGET_EXHAUSTED` 失败并清理。
- 复用既有 `ExecuteUpdateAsync` 状态机模式（`WHERE id AND status=Analyzing`），不与
  `TransitionAsync` 的 raw 更新产生 EF 并发冲突。
- 错误信息按拒绝码返回具体中文原因（`GamePackageRejectionMessages`），不再折叠为笼统的
  “游戏包不安全或不受支持。”；前端展示错误码与清单阻断诊断明细。

## 后果

- 未消费的 READY 摄取按实际大小占用预算（30MB 包约几十 MB），正常顺序上传不会再次占满配额；
  并发在途上传仍被最坏情况预留约束。
- 已被旧逻辑占满的存量行由 reaper 在到期后回收；本 ADR 不改变过期/消费/清理语义。

## 验证

- GamePackages 测试：`ReservationSettlesToActualSizeAfterAnalysis`、
  `UnconsumedReadyIngestionsReserveOnlyActualBytes`，以及既有并发预算测试不回归；
- 隔离真实 Kestrel 环境连续上传同一 31MB 包，DB 中两条 READY 行预留合计约实际大小（远小于
  2×6GiB），第二次上传成功；
- `./scripts/check.sh`、Web typecheck/test/build 全绿。
