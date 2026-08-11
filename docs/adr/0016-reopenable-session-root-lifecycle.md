# ADR-0016：Session 是可反复开启的持久 SessionRoot

状态：Accepted

日期：2026-08-11

补充：[`ADR-0007`](0007-session-root-native-save-ownership.md)

## 背景

早期 Session 状态机把 `CLOSED` 视为终态，并把 Worker 崩溃后的 `CRASHED` Session 视为只能检查
存档、不能再次运行的历史记录。这使用户在手动关闭、Worker 崩溃或 API 重启后，必须新建另一个
Session 才能继续游戏，即使原 SessionRoot 仍完整持久化。

ADR-0007 已决定 SessionRoot 从 Session 创建到删除始终存在，并且同一 Session 的 Worker 重启
直接复用原目录。既然原生存档、游戏副本和配置都以该目录为权威，Session 更适合表示一个持久的
游戏工作区；Worker 只是该工作区在某一时段的独占执行者。

## 决定

- Session 是持久资源，拥有固定 `gameId`、源内容摘要/manifest 和唯一 SessionRoot。创建时只复制
  Game current content 一次；以后开启同一 Session 不再读取或复制 Game current content，也不
  因 Game 后续编辑而改变 SessionRoot。
- `CLOSED` 表示最近一次 Worker 被用户或管理员正常停止且当前没有活动 Worker，不表示 Session
  资源被删除。`CRASHED` 表示最近一次 Worker/API 生命周期异常结束且当前没有活动 Worker。二者
  都不占用活动配额，都允许用户重新开启同一 Session。
- 新增显式 Session open 操作。`CLOSED` 或 `CRASHED` 经授权、配额检查、SessionRoot 安全校验后
  可以进入 `STARTING`；每次开启都递增 worker epoch、创建新 WorkerLease 并启动一个新 Worker。
- 从 `CRASHED` 重新开启不是指令级恢复。新 Worker 从同一 SessionRoot 冷启动 Emuera，用户通过
  游戏自身的原生读取功能加载已有存档；未保存的解释器内存状态不恢复。
- Session close 只影响运行占用：拒绝新输入、要求 Worker 刷新并退出、失效 lease、释放活动配额，
  最后进入 `CLOSED`。关闭不得删除、重建、复制或回滚 SessionRoot，也不得更改绑定的 Game、源
  摘要或运行时清单快照。
- 浏览器连接存在性不属于 Session 生命周期。Worker 健康时 Session 始终为 `RUNNING`，无论当前
  有零个、一个或多个浏览器连接；Realtime Gateway 在内存中维护连接数并通过断流/心跳最终发现
  离线，不把不可靠的网络存在性写成 `DETACHED` 持久状态。
- Worker 崩溃、API 退出或资源强制终止进入 `CRASHED`，同样保留 SessionRoot。重新开启前必须先
  证明旧 Worker 已退出并失去目录写权限；无法证明时保持不可开启并报告故障。
- 同一 Session 同时最多有一个有效 Worker。开启、存档写操作和另一开启请求通过 WorkerLease、
  epoch、活动配额与条件更新互斥；关闭与重新开启也不能跳过已确认的进程退出屏障。
- Session 删除与 Session close 是不同操作。删除若进入产品范围，必须显式授权和确认，只允许无
  活动 Worker 时执行，并遵循备份、审计和安全目录删除策略；MVP 不因关闭或崩溃自动删除 Session。

## 备选方案

### `CLOSED` 保持终态，继续游戏时创建新 Session

状态机简单，但会复制整棵游戏目录、产生多份几乎相同的 Session 记录，并把用户理解的同一存档
进度拆成多个资源。它也没有利用 ADR-0007 已保证的持久 SessionRoot，因此不采用。

### 删除 `CRASHED`，所有无 Worker 状态统一为 `CLOSED`

用户操作更简单，但会丢失“上次运行异常结束、存档可能正在覆盖”的重要诊断和安全提示。保留
`CRASHED` 作为可重新开启的异常静止态。

### 崩溃后自动重新启动 Worker

可能造成崩溃循环，也可能让损坏或恶意游戏立即再次运行。MVP 要求用户显式重新开启，并在界面
展示上次异常原因。

### 从崩溃指令点恢复

需要解释器快照和兼容性证明，超出 MVP。复用 SessionRoot 只保证原生文件与存档存在，不保证
内存状态恢复。

## 后果

- 用户可以把一个 Session 长期作为一个游戏进度槽，按需开启和关闭 Worker，不必为每次游玩创建
  新 Session。
- `CLOSED` 不再是领域终态，现有领域状态机和测试必须迁移；`CRASHED → STARTING` 成为正式 MVP
  转换，不再使用未来的 `RECOVERING` 才能继续。
- 移除 `DETACHED` 状态可避免浏览器崩溃、网络分区、多客户端连接计数和数据库状态之间的竞态；
  UI 如需展示“当前未连接”，只能将其作为瞬时连接信息，不得据此释放 Worker或活动配额。
- Session 创建与 Worker 开启成为两个不同用例。UI 可以在创建后立即调用 open 提供“一步开始”
  体验，但 HTTP 幂等键、失败结果和事务边界必须独立。
- 重新开启必须复核 SessionRoot 的类型、owner、绑定 marker/manifest 和无活动写租约，但不能把
  Game current content 的新 revision 覆盖到旧 SessionRoot。
- 存档管理仍只在无活动 Worker 时允许；`CLOSED` 和已完成进程回收的 `CRASHED` 都属于可管理状态。
- Game 删除继续受任意 Session 引用保护，因为可关闭的 Session 以后仍可能重新开启。

## 验证

- 创建 Session 只物化一次 SessionRoot；关闭后重新开启、崩溃后重新开启和 API 重启后重新开启
  都保持相同目录 identity、源摘要和 manifest，并使用更大的 epoch。
- 手动关闭只终止 Worker并把状态置为 `CLOSED`，SessionRoot 全部文件、原生存档和自定义目录保持；
  随后同一 Session ID 可再次进入 `RUNNING`。
- kill Worker 或 API 后 Session 变为 `CRASHED`；旧 Worker 写权限确认释放后，同一 Session ID 可
  再次开启并通过 Emuera 原生菜单加载已有存档。
- Game current content 在 Session 关闭期间被重新启用后，重新开启旧 Session 仍运行其原有
  SessionRoot，不混入新 Game 文件。
- 浏览器刷新、崩溃或网络中断不会触发 Session 状态写入；Worker 保持 `RUNNING`，重连后恢复快照
  和当前 prompt。
- 两个并发 open 请求最多创建一个 lease/Worker；open 与存档上传、删除或重命名竞争时最多一方
  获得 SessionRoot 写权限。
- 无法确认旧 Worker 已退出或 SessionRoot 校验失败时，open 被拒绝且不递增为可用活动 lease。
