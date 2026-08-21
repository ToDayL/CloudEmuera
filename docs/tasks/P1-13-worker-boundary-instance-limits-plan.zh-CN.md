# P1-13 基础 Worker 进程边界与实例级上限实施方案

状态：已实施

日期：2026-08-21

实施记录（2026-08-21）：已完成 ADR-0027、实例级容量绑定与启动交叉校验、Worker bootstrap/环境边界、
SessionRoot/存档/asset 执行门、生产镜像与 Compose，以及两套隔离验证脚本。最终通过
`./scripts/test-production-image.sh`、`./scripts/test-instance-limits.sh`、`./scripts/check.sh`、
`./scripts/verify-dev-user.sh` 和 `./scripts/verify-third-party.sh`。生产部署身份随后依据
[`ADR-0028`](../adr/0028-production-bind-mount-ownership.md) 调整为启动服务者的 UID/GID；生产
`docker/compose.yml` 在未设置 `CLOUDEMUERA_DATA_PATH` 时使用 named volume，设置后使用其预先创建的
`/data` bind mount。

关联需求：GAME-002、SESS-001/002/008/009、PLAY-005/006/009、SAVE-008/009、OPS-002、
SEC-001～005/007/009、NFR-002～005、AC-010/012/014

关联决策：ADR-0008、ADR-0012、ADR-0015～0017、ADR-0020、ADR-0022、ADR-0023、ADR-0026～0028

前置任务：P1-03～P1-12、P1-S01、P1-S03

后续任务：P1-14 单容器生产进程管理与恢复、P1-15 MVP 验收、安全与性能门

## 1. 目标结果

P1-13 把已经分散落地的安全摄取、Session 容量门、Worker bootstrap、Snapshot 和 Realtime 背压
收敛为一套部署级、可配置、可重复验证的基础边界。它面向由部署者为自己及信任参与者运行的单机
自托管实例，不把 Worker 重新定义为敌对代码沙箱。完成后必须得到：

1. 生产镜像中的 API、Worker 和 Validator 均以同一个非 root 服务 UID 运行；生产部署只要求提供
   持久 `/data`，默认使用 named volume，也可切换为启动者拥有的 bind mount；不挂 Docker socket、
   宿主密钥、源码目录或其他无关宿主路径，Worker UDS 不暴露到容器外。
2. API 启动 Worker 前重新验证 SessionRoot 的目录身份、受保护 marker、runtime manifest 和当前
   binding；正常 Worker 只取得自己的 SessionRoot、当前 Session/Worker/epoch binding、私有 bootstrap
   token 和 UDS 信息，不取得 Game workspace/current 或其他 SessionRoot 路径。
3. 实例级容量配置具有唯一权威来源和启动期交叉校验，覆盖活动 Worker、未启动 Session、上传归档、
   展开内容、文件数量、单文件、staging、SessionRoot、存档、Snapshot、IPC/WebSocket 队列、连接/输入
   以及 DataRoot 最低剩余空间。
4. 每个写入、进程创建和内存队列入口都实际执行相应限制。并发请求不能共同越过一个剩余名额或重复
   承诺同一份磁盘预算；实际写入期间仍以累计字节和底层 `ENOSPC` 作为第二道防线。
5. 所有正常超限都产生稳定错误码、有限日志和可恢复状态；不能依靠 OOM、磁盘写满、未处理异常或
   队列无限增长来形成限制。
6. 部署文档给出容器整体 CPU、内存和 PID 限制示例，并明确这些限制属于部署选项。缺少独立 UID、
   namespace、seccomp 或每 Worker cgroup 不影响 readiness。
7. `scripts/test-production-image.sh` 和 `scripts/test-instance-limits.sh` 能在隔离的临时 Compose project、
   DataRoot 和端口中证明上述结果，且不读取、修改或清理人工 `.env`、`./data` 和开发容器。

本任务的完成标准不是“已有若干常量”或“Dockerfile 写了 `USER app`”，而是配置、执行点、错误语义、
生产镜像和故障/并发验证形成闭环。

## 2. 决策优先级与任务边界

### 2.1 文档优先级

实现时按以下顺序解释已有文本：

1. 当前中英文需求；
2. ADR-0017 的可信参与者自托管边界；
3. ADR-0015/0016 的 API-owned Worker、持久 SessionRoot、epoch fencing 和退出屏障；
4. ADR-0008/0012/0022 的 ZIP、staging 和存档限制口径；
5. ADR-0020/0023/0026 的 Snapshot、Realtime 和 presentation asset 边界；
6. 已实施任务文档中的交接约束。

ADR-0017 早期写有“创建 Session 总数不设上限”，当前 SESS-001/OPS-002 已明确默认最多保留 64 个
未启动 Session。当前规则解释为：不限制历史累计创建次数，但对尚未删除且处于 `CREATING`、`CLOSED`
或 `CRASHED` 的持久 Session 行实施实例级保留上限；用户删除符合条件的 Session 后释放名额。

