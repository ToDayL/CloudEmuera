# ADR-0015：API 直接管理 Session Worker 生命周期

状态：Accepted

日期：2026-08-11

2026-08-12 修订说明：ADR-0017 保留本文的 API-owned Worker Manager、持久 WorkerLease、epoch、
单 Worker 和故障对账，但取代每 Worker 强沙箱/资源限制以及控制通道短断重连要求。当前产品语义为
同 UID 非 root 子进程，控制通道断开即退出；本文相关历史表述不再作为 MVP 完成门。

## 背景

早期设计把 Web/API、Worker Supervisor 和每个 Session 的 Worker 分成三类进程。独立 Supervisor
负责进程启动、心跳、epoch、租约和故障对账，并在 API 重启期间继续管理 Worker。该拓扑可以让
控制面局部重启而不中断游戏，但要求 API 与 Supervisor 共同协调 SQLite 中的 Session、活动配额和
WorkerLease，并额外维护 API—Supervisor 命令、去重、重连、状态同步和两套进程恢复逻辑。

MVP 已明确只部署一个容器、一个 API 实例和一台主机，不要求 API 重启期间保留正在执行的
Session。ADR 定稿时 P1-05 的持久 Worker 管理尚未实现；当时的 `CloudEmuera.Supervisor` 只承载
P0-06 的进程隔离和 Worker IPC 证明。因此在进入持久租约实现前收回进程边界，可以降低单机 SQLite 的多进程
协调成本，而不牺牲每 Session 独立 Runtime 的安全边界。

## 决定

- MVP 只保留一个长驻控制面进程：Web/API。API 是运行期间唯一访问 SQLite 的业务进程；独立
  `CloudEmuera.Migrator` 只在 API 启动前持有独占迁移锁并完成迁移或检查，Session Worker 不访问
  SQLite。
- API 内建立边界清晰的 Worker Manager。Application 定义 Worker 生命周期端口和事务用例，外部
  实现负责 UDS、启动凭据、进程创建/监视、心跳、资源限制和终止。HTTP 请求、Realtime Gateway
  和后台 Worker Manager 不共享长生命周期 `DbContext`，并继续使用短事务、CAS 和
  `state_version`。
- 每个活动 Session 仍由一个独立 Session Worker 操作系统进程承载。API 不加载 Emuera Runtime，
  Worker 不承载多个 Session，也不获得 Game 库、其他 SessionRoot 或数据库访问权。
- Worker 通过受限 UDS 上的版本化 gRPC 双向流连接 API，注册身份仍绑定
  `sessionId + workerId + epoch + bootstrap token`。移除的是 API—Supervisor IPC，不是
  API—Worker IPC、协议校验、epoch fencing、结构化输出或背压。
- Session、活动配额和 WorkerLease 是持久事实。按 ADR-0016，Session 创建先独立物化一次
  SessionRoot；open 时 API 在短事务中写入 `STARTING`、递增 epoch 和 lease，提交后才执行
  SessionRoot 安全复核、沙箱配置和进程启动。不得在 SQLite 事务中等待文件复制、IPC 或进程退出。
  任何后序失败都以匹配 `sessionId + workerId + epoch + state_version` 的条件更新收敛到 `CRASHED`。
- 正常 API 停止时，Worker Manager 先停止接收新 Session/输入，对所有活动 Worker 执行有界优雅
  停止，超时后强制终止。因控制面停止而中断的活动 Session 标记为 `CRASHED`，保留 SessionRoot，
  不伪装成用户完成的 `CLOSED`。
- 每次 API 启动生成新的控制面实例身份。Worker 与控制通道断开后只允许在短且有界的宽限期内向
  同一 API 实例重连；超过期限或发现实例身份变化时必须自行退出。Linux parent-death signal、专属
  进程组或 cgroup 作为清理兜底，不能把子进程会随父进程自然退出当作保证。
- API 启动时对持久活动 Session 执行故障对账：先通过 PID/start identity、进程组或 cgroup 确认
  上一实例的 Worker 已退出，必要时终止并等待；随后以 CAS 失效 lease、释放活动配额并把 Session
  标记为 `CRASHED`。在无法证明旧 Worker 已失去 SessionRoot 写权限时，API 不得释放租约、启动新
  Worker 或进入正常 ready。
