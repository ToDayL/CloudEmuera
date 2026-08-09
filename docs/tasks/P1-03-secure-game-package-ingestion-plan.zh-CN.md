# P1-03 安全游戏包摄取详细设计

状态：DONE（2026-08-09）

整改复核：DONE（2026-08-09；补齐 dirfd/openat 安全边界、`lease.json` 归属校验、CAS 并发控制、
后台 reaper、消费崩溃恢复、完整诊断、可配置中央目录上限与恶意归档测试）

设计日期：2026-08-09

对应开发步骤：`P1-03 — 安全游戏包摄取`

前置条件：P1-01 SQLite 首版 schema 与独占 Migrator 已完成；P1-02 本地身份、资源授权与审计
已完成；P0-05 已定义 Game current content 与 SessionRoot 之间的完整普通文件树复制约束

后续步骤：P1-04 草稿编辑与不可变发布；P1-06 Session 创建；P1-12 Worker 沙箱与资源限制

需求映射：GAME-001～003、GAME-007、SEC-001/005、OPS-002/004/005、COMP-005/006、
NFR-011、AC-010。GAME-007 在本步骤建立结构、编码和安全诊断模型；真实 Emuera parser、
资源引用和 Blocked capability 验证由 P1-04 发布验证继续接入。

架构决策：[`ADR-0008`](../adr/0008-secure-zip-ingestion-policy.md)。

## 1. 任务结论

P1-03 交付一个传输无关、可由 P1-04 的正式上传 API 直接调用的摄取事务。它接收不可 seek 的
字节流，把原始 ZIP 写入隔离暂存，完成 ZIP 结构预检、安全逐项解包、实际字节配额、路径碰撞、
文本编码、逐文件摘要和规范内容清单，成功后返回一个有期限、只能消费一次的候选内容句柄。

本步骤完成后必须能够证明：

- 上传体和每个归档条目始终流式处理，不把整个归档或单个大文件载入内存；
- ZIP 在任何文件写出前先完成中央目录和全部条目的安全预检，预检信息在解包时再次核对；
- 解包只通过受保护目录句柄创建目录和普通文件，不恢复归档权限、owner、时间、xattr 或链接；
- 压缩前字节数、条目数、单文件实际字节、总展开实际字节、深度和实际压缩比都有独立硬上限；
- 逻辑路径统一为 NFC、`/` 分隔相对路径，同时拒绝 Windows/Linux 间会产生歧义的名称；
- ZIP 声明的软链接、硬链接或特殊文件被拒绝，最终内容树也重新 `lstat/statx` 验证为普通文件和
  目录，且不同文件不共享 inode；
- UTF-8 BOM、严格 UTF-8 无 BOM 和严格 Windows-31J/CP932（产品界面称 Shift-JIS）产生确定的
  编码记录，不依赖宿主 locale；
- 内容摘要只由规范路径、条目类型、实际大小和文件 SHA-256 决定，不受 ZIP 顺序、压缩级别、
  timestamp 或原始 archive digest 影响；
- 同一用户或多个用户并发摄取时，SQLite 中的暂存预算预留防止以并发请求绕过全局 staging
  上限；API 重启后可以识别和安全回收遗留摄取；
- 任一拒绝、取消、超时、磁盘错误或进程中断都不会产生 P1-04 可消费的 `ready/` 内容目录，
  更不会写入最终 `/data/games/{gameId}/workspace/` 或 `content/`。

P1-03 不创建 Game，不启用 current content，也不增加面向产品的临时“扫描上传” HTTP 资源。
P1-04 的 `PUT /games/{id}/package` 在同一请求中调用本摄取事务，随后把候选内容绑定为授权用户
拥有的 workspace；在此之前以 Application 服务测试、非 seek 流测试和真实
Linux 文件系统集成测试完成验收。

## 2. 范围

### 2.1 必须实现

1. `IGamePackageIngestionService` 与一次性 `IngestedPackageLease` Application 契约；
2. 隔离 staging 目录、安全创建/清理和跨重启遗留项回收；
3. SQLite `game_package_ingestions` 预留/状态 migration；
4. 非 seek 输入流到 `archive.zip.part` 的有界复制、SHA-256 和准确字节计数；
5. classic single-disk ZIP 结构检查器和明确格式 allowlist；
6. 逻辑路径规范化、portable name policy、Unicode/大小写/文件—目录碰撞检查；
7. 基于目录句柄、禁止跟随链接的逐项安全解包；
8. 声明值和实际值双重配额、压缩炸弹与 truncated/corrupt stream 防御；
9. 文件摘要、规范 manifest、规范 content digest；
10. UTF-8/Windows-31J 严格编码检测与有界诊断集合；
11. 安全拒绝、内容诊断、日志和审计的稳定错误码；
12. 正常包、边界包、恶意 ZIP corpus、并发、取消、磁盘故障和 TOCTOU 测试；
13. 供 P1-04 消费候选内容和最终确认/放弃的原子接口；
14. 更新总体设计、开发计划、配置样例和需求—测试映射。

### 2.2 明确非目标