### 2.2 本任务交付

- 冻结实例级容量配置的名称、默认值、合法范围、交叉关系和超限语义；
- 将现有散落常量接入部署配置，补齐缺失的文件数量、列表、asset 和队列限制；
- 逐入口审计并修复 Worker open、Session create、ZIP 摄取、SessionRoot 复制、存档、Snapshot、IPC、
  WebSocket 和 asset 流式响应的实际执行点；
- 固化 Worker 启动路径和 bootstrap 最小披露契约；
- 补齐可运行的生产镜像、最小生产 Compose 示例和容器整体资源限制说明；
- 新增后端单元/集成、多进程、容器和压力回归；
- 同步需求、设计、ADR、配置说明、错误码和开发计划。

### 2.3 明确不做

- 不引入 NsJail、独立 Worker UID、user/mount namespace、seccomp、Landlock 或每 Worker cgroup；
- 不承诺阻止恶意 ERB/Runtime 利用同 UID 读取 DataRoot 中其他内容；
- 不建设每用户或每 Worker CPU、RSS、磁盘、PID、FD、IO、输出速率计量、调度、预留或计费；
- 不增加 Prometheus、OpenTelemetry、容量趋势、告警或资源图表；
- 不增加“沙箱能力”或“容器 CPU/内存/PID 已配置”的 readiness 探针；
- 不把实例容量配置存入 SQLite，不增加管理员在线修改容量 API/UI；MVP 通过部署配置修改并重启生效；
- 不修改 GameVersion、SessionRoot 私有副本、原生存档、epoch fencing、HTTP 幂等或 operation recovery
  的既有模型；
- 不在本任务完成 PID 1 信号转发、API 被强杀后的进程组回收、停机备份恢复或升级回滚；生产 Compose
  的 one-shot Migrator 成功门控已随本任务交付，其余进程管理与恢复属于 P1-14；
- 不以读取整个请求体、完整解压或构造完整 Snapshot 后再检查的方式实现“限制”。

## 3. 当前基线与缺口

### 3.1 可直接复用

- `InstanceCapacityOptions` 已提供活动 Worker 8、未启动 Session 64、游戏包 2 GiB、SessionRoot 4 GiB、
  staging 12 GiB、单存档 64 MiB 和 DataRoot 最低剩余 1 GiB 的实例默认值，并在 API 启动时读取
  `CloudEmuera:Capacity:*`。
- `SqliteSessionRuntimeStore` 已在 `BEGIN IMMEDIATE` 事务中检查活动 Worker 数；
  `SqliteSessionApplicationService` 已检查未启动 Session 数、SessionRoot 预算和并发 create reservation。
- ZIP 摄取已有 archive、expanded、single-file、entry-count、central-directory、depth、ratio、路径、链接、
  Unicode/大小写碰撞、实际写入字节和 staging reservation 检查，并已有稳定拒绝码。
- 存档上传和格式验证已使用 `MaxSaveFileBytes`；存档访问使用 dirfd、`O_NOFOLLOW`、mutation lease、持久
  operation 和幂等。
- `RealtimeOutputOptions`、`RealtimeGatewayOptions`、`BoundedRealtimeQueue`、`SessionOutputHub` 和
  Worker Manager 已具有 Snapshot、显示队列、控制队列、连接、pending input/event 的多数软硬边界。
- Worker bootstrap 已绑定 `controlPlaneInstanceId + sessionId + workerId + epoch + token`，命令行只传
  `--bootstrap-file`；SessionRoot inspector、受保护 marker 和 Worker 内部二次校验均可复用。
- `docker/Dockerfile` 的 runtime stage 保留非 root 默认用户；生产 Compose 显式注入启动服务者 UID/GID，
  开发 Compose 也显式注入宿主 UID/GID，并使用 `init: true`。
- P1-12 已提供 Worker/Realtime 当前 Snapshot 大小和 overflow 诊断，不需要新增资源指标平台。

### 3.2 必须补齐

1. `InstanceCapacityOptions` 没有表达 ZIP entry count/single-file、存档列表文件数/响应预算、asset 并发等
   全部 OPS-002 边界；部分限制仍是 Application/Infrastructure 内部常量，部署者无法统一配置。
2. `GamePackageIngestionLimits` 每次请求默认构造，archive/expanded 仅与 `InstanceCapacityOptions` 的两个
   字节字段取最小值；entry count、single-file 等仍未接入实例权威配置。
3. `LinuxSessionSaveRootAccessor` 把最大列表文件数 4096 和列表预算 8 MiB 写成私有常量，未进入启动
   校验，也未与实例配置文档对应。
4. Realtime 和 Worker 队列已有硬界，但配置分散在 `Capacity`、`Realtime`、`Worker` 三组，缺少一处
   启动期的整体交叉验证和可重复的端到端超限脚本。
