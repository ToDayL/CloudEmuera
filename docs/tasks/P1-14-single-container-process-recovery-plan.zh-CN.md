# P1-14 单容器进程管理与恢复实施方案

状态：已完成（2026-08-22；按 2026-08-21 产品约束修订）

日期：2026-08-21

关联需求：MVP 单容器、AC-003、AC-006、AC-007、OPS-003、NFR-002～005

关联决策：ADR-0015、ADR-0016、ADR-0017、ADR-0027、ADR-0028

前置任务：P1-05、P1-06、P1-12、P1-13

后续任务：P1-15 MVP 验收、安全与性能门

## 1. 目标结果

P1-14 不重新设计 Worker 沙箱或 Session 生命周期，而是把 P1-05/P1-13 已有的 API-owned Worker、
持久 lease、parent-death、生产镜像和同容器入口迁移收敛成可重复验证的容器启动、停止和恢复
闭环。完成后必须得到：

1. 生产运行期只有一个长期 API 容器；每个活动 Session 的 Worker 都是该 API 在同一容器内创建的
   子进程。容器入口脚本先运行 Migrator，再以 `exec` 启动 API，不存在独立 Migrator service。
2. Compose `SIGTERM` 能立即令 API 进入 draining，在固定总期限内并行停止全部 Worker；超时 Worker
   被强制回收。因控制面停止而中断的 Session 最终为 `CRASHED`，SessionRoot 保留。
3. API 被强制终止或 Worker 控制流断开时，现有 Linux parent-death 和 Worker 自退出语义能够防止
   Worker 脱离控制面继续运行；新 API 根据持久进程身份确认旧写者消失后完成故障对账。
4. `CLOSED`/`CRASHED` Session 都能用同一 Session ID 和 SessionRoot 重新开启；重新开启只递增 epoch，
   不重新复制 Game current content，也不宣称恢复崩溃前的解释器内存。
5. 默认开发环境由 API 和 Vite `web` 两个长期容器组成；API 服务已构建的 SPA，`web` 在 `5173`
   提供 HMR 并代理 API。Migrator、测试和 Playwright 容器只作为一次性工具运行，开发数据使用专用
   named volume。
6. 文档提供简单、可执行的停机备份和恢复步骤；自动化以隔离 named volume 中的临时 DataRoot 验证停机复制后元数据、
   SessionRoot 和存档仍可使用。

完成标准不是增加新的进程管理框架，而是现有轻量机制在真实 Docker Compose 中具有明确时间预算、
状态结果和故障验证。

## 2. 范围与非目标

### 2.1 本任务必须完成

- 把 API 停止改为“立即 draining、并行优雅停止、并行强制回收、持久 CRASHED”的有界流程；
- 统一应用 Host shutdown 与 Compose stop grace period，避免 Docker 在状态收敛前强杀容器；
- 保留并验证 Compose `init: true` 的信号转发和 zombie 回收；
- 保留并验证 Worker 的 `PR_SET_PDEATHSIG`、控制流断开退出和进程树终止；
- 将 Migrator 封装进生产和开发容器入口脚本，在 API 启动前同步完成迁移；
- 新增真实生产镜像进程恢复脚本；
- 将默认 dev 拓扑调整为一个长期 API 容器；
- 增加停机备份/恢复说明及临时数据恢复测试；
- 同步 README、详细设计、开发计划和中英文需求中的部署说明。

### 2.2 明确不做

- 不新增 s6、Supervisor、systemd、独立 Worker Host 或其他常驻控制进程；
- 不在镜像内下载或维护新的 PID 1 二进制，继续使用 Docker Compose `init: true`；
- 不增加 `setpgid`、自定义 process group、cgroup、namespace、seccomp、Landlock 或独立 Worker UID；
- 不增加 Worker PID/process-group 数据库字段或 schema migration；
- 不改变现有 PID、boot ID、process start ticks 和 epoch fencing 语义；
- 不实现 API 重启后接管旧 Worker、无损滚动升级或多 API；
- 不实现在线备份、增量备份、备份计划任务、加密归档或远端备份服务；
- 不提供任意指令点恢复。故障后的 reopen 始终是在原 SessionRoot 上冷启动 Emuera；
- 不为自动化测试增加生产可调用的“断开 Worker”管理接口。

