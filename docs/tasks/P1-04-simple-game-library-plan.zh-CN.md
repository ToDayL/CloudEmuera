# P1-04 简化游戏库、工作区编辑与当前内容启用详细计划

状态：DONE（2026-08-09 完成故障矩阵、平台差异验证与全量验收）

设计日期：2026-08-09

对应开发步骤：`P1-04 — 简化游戏库、工作区编辑与当前内容启用`

前置条件：P1-01 SQLite 首版 schema、P1-02 本地身份/授权/审计、P1-03 安全游戏包摄取、
P0-05 私有 SessionRoot 完整复制均已完成

后续步骤：P1-05 Supervisor 租约与 epoch；P1-06 Session 创建；P1-10 浏览器游戏库；P1-12
Worker/Validator 正式沙箱

需求映射：GAME-004～010、AUTH-001/002、OPS-004～006、COMP-004～006/008、SEC-001/005/009、
NFR-011/013、AC-008～010/014。

架构决策：[`ADR-0010`](../adr/0010-single-game-content-without-version-entities.md)；摄取边界见
[`ADR-0008`](../adr/0008-secure-zip-ingestion-policy.md)；SessionRoot 所有权见
[`ADR-0007`](../adr/0007-session-root-native-save-ownership.md)。

## 0.1 2026-08-09 浏览器 UI 接入（P1-10 不再处理 Game 的 UI）

- 游戏库页（`GET /api/v1/games`）：真实列表、可见性筛选、搜索、创建游戏、ZIP 导入 →
  摄取（`POST /game-package-ingestions`）→ 新建或绑定已有游戏工作区（`PUT /games/{id}/package`）；
- 单一 Game 内容页：当前内容/工作区概览、从当前内容开始编辑、上传替换工作区、验证（`:validate`）、
  原子启用（`:activate`）、丢弃草稿、编辑资料、删除与管理员禁用；
- 文件页：workspace/current 目录浏览与面包屑、文本查看/编辑（ETag 前置条件与 `X-Game-State-Version`
  CAS）、新建/删除文本文件、下载与有界搜索（签名游标分页）；当前内容只读；
- 兼容性页：展示最近一次验证的诊断（阻断/非阻断），可重复运行验证；
- 验证：Web 单元测试覆盖列表/创建/上传绑定/文件编辑/验证启用等分支；新增 HTTP 集成测试
  `GameLibraryApiContractTests`（含真实 parser 进程）与 `RuntimeBridge` 回归测试；隔离 e2e
  身份套件确认登录后进入真实数据游戏库不回归。

开发环境修复（同轮落地，映射 GAME-007 与验证能力）：

- `Program.cs`：Validator 程序集路径改为从内容根向上定位仓库内的
  `src/CloudEmuera.Validator/bin/{Debug|Release}/net10.0`，按环境优先、缺失时回退另一配置，
  发布布局回退到 API 同目录；
- `UpstreamHeadless/HeadlessEmueraConsole.cs`：`PrintSystemLine` 不再进入 `RuntimeMessages`，
  消除上游 DEBUG 构建的经过时间系统行被当作阻断诊断导致 Debug/Release 行为不一致的问题。

## 0. 2026-08-09 实现进度

本轮已经落地：

- 新增 `CollapseGameVersionsIntoGames` 及后续 schema migration；最终产品模型删除
  `GameVersionRow/Configuration/DbSet/status/resource action` 和 `game_versions`，历史 migration 原样保留；
- `games` 已具备唯一 workspace/current content、摘要、内部 revision、启用/删除审计字段；Session
  改为 `game_id + source_content_digest + source_content_revision + runtime_manifest_json`；
- 增加 `game_files`、`compatibility_diagnostics`、`game_content_operations` 及活动 operation/ingestion
  唯一约束；
- 实现 Game CRUD、管理员 block/unblock、P1-03 READY 摄取绑定、从 current 开始编辑、丢弃工作区、
  目录/文本/下载/有界搜索、验证、启用和 operation 查询 API；
- 工作区写入使用 Game 级持久文件锁和 `state_version` CAS；current 通过新树替换且设置只读，失败路径
  保留/恢复旧目录，不把 retired 暴露为版本资源；
- 验证已覆盖树类型/链接/nlink、UTF-8/CP932、基本结构、CALLSHARP 能力阻断、canonical digest、
  有界 manifest、文件索引和诊断持久化；
- operation 过期标失败，并能对“Game 已提交、ingestion 仍 CONSUMING”补做 Complete；
- RuntimeAdapter 和兼容性/Worker 测试符号已由 `GameVersionRoot/Identity` 改为
  `GameContentRoot/Identity`；
- 自动化已覆盖旧 PUBLISHED + Session 数据转换、无旧表终态、真实 ZIP 绑定、路径穿越、CAS、
  current 不可变、编辑编码和阻断诊断。
- 新增一次性 `CloudEmuera.Validator` 可执行项目；API 只通过有界子进程协议调用，Validator 仅执行
  固定 Emuera 初始化/解析，不进入游戏循环；超时、崩溃、超输出、非法协议均转换为稳定阻断诊断；
- Validate/Activate 先复制只读快照，再执行静态检查和真实 parser；生产镜像单独发布 Validator，
  API 程序集不引用 EmueraRuntime；真实最小 ERB 包进程测试证明 `INPUT` 不会被执行；
- 新增 `game_content_copy_leases` 和 `IGameContentCopyLeaseStore`，按 Game revision/digest 打开并持有
  `O_NOFOLLOW` 目录句柄；测试覆盖 current rename 后仍从同一 fd 读取且释放时删除持久租约；