5. presentation asset 已按 manifest、摘要、MIME、Range 和 `BoundedReadStream` 安全读取，但缺少明确的
   实例并发读取闸门及配置/压力验证。
6. `docker/Dockerfile` 只显式 publish API 和 Validator；API 工程没有 Worker 的普通 ProjectReference。
   必须以真实生产镜像启动 Session Worker 证明 Worker 程序和全部 runtime 依赖确实进入最终镜像。
7. 当前没有生产 Compose 示例，也没有 `test-production-image.sh`、`test-instance-limits.sh`；无法自动证明
   非 root、敏感挂载缺失、整体容器限制和容量行为。
8. 详细设计第 19 节仍把实例默认上限列为待编号 ADR。在改动默认值或配置契约前必须先形成决策记录。

## 4. 配置模型与决策记录

### 4.1 ADR-0027

实施第一步新增 `docs/adr/0027-instance-capacity-and-production-boundary.md`，至少冻结：

- 配置为实例级、部署期、重启生效，不是用户 quota 或数据库业务实体；
- 默认值及选择依据；
- 字节统一使用整数 bytes，数量使用正整数，时间使用明确单位后缀；
- archive、expanded、SessionRoot、staging、save、Snapshot 和各队列之间的合法关系；
- 超限发生在 admission、流式累计、发布/排队三个阶段时的不同恢复语义；
- 容器整体 CPU/内存/PID 限制与应用级容量门的职责分工；
- 同 UID Worker 的安全声明和 readiness 语义；
- 旧配置键的兼容周期和废弃 warning。

ADR 接受后更新 `docs/adr/README.md`，并把详细设计中的“待编号”替换为 ADR-0027。

### 4.2 权威配置类型

保持配置按执行职责拆分，避免制造一个同时依赖 API 和 Infrastructure 的巨型类型：

| 类型 | 配置前缀 | 权威内容 |
| --- | --- | --- |
| `InstanceCapacityOptions` | `CloudEmuera:Capacity` | Worker/Session、archive/expanded/file、staging、SessionRoot、save/list、DataRoot |
| `RealtimeOutputOptions` | `CloudEmuera:Realtime` | Snapshot、batch、逐连接显示队列软/硬字节和消息数 |
| `RealtimeGatewayOptions` | `CloudEmuera:Realtime` | 连接、订阅、client message、控制队列、pending input、发送超时 |
| `WorkerManagerOptions` | `CloudEmuera:Worker` | pending event/input、IPC、注册/心跳/停止期限 |
| `PresentationAssetOptions`（新增） | `CloudEmuera:Assets` | manifest、单资源、Range、并发读取和在途字节预算 |

`InstanceCapacityOptions` 至少新增或明确以下字段：

- `MaxArchiveBytes`（替代语义含混的 `MaxGamePackageBytes`，旧键兼容读取一个周期）；
- `MaxExpandedBytes`；
- `MaxArchiveSingleFileBytes`；
- `MaxArchiveEntryCount`；
- `MaxSessionRootBytes`；
- `MaxSessionRootFileCount`；
- `MaxStagingReservedBytes`；
- `MaxSaveFileBytes`；
- `MaxSaveListedFiles`；
- `MaxSaveListBytes`；
- `MinDataRootFreeBytes`；
- 现有 `MaxActiveWorkers`、`MaxInactiveSessions`。

不把所有安全解析参数都提升为容量配置。ZIP path UTF-8 长度、segment 长度、目录深度、compression ratio、
central-directory 和诊断数属于格式/安全策略，可保留在 `GamePackageIngestionLimits`，但生产请求必须从
服务端构造，客户端不得提交更大的 limits。

### 4.3 默认值与合法关系

ADR-0027 评审前不在实现方案中悄然改变现有已发布默认值。建议保留：

- 活动 Worker 8；未启动 Session 64；
- archive 2 GiB；expanded/SessionRoot 4 GiB；
- staging 12 GiB；单存档 64 MiB；最低剩余空间 1 GiB；
- Snapshot 12 MiB；Realtime 显示队列 soft/hard 1/2 MiB、32/64 条；
- Worker pending event 1 MiB、256 条；
- 存档列表 4096 文件、8 MiB 路径/元数据预算。

新增配置必须在应用接流量前一次性验证：

- 所有数量和字节上限为正，`MinDataRootFreeBytes >= 0`；
- `MaxArchiveSingleFileBytes <= MaxExpandedBytes <= MaxSessionRootBytes`；
- `MaxSaveFileBytes <= MaxSessionRootBytes`；
- 单次最大 admission reservation 不得大于 `MaxStagingReservedBytes`；
- soft queue 严格小于对应 hard queue；
- batch target 不大于 queue hard bytes，queue hard bytes 不大于 Snapshot；
- Snapshot 与最终 server envelope 不超过 IPC/Realtime 协议绝对上限；
- 可配置上限还必须受编译期绝对安全上限约束，防止误配置把 `int`、数组或协议长度推到危险范围。

