# P1-01 SQLite 首版 schema 与迁移详细实现计划

状态：DONE（2026-08-07 已实现并通过验收）

计划日期：2026-08-07

对应开发步骤：`P1-01 — SQLite 首版 schema 与迁移`

前置条件：P0-01～P0-06 已完成

后续步骤：P1-02 本地身份、资源授权与审计；P1-03 安全游戏包摄取；P1-05
Supervisor、租约、epoch 与状态机；P1-06 幂等 Session 创建与关闭纵切

需求映射：核心领域模型、GAME-004/008/010、SESS-002/005/007/010、SAVE-005/015、
OPS-005、NFR-011

## 1. 任务结论

P1-01 要建立 CloudEmuera 第一版权威元数据数据库，以及唯一有权修改该数据库 schema 的
Migrator 执行路径。本步骤结束后，空数据目录可以确定性创建最新 SQLite schema，已有数据库
可以安全重复执行迁移，核心引用、唯一性、epoch、幂等键和只追加审计约束由数据库本身强制，
而不是只依靠未来 API 的调用顺序。

本步骤完成后应能证明：

- SQLite 是 User、Game、GameVersion、Session、WorkerLease、幂等记录和审计事件的权威
  元数据源；游戏内容与 SessionRoot 仍以文件系统为权威内容源；
- Session 记录固定的 GameVersion、Runtime 版本和私有 SessionRoot；数据库中没有
  SaveArtifact、save generation 或存档内容表；
- 同一 Session 最多存在一个 WorkerLease，租约 epoch 必须等于 Session 当前 epoch；
- GameVersion 与 Session 的 Game 关系不能交叉伪造，仍被 Session 引用的版本不能被物理删除；
- 同一 actor/scope/idempotency key 只能有一条记录；
- `audit_events` 在数据库层拒绝普通 `UPDATE` 和 `DELETE`；
- 所有进程使用相同的 SQLite 连接约定，开启 foreign keys、WAL 和 busy timeout；
- 只有 `CloudEmuera.Migrator` 获取独占迁移锁并调用 `MigrateAsync`，API、Supervisor 和 Worker
  不在启动时隐式修改 schema；
- 迁移失败返回稳定的非零退出码，不留下部分新 schema，并保留可诊断的迁移前备份。

P1-01 只提供持久化结构和迁移基础设施，不实现登录、游戏上传、Session HTTP API、活动配额
分配、租约续期或审计业务写入。这些行为由后续纵切使用本步骤的 schema 实现。

## 2. 范围

### 2.1 必须实现

1. 冻结 SQLite 字段类型、UTC 时间、ID、枚举、JSON、摘要和相对路径的存储约定；
2. 在 `CloudEmuera.Infrastructure` 中建立 EF Core `CloudEmueraDbContext`、持久化行模型和
   `IEntityTypeConfiguration<T>` 映射；
3. 建立 `quota_profiles`、`users`、`games`、`game_versions`、`sessions`、
   `worker_leases`、`idempotency_records` 和 `audit_events`；
4. 通过 EF Core migration 建立表、外键、检查约束、唯一索引、查询索引和审计保护 trigger；
5. 把 EF migration history 表固定为 `schema_migrations`；
6. 提供统一的 SQLite options/connection factory，确保每个实际连接启用必需 PRAGMA；
7. 把 `CloudEmuera.Migrator` 从占位程序改为可重复运行的独占迁移 CLI；
8. 在存在待执行迁移时使用 SQLite Online Backup API 创建迁移前一致性备份；
9. 新增 `tests/CloudEmuera.Infrastructure.Tests`，覆盖 migration、schema、约束、并发和失败回滚；
10. 提供从空临时数据目录运行真实 Migrator 进程的测试或验证脚本；
11. 更新解决方案、锁文件、配置说明、开发计划和需求—测试映射。

### 2.2 明确非目标

- 不实现 P1-02 的密码哈希、Cookie、登录、角色策略或资源授权器；
- 不调用 `UserManager` 创建首个管理员，也不在 migration 中写默认密码或生产用户；
- 不实现 P1-03/04 的上传任务、`game_files`、`compatibility_diagnostics`、草稿编辑或发布用例；
- 不实现 P1-05 的租约获取/续租/回收、epoch CAS、心跳超时或 Session 状态转换；
- 不实现 P1-06 的 Session 创建/关闭及幂等响应编排；
- 不实现 `worker_command_results`；等 P1-05 明确命令重放窗口和清理语义后再迁移；
- 不建立 Save、SaveArtifact、SaveGeneration 或文件逐项索引表；
- 不把大型 manifest、游戏包、SessionRoot 或存档二进制内容放入 SQLite；
- 不使用 `EnsureCreated`、启动时建表、手写 `CREATE TABLE IF NOT EXISTS` 代替 EF migration；
- 不支持 PostgreSQL、远程数据库、多主 Migrator 或分布式迁移锁；
- 不让 API、Supervisor、Worker 调用 `Database.Migrate()`；
- 不承诺兼容从未发布过 migration 的本地开发数据库。P1-01 是首个受支持 schema 基线；
  未来每次 schema 变更必须增加 migration 和真实 N-1 升级测试，不能修改已发布 migration。

## 3. 工程边界与建议文件布局

建议新增或调整：

