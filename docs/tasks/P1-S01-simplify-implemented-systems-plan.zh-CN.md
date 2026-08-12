# P1-S01：按可信自托管边界简化已实现系统

状态：DONE（2026-08-12）

日期：2026-08-12

依据：[`ADR-0017`](../adr/0017-trusted-self-hosted-mvp-simplification.md)

## 1. 目的

本文是一个独立、可分步执行的代码简化计划，只处理 Phase 0、P1-01～P1-06 已经实现但超出新
产品边界的机制。它不实现 P1-07 之后的新功能，也不以“大重写”替换已验证的 SessionRoot、SQLite
和 Worker 生命周期。

目标是减少已经存在的产品入口、分支、配置和恢复语义，同时保证：

- 现有数据卷可以前滚升级，不能要求用户删除数据库或 `/data`；
- 身份、角色、资源授权和关键审计保留；
- 已实现的 Migrator、迁移前备份、持久 HTTP 幂等、Session 创建 operation recovery、mutation
  lease、WorkerLease/epoch/state version 保留；
- 安全 ZIP 摄取、parser-only Validator、manifest/digest、原子 current 启用、SessionRoot 私有复制、
  原生双布局存档保留；
- 每个活动 Session 仍使用独立 Worker 子进程；
- 每个提交只完成一个逻辑步骤，使用 `git commit -s`。

## 2. 明确不做

- 不删除 User、Role、owner、ResourceAuthorizer、本地账户 bootstrap 或 CSRF/Origin 检查；
- 不删除 `idempotency_records`、`session_creation_operations`、`session_root_mutation_leases`；
- 不把 Emuera Runtime 合并到 API，也不改为一个 Worker 承载多个 Session；
- 不回改任何已经发布的 migration；schema 变化只能新增前滚 migration；
- 不为了代码更少放宽 ZIP 路径/链接/压缩炸弹检查、dirfd/O_NOFOLLOW 或结构化显示允许列表；
- 不在本任务实现 P1-07 WebSocket、P1-09 存档 HTTP 或 P1-13 生产镜像新功能。

## 3. 开始前基线

1. 工作区必须干净；若有其他变更，先提交或由用户明确指定排除范围。
2. 记录当前 migration、OpenAPI、前端路由和测试数量。
3. 在 dev Docker 内执行完整基线：

   ```bash
   ./scripts/dev-up.sh
   ./scripts/check.sh
   ./scripts/verify-dev-user.sh
   ./scripts/verify-third-party.sh
   ```

4. 保存 `./scripts/check.sh` 摘要；本任务的每一步至少运行对应定向测试，最后再次完整验证。

## 4. S01-1：移除浏览器 Game 编辑产品入口

### 范围

- 从 HTTP/OpenAPI 产品面删除或返回稳定 `404/410` 的以下写入能力：从 current 开始编辑、写入文本、
  创建文件、重命名、删除文件和服务端全文搜索；具体路由先用 `rg` 盘点，不凭本文猜测名称。
- 保留包上传/替换、ZIP 安全摄取、目录浏览、文本只读查看、文件下载、验证、原子启用、Game 删除
  和可见性/授权。
- 前端删除 Monaco 编辑器、编辑状态、保存/重命名/删除/搜索控件和对应 Query mutation；Game 页面
  改成“上传包 → 查看诊断/文件 → 启用”的只读流程。
- `workspace` 名称和数据库/目录布局暂时保留，重新定义为内部摄取工作区，避免无收益的数据迁移。

### 实施顺序

1. 用 `rg` 列出 API route、Application port、Infrastructure method、Contracts DTO、Web client、
   React 页面和测试的完整调用图。
2. 先修改契约和 API 集成测试，冻结保留/删除的端点集合。
3. 删除前端入口和不可达状态。
4. 删除只由编辑 API 使用的 Contracts/Application/Infrastructure 代码；若底层方法仍被上传/启用
   恢复流程调用则保留并改为内部接口。
5. 不删除历史审计 action 或数据库数据；旧记录仍需可读取。

### 验证

