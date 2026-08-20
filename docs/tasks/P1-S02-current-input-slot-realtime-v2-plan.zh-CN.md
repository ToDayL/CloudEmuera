# P1-S02 当前输入槽与 Realtime/IPC 协议升级详细方案

状态：待实施

日期：2026-08-20

关联需求：SESS-002/004/006/007、PLAY-004～008、PLAY-011、AC-002/005。

关联决策：ADR-0018、ADR-0020、ADR-0021（被本任务替代的 v1 输入部分）、
ADR-0025。

前置任务：P1-07、P1-08、P1-09、P1-11。

## 1. 目标和边界

本任务修复高频 `TINPUT/TINPUTS` 超时重绘游戏的输入耦合：浏览器不再将已显示 Snapshot 的 `promptId`
作为 Worker 接受输入的前置条件。浏览器只发送一次输入意图；Worker 在输入到达时，用持有当前 prompt、
timeout、取消和首个输入的同一临界区决定是否处理。

结果必须与固定上游桌面行为一致：桌面 `MainWindow` 将文本/按钮值传给
`EmueraConsole.PressEnterKey`，后者在处理事件时读取 `WaitInput`/`inputReq`。`InputRequest.ID` 只防止旧的
本地 timer 回调影响新 request，不是 UI 回传的交互 token。两次 TINPUT 是否是同一轮轮询不能由行号、
显示内容、timeout、约束或时间间隔可靠推断，因此不增加 `interactionId`。

范围包括 RuntimeAdapter、Application 输入契约、IPC、API Worker correlation、Realtime schema/endpoint、
Web store 和所有相应测试。此次为仓库内破坏性升级：只支持 WebSocket v2 和 IPC v4，不提供 v1/v3 并行、
转换器或数据库迁移。

不做：输入延后队列、未来 prompt 重放、API 根据 snapshot 或 SQLite `current_prompt_id` 改写输入、跨 epoch
receipt、客户端控制权租约、对远程延迟导致早先物理操作落到语义不同的后续 prompt 的虚假保证。

## 2. 不变量和线性化规则

`InputCoordinator` 的已有单锁是 Worker 内唯一输入线性化点。接收 `ConsoleInputAttempt` 后必须在这把锁内
依次做 receipt、通用载荷校验、当前槽读取、prompt 约束校验和终态完成；timeout、cancel、close 与输入也
只能在这把锁内改变同一 prompt。

| 到达时的线性化结果 | Worker 动作 | receipt 和重试 |
| --- | --- | --- |
| 有当前 prompt，输入先获胜 | 对该 prompt 校验并接受，清槽、完成 waiter | 缓存 `ACCEPTED` 与 resolved prompt；同值重试回首次结果 |
| 有当前 prompt，格式或 source 不允许 | 不清槽，返回 `INVALID_FORMAT` | 缓存；新用户操作必须生成新 clientMessageId |
| 没有当前 prompt | 返回 `NO_ACTIVE_PROMPT`，永久丢弃 | 缓存；同 ID 不得在下一次 prompt 重放 |
| timeout/cancel/close 先获胜 | 当前输入不能再完成该 prompt | 随后输入观察到空槽，返回并缓存 `NO_ACTIVE_PROMPT`；不把值转交后续 prompt |
| 同一 clientMessageId、payload 不同 | 不进入 prompt 判断 | 返回 `CONFLICT`，不覆盖首次 receipt |
| API route 的 epoch 已更换或已停止 | 不写入 Worker | `STALE_EPOCH` 或 `SESSION_NOT_ACCEPTING_INPUT`；不写 Worker receipt |

即使显示中的 `promptId` 已改变，只要输入实际到达时存在可接受的当前 prompt，Worker 就按当前 prompt
处理。反之，落在两次 `WaitInput` 的空隙必须丢弃。这是唯一能同时复刻上游行为且不把旧操作投递给未知未来
交互的语义。

## 3. 公共 Realtime v2 契约

入口保持 `GET /api/v1/realtime`，但只协商 `cloudemuera.realtime.v2`；所有 envelope 的
`protocolVersion` 必须为 `2`。`client.hello.supportedProtocolVersions` 必须包含 `2`，服务端 hello/错误
消息只公布 v2。删除 v1 schema、golden、generated type、常量和协商支持；请求 v1、缺失子协议或 version 1
都按既有稳定协议拒绝路径结束，不能降级。

`session.input` 的封闭 payload 为：

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

`promptId`、`interactionId`、snapshot sequence 和任何客户端推测的 Runtime 状态都不是 input 字段；schema
以 `additionalProperties: false` 拒绝它们。Snapshot、`display.batch` 的 `ConsolePrompt` 继续含 `promptId`，
用于渲染、close display operation、内部 timeout 和诊断，不能被 Web input DTO 复用。

`session.input.result` 必须返回下列 payload：

```json
{
  "clientMessageId": "cmsg_...",
  "status": "ACCEPTED",
  "reasonCode": "accepted",
  "resolvedPromptId": "prompt_...",
  "normalizedValue": "0"
}
```

