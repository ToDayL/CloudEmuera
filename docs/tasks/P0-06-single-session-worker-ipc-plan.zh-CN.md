# P0-06 单 Session Worker 与 IPC 冒烟链路详细实现计划

状态：DONE

计划日期：2026-08-05

对应开发步骤：`P0-06 — 单 Session Worker 与 IPC 冒烟链路`

前置条件：P0-01～P0-05 已完成

后续步骤：P1-01 SQLite 首版 schema 与迁移；P1-05 Supervisor、租约、epoch 与状态机

需求映射：SESS-002/003/004/010、PLAY-001/004/005/007/008/010/012、OPS-004、
SEC-007、NFR-002/003/008/011/013、Phase 0 进程隔离

## 1. 任务结论

P0-06 要把已经验证过的 headless Emuera 从兼容性测试进程移入真正的
`CloudEmuera.Worker` 操作系统进程，并通过 Supervisor 持有的 Unix domain socket
完成一次完整的注册、版本握手、启动、输出、INPUT、输入结果和关闭往返。

本步骤完成后，仓库应能证明：

- 一个 Worker 进程在其整个生命周期内只绑定一个 `sessionId + workerId + workerEpoch`
  和一个实际 SessionRoot；
- Worker 使用 P0-05 已物化的 SessionRoot，不自行复制 GameVersion，也不创建第二套运行目录；
- Supervisor 与 Worker 使用版本化 Protobuf 消息和 gRPC 双向流，而不是标准输入输出、
  临时 JSON 文件或进程内回调传递运行时事件；
- 固定上游解释器在独立进程中到达 INPUT，Supervisor 能收到结构化输出并提交一次
  `promptId + clientMessageId` 输入；
- 控制客户端或 API 占位进程退出不会连带终止 Supervisor/Worker；
- Supervisor 请求关闭后，Worker 停止接收新输入、取消 Runtime、报告终态并在期限内退出；
- 第二个 Session 或不同 epoch 不能复用同一 Worker。

P0-06 是进程和协议的最小证明，不是完整调度系统。P1-05 才实现数据库中的
WorkerLease、epoch 分配/续租、心跳过期状态转换和进程对账；P1-12 才实现完整 Worker
沙箱和资源限制。

## 2. 范围

### 2.1 本步骤必须实现

1. 重构 `worker.proto` 为可承载 P0-06 往返的 IPC v1 契约；
2. 建立协议常量、消息校验、错误原因码、大小上限和未知字段兼容测试；
3. 在 Supervisor 中建立仅监听 UDS 的 gRPC server，并安全创建/回收 socket；
4. 定义 Worker 启动描述文件，安全传入固定身份、SessionRoot、profile、UDS 和启动令牌；
5. 由 Supervisor 启动真正的 `CloudEmuera.Worker` 子进程；
6. Worker 连接 Supervisor、注册并完成 IPC/Runtime 版本握手；
7. Worker 创建 `StructuredGameConsole`、`LocalRuntimeFileSystem` 和
   `EmueraRuntimeHost`，运行 P0-05 的实际 SessionRoot；
8. 将结构化 Console 增量和 prompt 通过有界异步发送泵发给 Supervisor；
9. 将 Supervisor 的输入命令提交到 `StructuredGameConsole.SubmitInput`，并回传确定结果；
10. 实现心跳、Runtime ready/completed/failed、优雅停止和进程退出码；
11. 强制一个 Worker 只能启动一个 Session，重复命令可幂等返回，身份不一致则拒绝；
12. 新增 IPC 契约测试和真实多进程集成测试，并保持 P0-01～P0-05 回归通过；
13. 更新 IPC/进程架构文档、运行命令和需求—测试映射。

### 2.2 明确非目标

- 不新增 SQLite 表或 migration；
- 不实现持久 WorkerLease、epoch 分配、续租、过期判定或 Session 状态落库；
- 不实现 API↔Supervisor 的正式业务 IPC；测试控制器可以直接驱动 P0 Supervisor harness；
- 不实现浏览器 WebSocket、断线恢复 API 或多客户端仲裁；
- 不实现多 Session 调度、活动配额或 Worker 池；
- 不实现 Supervisor 崩溃后的进程收养与数据库对账；
- 不实现 namespace、cgroup、seccomp、网络 namespace 或 CPU/内存限额；
- 不提供 TCP fallback，也不把 UDS 映射为容器端口或放进宿主共享目录；
- 不经 IPC 复制、提交或解析存档；Worker 继续直接读写自己的 SessionRoot；
- 不修改 Emuera 原生存档格式，也不以 Worker 退出作为“存档提交”；
- 不修改固定上游解释器源码，除非出现无法由 Worker/headless glue 解决的阻断问题；
- 不承诺 P0-06 的内存令牌/连接状态在 Supervisor 重启后恢复，P1-05 结合持久租约实现。