配置校验失败时 API 以清晰配置键和稳定启动错误退出；不得自动钳位后静默运行。仅旧键映射输出一次
deprecation warning。

## 5. 容量执行矩阵

| 资源 | Admission/执行点 | 并发正确性 | 超限结果 |
| --- | --- | --- | --- |
| 活动 Worker | `SqliteSessionRuntimeStore.BeginOpenAsync` | `BEGIN IMMEDIATE` 内计数并写 STARTING lease | `ACTIVE_WORKER_LIMIT_EXCEEDED` |
| 未启动 Session | create prepare transaction | 同事务统计 `CREATING/CLOSED/CRASHED`，成功删除后释放 | `INACTIVE_SESSION_LIMIT_EXCEEDED` |
| archive bytes | HTTP bounded stream + ingestion copy | 每请求累计实际字节 | `ARCHIVE_TOO_LARGE` |
| expanded bytes | ZIP preflight + 实际解压写入 | 单 ingestion；实际累计二次校验 | `EXPANDED_SIZE_EXCEEDED` |
| archive entries | central directory preflight | 完整写入前拒绝 | `ENTRY_COUNT_EXCEEDED` |
| archive single file | preflight + 每文件实际写入 | 声明值和实际值均检查 | `ENTRY_TOO_LARGE` |
| staging 总预留 | ingestion reservation transaction | 汇总活动 reservation，提交/失败/reaper 释放 | `STAGING_BUDGET_EXHAUSTED` |
| SessionRoot bytes/files | create prepare + dirfd copy | create operation reservation + 实际复制累计 | Session 稳定容量错误，operation 可恢复 |
| DataRoot free bytes | 每次大写入 admission + 写入错误 | 预留防并发；`ENOSPC` 仍单独处理 | `DATA_ROOT_SPACE_LOW` 或稳定 IO 错误 |
| save bytes | Content-Length、bounded stream、format validate、publish | mutation lease + operation | 现有 `SAVE_FILE_TOO_LARGE` 类稳定错误 |
| save list | dirfd enumeration | 达到上限立即停止，不构造无界列表 | `SAVE_LIST_LIMIT_EXCEEDED` |
| Snapshot | Worker contract + API reducer/serializer | 单 Hub publish gate；按实际序列化字节 | Hub fault/Worker crash 使用稳定 reason |
| Worker IPC pending | `ApiWorkerSession` bounded event/input structures | 当前 binding 内排队 | backpressure/fault，不扩容 |
| WebSocket connection/control | registry + bounded control writer | 原子注册/逐连接队列 | 503、input backpressure 或关闭当前连接 |
| WebSocket display | `BoundedRealtimeQueue` | soft 替换 Snapshot，hard 关闭慢连接 | 只影响当前连接，Hub 保留一致状态 |
| asset reads | manifest reader + 新实例 gate | 原子并发名额和在途预算 | 429/503 稳定错误，不先分配完整内容 |

## 6. 分层和文件级改动

### 6.1 Domain

原则上不新增领域实体或数据库表。只补充/保留以下纯业务性质测试：

- 活动 Worker 只剩一个名额时，并发 open 最多一个获得有效 lease；
- 未启动 Session 只剩一个名额时，并发 create 最多一个进入持久 create operation；
- close/crash/delete 释放相应名额的语义；
- 容量拒绝不递增 epoch、不改变 SessionRoot、不生成第二个 Worker。

若现有容量判断完全位于 Infrastructure 的 SQLite 事务，可在 Infrastructure 集成测试覆盖，不为追求层级
形式把数据库计数伪造成 Domain 聚合。

### 6.2 Application

修改 `CloudEmuera.Application/GamePackages/GamePackageContracts.cs`：

- 保留 `GamePackageIngestionLimits` 作为安全解析执行参数；
- 增加从服务端容量/策略生成的明确入口，禁止 HTTP body/query 覆盖 limits；
- 保持已有 `GamePackageRejectionCodes`，不为相同事实增加第二组 code。

修改存档 contracts：

- 为列表容量超限增加一个稳定 Application error code；
- 不在成功响应中返回截断列表，避免用户把“不完整”误认为完整文件事实；
- 下载仍流式，不把最大存档文件读入内存。

Realtime/Worker contracts 仅在确有缺失的稳定 reason 时增加 code。不得因配置化修改 HTTP、WebSocket 或
IPC major；限制值变化不是协议 major 变化。

### 6.3 Infrastructure

修改 `Capacity/InstanceCapacityOptions.cs`：

- 增加缺失字段、编译期绝对上限和完整 `Validate()`；
- 对跨类型关系提供由 API composition root 调用的集中 validator，而不是让 Infrastructure 引用 API 类型；
- 单元测试覆盖每个非法边界和相等/差一边界。

修改 Game package 摄取：