- Game owner marker 已升级为绑定 gameId 和目录 device/inode 的 schema 2；文本写入和删除使用
  dirfd/openat、普通文件/nlink 复核、同目录临时文件、fsync 和 renameat，activation 快照发布前
  fsync 全树；过期 copy lease 与 VALIDATING 状态由后台 reaper 收敛；
- 新增 `AddGameContentCopyLeases` migration，终态为 14 个业务实体、9 个 migration；最终
  `check.sh` 中 Infrastructure 77 项及其余 .NET/Web 测试全部通过，Release 0 warning/0 error；
  `verify-dev-user.sh`、`verify-third-party.sh` 和 `git diff --check` 也已通过。
- 内容复制、workspace/current 发布、retired 清理和迁移物理搬运的 Linux 主路径已收敛到 dirfd、
  `openat`/`renameat`、nlink 校验和 fsync；只读树清理也有显式安全边界；
- `CONTENT_READY` 已成为可持久恢复边界：重启对账可补做数据库提交或恢复旧树并保留恢复现场，
  长复制/解析会续租，retired 树经过 operation 状态和租约检查后才清理；
- 文本读取/写入和下载已提供强 ETag 及创建前置条件；验证/启用/绑定/上传使用持久幂等键，搜索
  使用签名且绑定 workspace revision 的稳定游标，游戏读写/搜索/验证使用独立速率限制；
- 诊断持久化增加管理员显式 override 白名单和审计；新增 `plan-game-collapse`、身份绑定的歧义
  选择文件、旧 DataRoot 物理迁移、迁移 journal/backup manifest 及可重跑收尾；

P1-04 收尾（2026-08-09 完成）：

- dirfd/TOCTOU 审计：写路径全部锚定 dirfd/openat/renameat；只读遍历（目录/文本/下载/搜索）逐段
  执行 ReparsePoint 检查并 RejectLink，下载在打开句柄后复核 nlink/归属；工作区与 current 树在
  复制/写入时强制无符号链接，读取路径以逐段 lstat + 链接拒绝兜底。残余的 check-then-use 竞态
  需要本地写权限访问应用私有 0700 数据目录，不在单容器 MVP 的 Web 威胁模型内；已用
  `ReadTraversalRejectsSymlinksInsideContentTrees` 固化 list/read/download 对树内符号链接的拒绝。
- 平台差异验证：把非 Linux managed fallback（copy/replace/publish/delete）抽为 internal 助手并
  直接测试——符号链接拒绝、原子交换与 retired 保留、普通树删除；Linux dirfd 主路径保持默认。
- 故障矩阵扩充：真实 SIGKILL 的 parser 进程终止、Validator 崩溃转为持久阻断诊断且 workspace
  CAS 回 DRAFT、DB 已提交但 operation 未 COMMITTED 的 CONTENT_READY 补标、lease 过期的
  RUNNING operation 标 FAILED 并清理 work。连同既有覆盖（CONTENT_READY 树不匹配恢复、retired
  安全期/租约、共享 current 隔离、BLOCK/DELETE 引用保护、大清单入口上限）组成完整故障矩阵。

2026-08-09 review 收尾（535239b 之后）：

- 修复签名搜索游标篡改测试的不稳定：base64url 末字符只编码 4 个有效位（'w'→'x' 解码出相同尾
  字节），原测试约 6% 运行把"被篡改"游标解码成原签名而不抛异常；测试改为篡改完全有效的中间
  字符后 `check.sh` 重新稳定；
- Game 错误码按 §15 对齐：新增 GAME_NAME_CONFLICT、GAME_IN_USE、GAME_HAS_NO_CURRENT_CONTENT、
  WORKSPACE_NOT_FOUND、WORKSPACE_ALREADY_EXISTS、FILE_NOT_FOUND、FILE_TYPE_NOT_EDITABLE、
  FILE_TOO_LARGE_TO_EDIT、TEXT_ENCODING_UNSUPPORTED、TEXT_NOT_REPRESENTABLE、
  VALIDATION_IN_PROGRESS、ACTIVATION_IN_PROGRESS、ACTIVATION_VALIDATION_FAILED；冲突码改为
  GAME_STATE_CONFLICT；Validator 协议错误码改为 VALIDATOR_PROTOCOL_ERROR；
- 审计动作改为计划清单：GAME_CREATE/GAME_UPDATE/GAME_DELETE/GAME_BLOCK/GAME_UNBLOCK/
  GAME_PACKAGE_UPLOAD/GAME_WORKSPACE_CREATE/GAME_WORKSPACE_DISCARD/GAME_FILE_UPDATE/
  GAME_FILE_DELETE/GAME_VALIDATE/GAME_ACTIVATE/GAME_DIAGNOSTIC_OVERRIDE；删除未使用的
  `VersionLabelMaxLength` 常量；
- API 暴露 ASP.NET Core OpenAPI 文档端点 `/openapi/v1.json`（设计文档"HTTP 契约由 OpenAPI 生成"
  在 P1-04 的落地部分），契约测试断言 games 路径存在且全文不含 GameVersion；生成式 TypeScript
  客户端与 WebSocket JSON Schema 类型生成推迟到 P1-10 浏览器游戏库任务，作为 P1-04 遗留接口
  记录；
- §12 的 Session 创建/启用并发完整集成测试按计划属于 P1-06；P1-04 交付 copy lease 端口和
  rename 后 dirfd 读取测试。

## 1. 目标结果

P1-04 完成后，产品和公开契约中只有 Game，没有 GameVersion：

```text
Game
├── metadata / owner / visibility / status
├── workspace/          # 最多一份，owner 可编辑
└── content/            # 最多一份，当前可运行，只读复制来源

Session
├── gameId
├── sourceContentDigest + runtimeManifest snapshot
└── private persistent SessionRoot
```