## 3. 进程拓扑与所有权

```text
测试控制器 / future API
          │  P0 测试控制面（不进入 Worker）
          ▼
CloudEmuera.Supervisor process
  ├── 创建受限 UDS listener
  ├── 持有启动令牌与唯一 WorkerBinding
  ├── 启动/监视 Worker 子进程
  └── gRPC WorkerControl.Connect 双向流 server
                  ▲
                  │ UDS；Worker 主动连接并注册
                  │
CloudEmuera.Worker process
  ├── 不可变 WorkerBinding
  ├── IPC receive/send pumps
  ├── StructuredGameConsole + 有界 ConsoleStateStore
  ├── EmueraRuntimeHost
  └── SessionRoot（P0-05 已物化，唯一可写者）
```

关键所有权：

- Supervisor 是 UDS listener、启动令牌和子进程句柄的所有者；
- Worker 是 Runtime、ConsoleSnapshot、输入协调器和运行期 SessionRoot 写权限的所有者；
- Worker 主动连接 Supervisor；不要为每个 Worker 再开放一个监听 socket；
- Worker 创建后身份不可变，不提供“切换 Session”方法；
- Worker 不接受 GameVersionRoot。启动前 SessionRoot 已由上层依据 P0-05 创建并绑定；
- API 不是 Worker 的父进程，也不持有 Runtime。API/测试控制客户端断开不得向 Worker
  传播进程 lifetime cancellation；
- Supervisor 停止和整个容器 SIGTERM 可以触发有界的 Worker 集体关闭。

依赖方向保持：

```text
RuntimeAdapter ← EmueraRuntime ← Worker
                  Ipc ← Worker
                  Ipc ← Supervisor
```

`CloudEmuera.Ipc` 只能放传输 DTO、生成代码、协议常量和纯校验，不引用 Domain、Worker、
Supervisor 或 EmueraRuntime。生成的 Protobuf 类型不能直接成为领域实体。

## 4. IPC v1 契约

### 4.1 服务方向

保留单个双向流服务：

```proto
service WorkerControl {
  rpc Connect(stream WorkerEnvelope) returns (stream SupervisorEnvelope);
}
```

Supervisor 是 server，Worker 是 client。每个 gRPC stream 只代表一个 Worker 连接。
不要在同一 stream 上复用多个 Worker，也不要依靠 metadata 之外的远程 TCP 地址识别对端。

### 4.2 Envelope 公共字段

两个方向的 envelope 至少包含：

```text
protocol_version
message_id
correlation_id（响应时必填；主动事件可空）
session_id
worker_id
worker_epoch
payload oneof
```

约束：

- `protocol_version` 当前固定为 `1`；缺失、`0` 或非 `1` 在注册阶段以
  `unsupported_protocol_version` 拒绝；
- ID 使用非空、长度有上限的 ASCII 安全字符串；测试可使用固定 ID，但产品调用方沿用
  `sess_`、`wrk_` 等类型前缀；
- `worker_epoch` 必须大于 `0`；P0 harness 显式分配测试值，不能由 Worker 猜测；
- 每条主动消息有新的 `message_id`；响应的 `correlation_id` 等于原命令的
  `message_id`；
- 未注册连接只能发送 registration；注册后 envelope 身份必须逐字段等于绑定值；
- oneof 为空或未知 payload 返回/记录 `unsupported_message`，不能导致 server 崩溃；
- 字段号发布后不得复用；移除字段必须加入 `reserved`；enum 的 `0` 值必须为
  `UNSPECIFIED`；
- P0-06 不使用 Protobuf `Any`、JSON 字符串 payload 或任意类型名反射。

### 4.3 Worker → Supervisor 消息

建议的最小 payload：

| 消息 | 关键字段 | 语义 |
| --- | --- | --- |
| `WorkerRegistration` | bootstrap token、protocol/runtime 版本、pid | stream 第一条消息 |
| `WorkerReady` | integration version、upstream commit、save layout、last sequence | Runtime 初始化成功 |
| `WorkerHeartbeat` | monotonic timestamp、last sequence、waiting、resident bytes | 活性/进度信号 |
| `DisplayBatch` | first/last sequence、结构化 operations | 非阻塞批量输出 |
| `InputResult` | command/client/prompt ID、result kind、reason | 输入的确定处理结果 |
| `RuntimeCompleted` | Runtime status、last sequence | ERB 正常 QUIT/结束 |
| `RuntimeFailed` | stable code、phase、safe message、fatal | 初始化或执行失败 |
| `WorkerStopped` | reason、last sequence、graceful | stop 的最终确认 |

