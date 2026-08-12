# ADR-0009：草稿发布事务与不可变版本身份

状态：Superseded by ADR-0010

日期：2026-08-09

取代说明：产品决定移除 GameVersion 概念。后续设计见
[`ADR-0010`](0010-single-game-content-without-version-entities.md)。本文仅保留为历史决策记录，
不得作为新实现依据。

范围说明：本文中浏览器编辑及其他旧产品入口同样仅为历史记录；当前实现边界以
[`ADR-0017`](0017-trusted-self-hosted-mvp-simplification.md) 和 [`ADR-0010`](0010-single-game-content-without-version-entities.md) 为准。

## 背景

P1-03 已把不受信任 ZIP 收敛为有期限、只能消费一次的 READY 候选内容，但不创建 Game、
GameVersion 或最终内容目录。P1-04 需要在此基础上支持可变草稿、浏览器文本编辑、真实 parser
校验和不可变发布，并处理文件系统原子 rename 与 SQLite 事务无法组成单一原子事务的问题。

现有 `game_versions.content_digest` 对非 NULL 值建立全局唯一索引，发布完成条件也要求并发发布
不能产生两个相同版本身份。与此同时，P1-03 最多允许 50,000 个条目，而现有
`manifest_json` 的数据库约束上限为 1 MiB；完整逐文件清单不能可靠地塞入单个 JSON 字段。

只把 GameVersion 状态改成 `PUBLISHED`、再按字符串路径移动目录，会留下多个无法安全判断的
崩溃窗口：目录存在但数据库无引用、数据库已提交但摄取仍为 CONSUMING、校验期间草稿继续被
编辑，以及两个请求同时发布相同内容。

## 决定

- 草稿和已发布版本使用不同物理命名空间：
  `games/{gameId}/drafts/{gameVersionId}/content/` 是可变工作区，
  `games/{gameId}/versions/{gameVersionId}/content/` 是不可变发布内容；同级临时目录只使用随机
  operation ID 命名。
- GameVersion 行在草稿、校验和发布过程中保持同一个 `gameVersionId`。从已发布版本开始编辑时
  创建新的 DRAFT 行和独立文件树，不修改原行；复制可以使用 reflink，但必须保持写时复制语义，
  禁止硬链接或任何共享可写 inode。
- 规范 `contentDigest` 继续作为全局唯一的不可变内容身份。摘要只由规范路径、条目类型、实际
  字节数和文件 SHA-256 构成，不包含 ZIP 顺序、时间、压缩方式或数据库 ID。不同草稿发布出相同
  摘要时只有一个成功，另一个返回稳定冲突；不得创建第二个非 NULL digest 身份。
- 所有 IMPORT、CLONE、VALIDATE 和 PUBLISH 长操作写入持久
  `game_version_operations`。操作使用状态、lease 和 `state_version` CAS；API 启动和周期后台
  reconciler 负责续作、回滚或安全回收，正确性不依赖请求、进程内任务或无限期 lease。
- 发布先冻结草稿 revision，复制到 `publish-work/{operationId}`，重新校验文件类型、清单和摘要，
  运行受限的 parser-only Validator，写出 canonical runtime manifest，设置只读权限并 fsync，
  原子 rename 到最终版本目录；数据库事务随后以 CAS 提交 PUBLISHED 元数据和 operation。
- 文件系统先成功而数据库事务失败时，不在请求错误路径立即递归删除。reconciler 只通过受保护
  父目录句柄、operation 归属、inode/type/owner/mode 校验和安全期回收无数据库引用的目录。
- 从 P1-03 创建草稿时记录唯一 `source_ingestion_id`。若 GameVersion 已提交而 ingestion 仍为
  CONSUMING，reconciler 核对 owner、digest 和最终草稿后幂等补做消费完成；长复制必须续租，不能
  通过 staging 字符串路径绕开受保护内容目录句柄。
- 新增 `game_version_files` 保存逐条规范文件/目录记录，新增
  `compatibility_diagnostics` 保存有界诊断。`manifest_json` 和
  `compatibility_summary_json` 只保存有界顶层清单、摘要和计数；完整 canonical manifest 同时作为
  发布目录中的只读文件保存。
