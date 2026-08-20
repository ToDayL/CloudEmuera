# P1-11：浏览器 Session 控制台和存档界面详细开发方案

状态：已实现（P1-11；最终发布矩阵交由 P1-15）

替代说明（2026-08-20）：本任务的渲染、Session UI 和存档界面仍有效；其中 WebSocket v1、输入携带
`promptId`、按 prompt 取消 pending 的描述已由 P1-S02/ADR-0025 取代。实现和后续维护以 P1-S02 的
Realtime v2 当前输入槽语义为准。

设计日期：2026-08-15

对应开发步骤：`P1-11 — 浏览器 Session 控制台和存档界面`

关联需求：AUTH-001～003/005、SESS-003～007/010～012、PLAY-001～012、SAVE-003/006/009、
COMP-007、OPS-002/004、SEC-006/008/009、NFR-001/006～008/013、AC-002/005/008/009/011/012/014

关联决策：[`ADR-0004`](../adr/0004-runtime-rich-content-allowlist.md)、
[`ADR-0015`](../adr/0015-api-owned-worker-lifecycle.md)、
[`ADR-0016`](../adr/0016-reopenable-session-root-lifecycle.md)、
[`ADR-0018`](../adr/0018-emuera-structured-interaction-model.md)、
[`ADR-0019`](../adr/0019-libgdiplus-mvp-graphics-compatibility.md)、
[`ADR-0020`](../adr/0020-api-snapshot-mirror-and-bounded-realtime-output.md)、
[`ADR-0021`](../adr/0021-freeze-realtime-websocket-v1.md)、
[`ADR-0022`](../adr/0022-session-save-file-cap.md)、
[`ADR-0023`](../adr/0023-session-presentation-assets-and-csp.md)

前置任务：P1-01～P1-10，尤其是 P1-06 Session HTTP、P1-07 结构化 Runtime、P1-08 Snapshot、
P1-09 WebSocket 和 P1-10 存档 API

后续任务：P1-12 管理诊断、P1-13 实例级限制、P1-15 MVP 统一验收和视觉回归

## 1. 目标结果

P1-11 把已有的后端纵切接成第一个可实际游玩的浏览器客户端。完成后，用户可以从真实 Session 列表
创建、开启、连接、输入、关闭和重新开启 Session；刷新或断网后以当前 Worker 的完整 Snapshot 恢复；
在停止态管理 SessionRoot 中的原生存档；并在桌面及移动浏览器中安全呈现 P1-07 已冻结的全部
Supported Console、Scene、Input 和 Media 语义。

本阶段的核心结果是：

1. Session、存档和实时页面不再读取 `App.tsx` 中的演示数组或模拟连接状态，所有可见事实来自 HTTP
   或 WebSocket 的正式契约。
2. 浏览器对 `session.snapshot` 做完整替换，只对当前 epoch 的连续 `display.batch` 做原子归约；发现
   epoch 变化、序号缺口、归约失败或 `resync.required` 时停止消费旧基线并重新取得 Snapshot。
3. DOM、Canvas 2D 和 WebAudio renderer 覆盖 P1-07 的全部 Supported 节点、操作和媒体状态；未知或
   无法呈现的 Supported 能力必须显示阻断错误，不能静默丢弃、转成普通文本或使用 `innerHTML`。
4. 输入控件完整覆盖所有浏览器可提交的输入类型，严格使用当前 `promptId + workerEpoch`；同一尝试的
   `clientMessageId` 在回执不确定时保持不变，不自动用新 ID 重放。
5. 计时输入只显示 Worker deadline 的估算剩余时间；浏览器到零不提交默认值、不关闭 prompt，等待
   Worker 的权威 `ClosePrompt` 或后续 Snapshot。
6. 图片、Sprite、背景、字体和音频只通过按 Session 授权的同源资源 API 取得；浏览器不接收或拼接
   SessionRoot 路径、游戏 URL、`data:` URL 或任意 MIME。
7. 既有 P1-04 游戏库、登录和只读文件页面继续复用。本阶段只删除残留的浏览器写文件、搜索等越界
   入口，不重新实现 Game UI，也不把 P1-12 管理功能提前塞入 Session 页面。

## 2. 范围与非目标

### 2.1 本阶段必须实现

- Session 列表、筛选、详情、创建、open、close、reopen 的真实 HTTP client、TanStack Query hooks、
  loading/empty/error/pending 状态和幂等命令体验；
- “创建并开始”的两阶段编排：独立 create key 和 open key、分别展示失败、对 `202` 轮询最终状态，
  绝不把两个服务端命令合并为一个隐式事务；
- 单一浏览器 Realtime connection manager、严格 envelope decode、heartbeat、resume/unsubscribe、重连
  退避、身份失效处理和逐 Session store；
- 由 JSON Schema/OpenAPI 生成或机器校验的闭合 TypeScript 契约，替换当前
  `StructuredSnapshotPayload = Record<string, unknown>` 占位；
- 纯函数 Console reducer、完整 Snapshot 替换、transaction 原子归约、epoch/sequence fencing、
  resync 状态机和 pending input receipt 管理；
- 可访问 DOM scrollback、安全 HTML AST、Canvas scene/background/raster、静态资源、动画、字体和
  WebAudio channel renderer；
- EnterKey、AnyKey、Integer、Text、AnyValue、IntegerButton、TextButton、PrimitivePointerKey 和
  WaitOnly 的 UI/键盘/触摸行为；
- 服务端时间基准、deadline 倒计时、timeout 等待态、迟到/重复/冲突/背压回执和多客户端首个回答体验；
- Session 原生存档 list/download/upload/replace/rename/delete 页面，活动态写操作锁定、CRASHED 风险
  说明、进度、错误和显式删除确认；
- 按 Session 授权的 runtime asset/presentation manifest HTTP 纵切，包含资源 identity、MIME、缓存、
  Range、dirfd/no-follow 和跨用户隔离测试；
- 桌面和移动 responsive layout、安全区、软键盘、触摸目标、焦点管理、键盘导航、ARIA live 状态和
  基础自动化可访问性检查；
- 独立 DataRoot/Compose project 的组件、协议、HTTP 集成和 Playwright 场景。

### 2.2 明确不做