`DisplayBatch` 必须使用显式 Protobuf schema 表达 P0 fixture 所需的文字、样式、换行、
图片和 prompt 操作，不能发送原始 HTML。字段映射必须与 RuntimeAdapter 类型逐项测试。
图片只发送受控 `ConsoleAssetId` 和布局元数据，不发送任意宿主路径或文件字节。

`RuntimeFailed.safe_message` 必须沿用 `EmueraRuntimeHost` 的路径脱敏原则。异常堆栈只进入
受控诊断日志，不进入协议响应；日志也不能出现启动令牌或默认记录玩家输入全文。

### 4.4 Supervisor → Worker 消息

建议的最小 payload：

| 消息 | 关键字段 | 语义 |
| --- | --- | --- |
| `RegistrationResult` | accepted、reason、negotiated version | 注册握手结果 |
| `StartRuntime` | command ID/deadline、expected binding/profile | 初始化并启动唯一 Runtime |
| `SubmitInput` | prompt ID、clientMessageId、typed value、deadline | 提交玩家输入 |
| `StopWorker` | deadline、reason code | 停止新输入并优雅终止 |

`StartRuntime` 不传任意 GameVersionRoot，也不覆盖启动描述中的 SessionRoot。它只确认已绑定
值并启动；任何不一致返回 `binding_mismatch` 后退出。第一次成功启动后：

- 相同 `message_id` 的重试返回缓存结果；
- 新 ID 但相同绑定的第二次启动返回 `already_started`；
- 不同 Session、Worker 或 epoch 返回 `binding_mismatch` 并关闭连接；
- Runtime 完成后不能在同一进程启动另一个 Session。

`SubmitInput` 映射现有 `ConsoleInputCommand`。协议结果至少区分 accepted、duplicate、
conflict、stale prompt、no active prompt、invalid command 和 invalid format。不要把所有失败
压成布尔值。

### 4.5 版本握手

注册同时检查三个不同概念：

1. IPC protocol version：当前为 `1`；
2. `RuntimeBaseline.CloudEmueraIntegrationVersion`；
3. `RuntimeBaseline.UpstreamCommit`。

协议不匹配必须在启动 Runtime 前拒绝。Runtime baseline 不匹配使用独立的
`runtime_version_mismatch`，便于未来允许滚动升级时扩展策略。reason code 是稳定机器值，
人类消息不参与程序分支。

未知可选字段必须能被旧端解析并忽略；未知协议版本不能仅靠“恰好能解析”而接受。
契约测试应把一个手工追加未知 field 的 v1 消息 parse→serialize，并验证已知字段不变、
未知字段仍被 Protobuf 保留。

### 4.6 大小、背压和优先级

- 为单 envelope、单 DisplayBatch、单字符串和 batch operation 数设置显式上限；
- gRPC receive/send limit 与应用层 limit 同时配置，应用层限制更小；
- Runtime 线程只写 `StructuredGameConsole`，不得等待 gRPC `WriteAsync`；
- Worker 使用有界通知 channel 唤醒发送泵，ConsoleStateStore 仍是输出事实源；
- 通知可合并，发送泵按 sequence 从 StateStore 读取，不能因通知丢弃而丢失状态；
- 控制响应、stop 和 heartbeat 使用独立高优先级队列，不能排在大量 display 后面；
- 写 stream 必须只有一个串行 writer；多个任务不能并发调用 gRPC response writer；
- 若 display 增量已被有界历史压缩，P0 可以发送带基准 sequence 的 snapshot 消息，或明确
  终止为 `output_resume_gap`；优先实现 snapshot，以复用 P0-03 能力；
- 达到队列上限不能无限分配内存，也不能阻塞解释器。

## 5. Worker 启动配置与凭据

### 5.1 启动描述文件

Supervisor 在自己的受限 runtime 目录中为每个 Worker 创建一次性 JSON 描述文件，并仅把
描述文件路径作为 `--bootstrap-file` 参数传给 Worker。内容至少包括：

```text
schemaVersion
protocolVersion
sessionId
workerId
workerEpoch
sessionRoot
compatibilityProfile
supervisorSocketPath
bootstrapToken
connectDeadline
heartbeatInterval
shutdownGracePeriod
```

要求：

- 父目录仅服务账户可进入，目录 mode `0700`，文件 mode `0600`；
- 文件必须是同一服务账户拥有的普通文件，拒绝 symlink、hardlink count 异常和特殊文件；
- Worker 打开文件后立刻读取、严格校验并清除内存外的临时字节；
- Supervisor 在注册成功或启动失败后删除描述文件；不得依赖 Worker 有删除父目录权限；
- token 使用 CSPRNG，至少 256 bit，经 base64url 表达；不得出现在 argv、环境变量或日志；
- Supervisor 使用固定时间比较 token；一个 token 只能绑定一个身份；
- P0 允许同一存活 Worker 在 stream 短断后用内存中的 token 重新注册，进程退出后令牌失效；
- 描述文件 schema 版本与 IPC 版本分别校验，不能混为一项。