- 不支持 7z、RAR、tar、gzip、自解压程序、多卷 ZIP、加密 ZIP 或 ZIP64；
- 不从 URL 拉取游戏包，不处理浏览器提供的本地绝对路径；
- 不相信 MIME、扩展名、`Content-Length`、ZIP 中央目录大小或 CRC 作为唯一安全依据；
- 不保留 ZIP 内的权限、可执行位、owner、group、时间戳、ACL、xattr、稀疏文件或链接语义；
- 不自动剥离“唯一顶层目录”、不自动重命名大小写、不修复非法路径；
- 不在本步骤提供 Game CRUD、workspace 编辑、文件搜索、内容启用或逻辑删除；
- 不运行真实 Emuera parser/Worker，不解析资源引用，不允许诊断绕过平台级禁止项；
- 不做杀毒产品集成或版权/字体授权判断；这些不属于归档边界正确性的替代品；
- 不把 staging 路径、ZIP 原始文件名、ERB/CSV 正文或完整用户文件名写入普通日志/审计；
- 不以请求结束时的普通 `Directory.Delete(recursive: true)` 作为安全清理实现。

## 3. 核心领域边界与依赖

### 3.1 分层

```text
P1-04 HTTP upload adapter（后续）
              │ non-seek Stream + actor + requestId
              ▼
CloudEmuera.Application.GamePackages
  GamePackageIngestionService
  policy / result / manifest / diagnostic contracts
              │
       ┌──────┴─────────┐
       ▼                ▼
IIngestionReservationStore   IGamePackageStagingStore
       │                │
       ▼                ▼
SQLite reservation      Linux dirfd/openat implementation
                              │
                    ZIP inspector / extractor / encoding detector
```

Application 只编排阶段和定义结果，不引用 EF Core、`ZipArchive`、Linux syscall 或 ASP.NET
类型。Infrastructure 实现 SQLite、ZIP 和文件系统端口。Contracts 在 P1-04 才增加正式 HTTP
DTO；P1-03 的内部路径句柄不得进入公开契约。

### 3.2 建议工程布局

```text
src/CloudEmuera.Application/GamePackages/
├── GamePackageIngestionService.cs
├── GamePackageIngestionRequest.cs
├── GamePackageIngestionLimits.cs
├── GamePackageManifest.cs
├── GamePackageDiagnostic.cs
├── GamePackageRejection.cs
├── IngestedPackageLease.cs
├── IIngestionReservationStore.cs
└── IGamePackageStagingStore.cs
src/CloudEmuera.Infrastructure/GamePackages/
├── SqliteIngestionReservationStore.cs
├── LinuxGamePackageStagingStore.cs
├── ZipStructureInspector.cs
├── ZipEntryPathPolicy.cs
├── SafeZipExtractor.cs
├── TextEncodingDetector.cs
├── ContentManifestBuilder.cs
└── StaleIngestionReaper.cs
tests/CloudEmuera.GamePackages.Tests/
├── ArchiveSecurityTests.cs
├── ArchiveQuotaTests.cs
├── PathPolicyTests.cs
├── EncodingTests.cs
├── ManifestTests.cs
├── IngestionFailureTests.cs
├── IngestionConcurrencyTests.cs
└── Fixtures/Corpus/
```

测试项目引用 Application 和 Infrastructure，但产品层不得引用测试 corpus。若 Linux syscall
封装与 P1-01 的 `LinuxFileOperations` 能共用，应提取内部通用原语，不能复制两套略有差异的
`openat/statx/unlinkat` 安全规则。

## 4. 摄取状态机与一次性消费

### 4.1 状态

```text
RESERVED → RECEIVING → INSPECTING → EXTRACTING → ANALYZING → READY
    │          │            │            │            │          │
    └──────────┴────────────┴────────────┴────────────┴──────→ FAILED

READY → CONSUMING → CONSUMED
  │         │
  └─────────┴───────────────────────────────────────────────→ ABANDONED
```

- `RESERVED`：SQLite 已预留 staging 最坏情况预算，目录尚未创建；
- `RECEIVING`：正在写 `archive.zip.part`；
- `INSPECTING`：归档落盘完成，中央目录预检；
- `EXTRACTING`：只向 `candidate.work/content/` 写普通文件和目录；
- `ANALYZING`：重新遍历、编码检测和 manifest 生成；
- `READY`：完整 `candidate.work/` 已原子 rename 为 `ready/`，允许一次消费；
- `CONSUMING`：P1-04 已 CAS 抢占，其他请求不能重复绑定；
- `CONSUMED`：候选目录已由后续用例安全移动/复制并确认；
- `FAILED/ABANDONED`：不可消费，等待本请求或 reaper 清理。

每次转换匹配 `id + owner_user_id + expected_status + state_version`。文件系统大操作不在 SQLite
事务内执行；数据库只记录意图、预算和可恢复阶段。同文件系统 `candidate.work → ready` 的原子
rename 是 READY 的文件系统前置条件，`ANALYZING → READY` 数据库 CAS 才是对消费者可见的
线性化点。若 rename 后 CAS 失败，该目录仍没有 READY 数据库状态，只能被 reaper 回收，不能被
P1-04 猜测使用。