- 不改变 P1-07 的 Console/Input/Media 语义，不让浏览器决定 timeout、默认值、`ISTIMEOUT` 或输入赢家；
- 不增加 WebSocket ack、历史补发、离线输入队列、跨 Worker 输入去重或浏览器连接控制权租约；
- 不把 Snapshot、增量、输入回执、音频播放进度或连接数写入 SQLite；
- 不使用 SignalR、gRPC-Web、Service Worker 离线运行、SSR、WebGL 或第三方富文本 renderer；
- 不执行 raw HTML/CSS，不使用 `dangerouslySetInnerHTML`，不接受外部 URL、任意 `data:`/`blob:` 来源；
  仅动态 Raster 的内部解码可以创建由 renderer 管理并及时 revoke 的本地 Blob URL；
- 不提供控制台历史导出、宏、自动点击、按键脚本、多个客户端独立控制租约或会话内聊天；
- 不允许活动 Worker 期间上传、替换、重命名或删除存档，不增加 SaveArtifact、generation、回收站、
  ZIP 存档包或 Session 间复制；
- 不重复开发 P1-04 已完成的 Game 创建、导入、验证、启用和文件只读浏览；
- 不实现 P1-12 的管理员 force-stop、Worker 诊断和通用审计 UI，也不实现 P1-15 的完整四浏览器发布矩阵。

## 3. 已有基线与实施前缺口

### 3.1 可直接复用

- `auth.tsx` 已接入 Cookie 登录、强制改密和真实身份恢复；P1-04 的 Game UI 已接入真实 API；
- P1-06 已提供 Session create/list/detail/open/close、ETag、`202`、稳定错误和独立幂等 scope；
- P1-09 已提供 `/api/v1/realtime`、`cloudemuera.realtime.v1`、live authorization、heartbeat、完整
  Snapshot/resync、输入回执和协议 golden fixture；
- `RealtimePayloadMapper` 已把 v3 Runtime 状态映射为浏览器友好的 camelCase 闭合 union；
- P1-10 已提供 saves list/download/PUT/PATCH/DELETE、mutation lease、幂等、确认和稳定错误；
- P1-07 的 `runtime-capabilities.json`、Snapshot 和 transaction golden JSON 可作为 renderer fixture；
- 现有页面已经提供一版视觉骨架、移动 breakpoint 和登录/Game 流程测试，可保留其无歧义的视觉语言。

### 3.2 必须先消除的缺口

1. `App.tsx` 的 Session、Console 和 Saves 页面仍使用固定数组、假倒计时和本地 `connected` 开关，必须
   拆成真实 feature modules；不能在同一个巨型组件中继续叠加协议状态。
2. `realtime/protocol.ts` 仍把 Snapshot/Batch 写成 `Record<string, unknown>`，现有 JSON Schema 也只
   严格约束 envelope/input，尚未完整描述 Console union。P1-11 必须让 C# DTO、schema、generated TS
   和 golden fixture 成为同一份可校验事实，防止客户端以类型断言吞掉未知字段。
3. 仓库尚无 Session runtime asset API。生产 `assetId` 是内容摘要身份，但浏览器无法从摘要安全取得
   SessionRoot 内字节；音频也需要 Range/正确 MIME。该缺口属于 SEC-008 和 P1-11 renderer 的阻断项。
4. 字体 family 是逻辑名，当前公开契约没有浏览器允许的 family→asset/fallback 映射。必须冻结最小
   presentation manifest；不能直接把上游宿主字体名塞入 CSS `font-family`。
5. 现有 CSP 只有基础 `default-src 'self'`。实现 WebSocket、同源图片/字体/音频和内部 Raster blob 后，
   必须给 production/dev 分别建立最小 directive 并测试，不能用 `*`、`unsafe-eval` 或宽泛外部来源。
6. 现有 e2e 主要覆盖身份。需要新建隔离脚本创建真实 Game/Session/Worker，不能读取人工 `.env`、
   `./data` 或复用身份测试的 Compose project。

第 3～5 项涉及新的公开资源契约和浏览器安全策略。实施切片 1 必须新增 ADR 冻结资源 identity、
presentation manifest、旧 Session 兼容、MIME/Range/cache/CSP 语义；若无法从既有 frozen manifest
确定性恢复 `assetId → file identity` 或字体映射，应先修正 P1-07/P1-06 清单，而不是在浏览器按路径猜测。

## 4. 前端架构与状态所有权

### 4.1 建议目录

```text
src/CloudEmuera.Web/src/
├── api/
│   ├── client.ts                 # cookie、CSRF、统一错误、request id
│   ├── generated.ts              # OpenAPI 生成 DTO；禁止手改
│   └── queryClient.ts
├── sessions/
│   ├── api.ts                    # list/detail/create/open/close hooks
│   ├── SessionListPage.tsx
│   ├── NewSessionPage.tsx
│   └── sessionErrors.ts
├── realtime/
│   ├── generated.ts              # realtime schema 生成的闭合 union；禁止手改
│   ├── codec.ts                  # 运行时 decode/校验
│   ├── connection.ts             # 唯一 socket、hello/ping/reconnect
│   ├── sessionStore.ts            # epoch/sequence/snapshot/input 状态
│   ├── reducer.ts                # 纯函数 transaction reducer
│   └── testFixtures.ts
├── console/
│   ├── ConsolePage.tsx
│   ├── ScrollbackRenderer.tsx
│   ├── SafeHtmlRenderer.tsx
│   ├── CanvasRenderer.tsx
│   ├── MediaController.ts
│   ├── AssetResolver.ts
│   ├── PromptController.tsx
│   └── DeadlineClock.ts
├── saves/
│   ├── api.ts
│   ├── SavesPage.tsx
│   └── SaveMutationDialogs.tsx
└── test/
    ├── realtimeHarness.ts
    └── accessibility.ts
```

目录名可以按实现调整，但以下依赖边界必须保持：页面只调用 hooks/store；renderer 不发 HTTP 生命周期
命令；reducer 不依赖 React、DOM、时钟、网络或 WebAudio；Realtime store 不持有 saves list；TanStack
Query 不保存 ConsoleSnapshot。

### 4.2 状态归属