如果现有部署对 Unix mode/owner 的跨平台单元测试不稳定，可把严格物理检查放入 Linux
集成测试；产品代码仍必须 fail closed。P0-06 的运行目标是 Linux 容器，不要求 Windows
named pipe fallback。

### 5.2 SessionRoot 验证

Worker 启动时只消费已经存在的 P0-05 SessionRoot：

- 规范化路径，拒绝根目录、相对路径、symlink/reparse point 和与 UDS/runtime 目录重叠；
- 读取 P0-05 binding metadata，确认 manifest identity 与请求绑定一致；
- 通过 `EmueraSaveLayoutInspector` 和 Runtime 二次检查确认保存布局；
- 不调用 `SessionRootLayoutBuilder.Build`，不从 GameVersion 再复制或修补文件；
- 将进程工作目录设为 SessionRoot，并构造现有 `RuntimePaths`/`LocalRuntimeFileSystem`；
- Worker 退出时保留 SessionRoot 全部内容，只清理自己拥有的 socket/client 临时状态。

P0 harness 可以在启动 Worker 前使用生产 builder 从 fixture 构造 SessionRoot，但这个动作
属于 Supervisor/测试准备阶段，不属于 Worker。

## 6. Supervisor 实现分解

建议不要继续把全部逻辑放在顶层 `Program.cs`。拆分为可测试组件：

```text
SupervisorOptions / UdsEndpointOptions
UnixSocketLifecycle
WorkerBootstrapStore
WorkerProcessLauncher
WorkerBindingRegistry（P0 仅内存、单 worker）
WorkerControlGrpcService
WorkerConnection / WorkerMessageRouter
WorkerProcessMonitor
```

### 6.1 UDS 生命周期

1. socket 放在专用 runtime 目录，不放在 SessionRoot、GameVersionRoot 或公开 bind mount；
2. 创建父目录并验证 owner/mode/所有父段不是链接；
3. 若 endpoint 已存在，只能在确认它是本服务目录内的 Unix socket 且没有活跃 listener 后
   清理；普通文件、目录或链接一律 fail closed；
4. Kestrel 只 `ListenUnixSocket`，不额外监听 TCP；
5. bind 后把 socket mode 收紧到 `0600`，并在接受注册前复核；
6. 正常退出只删除自己创建且 identity 未变化的 socket；不得递归删除宽泛目录。

### 6.2 注册与消息路由

- stream 第一条消息设置短 deadline；未注册连接不能占用资源无限等待；
- 校验 token、版本和完整 binding 后才把连接放入 registry；
- 同一 binding 重连时，新 stream 原子替代旧 stream，旧 stream 不再接收命令；
- 不同 binding 使用同一 token 或同一 workerId 必须拒绝；
- 所有 Worker 事件先做 envelope 和 payload validation，再更新 P0 内存视图；
- display sequence 必须连续且严格递增；重复已确认 batch 可幂等忽略，gap/倒退产生稳定错误；
- Supervisor 断开不会立即 kill Worker；P0 Worker 按有界退避重连并继续保存 ConsoleSnapshot；
- 超过 P0 配置的重连窗口后可以让 smoke test 明确失败，但不能无界增长重连任务。

### 6.3 子进程管理

- 使用显式 Worker executable/dotnet entry assembly 和参数数组，不经过 shell；
- `UseShellExecute=false`，重定向标准输出/错误仅用于受控日志，不用作 IPC；
- 记录 pid、开始时间和退出码，并关联 session/worker/epoch；
- 不把 API 请求 cancellation 直接传给子进程 lifetime；
- Supervisor 正常关闭时先发 StopWorker，等待 grace period，再升级为进程终止；强制 kill 的
  完整策略留给 P1，但 P0 测试必须有超时，不能挂死测试进程；
- 集成测试的 finally 只能终止由该测试明确启动且 pid/start identity 匹配的进程。

## 7. Worker 实现分解

建议组件：

```text
WorkerBootstrapLoader
ImmutableWorkerBinding
SupervisorUdsChannelFactory
WorkerConnectionLoop
WorkerCommandDispatcher
WorkerRuntimeController
ConsoleOutputPump
WorkerHeartbeatService
WorkerExitCode
```

### 7.1 Host 启动顺序

