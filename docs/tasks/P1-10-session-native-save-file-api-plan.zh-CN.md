# P1-10：Session 原生存档文件 API 详细开发方案

状态：实现完成；最终验收待 P1-15

设计日期：2026-08-14

对应开发步骤：`P1-10 — Session 原生存档文件 API`

关联需求：AUTH-001～003、SAVE-001～010、SAVE-013～016、OPS-002/004/005、SEC-005/008/009、
NFR-004/006/011/013、AC-004、AC-007、AC-013、AC-014

关联决策：[`ADR-0007`](../adr/0007-session-root-native-save-ownership.md)、
[`ADR-0016`](../adr/0016-reopenable-session-root-lifecycle.md)、
[`ADR-0017`](../adr/0017-trusted-self-hosted-mvp-simplification.md)、
[`ADR-0022`](../adr/0022-session-save-file-cap.md)

前置任务：P0-05、P1-01～P1-06、P1-S01

后续任务：P1-11 浏览器存档界面、P1-13 实例级容量上限、P1-14 备份恢复、P1-15 MVP 验收

## 1. 目标结果

P1-10 在 P1-06 已完成的持久 SessionRoot、WorkerLease 和停止态 mutation lease 上建立正式存档
HTTP 纵切。它不是新的存档系统，而是对 SessionRoot 中 Emuera 原生文件的授权、安全文件访问：

1. Session owner 可以列出和下载自己的原生存档；下载统一经过 API 代理，不暴露物理路径或签名 URL。
2. 只有 Session 处于 `CLOSED`，或已完成旧 Worker 回收屏障的 `CRASHED` 时，才允许上传/替换、
   重命名和删除；修改权与 open 在 SQLite 中线性化，任何时刻最多一个 SessionRoot 写者。
3. 根目录和 `sav/` 两种布局只由 Session 创建时复制的 `emuera.config` 决定。API 使用逻辑存档路径，
   不允许调用者选择、同时搜索或越过物理布局。
4. 上传在 SessionRoot 外流式暂存，执行大小、路径、普通文件和基本原生格式检查，再通过 dirfd 下的
   原子 rename 发布；不整体缓存文件、不解析游戏语义、不转换格式。
5. 上传、重命名和删除具有持久 operation、幂等回放和崩溃恢复。API/HTTP 中断不能留下未知文件发布
   与已过期 lease 并发，也不能因恢复器猜测而覆盖用户存档。
6. 原生文件继续是唯一权威内容。不新增 `SaveArtifact`、generation、存档内容表、历史版本、跨 Session
   复制 API、退出提交或 Game digest 兼容证明。

## 2. 范围与非目标

### 2.1 本阶段必须实现

- list、download、upload/replace、rename、delete 五类 HTTP API；
- 版本化 DTO、OpenAPI、稳定错误码、认证、CSRF、资源授权和删除确认；
- 与 Runtime 共用的逻辑存档路径策略，以及当前 Session save layout 的安全解析；
- 基于受保护 Session container/root dirfd 的枚举、打开、暂存、原子发布、重命名和删除；
- 原生二进制、压缩二进制、旧文本 `.sav` 和 `sav/` 原生辅助文件的有界基本格式检查；
- `save_file_operations` 持久状态、通用幂等记录接入、mutation lease 续租和恢复器；
- open 与所有修改操作的竞争、API kill、磁盘满、SQLite/fsync 故障和恶意路径自动化测试；
- 两种真实 Emuera 布局的保存、关闭、下载、删除/上传、重开和原生加载验收。

### 2.2 明确不做

- 不建立 Save、SaveArtifact、generation、slot metadata 或文件摘要索引表；operation 表只记录管理命令，
  不能成为存档内容目录；
- 不提供 Session 间 copy/move、批量导入导出、ZIP 存档包或跨服务器迁移格式；
- 不提供版本历史、回收站或单文件回滚；历史恢复由静止 SessionRoot/`/data` 外部备份承担；
- 不验证存档中的 Game unique code、脚本版本或变量结构与 Session source digest 兼容；
- 不在 API 中加载 Emuera Runtime，不调用完整原生 deserializer，不改写、压缩或重新保存内容；
- 不提供活动 Worker 文件修改。list/download 在接受非事务性读取语义后可以访问活动 Session；
- 不提供通用 SessionRoot 文件浏览、目录管理或配置/ERB/CSV 修改；
- 不实现 P1-11 的 React Query、上传进度、确认对话框和移动端 UI。

## 3. 已有基线与实施前缺口

### 3.1 可直接复用

- P0-05 的 `EmueraSaveLayoutInspector` 和 `RuntimePaths` 已定义根目录与 `sav/` 物理布局，并覆盖原生
  `save*.sav/global.sav`、`txtN.txt`、`imgN.png` 和安全目录段；
