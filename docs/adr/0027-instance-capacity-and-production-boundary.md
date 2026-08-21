# ADR-0027：实例级容量上限与生产 Worker 边界

- 状态：Accepted
- 日期：2026-08-21
- 关联：ADR-0008、ADR-0012、ADR-0015、ADR-0016、ADR-0017、ADR-0020、ADR-0022、ADR-0023、ADR-0026

## 背景

P1 单机 MVP 已经在 ZIP 摄取、SessionRoot、原生存档、Snapshot、Realtime 队列和 API-owned Worker
中分别具备边界，但其中一部分仍是 Infrastructure 内部常量，另一部分由 API composition root 分散读取。
部署者无法用一套配置理解实例可以同时承受的 Worker、磁盘和内存压力，也缺少对这些值进行整体关系校验的
启动失败点。

本项目的当前目标是由部署者为自己和信任参与者运行的单机实例。Worker 与 API 使用同一个服务 UID，
这能限制普通文件属主和公开挂载，但不构成恶意游戏代码的内核级沙箱。P1-13 不引入独立 UID、namespace、
seccomp、Landlock、每 Worker cgroup 或资源计量；若未来允许运行不受信任的上传内容，必须重新评审这一
信任边界。

## 决定

### 1. 配置所有权和生效方式

容量是部署级实例配置，来源优先级仍为安全代码默认值、配置文件、环境变量；它不写入 SQLite，不是用户
quota，也不提供在线修改 API/UI。修改配置需要重启 API 后生效。API 在创建 HTTP listener、Worker UDS
或接收业务流量前绑定并校验所有选项；任何非法配置都以包含配置组/字段的稳定启动错误退出，不能静默钳位。

字节值统一使用整数 bytes，数量使用正整数，时间字段在配置名中带有 `Milliseconds`、`Seconds` 等明确
单位后缀。权威类型按职责拆分：

| 类型 | 配置前缀 | 内容 |
| --- | --- | --- |
| `InstanceCapacityOptions` | `CloudEmuera:Capacity` | Worker、未启动 Session、archive/expanded/file、staging、SessionRoot、save/list、DataRoot |
| `RealtimeOutputOptions` | `CloudEmuera:Realtime` | Snapshot、batch、显示队列 soft/hard 字节和消息数 |
| `RealtimeGatewayOptions` | `CloudEmuera:Realtime` | 连接、订阅、client message、控制队列、pending input、超时 |
| `WorkerManagerOptions` | `CloudEmuera:Worker` | pending event/input、IPC、注册/心跳/停止期限 |
| `PresentationAssetOptions` | `CloudEmuera:Assets` | manifest、单资源、Range、并发读取和在途字节 |

### 2. 默认值和交叉关系

默认值如下。它们是实例安全预算，不代表每个用户或每个 Worker 的配额。

| 字段 | 默认值 |
| --- | ---: |
| `MaxActiveWorkers` | 8 |
| `MaxInactiveSessions` | 64 |
| `MaxArchiveBytes` | 2 GiB |
| `MaxExpandedBytes` | 4 GiB |
| `MaxArchiveSingleFileBytes` | 1 GiB |
| `MaxArchiveEntryCount` | 50,000 |
| `MaxSessionRootBytes` | 4 GiB |
| `MaxSessionRootFileCount` | 50,000 |
| `MaxStagingReservedBytes` | 12 GiB |
| `MaxSaveFileBytes` | 64 MiB |
| `MaxSaveListedFiles` | 4,096 |
| `MaxSaveListBytes` | 8 MiB |
| `MinDataRootFreeBytes` | 1 GiB |
| Realtime Snapshot | 12 MiB |
| Realtime display queue | 1/2 MiB、32/64 条（soft/hard） |
| Worker pending event | 1 MiB、256 条 |

启动时至少强制以下关系，并同时应用代码中的绝对安全上限：

- 所有数量和字节值为正，`MinDataRootFreeBytes >= 0`；
- `MaxArchiveSingleFileBytes <= MaxExpandedBytes <= MaxSessionRootBytes`；
- `MaxSaveFileBytes <= MaxSessionRootBytes`；
- 一次 ingestion 的最大 archive+expanded reservation 不超过 `MaxStagingReservedBytes`；
- 每个 soft queue 严格小于对应 hard queue；
- batch target <= queue hard bytes <= Snapshot；
- Snapshot、Worker pending 和最终浏览器 server envelope 不超过 IPC/Realtime 协议绝对上限；
- asset 单资源预算不超过 SessionRoot 预算，asset 在途预算至少能容纳一个单资源。

配置值不会因当前磁盘剩余空间变化而自动改写；`MinDataRootFreeBytes` 只作为每次大写入 admission 和
最终写入错误的安全阈值。

### 3. 执行点和超限语义

- 活动 Worker 在 SQLite `BEGIN IMMEDIATE` 内计数并写入 STARTING lease；超过上限返回
  `ACTIVE_WORKER_LIMIT_EXCEEDED`，不递增 epoch、不创建第二个 Worker。
- 未启动 Session 在 create prepare 事务内计数；超过上限返回 `INACTIVE_SESSION_LIMIT_EXCEEDED`。
- archive 在 HTTP bounded stream 和 ingestion copy 中按实际字节限制；ZIP preflight 与实际解压分别
  检查 entry count、single-file 和 expanded bytes，使用现有稳定的 `ARCHIVE_TOO_LARGE`、
  `ENTRY_COUNT_EXCEEDED`、`ENTRY_TOO_LARGE`、`EXPANDED_SIZE_EXCEEDED` 等错误。