```text
src/CloudEmuera.Infrastructure/
└── Persistence/
    ├── CloudEmueraDbContext.cs
    ├── CloudEmueraDbContextFactory.cs
    ├── SqliteDatabaseOptions.cs
    ├── SqliteConnectionFactory.cs
    ├── Migration/
    │   ├── DatabaseMigrationRunner.cs
    │   ├── MigrationLock.cs
    │   └── MigrationResult.cs
    ├── Entities/
    │   ├── QuotaProfileRow.cs
    │   ├── CloudEmueraUser.cs
    │   ├── GameRow.cs
    │   ├── GameVersionRow.cs
    │   ├── SessionRow.cs
    │   ├── WorkerLeaseRow.cs
    │   ├── IdempotencyRecordRow.cs
    │   └── AuditEventRow.cs
    ├── Configurations/
    │   └── <one configuration per entity>.cs
    └── Migrations/
        ├── <timestamp>_InitialMetadata.cs
        ├── <timestamp>_InitialMetadata.Designer.cs
        └── CloudEmueraDbContextModelSnapshot.cs
src/CloudEmuera.Migrator/
├── Program.cs
├── MigratorCommand.cs
└── appsettings.json
tests/CloudEmuera.Infrastructure.Tests/
├── CloudEmuera.Infrastructure.Tests.csproj
├── Persistence/
│   ├── InitialMigrationTests.cs
│   ├── PersistenceConstraintTests.cs
│   ├── SqliteConfigurationTests.cs
│   ├── MigrationFailureTests.cs
│   └── MigrationConcurrencyTests.cs
└── Support/
    ├── TemporarySqliteDatabase.cs
    └── TestMigrationContext.cs
```

具体文件可按实现语言习惯合并，但职责不得混合：

- `Domain` 保存业务枚举和状态转换规则，不引用 EF Core；
- `Infrastructure.Persistence.Entities` 是数据库行模型，不把 EF attribute 放进 Domain；
- `DbContext` 只负责模型和持久化，不承载 Session 状态机或授权逻辑；
- `DatabaseMigrationRunner` 是可复用基础设施服务，Migrator 的 `Program` 只负责配置、日志、
  取消信号和退出码；
- 后续 repository 在 Application 端口与行模型之间映射，不能向 API 返回 EF tracked entity；
- migration 文件和 model snapshot 是提交到 Git 的不可变发布资产。

现有 `CloudEmueraDbContext : IdentityDbContext<...>` 会隐式创建完整 `AspNet*` 表集，与
设计中首版自定义 `users` 表不一致。P1-01 应改为普通 `DbContext`，显式映射
`CloudEmueraUser` 到 `users`。P1-02 再实现适配该表的 ASP.NET Core Identity user store；
不得为了复用默认 `IdentityDbContext` 而悄悄扩大首版 schema。

## 4. 全局 SQLite 存储约定

### 4.1 ID

- 外部资源 ID 存为 `TEXT` 主键，使用 `StringComparer.Ordinal`/SQLite `BINARY` 比较；
- 新 ID 由应用生成不可预测的 UUIDv7 字符串：`usr_`、`game_`、`gver_`、`sess_`、
  `wrk_`、`qtp_`、`audit_`；
- migration 不自动生成资源 ID；数据库默认值不得使用递增整数或 `random()`；
- 映射配置最大长度并添加非空/前缀检查。检查只防明显损坏，完整 ID 解析由应用值对象负责；
- ID 和 idempotency key 大小写敏感，不使用 SQLite 内建 `NOCASE` 处理 Unicode 身份。

### 4.2 UTC 时间

- 所有 `*_at` 列统一使用 `INTEGER NOT NULL/NULL`，值为 Unix epoch milliseconds UTC；
- .NET 边界使用 `DateTimeOffset`，ValueConverter 写入/读出 `long`，拒绝 `DateTimeKind.Local`
  或无 offset 的字符串；
- 时间由注入的 `TimeProvider` 产生，不用 SQLite `CURRENT_TIMESTAMP` 作为业务时间；
- 对 `expires_at > created_at`、`closed_at >= created_at` 等能独立判断的关系加检查约束；
- API 将来输出 RFC 3339，但不能把 API 格式反向混入数据库列。

### 4.3 枚举与布尔值

- 领域状态以稳定大写英文 `TEXT` 保存，不保存 C# enum ordinal；
- 每个枚举列配置 `CHECK (... IN (...))`，未知值读取时 fail closed；
- 布尔值存 `INTEGER`，只允许 `0/1`；
- `state_version`、`worker_epoch`、sequence、计数和 PID 使用 `INTEGER`，添加非负或正数检查；
- `state_version` 初始为 `0`，每次成功 CAS 显式 `+1`；SQLite 不提供自动 rowversion。

### 4.4 JSON、摘要和路径

- JSON 列写入规范 UTF-8 JSON 文本，空对象用 `{}`、空数组用 `[]`，不得存空字符串；
- 对 JSON 列增加 `CHECK(json_valid(column))`，应用仍需按自己的 schema 反序列化和限长；
- SHA-256 统一为 `sha256:` 加 64 个小写十六进制字符；数据库使用长度、前缀及 lowercase
  检查，完整解析由应用完成；
- `content_path` 和 `session_root_path` 存相对配置 DataRoot 的、使用 `/` 分隔的规范路径，
  例如 `games/game_x/gver_y/content` 与 `sessions/sess_x/root`；
- 数据库不存宿主绝对路径。运行时由受控 resolver 拼接 DataRoot，并再次执行 P0-05 的
  规范化、链接和目录边界检查；
- 路径列不得以 `/` 开头、包含反斜杠、空段、`.` 或 `..` 段。SQLite CHECK 做基础拒绝，
  安全解析器做权威验证；