- P1-06 的 protected `metadata/session-root.json` 已绑定 Session、owner、Game/source、layout 和 root
  device/inode；`SessionRootRuntimeInspector` 已能在 open 前交叉核对 DB、marker、root 和 config；
- `ISessionRootMutationLeaseStore` 已定义停止态写租约，`SqliteSessionRuntimeStore` 的 open 事务已检查
  mutation lease；
- P1-02 的 Cookie/CSRF、`IResourceAuthorizer`、隐藏式 `404`、审计上下文和 request correlation 可复用；
- `LinuxFileOperations` 已提供受保护目录遍历、`openat(O_NOFOLLOW)`、普通文件 identity、`renameat`、
  `unlinkat` 和 `fsync` 基础能力；
- 通用 `idempotency_records` 已支持 `IN_PROGRESS/SUCCEEDED/FAILED`、安全响应回放和启动恢复。

### 3.2 必须先修正的缺口

1. 当前 `SqliteSessionRootMutationLeaseStore.TryAcquireAsync` 会在获取事务内直接删除过期 lease。
   P1-06 设计要求只有核对 staging/operation ownership 的恢复器才能接管或释放。P1-10 必须取消普通
   获取者的无条件过期回收，否则暂停或重启中的文件发布可能与 open/新修改并发。
2. mutation lease 目前没有对应的持久 save operation，无法区分“尚未写文件”“已暂存”“rename 已发生
   但 DB 未提交”。必须增加前滚 migration 和恢复矩阵，不能把 `expires_at` 当成操作已结束的证明。
3. `RuntimePaths.IsAllowedSaveFileName/IsAllowedSaveDirectorySegment` 是 internal Runtime 实现细节。
   应抽取不依赖物理路径的公共 `EmueraSavePathPolicy`，供 Runtime 和 Save API 共用，避免规则漂移。
4. `LinuxFileOperations.RenameAt` 默认允许 POSIX 覆盖。用户 rename 应采用 no-replace 语义，需要增加
   `renameat2(RENAME_NOREPLACE)` 封装；上传/替换则明确使用原子 replace 语义。
5. 实例级存档单文件默认上限仍是需求待决项。切片 1 必须以新 ADR 冻结默认值，或将配置设为部署必填；
   不得在实现中静默使用 SessionRoot 总上限或临时常量。

上述变更不改变 SessionRoot 所有权、状态机或原生格式边界。若实施中需要允许任意 `sav/` 文件名、
活动 Worker 修改、创建存档历史、跨 Session 复制或在 API 中完整反序列化，必须先新增 ADR。

## 4. 核心不变量与线性化点

### 4.1 持久不变量

| 不变量 | 裁决事实 |
| --- | --- |
| 存档唯一权威副本位于 SessionRoot | 文件系统；DB 不保存内容或 generation |
| 物理布局不能由 API 调用者选择 | protected marker + copied `emuera.config` + runtime manifest snapshot |
| 一个 Session 同时最多一个修改操作 | `session_root_mutation_leases.session_id` 主键 |
| Worker 与管理写者互斥 | open 与 mutation acquisition 在各自 `BEGIN IMMEDIATE` 中交叉检查 |
| 旧 Worker 未确认退出时不能修改 | Session 静止状态 + 无 WorkerLease + recovery barrier |
| HTTP/API 中断不丢失已线性化命令 | `idempotency_records` + `save_file_operations` |
| 越权不能枚举 Session 或文件 | owner-first repository query + 统一 `SESSION_NOT_FOUND` |
| 文件访问不依赖字符串前缀 | protected dirfd + 逐段 `openat(O_NOFOLLOW)` |

### 4.2 线性化点

| 用例 | 线性化点 | 后续行为 |
| --- | --- | --- |
| list | 每个条目成功打开并读取 identity | 允许活动 Worker 随后改变目录，结果是非事务性视图 |
| download | 成功打开并固定目标文件描述符 | 从该 fd 流式读取；路径随后替换不改变授权对象 |
| upload/replace | staging 到目标的原子 rename | operation 必须提交成功或由恢复器识别已发布事实 |
| rename | source identity 到 target 的 no-replace rename | source 消失且 target identity 匹配即视为已执行 |
| delete | 对已验证 source identity 执行 `unlinkat` | source 消失即视为已执行；审计/幂等由恢复器补齐 |
| open/mutation | WorkerLease 或 mutation lease 的 SQLite 事务提交 | 先提交者获写权，另一方稳定失败 |

请求取消只传到幂等/operation/mutation lease 提交前。进入 `STAGED` 或取得 mutation lease 后，文件发布、
fsync、状态收敛和 lease 释放使用 operation/host token；HTTP 等待超时或断开不得取消已提交副作用。

## 5. 公开 HTTP 契约

### 5.1 通用约定