| 状态 | 权威来源 | 浏览器缓存 | 失效方式 |
| --- | --- | --- | --- |
| 登录用户 | `/auth/me` | AuthProvider | 401/安全戳变化重新登录 |
| Session 列表/详情 | HTTP + SQLite | TanStack Query | create/open/close 后精确失效/轮询 |
| Console 当前状态 | Worker Snapshot + transaction | 每 Session realtime store | Snapshot 完整替换或 unsubscribe |
| 连接/重连状态 | 当前 WebSocket | connection manager | socket close/hello timeout |
| pending input | 当前浏览器内存 | 每 Session realtime store | 确定回执、prompt 改变或页面销毁策略 |
| saves list | HTTP + SessionRoot | TanStack Query | 成功修改后失效 |
| 媒体实际播放 | 浏览器 AudioContext | MediaController | media revision/Snapshot/页面卸载 |

TanStack Query 的重试只用于安全 GET。create/open/close/save mutation 自己持有幂等键；库级自动重试若
会生成新 key 必须关闭。页面重新 render、StrictMode 双调用或 route remount 不得创建第二个命令。

## 5. 契约生成与浏览器边界校验

### 5.1 HTTP 类型

- 以 `/openapi/v1.json` 为输入生成 `api/generated.ts`，生成结果和 [`scripts/generate-api-types.mjs`](../../scripts/generate-api-types.mjs) 提交仓库；
- CI/check 验证重新生成后无 diff，避免 C# DTO 与前端手写接口漂移；
- 生成器只作为开发依赖并锁入 `pnpm-lock.yaml`，最终 bundle 不包含 schema/compiler；
- API 错误继续统一为 `{code,message,requestId,details?}`，UI 按稳定 `code` 决策，不匹配中文 message。

### 5.2 Realtime 类型与校验

- 扩展 `realtime-v1.schema.json`，完整描述 Snapshot、transaction、node、operation、prompt、scene、
  media 和所有枚举 discriminator；`additionalProperties` 的前向兼容只允许在已决定的对象边界；
- 从 schema 生成 TypeScript discriminated union，并保留轻量 runtime decoder。decoder 必须拒绝未知
  `type`/operation/node、缺失必需字段、非有限数字、非法 base64 raster 和超出前端同值上限的数据；
- C# protocol tests 继续对 golden JSON 做序列化，Web tests 读取同一 fixture 解码和归约；
- 改变必需字段或语义必须升级 realtime subprotocol，不能在 P1-11 为迁就浏览器静默放宽 v1。

### 5.3 capability handshake

客户端构建时从 `runtime-capabilities.json` 生成 Supported capability list/digest，发送 `client.hello`；
`server.hello` digest 不同或 `session.resume.result=CAPABILITY_MISMATCH` 时显示版本不兼容阻断页，不进入
部分 renderer。UI 不允许用户“仍然继续”绕过 digest。

## 6. Session runtime 资源 HTTP 纵切

### 6.1 公开契约

建议冻结两个只读端点：

```http
GET /api/v1/sessions/{sessionId}/presentation-manifest
GET /api/v1/sessions/{sessionId}/assets/{assetId}
Range: bytes=...
```

最小 presentation manifest：

```json
{
  "schemaVersion": 1,
  "assets": [
    {"assetId":"sha256-...","mediaType":"image/png","byteLength":1234,"contentDigest":"sha256:..."}
  ],
  "fonts": [
    {"family":"default","assetId":"sha256-...","fallback":"sans-serif",
     "cssFamily":"cloudemuera-font-0123456789abcdef","aliases":[]}
  ],
  "fontDiagnostics": []
}
```

能力集合摘要只在 realtime `server.hello`/`session.resume` handshake 中校验，不在
presentation manifest 中重复；这样资源清单与运行时能力契约各自保持单一来源。

若完整 asset list 对大型 Game 过大，可以只公开有界字体映射，并让 asset GET 按 ID 查 frozen entry；
该取舍由新增 ADR 冻结。无论采用哪种形态，都不能公开逻辑/物理路径、SessionRoot、Game workspace、
原始 manifest JSON 或同摘要的其他用户资源位置。

### 6.2 资源解析与安全

- 每次请求先 owner-first 查询 Session 并执行 `SessionRead`；不存在和越权统一
  `404 SESSION_NOT_FOUND`；授权后未知 ID 返回 `404 SESSION_ASSET_NOT_FOUND`；
- 只接受 frozen runtime manifest 中可由浏览器安全表达的图片、音频和字体条目。生产摘要 ID 必须与
  entry digest 一致；重复摘要选择任一同字节 entry，但仍从该 SessionRoot 打开；
- 使用 protected Session container/root dirfd、逐段 `openat(O_NOFOLLOW)`、普通文件、owner/mode/
  link count 和打开后 identity 校验；不能按字符串 join 后 `File.Open`；
- MIME 来自服务端 allowlist 与文件 sniffing 的交集，不信任扩展名或游戏 manifest。至少覆盖实现矩阵
  中真实使用的 PNG、受支持音频和经批准字体；不支持类型产生稳定兼容错误；
- 响应设置 `X-Content-Type-Options: nosniff`、private cache、强 ETag/immutable content identity；
  同摘要可缓存，但认证响应不得进入共享公共缓存；
- 音频实现单 Range 和 `206/416`，拒绝多 range，设置 `Accept-Ranges: bytes`；图片/字体可完整流式读取；
- 活动 Session 允许只读资源访问。打开 fd 后 Worker 替换同路径不改变本次响应 identity；SessionRoot
  中与 frozen digest 不匹配的文件 fail closed，不把 Worker 修改的任意字节以内联资源返回；
- 资源端点有并发读取、响应字节和速率上限；请求日志只记 asset ID 摘要前缀，不记录路径。

### 6.3 旧 Session 与 manifest 演进

P1-11 前已创建的 Session 可能只有 schema v1 frozen entries。实现必须提供确定的只读适配：由 entry
digest 推导现有 `sha256-...` ID，并仅在 MIME sniff/路径类别满足 allowlist 时服务。字体 family 无法
确定映射时使用 ADR 批准的部署 fallback 并显示兼容诊断；不得修改或重建旧 SessionRoot。新建 Session
可写入新版 presentation catalog，但 open/reopen 仍只复用既有 frozen manifest。

