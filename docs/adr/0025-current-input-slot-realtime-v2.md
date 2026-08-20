# ADR-0025：浏览器输入投递到当前 Runtime 输入槽

状态：已接受

日期：2026-08-20

关联：PLAY-007/008/011、SESS-002/004/006/007、ADR-0018、ADR-0021、P1-S02

## 背景

现有 `cloudemuera.realtime.v1` 要求浏览器在 `session.input` 中携带显示 Snapshot 中的
`promptId`。这把 Worker 内的短生命周期 `ConsolePrompt` 直接暴露成浏览器输入的前置条件。

固定 Emuera 上游的桌面入口并不这样工作。桌面 `MainWindow` 将文本或按钮值直接调用
`EmueraConsole.PressEnterKey`，后者按调用当刻的 `ConsoleState.WaitInput` 和 `inputReq` 处理。
`InputRequest.ID` 仅用于防止旧的本地计时器回调操作新的 request，不由 UI 回传。

部分游戏以短间隔 `TINPUT/TINPUTS` 超时推进脚本、更新立绘后再次进入输入等待。每次等待在
CloudEmuera 当前 adapter 中都会创建新的 `promptId`。浏览器绘制的旧 Snapshot 与下一条输入在网络中
交错时，v1 会稳定返回 `STALE_PROMPT`，即使用户的意图正是原版会交给当前 `WaitInput` 的输入。

不能可靠地从源码位置、时间间隔、约束或显示内容推导两次 `TINPUT` 是否是同一游戏交互。因此不建立
`interactionId`，也不缓存旧输入以等待未来 prompt。

## 决定

### 1. 浏览器发送无目标输入意图

Realtime v2 的 `session.input` 固定为：

```json
{
  "protocolVersion": 2,
  "type": "session.input",
  "messageId": "msg_...",
  "sessionId": "sess_...",
  "workerEpoch": 4,
  "payload": {
    "clientMessageId": "cmsg_...",
    "source": "BUTTON",
    "value": "0",
    "pointer": null,
    "key": null
  }
}
```

请求不含 `promptId`、`interactionId`、显示 sequence 或任何由浏览器猜测的 Runtime 状态。保留
`workerEpoch`，以继续 fence 已重开、已替换或已停止的 Worker。浏览器仍从 Snapshot 渲染当前 prompt 的
类型、约束、按钮和 deadline；这些是呈现提示，不是提交授权令牌。

输入结果包含 `clientMessageId`、状态、稳定 reason 和可空的 `resolvedPromptId`。后者只说明 Worker
实际尝试处理的 prompt，供 UI 收敛 pending 状态和诊断，绝不作为下一次提交的必需字段。

### 2. Worker 只在单一临界区读取当前输入槽

RuntimeAdapter 新增不含 prompt 的 `ConsoleInputAttempt`，包含 `clientMessageId`、value、source、pointer
和 key。`InputCoordinator.SubmitCurrent(attempt)` 在现有同一把锁内按以下顺序执行：

1. 查找 `clientMessageId` 的有界 receipt；同一 payload 返回首次结果，异 payload 返回 `CONFLICT`；
2. 校验 attempt 的通用结构和 source/pointer/key 对应关系；
3. 读取当刻 `currentPrompt`；不存在则生成并缓存 `NO_ACTIVE_PROMPT`，不进入任何队列；
4. 以此 prompt 校验 allowed source、`WaitOnly`、`OneInput` 和约束；
5. 成功时原子清除该 prompt、完成其 waiter，并缓存 `ACCEPTED` 与实际 `resolvedPromptId`；
6. timeout、显式 close、取消和输入继续在同一把锁中竞争，只有先线性化的终态获胜。

`TINPUT` 超时后到下一次 `WaitInput` 的空隙按步骤 3 丢弃。下一次 `WaitInput` 已开始时，输入按步骤 4
使用该当刻 request。系统绝不把步骤 3 的输入缓存、重放或补投递给未来 request。

Worker 仍是 prompt、格式、timeout、首个输入和去重的唯一权威；API 不读取 SQLite 的
`current_prompt_id` 来代替上述临界区判断。

### 3. 幂等性以用户输入意图为边界

receipt key 继续是当前 Worker 内有界的 `clientMessageId`，fingerprint 改为：

```text
value + source + pointer payload + key payload
```

它不包含内部 `promptId`，也不包含 `workerEpoch`（receipt 本身只存在于一个 Worker epoch）。首次结果，
包括 `NO_ACTIVE_PROMPT`、`INVALID_FORMAT` 和 `INVALID_COMMAND`，都必须缓存。这样网络重试不会在
下一次 TINPUT 或下一段游戏逻辑中执行第二次。调用方要在一个已完成或被拒绝的输入后再次表达意图，必须
生成新的 `clientMessageId`。

多个浏览器同时提交仍在同一 Worker 临界区竞争，至多一个尝试可接受当前 prompt。若 timeout、cancel 或 close
先取得该锁，随后到达的无目标输入只能观察到空槽并得到 `NO_ACTIVE_PROMPT`；它没有 prompt token 可据以把
结果归因为 `TIMED_OUT` 或 `CANCELLED`。旧 epoch、停止态、
IPC 写入失败和 pending 上限的处理不改变；这些结果由 API 在 Worker 外层生成，不进入 Worker receipt。