- DRAFT 是唯一可编辑状态；VALIDATING 冻结编辑；校验失败回到 DRAFT 并保存当前 revision 的诊断。
  BLOCKED 只表示已经发布后被安全策略阻止创建新 Session，不用于普通草稿校验失败。
- 逻辑删除先把 Game/GameVersion 标记为 DELETED。仍被任意 Session 引用的版本拒绝删除；P1-04
  不同步物理删除已发布内容，后续回收必须遵守引用检查、恢复保留期和同样的安全目录规则。
- 真实 Emuera parser 不在 API 进程内运行。P1-04 提供一次性 parser-only Validator 进程；它只读
  冻结快照、不进入游戏执行阶段、使用有界输入/输出/诊断和硬超时。P1-12 再接入正式
  namespace/cgroup/seccomp 沙箱，不能因此把 Validator 合并回 API。

## 备选方案

### 在原发布目录中直接编辑并在发布时 chmod

目录身份简单，但已发布内容与草稿没有可靠边界，误写或编辑/发布竞争会改变 Session 绑定内容，
不满足 GAME-005/008，因此不采用。

### 每次编辑都创建一个新的 GameVersion

可天然保留历史，但一次浏览器编辑会制造大量业务版本和内容副本，也不能替代文件级 If-Match；
MVP 使用一个可变 DRAFT revision，发布时才形成不可变身份。

### 只依赖目录权限保证不可变

API 服务身份可以重新 chmod，权限不是完整授权边界。只读 mode 作为纵深防御，真正写入端口还
必须校验版本状态和受保护目录身份。

### 把完整清单继续放入 manifest_json

实现较少，但 50,000 个条目会超过现有 1 MiB JSON 约束，目录浏览和文件 ETag 也会反复解析大
JSON，因此采用明细表加有界摘要。

### 请求内同步完成发布且失败时立即删除目录

实现直观，但请求取消/API 崩溃后没有权威恢复意图，数据库和文件系统故障窗口不可判定；立即
递归删除还可能放大 TOCTOU 风险，因此采用持久 operation 与对账。

### 允许同一内容摘要挂到多个 GameVersion

可保留不同标签和归属，但与现有全局唯一约束及“相同版本身份唯一”完成条件冲突，也会要求重新
定义跨 Game 授权和内容回收。MVP 保持摘要唯一；未来需要内容对象与业务版本多对一时另立 ADR。

## 后果

- 发布和摄取绑定在 API 重启、取消、磁盘满或 SQLite 失败后可确定性收敛，但需要新增 operation
  表、reconciler 和故障注入测试。
- 草稿和发布内容会短时同时占用磁盘；发布前必须检查空间并在复制过程中执行实际字节上限。
- 相同内容不能仅通过不同标签重复发布；客户端会收到稳定冲突，并只在有读取权限时获得既有版本
  标识。
- 逐文件清单适合目录查询和 SessionRoot 物化，数据库行数会随游戏文件数增长；批量写入必须有界，
  发布事务不能包含文件哈希或 parser 等长操作。
- P1-04 的 Validator 只提供进程隔离和资源硬边界的最小实现；正式不受信任多用户部署仍必须等待
  P1-12 的完整沙箱就绪条件。
- 已发布内容的物理回收不再是普通 DELETE 请求的一部分，运维保留期和备份策略可以独立演进。

## 验证

- 同一草稿并发发布只有一个 CAS 胜者；不同草稿产生相同 digest 时数据库只保留一个身份。
- 编辑已发布版本创建独立 DRAFT，源/目标没有共享可写 inode，源摘要始终不变。
- 在内容 rename、SQLite 提交、P1-03 CompleteConsume 和 operation 提交前后分别注入退出，重启后
  reconciler 能完成或回收且不暴露半成品。
- 50,000 条目清单不依赖单个大 JSON；发布后的明细表、canonical manifest 和重新遍历摘要一致。
- Validator 超时、崩溃、超输出和非法协议产生稳定阻断诊断，API 进程保持健康。
- 被 Session 引用的版本不能删除；逻辑删除不会同步移除发布目录。