## 7. Realtime connection 与恢复状态机

### 7.1 连接级状态

```text
DISCONNECTED
  -> CONNECTING
  -> HELLO_PENDING
  -> READY
  -> BACKING_OFF -> CONNECTING
  -> AUTH_REQUIRED | INCOMPATIBLE | DISPOSED
```

- URL 从当前同源 `http/https` 派生 `ws/wss`，不得从 Game 内容配置；必须协商
  `cloudemuera.realtime.v1`；
- socket 建立后先在服务端期限内发送一次 `client.hello`，只有收到并校验 `server.hello` 才进入
  READY；按 server heartbeat 对 ping 回 pong；浏览器不自行发业务 keepalive；
- 网络/1012 关闭使用 capped exponential backoff + jitter。1008 身份/权限和 1002 协议错误不无限重试；
- `online` 事件可以提前下一次尝试，`offline` 只改变展示，不关闭 Session；页面隐藏不暂停 Worker、
  prompt 或连接状态机；
- 一个 tab 只创建一个 socket，各 Console page 通过 subscription 使用；React StrictMode mount/unmount
  测试必须证明没有双连接或幽灵 timer。

### 7.2 Session 订阅状态

```text
IDLE -> RESUMING -> SNAPSHOT_READY -> LIVE
                  -> SNAPSHOT_RETRY
LIVE -> RESYNCING -> RESUMING
LIVE -> STREAM_ENDED
any  -> FORBIDDEN | SESSION_STOPPED | UNSUBSCRIBED
```

- resume 只发送 `capabilityDigest` 和可选 `lastEpoch`，不发送 lastSequence 并期待历史补发；
- 当前 API 在首个 Snapshot 尚未生成时保持订阅，Worker 首个 display batch 到达后直接发送；若兼容旧 peer
  返回 `SNAPSHOT_NOT_READY`，按独立短退避重试，保持“Worker 正在准备显示”状态，不重新 open Session；
- Snapshot 到达后验证 envelope `workerEpoch/sequence` 与 payload 一致，再一次性替换 store；
- Batch 只有在 epoch 相同、`firstSequence == currentSequence + 1`、内部 transaction 连续且
  `lastSequence` 一致时才可归约；
- `resync.required`、epoch 变化、序号缺口、decoder/reducer 失败时立即冻结旧画面并显示重同步 overlay，
  unsubscribe/resume 取得新 Snapshot；不得继续在错误基线上应用增量；
- `session.stream.ended` 释放订阅和 media，失效 Session detail，再根据最终 HTTP state 展示 CLOSED/
  CRASHED；不能把 socket 结束直接写成 Session 状态。

### 7.3 多客户端与多 tab

每个 tab 独立连接并取得相同 Snapshot。UI 明示“其他设备也可回答，首个有效输入生效”；收到
`STALE_PROMPT`、`NO_ACTIVE_PROMPT` 或 Snapshot 中 prompt 已变化时，把本地 pending 尝试收敛为已失效，
不提示用户其值已被执行。P1-11 不做 BroadcastChannel 主 tab 选举，也不在浏览器间共享 input ID。

## 8. Console store 与原子 reducer

### 8.1 Store 最小模型

```text
sessionId
workerEpoch?
sequence?
consoleState?
phase: idle/resuming/live/resyncing/ended/error
pendingInput?
lastReceipt?
clockSample?
fatalRenderError?
```

Snapshot 是完整值，不把 scrollback、scene、prompt 分散到多个互相可能不同步的 React state。reducer
在临时 draft/不可变副本上应用一个 transaction；全部 operation 成功后才提交并推进 sequence，任一
失败保留旧状态并触发 resync。

### 8.2 operation 覆盖

reducer 必须穷尽处理：

- `appendNodes`/历史兼容操作、`clearConsole`、`clearScrollback`；
- `appendLine`、`appendInline`、`replaceLine`、`deleteLines`；
- `setWindowMetadata`；
- background upsert/remove/clear；
- drawable upsert/remove/range clear/all clear；
- hit region upsert/remove/clear；
- media channel set/stop/all stop；
- prompt open/close。

未知 ID 的收敛语义必须与 RuntimeAdapter reducer 一致；不能让前端自行决定是 no-op 还是错误。建议把
RuntimeAdapter 的 golden property cases导出为 JSON 向量，在 C# 与 TypeScript 两边运行相同初态、操作
和终态断言。

### 8.3 浏览器内存边界

- store 只持有一份当前完整状态和一个 pending input，不保存所有 transaction、旧 Snapshot 或 console
  export history；
- keyed DOM 使用稳定 line/node identity，scrollback 更新后只滚动新增区域；用户主动向上滚动时不抢
  scroll，提供“回到最新”；
- Raster base64 只在当前 drawable 生命周期内解码，替换/清除/epoch 变化时释放 Blob URL/ImageBitmap；
- asset fetch 由浏览器 HTTP cache 和有界 resolver map 复用，失败项有短负缓存，页面卸载取消请求；
- animation frame、countdown、ResizeObserver 和 AudioContext 均有显式 dispose 测试。

## 9. Renderer 设计

### 9.1 总体分层

同一 `consoleState` 组合为：

```text
Console viewport
├── authorized background layers
├── Canvas 2D scene（drawable + hit region）
├── accessible DOM scrollback（line + node + safe HTML AST）
├── prompt/input layer
└── media permission/status controls
```

现有“现代/兼容”若保留，只能改变 shell、密度和缩放策略，必须共享同一个 store/reducer/renderer，
不能存在一套完整、一套降级的语义。默认使用 Worker `windowMetadata.viewportWidth/height` 作为逻辑
坐标系，通过 letterbox/等比缩放适配容器；pointer 在提交前逆变换回逻辑像素并 clamp 到 viewport。

### 9.2 DOM scrollback

- 每个 `ConsoleLine` 使用稳定 `lineId`，按 left/center/right 和 temporary 属性渲染；
- TextNode 只作为 React text child，颜色转固定 `rgba()`，font size/line height 先范围检查后设置 CSS
  custom property；装饰枚举映射固定 class；
- ButtonNode 使用原生 `<button type="button">`，保留 label nodes、enabled、tooltip、focus 和 generation；
  只有当前 prompt 允许 BUTTON 时才提交 value；