- 两个路径列均唯一，避免两个资源无意绑定同一权威目录。

## 5. 首版 schema

所有表使用显式小写 snake_case 名称。所有外键声明 `ON UPDATE RESTRICT`。除特别说明外，
资源删除使用 `ON DELETE RESTRICT`，不以 cascade 隐式删除用户数据或审计证据。

### 5.1 `quota_profiles`

P1-01 不实现配额分配算法，但 `users.quota_profile_id` 不能成为无约束字符串，因此首版加入
最小 profile 表：

```text
id TEXT PRIMARY KEY
name TEXT NOT NULL UNIQUE
max_active_sessions INTEGER NOT NULL
max_game_package_bytes INTEGER NOT NULL
max_session_bytes INTEGER NOT NULL
max_output_bytes_per_second INTEGER NOT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
state_version INTEGER NOT NULL DEFAULT 0
```

所有 limit 必须大于零，`state_version >= 0`。首版 migration 只建立结构，不写入默认 profile
或默认用户；P1-02 的显式首次启动/管理员引导流程必须先创建 profile，再创建引用它的用户。
测试各自插入确定性的 profile fixture，不把测试数据放入生产 migration。

### 5.2 `users`

```text
id TEXT PRIMARY KEY
login_name TEXT NOT NULL
normalized_login_name TEXT NOT NULL UNIQUE
password_hash TEXT NULL
security_stamp TEXT NOT NULL
role TEXT NOT NULL                  -- PLAYER | ADMIN
status TEXT NOT NULL                -- ACTIVE | DISABLED
access_failed_count INTEGER NOT NULL DEFAULT 0
lockout_end INTEGER NULL
quota_profile_id TEXT NOT NULL REFERENCES quota_profiles(id)
preferences_json TEXT NOT NULL DEFAULT '{}'
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
state_version INTEGER NOT NULL DEFAULT 0
```

约束：登录名和规范化登录名非空且有明确最大长度；`access_failed_count >= 0`；role/status
使用 CHECK；JSON 合法；`updated_at >= created_at`。只对规范化登录名建立身份唯一性，P1-02
必须使用单一、确定的 Unicode normalization/case-fold 算法后再查询。

`CloudEmueraUser` 可以继续继承 `IdentityUser<string>` 以便 P1-02 接入密码 hasher 和
UserManager，但只映射上述实际需要的属性；未进入首版需求的 email、phone、two-factor 等
属性显式 `[NotMapped]` 或改为独立持久化类型，不能让约定映射生成额外列。

### 5.3 `games`

```text
id TEXT PRIMARY KEY
owner_user_id TEXT NOT NULL REFERENCES users(id)
name TEXT NOT NULL
visibility TEXT NOT NULL            -- PRIVATE | SERVER_SHARED
status TEXT NOT NULL                -- ACTIVE | DELETED
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
state_version INTEGER NOT NULL DEFAULT 0
UNIQUE(owner_user_id, name)
```

添加 role/status、非空名称、时间和版本检查。所有者删除受 RESTRICT 保护。名称 Unicode/大小写
冲突策略属于 P1-04；本步骤只按 BINARY 精确值执行已确认的 `(owner_user_id, name)` 唯一约束，
不要自行引入 locale 相关数据库 collation。

### 5.4 `game_versions`

```text
id TEXT PRIMARY KEY
game_id TEXT NOT NULL REFERENCES games(id)
version_label TEXT NOT NULL
status TEXT NOT NULL                -- DRAFT | VALIDATING | PUBLISHED | BLOCKED | DELETED
content_digest TEXT NULL
content_path TEXT NOT NULL UNIQUE
manifest_json TEXT NOT NULL
runtime_config_json TEXT NOT NULL
compatibility_summary_json TEXT NOT NULL
created_by TEXT NOT NULL REFERENCES users(id)
created_at INTEGER NOT NULL
published_at INTEGER NULL
state_version INTEGER NOT NULL DEFAULT 0
UNIQUE(game_id, version_label)
UNIQUE(id, game_id)
```

额外约束：

- 对非 NULL `content_digest` 建唯一索引；多个 draft 可以为 NULL；
- `PUBLISHED`/`BLOCKED` 版本必须有合法 digest 和 `published_at`，draft/validating 不要求；
- `published_at >= created_at`；三个 JSON 列必须有效；
- 不创建删除已发布版本的 cascade；Session FK 会阻止物理删除被引用版本；
- P1-04 负责在事务和文件系统发布屏障中实现发布后内容不可变。本步骤不使用会阻止
  `PUBLISHED -> BLOCKED/DELETED` 管理状态变化的粗粒度 update trigger。

### 5.5 `sessions`

```text
id TEXT PRIMARY KEY
owner_user_id TEXT NOT NULL REFERENCES users(id)
game_id TEXT NOT NULL REFERENCES games(id)
game_version_id TEXT NOT NULL
runtime_version TEXT NOT NULL
session_root_path TEXT NOT NULL UNIQUE
name TEXT NOT NULL
state TEXT NOT NULL                 -- CREATING | STARTING | RUNNING | DETACHED |
                                    -- STOPPING | CLOSED | CRASHED
state_version INTEGER NOT NULL DEFAULT 0
worker_epoch INTEGER NOT NULL DEFAULT 0
waiting_for_input INTEGER NOT NULL DEFAULT 0
current_prompt_id TEXT NULL
last_output_sequence INTEGER NOT NULL DEFAULT 0
close_reason TEXT NULL
created_at INTEGER NOT NULL
started_at INTEGER NULL
last_activity_at INTEGER NOT NULL
closed_at INTEGER NULL
FOREIGN KEY(game_version_id, game_id) REFERENCES game_versions(id, game_id)
UNIQUE(id, worker_epoch)
```