## 3. 当前基线与实际缺口

### 3.1 可直接复用

- `docker/Dockerfile` 已把 API、Worker、Validator、Migrator 和 SPA 发布进同一个最终镜像；入口为
  `/app/start.sh`，默认 root，生产 Compose 可在 bind mount 时使用部署者 UID/GID。
- `docker/compose.yml` 只保留一个 `api` 服务，由 `/app/start.sh` 在同一容器内迁移后启动 API。
- API `WorkerManagerHostedService` 已在启动时枚举持久 lease，通过 PID、boot ID 和 process start ticks
  精确确认旧 Worker，并对确认退出的 lease 执行 `CRASHED` 对账。
- 无法确认进程身份的 lease 已只冻结对应 Session 的 open/存档写，不使整个可信自托管实例失去 ready。
- Worker 已安装 Linux `PR_SET_PDEATHSIG(SIGKILL)`；控制流断开后停止 Runtime 并退出，不尝试跨 API
  实例重连。
- API 已能发送 `StopWorker`、等待 `WorkerStopped`/进程退出，并通过
  `Process.Kill(entireProcessTree: true)` 回收超时 Worker。
- Realtime 和 Session command 已存在 control-plane draining 拒绝语义。

### 3.2 必须修复

1. `WorkerManager.DisposeAsync` 逐个停止 Worker。默认 8 个活动 Worker、单 Worker 5 秒 timeout 时，
   最坏时间随 Worker 数线性增加，可能超过 Docker 默认停止宽限。
2. draining 主要在 `WorkerManagerHostedService.StopAsync` 开始。Host 会按注册顺序反向停止多个
   HostedService，应在收到 `ApplicationStopping` 时立刻建立控制面屏障，不能等到 Worker hosted
   service 最后才拒绝新命令。
3. 应用没有显式的 Host shutdown 总预算，生产 Compose 也没有 `stop_signal`/`stop_grace_period`，
   应用内部 timeout 与容器外部 timeout 尚未形成闭环。
4. `scripts/test-process-recovery.sh` 尚不存在；现有 `test-production-image.sh` 只覆盖正常 Worker 使用和
   用户 close，没有覆盖 API SIGTERM/SIGKILL、Worker SIGKILL、重启对账和同 Session reopen。
5. 开发 Compose 默认同时长期运行 API 和 Vite，不能直接复现生产单长期容器的信号和父子进程关系。
6. README 只有生产启动说明，没有完整的停机数据目录复制与恢复步骤。

## 4. 目标进程与状态语义

### 4.1 生产拓扑

```text
init (PID 1)
  └─ /app/start.sh
      ├─ Migrator（启动前同步、一次性）
      └─ exec API
          ├─ Worker(session A)
          ├─ Worker(session B)
          └─ Validator（按请求短暂运行）
```

“单容器”在本任务中表示一个长期应用容器。Migrator 是该容器启动脚本中的一次性阶段；构建、测试和数据
复制所用的一次性工具容器也不属于运行期服务。API 运行期间仍是唯一访问 SQLite 的业务进程。

### 4.2 停止原因与最终状态

| 触发 | Worker 停止原因 | 最终 Session 状态 |
| --- | --- | --- |
| 用户显式 close | 现有用户 close reason | `CLOSED` |
| 管理员 force-stop | `admin_force_stopped` | `CRASHED` |
| API/容器 SIGTERM | `control_plane_stopped` | `CRASHED` |
| API SIGKILL/控制流断开 | 新 API 对账 `control_plane_restarted` 或现有断流 reason | `CRASHED` |
| Worker 自身异常退出 | 现有 runtime/heartbeat reason | `CRASHED` |
| 游戏正常完成 | 现有自然完成语义 | `CLOSED` |

API 停止时 Worker 即使返回退出码 0，也不能把控制面中断伪装成用户正常关闭。最终状态写入必须继续
使用完整 binding 和 `state_version` CAS；退出监视器与 Host shutdown 同时观察到退出时，最多一个
写入生效，另一个把条件更新未命中视为已由权威路径收敛。

## 5. 有界停止设计