- 基础路径：`/api/v1/sessions/{sessionId}/saves`；所有端点要求认证，修改端点要求 CSRF；
- upload/replace、rename、delete 要求 `Idempotency-Key`，规则与 Session 命令一致；
- 客户端路径相对于当前逻辑 save root。物理布局为 `sav/` 时也不在公开 path 前添加 `sav/`；
- 路由捕获 `{**path}` 后只执行一次 URL decode，再交给统一路径策略；
- 不存在和越权 Session 统一 `404 SESSION_NOT_FOUND`；授权通过后的文件缺失返回
  `404 SAVE_NOT_FOUND`；
- 大文件上传/下载全程流式处理，不读取为 byte array 或 multipart 表单模型。

### 5.2 列表

```http
GET /api/v1/sessions/{sessionId}/saves
```

```json
{
  "schemaVersion": 1,
  "layout": "ROOT",
  "items": [
    {
      "path": "save00.sav",
      "kind": "NORMAL",
      "sizeBytes": 18291,
      "modifiedAt": "2026-08-14T10:00:00Z"
    }
  ]
}
```

- `layout` 为 `ROOT | SAV_DIRECTORY`；
- `kind` 为 `NORMAL | GLOBAL | AUXILIARY_TEXT | AUXILIARY_IMAGE`；
- 只返回允许策略内、成功以 no-follow 打开的普通单链接文件；
- 递归深度、文件数和响应估算字节有硬上限，超限返回稳定容量错误，不截断成似乎完整的列表；
- 排序固定为逻辑 path 的 ordinal 顺序；size/modifiedAt 是枚举当时观测值；
- 活动 Worker 期间允许列表。条目在响应后新增、消失或变化属于明确接受的非事务性语义。

### 5.3 下载

```http
GET /api/v1/sessions/{sessionId}/saves/{**path}
```

- 每次验证 Session owner，再校验逻辑 path 和物理 root；
- 使用已固定的只读 fd 流式读取，不在响应期间按字符串重新打开；
- `Content-Type: application/octet-stream`、`Content-Disposition: attachment`、
  `X-Content-Type-Options: nosniff`、private/no-store cache policy；
- 下载名只使用净化后的最后一段，不能把路径、CR/LF 或用户 MIME 放入 header；
- 活动 Worker 期间允许下载。正在覆盖的文件可能产生上游/文件系统当时可见内容，API 不声明事务性
  save generation。

### 5.4 上传或替换

```http
PUT /api/v1/sessions/{sessionId}/saves/{**path}
Idempotency-Key: <opaque-key>
X-CSRF-TOKEN: ...
Content-Type: application/octet-stream

<raw bytes>
```

- 不使用 multipart；请求体就是原生文件；
- Content-Length 缺失时仍按实际流计数，超过 `MaxSaveFileBytes` 立即停止并返回 `413`；
- 成功新建返回 `201 Created`，替换返回 `204 No Content`；
- 同键同目标同 payload digest 回放原结果；同键目标或 payload 不同返回
  `409 IDEMPOTENCY_KEY_REUSED`；
- 请求可能在完整上传并计算 digest 后才确定 replay。重复请求产生的临时 staging 必须安全清理；
- 目标不存在和存在都允许，原子 replace 是 PUT 的显式语义。

### 5.5 重命名

```http
PATCH /api/v1/sessions/{sessionId}/saves/{**path}
Idempotency-Key: <opaque-key>
X-CSRF-TOKEN: ...
Content-Type: application/json

{ "targetPath": "save20.sav" }
```

- source/target 均相对于同一逻辑 save root，不能跨布局；
- target 已存在返回 `409 SAVE_TARGET_EXISTS`，不把 rename 隐式变为 replace；
- source 与 target 相同返回收敛式 `204`，但仍须先授权并验证 source 存在；
- 只允许文件重命名，不创建或移动目录；`sav/` 模式目标父目录必须已经存在且安全。

### 5.6 删除

```http
DELETE /api/v1/sessions/{sessionId}/saves/{**path}
Idempotency-Key: <opaque-key>
X-CSRF-TOKEN: ...
X-Confirm-Delete: true
```

- 确认头缺失或不是精确 ASCII `true` 返回 `428 SAVE_DELETE_CONFIRMATION_REQUIRED`；
- 首次执行文件不存在返回 `404 SAVE_NOT_FOUND`；成功删除的同幂等键回放原 `204`；
- 成功删除必须写追加式 `SESSION_SAVE_DELETED` 审计；
- 不递归删除目录，不清理空父目录，不提供通配符或批量删除。

### 5.7 稳定错误映射