### 4.2 一次性句柄

`IngestedPackageLease` 至少暴露：

```text
ingestionId
ownerUserId
archiveDigest / archiveBytes
contentDigest / expandedBytes / fileCount / directoryCount
manifest
diagnostics
expiresAt
```

它不暴露绝对路径。P1-04 必须调用 `BeginConsumeAsync(ingestionId, ownerUserId,
expectedContentDigest)`，成功 CAS 到 CONSUMING 后，Infrastructure 才把受保护目录句柄交给
后续草稿构造器。`CompleteConsumeAsync` 标记 CONSUMED；异常路径调用 `AbandonAsync`。lease 的
`DisposeAsync` 只做最佳努力标记和清理，正确性依赖持久状态与 reaper，而不是终结器或进程内存。

## 5. SQLite 预留模型

新增 migration，不修改 P1-01/P1-02 已提交 migration：

```text
game_package_ingestions
id TEXT PK                           -- ing_<UUIDv7 N format>
owner_user_id TEXT NOT NULL FK users
status TEXT NOT NULL                 -- 上述稳定大写枚举
staging_path TEXT NOT NULL UNIQUE    -- games/staging/{ingestionId} 相对 DataRoot
reserved_bytes INTEGER NOT NULL      -- archive limit + expanded limit
archive_bytes INTEGER NOT NULL DEFAULT 0
expanded_bytes INTEGER NOT NULL DEFAULT 0
entry_count INTEGER NOT NULL DEFAULT 0
archive_digest TEXT NULL
content_digest TEXT NULL
limits_json TEXT NOT NULL
summary_json TEXT NOT NULL
created_at INTEGER NOT NULL
updated_at INTEGER NOT NULL
expires_at INTEGER NOT NULL
reservation_released_at INTEGER NULL
cleanup_completed_at INTEGER NULL          -- 安全后序清理完成；终态清理可跨重启幂等重试
state_version INTEGER NOT NULL DEFAULT 0
```

约束：

- ID、相对路径、digest、JSON 和时间沿用 P1-01 的 CHECK 约定；
- `reserved_bytes/archive_bytes/expanded_bytes/entry_count >= 0`；`reserved_bytes` 只在安全清理或候选
  内容完成消费后原子归零，并同时填写 `reservation_released_at`；
- digest 只在相应阶段以后允许非 NULL；
- 仅 ACTIVE 用户可以新建预留；用户删除继续 `RESTRICT`；
- 索引 `(status, expires_at)` 供 reaper 使用，`(owner_user_id, created_at DESC)` 供诊断；
- `summary_json` 只保存计数和稳定诊断码，不保存原始文件名列表；完整 manifest 随 staging
  候选存在，P1-04 消费时写入 Game workspace 文件索引和摘要元数据。

预留在 `BEGIN IMMEDIATE` 中读取用户 quota，并计算：

```text
effectiveArchiveLimit = min(user.max_game_package_bytes, configuredMaxArchiveBytes)
effectiveExpandedLimit = min(user.max_session_bytes, configuredMaxExpandedBytes)
reservation = effectiveArchiveLimit + effectiveExpandedLimit
activeReserved = SUM(reserved_bytes WHERE reserved_bytes > 0)
```

只有 `activeReserved + reservation <= MaxStagingReservedBytes` 且 DataRoot 当前可用空间高于
`reservation + MinDataRootFreeBytes` 才接受。文件系统空闲量可能被外部进程改变，因此写每个块前
仍检查本摄取计数，周期性检查安全余量；`ENOSPC/EDQUOT` 必须 fail closed。预留解决 API 内并发
超卖，不承诺替代宿主磁盘 quota。

FAILED/ABANDONED 本身不释放预算；只有安全删除 staging 后才把 `reserved_bytes` 置零。终态行保留
一个短诊断窗口后批量删除；reaper 每轮有最大行数、最大清理字节和时间预算，不能因
大量孤儿阻塞 API。数据库记录缺失、目录身份不符或路径异常时只报告高优先级安全事件，不递归
猜测删除。

## 6. 文件系统布局与权限

单次摄取只使用服务器生成名称：

```text
/data/games/staging/{ingestionId}/       mode 0700
├── lease.json                           mode 0600，服务器生成，不含秘密
├── archive.zip.part                     mode 0600
├── candidate.work/                      mode 0700，完成中的候选 bundle
│   ├── content/                         mode 0700，解包目标
│   └── manifest.json.part               mode 0600
└── ready/                               mode 0700，candidate.work 原子改名后出现
    ├── content/                         mode 0700
    └── manifest.json                    mode 0600
```

- `/data`、`games`、`staging` 启动时逐级验证为服务账户拥有、非链接、单链接目录且没有 group/other
  写权限；
- 生产 Linux 通过已验证的父目录 fd 使用 `mkdirat/openat/renameat/unlinkat`，文件创建使用
  `O_CREAT|O_EXCL|O_NOFOLLOW|O_CLOEXEC`；目录逐段 `O_DIRECTORY|O_NOFOLLOW` 打开；