用户上传或编辑唯一 workspace，验证成功后把它原子启用为 current content。系统不保存可选择的
历史版本，不提供版本标签、版本列表、回滚或“基于某版本创建 Session”。Game 后续变化不影响已有
Session，因为 Session 创建时已经把当时内容复制到自己的 SessionRoot。

本步骤还必须清除 P1-01 已实现的 GameVersion 产品模型：新增 migration 转换数据并删除
`game_versions`，修改实体、授权、测试和 RuntimeAdapter 命名。已提交 migration 文件仅作为升级
起点保留，不回写、不删除。

## 2. 非目标和简化边界

- 不保留 Game 内容历史或产品级回滚；恢复依赖重新上传或 `/data` 运维备份；
- 不允许直接原地编辑 current content；workspace 是保证最后可运行内容稳定所需的内部工作区，
  不是版本资源；
- `content_revision` 只是 CAS 计数，不生成 ID、标签、列表或按 revision 访问 API；
- 不把旧 content 长期保留成隐藏版本；只允许 operation 安全期内的临时 retired 目录；
- 不更新已有 SessionRoot，不把 Game 编辑传播到运行中或已关闭 Session；
- 不在 P1-04 实现 Session API、Web UI 或正式 NsJail 沙箱；
- 不把同 digest 的不同 Game 合并到共享可写/可寻址内容对象；
- 不修改 Emuera 原生存档格式和 SessionRoot 直接持有存档的语义。

## 3. Game 状态模型

### 3.1 两个正交状态

`status` 管理能否被读取/启动：

```text
ACTIVE ──admin block──> BLOCKED
  │                       │
  └────logical delete─────┴──> DELETED
BLOCKED ──admin unblock──> ACTIVE
```

`workspace_status` 管理编辑流水线：

```text
NONE ──upload/edit──> DRAFT ──CAS──> VALIDATING
  ▲                     ▲              │
  │                     └──failed──────┤
  └────────activated successfully──────┘
```

组合语义：

- 新 Game 可以是 ACTIVE + DRAFT，但在 `content_digest IS NULL` 时不可创建 Session；
- ACTIVE + NONE/DRAFT 且 current content 存在时均可创建 Session；DRAFT 不影响 current；
- VALIDATING 冻结 workspace 写入，current 仍可供 Session 创建；
- BLOCKED 保留 workspace/current 和既有 SessionRoot，但禁止新 Session；
- DELETED 对普通资源 API 表现为不存在，且不能有活动 operation。

### 3.2 不变量

- 一个 Game 最多一个 workspace 和一个 current content；
- workspace 可写，current content 只读且不能通过文件 API 获取写句柄；
- `content_revision >= 0` 且每次成功启用恰好加一；
- current metadata 要么全部为空，要么 digest/path/manifest/runtime config/activated fields 全部存在；
- workspace_status NONE 时 workspace_path 为空，DRAFT/VALIDATING 时存在；
- Game 被 Session 引用时不能 DELETED；
- Session 固定 `game_id + source_content_digest + runtime_manifest_json`，不固定内部 revision 资源；
- Game 内容替换和 Session 创建竞争时，SessionRoot 只能是完整旧树或完整新树。

## 4. 目标数据库模型

### 4.1 games

在已有 `games` 上增加：

```text
status TEXT NOT NULL                  -- ACTIVE | BLOCKED | DELETED
workspace_status TEXT NOT NULL        -- NONE | DRAFT | VALIDATING
workspace_path TEXT NULL
current_content_path TEXT NULL
content_digest TEXT NULL
content_revision INTEGER NOT NULL DEFAULT 0
manifest_json TEXT NOT NULL DEFAULT '{}'
runtime_config_json TEXT NOT NULL DEFAULT '{}'
compatibility_summary_json TEXT NOT NULL DEFAULT '{}'
activated_by TEXT NULL FK users
activated_at INTEGER NULL
deleted_by TEXT NULL
deleted_at INTEGER NULL
```

不要给 `content_digest` 建唯一索引。两个不同 owner 或两个 Game 上传相同内容是合法的；digest 是
一致性证据，不再是全局 GameVersion 身份。

CHECK：

- status、workspace_status 使用稳定大写 allowlist；
- content/workspace path 仍是受限相对 DataRoot 路径；
- digest、JSON、时间、ID 沿用 P1-01 约束；
- current 字段全空或成组存在；
- deleted 字段与 DELETED 状态成对；
- `content_revision/state_version >= 0`。

### 4.2 sessions

删除：

```text
game_version_id
FK(game_version_id, game_id) -> game_versions
ix_sessions_game_version*
```

增加：

```text
source_content_digest TEXT NOT NULL
source_content_revision INTEGER NOT NULL
runtime_manifest_json TEXT NOT NULL
```

保留 `game_id` FK RESTRICT。`source_content_revision` 只用于诊断和竞态核对，不使旧内容成为可读取
资源；真正的运行快照是 SessionRoot。增加 `(game_id, source_content_digest)` 普通索引。

### 4.3 game_files

```text
game_id TEXT NOT NULL FK games
scope TEXT NOT NULL                    -- WORKSPACE | CURRENT
logical_path TEXT NOT NULL
entry_kind TEXT NOT NULL               -- FILE | DIRECTORY
byte_length INTEGER NOT NULL
content_digest TEXT NULL
file_kind TEXT NULL                    -- TEXT | BINARY
text_encoding TEXT NULL                -- UTF8 | UTF8_BOM | SHIFT_JIS | UNKNOWN
has_bom INTEGER NULL
PRIMARY KEY(game_id, scope, logical_path)
```

WORKSPACE 行是可重建索引，物理 workspace 是草稿权威；CURRENT 行与 canonical manifest、物理树
和 Game digest 在启用点一致，之后不可普通更新。

### 4.4 compatibility_diagnostics