- staging reservation 在同一事务内汇总；提交、失败和 reaper/recovery 都只释放本 operation 的预留，
  超限返回 `STAGING_BUDGET_EXHAUSTED`。
- SessionRoot 在 prepare 时检查清单并在 dirfd copy 中重新累计文件数和字节；失败保留可恢复 operation
  记录，不发布半目录，也不删除既有 SessionRoot。
- 存档上传继续使用 Content-Length、bounded stream、格式校验、mutation lease 和 operation；列表在
  dirfd 枚举过程中达到文件数或 UTF-8 JSON 列表预算就停止，返回 `SAVE_LIST_LIMIT_EXCEEDED`。
- Presentation asset manifest 超限返回 `SESSION_ASSET_MANIFEST_TOO_LARGE`（413）；单资源或实例并发
  在途预算不足返回 `SESSION_ASSET_CAPACITY_EXCEEDED`（503），读取 lease 在完整流结束、取消或异常时
  释放。单 Range 超过 `MaxRangeBytes` 返回 `SESSION_ASSET_RANGE_TOO_LARGE`（416）。
- Snapshot、IPC pending 和 WebSocket 队列沿用现有 fault/backpressure 语义：不截断合法 Snapshot、
  不扩容无界队列；慢连接关闭，Hub/Worker fault 由现有生命周期对账为可诊断的 `CRASHED`，SessionRoot
  保留。

### 4. Worker 进程边界

API-owned Worker 仍通过 `SessionRootRuntimeInspector` 在创建子进程前重新检查 data-root 下的持久
SessionRoot、普通目录、私有 marker、runtime manifest、binding 摘要、owner/link/reparse 状态。
`WorkingDirectory` 固定为已检查的 SessionRoot，命令行只包含 Worker assembly 和私有
`--bootstrap-file`。bootstrap 只投影当前 Session 的 binding、SessionRoot、UDS、token 和运行时必要的
显示/超时字段，不包含 Game workspace/current、数据库路径、其他 SessionRoot、用户 secret 或管理员配置。

Worker 启动时再次校验 bootstrap 和 binding；API 仍使用 control-plane instance、token、epoch fencing 和
父进程退出屏障约束注册、心跳、输出和输入。Worker 环境移除 Hot Reload、diagnostic port、watcher、
bootstrap 管理员变量以及 API 的 `CloudEmuera__*` 配置；保留显式的非 secret runtime debug 开关。

生产镜像把 API、Worker、Validator、Migrator 分别发布到 `/app/api`、`/app/worker`、`/app/validator`、
`/app/migrator`，镜像默认以非 root 用户运行，`/app` 只读，只有 `/data` 可写。生产 Compose 的实际
运行身份与数据目录所有权由 [ADR-0028](0028-production-bind-mount-ownership.md) 冻结：部署者提供
UID/GID，`CLOUDEMUERA_DATA_PATH` 缺省时使用 named volume，设置后使用部署者目录 bind mount；API、
Worker、Validator 和 Migrator 使用该同一非 root 身份。不挂 Docker socket、宿主 home、密钥、源码或
Worker UDS，不使用 `privileged`，并示例 `init: true`、整体 `cpus`、`mem_limit` 和 `pids_limit`。
这些是部署层整体限制，不进入 `/health/ready`，也不声明成恶意 Worker 沙箱能力。

### 5. 旧键兼容

`CloudEmuera:Capacity:MaxGamePackageBytes` 作为 `MaxArchiveBytes` 的旧键只兼容一个发布周期；旧键
被使用时输出一次 deprecation warning。历史根键 `CloudEmuera:MinDataRootFreeBytes` 同样只兼容一个周期，
新部署必须使用 `CloudEmuera:Capacity:MinDataRootFreeBytes`。新键与旧键同时存在时新键优先，不自动合并或
钳位。

## 备选方案

1. 继续保留各组件常量：改动少，但部署者无法校验跨层预算，也不能可靠解释磁盘和内存拒绝。
2. 把容量写入 SQLite 或提供管理员在线修改：会让配置变成业务状态，增加并发、审计和重启一致性范围，
   与当前单机部署目标不符。
3. 为每个 Worker 创建独立 UID/namespace/cgroup：更接近敌对代码沙箱，但超出 P1-13 的可信自托管边界，
   会引入运维和 readiness 依赖，留待未来新增 ADR。

## 后果

正面结果是所有大对象入口都拥有可追踪的 admission 或实际累计边界，容量拒绝不会改变已有 current、
SessionRoot、存档或 epoch；配置错误尽早暴露，生产镜像不会因缺少 Worker/Validator 产物而出现假 ready。
代价是部署者需要理解一组带关系的配置，调低配置不会清理历史数据，且生产实例仍必须只运行可信参与者和
可信游戏。容器整体 CPU/内存/PID 限制与应用级容量门需要分别配置和分别验证。

## 验证

- `InstanceCapacityOptions`、`PresentationAssetOptions`、save list 和 Worker environment 有单元/集成测试；
- dev Docker 中运行完整 solution build/test、runtime compatibility、`verify-dev-user.sh` 和
  `verify-third-party.sh`；
- `scripts/test-production-image.sh` 构建最终镜像，检查非 root UID、四类产物、部署者 UID/GID、仅 `/data`
  bind mount、资源限制和真实 API Worker 生命周期；
- `scripts/test-instance-limits.sh` 使用临时 project、DataRoot、端口和小合法配置运行容量/并发回归，不
  触碰人工 `.env`、`./data` 或其他 Compose project。