| HTTP | code | 场景 |
| --- | --- | --- |
| 400 | `SAVE_PATH_INVALID` | 路径、文件名、JSON 或 header 非法 |
| 404 | `SESSION_NOT_FOUND` | Session 不存在或越权 |
| 404 | `SAVE_NOT_FOUND` | 已授权 Session 中 source 不存在 |
| 409 | `SESSION_NOT_QUIESCENT` | Session 为活动/过渡态 |
| 409 | `SESSION_HAS_ACTIVE_WORKER` | 状态/lease 不一致时 fail closed |
| 409 | `SESSION_MUTATION_IN_PROGRESS` | 另一个 mutation 持有写权 |
| 409 | `SAVE_TARGET_EXISTS` | rename 目标已存在 |
| 409 | `IDEMPOTENCY_KEY_REUSED` | 同键请求摘要或 payload 不同 |
| 413 | `SAVE_FILE_TOO_LARGE` | 声明或实际字节超过上限 |
| 415 | `SAVE_FORMAT_INVALID` | 不满足基本原生格式 |
| 428 | `IDEMPOTENCY_KEY_REQUIRED` | 修改缺少幂等键 |
| 428 | `SAVE_DELETE_CONFIRMATION_REQUIRED` | 删除缺少显式确认 |
| 503 | `SESSION_ROOT_INVALID` | marker/root/layout/identity 不一致 |
| 503 | `DATA_ROOT_SPACE_LOW` | 剩余空间低于安全阈值 |
| 503 | `SAVE_OPERATION_RECOVERY_REQUIRED` | 未知遗留写者/operation |

错误体不得包含 SessionRoot、staging、inode、原生内容、异常文本或目标是否属于其他用户。

## 6. 路径、布局与文件访问

### 6.1 公共逻辑路径策略

在 `CloudEmuera.RuntimeAdapter/Paths` 抽取 `EmueraSavePathPolicy`，只处理纯逻辑路径：

- 输入按 `/` 分段，拒绝空路径、空段、`.`、`..`、反斜杠、绝对路径、盘符、NUL 和控制字符；
- 规范化为 NFC；同目录不得出现大小写折叠或 Unicode 规范化碰撞；
- 单段和总路径长度使用与 Runtime 一致的硬上限；文件名为大小写敏感的固定小写原生形式；
- `ROOT` 只允许一个文件段：`global.sav` 或 `save` + 1～10 位 ASCII 数字 + `.sav`；
- `SAV_DIRECTORY` 允许安全目录段 `[A-Za-z0-9_-]{1,64}`，文件叶为：
  `global.sav`、`saveN.sav`、`txtN.txt` 或 `imgN.png`；
- API 不接受公开路径前缀 `sav/`，不允许从 `ROOT` 访问实际 `SessionRoot/sav`；
- `kind` 只由合法文件名推导，不能由 Content-Type 或客户端字段指定。

Runtime 现有 resolve 方法改为调用该策略，保持 P0-05/P1-07 行为和测试。

### 6.2 Session save root accessor

新增 `ISessionSaveRootAccessor`：

1. owner-first 读取 Session 投影、固定 layout、root relative path 和 marker expectation；
2. 从 DataRoot dirfd 逐段打开 `sessions/{sessionId}`、`metadata`、`root`，拒绝 ancestor/leaf link；
3. 交叉核对 DB、`metadata/session-root.json`、root device/inode、内部 marker 和 config layout；
4. 返回作用域化 root/save-root `SafeFileHandle`，不返回可重新字符串寻址的绝对路径；
5. 修改操作在取得 mutation lease 后再次核验；list/download 不取 mutation lease，但每个文件以 fd 固定；
6. identity 改变、特殊文件、异常 owner/mode/nlink 均 fail closed，不自动修复或从 Game current 重建。

Linux 可通过 `/proc/self/fd/{dirfd}` 取得目录项名称，但名称只用于下一次 `openat`；不得直接用该字符串
路径读取。每个 entry 必须立即 no-follow 打开并 `statx`。活动 list 中消失的 entry 可跳过；link、special、
异常 hardlink 或碰撞必须返回 `SESSION_ROOT_INVALID`。递归只进入合法目录段并受深度/条目数上限约束。

## 7. 基本原生格式校验

校验只证明“看起来是该原生文件类别且没有明显损坏/超限”，不证明可被当前 Game 完整加载。

### 7.1 `.sav` 二进制格式

- 读取固定小前缀，接受 upstream `EraBDConst.Header` 或 `ZipHeader`；
- 校验支持的 container version、有限 `dataCount` 和 header 长度，算术使用 checked；
- 普通二进制读取 file type：`saveN.sav` 必须为 `Normal`，`global.sav` 必须为 `Global`；
- 压缩二进制只通过有界 GZip reader 取得第一个解压字节并停止；输入大小、解压读取字节、deadline
  和异常有硬上限，不能全量展开；
- 不读取 Game unique code、script version、save title、变量和尾部，不把 parser 结果存库。

### 7.2 `.sav` 旧文本格式