`resolvedPromptId` 可为 `null`，表示没有 Runtime prompt 被尝试（例如 `NO_ACTIVE_PROMPT`）或 API 在 Worker
外层拒绝。它是事实回执，不参与请求或 API correlation。v2 允许的 Worker 最终状态是 `ACCEPTED`、
`DUPLICATE`、`CONFLICT`、`NO_ACTIVE_PROMPT`、`INVALID_FORMAT`、`INVALID_COMMAND`；
API 外层还可产生 `FORBIDDEN`、`STALE_EPOCH`、`SESSION_NOT_ACCEPTING_INPUT`、
`SESSION_NOT_RUNNING`、`INPUT_BACKPRESSURE`、`WORKER_UNAVAILABLE`。

## 4. IPC v4 契约和版本切换

将 `structured-worker.proto` 的 package 改为 `cloudemuera.ipc.v4`，C# namespace 改为
`CloudEmuera.Ipc.V4`，`RuntimeBaseline.StructuredIpcProtocolVersion` 改为 `4`。所有 registration、ready、
envelope、capability digest、generated source、API/Worker import 和 contract fixture 同一次替换；运行中只有
匹配 v4 的 API/Worker 可注册。

为避免将旧字段号静默解释为新语义，`SubmitInput` 精确冻结为：

```proto
message SubmitInput {
  reserved 1;                 // v3 prompt_id
  string client_message_id = 2;
  string value = 3;
  InputSource source = 4;
  oneof payload {
    PointerPayload pointer = 5;
    KeyPayload key = 6;
  }
  int64 deadline_unix_milliseconds = 7;
}

message InputResult {
  reserved 1;                 // v3 prompt_id
  string client_message_id = 2;
  InputResultKind kind = 3;
  string reason_code = 4;
  string normalized_value = 5;
  bool has_normalized_value = 6;
  optional string resolved_prompt_id = 7;
}
```

生成后的 C# 对可选 `resolved_prompt_id` 使用 protobuf 的 presence API；Application/Realtime 边界统一映射为
`string?`。v3 文件名或历史文档可保留为历史记录，但不得继续生成、引用或可连接。原 `worker.proto` P0-06
历史说明不被误称为当前结构化 IPC。

## 5. 分层实施顺序

### 5.1 RuntimeAdapter

1. 将 `ConsoleInputCommand` 替换为无 prompt 的 `ConsoleInputAttempt`，保留 `clientMessageId`、value、source、
   pointer、key 和相同通用验证。fingerprint 严格为 `value + source + pointer + key` 的长度界定结构编码，
   不含 prompt 或 epoch。
2. `ConsoleInputResult` 分离请求 `clientMessageId` 与可空 `ResolvedPromptId`；所有 factory、receipt 和测试
   不能假定结果 prompt 等于请求字段。删除 `StalePrompt` 的输入业务路径。
3. `InputCoordinator.SubmitCurrent(attempt)` 在现有锁内先查 receipt；同值返回稳定首次 result，异值返回
   conflict。无 prompt 总是产生并缓存 `NO_ACTIVE_PROMPT`。成功时读取当刻 prompt、完成 waiter 并记录实际 ID。
4. `StructuredGameConsole` 只暴露“提交到当前槽”的方法；内部创建、snapshot 显示和 timeout 仍使用 prompt ID。
   所有 headless driver 调整为新的调用方式，不能在测试辅助代码中从 snapshot 再把 prompt ID 传回。

### 5.2 Application、Worker 和 API

1. `SessionInputCommand` 删除 `PromptId`；`SessionInputResult` 改为 `ResolvedPromptId: string?`，所有
   `RealtimeSessionResults` 外层错误填 null。`SessionInputResultCodes.StalePrompt` 及其映射删除。
2. Worker controller 将 v4 `SubmitInput` 构造成 `ConsoleInputAttempt`，收到的 `InputResult` 写回 nullable
   resolved prompt。Worker stopping/deadline/console unavailable 的 reply 不伪造 prompt ID。
3. `ApiWorkerSession.QueueInputAsync` 的 pending correlation 只校验 correlation、完整 persistent binding、
   `clientMessageId` 与 epoch；禁止要求 result prompt 与请求相等。更新估算字节数，去除 prompt 长度。
4. `WorkerManager.BeginInputAsync` 和 shared `ISessionCommandGate` 继续在发送前验证持久 `RUNNING` binding 和
   epoch；等待 IPC receipt 在 gate 外完成。API 不读取 `current_prompt_id` 决定接受、重写或补发输入。

### 5.3 Realtime 与 Web

1. 将 `RealtimeProtocol.Version`/`Subprotocol` 改为 `2`/`cloudemuera.realtime.v2`；`RealtimeInputPayload` 删除
   `PromptId`，result DTO 增加可空 `ResolvedPromptId`。更新严格 parser、source generation context、schema、
   `CloudEmuera.Contracts.csproj` 复制项、生成和校验脚本。