- Image/Sprite inline node 使用授权 asset endpoint、source rect 和 destination box。裁剪使用受控容器/
  canvas，不把 assetId 写成任意 URL；decorative 使用空 alt，其余必须保留 alt；
- HtmlIsland 递归映射 `text/break/element` 的固定 tag 表和固定 style 字段，禁止 spread 任意 attribute、
  `innerHTML`、链接导航和事件属性；含 asset 的 element 仍走 AssetResolver；
- Shape inline node 用受控 SVG 或 Canvas primitive；若采用 SVG，只创建固定 element/number/color 属性，
  不接受 SVG 字符串、foreignObject、URL paint 或 style 文本。

### 9.3 Canvas/background/CBG

- 按 `zIndex + stable id` 的冻结排序绘制 Sprite、Shape、HtmlIslandDrawable 和 RasterDrawable；
- background mode 精确实现 stretch/contain/cover/center/repeat，depth/opacity 有界；
- Sprite 使用 source rect、bounds、opacity、frame 和 animation frames；时间推进只改变呈现帧，不修改
  realtime store 的权威 revision；Snapshot/replace 后重建 animation clock；
- Shape 覆盖 rectangle/ellipse/line/polygon/space，使用逻辑像素和固定 RGBA；
- HtmlIslandDrawable 可用绝对定位的安全 DOM overlay 与 Canvas 同坐标系，不 rasterize raw HTML；
- Raster 先校验 PNG signature、解码错误和尺寸，再创建 ImageBitmap；hover 数据与 hit region 只影响
  当前交互显示；mapping/hit-test map 不直接暴露像素给脚本；
- HitRegion 负责 pointer hover/click、tooltip、enabled 和 input value。只有当前 prompt 允许 POINTER 时
  提交；重叠区域使用与 scene 相同的稳定 z-order。

### 9.4 字体

- `presentation-manifest.fonts` 只允许逻辑 family、授权 font asset 和固定 generic fallback；服务端按字体文件 stem
  确定 family，发生缺失/碰撞时使用 asset 摘要派生的独立 family 并返回诊断码；浏览器只引用 session-scoped
  `cssFamily`，不能把逻辑名直接解释成 CSS family；
- 使用 `FontFace` 从同源授权 API 加载，成功后注册在 Session scoped CSS family 名下；不能把逻辑名
  直接解释成任意系统字体；
- 加载失败显示兼容警告并使用清单 fallback，不阻塞文本可读性；需要精确测量的 fixture 将 fallback
  视为视觉差异并在 P1-15 阻断；
- 覆盖 CJK、全角/半角、组合字符、emoji、粗斜体、固定行高、对齐和按钮 hit box。

### 9.5 WebAudio

- 一个 Console page 一个惰性 `AudioContext`，channel 以 `(channel, revision)` 管理；新 revision 完成
  前旧异步 fetch/decode 不得迟到覆盖；
- `requested` 加载授权 asset，按 loop/volume 连接 GainNode；`stopped`、stop channel、stop all、
  Snapshot 替换、stream ended 和页面卸载都确定释放 source/gain；
- `immediate` 先尝试播放，若浏览器 autoplay 阻止则进入“等待用户启用声音”；`onUserGesture` 从开始
  就等待手势。用户手势只解锁浏览器播放，不向 Worker回传“播放成功”并不改变 Runtime；
- 提供静音/启用声音控件和可访问状态。页面后台 timer throttling 不影响 Worker，只允许音频实际播放
  出现浏览器平台限制；
- decode/MIME/网络失败显示非敏感兼容错误，不无限重试、不把 asset 路径写入日志。

## 10. Prompt、输入与 deadline

### 10.1 控件映射

| inputType | UI | 可发送来源 |
| --- | --- | --- |
| `enterKey` | 聚焦 console，Enter/确认按钮 | KEYBOARD/BUTTON，服从 allowedSources |
| `anyKey` | 捕获受限 keydown，显示按键等待 | KEYBOARD |
| `integer` | `inputmode=numeric` 文本框，显示 min/max | KEYBOARD |
| `text` | 文本框/移动软键盘 | KEYBOARD |
| `anyValue` | 文本框，不在浏览器猜测数值分支 | KEYBOARD |
| `integerButton`/`textButton` | 结构化 ButtonNode | BUTTON；若允许也可显示手工输入 |
| `primitivePointerKey` | Canvas hit region/受限键盘 | POINTER/KEYBOARD |
| `waitOnly` | 固定底部输入栏保留禁用文本框和回车键，不显示等待文案 | 无浏览器提交 |

System input 保留给 Worker 的运行时语义，但不再因此隐藏浏览器交互：`integer`、`text`、`anyValue`、
`anyKey` 等可表达类型使用同一套受约束结构化控件，`allowedSources` 是 UI 前置约束但 Worker 仍最终校验；
`waitOnly` 仍不可提交；固定底部输入栏只保留禁用的输入框和回车键，使用控件颜色表达当前是否可提交，不能
代替 Worker 的输入状态。OneInput 可以在客户端限制一个 Unicode scalar/上游规定格式以改善体验，但不能代替
Worker normalized/invalid receipt。

### 10.2 提交流程

1. 从 store 原子读取当前 epoch/prompt 和 allowed source；
2. 第一次提交生成 `clientMessageId` 和 envelope `messageId`，保存 payload fingerprint/pending 状态；
3. 在收到确定回执前禁用产生不同值的重复提交；socket 中断后不自动重放；
4. 重连 Snapshot 若仍是同一 epoch/prompt，显示“结果未知，可重试”，重试复用原
   `clientMessageId` 和完全相同 payload，但 envelope `messageId` 必须新建；
5. Snapshot prompt 已变化则把 pending 标为失效且不重发；
6. `ACCEPTED/DUPLICATE` 显示已提交并等待 ClosePrompt；`CONFLICT` 为客户端错误；
   `STALE_PROMPT/NO_ACTIVE_PROMPT` 收敛当前控件；`INPUT_BACKPRESSURE/WORKER_UNAVAILABLE` 提供同 ID
   手工重试；`FORBIDDEN/AUTHENTICATION_EXPIRED` 退出连接。