```text
id TEXT PRIMARY KEY
game_id TEXT NOT NULL FK games
workspace_revision INTEGER NOT NULL
stage TEXT NOT NULL
severity TEXT NOT NULL
code TEXT NOT NULL
logical_path TEXT NULL
line_number INTEGER NULL
message_key TEXT NOT NULL
arguments_json TEXT NOT NULL
publish_blocking INTEGER NOT NULL
override_policy TEXT NOT NULL          -- NEVER | ADMIN
overridden_by TEXT NULL
overridden_at INTEGER NULL
created_at INTEGER NOT NULL
```

数据库列沿用 `publish_blocking` 会继续泄漏旧术语，实施时应改为 `activation_blocking`。公开 DTO 只
使用 activation。若迁移前尚不存在该表，直接使用新名称。

### 4.5 game_content_operations

```text
id TEXT PRIMARY KEY                    -- gop_<UUIDv7>
game_id TEXT NOT NULL FK games
operation_type TEXT NOT NULL           -- IMPORT | RESET_WORKSPACE | VALIDATE | ACTIVATE
status TEXT NOT NULL                   -- PENDING | RUNNING | CONTENT_READY | COMMITTED | FAILED
expected_game_state_version INTEGER NOT NULL
expected_content_revision INTEGER NOT NULL
ingestion_id TEXT NULL
work_path TEXT NULL
content_digest TEXT NULL
lease_expires_at INTEGER NOT NULL
error_code TEXT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
completed_at INTEGER NULL
state_version INTEGER NOT NULL
```

活动唯一索引保证每个 Game 最多一个 PENDING/RUNNING/CONTENT_READY operation。所有状态转换同时匹配
operation 状态/版本和 Game expected state；长文件操作不放在 SQLite 事务里。
`ingestion_id` 对非 NULL 值建立唯一索引，使一个 READY 候选不能绑定两个 Game/workspace。

## 5. 旧 schema 数据迁移

### 5.1 原则

- 不修改 `20260807071428_InitialMetadata` 或任何已提交 designer/snapshot 历史；
- 新增 `CollapseGameVersionsIntoGames` migration 和迁移前布局检查；
- Migrator 仍先持独占锁、备份数据库、执行完整性检查；
- 数据库转换和物理目录转换必须可重入、有 operation/marker、失败前置或可恢复；
- 无法无损决定当前内容时在修改数据库前失败，不静默选错版本；
- 自动化 fixture 必须覆盖空库、无 version Game、单 version、多个 version、Session 跨版本和异常路径。

### 5.2 选择规则

对每个旧 Game 计算：

1. `currentCandidate`：状态 PUBLISHED 的行按 `published_at DESC, created_at DESC, id DESC`；
2. 若超过一个 PUBLISHED 且摘要/路径不同，自动迁移前返回
   `LEGACY_GAME_CURRENT_AMBIGUOUS`，要求管理员使用迁移辅助命令显式选择；
3. BLOCKED 只有一个时可作为 currentCandidate，并把 Game 迁为 BLOCKED；
4. DRAFT/VALIDATING 中最多一个可成为 workspaceCandidate；多个则
   `LEGACY_GAME_WORKSPACE_AMBIGUOUS`；
5. 没有 current、只有一个 DRAFT 时迁为 workspace，无 current content；
6. DELETED 旧版本不迁为 current/workspace，只进入备份后的遗留内容清单；
7. 同一旧 version 的 metadata/manifest/digest/path 必须先通过 CHECK 和物理树复核。

不允许简单使用“最新 ID”默选多个不同 PUBLISHED，因为那可能让未来 Session 使用错误游戏内容。

Migrator 增加只读 `plan-game-collapse` 子命令，输出不含正文/绝对路径的 JSON 报告；无歧义项自动
标为 selected，歧义项列出旧 ID、状态、摘要、时间和 Session 引用计数。管理员可以生成显式
selection JSON 后通过 `migrate --game-collapse-plan <file>` 执行。计划文件必须绑定数据库 inode、
schema version 和报告摘要，数据库变化后拒绝复用；计划和错误日志不得泄露游戏文件名。

### 5.3 Session 转换

每个旧 Session 通过原 `game_version_id` 读取：

- `source_content_digest = oldVersion.content_digest`；NULL 时迁移失败，除非 Session 状态/根目录可
  通过安全重扫得到 digest；
- `runtime_manifest_json` 由 old manifest + runtime config 规范构造；
- `source_content_revision` 使用迁移生成的只读诊断序号，不建立旧内容引用；
- 保留 `game_id/runtime_version/session_root_path`；
- 校验 SessionRoot 存在性不应成为 DB migration 的普通 EF SQL 副作用；由迁移 preflight/report
  输出异常，数据库外物理校验在 DataRoot migrator 阶段完成。

Session 已拥有私有内容，因此其旧 GameVersion 即使不是 currentCandidate，也不必在游戏库保留。

### 5.4 物理布局转换

旧预期布局可能是：

```text
games/{gameId}/{gameVersionId}/content/
games/{gameId}/drafts/{gameVersionId}/content/
```

目标：

```text
games/{gameId}/content/
games/{gameId}/workspace/
```

DataRoot migrator 必须：

- 只按数据库选择结果和受保护父目录句柄打开旧目录；
- 核对 owner marker、普通文件类型、manifest 和 digest；
- 在同一 Game 下创建 `.migration-{operationId}`，复制/rename 后 fsync；
- 数据库提交前保留可恢复 marker，提交后才把未选旧目录移到
  `backups/legacy-game-content/{migrationId}/`；
- 不在首次升级中直接永久删除未选内容；备份清单记录旧 version ID、Game ID、digest 和路径；
- 任一链接、特殊文件、归属或摘要异常 fail closed；
- 空目录/实际从未写入的 P1-01 测试 schema 可以只完成数据库表转换。