### 5.1 时间预算

本任务使用少量固定默认值，不增加新的复杂配置面：

| 阶段 | 建议默认值 | 说明 |
| --- | ---: | --- |
| Worker 优雅停止 | 5 秒 | 复用 `WorkerShutdownTimeout` |
| 强制终止后确认退出 | 5 秒 | 所有剩余 Worker 共用一个截止时间 |
| ASP.NET Host shutdown | 15 秒 | 包含 Worker 回收和 SQLite 最终 CAS |
| Compose stop grace period | 20 秒 | 比 Host 多 5 秒容器退出余量 |

若继续允许 `CloudEmuera:Worker:WorkerShutdownTimeoutSeconds` 覆盖 Worker timeout，则启动时只需验证
Host shutdown 大于两段 Worker 回收所需预算、Compose 示例值覆盖默认配置。P1 MVP 不增加在线修改接口。

### 5.2 收到停止信号

在 `WorkerManagerHostedService.StartAsync` 注册 `IHostApplicationLifetime.ApplicationStopping` 回调。
回调必须同步、幂等且不做 IO，只执行：

1. `SessionRuntimeCoordinator.BeginDraining()`；
2. `WorkerManager.BeginDraining()`；
3. readiness 标记为 `control_plane_stopping`。

这样 SIGTERM 到达后，新 open、输入和 Realtime resume 会立即走已有 draining/not-ready 错误，不等待
其他 HostedService 依次停止。HTTP listener 随 Host 正常停止，不需要增加独立流量代理。

### 5.3 并行停止 Worker

把当前串行 dispose 改为以下固定阶段：

1. 快照 `WorkerManager.Workers.ToArray()`，停止启动新 Worker；
2. 并行向全部快照 Worker 发送 `StopWorker(control_plane_stopped)`；
3. 使用同一个优雅截止时间等待每个 Worker 的 `WorkerStopped` 或进程退出；
4. 对失败/超时且仍存活的 Worker 并行调用现有 `TerminateProcessAsync`；
5. 使用同一个强制退出截止时间确认所有进程结果；
6. 对已确认退出的 Worker 以短事务并行/有限并发写入 `CRASHED`；
7. 未确认退出的 Worker 保留 lease，记录稳定错误，交给下次启动对账；
8. 清理 UDS、bootstrap 临时文件、进程输出 pump 和专用 launcher 线程。

禁止为每个 Worker 重新创建完整的 5+5 秒串行预算。实现可以用 `Task.WhenAll` 和共享
`CancellationTokenSource.CancelAfter`；单个 Worker 的异常转换成该 Worker 的结果记录，不能提前取消
其他 Worker 的回收。

### 5.4 启动对账

保持现有算法，不扩大恢复协议：

- 精确身份不存在或已退出：对账为 `CRASHED`；
- 精确身份仍存活：调用现有进程树终止并等待，确认后对账；
- 身份缺失、PID 已复用或无法确认退出：保留 lease/write fence；
- 单个 fence 不影响实例 ready 和其他 Session；
- 新 API 不接受旧 control-plane instance 的注册，也不重建旧 Worker 实时连接。

最多只有实例级活动 Worker 上限数量的遗留进程，P1-14 不进一步实现并行启动清理或进程组扫描；先以
真实脚本证明现有启动时间满足 MVP。

## 6. 生产镜像与 Compose 改动

### 6.1 `docker/Dockerfile`

- 复制 `/app/start.sh` 为唯一入口；默认路径先执行 `dotnet ...Migrator.dll migrate`，再 `exec` API；
- 不加入 s6、Supervisor、systemd 或其他常驻进程管理包；
- 声明 `STOPSIGNAL SIGTERM`，保持 `/app` 只读、`/data` 可写及四类发布产物；
- 不创建固定 `10001` 用户；默认 root 兼容 named volume，bind mount 可由 Compose 指定 UID/GID；
- 已构建 SPA 由 API 静态服务，不启动独立 web 运行时进程。

### 6.2 `docker/compose.yml`

API 服务补充：

```yaml
init: true
stop_signal: SIGTERM
stop_grace_period: 20s
```