- 不使用用户字符串拼绝对路径，不用 `GetFullPath + StartsWith` 代替目录句柄约束；
- `archive.zip.part` 完整落盘并 flush/close 后才进入 INSPECTING；是否 `fsync` 目录不是成功响应的
  持久性承诺，进程中断后的 READY 仍须经状态和目录双重对账；
- 解包仅创建 `0700` 目录与 `0600` 文件，不应用 ZIP external attributes；
- 同一相对路径只允许一次创建，绝不以覆盖方式处理重复条目；
- 清理从已验证 ingestion 根 fd 向下遍历，遇链接、特殊文件、异常 owner、异常 hardlink count 或
  越界深度即停止该项并报警，不跟随目标；
- P1-04 发布只消费 `ready/content`，原始 archive 默认不保留；需要保留原包须另行定义隐私、配额
  和生命周期，不在 P1-03 偷加。

## 7. ZIP 格式策略与结构预检

### 7.1 接受的格式子集

MVP 接受：single-disk classic ZIP、普通目录条目、普通文件条目、Stored(method 0) 和
Deflate(method 8)。拒绝：

- ZIP64 locator/record/extra、multi-disk、spanned archive；
- traditional encryption、strong encryption、未知压缩方法；
- 重叠条目数据、中央目录越界、EOCD/offset/length 溢出、local header 前置 payload 或 EOCD
  comment 之后的 trailing/polyglot 数据；
- 非法或重复 extra field、已知链接/特殊文件 metadata；
- local header 与 central header 的 name、flags、method 或可比较 size 不一致；
- 声明 CRC/size 与实际读取结果不一致、truncated stream 或解压器异常。

不直接把 `ZipArchive` 的条目枚举当作安全预检。`ZipStructureInspector` 先解析 EOCD、中央目录、
local header 和 extra TLV，所有长度/offset 运算使用 checked 64-bit，并生成不可变的
`ValidatedZipEntry` 列表。之后按已验证的 data offset 和 compressed length 建立有界输入流，Stored
直接复制、Deflate 使用 `DeflateStream`；不再让框架重新解释 entry name 或枚举额外条目。

### 7.2 文件名字节

- general purpose flag bit 11 为 1 时，name bytes 必须是严格 UTF-8；
- bit 11 为 0 时只接受 7-bit ASCII name bytes；不根据系统 locale 猜 CP437/CP932；
- Info-ZIP Unicode Path extra 只能在其 CRC 与原 name bytes 匹配、内容为严格 UTF-8 时作为名称，
  且仍进入同一规范化和碰撞流程；冲突则拒绝；
- ZIP 注释不参与路径和 manifest，按字节长度限制后忽略；
- 文件内容编码与 ZIP entry name 编码是两个不同概念。Shift-JIS/Windows-31J 的 ERB/CSV 内容
  可以位于 ASCII 或 UTF-8 路径下。

### 7.3 链接和特殊文件

中央目录 external attributes 的 Unix mode 若声明 symlink、socket、FIFO、block/char device 或
其他非 regular/directory 类型，立即拒绝；DOS reparse/device/directory 属性与路径形态矛盾也
拒绝。解析已知 Unix extra field，发现 symlink/hardlink target 或非普通类型即拒绝。

ZIP 没有统一 hardlink 表达。实现绝不应用任何 link metadata，所有接受的文件均以 `O_EXCL`
创建并写入独立 inode；预检拒绝已知 hardlink 表达，完成遍历再确认普通文件 `nlink == 1` 且
`(dev, ino)` 在内容树内唯一。这样未知且被忽略的非内容 metadata 也不可能在目标树中形成链接。

## 8. 逻辑路径规范化与碰撞

每个 entry name 按以下固定顺序处理：

1. 严格解码 name bytes；
2. 拒绝 NUL、C0/C1 control、DEL、反斜杠 `\`；
3. 拒绝空路径、开头 `/`、`//`、UNC、盘符、任意 `:` 和结尾非目录 `/`；
4. 以 `/` 分段，拒绝空段、`.`、`..`；
5. 每段转 Unicode NFC；规范化后再执行长度和保留名检查；
6. 拒绝尾随空格/点、Windows device names（含扩展名前的 CON/PRN/AUX/NUL/COM1…/LPT9）、
   Unicode noncharacter 和孤立 surrogate；
7. 每段 UTF-8 最多 255 bytes，完整逻辑路径最多 1024 bytes，深度最多配置值；
8. 规范路径使用 `/` 连接，目录不保留尾随 `/`；
9. 建立 `NFC + OrdinalIgnoreCase` 碰撞键，并同时维护精确规范路径键；
10. 检查 file/file、dir/dir 重复以及 `a` 为文件但又出现 `a/b` 的 file/dir prefix 冲突。

NFC 改写产生 `PATH_NORMALIZED_TO_NFC` 信息诊断，但 manifest 和物理目标都只使用规范路径。
任何两个归档条目落到同一碰撞键都硬拒绝，不选择“第一个获胜”，也不自动改名。目录的显式条目
与由文件父路径隐式产生的同一目录可以合并一次；两个显式目录条目仍视为重复归档条目并拒绝。