必须实现：

- `(game_version_id, game_id)` 复合 FK，禁止把属于 Game A 的版本写入 Game B Session；
- `runtime_version` 在创建时固定，不能为空；
- `state_version/worker_epoch/last_output_sequence >= 0`；
- `waiting_for_input IN (0,1)`；waiting=true 时 prompt 必须非空，waiting=false 时 prompt 必须
  为 NULL；
- CLOSED 必须有 `closed_at`，其他状态 `closed_at` 必须为 NULL；
- 时间不能早于 `created_at`；
- `session_root_path` 是 P0-05 私有持久目录，不因为 CLOSED/CRASHED 自动删除；
- 查询索引：`(owner_user_id, created_at DESC)`、`(state, last_activity_at)`、
  `(game_version_id)`。

数据库 CHECK 只表达单行不变量。合法状态边由 `Domain.SessionStateMachine` 和后续 CAS SQL
强制，不在 trigger 中复制一份容易漂移的状态机。

### 5.6 `worker_leases`

```text
session_id TEXT PRIMARY KEY
worker_id TEXT NOT NULL UNIQUE
epoch INTEGER NOT NULL
status TEXT NOT NULL                 -- STARTING | ACTIVE | STOPPING | EXPIRED
pid INTEGER NULL
ipc_endpoint TEXT NOT NULL
runtime_version TEXT NOT NULL
protocol_version INTEGER NOT NULL
acquired_at INTEGER NOT NULL
heartbeat_at INTEGER NOT NULL
expires_at INTEGER NOT NULL
FOREIGN KEY(session_id, epoch) REFERENCES sessions(id, worker_epoch)
UNIQUE(session_id, epoch)
```

该复合 FK 是 epoch fencing 的持久化底线：不能插入 epoch 与 Session 当前 epoch 不一致的租约。
`session_id` 主键保证每个 Session 最多一条当前租约，`worker_id` 唯一避免一个 Worker 被绑定
两次。另检查 epoch/protocol version 为正、PID NULL 或正数、心跳不早于 acquired、expires
晚于 heartbeat。IPC endpoint 只保存 Supervisor 管理的逻辑/相对 endpoint 标识，不接受远程
TCP URL。

P1-05 必须在一个短事务中先以 CAS 递增 `sessions.worker_epoch/state_version`，再删除/替换租约；
本步骤只验证 schema 可以支持该事务，不提供业务方法。

### 5.7 `idempotency_records`

```text
actor_user_id TEXT NOT NULL REFERENCES users(id)
scope TEXT NOT NULL
idempotency_key TEXT NOT NULL
request_digest TEXT NOT NULL
response_status INTEGER NOT NULL
response_json TEXT NOT NULL
resource_id TEXT NULL
created_at INTEGER NOT NULL
expires_at INTEGER NOT NULL
PRIMARY KEY(actor_user_id, scope, idempotency_key)
```

限制 scope、key、response JSON 和 resource ID 长度；digest 必须为规范 SHA-256；HTTP status
限制在 100～599；response JSON 合法；`expires_at > created_at`。增加 `(expires_at)` 索引供
后续清理。`resource_id` 暂不设多态 FK。P1-06 负责“同键同摘要回放、同键不同摘要返回 409”
的事务算法。

### 5.8 `audit_events`

```text
id TEXT PRIMARY KEY
occurred_at INTEGER NOT NULL
actor_user_id TEXT NULL
actor_type TEXT NOT NULL             -- USER | ADMIN | SYSTEM
action TEXT NOT NULL
resource_type TEXT NOT NULL
resource_id TEXT NOT NULL
request_id TEXT NULL
result TEXT NOT NULL
reason_code TEXT NULL
metadata_json TEXT NOT NULL
```

`actor_user_id` 是审计快照中的逻辑引用，不设 FK，避免用户逻辑删除/未来清理破坏审计链；
actor type、result、字段长度和 JSON 合法性由 CHECK 限制。建立 `(occurred_at)`、
`(resource_type, resource_id, occurred_at)`、`(actor_user_id, occurred_at)` 索引。

migration 创建 `BEFORE UPDATE` 和 `BEFORE DELETE` trigger，使用 `RAISE(ABORT, ...)` 保护
append-only 语义，并加入测试。将来若需审计保留策略，必须用新的受评审 migration 修改策略，
不能在普通 repository 中绕过 trigger。

### 5.9 `schema_migrations`

使用 EF Core migrations history，不再维护第二套手写版本表。通过
`MigrationsHistoryTable("schema_migrations")` 固定名称，保留 EF 所需 migration id 与产品
版本列。测试必须断言仓库中只有这一张迁移历史表，不出现默认 `__EFMigrationsHistory`。

## 6. DbContext 与连接配置

### 6.1 模型配置

`CloudEmueraDbContext` 必须：

- 从普通 `DbContext` 继承；
- 暴露八张业务表的 `DbSet`；
- 逐项应用 configuration，并对表/列/索引/约束显式命名；
- 为 `DateTimeOffset <-> INTEGER`、枚举 `<-> TEXT` 提供集中 converter/comparer；
- 禁止 lazy-loading proxy；默认查询不应依赖 navigation 自动加载；
- 在测试中验证 `context.Model` 不产生意外 `AspNet*` 表、shadow FK 或 cascade delete；
- 不在 `OnModelCreating` 读取当前时间、环境变量或文件系统状态，确保 migration 可重现。