- API/DI 构造服务端有效 `GamePackageIngestionLimits`；
- archive/expanded/single-file/entry-count 取实例配置和编译期安全策略的交集；
- reservation 使用有效上限，提交后按实际字节结算；
- 预检、解压和 fsync 前后的累计检查保持 fail closed；
- 超限清理只操作当前受保护 staging，保留持久 operation/reaper 的崩溃恢复语义。

修改 Session create/copy：

- manifest bytes/file count 在 prepare 时检查并预留；
- dirfd copy 每创建一个普通文件、每写一个 chunk 都累计实际事实；
- 源内容在 prepare 后被替换、文件长度漂移、磁盘不足或配置超限时，operation 进入可恢复失败并安全清理
  自己的 staging；不得发布半个 SessionRoot；
- 已有 SessionRoot 不因随后调低配置而被自动删除；只在新 create/open/save 操作边界应用适当限制。

修改 Saves：

- `LinuxSessionSaveRootAccessor` 注入容量选项，移除 4096/8 MiB 私有魔法常量；
- 枚举时累计条目和编码后响应预算，超过即停止并返回稳定错误；
- upload 的声明长度、实际读取、格式校验、staging publish 全程使用同一有效上限；
- 列表/下载不改变 mutation lease 语义，上传/rename/delete 继续要求停止态 lease。

### 6.4 API composition root

修改 `CloudEmuera.Api/Program.cs`：

- 通过小型 binder/helper 分组读取配置，避免 composition root 继续堆积重复 `GetValue`；
- 构造所有 options 后先逐类型验证，再执行跨类型验证，最后才准备 UDS、注册 hosted service 和监听端口；
- 配置错误不输出 secret、本地绝对 SessionRoot 或 bootstrap 路径；
- 旧 `CloudEmuera:MinDataRootFreeBytes` 和计划替换的旧 package 键只兼容读取一个周期并记录 warning；
- production `appsettings.json` 显示安全默认值或由代码默认唯一提供，不能同一默认值在多个位置漂移。

新增 `PresentationAssetOptions` 和实例级 gate：

- 在解析授权、Session binding 和 manifest 后、打开/发送文件前获取名额；
- 限制并发响应数及可选的声明在途字节预算；
- 保持 Range、ETag、MIME/signature、`nosniff` 和私有缓存语义；
- 响应完成、取消和异常路径都必须释放名额；
- 不为执行限制把资源读入 `byte[]`。

### 6.5 Worker Manager 和 Worker

修改 Worker launch 相关代码及测试：

- `SessionRootRuntimeInspector` 在进程创建前验证 SessionRoot 位于预期 data-root session container、是普通
  目录、owner 正确、没有替换、marker/runtime manifest/binding 摘要匹配；
- `ProcessStartInfo.WorkingDirectory` 固定为已验证 SessionRoot；
- `ArgumentList` 只包含 Worker assembly 和私有 `--bootstrap-file`；
- bootstrap schema 只包含当前 Worker 必要字段，不新增 Game workspace/current、数据库路径、其他 Session、
  用户 secret 或管理员配置；
- 继承环境采用最小化策略：至少移除 Hot Reload、diagnostic port、开发 watcher 和不需要的 bootstrap
  管理员变量；若改为 allowlist，必须保留 .NET globalization/graphics 所需变量并用兼容测试证明；
- bootstrap 目录/文件继续为 `0700/0600`，拒绝链接、特殊文件、异常 hardlink 和错误 owner；
- Worker 启动后再次读取并验证相同 binding，注册仍由 token、control-plane instance 和 epoch fence 决定；
- pending event/input 到达 hard limit 时使用现有 backpressure/fault 语义，不改成无界 `List` 或 channel。

新增真实子进程测试读取测试 Worker 的 `/proc/<pid>/cmdline`、允许的 environment 和 bootstrap 投影，断言：

- 只有自己的 SessionRoot；
- 不含 Game workspace/current、其他 SessionRoot、数据库文件或 bootstrap admin secret；
- 进程 UID 非 0；
- 错误 SessionRoot/binding 在 Runtime 获得写权前失败。

### 6.6 生产镜像与部署文件

修改 `docker/Dockerfile`：

- 显式 publish API、Worker、Validator 和 Migrator 产物；生产 Compose 通过 one-shot Migrator 成功后再启动
  API，后续 P1-14 只补充更完整的 PID 1、信号和恢复编排；
- 采用确定的 `/app/api`、`/app/worker`、`/app/validator`、`/app/migrator` 布局，API 配置明确指向 Worker
  和 Validator assembly；
- runtime stage 只安装必需系统库，创建非 root 默认用户，`/app` 不可由运行用户任意改写，`/data`
  和必要 runtime 目录可写；生产 Compose 用部署者 UID/GID 覆盖实际运行身份；
- 不复制源码、测试、NuGet/pnpm cache、开发证书、`.env`、Git 数据或构建 secret；
- 不在镜像中声明或创建 Docker socket 等宿主接口；
- 保持单个对外 HTTP 端口，Worker UDS 位于容器私有 runtime/data 路径且不声明为 volume/port。