路径排序统一按规范 UTF-8 bytes 的 unsigned lexicographic 顺序，不能使用当前文化排序。

## 9. 双阶段配额与安全解包

### 9.1 配额快照

开始摄取时把所有有效上限写入 `limits_json`，一次摄取中管理员修改 quota 不追溯改变该请求。
具体默认值和配置关系由 ADR-0008 冻结；所有测试使用显式 limits，不依赖机器环境。

硬限制至少包括：

```text
MaxArchiveBytes
MaxExpandedBytes
MaxSingleFileBytes
MaxEntryCount
MaxDirectoryDepth
MaxCompressionRatio
MaxPathUtf8Bytes / MaxSegmentUtf8Bytes
MaxCentralDirectoryBytes
MaxDiagnostics
MaxIngestionDuration
MaxStagingReservedBytes / MinDataRootFreeBytes
```

### 9.2 声明值预检

中央目录扫描期间，以 checked arithmetic 累加 entry count、declared uncompressed bytes、
central directory bytes，并逐项验证 declared single-file size 和 declared ratio。任何声明值超限
都在创建 `candidate.work/content` 前拒绝。

声明 `compressedSize=0 && uncompressedSize>0` 视为无限 ratio；空文件 ratio 记 1。ratio 是辅助
防线，`MaxExpandedBytes` 和 `MaxSingleFileBytes` 才是最终硬边界。

### 9.3 实际值强制

解包串行进行，每个 entry 的输出经过两层 counting stream：单文件计数和全局计数。每次写目标
文件前先检查下一块是否越界；不能先写超限字节再报错。解包后实际字节必须等于中央目录声明值，
实际压缩比再次计算并满足上限；输出流同时增量计算 CRC32，并与中央目录声明值比较，不能只依赖
decoder 是否抛出异常。

输入流读取、解压和写盘都接受取消与总 deadline。取消不会转成内容诊断，而是稳定拒绝
`INGESTION_CANCELLED`；超时为 `INGESTION_DEADLINE_EXCEEDED`。实现不得启动无法取消且继续占用
CPU/磁盘的后台解压任务。

## 10. 内容扫描、编码和诊断

### 10.1 文本候选

以下路径按不区分大小写扩展名识别为文本候选：`.erb`、`.erh`、`.csv`、`.config`、`.txt`，
以及根级 `emuera.config`。扩展名只决定编码检查，不授予执行能力或绕过资源限制。其他文件按
binary 记录 MIME 为 `application/octet-stream`；安全的具体资源 MIME 由 P1-04 发布扫描决定。

### 10.2 确定性检测

对完整文件使用 exception fallback 增量解码，顺序固定：

1. `EF BB BF`：严格 UTF-8，记录 `UTF8_BOM`；BOM 后无效则 ERROR；
2. 无 BOM 且完整文件严格 UTF-8：记录 `UTF8`；
3. 否则完整文件严格 Windows-31J/CP932：记录 `SHIFT_JIS`；
4. 两者都失败：记录 `UNKNOWN` 和阻断诊断 `TEXT_ENCODING_UNSUPPORTED`。

需注册 `CodePagesEncodingProvider`，但必须显式请求 code page 932 和 exception fallback，不调用
`Encoding.Default`。纯 ASCII 按 UTF8 记录。UTF-16/32 BOM 产生阻断诊断，不自动转码。NUL、非法
control 或解码后不合法 Unicode 产生内容诊断；这些不改变归档边界成功事实，但会阻止 P1-04
发布，允许用户在草稿中修复。

编码检测可以重新流式读取已写文件，不整体缓存。manifest 记录 encoding 与 BOM，不记录解码后
全文或推断置信度伪精度。

### 10.3 诊断模型

```text
code                    稳定机器码
severity                INFO | WARNING | ERROR
stage                   ARCHIVE | PATH | EXTRACT | ENCODING | STRUCTURE
logicalPath?            规范相对路径；输出前长度限制和控制字符过滤
messageKey              前端本地化 key，不以异常文本作为协议
arguments               有 allowlist 的数字/枚举，不含物理路径或正文
publishBlocking         bool
```

安全边界失败使用 `GamePackageRejection`，整个摄取失败；内容质量问题使用 diagnostics，摄取可以
READY，但 P1-04 不得发布存在 `publishBlocking=true` 的版本。`MaxDiagnostics` 后不再追加逐项详情，
只累加按 code 的 suppressed count，并增加 `DIAGNOSTICS_TRUNCATED`；不能因攻击者制造百万诊断而
耗尽内存。

至少冻结以下拒绝码：

```text
ARCHIVE_TOO_LARGE / ARCHIVE_FORMAT_UNSUPPORTED / ARCHIVE_CORRUPT
ARCHIVE_ENCRYPTED / ZIP64_UNSUPPORTED / ZIP_METHOD_UNSUPPORTED
ENTRY_COUNT_EXCEEDED / ENTRY_TOO_LARGE / EXPANDED_SIZE_EXCEEDED
COMPRESSION_RATIO_EXCEEDED / PATH_DEPTH_EXCEEDED
PATH_INVALID / PATH_RESERVED_NAME / PATH_COLLISION / PATH_TYPE_CONFLICT
LINK_ENTRY_FORBIDDEN / SPECIAL_ENTRY_FORBIDDEN
STAGING_BUDGET_EXHAUSTED / DATA_ROOT_SPACE_LOW / STAGING_IO_FAILED
INGESTION_CANCELLED / INGESTION_DEADLINE_EXCEEDED
```