1. 解析唯一 `--bootstrap-file`，未知参数或重复参数失败；
2. 安全读取并校验启动描述；
3. 建立不可变 binding，设置日志 scope；
4. 通过 `SocketsHttpHandler.ConnectCallback` 连接唯一 UDS，不启用 TCP fallback；
5. 发送 registration，等待 accepted；
6. 收到 `StartRuntime` 后验证 binding/deadline/command receipt；
7. 创建 `StructuredGameConsole`、文件/时钟/媒体端口和 `EmueraRuntimeHost`；
8. 初始化完成后发送 `WorkerReady`，再启动 Runtime、output pump 和 heartbeat；
9. 收到 input 时在 IPC 线程调用线程安全的 `SubmitInput`，不占用解释器线程；
10. Runtime 完成/失败后发送终态，但继续到受控连接收尾，不自动复用进程；
11. stop/SIGTERM 取消 lifetime token，等待 Runtime 和发送泵在 grace period 内结束；
12. dispose host/channel，保留 SessionRoot，返回稳定退出码。

### 7.2 单 Session 强制约束

`WorkerRuntimeController` 使用原子状态机：

```text
Created → Registered → Initializing → Running/WaitingForInput
                                     → Completed/Failed
任意非终态 → Stopping → Stopped
```

只有 `Registered` 能进入一次 `Initializing`。binding 在构造后只读；不存在 Reset、
Rebind 或第二个 Runtime slot。并发两个 start 命令最多一个进入初始化，另一个得到缓存结果
或 `already_started`。不同 binding 的 envelope 在到达 dispatcher 前即被拒绝。

### 7.3 输出通知接线

P0-03 的 ConsoleStateStore 是权威状态。可在 RuntimeAdapter 增加窄的、非阻塞的
“state advanced”通知，或在 Worker 中使用有界轮询；优先采用通知：

- 通知只携带“最新 sequence 已变化”，不复制完整节点；
- `Emit` 在持锁更新状态后 `TryWrite`，channel 满时合并通知；
- output pump 读取 `ReadSince(lastSent)`，映射为 Protobuf batch；
- 发送成功后才推进 `lastSent`；连接断开时不推进确认位置；
- snapshot 替代历史时从 snapshot base sequence 继续，不能制造 sequence 连续的假象；
- Protobuf 映射层放在 Worker/Ipc adapter，不让 RuntimeAdapter 引用 Protobuf。

### 7.4 心跳与重连

- heartbeat interval 使用配置值并设合理上下限，测试可缩短；
- 心跳读取当前 sequence、prompt 状态和进程 working set，不扫描 SessionRoot；
- UDS stream 暂断不取消 Runtime，也不清空 ConsoleStateStore 或当前 prompt；
- 重连使用指数退避加抖动并受上限约束；测试注入 deterministic delay/random；
- 重新注册时携带同一 binding 和 last sequence；Supervisor 重新确认 epoch 后继续；
- P0 至少测试一次短断重连后 prompt 仍可输入；持久租约和 Supervisor 重启恢复留给 P1-05。

## 8. 关闭、失败与退出码

### 8.1 优雅关闭

处理 `StopWorker` 时顺序固定：

1. 校验 binding、command ID 和 deadline；
2. 原子转入 Stopping；
3. 后续新 input 返回 `worker_stopping`；
4. 取消 Runtime lifetime token，使等待中的 INPUT 被唤醒；
5. 等待 Runtime/Console pump 完成，发送可发送的最后结构化状态；
6. dispose `EmueraRuntimeHost`，不删除或重建 SessionRoot；
7. 发送 `WorkerStopped`，完成 gRPC stream；
8. 进程以成功的 stopped exit code 退出。

如果 stop deadline 已过且命令从未执行，返回 `deadline_exceeded`；如果已开始 stop，重复命令
返回同一最终结果。SIGTERM 使用相同关闭路径，但不能假定 Supervisor stream 仍可写。

### 8.2 稳定失败分类

至少定义：

```text
0   normal completion / graceful stop
10  bootstrap invalid
11  IPC registration/version rejected
12  SessionRoot binding invalid
13  Runtime initialization failed
14  Runtime execution failed
15  shutdown deadline exceeded
```

具体数字可在实现时调整一次，但发布后集中定义并测试，不能散落 magic number。协议 reason
code 比 OS exit code 更细；Supervisor 同时记录两者。异常退出不得删除 SessionRoot。

## 9. 测试计划

### 9.1 新测试项目

新增并加入 `CloudEmuera.slnx`：

```text
tests/CloudEmuera.Ipc.ContractTests/
tests/CloudEmuera.Worker.IntegrationTests/
```

两个项目都使用锁定依赖。测试 helper 不得被产品程序集引用。

### 9.2 IPC 契约测试

类别：`Category=IpcContract`