### 5.5 表重建顺序

SQLite migration 建议顺序：

1. 创建目标 `games_new`、`sessions_new` 和新辅助表；
2. 基于 preflight 选择表填充 Game current/workspace metadata；
3. join `sessions -> game_versions` 填充源摘要/manifest；
4. 校验行数、FK、NULL、digest 和 JSON；
5. 删除旧 FK 依赖表，rename new tables；
6. 删除 `game_versions`；
7. 重建索引、trigger 和 EF snapshot；
8. `foreign_key_check`、`quick_check` 和产品 schema contract；
9. 物理布局完成后写 migration completion marker。

## 6. 代码修改指导

### 6.1 Persistence

删除产品代码：

- `Entities/GameVersionRow.cs`；
- `Configurations/GameVersionConfiguration.cs`；
- `CloudEmueraDbContext.GameVersions`；
- `SqliteStorageConventions.GameVersionsTable`；
- `GameRow.Versions`；
- `SessionRow.GameVersionId/GameVersion`；
- `GameVersionStatus` 和 VersionLabel 限制。

修改：

- `GameRow` 加第 4.1 节字段、content/workspace 导航和状态；
- `SessionRow` 加 source digest/revision/manifest；
- `GameConfiguration` 加成组 CHECK、路径、digest、JSON、时间、blocked/deleted 约束；
- `SessionConfiguration` 删除复合 version FK/index，增加 Game FK/source 字段 CHECK/index；
- `CloudEmueraDbContextModelSnapshot` 只通过新 migration 生成结果，不手工伪造历史 designer；
- `PersistenceFixtures` 以 Game current content 和 Session source snapshot 创建 fixture；
- `InitialMigrationTests` 区分历史迁移表和最新产品 schema：历史 migration 可出现
  `game_versions`，升级后的最终 schema 必须不存在。

禁止直接删除旧 migration 文件，否则已有 DataRoot 不能升级且 migration history 会失配。

### 6.2 Authorization

删除：

- `ResourceKind.GameVersion`；
- `ResourceAction.GameVersionRead/GameVersionMutate`；
- `SqliteResourceAccessReader` 的 version join；
- 所有 `/game-versions` 授权调用。

Game action 调整为：

```text
GameRead
GameMutate
GameValidate
GameActivate
GameBlock
```

SERVER_SHARED 只共享 Game current content/read/download；workspace、诊断正文和 operation 仅 owner。
管理员默认不读取他人私有内容，GameBlock 只读最小元数据并必须审计。

### 6.3 RuntimeAdapter 重命名

当前 P0 代码中的 `GameVersionRoot` 是复制源的历史名称，不再保留：

```text
GameVersionRoot                 -> GameContentRoot
GameVersionIdentity             -> GameContentDigest
SessionRootPublishedManifest    -> GameContentManifest
ValidatePublishedGameVersion    -> ValidateGameContentSource
<game-version-root> diagnostic  -> <game-content-root>
```

涉及：

- `RuntimePaths.cs`；
- `SessionRootLayout.cs`；
- `SessionRootLayoutBuilder.cs`；
- `SessionRootManifest.cs`；
- RuntimeAdapter/RuntimeCompatibility/Worker 集成测试 workspace helpers。

这只是平台端口术语迁移，不改变 P0-05 完整复制、禁止链接、无共享 inode、原子 SessionRoot 和
两种存档布局行为。可保留一个发布周期的 `[Obsolete]` 内部兼容别名仅在确有跨项目编译需要时；
公开/产品契约不得继续输出 GameVersion。

### 6.4 Web 和 Contracts

- 删除 `gver_` ID、GameVersion DTO、version label/status、版本列表和 `/game-versions` schema；
- Game 详情增加 `hasCurrentContent/contentDigest/contentRevision/workspaceStatus/diagnosticSummary`；
- Session DTO 用 `gameId/sourceContentDigest`；
- 当前占位 Web 中“版本”列改为内容状态/摘要短值；
- OpenAPI、生成 TypeScript 类型和测试 fixture 同步；
- 搜索全仓，产品代码除历史 migration compatibility 外不得再出现旧术语。

### 6.5 P1-03 消费契约

保留 `IGamePackageIngestionService`，扩展：

- `RenewConsumeAsync`；
- Complete 对相同 owner/Game workspace 提交证明幂等；
- `games.source_ingestion_id` 或 IMPORT operation 的唯一 ingestion 绑定；
- reconciler 在 Game workspace 已提交、ingestion 仍 CONSUMING 时补 Complete；
- 无提交证明且 lease 到期时才 Abandon；
- 消费方仍只取得目录句柄，不取得 staging 字符串路径。

## 7. 文件系统布局和安全端口

```text
/data/games/{gameId}/
├── owner.json
├── workspace/                       # 0700，可编辑
├── content/                         # 0555；文件 0444
├── runtime-manifest.json            # current 内容清单
└── operations/
    ├── {operationId}/work/
    └── {operationId}/retired/       # 仅安全期内存在
```

- Game/operation ID 只来自数据库；用户 path 永远是 NFC `/` 逻辑相对路径；
- `owner.json` 绑定 Game ID、父目录 device/inode、schema version；
- mkdir/open/rename/unlink/chmod/fsync 全部锚定 dirfd 并 `O_NOFOLLOW`；
- current content 不能原地写；启用创建新的 operation tree；
- retired 不是版本资源，不可通过 API 读取，operation 完成/安全期后回收；
- 普通复制为基线，reflink 可优化，硬链接/共享可写 inode禁止；
- Game source 和 SessionRoot 复制前后都重验类型、nlink、inode、计数、实际字节和 digest。