UI 和日志默认不回显提交全文。密码类 Game 输入没有协议级敏感标志，因此本阶段至少不把 input value
写入 telemetry、URL、localStorage、错误边界和 test snapshot。

### 10.3 deadline 显示

- 以 `server.hello.serverNowUnixMilliseconds` 和本地 monotonic sample 建立显示偏移；后续 ping 可平滑
  更新，但不能让倒计时向后大幅跳跃；
- 使用 Snapshot/prompt 原始 `deadlineUnixMilliseconds`，重连不加 timeout duration；
- 页面用 monotonic `performance.now()` 驱动低频显示，后台恢复时重新按 deadline 计算，不逐秒发网络消息；
- 本地剩余时间到零后显示“时间已到，等待游戏确认”，保留 prompt disabled 状态，直到 Worker transaction
  关闭/替换；不能本地插入 timeoutMessage、默认值或 ClosePrompt；
- wall clock 偏移、后台 timer throttling 和设备时间跳变测试必须证明 Worker 结果不受影响。

## 11. Session 生命周期 UI

### 11.1 列表与详情

- 列表分页使用后端 cursor，支持已有 game/state filter；不在客户端一次性取全量；
- 展示 name、Game、source digest 短摘要、state、created/last activity、waiting input；不展示 root path、
  PID、endpoint 或 manifest JSON；
- `CREATING/STARTING/STOPPING` 使用有限轮询并在页面失焦时降频；RUNNING 进入 Console 后以 realtime 为
  显示权威，但 HTTP detail 仍负责生命周期状态；
- CLOSED 提供 open/存档，CRASHED 显示冷重开说明和风险，不把“继续”描述为指令级恢复。

### 11.2 创建并开始

- 表单从真实可创建 Game query 读取，BLOCKED/无 current content 禁用并解释；
- create 使用 key A。`201` 后得到 Session；`202` 按 Location/detail 轮询到 CLOSED 或稳定失败；
- 只有 create 已成功才用 key B 调用 open。open `202` 轮询到 RUNNING/CRASHED；
- create 失败不调用 open；open 失败保留已创建的 CLOSED/CRASHED Session，并提供重试 open 或查看详情；
- route 重载可以从 URL/HTTP state恢复，不依赖 React component 中未持久的“正在创建”布尔值；原请求
  未知时由同 key 重试，而不是再创建 Session。

### 11.3 close/reopen

- close 必须二次确认“终止 Worker、保留 SessionRoot、不能恢复当前指令”；提交后立即禁用输入并等待
  服务端 `STOPPING → CLOSED/CRASHED`，但不能在客户端提前伪造 CLOSED；
- socket stream ended 与 HTTP 状态并行收敛；close `202` 轮询，失败显示 requestId；
- CLOSED/已回收 CRASHED 的 reopen 使用新幂等 key；RUNNING no-op 不增加 epoch；
- reopen 后 realtime 必须收到更大 epoch 的新 Snapshot，并清除旧 pending input、media、Raster 和
  animation；不得合并旧 console。

## 12. 存档界面

### 12.1 查询与下载

- `/saves` 支持从 Session 列表选择，或使用 `/sessions/{id}/saves` 深链；建议最终采用后者并保留导航；
- TanStack Query 缓存 key 至少包含 sessionId。响应显示逻辑 path、kind、size、modifiedAt 和 layout；
- 下载用同源 API 触发，并保留服务端 `Content-Disposition`；不能把全文件读入 JS state 或 realtime store；
- 活动态允许 list/download，并明确这是非事务性视图。

### 12.2 上传/替换、重命名和删除

- 只有 `CLOSED` 或已确认可修改的 `CRASHED` 页面启用 mutation；真正竞争仍由服务端 mutation lease
  裁决，按钮状态不是授权；
- 上传采用单文件 picker、允许逻辑路径、大小预检和进度/取消展示。服务端进入已提交副作用后客户端
  cancel 只停止等待；同 key 查询/重试取得最终结果；
- 目标存在时必须使用明确“替换”确认，不以普通上传静默覆盖；
- rename 使用服务端逻辑路径规则，展示 `SAVE_ALREADY_EXISTS`、非法名称和 operation recovery 错误；
- delete 对具体 path 显示不可撤销确认并发送服务端确认字段/header；成功后失效 saves list；
- open 与 mutation 竞争失败时按稳定 code 提示“Session 已开始运行”，刷新 Session detail/saves，不能
  乐观宣称文件已改；
- CRASHED 状态提示存档可能处于原生 writer 崩溃现场，下载/修改不等于格式有效。

## 13. 移动端、可访问性和浏览器安全

### 13.1 移动交互

- 使用 `100dvh`、`env(safe-area-inset-*)` 和 VisualViewport 处理地址栏/刘海/软键盘；console header、
  prompt 和最新输出在键盘弹出时仍可达；
- 触摸 target 至少 44×44 CSS px；pointer event 统一 mouse/touch/pen 并转换逻辑坐标；
- 输入聚焦不得导致 Canvas 错位；屏幕旋转/resize 保留 store 和用户 scroll anchor；
- 页面退到后台或网络切换只改变连接展示，不触发 close/open，不重置 deadline。

### 13.2 可访问性

- scrollback 使用可读文档/日志语义，但高频输出不能把整个历史设为 assertive live region；仅 prompt、
  连接、错误和输入回执使用节制的 `aria-live`；
- 所有游戏 Button、hit region 替代控件、媒体开关、close/save dialog 可键盘访问并有可见 focus；
- Canvas 提供当前可交互 hit region 的 DOM 等价按钮列表；纯视觉 drawable 使用合理 alt/隐藏语义；
- dialog 捕获并恢复焦点，Escape 取消非破坏动作；删除/close 不以颜色作为唯一警告；
- foreground/background 对比来自游戏语义时不擅自改色，但应用 shell、状态、错误和 focus 满足 WCAG
  基础要求；提供高对比 shell，不改变游戏状态。

### 13.3 CSP 与注入防护