至少覆盖：

1. 每种 v1 payload 的 serialize/parse roundtrip；
2. envelope 缺失 protocol/version/ID/binding/payload 的拒绝；
3. protocol `0`、未来版本和 runtime baseline 不匹配的稳定 reason code；
4. 手工追加未知字段后的 parse→serialize 前向兼容；
5. oneof 未知 payload 不导致进程异常；
6. enum `UNSPECIFIED` 被 validator 拒绝；
7. 超长 ID、字符串、过大 batch 和 operation 数量被应用层拒绝；
8. ConsoleOperation ↔ Protobuf 的文字、样式、换行、图片、prompt 映射；
9. 非允许 HTML/任意路径不会因映射重新出现；
10. InputResult 各 kind 的无损映射；
11. correlation ID 与命令 ID 规则；
12. 字段号/保留字段和固定 golden bytes，防止无意破坏 wire contract。

不要断言整个生成 C# 类的文本；断言 wire 行为和公开 validator 行为。

### 9.3 Worker 单元/组件测试

可放在 Worker Integration 项目中以 in-process 组件方式执行：

- bootstrap 文件合法读取、mode/owner/link/重复键/未知版本拒绝；
- token 比较和日志脱敏；
- controller 并发 start 只有一次成功；
- 相同 command ID 返回缓存结果，不同 binding 失败；
- stop 与 input 竞争时最多一个输入被执行，进入 Stopping 后全部拒绝；
- output 通知合并不丢 sequence，batch 受上限约束；
- display 队列饱和时 heartbeat/stop 仍能被发送；
- channel 短断不会取消 Runtime 或清空当前 prompt；
- cancellation/dispose 幂等，无未观察 Task exception。

### 9.4 真实多进程集成测试

类别：`Category=ProcessIsolation`，仅在 Linux dev container 运行。每项使用独立临时目录和
UDS，显式记录启动的 pid，并有总超时。

#### 场景 A：完整 input roundtrip

对 v18 和 EM+EE 两个 fixture 参数化执行：

1. 校验 fixture 并通过 P0-05 builder 创建全新 SessionRoot；
2. 启动真实 Supervisor UDS server；
3. Supervisor 启动真实 Worker 进程；
4. 验证 registration 和三类版本完全匹配；
5. 发送 StartRuntime，等待 WorkerReady；
6. 收集 display batch，断言到达 fixture 的 INPUT 且 sequence 连续；
7. 使用实际 promptId 提交 fixture input；
8. 断言 InputResult=accepted、最终 transcript 与 P0-04 基线一致；
9. 断言 Worker 正常结束，GameVersion 未改变，SessionRoot 保留；
10. 断言 Worker pid 与测试/Supervisor pid 不同，且 Worker 工作目录为 SessionRoot。

#### 场景 B：单 Worker 绑定拒绝

- 向已启动 Worker 发送第二个 start；同 binding 返回 `already_started`；
- 伪造另一个 sessionId、workerId 或 epoch 的 envelope，得到 `binding_mismatch`；
- 验证没有创建第二个 Runtime、第二个 prompt 或第二套 SessionRoot 写入。

#### 场景 C：API/控制客户端独立

- 由 Supervisor 启动等待 INPUT 的 Worker；
- 启动后退出/终止一个独立 API 占位进程或测试控制客户端；
- 验证 Supervisor 与 Worker pid 仍存活、prompt 和 sequence 保持；
- 重新连接测试控制端并成功提交输入。

这项证明进程拓扑，不宣称已实现 P1 的 API 恢复发现协议。

#### 场景 D：IPC 短断重连

- Worker 等待 INPUT 时主动中断当前 gRPC stream，但保留 Supervisor listener；
- 验证 Worker 不退出且不丢失 prompt；
- Worker 使用同一 binding 重新注册；
- Supervisor 从上次 sequence 恢复，提交输入并完成场景。

#### 场景 E：关闭等待输入的 Worker

- Runtime 停在无期限 INPUT；
- 发送 StopWorker；
- 验证新输入被拒绝、Runtime cancellation 完成、WorkerStopped 到达；
- Worker 在例如 5 秒测试期限内退出；
- SessionRoot 和既有存档/未知合法文件仍存在，bootstrap 文件及 UDS 被安全清理。

#### 场景 F：并行进程隔离

- 使用两个独立 SessionRoot 启动两个 Worker；
- 两者并行到达 INPUT 并接受不同输入；
- 验证各自 sequence、prompt、输出和文件互不串线；
- 此场景同时证明 Emuera 的 static gate 只在单进程内串行，不会阻止独立进程并发运行。

### 9.5 UDS 安全测试

Linux 集成测试至少覆盖：