新增 `docker/compose.yml` 和 `docker/.env.example`：

- `CLOUDEMUERA_DATA_PATH` 缺省时使用 `cloudemuera-data` named volume，设置后将部署者预先创建的宿主机
  数据目录 bind mount 到 `/data`；两种模式均用 `.env` 中的启动者 UID/GID 运行；
- `migrator` 作为 one-shot 服务先完成迁移，API 通过 `service_completed_successfully` 依赖门控启动；
- 不使用 privileged，不挂 Docker socket，不挂宿主 home、密钥或源码；
- 显式 `init: true`，PID 1/信号的正式编排在 P1-14 完成；
- 给出 `cpus`、`mem_limit`/等价 Compose 字段和 `pids_limit` 示例；
- 可采用 `cap_drop: [ALL]`、`no-new-privileges:true` 等不破坏运行时的容器级 hardening，但必须由生产镜像测试
  证明；它们不是恶意 Worker 沙箱声明；
- 不把 CPU/内存/PID 是否设置接入 `/health/ready`。

## 7. 稳定错误与状态语义

### 7.1 HTTP admission

- 活动 Worker 和未启动 Session 容量属于当前实例状态冲突，沿用 HTTP `409`；响应携带稳定 code，
  不泄漏其他用户 Session 数量或路径。
- Realtime 全局连接容量耗尽沿用 `503 REALTIME_CAPACITY_EXCEEDED`；单连接订阅/pending input 超限使用
  现有协议 result code。
- asset 并发容量是瞬时服务容量，返回 `429` 或 `503` 必须由 ADR-0027 冻结，并带合理 `Retry-After`；
  不能返回资源不存在以掩盖已通过的授权。
- ZIP 和 save 的格式/容量错误沿用现有 4xx 及稳定 code；磁盘底层 IO 故障与用户输入超限保持区分。

### 7.2 持久 operation

- admission 前拒绝不得创建残留 operation/staging；
- 已持久预留后失败必须记录足以恢复的阶段，reaper/recovery 只清理本 operation 拥有的受保护路径；
- 容量失败不得删除已有 Game current、SessionRoot 或存档；
- 调低配置不批量修改历史行，不把正在运行 Worker 自动标记 CRASHED；新操作按新配置执行。

### 7.3 Snapshot 和队列

- 单个合法 committed Snapshot 超过 hard limit 时不能发送截断树，也不能继续接受导致内存增长的输出；
  Hub 进入稳定 fault，Worker/Session 通过现有生命周期收敛为可诊断的 `CRASHED`，SessionRoot 保留；
- 显示队列达到 soft limit 时用最新完整 Snapshot 替换积压；达到 hard limit 或反复 resync 失败时只关闭慢连接；
- input/control queue 满返回 backpressure，不能执行两次、不能挤掉已接受输入后伪报失败；
- 日志只记录限制名、配置值、观察值、session/worker/epoch 等关联字段，不记录 Snapshot/game text、输入值、
  token 或绝对路径。

## 8. 实施步骤与提交拆分

### 步骤 0：冻结决策和基线

1. 新增 ADR-0027 和本实施方案引用；
2. 记录当前默认值、配置键、稳定错误码、相关测试和生产镜像行为；
3. 在 dev Docker 内运行完整基线 `./scripts/check.sh`；
4. 用当前 `docker/Dockerfile` 做一次生产镜像 smoke，记录 Worker 缺失或启动失败事实，不在测试中临时复制
   host build 产物绕过问题。

验证：文档交叉引用无待决默认值；基线失败与本任务缺口对应。

### 步骤 1：统一容量配置和启动校验

1. 扩展 `InstanceCapacityOptions`；
2. 增加 binder、旧键 warning 和跨 options validator；
3. 把 save list、package file count/single-file 等魔法常量接入 DI；
4. 增加边界/交叉关系单元测试。

验证：合法默认和最小边界可启动；每个非法关系均在监听端口/准备 UDS 前失败。

### 步骤 2：补齐持久资源 admission

1. ZIP 使用实例有效 limits，补齐 archive/expanded/file count/single-file 并发与实际写入测试；
2. Session create 同时限制 manifest bytes/file count、reservation 和实际复制；
3. save upload/list 使用统一配置；
4. DataRoot free-space 和 `ENOSPC` 故障注入覆盖预留后空间变化。

验证：正常、差一边界、恰好等于、超一边界、两个并发争最后预算、取消/崩溃恢复全部通过。

### 步骤 3：补齐内存和流式响应边界

1. 对 Snapshot、Worker pending event/input、Realtime connection/control/display options 做整体校验；
2. 为 asset 增加实例并发/在途预算；
3. 补充慢客户端、持续输出、反复连接和取消 asset 请求压力测试；
4. 确认 P1-12 管理诊断能反映 overflow/fault，但不新增资源时序指标。