production policy 至少显式收窄 `script-src 'self'`、`style-src 'self'`、`connect-src 'self' wss:` 的
实际同源形态、`img-src 'self' blob:`、`media-src 'self'`、`font-src 'self'`，并继续禁止 object/frame/
base/form 扩张。精确值由新增 ADR 和生产 bundle测试冻结；开发 HMR 独立配置。若 Canvas Raster 使用
Blob，Blob 只能来自已验证的实时 PNG bytes，生命周期由 renderer 管理；不能因此允许 `data:` 或外部源。

## 14. 错误、可观测性与用户反馈

- 页面按稳定 code 分为：认证/授权、生命周期冲突、容量/背压、协议不兼容、资源兼容、网络暂时失败、
  服务端故障；未知错误显示安全通用文案和 requestId；
- 连接状态至少区分连接中、获取 Snapshot、实时、重同步、连接中断、Session 已结束、身份失效；不能用
  一个真假 `connected` 掩盖所有状态；
- renderer 遇到未知 Supported node/operation 或授权资源摘要不匹配时显示 session-scoped 阻断面板并
  请求 resync；重复 Snapshot 仍失败则停止订阅，避免错误循环；
- 浏览器日志不记录 input value、Cookie、CSRF、完整 save path、raw HTML、Raster bytes 或资源物理路径；
- P1-12 才增加服务器诊断 UI。P1-11 只消费已有 requestId/reasonCode，不创建独立遥测平台。

## 15. 测试设计

### 15.1 协议、codec 与 reducer 单元测试

- 所有 server/client envelope golden、未知 discriminator、缺字段、重复/超限/非有限值和 capability mismatch；
- Snapshot 完整替换、epoch 增长清空旧状态、连续 batch、缺口、重复/倒序 batch、transaction 原子失败；
- 每个 operation 正常、边界、未知 ID 和主要失败路径；与 C# reducer 共享 fixture 终态；
- `resync.required` 后旧 batch 不再改变画面，替代 Snapshot 后从新 sequence 继续；
- StrictMode 双 mount、socket dispose、timer/Blob/ImageBitmap/AudioContext 清理和有界缓存。

### 15.2 renderer 组件测试

- Text/style/alignment/temporary line、Button/tooltip/enabled、Image/Sprite/source rect/hover/animation；
- background 五种 mode、所有 Shape、Raster normal/hover、hit region z-order、HTML AST 全 tag/style allowlist；
- 危险 tag/attribute/URL、未知 node、坏 PNG、资源 404/digest mismatch、字体 fallback 均明确失败；
- Media requested/stopped/revision race/loop/volume/autoplay blocked/user gesture/decode failure/stop all；
- 全部 input type、allowed source、OneInput、default 只展示不代提交、pending retry same ID、receipt 状态；
- deadline、wall clock 跳变、后台节流模拟、本地到零等待 Worker；
- 键盘导航、focus restore、ARIA name/live region 和 Canvas hit region 等价控件。

### 15.3 HTTP/API 集成测试

- Session list/create/open/close/reopen UI 所需 DTO/OpenAPI 不漂移；
- asset manifest/stream 的跨用户、跨 Session、未知 ID、digest mismatch、symlink/hardlink/TOCTOU、MIME
  sniff、Range、ETag、活动态读取和响应 header；
- saves UI 对应 list/download/mutation、活动态拒绝、open/mutation 竞争、幂等重放、删除确认和错误码；
- CSP/security headers、Origin、Cookie 失效和强制改密后的实时关闭。

### 15.4 Playwright 纵切

新建 `scripts/test-session-ui-e2e.sh`，使用临时目录、独立 `.env`、Compose project、端口和 bootstrap
账户，退出时清理；不得读写人工 `./data`。至少覆盖：

1. desktop Chromium：登录→选择已启用 Game→创建→open→Snapshot→普通输入→输出→close→存档下载→reopen；
2. timed prompt：开始倒计时→断开/刷新→原 deadline 剩余时间→本地到零不提交→Worker timeout 后继续；
3. rich fixture：文本/样式/行替换、Button、HTML、图片/Sprite、背景、Shape/CBG、动画、音频手势；
4. 两个 browser context 同看一 Session 并发回答，只有一个 ACCEPTED，另一端收敛；
5. mobile Chrome/WebKit viewport：软键盘、safe area、触摸 Button/Canvas、旋转和网络切换重连；
6. 越权用户不能 resume、读 asset、下载或修改 save；
7. Worker crash→CRASHED→同 Session reopen epoch 增长，旧画面/input/media 不残留。

P1-11 要求 Chromium desktop/mobile 和至少一个 WebKit mobile smoke；Firefox/WebKit desktop 的完整
矩阵、授权代表游戏和像素阈值基线在 P1-15 完成，但本阶段不得以此为由跳过 renderer component fixture。

### 15.5 性能与视觉基线

- 以最大允许 Snapshot fixture 测 decode/reducer/render，不把整棵树重复 JSON stringify 或深拷贝；
- 持续小 transaction 下输入控件仍可响应，用户向上滚动时不抖动；
- 首个 Snapshot 可见时间记录并验证正常开发环境目标，最终 NFR-001 P95 在 P1-15；
- 为 CJK/字体/行高、Sprite crop、background、Shape 和 mobile safe area 生成稳定 screenshot fixture；
  P1-11 建立基线，P1-15 扩展四浏览器容差。

## 16. 实施切片

### 切片 1：冻结浏览器资源与生成契约

- 新增 ADR：Session asset identity/presentation manifest、旧 Session 适配、MIME/Range/cache/CSP；
- 完整化 realtime JSON Schema，接入 OpenAPI/realtime TypeScript 生成和 drift check；
- 用 golden fixture 证明 C# serializer→schema→TS decoder 一致；
- 更新 requirements/design 中“资源经授权 API”落点和 P1-11 边界。

通过门：没有 `Record<string, unknown>`、手写漂移 DTO 或未决 asset/font URL 方案。

### 切片 2：授权 Session asset API

- 实现 presentation manifest 和 asset stream Application port/Infrastructure/API；
- 接入 frozen manifest、protected dirfd、MIME sniff、digest、Range/ETag/cache/security headers；
- 覆盖旧 Session、跨用户、链接/TOCTOU、活动 Worker 和资源替换故障。

通过门：真实 fixture 的图片、字体和音频可只凭 `sessionId + assetId` 同源取得，任意路径不可达。

### 切片 3：Session HTTP feature 与页面拆分