- 浏览器断开仍不关闭 Session，也不改变其持久 `RUNNING` 状态；Realtime Gateway 只在内存中维护
  当前连接数和最近连接时间，不建立 `DETACHED` Session 状态。API 进程退出、容器重启和 Worker
  崩溃都不提供指令级恢复，只保证 SessionRoot 原样保留和故障可诊断。
  按 ADR-0016，完成旧 Worker 回收后用户可以用更大的 epoch 重新开启同一 Session，并通过游戏
  原生功能加载该 SessionRoot 中的存档。

## 备选方案

### 保留独立 Supervisor 并继续访问 SQLite

可以保留 API 独立重启能力，但 Session 状态转换、配额、租约和进程事实跨两个业务进程协调，正是
当前希望消除的主要维护成本。MVP 不要求该可用性，因此不采用。

### 保留无数据库的独立 Supervisor

让 API 成为 SQLite 唯一访问者、Supervisor 只报告进程事实，可以减少数据库争用，但仍需维护
API—Supervisor 命令幂等、事件缓冲、重连和故障恢复协议。若未来需要 API 无损滚动重启，可重新
评估该方案；当前单实例 MVP 不为此保留独立进程。

### 把 Emuera Runtime 合并进 API

进程数量最少，但 Emuera 全局静态状态、崩溃、资源耗尽和不受信任文件访问会直接影响身份与数据
控制面，也无法可靠隔离并行 Session，因此禁止。

### API 重启后接管存活 Worker

需要稳定跨实例凭据、孤儿进程发现、输出补偿和旧/新控制面 fencing，保留了独立 Supervisor 原本
承担的大部分复杂度。MVP 明确接受 API 退出导致活动 Session 失败，因此不采用。

## 后果

- 运行期 SQLite 只有 API 一个业务进程访问，Session 状态和进程副作用由同一编排边界负责；仍需
  处理 API 内并发，单进程不替代短事务、busy timeout、CAS 或幂等。
- 删除独立 Supervisor 后，生产镜像、健康检查、配置和启动顺序更简单，也不再需要 Supervisor
  重启接管或 API—Supervisor 命令结果表。
- API 成为控制面和 Worker 管理的共同故障域。API 崩溃会中断所有活动 Session；用户可能丢失未
  保存进度，正在被原生 writer 覆盖的存档也不保证有效，但其他 SessionRoot、Game workspace 和
  current content 不受修改。
- API 拥有启动沙箱进程的权限，故其 Worker Manager、launcher 参数、路径和凭据处理必须单独测试
  和审计；Worker 的 namespace/cgroup/seccomp/rlimit 边界不因进程合并而削弱。
- P0-06 的 Supervisor/Worker 测试仍是已完成的进程隔离证据，但其独立 Supervisor 拓扑和无限重连
  语义不再是产品目标。P1-05 已把可复用的启动、UDS、安全和监视实现迁入 API，并删除独立
  Supervisor 项目/入口及相应部署配置。
- 若未来引入多 API、滚动无损升级或多 Worker Host，必须通过新 ADR 重新建立明确的 Worker 所有权
  和外部协调方案，不能直接让多个 API 进程共同管理同一个 SQLite 和 Worker 集合。

## 验证

- 架构测试证明生产解决方案和容器不再启动独立 Supervisor，API 不引用或加载 Emuera Runtime，
  Worker 不引用 EF Core/SQLite。
- 同一 Session 并发启动只产生一个有效 lease 和 Worker；旧 epoch 的注册、心跳、输出与输入结果
  全部被拒绝。
- API 正常停止会在期限内优雅停止 Worker，超时路径会强制终止；相应 Session 变为 `CRASHED` 且
  SessionRoot 保留。
- 强制终止 API 后，所有由该实例创建的 Worker 在断连宽限期内退出或被系统级兜底回收；新 API
  在确认旧 Worker 无写权限后失效租约、释放配额并完成 `CRASHED` 对账。
- 模拟无法确认或终止旧 Worker 时，API readiness 失败，且不能启动替代 Worker或开放存档写操作。
- 浏览器断开和 WebSocket 重连不会终止 API 或 Worker，仍可恢复同一 epoch 的快照和 prompt。
- SQLite 并发测试覆盖 HTTP、后台心跳持久化、关闭和 Worker 退出竞争，验证短事务、CAS 和活动
  配额不会因进程合并而丢失更新。