### 6.2 统一连接约定

所有产品进程必须通过同一个 options factory 创建 SQLite 连接：

```text
Data Source=<validated absolute db path>
Mode=ReadWriteCreate（Migrator）/ ReadWrite（业务进程）
Cache=Shared 是否使用需固定，不由调用方随意覆盖
Foreign Keys=True
Default Timeout=<configured busy timeout seconds>
Pooling=True
```

打开连接后验证/设置：

```sql
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = <bounded milliseconds>;
PRAGMA journal_mode = WAL;       -- Migrator/初始化管理连接设置并验证结果
PRAGMA synchronous = NORMAL;     -- 固定 MVP 默认值
```

`journal_mode` 是数据库级设置，不应在每个高频请求中反复切换。业务连接仍要逐连接确认
`foreign_keys=1` 和 busy timeout。所有 PRAGMA 参数来自强类型、有上下限的配置，不拼接用户输入。

测试必须使用磁盘临时数据库；仅测试纯 EF model 时才可用 in-memory connection。WAL、文件锁、
backup、多个连接与崩溃回滚测试禁止使用 `Data Source=:memory:`。

### 6.3 数据库路径安全

- 配置接受 DataRoot 和数据库相对路径，默认 `cloudemuera.db`；
- resolver 生成绝对路径后确认位于 DataRoot 直属允许位置；
- 拒绝相对逃逸、绝对覆盖、数据库文件 symlink、父目录 symlink 和特殊文件；
- 数据库、`-wal`、`-shm`、migration lock 和 backup 只位于受限持久目录；
- 新建数据库/备份文件 mode 为 `0600`，父目录遵循部署的服务用户权限；
- Linux 实现不把 `struct stat` 当作固定字节布局解析，而是使用顺序封送的内核 `statx` 结构读取
  文件类型和 inode；数据库、锁、WAL/SHM 与备份叶节点均在受保护父目录句柄下通过
  `openat(..., O_NOFOLLOW)` 校验和打开；
- Linux DataRoot 通过从 `/` 开始逐级 `openat(O_DIRECTORY|O_NOFOLLOW)` 遍历/创建，避免父级目录
  在路径校验后被替换；
- Linux 将已取得的 database/source/destination 文件描述符以 `/proc/self/fd/<fd>` 作为 SQLite
  `DataSource`，并把 guard 持有到连接关闭；SQLite 不再重新解析原始数据库或临时文件路径。
- 错误日志可记录稳定的逻辑数据库名，不记录凭据；SQLite connection string 不进入日志。

## 7. Migrator 执行协议

### 7.1 CLI

`CloudEmuera.Migrator` 至少支持：

```text
CloudEmuera.Migrator migrate --data-root <path> [--database cloudemuera.db]
CloudEmuera.Migrator check   --data-root <path> [--database cloudemuera.db]
```

- `migrate`：获取锁、检查数据库、必要时备份、应用全部 migration、执行一致性检查；
- `check`：只读验证数据库可打开、migration version 与二进制兼容、foreign key check 和
  quick check；不得修改 schema；
- 支持 `SIGTERM`/Ctrl-C cancellation；开始不可取消的 SQLite commit 后要完成当前原子步骤再退出；
- 日志包含 operation、migration id、elapsed 和 result，不输出用户数据、password hash、JSON
  response 或 connection string。

建议稳定退出码：

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功/已是最新 |
| 10 | 配置或路径非法 |
| 11 | 迁移锁已被占用 |
| 12 | 数据库版本比当前二进制新或 migration history 不兼容 |
| 13 | 备份失败，未开始 migration |
| 14 | migration 失败 |
| 15 | quick/integrity/foreign-key 检查失败 |

### 7.2 独占锁

- 锁文件使用数据库同目录的固定名，例如 `cloudemuera.db.migration.lock`；
- Windows/其他平台以原子 exclusive create/open + `FileShare.None` 持有打开句柄；Linux 使用受保护
  父目录句柄下的 `openat(O_NOFOLLOW)` 加 `flock(LOCK_EX|LOCK_NB)`，并持有该父目录句柄和文件句柄
  到整个操作结束；
- 验证锁文件是当前服务用户拥有的普通文件、link count 正常且不是 symlink；
- 锁竞争在有界时间内失败并返回 11，不无限等待；
- 进程崩溃后 OS 释放句柄，下一进程可复用安全的普通锁文件；不要把文件存在本身视为永久锁；
- 两个并发 Migrator 的集成测试必须证明只有一个进入迁移临界区。

该锁只协调 CloudEmuera schema migration，不替代 SQLite 自己的事务锁。生产进程编排仍应先运行
Migrator，再启动依赖新 schema 的 API/Supervisor。

### 7.3 迁移前备份

当数据库已存在且有 pending migration 时：

1. 先通过只读检查确认现有 history 是当前二进制可升级的前缀；
2. 在数据库同一受控 DataRoot 下创建临时 backup；
3. 使用 `Microsoft.Data.Sqlite` 的 SQLite Online Backup API 生成一致性副本；
4. 对备份执行 `quick_check`，fsync/关闭后在受保护目录句柄下原子发布为带 UTC timestamp 与来源
   migration id 的最终文件；Linux 使用 `linkat`/inode identity check/`unlinkat`，便携路径使用
   原子重命名；任何替换竞态都 fail closed；