- 依据 Session runtime manifest/config 的保存编码策略选择严格 decoder，不使用系统 locale；
- 只读取有界前缀和有限行；拒绝空文件、NUL、非法编码、过长首行和明显截断；
- 前两个逻辑字段必须按 invariant culture 解析为 `Int64`，对应原生 unique code/version 结构位置；
- 不比较字段值，因此不会把 Game 不兼容误报为平台验证失败；
- 文本格式不能可靠区分 Normal/Global，类别由目标名决定，完整原生 reader 在实际加载时裁决。

### 7.3 `sav/` 辅助文件

- `imgN.png`：校验 PNG signature、IHDR length/type、非零且受限 dimensions，不解码完整图片；
- `txtN.txt`：使用严格文本编码验证有限前缀，拒绝 NUL；不规范化换行或重写编码；
- 所有文件要求 `0 < size <= MaxSaveFileBytes`；
- validator 接收已打开 staging fd，不根据用户 path 重新打开文件。

校验器位于 Infrastructure，Application 只依赖 `ISaveFileFormatValidator`；API 和 Domain 不得引用内置
Emuera upstream assembly。

## 8. 持久化、mutation lease 与暂存

### 8.1 `save_file_operations`

新增前滚 migration：

```text
id TEXT PK                              -- sfop_...
session_id TEXT NOT NULL FK sessions
actor_user_id TEXT NOT NULL FK users
idempotency_scope TEXT NOT NULL         -- SAVE_IMPORT | SAVE_RENAME | SAVE_DELETE
idempotency_key_hash TEXT NOT NULL
type TEXT NOT NULL                      -- IMPORT | RENAME | DELETE
status TEXT NOT NULL                    -- PREPARED | STAGED | PUBLISHED | COMMITTED | FAILED
source_path TEXT NULL
target_path TEXT NOT NULL
payload_path TEXT NULL
payload_size INTEGER NULL
payload_digest TEXT NULL
expected_source_identity_json TEXT NULL
expected_target_captured INTEGER NOT NULL
expected_target_exists INTEGER NOT NULL
expected_target_identity_json TEXT NULL
result_json TEXT NOT NULL
error_code TEXT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
completed_at INTEGER NULL
state_version INTEGER NOT NULL
```

- ID/path/status/digest/JSON/time/state version 全部有 CHECK；
- 增加 `(status, updated_at, id)` 和 `(session_id, status)` recovery 索引；
- operation ID 写入对应 mutation lease；原始幂等键不入表、不入日志；operation/hash 和幂等记录均按
  `SessionId + scope + key` 隔离；
- IMPORT 进入 `STAGED` 前必须持久化目标发布前的 presence/identity，恢复发布前再次核对；
- terminal operation 至少保留幂等窗口；窗口到期且 staging 清理、lease 释放均确认后由 recovery reaper 删除
  终态 operation/存档 API 幂等记录；清理 DB/staging 时不得删除已发布存档；
- 该表不是当前 save 索引。list 每次读取文件系统，不新增 SaveArtifact/content 表。

### 8.2 mutation lease 加固

- `TryAcquireAsync` 遇到任意现有 row 都返回 active；普通调用者不得按 wall clock 删除；
- acquisition 同一 `BEGIN IMMEDIATE` 要求 owner、`CLOSED/CRASHED`、无 WorkerLease、operation 可执行；
- `CRASHED` 必须完成旧进程退出/写权限释放屏障；无法证明则 recovery required；
- renew/release 匹配 `sessionId + operationId + actorUserId + purpose`，renew 要求 operation 非 terminal；
- 续租 deadline 小于 duration，失败后停止新文件动作并进入恢复；
- 只有 recovery 核验 operation、staging marker、source/target identity 和旧 Worker 屏障后才能接管、
  完成或释放过期 row；
- `SaveCopy` 可为 schema 兼容保留，但 P1-10 不暴露 copy API。

### 8.3 暂存布局

```text
/data/sessions/{sessionId}/
├── root/
└── metadata/save-operations/{operationId}/
    ├── operation.json
    └── payload.tmp
```

- staging 在 SessionRoot 外且 Worker 看不到；与 root 同一文件系统，可跨目录原子 rename；
- marker 绑定 operation/session/actor、target、payload digest/size 和目录/file identity；
- 使用随机 ID、`mkdirat`、`O_CREAT|O_EXCL` 和私有权限；
- payload 写完后 flush file、fsync staging dir，再进入 `STAGED`；
- cleanup 只通过 marker + expected inode 的后序 `unlinkat`，不使用递归 `Directory.Delete`。

## 9. 修改算法与恢复

### 9.1 upload/replace