- listener 确实是 Unix socket、mode 为 `0600`、父目录为 `0700`；
- 没有 TCP listener；
- 未持有正确 token 的同 UID client 注册失败；
- socket 路径预先存在普通文件、目录或 symlink 时 fail closed 且原条目未被删除；
- stale socket 只在验证类型、父目录和 ownership 后清理；
- bootstrap 是 symlink、hardlink 或权限过宽时 Worker 拒绝；
- token/sessionRoot/用户输入不出现在 stdout、stderr 和结构化日志中。

### 9.6 回归测试

必须继续通过：

```bash
./scripts/test-runtime-compat.sh --scenario input-roundtrip
./scripts/test-runtime-compat.sh --scenario save-root
./scripts/test-runtime-compat.sh --scenario save-directory
```

P0-04/P0-05 的 in-process harness 仍是解释器兼容性定位工具；P0-06 新测试证明跨进程和 IPC，
不应用较慢的 Worker 集成测试替换全部细粒度 Runtime 测试。

## 10. 建议实施顺序

### 阶段 1：冻结 IPC v1

1. 盘点 RuntimeAdapter 的 Console/Input 类型，列出逐字段映射；
2. 修改 `worker.proto`，集中添加协议常量和 validator；
3. 建立 `CloudEmuera.Ipc.ContractTests`；
4. 先让序列化、未知字段、版本和 limit 测试通过。

完成门：契约测试不启动 Worker，能独立证明 wire contract。

### 阶段 2：UDS 与注册握手

1. 实现 Supervisor UDS lifecycle 和 gRPC service；
2. 实现 bootstrap store/loader 和 Worker UDS channel；
3. 完成注册、版本/令牌/binding 校验；
4. 添加 UDS 权限、恶意现有路径和错误版本测试。

完成门：真实 Worker 进程能注册，错误凭据/版本在 Runtime 初始化前被拒绝。

### 阶段 3：Runtime controller

1. Worker 引用 `CloudEmuera.EmueraRuntime`；
2. 从已有 SessionRoot 构造 ports/options；
3. 实现一次性 start 状态机和 Runtime 终态映射；
4. 添加并发 start、错误 SessionRoot 和初始化失败测试。

完成门：真实 Worker 能运行 fixture 到 INPUT，但暂不要求远程输入。

### 阶段 4：输出与输入

1. 实现 Console 状态推进通知和单 writer 输出泵；
2. 实现 DisplayBatch/必要 Snapshot 映射；
3. 实现 SubmitInput/InputResult；
4. 跑通 v18 和 EM+EE 跨进程 input roundtrip。

完成门：输出 transcript 与 P0-04 基线一致，输入去重/冲突语义保持。

### 阶段 5：心跳、重连与关闭

1. 实现 heartbeat、高优先级控制队列和短断重连；
2. 实现 stop/SIGTERM 共用的取消路径；
3. 实现 Supervisor process monitor 和有界收尾；
4. 完成 API 独立、重连、关闭及两个 Worker 并行测试。

完成门：所有 `ProcessIsolation` 测试可重复通过且没有残留进程/socket。

### 阶段 6：文档与全量质量门

1. 更新设计文档的实际 proto 消息、启动配置和 P0/P1 边界；
2. 更新 README/AGENTS 的可执行验证命令；
3. 如修改 `Upstream/`，同步 `MODIFICATIONS.md`、integration version 和第三方验证；
4. 运行全量检查并记录环境、测试数和结果；
5. 将本文件状态改为 DONE，并把开发计划的 P0-06 改为 DONE、下一步改为 P1-01。

## 11. 验证命令

按仓库约束，所有 .NET 命令通过 dev Docker 执行。定向验证：

```bash
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Ipc.ContractTests \
  --configuration Release

source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Worker.IntegrationTests \
  --configuration Release --filter 'Category=ProcessIsolation'
```

回归与最终质量门：

```bash
./scripts/test-runtime-compat.sh --scenario input-roundtrip
./scripts/test-runtime-compat.sh --scenario save-root
./scripts/test-runtime-compat.sh --scenario save-directory
./scripts/check.sh
./scripts/verify-third-party.sh
./scripts/verify-dev-user.sh
git diff --check
```

如果为 Worker 集成测试增加单独脚本，例如 `scripts/test-worker-ipc.sh`，脚本必须由
`check.sh` 或 CI 明确调用，失败返回非零退出码，并确保退出时回收自己启动的测试进程。

## 12. 完成定义

只有同时满足以下条件，P0-06 才能标为 DONE：