## 8. HTTP API

```text
GET    /api/v1/games
POST   /api/v1/games
GET    /api/v1/games/{gameId}
PATCH  /api/v1/games/{gameId}
DELETE /api/v1/games/{gameId}

PUT    /api/v1/games/{gameId}/package
POST   /api/v1/games/{gameId}:edit
DELETE /api/v1/games/{gameId}/workspace
GET    /api/v1/games/{gameId}/files?scope=workspace|current&path=...
GET    /api/v1/games/{gameId}/file?scope=workspace|current&path=...
PUT    /api/v1/games/{gameId}/file?path=...
DELETE /api/v1/games/{gameId}/file?path=...
GET    /api/v1/games/{gameId}/download?scope=workspace|current&path=...
GET    /api/v1/games/{gameId}/search?scope=workspace|current&q=...
POST   /api/v1/games/{gameId}:validate
POST   /api/v1/games/{gameId}:activate
GET    /api/v1/games/{gameId}/operations/{operationId}
```

不存在版本路由。`scope=current` 对 SERVER_SHARED 可读；workspace 只 owner。PUT/DELETE 文件隐含
workspace，current 永不允许写。

通用约定：

- 写操作认证、首次改密检查、CSRF、owner 授权和独立速率限制；
- metadata/activate 使用 `If-Match: "<gameStateVersion>"`；文件使用强 SHA-256 ETag；
- upload/activate 等可重试操作要求 Idempotency-Key；
- path 放 query，避免 catch-all route 和 encoded slash 二次解释；
- 不存在、DELETED、无权统一 404；状态冲突 409；ETag 失败 412；验证失败 422；
- 大文件流式处理，不整体载入内存。

## 9. workspace 生命周期

### 9.1 新上传/替换

```text
GameMutate authorization
 → P1-03 IngestAsync(request.Body)
 → IMPORT operation + BeginConsume owner/digest CAS
 → 从内容目录句柄复制到 operations/{id}/work
 → 周期续租，重验 manifest/type/inode/digest
 → 原子替换 workspace（旧 workspace 进入本 operation retired）
 → SQLite CAS: workspace_status=DRAFT，更新 WORKSPACE game_files
 → CompleteConsume + operation COMMITTED
```

替换 workspace 不改 current content。请求断开在持久 operation 建立前可以取消；建立后只停止
HTTP 等待，operation 继续或由 reconciler 接管。

### 9.2 从 current 开始编辑

`POST :edit` 在 workspace NONE 时把 current 完整复制/reflink 到 operation work，验证无共享可写
inode后原子成为 workspace。已有 workspace 返回 409，不隐式覆盖用户未启用修改。

### 9.3 文本编辑

- 只允许 ERB、CSV 和明确配置类文本；二进制只读/下载；
- 读取返回 encoding/BOM/byteLength、强 ETag 和 Game stateVersion；
- 修改要求 If-Match，创建要求 If-None-Match:*；
- encoding 为空时保持原编码/BOM；显式转换才改变；CP932 不可表示字符返回 422；
- 同目录随机临时文件 0600，写入/flush/fsync 后 rename；
- 成功编辑递增 Game stateVersion，清除当前 workspace revision 的验证结果；
- 文件成功而索引更新失败时物理 workspace 为权威，reconciler 重扫，不凭旧 DB 内容覆盖文件。

### 9.4 搜索

仅扫描指定 scope 的 TEXT 清单；使用严格记录编码、字面量查询、稳定 path/line/column 排序和签名
游标。限制 query 字节、文件数、总解码字节、单行、结果、片段和耗时；日志不记录 query/正文。

## 10. Validator

activate 前冻结 workspace：CAS DRAFT→VALIDATING，复制/打开只读 operation snapshot。阶段：

1. Tree：普通类型、路径、碰撞、nlink/inode、实际配额；
2. Structure：ERB/CSV/config/入口；
3. Encoding：严格 UTF-8/BOM/CP932；
4. Capability：CALLSHARP、进程、DLL、任意网络；
5. Resources：图片/音频/Sprite 引用与大小写；
6. Parser：真实固定 Emuera parser-only；
7. Manifest：运行时基线、compatibility profile、save layout、能力、诊断；
8. Digest：canonical file manifest 和 content digest。

真实 parser 在一次性 `CloudEmuera.Validator` 进程，API 不加载 EmueraRuntime。Validator 只读冻结
根、不进入游戏主循环、不接受输入、不执行 CALLSHARP；stdout 是有界版本化 JSON，超时/崩溃/
超输出/非法协议产生稳定阻断诊断。P1-12 再接入正式 namespace/cgroup/seccomp。

## 11. current content 原子启用

```text
DRAFT CAS → VALIDATING + ACTIVATE operation RUNNING
  → 冻结 workspace 到 operation work
  → 全量 Validator + manifest + digest
  → 文件 0444 / 目录 0555 / fsync
  → current 存在时 rename 到本 operation retired
  → work 原子 rename 为 content
  → operation CONTENT_READY
  → SQLite 短事务：
       匹配 Game expected state/workspace/content revision
       CURRENT game_files 替换为新清单
       写 digest/path/manifest/runtime config/activated fields
       content_revision += 1
       workspace_status=NONE, workspace_path=NULL
       operation=COMMITTED
```

启用不创建新资源 ID。失败诊断保存在 Game 上，workspace 回 DRAFT，current 不变。成功后新 Session
使用新 digest；所有既有 SessionRoot 保持原样。

旧 current 进入 operation retired 只是跨提交窗口的临时恢复材料；DB 提交、无活动 copy lease 且
超过安全期后回收，不建立列表/下载/回滚接口。

## 12. Session 创建与内容替换并发契约

P1-06 必须采用以下协议，P1-04 先提供端口和测试替身：