1. 认证、owner-first Session 查询、逻辑 path 和 Content-Length 预检；
2. prepare 幂等记录和 `PREPARED` operation；
3. 获取 `SAVE_IMPORT` lease，启动续租；重新授权并核对静止状态、无 Worker、退出屏障、root 和 layout，随后记录旧目标是否存在及 identity；
4. 在 lease 覆盖下建立受保护 staging，流式写 payload，同时计算 SHA-256、实际大小并检查 DataRoot 最低剩余空间；
5. 对 payload fd 执行基本格式检查，fsync，CAS `PREPARED → STAGED`；
6. 再次核对目标 identity 和 lease，逐段 no-follow 打开目标父目录；
7. 从 staging dirfd 到 target parent 原子 rename，允许 PUT replace，fsync target 和 parent；
8. CAS `STAGED → PUBLISHED`，同一 DB 事务写审计、幂等成功和 `COMMITTED`；
9. 停止续租、释放 lease并安全清理 staging。HTTP 断开只停止等待，不取消步骤 3 之后的收敛。

### 9.2 rename/delete

rename：prepare operation并获取 `SAVE_RENAME` lease；核对 root/layout；打开 source/target parent，记录
source identity；target 存在则冲突；用 `renameat2(RENAME_NOREPLACE)` 后 fsync parent；重新打开 target
核对原 source identity，再提交 operation/审计/幂等并释放 lease。

delete：先验证确认、幂等和 owner；prepare operation并获取 `SAVE_DELETE` lease；核对 root/layout；
打开 source并记录 identity；以 parent dirfd + leaf + expected identity 执行 checked `unlinkat`，fsync parent；
确认 source 不在后提交 `SESSION_SAVE_DELETED`、幂等和 operation，再释放 lease。

### 9.3 启动与周期恢复

恢复器在 Worker/Session lifecycle reconciliation 之后、readiness 开放前处理非终态 save operations；
周期任务只处理超时项。必须通过 operation CAS 防止两个恢复器同时动作。

| 类型/DB 状态 | 文件事实 | 动作 |
| --- | --- | --- |
| IMPORT PREPARED | 无完整 payload | 核验 marker 后清理，稳定失败 |
| IMPORT STAGED | payload 完整、target 未匹配 digest 且仍匹配发布前 identity | 重新取得写权后继续发布 |
| IMPORT STAGED/PUBLISHED | payload 消失、target digest/size 匹配 | 认定已发布，补提交成功 |
| RENAME PREPARED | source identity 仍在、target 不存在 | 重新取得写权后继续 rename |
| RENAME PREPARED/PUBLISHED | source 不在、target identity 匹配 | 认定已执行，补提交成功 |
| DELETE PREPARED/PUBLISHED | source identity 已不在 | 认定已删除，补审计/提交 |
| 任意 | source/target/marker/root identity 冲突 | fail closed、保留现场、阻止 open/新修改 |
| 任意 | 旧 Worker 写权限无法证明释放 | 保留 mutation lease，返回 recovery required |
| 任意 | 当前 operation 仍持有有效 mutation lease | 跳过本轮，等待 owner 续租停止后再恢复 |
| COMMITTED/FAILED | 遗留 staging 可证明归属 | 安全清理；不得触碰 published save |

恢复成功/失败和审计在同一 DB 事务中收敛，避免重复审计。恢复不得调用完整 Emuera reader 判断哪个
文件“更正确”，也不得为了回滚替换而创建隐藏的第二份权威存档。

若发现没有对应 `save_file_operations` 的遗留 mutation lease（包括历史 `mut_*` lease），恢复器必须
fail closed：不按过期时间自动删除，也不开放该 Session 的 open/存档写。修复属于离线维护流程，顺序为：
先停止 API 和所有 Worker、备份 SQLite 与对应 SessionRoot；再核对 Worker 已退出、SessionRoot
protected marker/layout 与租约的 Session/owner/purpose；`sfop_*` 必须先恢复或保留其 operation/staging
现场，不能直接删 lease；只有确认是没有文件操作事实的历史 `mut_*` 租约后，才能在停机维护事务中删除
该单行租约并记录维护审计。当前 P1-10 不提供在线强制释放入口；在 P1-14 提供正式维护命令前，
无法完成上述证明时应保留现场并返回 `SAVE_OPERATION_RECOVERY_REQUIRED`。

## 10. 授权、安全与审计

- 所有端点：认证/强制改密 → owner-filtered Session repository → `SessionRead`；
- 修改再执行 `SessionControl`，取得 mutation lease 后、文件动作前重新授权；
- 幂等回放也重新验证当前 actor；path/format 错误只在 Session 授权成功后返回；
- 禁止 symlink、hardlink `nlink != 1`、FIFO、socket、device、异常 UID/mode、ancestor 替换；
- 不信任 Content-Type、Content-Disposition、Content-Length、客户端文件名或修改时间；
- 大小相加、header、路径和压缩字段使用 checked 算术；
- DataRoot 空间低于阈值时禁止上传发布，不自动删除用户数据；ENOSPC 必须显式失败；
- 下载 header 防 CR/LF，默认 attachment + nosniff；日志不记录存档内容或原始标题；
- API/Worker 同 UID 继续遵循 ADR-0017 的可信自托管边界，不宣称抵御恶意 Worker。