只保留 `api`、唯一 `/data` volume、`init`、停止信号/宽限期，以及现有安全选项。不设置 CPU 限制，
不在生产 environment 中主动映射 `CloudEmuera__Capacity:*`。宿主 HTTP 端口默认只绑定 `127.0.0.1`，是否使用
HTTPS 和跳转由上级网关选择，应用不强制协议；直接公网绑定必须显式设置 `CLOUDEMUERA_HTTP_BIND_ADDRESS`。
公开入口使用 HTTPS 时可设置 `CLOUDEMUERA_SECURITY_SECURE_COOKIES=true`。健康检查可以继续由外部访问
`/health/live`/`ready` 验证，不要求为了 P1-14 增加镜像内 curl 等工具。

### 6.3 ASP.NET Host

在 composition root 配置 `HostOptions.ShutdownTimeout = 15s`，并用配置/单元测试冻结默认值。Host
取消 token 到期后不得继续无限等待 Worker；未完成的 lease 留给下次启动对账。

## 7. 默认 dev 单容器

### 7.1 Compose 服务

保留 `api` 服务名，避免修改全部既有命令；调整为默认 API + Vite 前端的开发拓扑：

- `api`：构建后的 ASP.NET API，直接管理 Worker，并服务 Vite 构建产物；
- `web`：默认长驻的 Vite pnpm 服务，监听宿主 `5173` 并通过 Compose 网络代理 API；
- `e2e`：继续是 profile 下的一次性 Playwright 工具服务。

开发 API 的 `/data` 始终挂载 Compose 管理的 `cloudemuera-dev-data` named volume；开发 Compose 不读取
`CLOUDEMUERA_DATA_PATH`，因此不会自动使用 checkout 的 `./data`。使用唯一 Compose project 的自动化测试
会获得隔离的 named volume，并由 `down --volumes` 清理。

开发 API 设置 `ASPNETCORE_WEBROOT=/workspace/src/CloudEmuera.Web/dist`。不要把 Vite 构建结果复制进
受 Git 跟踪的 `src/CloudEmuera.Api/wwwroot/index.html`，也不在 API 容器内安装 Node。

### 7.2 `scripts/dev-up.sh`

顺序改为：

1. 读取宿主 UID/GID 和可选 `docker/.env`；
2. 构建 `api`、`web` 工具镜像；
3. 优雅停止可能运行的旧 API；
4. 在一次性 `api` 容器中 restore/build API、Worker、Validator、Migrator；
5. 在一次性 `web` 容器中执行 frozen install 和 production build；
6. 执行 `docker compose up --detach api web`，由 `dev-start.sh` 在容器内迁移后启动 API，同时启动 Vite；
7. 输出 API/构建 SPA 地址 `http://localhost:28648` 和 Vite 地址 `http://localhost:5173`；生产默认宿主端口为 `28647`。

停止旧 API 必须发生在替换运行 DLL 前，继续避免 `dotnet watch`/CLI host 改变 API—Worker 直接父子关系。

### 7.3 检查与身份验证

- `scripts/check.sh` 继续用一次性 `api` 容器运行 .NET，用命令覆盖让 `web` 容器运行 pnpm 检查；
- 前端契约验证通过 Docker 网络访问当前 API，或由脚本显式启动临时 API；
- `scripts/verify-dev-user.sh` 验证 `api`、Vite `web` 和 `e2e` 都使用宿主 UID/GID；
- 增加默认 `dev-up` 后 `api` 和 `web` 两个长期 service 都在运行的检查；
- `dev-up` 的 Vite 前端不额外引入 profile，`./scripts/check.sh` 仍不依赖前端长驻进程。

## 8. 停机备份与恢复

P1-14 只提供冷备份说明，不增加产品备份 API 或常驻任务。

### 8.1 备份

1. `docker compose stop api`；
2. 等待容器停止成功，确认没有活动 Worker；
3. 复制整个 DataRoot 到 DataRoot 之外的备份目录；
4. 保留原目录层级、文件权限和部署 UID/GID；
5. 重新启动 API，并等待 `/health/ready`。

必须整体复制 SQLite 数据库及可能存在的 WAL/SHM、Data Protection keys、games、sessions、SessionRoot
和持久 operation 数据。不能只复制 `.db` 或只复制存档目录。