## 11. Manifest 与内容摘要

manifest schema v1：

```json
{
  "schemaVersion": 1,
  "archive": {
    "bytes": 1234,
    "digest": "sha256:...",
    "format": "zip"
  },
  "content": {
    "bytes": 5678,
    "fileCount": 3,
    "directoryCount": 2,
    "digest": "sha256:..."
  },
  "files": [
    {
      "path": "ERB/START.ERB",
      "bytes": 321,
      "digest": "sha256:...",
      "kind": "TEXT",
      "encoding": "SHIFT_JIS",
      "bom": false
    }
  ],
  "directories": ["ERB", "CSV"],
  "diagnosticSummary": { "errors": 0, "warnings": 1, "infos": 0 }
}
```

content digest 使用版本化二进制 framing，不拼接易歧义文本：

```text
SHA256(
  UTF8("CloudEmuera.GamePackageContent\0") || UInt32BE(schemaVersion) ||
  for entry in canonicalUtf8PathOrder:
    Byte(kind) || UInt32BE(pathUtf8Length) || pathUtf8 ||
    UInt64BE(fileLengthOrZero) || fileSha256RawOrZero32
)
```

父目录（包括隐式目录）参与摘要，root 不作为条目。manifest JSON 使用 source-generated DTO，
但 JSON 字节本身不作为 content digest 输入。摘要生成后执行独立二次遍历，核对每项类型、owner、
mode、nlink、size 和 SHA-256；检测到与首次提取结果不同即 `STAGED_CONTENT_CHANGED` 硬失败。

archive digest 用于上传重复观测，content digest 用于内容身份。两个不同 ZIP 可拥有相同 content
digest；P1-04 是否复用物理内容仍须保证 Game current content 不变性和授权，不能仅凭 digest 越权引用
另一用户的 staging 目录。

## 12. 清理、崩溃恢复与 TOCTOU

### 12.1 请求内失败

失败顺序：停止读取/解压 → 关闭所有 entry/target fd → CAS 为 FAILED → 使用已持有/重新验证的
staging root fd 清理 → 释放预留。若清理失败，保留 FAILED 行和 reserved budget，交给 reaper；
不能为了让请求看似成功而先释放预算。

### 12.2 启动和周期 reaper

reaper 只领取已过期的非终态行或超过保留期的终态行：

1. 在短事务中 CAS 到 ABANDONED 并取得预期 relative path、owner 和 state version；
2. 从受保护 `games/staging` fd 打开精确的 `{ingestionId}` 子目录；
3. 核对目录 inode 与 `lease.json` 中服务器生成的 ingestionId，不信任 JSON 中的路径；
4. 以不跟随链接的后序遍历删除接受类型；异常类型停止并审计；
5. 删除根目录成功后再删除/归档数据库行并释放预算。

READY lease 过期也回收，P1-04 必须在期限内 BeginConsume。P1-03 尚不存在 Game workspace 提交目标，
因此超时 CONSUMING 由 watchdog CAS 为 ABANDONED 并回收，避免进程崩溃后永久占用预算。P1-04
引入正式提交事务时必须先增加续租和 Game workspace 提交对账，再允许可能超过 watchdog 的消费。

### 12.3 攻击者可写假设

正常部署中用户不能直接写 `/data`，但测试必须模拟服务账户目录被预置 symlink、FIFO、socket、
hardlink、异常 owner/mode、目录替换和 rename race。任何身份不一致都 fail closed；不得删除目标
或继续摄取。若受保护 staging 根本身不安全，readiness 失败并禁止所有新上传。

## 13. 日志、审计与可观测性

结构化日志事件：

```text
game_package.ingestion_reserved
game_package.upload_received
game_package.archive_rejected
game_package.ingestion_ready
game_package.ingestion_failed
game_package.ingestion_reaped
```

允许字段：`requestId`、`ingestionId`、不可逆 `userIdHash`、phase、稳定 result/reason code、
archive/expanded bytes、entry count、durationMs、digest 前 12 hex（仅在成功时且确有排障需要）。
禁止字段：原始文件名、物理 staging 路径、完整 digest 默认值、ERB/CSV 内容、Cookie/token、异常
中可能带出的 entry 正文或绝对路径。

P1-03 记录上传摄取结果审计；P1-04 把 Game workspace/activate 审计放在自己的事务。审计失败是否使
READY 失败遵循 P1-02 的敏感操作原子性：面向用户的摄取完成必须能留下最小审计事实，否则
候选不可消费并进入失败清理。

指标至少包括：active reservations、reserved bytes、received/expanded bytes、各 rejection code、
ingestion duration、compression ratio histogram、diagnostic counts、reaper backlog/bytes/failures。
`ingestionId`、用户和路径不作为时序标签。