首次执行而非幂等回放写 `SESSION_SAVE_IMPORTED`、`SESSION_SAVE_RENAMED` 和强制要求的
`SESSION_SAVE_DELETED`。审计 metadata 只含 sessionId、operationId、逻辑 source/target、kind、size、
digest、result、reasonCode 和 requestId；不含物理路径、inode、payload、save title、异常或幂等键。

## 11. 分层与代码归属

```text
src/CloudEmuera.Contracts/Saves/SaveContracts.cs
src/CloudEmuera.Application/Saves/
├── SessionSaveContracts.cs
├── ISessionSaveApplicationService.cs
├── ISessionSaveRepository.cs
├── ISessionSaveRootAccessor.cs
├── ISaveFileOperationStore.cs
└── ISaveFileFormatValidator.cs
src/CloudEmuera.Infrastructure/Saves/
├── SessionSaveApplicationService.cs
├── SqliteSaveFileOperationStore.cs
├── LinuxSessionSaveRootAccessor.cs
├── EmueraSaveFileFormatValidator.cs
└── SaveFileOperationRecovery.cs
tests/CloudEmuera.RuntimeAdapter.Tests/Paths/EmueraSavePathPolicyTests.cs
tests/CloudEmuera.Infrastructure.Tests/Saves/EmueraSaveFileFormatValidatorTests.cs
tests/CloudEmuera.Api.IntegrationTests/SessionSaveApiContractTests.cs
```

- Contracts 只有 HTTP DTO，不引用 EF、Runtime path 或文件句柄；
- Application 定义用例和持久 operation/mutation/accessor/validator 端口，不引用 ASP.NET、SQLite、Unix
  API 或 upstream Emuera；
- Infrastructure 实现 SQLite、dirfd、格式嗅探、fsync 和 recovery；
- API handler 只做身份/CSRF/header/body 绑定和 HTTP 映射，不直接用 `DbContext`、`File`、`Directory`、
  WorkerManager 或 SessionRoot 字符串；
- RuntimeAdapter 持有纯逻辑 path policy；Worker/EmueraRuntime 不引用 Save API，存档不经过 IPC。

## 12. 测试设计

所有测试名或 trait 注释映射 SAVE/AUTH/SEC/AC；自动化使用独立临时 DataRoot，不读取人工 `.env`、
`./data` 或现有 Compose project。

### 12.1 路径、格式与持久化

- 两种布局的合法原生/辅助文件；空、绝对、盘符、`..`、反斜杠、双解码、NUL、Unicode/大小写碰撞；
- binary、zip binary、Normal/Global type、旧 UTF-8/CP932 text、PNG/aux text；
- 空/截断、错误 magic/version/type、超大 dataCount、gzip bomb/timeout、非法编码；
- migration upgrade、CHECK/FK/index、ModelSnapshot；公开 schema 不出现 SaveArtifact/content 表；
- 三个幂等 scope；同键 replay/冲突；过期 lease 不能被普通获取者删除；recovery 核验后才能接管。

### 12.2 竞争、文件系统与故障注入

- 同 Session 两个 mutation 最多一个；open 与 upload/rename/delete 最多一个获得写权；
- `CLOSED`/已回收 `CRASHED` 可修改；活动态、未知旧 Worker、root invalid fail closed；
- root/save parent/source/target/staging 的 symlink、hardlink、FIFO、socket、device、owner/mode 异常；
- 校验/open、open/rename/unlink、枚举/替换 TOCTOU；rename target 竞争时 no-replace；
- 上传中断、超限、ENOSPC、fsync file/dir 失败、rename 前后 kill API、DB/audit commit 前后 kill；
- cleanup marker/inode 替换时保留现场且不越出 Session container；
- list/download 活动文件时允许变化但不会跟随替换链接或越出 root；
- 双用户、同用户双 Session、Global、普通 slot 和辅助文件的 inode/内容隔离。

### 12.3 HTTP 与真实 Runtime

- 匿名、强制改密、CSRF、跨用户五类端点；不存在/越权统一 404；
- OpenAPI、raw upload、streaming download、安全 header、删除 428、稳定错误；
- 两种布局的 list/download/import/replace/rename/delete；请求断开后同键只发布一次；
- 活动 Worker list/download 成功，所有修改拒绝；
- 真实 Emuera save → close → download → delete/upload → reopen → 原生 load；
- kill Worker 后完成 CRASHED 回收再管理；无法确认退出时拒绝；
- Game current 替换不改变旧 Session layout；API 未引用 upstream assembly，payload 未重写。

### 12.4 验证命令