验证：压力期间队列计数/字节不超过 hard limit，内存达到稳定平台；其他 Session/连接保持可用。

### 步骤 4：固化 Worker 启动边界

1. 启动前 SessionRoot identity/binding 复验；
2. 最小命令行、bootstrap 和环境；
3. 正常、错误 root、替换 root、错误 owner/marker/manifest、其他 Session 注入测试；
4. 真实 Worker 非 root、注册和运行兼容回归。

验证：失败发生在 Worker 获得 Runtime 写权前，旧 lease/operation 按现有补偿收敛，正常 Worker 可完成
真实 input/save roundtrip。

### 步骤 5：生产镜像和部署说明

1. 显式发布所有生产进程产物；
2. 收紧 runtime 文件布局、owner/permission 和镜像内容；
3. 新增最小生产 Compose 及整体 CPU/内存/PID 示例；
4. 新增 `scripts/test-production-image.sh`。

验证：全新临时数据卷中 API 可启动、真实 Worker 可 open/close；容器内相关进程 UID 非 0；镜像/Compose
没有敏感挂载或旧 Supervisor。

### 步骤 6：实例限制总验收

1. 新增 `scripts/test-instance-limits.sh`；
2. 使用极小但合法的临时配置触发每种容量边界；
3. 汇总需求—测试追踪，更新开发计划实施记录；
4. 运行完整仓库验证。

验证：两套 P1-13 脚本、`./scripts/check.sh`、`verify-dev-user.sh`、`verify-third-party.sh` 全部通过。

建议提交按上述步骤拆分，每个提交只包含一个可独立验证的逻辑变更，使用 Conventional Commit、scope 和
`git commit -s`。不要把 P1-14 的 PID 1/备份恢复实现混入 P1-13 提交。

## 9. 自动化测试矩阵

### 9.1 配置单元测试

- 每个字段的零、负数、绝对上限、等于上限和超过上限；
- archive/expanded/single-file、save/SessionRoot、batch/queue/Snapshot 的交叉关系；
- 旧键读取及 warning，新键优先；
- 非数字、溢出和不支持单位 fail closed；
- 日志不包含 secret 或本地绝对资源路径。

### 9.2 SQLite/文件系统集成测试

- 最后一个活动 Worker 名额的两个并发 open 最多一个成功；
- 最后一个未启动 Session 名额的两个并发 create 最多一个成功；
- close/crash 后活动名额释放，delete 后未启动名额释放；
- archive/expanded/single-file/entry-count 的 `limit-1/limit/limit+1`；
- ZIP 声明大小与实际写入不一致、压缩炸弹、链接、穿越、碰撞继续拒绝且不污染受保护目录；
- staging 总预算的并发 reservation、取消、API 中断和 reaper 释放；
- SessionRoot manifest 低估/源替换、文件数和实际复制字节超限；
- DataRoot admission 后空间下降和 fsync `ENOSPC`；
- save 上传声明/实际长度、格式校验、列表数量/预算和 mutation lease 竞争。

### 9.3 Realtime/Worker 测试

- Snapshot 恰好上限成功，超一字节进入稳定 fault；
- 单 transaction、batch 和 envelope 的上限关系；
- pending event/input、control/display queue 达到 soft/hard 边界；
- 慢客户端只影响自身，重连仍取得最新一致 Snapshot；
- input backpressure 不产生重复执行；
- 多连接/多 Session 达到连接和订阅上限；
- Worker bootstrap/命令行/环境只包含允许字段；
- 错误 SessionRoot、manifest、epoch、token、parent identity 被拒绝；
- 控制通道断开继续有界退出，此行为不因容量配置回归。

### 9.4 生产容器测试

- multi-stage 构建使用 locked/frozen dependencies；
- 最终镜像不包含 SDK、源码、测试、`.git`、`.env`、package cache 或开发证书；
- API、Worker、Validator 和 Migrator 均非 root；
- `/app` 权限不允许服务用户替换 binary，`/data` 和私有 runtime 路径可写；
- 真实 create/open 证明 Worker assembly 和依赖存在；
- Compose 解析结果无 Docker socket、宿主 home/key/source mount、privileged 或 Worker UDS port；
- 容器整体 CPU/memory/PID 示例可应用，缺失这些限制仍不改变 ready；
- 临时 project/DataRoot/env/port 在成功和失败后都可安全清理，不触碰人工环境。

## 10. 验证脚本设计

### 10.1 `scripts/test-production-image.sh`

脚本必须：

1. 通过 `scripts/lib/dev-env.sh` 取得宿主 UID/GID 等公共逻辑，并让生产 Compose 使用该 UID/GID 运行；镜像
   本身仍检查非 root 默认用户；