- 建立 QueryClient、generated HTTP client、Session hooks/list/detail/create/open/close；
- 拆出占位 Session 页面，完成两阶段 create→open、202 polling、close/reopen/error UX；
- 保持 P1-04 Game 页面回归，移除越界文件写/搜索入口和假数据。

通过门：刷新页面后所有 Session 状态由 API 恢复，StrictMode 不重复命令。

### 切片 4：Realtime connection、store 和 reducer

- 实现单 socket、hello/ping/pong、resume/unsubscribe、重连和身份/协议终态；
- 实现完整 Snapshot replace、transaction reducer、epoch/sequence/resync 和 pending receipt；
- 使用共享 golden/property fixture 穷尽所有 operation。

通过门：断线、缺口、overflow resync、reopen epoch 和未知 operation 都不会污染旧基线。

### 切片 5：DOM、HTML、输入与 deadline

- 实现 scrollback line/node、安全 HTML AST、Button、文本输入、键盘和 prompt controller；
- 实现全部 input type/source、same-ID retry、receipt UX、deadline clock；
- 完成键盘导航、live region、focus 和基础移动软键盘布局。

通过门：普通/计时/OneInput/多客户端输入由 Worker 唯一裁决，客户端到零不产生输入。

### 切片 6：Canvas、资源、字体、动画和 WebAudio

- 实现 background/scene/drawable/hit region、坐标变换和移动 pointer；
- 接入 AssetResolver、FontFace、Raster 生命周期、Sprite animation；
- 接入 MediaController、autoplay 手势、revision race 和 stop/dispose；
- 完成 CSP 和注入/资源失败测试。

通过门：P1-07 能力矩阵中每个 Supported visual/media 项都有组件 fixture 和失败态，无静默 no-op。

### 切片 7：存档 UI

- 接入 list/download/upload/replace/rename/delete 和 Session 深链；
- 完成活动态锁定、CRASHED 提示、进度、幂等、确认和 query invalidation；
- 覆盖 open/mutation 竞争和文件 operation recovery 错误。

通过门：停止态修改和活动态只读语义清晰，浏览器不缓存文件正文或绕过服务端 lease。

### 切片 8：移动、e2e、文档和交接

- 建立隔离 `test-session-ui-e2e.sh` 和 desktop/mobile 场景；
- 完成 safe area、VisualViewport、触摸、旋转、网络切换和基础 a11y scan；
- 更新设计、兼容性报告、开发计划、OpenAPI/schema 版本、第三方声明和 P1-15 交接；
- 运行完整质量门。

通过门：本方案完成定义全部满足，剩余仅是明确交给 P1-15 的四浏览器/代表游戏发布验收。

## 17. 验证命令

所有命令必须使用仓库 dev Docker 和宿主 UID/GID：

```bash
./scripts/dev-up.sh

source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm web \
  sh -c 'pnpm install --frozen-lockfile && pnpm verify:contracts && pnpm typecheck:web && pnpm test:web && pnpm build:web'

docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release \
  --filter 'Category=SessionAssets|Category=SessionLifecycle|Category=SessionSaves|Category=Realtime'

./scripts/test-session-ui-e2e.sh
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check

./scripts/dev-down.sh
```

若 `test-session-ui-e2e.sh` 为保持隔离自行管理 Compose，则不得依赖先运行的 `dev-up.sh`，并必须像
`test-identity-e2e.sh` 一样使用临时 DataRoot/环境/项目名及 trap 清理。

## 18. 完成定义

P1-11 只有同时满足以下条件才可标记实现完成：

1. Session/Console/Saves 正式页面无演示数组、假连接、假计时或本地伪状态，登录/Game 既有流程回归通过；
2. HTTP 与 realtime 契约由 OpenAPI/schema 生成或机器校验，Snapshot/Batch 不再是 unknown record；
3. Snapshot replace、连续 batch、transaction 原子性、epoch fencing 和 resync 在 TS 与 C# 共享 fixture
   上一致；
4. P1-07 能力矩阵的全部 Supported text/layout/button/HTML/image/Sprite/background/Shape/CBG/
   animation/audio 项都有实际 renderer、正常/边界/失败组件测试，无 `innerHTML` 或静默降级；
5. 全部 input type/source、OneInput、same-ID retry、多客户端竞争、close/input 和 timeout race 的 UI
   行为不越过 Worker 权威；
6. deadline 使用服务端基准，刷新/断网/后台/客户端时钟跳变不重置或代替 Worker timeout；
7. 图片、字体和音频只经按 Session 授权 API，跨用户、路径、链接、digest、MIME、Range/CSP 测试通过；
8. save list/download/mutation、活动态拒绝、CRASHED 提示、确认、幂等和 open 竞争体验完整；
9. 桌面与移动 viewport 的软键盘、安全区、触摸、键盘、focus、ARIA 和基础 a11y scan 无阻断错误；
10. 连接、store、Raster、asset、animation、timer 和 audio 均有界且可释放，慢/坏客户端不影响 Worker；
11. 隔离 Playwright 纵切通过，`./scripts/check.sh`、dev user、third-party 和 `git diff --check` 通过；
12. 文档、schema/OpenAPI、CSP、配置、lockfile、许可证及 P1-15 验收交接同步更新。

以下项目明确留给 P1-15，不能反过来削减 P1-11 的实现范围：

- 授权代表游戏的完整长流程与四浏览器 desktop/mobile 发布矩阵；
- 跨浏览器像素差异阈值、音频设备/策略矩阵和最终 NFR-001 P95 证据；
- MVP 全阶段统一故障注入、备份恢复和生产镜像验收。

## 19. 后续任务交接

- P1-12 复用 Session HTTP 状态和 requestId，不从 Console socket 增加 force-stop 或诊断私有消息；
- P1-13 把 asset 并发/字节、browser queue 和 Snapshot 上限纳入实例配置验证，但不能改变本文恢复正确性；
- P1-14 production CSP、静态 SPA 和单容器进程编排必须保留同源 asset/WebSocket 路径；
- P1-15 使用本文 fixture、e2e 脚本和 screenshot 基线扩展四浏览器、代表游戏与最终验收，不重新定义
  renderer、timeout、asset identity 或输入一致性。