```bash
./scripts/dev-up.sh
source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore --configuration Release \
  --filter 'Category=SavePathSecurity'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Infrastructure.Tests --no-restore --configuration Release \
  --filter 'Category=SaveFormat|Category=SaveOperation|Category=Migration'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Api.IntegrationTests --no-restore --configuration Release \
  --filter 'Category=SessionSaves'
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
```

测试项目必须加入 `CloudEmuera.slnx`、lockfile 和 `check.sh` 自动发现范围，不能只创建示意命令。

## 13. 实施切片

1. **容量 ADR、契约与 migration**：冻结 `MaxSaveFileBytes`，定义 DTO/error/OpenAPI/path policy，新增
   operation migration并加固 mutation lease。
2. **安全 accessor 与格式校验**：实现 protected dirfd accessor、checked unlink/no-replace rename、
   binary/zip/text/PNG 有界嗅探和恶意路径测试。
3. **list/download**：实现 owner-first repository、枚举、排序、流式下载、安全 header和活动态读取。
4. **upload/replace 与恢复**：实现 raw streaming、staging、digest/size/space/format、lease 续租、原子
   replace、fsync、启动/周期 recovery 和 crash matrix。
5. **rename/delete**：实现 no-replace rename、checked unlink、删除确认、审计、幂等和 recovery。
6. **文档收尾与验收交接**：同步 OpenAPI、配置、requirements/design/development plan；把双布局真实
   Runtime、双用户隔离、open/mutation 竞争和 crash/reopen 矩阵交给 P1-15 的统一验收。

## 14. 完成定义

P1-10 的实现交付需要满足以下代码、协议和安全条件：

1. 五类 API 具有契约、认证、授权、CSRF/确认、流式行为和稳定错误；
2. 路径策略与 Runtime 共用，布局只由 Session 固定配置决定；
3. 上传执行大小、空间、普通文件和基本格式校验，API 不完整解析或改写原生内容；
4. 所有修改通过 mutation lease，并与 open 竞争最多一个获得写权；
5. 过期 lease 不被普通请求盲目回收，所有 crash window 由持久 operation 安全收敛；
6. 文件操作使用 protected dirfd/no-follow/identity/fsync，路径、链接和 TOCTOU 测试通过；
7. 删除要求显式确认并审计；幂等回放不重复文件副作用或审计；
8. schema/契约中不存在 SaveArtifact、generation、跨 Session copy 或内容级兼容证明；
9. 正常、边界、失败、并发和恶意文件系统测试覆盖已实现的 API/存储边界；
10. `./scripts/check.sh`、dev user、third-party 和 `git diff --check` 通过，文档/配置同步。

以下最终验收项统一移交 P1-15：

- 两种真实布局的保存→关闭→下载→删除/上传→同 Session 重开→Emuera 加载；
- 两个用户和同一用户两个 Session 的普通/Global/辅助文件隔离；
- 完整故障注入矩阵（含真实 SQLite 屏障、发布窗口和恢复重开）及跨环境质量门。

因此本方案的“实现完成”不等同于产品最终 DONE；P1-15 通过上述验收后再更新开发计划状态。

## 15. 实施记录（2026-08-14）

- 冻结 `MaxSaveFileBytes=64 MiB`，新增 ADR-0022、容量配置、迁移和 operation 表；过期 mutation lease
  不再由普通请求删除，Session open 也把遗留 lease 视为恢复屏障。
- 抽取 Runtime/API 共用的 `EmueraSavePathPolicy`，实现根目录与 `sav/` 布局的逻辑路径 allowlist、NFC
  规范化、大小写碰撞检查和物理 `sav/` 前缀隔离。
- 实现 Linux protected dirfd accessor、`renameat2(RENAME_NOREPLACE)`、checked unlink、identity/mode/
  hardlink 校验、staging marker、fsync、流式下载/上传和有界 native binary/text/PNG sniffing。
- 接入 list/download/PUT/PATCH/DELETE HTTP 端点、owner-first 授权、CSRF、幂等回放、删除确认、安全下载
  header、审计和周期 recovery hosted service。
- `dev-up.sh` 已成功应用 `20260814120000_AddSessionSaveFileOperations`；路径安全、格式/约束/迁移、
  operation recovery 和 ROOT/sav 两种布局的 Session saves HTTP 定向测试均通过。完整质量门结果以最终
  交付记录为准。

## 16. 后续任务交接

- P1-11 使用 TanStack Query 管理 saves list；修改成功后失效 list，不把文件内容放入 realtime store；
- P1-11 UI 展示活动 Session 修改拒绝、CRASHED 风险、格式错误和显式删除确认；
- P1-13 将 `MaxSaveFileBytes`、列表文件数和 DataRoot 空间纳入实例限制脚本，但不能改变 operation/lease
  正确性；
- P1-14 备份针对静止 SessionRoot 或整个 `/data`，不调用 generation API；
- P1-15 将 AC-004/007/013/014 的真实 Runtime、双用户隔离和故障恢复纳入一键验收。