1. SQLite 短事务读取 ACTIVE Game 的 `content_revision + digest + manifest`，创建 CREATING Session，
   写入相同 source snapshot；
2. 内容存储按 `gameId + expected revision + digest` 打开 current 目录句柄，并注册有期限 copy lease；
3. 目录 rename 后已打开 dirfd 仍指向同一树；ACTIVATE 不能在 lease 终结前删除 retired；
4. 复制到 Session staging root，逐项校验并计算 digest；
5. 只有结果等于 Session `source_content_digest` 才原子发布 SessionRoot；
6. 失败清理本次 Session staging，Session 标记明确失败/CRASHED，不退而复制当前另一 revision；
7. 释放 copy lease；reconciler 才可回收 retired。

并发结果只能是完整旧 digest 或完整新 digest。不能对路径先读 revision、随后在 rename 后重新按
字符串打开并混合两树。

建议 `game_content_copy_leases`：

```text
id TEXT PK
game_id TEXT NOT NULL
content_revision INTEGER NOT NULL
content_digest TEXT NOT NULL
consumer_type TEXT NOT NULL              -- SESSION_CREATE | VALIDATION
consumer_id TEXT NOT NULL
expires_at INTEGER NOT NULL
created_at INTEGER NOT NULL
UNIQUE(consumer_type, consumer_id)
```

这是短期复制租约，不是版本引用；SessionRoot 完成后即可释放。

## 13. 对账和故障窗口

| 状态 | 对账 |
| --- | --- |
| IMPORT RUNNING 只有 work | 验证归属后续作或回收 |
| workspace 已提交，ingestion CONSUMING | 核对 Game/owner/digest 后 CompleteConsume |
| RESET_WORKSPACE 中断 | current 不动，续作或回收 work |
| VALIDATING 到期无进程 | 写中断诊断，CAS 回 DRAFT |
| ACTIVATE RUNNING 只有 work | 从完整阶段重新验证，不相信内存进度 |
| CONTENT_READY，FS 已换、DB 仍旧 | 用 owner marker/manifest/digest/expected state 完成 CAS 或恢复旧 current |
| DB 已新 digest，operation 未 COMMITTED | 验证 current 后补标 COMMITTED |
| retired 有活动 copy lease | 延迟清理 |
| 未知/归属异常目录 | 不删除，告警 |

数据库终态先线性化，再以受保护 fd 后序清理，最后记录 cleanup 时间。不得使用普通
`Directory.Delete(recursive: true)` 处理未知树。

## 14. 删除和 BLOCK

- Game DELETE 与 Session 创建在事务中竞争；任意 Session 引用存在即 `409 GAME_IN_USE`；
- CLOSED/CRASHED Session 仍是引用；
- 无引用 Game 先 DELETED，停止 operation，内容在恢复期后安全回收；
- DELETE 请求不直接递归删目录；
- 管理员 BLOCK 只阻止新 Session，不改 Game content、workspace 或既有 SessionRoot；
- BLOCK/UNBLOCK/DELETE 都审计，管理员不因此获得私有文件正文读取权。

## 15. 稳定审计和错误

审计动作：

```text
GAME_CREATE, GAME_UPDATE, GAME_PACKAGE_UPLOAD
GAME_WORKSPACE_CREATE, GAME_FILE_UPDATE, GAME_FILE_DELETE
GAME_VALIDATE, GAME_ACTIVATE, GAME_WORKSPACE_DISCARD
GAME_BLOCK, GAME_UNBLOCK, GAME_DELETE
GAME_DIAGNOSTIC_OVERRIDE
```

错误码：

```text
GAME_NOT_FOUND
GAME_NAME_CONFLICT
GAME_HAS_NO_CURRENT_CONTENT
GAME_BLOCKED
GAME_IN_USE
GAME_STATE_CONFLICT
WORKSPACE_NOT_FOUND
WORKSPACE_ALREADY_EXISTS
WORKSPACE_NOT_EDITABLE
FILE_NOT_FOUND
FILE_CHANGED
FILE_TYPE_NOT_EDITABLE
FILE_TOO_LARGE_TO_EDIT
TEXT_ENCODING_UNSUPPORTED
TEXT_NOT_REPRESENTABLE
SEARCH_LIMIT_EXCEEDED
VALIDATION_IN_PROGRESS
ACTIVATION_IN_PROGRESS
ACTIVATION_VALIDATION_FAILED
VALIDATOR_TIMEOUT
VALIDATOR_PROTOCOL_ERROR
INGESTION_NOT_READY
INGESTION_OWNER_MISMATCH
GAME_STORAGE_FAILED
LEGACY_GAME_CURRENT_AMBIGUOUS
LEGACY_GAME_WORKSPACE_AMBIGUOUS
```

日志/错误不含正文、搜索词、绝对路径、inode、SQLite 原始错误、Validator stderr 或其他用户 ID。

## 16. 逐文件实施清单

### 16.1 第一提交：文档和迁移契约

- ADR-0010、需求中英文、总体设计、开发计划；
- migration preflight report DTO 和 legacy fixture；
- 明确旧 migration 不改、升级失败码和物理布局操作顺序。

### 16.2 第二提交：Persistence schema collapse

- 新 migration `CollapseGameVersionsIntoGames`；
- 修改 `GameRow/GameConfiguration/SessionRow/SessionConfiguration`；
- 删除非 migration 产品 `GameVersionRow/Configuration/DbSet/enum/constants`；
- 更新 snapshot、fixtures、constraint/migration tests；
- 验证最终 SQLite schema 不含 `game_versions/game_version_id/gver_`。

### 16.3 第三提交：授权和 Contracts