### 8.2 恢复

1. 停止 API；
2. 把当前 DataRoot 移到单独保留位置，不在原目录上混合覆盖；
3. 将完整备份复制到配置的 DataRoot；
4. 恢复为部署 UID/GID 可读写；
5. 执行 `docker compose run --rm --no-deps api rebind-session-roots`，由入口先迁移、校验数据库 marker，并离线刷新
   Game 目录和 SessionRoot 的 directory identity；
6. 启动 API 并等待 ready；
7. 检查 Game、Session 和存档；原活动 Session 经对账后应为 `CRASHED`，可显式 reopen。

文档同时给出 bind mount 和 named volume 的 Compose 命令示例。恢复涉及替换数据目录，自动化只在
`mktemp` 创建的隔离目录或隔离 named volume 执行；项目脚本不得默认覆盖人工 `./data`。

## 9. 文件级改动

### 9.1 API/Worker

- `src/CloudEmuera.Api/Program.cs`
  - 配置 Host shutdown timeout；
  - 不改变公开 HTTP/WebSocket 协议。
- `src/CloudEmuera.Api/Workers/WorkerManager.cs`
  - ApplicationStopping 立即 draining；
  - 并行两阶段 Worker shutdown；
  - 统一控制面停止的 `CRASHED` 收敛；
  - 保留现有启动对账、PID identity 和强制进程树终止。
- `src/CloudEmuera.Worker/Program.cs`
  - 原则上不改；仅在测试发现 SIGTERM/断流退出无法满足时间预算时做最小修复。

### 9.2 Docker 与脚本

- `docker/Dockerfile`：声明 `STOPSIGNAL SIGTERM`；
- `docker/compose.yml`：显式 stop signal/grace period；
- `docker/compose.dev.yml`：默认运行 API 和 Vite `web`，API 使用开发专用 named volume；
- `scripts/dev-up.sh`、`dev-down.sh`、`check.sh`、`verify-dev-user.sh`：适配单长期容器；
- `scripts/test-production-image.sh`：保留现有正常生产纵切并补充拓扑/停止配置断言；
- `scripts/test-process-recovery.sh`：新增真实 SIGTERM/SIGKILL/恢复纵切。

### 9.3 文档

- `README.md`：默认 dev 地址、Vite 前端、开发数据卷、生产停止、备份和恢复；
- `docs/requirements.zh-CN.md`、`requirements.en.md`：只在现有编号下同步单长期容器和冷备份表述；
- `docs/design.zh-CN.md`：启动/停止顺序、时间预算、dev 拓扑；
- `docs/development-plan.zh-CN.md`：完成后记录命令、结果和日期；
- 本任务不新增 ADR 或第三方依赖声明。

## 10. 自动化测试

### 10.1 后端测试

在现有 Worker/Application/API 测试工程增加：

1. 多 Worker shutdown 同时发送停止，不按 Worker 数串行等待；
2. 一个 Worker 优雅退出、一个超时时，超时 Worker被强制终止且另一个不受影响；
3. ApplicationStopping 回调立即使 open/input 返回 draining；
4. 控制面停止后，即使 Worker 正常退出也最终写为 `CRASHED`；
5. shutdown 与 exit monitor 竞争时只产生一个权威终态；
6. 持久化最终状态失败时 API 仍有界退出，lease 可由下一实例对账；
7. 用户 close 仍为 `CLOSED`，没有被新的控制面停止逻辑回归为 `CRASHED`；
8. 控制流断开后真实 Worker 有界退出，不建立第二条连接。

测试以 `Category=WorkerLifecycle` 或现有对应分类标注，并映射 AC-003/006/007。

### 10.2 `test-production-image.sh`

继续在独立临时 project/DataRoot/端口中验证：

- 全新数据目录由同一容器入口中的 Migrator 初始化，API 只在迁移成功后启动；
- named volume 默认 root 可写，bind mount 可验证配置的 UID/GID；
- 生产宿主端口默认只绑定 `127.0.0.1`，直接公网绑定必须显式配置并由外部 HTTPS 网关承担公网入口；
- API Compose 配置包含 `init: true`、`SIGTERM` 和明确 grace period；
- 生产镜像不包含旧 Supervisor 服务/入口；
- 上传 fixture、创建 Session、启动真实 Worker、Realtime 输入和用户 close 正常工作；
- `/health/live`、`/health/ready` 和 `/api/v1/version` 正常。