### 4. 破坏性协议升级，仓库只支持一个当前版本

本项目尚未承诺对外部客户端、独立 Worker 二进制或已发布 API 提供兼容期。因此这次升级不保留
`cloudemuera.realtime.v1`：删除 v1 schema、golden fixture、generated DTO/type 和子协议协商，只接受
`cloudemuera.realtime.v2`，envelope `protocolVersion` 固定为 `2`。旧客户端连接或发送带 `promptId` 的 v1
消息必须被协议校验拒绝，不能静默改写为当前输入槽语义。

API--Worker `SubmitInput` 同样升级为 `cloudemuera.ipc.v4` / C# `CloudEmuera.Ipc.V4`。v4 request 移除
`prompt_id`，result 使用可空 `resolved_prompt_id`；v3 proto、namespace、代码生成引用、capability digest 和
测试 fixture 在同一次变更中替换。v4 registration/ready/capability digest 与 RuntimeBaseline 同步更新；同一
API 进程只启动匹配的 v4 Worker，不保留 v3 到 v4 的输入适配层。

### 5. UI 和回执状态

浏览器 pending input 只保存 `workerEpoch + clientMessageId + source + value + pointer/key`，不再保存
promptId。回执以 `clientMessageId` 关联；来自旧 epoch 的回执不改变当前 store。Snapshot 中 prompt 的变化
仅影响控件渲染和 pending 的视觉状态，不能把已发消息标记为“可重投递”。连接中断前未收到回执的消息可
用原 `clientMessageId` 重试；若重连到了新 epoch，不重试旧意图，标记为 unknown/stale 并让用户重新操作。

对 `NO_ACTIVE_PROMPT`、`INVALID_FORMAT`、`STALE_EPOCH`、`SESSION_NOT_ACCEPTING_INPUT` 和
`WORKER_UNAVAILABLE`，UI 必须解除本地 pending 禁用状态。前两者不伪造游戏输出；实际 prompt 更新继续
由 Snapshot/display batch 驱动。

### 6. 兼容性边界

此决定有意复刻原版“事件到达时交给当前 `WaitInput`”的语义。远程网络仍可能使一个较早的用户操作在
语义不同的后续 prompt 到达时被接受；在不具备上游显式交互连续性标记时，无法同时消除该风险并兼容
TINPUT 刷新。该风险通过以下方式收敛但不伪装为完全消除：不缓存、立即线性化、epoch fencing、单 Worker
串行输入、唯一 client message ID、短的 API/IPC deadline 和 UI 在每次 Snapshot 后更新控件。

## 备选方案

### 保留浏览器 `promptId`

可严格拒绝陈旧画面上的输入，但与上游桌面输入模型不同，并使高频 TINPUT 游戏在正常网络抖动下不可玩，拒绝。

### 推断或生成 `interactionId`

无法可靠判断两次 TINPUT 是否属于同一轮轮询。相同行号、约束、画面或时间间隔都可能出现在不同游戏逻辑中，拒绝。

### 无目标输入的延后队列

会把没有 active prompt 时的旧操作投递给未来、可能不相关的 prompt，违反原版事件语义和本 ADR 的安全性质，拒绝。

### API 根据镜像 prompt 改写请求

API mirror 与 Worker 的当前输入槽之间没有原子关系，且会复制 Runtime 输入权威，拒绝。

## 后果

- `promptId` 仍保留在 Console Snapshot、display operation、Worker 内部 timeout/close 和回执诊断，不再是浏览器提交字段。
- Application、IPC、API correlation、Realtime schema、Web reducer 和测试 fixture 都需要同步迁移。
- 运行时 adapter 要将“receipt 的请求身份”和“实际处理的 prompt 身份”分离；所有既有仅按 promptId 关联回执的测试都必须改写。
- 这不是存储迁移：SQLite 的 `waiting_for_input/current_prompt_id` 继续只是观测与列表展示，不保存输入 receipt。

## 验证

- RuntimeAdapter 单元测试：空槽丢弃、TINPUT 边界输入、当前槽接受、timeout/input/close 竞态、receipt duplicate/conflict、LRU 淘汰；
- 真实 headless fixture：30ms TINPUT 重绘循环中在旧 Snapshot 后发送输入，确认 Runtime 接到值；在两个 TINPUT 之间发送则确认得到 `NO_ACTIVE_PROMPT` 且不会被下一次等待接受；
- IPC v4 契约：无 prompt request、可空 resolved prompt result、旧 v3 client 或 digest 不匹配 fail closed；
- Realtime v2：schema/golden、v1 子协议和带 `promptId` 的 input 被拒绝、epoch fencing、回执 correlation 只按 clientMessageId、重连 retry 与 pending 收敛；
- 真实 Kestrel + UDS：高频 TINPUT、双客户端竞争、关闭竞态、Worker 重开、断线和慢消费者；
- 在 dev Docker 完整执行 `./scripts/check.sh`。