## 14. API 接线约束（P1-04 消费）

P1-04 的正式 endpoint 为：

```text
PUT /api/v1/games/{gameId}/package
Content-Type: application/zip
Idempotency-Key: ...
```

接线时必须：先认证和授权 Game owner → 检查 CSRF/rate limit → 建立摄取预留 → 把
`HttpRequest.Body` 直接传给服务 → READY 后在同一用例内消费为 Game workspace。不得先 `MemoryStream`
缓冲，也不得使用 `IFormFile.CopyTo` 的默认临时位置。`Content-Length` 缺失可接受但仍计数；大于
上限可提前 413，小于上限不能免除实际计数。

建议错误映射：

| 情形 | HTTP | code |
| --- | ---: | --- |
| archive/entry/expanded hard limit | 413 | 对应稳定 rejection code |
| 不支持/损坏/路径/链接 | 422 | 对应稳定 rejection code |
| staging 并发预算耗尽 | 429 | `STAGING_BUDGET_EXHAUSTED` + `Retry-After` |
| DataRoot 空间或安全状态异常 | 503 | `DATA_ROOT_*` |
| 客户端取消 | 无业务响应或 499 日志语义 | `INGESTION_CANCELLED` |
| 认证/授权失败 | 401/404 | 沿用 P1-02，不进入摄取 |

原始异常文本不进入响应。对外 path detail 只给已规范并截断的逻辑路径；无权请求不能通过错误差异
判断 Game 是否存在。

## 15. 测试设计

### 15.1 正常与编码

- UTF-8 BOM、UTF-8 无 BOM、纯 ASCII 和 CP932 ERB/CSV 各自得到稳定 encoding；
- 无效 UTF-8 但有效 CP932 不能因 locale 不同得到不同结果；
- UTF-16/32、同时无效 UTF-8/CP932、NUL 文本产生阻断诊断但不越过 staging；
- 同内容不同 ZIP 顺序、timestamp、压缩级别得到相同 content digest；原 archive digest 不同；
- 空文件、空目录、隐式父目录和未知合法普通文件完整保留；
- 非 seek、短读、每次只返回 1 byte 的输入流仍正确；全程峰值内存不随归档大小线性增长。

### 15.2 恶意归档 corpus

corpus 必须由测试代码确定性生成最小 ZIP；二进制 fixture 只在无法表达畸形 header 时提交，并附
README 描述来源、预期 code 和许可证。至少覆盖：

- `../x`、`a/../../x`、`/x`、`C:/x`、UNC、反斜杠、NUL、`.`、空段、尾随点/空格；
- CON/AUX/COM1 等保留名，超长 segment/path/depth；
- NFC/NFD、大小写、Unicode case、重复 entry、file/dir prefix 碰撞；
- Unix symlink、FIFO、socket、device、已知 hardlink extra、DOS reparse；
- 0-byte 高展开、极端 ratio、伪造小 size、超单文件/总展开/条目数；
- truncated deflate、坏 CRC、local/central name mismatch、overlap、offset overflow、重复/畸形 extra；
- encrypted、未知 method、ZIP64、multi-disk、polyglot/trailing data；
- archive exactly-at-limit 成功和 limit+1 在写越界字节前失败。

每个拒绝测试断言稳定 code、目标外文件不存在、`ready/` 不存在、FD/预算最终不泄漏；不能只断言
“抛出异常”。

### 15.3 文件系统、竞争与故障

- 两个请求在只剩一个 staging reservation 时只有一个成功预留；
- API 在 RECEIVING/EXTRACTING/ANALYZING/READY 后被模拟终止，reaper 得到确定结果；
- cancellation、deadline、ENOSPC、短写、fsync/rename/SQLite CAS/审计失败逐点注入；
- staging 根或 ingestion 目录被替换为 symlink/FIFO/socket/其他 inode 时不跟随、不删除目标；
- 解包预检后条目或 archive 被替换，二次 header/identity 校验失败；
- 目标文件被 hardlink/替换、`nlink != 1` 或 inode 重复时最终扫描失败；
- reaper 与 BeginConsume 并发时只有 READY→CONSUMING 或 READY→ABANDONED 一个 CAS 成功；
- 多次 Dispose/Abandon/Complete 幂等，不重复释放 reservation 或删除已消费内容。

### 15.4 测试分类与需求映射

| Category | 重点 | 需求/验收 |
| --- | --- | --- |
| `ArchiveSecurity` | ZIP、路径、链接、碰撞、TOCTOU | GAME-002、SEC-005、AC-010 |
| `ArchiveQuota` | 压缩前/后、ratio、并发预算 | GAME-002、OPS-002 |
| `Encoding` | UTF-8/CP932 确定性与诊断 | GAME-003、COMP-005 |
| `Manifest` | 文件 hash、canonical digest | GAME-004 前置能力 |
| `IngestionRecovery` | 取消、崩溃、reaper、无半成品 | AC-010、NFR-011 |
| `PackageDiagnostics` | 有界诊断和结构错误 | GAME-007、COMP-006 |

## 16. 实施顺序

### 阶段 A：ADR、契约与 migration