- 删除 ResourceKind/Action GameVersion；
- Game action 和共享 current/workspace 隔离；
- 删除 GameVersion DTO/route/TypeScript type；
- Session contract 改 source digest；
- 授权回归证明 SERVER_SHARED 不泄漏 workspace。

### 16.4 第四提交：RuntimeAdapter 术语迁移

- 第 6.3 节符号重命名；
- 全部 RuntimeAdapter/compatibility/Worker tests 更新；
- 保持 P0-05 行为和 manifest digest 可重复；
- 架构检查禁止新产品代码出现旧术语。

### 16.5 第五提交：安全 Game content store

- dirfd/owner marker/workspace/content/operation/retired；
- copy/reflink fallback、atomic rename、fsync、read-only、safe cleanup；
- 文件系统恶意/TOCTOU/配额测试。

### 16.6 第六提交：P1-03 workspace binding

- PUT package adapter、续租、IMPORT operation、对账；
- ingestion 和 Game workspace 提交故障矩阵；
- 非 seek 真实 HTTP upload。

### 16.7 第七提交：Game/文件/搜索 API

- CRUD、workspace lifecycle、目录、文本 ETag、下载、搜索；
- CSRF、速率限制、owner/shared 边界、审计；
- OpenAPI 和 TypeScript 生成契约。

### 16.8 第八提交：Validator 和 activation

- parser-only host/protocol；
- 静态/资源/能力/parser stages；
- ACTIVATE operation、manifest/digest/current swap；
- crash reconciler 和 copy lease port。

### 16.9 第九提交：删除、迁移工具和全量验收

- Session 引用删除竞争、BLOCK；
- legacy data/layout migrator 和 operator report；
- 全仓旧术语 audit、全量测试、文档实际记录。

所有提交使用 Conventional Commits 固定格式和 `git commit -s`。不得把 P1-05/P1-06/P1-10/P1-12
完整功能混入；只提供它们依赖的 Game content snapshot 端口。

## 17. 测试矩阵

### 17.1 Migration

- 空 P1-03 数据库升级；
- Game 无 versions；单 DRAFT；单 PUBLISHED；单 BLOCKED；
- current + 单 draft；多个相同摘要 published；多个不同摘要 published（安全失败）；
- Session 引用 current/非 current/DELETED version；
- NULL digest、坏 JSON、路径缺失、链接/特殊文件、摘要不符；
- migration 中断、备份、重跑、foreign_key_check/quick_check；
- 最终 schema 无旧表/列/索引，数据行数和审计不丢失。

### 17.2 Game library

- PRIVATE/SHARED 列表和不可枚举 404；
- 新建、重名、If-Match、BLOCK/DELETE；
- Game 无 current 时 Session 前置检查失败；
- workspace 与 current 并存且共享用户只看 current；
- 无任何版本路由、标签或列表字段。

### 17.3 Ingestion/workspace

- UTF-8/CP932 非 seek ZIP 建 workspace；
- 替换 workspace 不改 current；
- ingestion 一次消费、续租、Complete 崩溃对账；
- 编辑 ETag 冲突、编码保持/转换、二进制拒绝；
- 路径穿越、Unicode/大小写碰撞、TOCTOU；
- 搜索各项 exactly-limit/limit+1。

### 17.4 Activation

- 结构/编码/parser/缺资源/Blocked capability；
- Validator 超时、kill、超输出、非法协议；
- 相同 workspace 重验 digest 可重复；
- activate 后 current 只读，workspace NONE；
- 启用失败 current 不变、workspace 可修复；
- 两个 activate 只有一个 CAS 胜者；
- 各 rename/DB/operation 故障点重启收敛。

### 17.5 Session isolation contract

- 创建与 activation 并发只得到完整旧/新 digest；
- 已有 SessionRoot 在 Game 编辑/启用/BLOCK 后 byte-for-byte 不变；
- 两用户/同用户两 Session 无共享可写 inode；
- Game delete 与 Session create 竞争只有一个合法结果；
- Worker 看不到 workspace/current，只看自己的 SessionRoot。

### 17.6 Architecture/terminology

在最新产品源码和公开契约中拒绝：

```text
GameVersion
game_versions
game_version_id
gver_
/game-versions
versionLabel（游戏内容语境）
```

允许范围仅限已提交历史 migration、ADR-0009 历史文本、迁移兼容读取器及明确标注的升级测试。

## 18. 验证命令

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore \
  --configuration Release --filter 'Category=Migration|Category=PersistenceConstraint'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore \
  --configuration Release --filter 'Category=GameLibrary'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore \
  --configuration Release --filter 'Category=RuntimePaths|Category=Architecture'
```

完整验证：

```bash
./scripts/dev-up.sh
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
```

## 19. 完成定义

1. 中英文 GAME-004～010 和全部引用关系只描述 Game + Session source snapshot；
2. 公开 API、Contracts、Web 类型和最新产品 schema 不存在 GameVersion；
3. 旧数据库通过新增 migration 升级，歧义数据在修改前明确失败且备份可用；
4. Game 最多一个 workspace/current，启用是可恢复原子替换；
5. Game 编辑/启用绝不修改既有 SessionRoot；
6. Session create/activate 并发性质有真实文件系统集成测试；
7. P1-03 一次性消费、配额、dirfd 和安全拒绝不回退；
8. 文本编码、ETag、搜索和 Validator 有界且可重复；
9. SERVER_SHARED 只共享 current，管理员默认不读私有正文；
10. 引用 Game 不能删除，BLOCK 不破坏既有 Session；
11. RuntimeAdapter 旧命名迁移且 P0-05 行为测试全通过；
12. 定向测试、`./scripts/check.sh`、用户映射、第三方和 diff check 全通过；
13. 开发计划记录实际 migration 策略、测试数量、命令和遗留给后续任务的接口。