```bash
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release \
  --filter "Category=GameLibraryApi|Category=OpenApi"'
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm web \
  sh -c "pnpm install --frozen-lockfile && pnpm typecheck:web && pnpm test:web && pnpm build:web"'
```

通过条件：上传、只读浏览、校验和启用仍工作；OpenAPI 不再宣传编辑能力；浏览器包中没有 Monaco
编辑入口；普通用户和管理员授权行为不变。

建议提交：`refactor(game): remove browser editing surface`

## 5. S01-2：把配额消费收敛为实例级上限

### 范围

- 保留 ZIP/展开/文件数、单文件、存档、Snapshot/队列和最低剩余空间等硬上限；配置来源改为实例级。
- open 只检查全局最大活动 Worker 数，不按用户或 `QuotaProfile` 调度。
- 已实现的 staging 和 SessionRoot 实际字节预留可暂时保留，因为它们同时提供磁盘故障边界和崩溃
  恢复；不再把它们描述为用户计费或公平调度。
- `QuotaProfile` 表、外键和现有用户数据第一步保留为兼容 schema，不立即做破坏性删除。

### 实施顺序

1. 盘点 `QuotaProfile` 各字段的实际读者，将字段分为：实例级安全上限、仅多租户公平性、未使用。
2. 新增单一实例级 options，并在启动时验证单位和上下限。
3. 将 open、摄取、Session 创建和未来存档所需的保留上限接到实例配置；删除 per-user 分支和错误码。
4. 对已不再读取的 profile 字段停止写入，但保持旧 schema/迁移可升级。
5. 只有在至少一个发布周期确认无读者后，才另立后续 migration 删除列/表；本任务默认不执行该步。

### 验证

```bash
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore --configuration Release \
  --filter "Category=ArchiveQuota|Category=SessionLifecycle|Category=Persistence"'
```

通过条件：两个不同用户受相同实例级上限约束；并发 open 在最后一个全局名额上最多一个成功；
现有数据卷 migration/check 通过；ZIP bomb、磁盘余量和有界字节测试不被删除。

建议提交：`refactor(quota): use instance capacity limits`

## 6. S01-3：简化 Worker 启动与断线生命周期

### 范围

- 保留 WorkerLease、epoch、bootstrap binding、私有 UDS、PID/start identity、进程监视、优雅停止和
  强制终止。
- 删除同一 API 实例内的断线宽限期重注册状态机、重连退避、为重连保留的输出/prompt 路径和相关
  配置。gRPC 控制流结束后 Worker 停止 Runtime 并有界退出。
- launcher 改成普通同 UID 非 root 子进程；删除尚未投入使用的 NsJail、独立 UID、namespace、
  seccomp、每 Worker cgroup/rlimit 配置抽象和 readiness 检查。
- 若某个沙箱接口已经被多个测试/生产路径使用，先收窄为 `IWorkerProcessLauncher`，再删除实现，避免
  在同一提交同时重写 Worker Manager。
- API 启动仍回收明确匹配 PID/start identity 的遗留 Worker；无法确认时只冻结对应 Session 的
  open/存档 mutation，不令全局 readiness 失败。

### 验证

```bash
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Application.Tests tests/CloudEmuera.Infrastructure.Tests \
  --no-restore --configuration Release --filter "Category=SessionLifecycle|Category=WorkerLease"'
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.Tests --no-restore --configuration Release'
```

必须新增的故障测试：

- 控制流关闭后 Worker 在期限内退出且不重新注册；
- 迟到旧 epoch 心跳/输出/输入结果仍被拒绝；
- API 正常关闭先优雅停止，超时后强制终止；
- API 被 kill 后，parent-death/进程组回收明确归属 Worker；
- 一个身份无法确认的遗留 lease 不影响其他无关 Session 的查询和 open；
- launcher 参数不包含 Game workspace/current 或其他 SessionRoot。

建议提交：`refactor(worker): exit on control disconnect`

## 7. S01-4：收敛已实现的审计和诊断表面

### 范围

- 保留现有追加式 `audit_events` schema、数据库保护、身份审计、管理员强停、Game 启用和关键资源
  变更审计；不删除历史记录。