2. 创建 `mktemp -d` 的 env/DataRoot 和唯一 Compose project；
3. 构建 `docker/Dockerfile` 的最终 runtime image；
4. 检查镜像 config 的 `User` 非空且非 `0/root`；
5. 只通过 `docker compose up -d` 启动全新实例，由 Compose 自动完成 bootstrap 前的 Migrator 成功门控；
6. 从容器内检查 API UID，并通过正式 Session API 启动真实 Worker 后检查 Worker UID、cmdline、环境和
   bootstrap 投影；
7. 检查 Worker 能完成最小 Runtime/input/close；
8. 检查 Compose 实际 mounts、ports、privileged/capabilities 和资源限制；
9. trap 中只停止并清理测试使用的 bind/named 两个 project 和临时目录。

脚本不得挂载仓库来补充最终镜像缺失文件，也不得使用宿主 `dotnet`/Node/pnpm。

### 10.2 `scripts/test-instance-limits.sh`

脚本使用一组极小合法配置降低测试成本，按独立场景启动实例：

- active Worker = 1，两个 Session 并发 open；
- inactive Session = 1，两个并发 create；
- 小 archive/expanded/single-file/entry-count fixture；
- 小 staging 和 DataRoot free-space 故障注入；
- 小 save file/list 限制；
- 小 Snapshot、Worker pending 和 Realtime queue 限制；
- 小 asset 并发限制和取消释放；
- 每个场景检查 HTTP/protocol code、持久状态、受保护路径和后续恢复操作。

脚本只负责跨进程/容器总验收；精确差一边界和恶意 fixture 细节放在 xUnit 测试，避免 shell 重复实现
业务断言。

## 11. 文档同步

实施期间同步修改：

- `docs/adr/README.md` 和 ADR-0027；
- `docs/requirements.zh-CN.md` / `requirements.en.md`：只在发现中英文漂移时修正，不扩大范围；
- `docs/design.zh-CN.md` / `design.en.md`：配置表、Worker 边界、部署和测试追踪；
- `docs/development-plan.zh-CN.md`：详细方案链接、验证命令和最终实施记录；
- README/部署文档：生产镜像、数据卷、非 root、整体 CPU/memory/PID 示例；
- `docker/.env.example`：列出支持的配置键，不给出真实 secret，并说明从 `docker/` 目录直接执行
  `docker compose up -d`；
- 错误码和 OpenAPI：仅当 HTTP 可见 code/状态确有变化时更新并重新生成前端类型；
- 安全限制清单：明确“可信参与者/可信游戏、同 UID、无恶意 Worker 内核隔离”。

## 12. 完成定义

P1-13 只有同时满足以下条件才可标为 DONE：

1. ADR-0027 已接受，默认值、配置键、超限语义和信任边界不再待决；
2. 所有容量配置在启动期校验，部署者可配置 OPS-002 要求的每类实例上限；
3. 活动 Worker、未启动 Session、ZIP、SessionRoot、staging、save、Snapshot、IPC/WebSocket、asset 和
   DataRoot 每个入口均有自动化正常/边界/主要失败测试；
4. 并发 open/create/reservation 不越过最后一个名额；
5. 超限产生稳定错误，未出现无界队列、半发布目录、错误释放 lease、重复输入或既有数据删除；
6. Worker 启动参数、bootstrap 和环境不包含 Game workspace/current、其他 SessionRoot、数据库路径或 secret；
7. 生产镜像可从全新 named volume 或临时宿主机数据目录 bind mount 启动真实 Worker，API/Worker/Validator/
   Migrator 均以部署者的非 root UID/GID 运行，bind mount 中的持久文件归该宿主机账号所有；
8. 生产 Compose/文档没有 Docker socket、宿主密钥或无关挂载，并说明整体 CPU/内存/PID 限制；
9. 文档明确同 UID Worker 不构成恶意代码隔离，缺少额外内核隔离不影响 ready；
10. 未引入每 Worker resource governance、遥测平台、在线容量 API/UI 或 P1-14 的进程/备份范围；
11. 以下验证全部通过：

```bash
./scripts/test-production-image.sh
./scripts/test-instance-limits.sh
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
```

## 13. P1-14/P1-15 交接

- P1-14 复用本任务已验证的非 root 镜像、显式进程布局和 Compose Migrator 成功门控，补轻量 PID 1、SIGTERM
  转发、parent-death/进程组回收、停机备份恢复和升级说明；不得重新放宽 Worker bootstrap 或敏感挂载。
- P1-14 可以扩展 `test-production-image.sh`，但 P1-13 的非 root、镜像产物和容量场景必须保持独立可跑。
- P1-15 使用本任务的极限 fixture 和脚本完成 AC-010/012、容量压力及已知限制报告，并补最终性能证据；
  不重新定义配置默认值或把可信自托管实例改成敌对多租户平台。
- 如果未来需要让不受信任用户上传并运行游戏，必须新增 ADR，重新引入内核强制隔离、每 Worker 资源治理
  和相应 readiness/故障注入矩阵，不能把 P1-13 的应用路径约束宣传为沙箱。