### 10.3 `test-process-recovery.sh`

脚本复用或抽取生产纵切中的登录、上传、创建 Session helper，执行：

1. 启动真实 Session，记录 `sessionId`、epoch、Worker PID 和 SessionRoot 文件标记；
2. `docker compose stop api`，测量退出时间不超过 Compose grace period；
3. 确认旧 Worker 不再存在；
4. 重新启动 API，确认 Session 为 `CRASHED`、Root 标记仍在；
5. reopen 同一 Session，确认 Session ID 不变、epoch 增大；
6. SIGKILL 当前 Worker，确认 API 保持 live，Session 在心跳/退出窗口内为 `CRASHED`；
7. 再次 reopen，然后 SIGKILL API，确认容器退出且 Worker被 parent-death/容器回收；
8. 启动新 API，确认再次对账为 `CRASHED`；
9. 在停止状态复制整个临时 DataRoot 到第二个临时目录，以新的 Compose project 启动恢复副本；
10. 确认相同 Game/Session、Root 标记和存档存在，并可 reopen。

控制流断开使用现有真实 Worker 集成测试覆盖，不在生产 API 暴露测试开关。无法确认遗留进程的局部
fence 继续由 Application/Infrastructure 测试覆盖，Bash 脚本不伪造宿主 PID。

## 11. 实施顺序

1. 先增加/调整 Worker shutdown 单元与集成测试，冻结并行全局期限和状态语义；
2. 实现 ApplicationStopping draining 与 Worker Manager 并行两阶段停止；
3. 配置 Host shutdown timeout、Docker stop signal/grace period；
4. 扩展生产镜像测试并新增 `test-process-recovery.sh`；
5. 将 dev 默认改为单长期 API 容器，适配 dev/check/UID 脚本；
6. 增加冷备份/恢复文档并把恢复场景接入进程恢复脚本；
7. 运行定向测试、生产/恢复脚本和完整质量门；
8. 更新开发计划为 DONE，记录实际测试数量和任何与本方案不同的实现取舍。

建议提交拆分：

1. `docs(p1-14): define process recovery implementation plan`
2. `refactor(worker): bound control-plane shutdown`
3. `build(container): align stop and development topology`
4. `test(p1-14): cover process and data recovery`
5. `docs(operations): document cold backup and restore`

所有提交使用 `git commit -s`，且每个提交只包含一个逻辑变更。

## 12. 验证命令与完成定义

定向验证：

```bash
docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.Application.Tests --no-restore --configuration Release \
  --filter 'Category=WorkerLifecycle'

docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release

docker compose -f docker/compose.dev.yml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release

./scripts/test-production-image.sh
./scripts/test-process-recovery.sh
./scripts/verify-dev-user.sh
```

最终验证：

```bash
./scripts/dev-up.sh
./scripts/check.sh
./scripts/test-production-image.sh
./scripts/test-process-recovery.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
```

通过条件：

1. 全新 named volume 和 bind mount 均可由单容器入口中的一次性 Migrator 启动；
2. 默认生产和 dev 都只有一个长期 API 容器；
3. named volume 默认使用 root，bind mount 时 API、Worker、Validator 和 Migrator 使用配置的 UID/GID；
4. 多 Worker SIGTERM 总时间有界，不随 Worker 数量线性累加；
5. API SIGTERM/SIGKILL 和 Worker SIGKILL 后没有遗留 Worker；
6. 受影响 Session 对账为 `CRASHED`，SessionRoot 保留且同 Session 可用更大 epoch reopen；
7. 用户显式 close 仍为 `CLOSED`；浏览器断开仍不停止 Worker；
8. 单个无法确认的旧 lease 只冻结对应 Session；
9. 停机复制恢复后数据库、Game、SessionRoot 和原生存档一致；
10. `./scripts/check.sh`、生产镜像、进程恢复、UID/GID 和第三方验证全部通过。