5. 只有备份成功后才调用 `MigrateAsync`；
6. 空的新数据库无需生成无意义备份；无 pending migration 的重复运行也不生成新备份；
7. 本步骤不自动清理旧备份，保留策略属于运维任务。

迁移失败不自动用备份覆盖现场数据库。EF migration 的事务必须保证当前 migration 不留下部分
schema；备份作为人工/后续生产恢复的可靠来源。自动覆盖可能掩盖失败现场或与错误启动的其他
进程竞争，因此不在 P1-01 中实现。

### 7.4 迁移原子性和兼容检查

- 首个 migration 使用 EF Core transaction；不得在 `SuppressTransaction` SQL 中执行业务 DDL；
- 执行前拒绝数据库中存在本二进制未知的更新 migration；
- 执行后运行 `PRAGMA foreign_key_check` 和 `PRAGMA quick_check`；任一非空/非 `ok` 结果失败；
- migration `Down` 必须能在测试数据库回退首版 schema，但生产 CLI 不暴露 downgrade 命令；
- 发布后不得编辑 migration；任何修正新增 migration；
- API/Supervisor 后续只做兼容性 check。数据库缺失或落后时 ready 失败并提示先运行 Migrator，
  不能自行升级。

## 8. 自动化测试设计

新增 `tests/CloudEmuera.Infrastructure.Tests`，引用 Infrastructure、Migrator runner 所在程序集、
`Microsoft.NET.Test.Sdk` 和 xUnit，加入 `CloudEmuera.slnx`。所有测试名或 trait 注明映射的需求。

### 8.1 Migration（`Category=Migration`）

1. `EmptyDatabase_MigratesToLatestSchema`
   - 在临时磁盘目录运行真实 runner；
   - 断言所有预期表、索引、FK、CHECK、trigger 和 `schema_migrations` 存在；
   - 断言没有 `__EFMigrationsHistory`、`AspNet*` 或 SaveArtifact 表。
2. `LatestDatabase_MigrateAgain_IsNoOpAndPreservesRows`
   - 迁移后插入一组满足引用关系的代表数据；
   - 再运行 Migrator；
   - 断言 migration history 和业务行逐字节/逐字段不变，且未新增 backup。
3. `PreexistingDatabase_InitialMigration_PreservesUnrelatedData`
   - 建立代表“P1 前无受支持 schema”的样例 SQLite 文件及 probe 数据；
   - 运行首版 migration，断言 probe 与新 schema 同时存在；
   - 文档明确这不是承诺支持任意旧开发 schema。真正第二版 migration 出现时，必须改为
     migrate-to-N、seed、migrate-to-N+1 的 N-1 数据保留测试。
4. `InitialMigration_Down_RemovesOwnedSchemaOnly`
   - 回退测试数据库；确认本 migration 所有表/index/trigger 被移除，不删除 preexisting probe。
5. `PendingMigration_CreatesVerifiedOnlineBackupBeforeApplying`
   - 使用测试 migration assembly 模拟旧版本；
   - 断言 backup 可独立打开、数据一致、命名/mode 正确。
6. `NoPendingMigration_DoesNotCreateBackup` 和 `NewDatabase_DoesNotCreateBackup`。
7. `DatabaseNewerThanBinary_CheckFailsWithoutMutation`。

### 8.2 PersistenceConstraint（`Category=PersistenceConstraint`）

至少覆盖：

- 重复 `normalized_login_name` 被拒绝；
- 重复 `(owner_user_id, game name)` 和 `(game_id, version_label)` 被拒绝；
- 两个 published version 使用同一 `content_digest` 被拒绝，多个 NULL draft digest 允许；
- 非法 digest、JSON、枚举、布尔、负 state version/epoch/sequence 被拒绝；
- Game B 的 Session 引用 Game A 的 version 被复合 FK 拒绝；
- 删除仍被 Session 引用的 GameVersion 被 RESTRICT 拒绝；
- 重复 `session_root_path`/`content_path` 被拒绝；
- 同一 Session 第二条 lease、重复 worker ID、错误 epoch lease 被拒绝；
- session epoch 仍被 lease 引用时，未按替换协议直接改 epoch 被 FK 拒绝；
- 同一 idempotency key/scope/actor 只能插入一次；同一 key 在不同 actor 或 scope 可以独立存在；
- audit INSERT 成功，UPDATE/DELETE 被 trigger 拒绝；
- CLOSED 没有 closed time、waiting/prompt 不一致、非法时间顺序被拒绝；
- 任何核心 owner/version FK 悬空插入被拒绝；
- 删除 User 不会 cascade 删除 Game、Session、幂等记录或审计事件。

约束测试必须通过新的独立 SQLite connection 执行至少一轮，防止只验证 EF change tracker 而
没有验证数据库约束。

### 8.3 SQLite 配置

- 每个 factory connection 的 `foreign_keys=1`；
- busy timeout 等于配置值并有上下限；
- 文件数据库使用 WAL，关闭重开后保持；
- 数据库和 backup 权限在 Linux 为 `0600`；
- symlink 数据库/父目录、逃逸相对路径、特殊文件被 fail closed；
- model metadata 没有 cascade delete、意外 shadow property 或未命名表；
- `DateTimeOffset` 在非 UTC offset 输入后以同一 instant 的 Unix 毫秒往返；
- enum 按稳定字符串而非 ordinal 保存。

### 8.4 失败、并发与故障注入

