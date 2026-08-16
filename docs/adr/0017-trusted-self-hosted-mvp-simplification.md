# ADR-0017：可信参与者自托管 MVP 的简化边界

状态：Accepted

日期：2026-08-12

## 背景

CloudEmuera 的早期需求把单容器 MVP 同时按“玩家自行部署”和“互不信任用户可上传游戏的托管
服务”设计，因此引入了每用户/每 Worker 配额、独立 UID 与 namespace/cgroup/seccomp 沙箱、Worker
断线重注册、无丢失增量恢复、完整运维指标和管理审计等机制。这些机制显著增加实现和部署复杂度，
但项目的实际产品边界是由部署者为自己及其信任的参与者运行的单机自托管实例。

身份、角色和资源授权仍有价值：它们用于区分家庭成员或朋友的资源、阻止普通 API 误操作，并为
远程访问提供登录边界。但同一实例的已认证用户不被视为会刻意利用 Emuera、Worker 或宿主内核漏洞
攻击其他用户的敌对租户。部署者选择并信任要运行的游戏；ZIP、路径、编码和显示内容仍按不安全
数据格式解析，以防损坏文件、路径逃逸和浏览器注入。

## 决定

- 保留现有 User、角色、资源所有权检查、本地身份 bootstrap、关键管理操作审计，以及已经实现的
  持久 HTTP 幂等和 operation recovery。它们不因本 ADR 重写或降级。
- 配额收敛为少量部署级容量上限：活动 Worker 数、上传/展开大小、文件数量、存档文件大小、输出
  Snapshot/队列大小和 DataRoot 最低剩余空间。MVP 不要求每用户或每 Worker CPU、内存、磁盘、
  PID、FD、输出速率计量、预留和调度；创建 Session 总数仍不设产品上限。
- Worker 仍是一 Session 一子进程，用于隔离 Emuera 全局状态并提供可终止的生命周期，但不再是
  恶意代码沙箱。API 与 Worker 可以使用同一非 root 容器 UID；MVP 不要求 NsJail、独立 UID、
  user/mount namespace、seccomp 或每 Worker cgroup，也不因缺少这些能力拒绝 ready。容器不得挂载
  Docker socket、宿主密钥或无关宿主目录，并可由部署者设置容器整体 CPU/内存/PID 限制。
- Worker 只通过应用协议和传入路径操作自己的 SessionRoot；这能防止普通逻辑错误，但同 UID、无
  mount namespace 的部署不宣称具备抵御恶意 Worker 读取其他 DataRoot 内容的内核强制边界。
- 保留持久 WorkerLease、单调 epoch、state version 和同一 Session 单有效 Worker约束，以解决重复
  open、迟到消息和存档写互斥。删除同一 API 实例内 IPC 断线重注册：控制通道断开即停止 Runtime
  并退出。API 启动时按已记录 PID/start identity 或进程组回收明确遗留 Worker，再把 lease 对账为
  `CRASHED`；无法确认的异常只阻止对应 Session 的 open/存档写，不要求整个实例因缺少强沙箱能力
  拒绝 ready。
- 实时重连总是取得一个带 `(workerEpoch, snapshotSequence)` 的完整有界 ConsoleSnapshot，然后
  接收新的实时批次；不实现基于客户端 ack 的历史增量补发、snapshot/subscribe 无丢失屏障或持久
  输入去重日志。`sequence`、`promptId` 和有界内存中的 `clientMessageId` 去重仍保留；旧 prompt
  或旧 epoch 的输入必须拒绝。
- Game MVP 保留 ZIP 上传、路径/链接/压缩炸弹检查、编码检测、真实 parser-only 验证、manifest/
  digest、原子启用和 SessionRoot 私有复制；不提供浏览器 ERB/CSV 编辑、搜索、创建/删除文件等
  编辑器能力。已实现的 workspace/current 内部模型可继续作为安全摄取与启用边界。
- 存档 MVP 保留停止态的列出、下载、上传/替换、重命名和删除，以及用户/Session 授权、路径/大小
  校验和活动 Worker 互斥；不提供跨 Session 存档复制、版本历史、签名下载 URL或内容级兼容证明。
  下载统一通过授权 API 代理。
- 运维 MVP 保留关联日志、敏感字段过滤、live/ready/version、当前 Worker/Session 基本状态和管理员
  强制停止。现有关键审计继续写入，但不实现通用审计查询 UI、OpenTelemetry/Prometheus、每 Worker
  资源指标、容量规划或安全例外策略。
- 已实现的 Migrator、SQLite 备份前置检查、API-owned Worker Manager、持久幂等和恢复流程继续
  使用。后续生产部署只补足非 root 容器、PID 1 信号/子进程回收、数据卷说明和停机备份恢复；不
  新增完整 s6 服务树、在线备份编排、滚动升级或沙箱 readiness 门。

## 后果

- MVP 的实现、测试矩阵和目标宿主要求显著缩小，同时保留浏览器远程使用、多用户资源区分、游戏
  包安全解析、原生存档和持久 SessionRoot 的核心价值。
- 同一实例不能安全地作为敌对租户托管平台宣传或开放。若部署者允许不信任用户上传并运行游戏，
  他们可能利用 Runtime 或文件访问漏洞越过应用级资源授权；反向代理认证不能弥补该执行面边界。
- ZIP 路径安全、解压上限、结构化显示允许列表、Cookie/CSRF/实时会话授权、全局有界缓存、SessionRoot
  独立副本和停止态存档互斥仍是强制要求，因为它们也防御损坏输入、浏览器攻击和普通故障。
- 已实现机制优先保留，避免为了减少概念数量立即进行高风险反向迁移。删除已实现代码必须按独立
  执行文档逐步完成，并在每步保持 schema 前滚兼容和现有用户数据可升级。

## 备选方案

### 继续按敌对多租户服务实现

安全保证最强，但必须完成独立 UID/mount namespace、seccomp、每 Worker cgroup、资源计量和更大
的故障注入矩阵，与个人自托管产品的成本收益不匹配。

### 删除身份与授权并限定单用户

实现更少，但远程访问仍需要认证，且部署者可能希望为可信家庭成员或朋友区分资源，因此不采用。

### 把 Runtime 合并进 API

进程更少，但 Emuera 全局状态、未处理异常和无法安全终止的 .NET 执行线程会影响控制面。保留子
进程的工程收益高于其有限管理成本，因此不采用。

## 验证

- 中英文需求使用相同编号表达新的边界，详细设计和开发计划不再要求独立 UID、NsJail、每 Worker
  资源配额、IPC 重注册、增量补发或管理指标平台。
- 两个授权用户的普通 API 资源隔离测试继续通过；文档明确该测试不证明恶意 Worker 内核隔离。
- ZIP 恶意语料、结构化显示、SessionRoot 独立副本、同一 Session 单 Worker、持久 HTTP 幂等和
  停止态存档互斥测试继续作为发布门。
- Worker 控制通道断开后在有界时间退出；API 重启能回收明确归属的旧 Worker并把对应 Session
  对账为 `CRASHED`，随后复用原 SessionRoot 重新开启。
- 重连总能取得一致的完整快照；持续输出和慢客户端不会导致 Worker、API 或浏览器队列无界增长。