1. `worker.proto` 覆盖注册、版本握手、启动、结构化输出、输入结果、心跳和停止；
2. IPC v1 有协议常量、严格 validator、未知字段兼容和错误版本拒绝测试；
3. Supervisor 只通过权限受限的 UDS 接受 Worker，不存在 TCP fallback；
4. bootstrap token 不出现在 argv、环境变量或日志，错误 token 在 Runtime 启动前失败；
5. v18 与 EM+EE fixture 都由真实独立 Worker 进程完成 input roundtrip；
6. Worker 直接使用 P0-05 SessionRoot，GameVersion 未变，退出后 SessionRoot 保留；
7. 同一 Worker 无法启动第二个 Session，身份或 epoch 不匹配被明确拒绝；
8. IPC 短断和控制客户端/API 退出不会立即终止 Runtime，当前 prompt 可继续；
9. 等待 INPUT 的 Worker 收到 stop 后在限定时间退出，且新输入不再执行；
10. 两个 Worker 可用两个 SessionRoot 并行运行且输出/输入/文件状态隔离；
11. 输出发送具有有界内存、单调 sequence 和控制消息优先级，不阻塞解释器线程；
12. 日志带 `sessionId/workerId/workerEpoch`，且不泄漏 token、宿主敏感路径或默认输入全文；
13. 新测试项目已加入解决方案并使用 locked packages；
14. P0-01～P0-05 定向回归、`check.sh`、第三方验证、开发用户验证和 diff check 全部通过；
15. 文档记录实际协议版本、Runtime baseline、执行环境、测试数量和已知限制。

## 13. 实现交接检查表

- [x] 先提交/保留当前 P0-05 已完成状态，避免覆盖工作区中的实现修改
- [x] 冻结 proto 字段号、enum 0 值、reserved 和大小限制
- [x] 新建并加入两个测试项目及 lock file
- [x] 建立安全 UDS lifecycle，不开放 TCP
- [x] 建立 bootstrap 文件和令牌验证，不经 argv/env 泄密
- [x] Worker 增加 EmueraRuntime 引用并消费既有 SessionRoot
- [x] 实现不可变 binding 和一次性 Runtime controller
- [x] 实现单 writer、有界、可恢复的结构化输出泵
- [x] 实现 SubmitInput 全结果映射与 command receipt
- [x] 实现 heartbeat、短断重连和 stop/SIGTERM 收尾
- [x] 完成六类真实多进程场景与 UDS 恶意路径测试
- [x] 运行 P0-04/P0-05 回归及全量质量门
- [x] 更新设计/计划/README/AGENTS 和实际完成记录

## 14. 实际实现与验证记录

2026-08-05 在 Linux dev Docker（.NET SDK 10）中完成本步骤。实现包含：版本化
`worker.proto` 与严格 validator；仅 UDS 的 Supervisor gRPC server；`0700` bootstrap
目录、`0600` bootstrap/UDS 文件、256-bit CSPRNG token、普通文件/owner/hardlink 校验；
显式 Worker 子进程启动和 SessionRoot 工作目录；不可变 binding、一次性 Runtime controller、
单 writer 有界控制/显示队列、heartbeat、短断重连、输入去重、stop/SIGTERM 收尾和稳定退出码。
stale socket 清理现在先以 `lstat` 验证 Unix socket 类型、服务账户属主、单链接和私有父目录，
再通过受保护父目录句柄执行 `unlinkat`；不可信类型、属主、权限、祖先链接和路径边界均 fail closed。
Supervisor/Worker 生命周期日志使用结构化 `sessionId/workerId/workerEpoch` 字段，且脱敏 token、
SessionRoot、UDS、bootstrap 路径和输入值。

定向结果：`CloudEmuera.Ipc.ContractTests` 9 项通过；
`CloudEmuera.Worker.IntegrationTests --filter Category=ProcessIsolation` 18 项通过，覆盖
两套 fixture input roundtrip、重复 start、错误 token/binding、独立控制客户端进程退出、短断重连、
等待输入停止、双 Worker 并行、Worker 日志字段/脱敏，以及 UDS 普通文件、目录、symlink、
stale socket、父目录权限、祖先 symlink、owner 和路径边界安全场景。全量 `CloudEmuera.slnx`
Release 构建通过（0 警告/0 错误）；默认 API 端口为 `28647` 的 `./scripts/check.sh` 通过，其中
Domain 4、IPC 9、RuntimeAdapter 137、RuntimeCompatibility 26、
Worker Integration 18 项测试及 Web typecheck、单测和生产构建全部通过。`input-roundtrip`
（2 fixture/36 assertions）、`save-root`（1/20）和 `save-directory`（1/20）兼容场景、
`verify-third-party.sh`、`verify-dev-user.sh` 与 `git diff --check` 均通过。P1-05 的持久
租约/epoch 对账、Worker 沙箱和正式 API IPC 仍是已知限制。