1. `FailingMigration_RollsBackAllChanges`
   - 使用测试专用 migration assembly，在一个 migration 中先建表/写数据再故意失败；
   - 断言该 migration history 未写入，中间表/列/数据均不存在，旧数据完整；
   - runner 返回 14。
2. `BackupFailure_PreventsMigration`
   - 注入 backup failure；断言 migration 未开始、history 不变、返回 13。
3. `TwoMigrators_OnlyOneOwnsMigrationLock`
   - 启动两个独立 runner/进程对同一文件竞争；一个成功，另一个在期限内返回 11；
   - 最终 schema 完整且 history 只有一份。
4. `CancellationBeforeMigration_LeavesDatabaseUnchanged`。
5. `InterruptedOrCorruptDatabase_CheckFailsClosed`
   - 使用截断/非 SQLite 文件；不得重新创建覆盖，返回明确错误。
6. `BusyDatabase_TimesOutWithinConfiguredBound`
   - 另一连接持有写锁；runner/写入在配置窗口内失败，不无限挂起。
7. backup 临时文件残留、普通同名文件、symlink 和权限异常均不能被静默覆盖；Linux 锁文件替换
   竞态不得跟随 sentinel symlink 或改变其内容/权限。

故障测试不得修改仓库 `data/`，全部使用独立临时目录并在失败时保留足够诊断。清理只能删除
测试自己创建且已经规范化验证的具体目录。

### 8.5 真实 Migrator 进程冒烟

至少一个 `Category=MigrationProcess` Linux 测试启动真正的
`CloudEmuera.Migrator.dll migrate`，而不是直接调用 DbContext：

- argv/config 指向临时 DataRoot；
- 第一次退出 0 并创建数据库；
- 第二次退出 0 且报告 up-to-date；
- `check` 退出 0；
- 非法路径和更高未知 migration 分别返回约定非零码；
- 两个真实 migrator 进程竞争同一数据库时，一个退出 0，另一个退出 11 并报告
  `migration_lock_busy`；
- stdout/stderr 不出现插入的 password hash、idempotency response 或完整 connection string。

## 9. 实施顺序

### 阶段 A：冻结契约和测试骨架

1. 新建 Infrastructure 测试项目并加入 solution；
2. 为表名、字段类型、索引、删除行为和 migration history 编写预期测试；
3. 定义强类型 `SqliteDatabaseOptions`、时间 converter 和路径 resolver 契约；
4. 明确默认 quota profile 常量和所有长度/timeout 上限；
5. 先让 schema tests 以“缺少 migration”方式失败，避免从当前 `IdentityDbContext` 意外生成
   `AspNet*` schema。

### 阶段 B：行模型与 DbContext

1. 将 DbContext 改为显式普通 EF Core context；
2. 添加八个 persistence row model 和逐实体 configuration；
3. 实现复合 alternate key/FK、delete restrict、JSON/enum/time/path CHECK 和索引；
4. 配置 `schema_migrations`；
5. 添加 design-time factory；
6. 运行 model tests，检查没有约定生成的多余列、表或 cascade。

### 阶段 C：生成并审查首个 migration

1. 在 dev Docker 中使用固定版本 `dotnet-ef` 生成 `InitialMetadata`；
2. 人工审查 migration SQL，特别检查 SQLite 类型、复合 FK、partial unique index、审计 trigger、
   Down 顺序和 history table；
3. migration 中加入审计 trigger，但不加入用户、配额或其他产品数据 seed；
4. 生成一次 idempotent SQL script 供评审，但运行时仍使用 compiled migration；
5. 删除并重新生成测试数据库，禁止为了通过测试直接修改实际 SQLite 文件。

若引入 `Microsoft.EntityFrameworkCore.Design` 或本地 `dotnet-ef` tool manifest，版本必须与
`Microsoft.EntityFrameworkCore.Sqlite` 精确保持 `10.0.10`，提交中央版本和所有受影响
`packages.lock.json`/tool manifest；restore 继续使用 locked mode。

### 阶段 D：统一连接与 Migrator

1. 实现 DataRoot/database resolver 和文件安全检查；
2. 实现 connection/options factory 与 PRAGMA 验证；
3. 实现有界独占 migration lock；
4. 实现 pending migration 检测、Online Backup、Migrate、foreign key/quick check；
5. 将 Migrator Program 接到 runner，添加 `migrate`/`check`、日志、取消和稳定退出码；
6. 明确 API/Supervisor 不调用 migrate，并为此添加架构/静态测试。

### 阶段 E：约束、升级和故障测试

1. 完成所有 `Migration` 与 `PersistenceConstraint` 测试；
2. 使用测试 migration assembly 验证失败事务回滚和 backup 前置屏障；
3. 使用两个并发 runner/进程验证独占锁；
4. 在 Linux 验证权限、symlink/special-file 拒绝和真实进程退出码；
5. 检查日志脱敏和临时文件清理；
6. 重跑 P0 全部测试，确认新 Infrastructure/Migrator 不改变 Worker Runtime 行为。

### 阶段 F：文档与全量质量门

1. 更新 `docs/design.zh-CN.md` 的首版实际字段、时间和相对路径约定；
2. 更新 Migrator 使用说明、DataRoot/backup 布局和退出码；
3. 更新 `docs/development-plan.zh-CN.md` 的实际命令、测试数量和完成证据；
4. 若实现必须改变已冻结的 schema 范围或迁移所有权，先补 ADR，再改代码；
5. 执行下节全部验证，全部通过后才把 P1-01 标记为 DONE。