2. 以 v2 替换 `realtime-v1.schema.json`、`*.v1.json` golden、`generated.ts` 和 fixture 名称。现有 schema
   路径/名称是仓库内实现细节，不保留 alias；`promptId` 必须因封闭 payload 被拒绝。
3. `connection.ts` 只发送 intent；pending item 保存 epoch、clientMessageId、source、value、pointer、key，
   不保存 prompt。pending key 为 `(workerEpoch, clientMessageId)`，结果只按该 key settle。
4. snapshot prompt 变更只更新控件，不撤销、重发或重新标记 pending。断线在同 epoch 内可用同一 ID 及相同
   payload 重试；重连到新 epoch 时丢弃旧 pending retry，并让下一次用户动作取得新 ID。任何 final result
   均解除该输入的本地 pending；`NO_ACTIVE_PROMPT` 不伪造 display 更新。

## 6. 测试矩阵

| 层级 | 必须新增或改写的场景 |
| --- | --- |
| RuntimeAdapter unit | 当前槽接受；空槽丢弃；空槽 receipt 后打开新 prompt 同 ID 仍返回首次 no-active；duplicate/conflict；LRU 淘汰；source/format；两个并发 input 只有一个接受；timeout/input/close 利用 barrier 在同一临界区断言单一终态，后到输入为 no-active。 |
| Runtime compatibility | 增加或扩展真实 30ms `TINPUT/TINPUTS` fixture：旧 Snapshot 后提交无目标输入最终进入当前 WaitInput；两个请求间的人工同步空洞返回 no-active 且下一次等待不消费；原有计时/`ISTIMEOUT`、button、keyboard、pointer 行为回归。 |
| IPC contract | v4 proto descriptor、reserved 1、optional presence、无 request prompt、可空 result resolved prompt；v3 namespace/package 或旧 digest 注册 fail closed。 |
| API/Worker integration | correlation 伪造、未知、迟到、重复回执不完成错误 waiter；不同 epoch/worker binding 回执拒绝；close 先进入 gate 后输入不写 socket；已入队输入和 close 的 Runtime 终态仍单一。 |
| Realtime contract | 仅 v2 subprotocol；hello version 2；v1 和带 `promptId` payload 被严格 parser/schema 拒绝；golden snapshot/input/result 的 resolvedPromptId null/非 null；message ID 和 input receipt 的上限不变。 |
| Kestrel + UDS | 真实 Cookie 连接、30ms timer 游戏、双客户端竞争、断线同 epoch retry、reopen 后旧 epoch 不重试、慢消费者和 draining；检查只有当前 Worker 接收输入。 |
| Web unit/e2e | generated type 编译；pending 不含 prompt；snapshot prompt 切换不错误撤销 pending；no-active/invalid/stale-epoch 的 UI 收敛；桌面/移动控制器都按新 payload 发出一次输入。 |

测试名称或 `Trait` 显式标记 PLAY-007、PLAY-008 或 PLAY-011。原有“旧 prompt 必须 stale”测试应删除或
替换为上述当前槽/空槽规则，不能仅调整断言让旧测试失去覆盖。

## 7. 完成和验证

实施前后都用 dev Docker，不使用宿主 .NET、Node 或 pnpm：

```bash
./scripts/dev-up.sh
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests tests/CloudEmuera.RuntimeCompatibility.Tests \
  --no-restore --configuration Release --filter "Category=ConsoleContract|Category=RuntimeBridge|Category=InputDeduplication"'
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Ipc.ContractTests tests/CloudEmuera.Realtime.Tests \
  tests/CloudEmuera.Worker.IntegrationTests --no-restore --configuration Release \
  --filter "Category=InputDeduplication|Category=WebSocketProtocol|Category=Realtime|Category=RuntimeBridge"'
bash -lc 'source scripts/lib/dev-env.sh && docker compose -f compose.dev.yaml run --rm web \
  sh -c "pnpm install --frozen-lockfile && pnpm typecheck:web && pnpm test:web && pnpm build:web"'
./scripts/check.sh
git diff --check
```

根据最终测试实际 trait 细化 filter，避免以不存在的分类掩盖遗漏。完成前还必须用 `rg` 确认生产代码、当前
schema、generated client 和运行时契约中不再存在 v1 subprotocol、V3 namespace、browser input `promptId`
或 `STALE_PROMPT` 输入路径；历史 ADR/P1-09 记录可保留并明确已被 ADR-0025/P1-S02 替代。

完成条件：所有矩阵场景与完整检查通过；协议版本、capability digest、golden 和生成物一致；没有 SQLite
migration，因为 receipt/current slot 仍是单 Worker 的易失运行时状态；文档中的当前需求和设计只描述 v2/v4。

建议按逻辑拆分提交：`refactor(runtime): submit input to current slot`、`refactor(ipc): upgrade input contract to v4`、
`refactor(realtime): replace prompt-bound input with v2`、`test(runtime): cover timed input redraw races`。