- 删除仅为未来通用审计浏览、每 Worker 资源指标、OpenTelemetry/Prometheus 或容量面板准备且当前
  无产品消费者的 DTO、options、占位 route 和前端占位组件。
- 保留结构化日志、敏感字段过滤、health/ready/version，以及 Worker/Session 基本状态所需字段。
- 不将 `sessionId` 等高基数字段做指标标签；本任务不新增时序数据库依赖。

### 验证

```bash
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests tests/CloudEmuera.Api.IntegrationTests \
  --no-restore --configuration Release --filter "Category=Audit|Category=Health|Category=OpenApi"'
```

通过条件：关键变更仍产生审计且表不可通过普通 DbContext 更新/删除；日志不包含密码、Cookie、token、
SessionRoot 或输入全文；删除的占位能力不再出现在 OpenAPI 和前端导航中。

建议提交：`refactor(ops): keep basic diagnostics only`

## 8. S01-5：兼容清理与文档收尾

1. 用 `rg` 确认生产代码、配置样例和当前文档不再引用以下已取消能力：

   ```text
   NsJail, per-worker cgroup, independent worker UID,
   worker reconnect/re-register, browser game editor,
   per-user active quota, cross-session save copy,
   OpenTelemetry, Prometheus, audit query UI
   ```

   历史 ADR/任务记录可以保留，但必须有“已由 ADR-0017 取代”的显著说明，不能继续作为当前完成门。
2. 检查 OpenAPI/JSON Schema/TypeScript 生成物和前端路由无孤儿类型。
3. 对新增配置提供升级说明；旧环境变量可忽略一个兼容周期，并以不含秘密的 warning 提示弃用。
4. 更新 `docs/development-plan.zh-CN.md` 的实际测试数量和状态。

最终验证：

```bash
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
```

建议提交：`docs(scope): finish trusted self-hosted cleanup`

## 9. 删除与保留矩阵

| 已实现机制 | 本任务决定 | 理由 |
| --- | --- | --- |
| Local Identity、角色、owner 授权 | 保留 | 用户明确要求；远程访问和资源区分仍需要 |
| 关键追加式审计 | 保留并收窄消费者 | 已实现且对管理操作有价值 |
| 安全 ZIP、dirfd、配额字节上限 | 保留 | 防损坏格式、路径逃逸和磁盘耗尽 |
| workspace/current 内部模型 | 保留 | 支撑检查与原子启用；只删除编辑产品面 |
| 浏览器文件编辑/搜索 | 删除 | 明确不需要编辑器类功能 |
| Worker 独立进程 | 保留 | 全局状态隔离和可终止性 |
| Worker 独立 UID/NsJail/namespace/seccomp/cgroup | 删除或不实现 | 新信任模型不承诺恶意 Worker 隔离 |
| WorkerLease/epoch/state version | 保留 | 本地并发、迟到消息和单 writer 正确性 |
| Worker 断线重注册 | 删除 | 故障语义改为退出并显式 reopen |
| 持久 HTTP 幂等/operation recovery | 保留 | 用户明确要求且已完成 |
| per-user active/resource quota 消费 | 删除 | 改为实例级容量门 |
| QuotaProfile schema | 暂保留 | 避免立即破坏性 migration |
| Migrator 和迁移前备份 | 保留 | 已实现且升级价值高 |
| 每 Worker 指标平台/通用审计 UI | 删除或不实现 | 个人自托管不需要 |

## 10. 完成定义

- S01-1～S01-5 分别有独立、带 DCO signoff 的逻辑提交；
- 所有生产入口与 ADR-0017 一致，不再对用户宣称未实现的恶意 Worker 隔离；
- 现有数据库和 DataRoot 能前滚启动，身份、授权、关键审计和持久幂等行为不回退；
- 上传检查、current 启用、Session 创建/open/close/reopen 和原生存档基线测试继续通过；
- 完整 `./scripts/check.sh`、开发用户映射和第三方校验通过；
- 删除代码后测试不是简单移除：每个被保留的不变量仍有对应自动化测试。