## 10. 验证命令

所有 .NET 构建与测试必须通过仓库 dev Docker；不要使用宿主 `dotnet`。先启动环境：

```bash
./scripts/dev-up.sh
```

定向验证：

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore \
  --configuration Release --filter 'Category=Migration|Category=PersistenceConstraint'

source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore \
  --configuration Release --filter 'Category=MigrationProcess'
```

实现时新增一个从空临时目录运行 Migrator 的脚本，例如：

```bash
./scripts/test-migrations.sh
```

该脚本必须使用 `scripts/lib/dev-env.sh` 注入的 UID/GID、在 dev Docker 内运行、使用
`mktemp -d` 创建专用目录，并在任何断言失败时返回非零退出码。

完整质量门：

```bash
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
```

完成后：

```bash
./scripts/dev-down.sh
```

如果变更 Compose 服务、镜像或挂载，必须扩展 `verify-dev-user.sh`，验证 Migrator 创建的
database、WAL/SHM、lock 和 backup 文件均属于宿主 UID/GID。若不新增服务而复用 api 开发
容器，也仍需验证新文件所有权。

## 11. 完成定义

P1-01 只有同时满足以下条件才可标记为 DONE：

1. 首版 migration、designer 和 model snapshot 已提交，locked restore 可重现；
2. 空数据库可由真实 Migrator 进程升级到最新 schema；
3. 重复运行 migration 为无副作用成功，不清空或改写代表数据；
4. 预先存在的样例数据在首版迁移后保留，并记录首版兼容边界；
5. migration history 仅使用 `schema_migrations`，没有默认 history 或意外 Identity 表；
6. 八张业务表的字段、FK、CHECK、unique/index 和 delete behavior 与本计划一致；
7. Session 固定 GameVersion、runtime version 和私有相对 SessionRoot；没有存档内容表；
8. 数据库强制版本引用一致性、每 Session 单 lease、worker ID 唯一和 lease epoch 匹配；
9. 数据库强制幂等复合键，审计事件拒绝 UPDATE/DELETE；
10. 所有时间以 Unix UTC 毫秒、枚举以稳定字符串、布尔以受限 INTEGER 保存；
11. 每个产品连接启用 foreign keys 和有界 busy timeout，数据库使用 WAL；
12. 只有 Migrator 能执行 schema 迁移；API/Supervisor/Worker 没有启动时自动迁移路径；
13. pending migration 前生成通过 quick check 的 Online Backup；无 pending 时不制造备份；
14. 故意失败 migration 完整回滚，备份失败时 migration 未开始；
15. 两个并发 Migrator 最多一个进入临界区，失败者在期限内返回稳定退出码；
16. 数据库路径穿越、symlink、特殊文件和未知更高版本均 fail closed；
17. 日志和错误不泄露密码 hash、响应正文、connection string 或用户输入；
18. Infrastructure 定向测试、真实 Migrator 冒烟、`check.sh`、开发用户和第三方验证全部通过；
19. 文档与实际 migration、命令、默认配置和锁文件一致；
20. P0 Runtime、IPC 和 Worker 进程测试无回归。

## 12. 实现交接清单

下一 session 开始实现前应按顺序确认：

- [x] 阅读本计划、requirements 第 5/6/9/11 章和 design 第 2.5/4/5/14 章；
- [x] 确认 P0-06 已 DONE，当前 worktree 没有误覆盖其他任务修改；
- [x] 建立 Infrastructure 测试项目和 schema 预期测试；
- [x] 冻结所有最大长度、timeout 和默认 quota profile 数值；
- [x] 移除 DbContext 对默认 `IdentityDbContext` schema 的依赖；
- [x] 显式配置八张业务表、`schema_migrations`、约束和索引；
- [x] 在 dev Docker 生成、审查并提交 `InitialMetadata` migration；
- [x] 实现连接 factory、路径验证、独占锁、backup 和 Migrator CLI；
- [x] 完成数据库约束、回滚、锁竞争和真实进程测试；
- [x] 通过定向测试后执行全量质量门；
- [x] 把实际命令、测试数量、migration id 和已知限制写回开发计划；
- [x] 全部完成后将 P1-01 设为 DONE，并把 P1-02 设为 NEXT。

## 14. 实际实现与验证记录

- migration：`20260807071428_InitialMetadata`；EF history 固定为 `schema_migrations`。
- schema：八张业务表；EF Core SQLite provider 的内部 `__EFMigrationsLock` 仅为 provider
  实现细节，不作为产品 history；未创建 `AspNet*`、Save 或 SaveArtifact 表。
- 测试：Infrastructure 全量 36 项、`Migration|PersistenceConstraint` 33 项、真实
  `MigrationProcess` 3 项，均在 Linux dev Docker 通过；新增 statx 架构安全文件类型测试、
  锁文件替换竞态测试、descriptor-backed SQLite inode 回归测试和两个真实 Migrator 的跨进程
  竞争测试。
- 边界：迁移前 Online Backup、失败回滚、lock 竞争、数据库损坏/高版本、权限和日志脱敏，
  以及路径 symlink/FIFO/sidecar、WAL、busy timeout、独立连接约束、epoch fencing、目录句柄
  `openat(O_NOFOLLOW)` 和备份发布 inode 校验均有测试。
- 已知限制：本步骤只提供 schema 与迁移基础设施；身份认证、授权、上传、租约业务算法和
  Session API 由后续 P1-02/P1-03/P1-05/P1-06 实现。