1. 接受 ADR-0008 并冻结 ZIP/路径/配额口径；
2. 建立 Application request/result/limits/diagnostic/manifest 契约；
3. 新增 ingestion entity、EF configuration、migration 和约束测试；
4. 先建立 corpus generator 和稳定 rejection code 参数化测试骨架。

### 阶段 B：安全 staging 与流式接收

1. 复用/提取 P1-01 Linux dirfd 原语；
2. 实现 staging 根验证、预算预留和安全目录创建；
3. 实现有界 stream spool、archive hash、取消和精确 limit；
4. 实现失败清理与 reaper 最小闭环。

### 阶段 C：ZIP inspector 与路径策略

1. 解析 EOCD/central/local headers 和 extra fields；
2. 冻结 accepted methods/flags 与声明配额；
3. 实现 UTF-8/ASCII name、NFC/portable name/collision trie；
4. 让全部 ArchiveSecurity corpus 在“尚未落文件”阶段得到预期拒绝。

### 阶段 D：安全解包与实际配额

1. 串行提取到 `candidate.work/content`，仅创建目录/普通文件；
2. 强制 actual per-file/total/ratio/CRC/size；
3. 逐文件 SHA-256、inode/mode/owner/nlink 二次扫描；
4. 完成故障注入、取消和 TOCTOU 测试。

### 阶段 E：编码、manifest 与 READY 消费

1. 实现严格 UTF-8/CP932 流式检测；
2. 实现有界诊断和 manifest schema v1；
3. 实现 canonical content digest 和 `candidate.work → ready`；
4. 实现 BeginConsume/Complete/Abandon 与 reaper 竞争测试。

### 阶段 F：集成与文档

1. 增加非 seek transport harness，证明未来 HTTP adapter 无需内存缓冲；
2. 接入日志、审计、配置校验、metrics；
3. 更新总体设计、开发计划和 P1-04 交接说明；
4. 运行定向测试、全量质量门和 `git diff --check`。

## 17. 验证命令

所有命令必须在 dev Docker 内运行：

```bash
./scripts/dev-up.sh

source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.GamePackages.Tests --no-restore \
  --configuration Release --filter 'Category=ArchiveSecurity|Category=Encoding'

source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.GamePackages.Tests --no-restore \
  --configuration Release

./scripts/check.sh
./scripts/verify-third-party.sh
git diff --check
./scripts/dev-down.sh
```

测试项目和命令在实施前尚不存在；P1-03 只有在它们真实存在、使用 locked restore、返回正确退出码
且全量质量门通过后才能标记 DONE。

## 18. 完成定义

P1-03 只有同时满足以下条件才完成：

1. 正常 UTF-8/CP932 ZIP 从非 seek 流摄取为 READY，manifest 与 digest 可重复；
2. 章节 15 的恶意 corpus 每类至少一个测试，稳定返回预期 rejection code；
3. 声明和实际配额均覆盖 exactly-limit 与 limit+1；压缩炸弹不会先写出超限内容；
4. 所有 path/link/special/collision 失败均不在 staging 外创建或修改文件；
5. 任一失败在请求清理或跨重启 reaper 对账完成后不留下 `ready/content`，最终 Game workspace
   始终保持不存在；
6. 并发预算只有一个胜者，跨重启 reaper 不依赖 API 内存恢复预留；
7. TOCTOU、取消、超时、磁盘满和 SQLite/rename 失败有自动化覆盖；
8. staging 最终树只含服务账户拥有的普通文件/目录，文件 `nlink == 1`，无共享 inode；
9. diagnostics 有界、日志和审计不泄露内容、绝对路径或认证秘密；
10. Domain/Application 依赖方向和 P0-05 Game current content 完整复制契约未被破坏；
11. migration 可从 P1-02 数据库升级并保持数据，失败不留下部分 schema；
12. 定向测试、`./scripts/check.sh`、第三方校验和 diff check 全部通过；
13. 开发计划记录实际测试数量、命令、恶意 corpus 范围和剩余 P1-04 接线事项。

## 19. P1-04 交接清单

P1-04 只能在以下 P1-03 契约上继续：

- 上传 adapter 传原始 request body stream、actor、requestId 和 idempotency context；
- 只有 READY 且 owner/digest/expiry 匹配的 lease 能进入 CONSUMING；
- workspace 创建必须复制或原子移动完整 `ready/content` 和 manifest，不能按已知目录丢弃文件；
- Game workspace 数据库事务失败时调用 Abandon/对账，不暴露无 DB 引用的目录；
- P1-04 可以追加 parser/resource/capability diagnostics，但不能降低 P1-03 安全 rejection；
- 发布时重新验证 manifest、文件类型和摘要并转只读；不得相信数小时前的 staging 检查替代发布
  时的 TOCTOU 复核；
- P1-04 决定 idempotency response，P1-03 不创建业务 Game 或内容 revision；
- P1-04 必须在正式 Game workspace 提交接入前增加 CONSUMING 续租与提交对账，不能把 P1-03
  “尚无业务提交目标时超时即放弃”的 watchdog 规则原样用于长事务。
