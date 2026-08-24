# ADR-0008：安全 ZIP 摄取边界与配额口径

状态：Accepted

日期：2026-08-09

2026-08-24 修订说明：[`ADR-0032`](0032-one-step-game-upload-and-load-only-validation.md) 取代本文的
可移植路径/名称策略、链接与特殊类型拒绝、ASCII-only ZIP 名称以及内容扫描门槛。容量、归档可读性、
受保护 staging、无歧义物化、预算与恢复机制继续有效。

## 背景

P1-03 需要接收不受信任的游戏包。总体设计已经选择 MVP 仅支持 ZIP，并要求拒绝路径穿越、
链接、大小写/Unicode 碰撞、压缩炸弹和超配额输入，但尚未冻结 ZIP 子集、文件名编码、路径
规范化、压缩比口径、并发 staging 预算及候选内容的提交边界。

仅依赖 `System.IO.Compression.ZipArchive` 枚举并把 `FullName` 拼到目标路径，无法充分审查中央/
local header 不一致、external attributes、extra field、重复与 file/dir prefix 冲突；只检查 ZIP
声明的展开大小也不能防御伪造 header。只用 API 进程内 semaphore 限制并发上传，则进程重启会
丢失预算，也不满足系统正确性不能依赖 API 临时状态的约束。

## 决定

- MVP 只接受 single-disk classic ZIP 的 Stored 和 Deflate 普通文件/目录；拒绝 ZIP64、多卷、
  加密、未知 method、自解压前缀、EOCD 后置 payload、链接和特殊文件。扩展格式需另立 ADR。
- 原始上传先以流式、有界方式写入受保护 staging 文件。因为 ZIP 中央目录需要 seek，不尝试在
  HTTP body 上边读边直接解包。
- 在任何条目落盘前，自行预检 EOCD、central/local headers、flags、method、offset、extra、
  external attributes、全部路径和声明配额；解码按预检得到的 offset/length 使用有界 Stored/
  `DeflateStream`，不再依赖 `ZipArchive` 的二次名称解释。
- ZIP name 有 UTF-8 flag 时严格按 UTF-8 解码；否则只接受 ASCII，允许 CRC 匹配的 Unicode Path
  extra 提供严格 UTF-8 名称。内容编码独立检测 UTF-8 与 Windows-31J/CP932。
- 目标逻辑路径统一为 NFC 和 `/`；拒绝绝对路径、父级、反斜杠、冒号、控制字符、Windows
  保留名、尾点/空格、超长路径，并以 NFC 后的 ordinal case-insensitive key 检测碰撞。
- 解包只通过受保护目录 fd 创建 `0700` 目录和 `0600` 普通文件，不应用 ZIP 权限、owner、时间、
  xattr 或链接 metadata。完成后再次验证类型、owner、mode、`nlink == 1` 和 inode 唯一性。
- 声明 entry count/size/ratio 在写文件前预检；实际单文件字节、总展开字节和 ratio 在每次写前
  强制。声明值永远不是最终配额依据。
- 用户 `max_game_package_bytes` 限制原始 ZIP；用户 `max_session_bytes` 同时作为展开内容上限的
  上界。部署配置可进一步收紧，不可扩大用户 quota。
- 初始部署安全默认值为：原始 ZIP 最多 2 GiB、展开内容最多 4 GiB、单文件最多 1 GiB、条目
  最多 50,000、中央目录最多 64 MiB、目录深度最多 32、压缩比最多 200、segment/path UTF-8
  最多 255/1024 bytes、单次诊断最多 1,000、摄取 deadline 15 分钟。实际有效值始终取用户 quota
  与部署配置较小者。50,000 低于 classic ZIP 的 65,535 条目格式上限。
- 全局 staging 预留默认 12 GiB，DataRoot 保留空间默认 1 GiB。每次请求在 SQLite 中悲观预留
  `effective archive limit + effective expanded limit`，终态清理后释放；运维可按磁盘容量收紧。
- 解包只写 `candidate.work/content`。完整分析、manifest 定稿和二次摘要通过后，同文件系统原子
  rename `candidate.work` 为 `ready`，并以 SQLite CAS 标记 READY。只有匹配 owner、digest、
  期限的 READY 能一次性进入 CONSUMING。
- staging 只通过受保护根 dirfd 下的 `mkdirat/openat(O_NOFOLLOW)/renameat/unlinkat` 操作；清理
  是不跟随链接的后序遍历。数据库终态先线性化，`cleanup_completed_at` 标记幂等物理清理完成，
  API 启动及周期后台 reaper 负责跨重启收敛。
- 每个 staging 根在写入任何归档内容前创建 mode 0600 的 `lease.json`，绑定 ingestion ID 与根目录
  `(device, inode)`；请求清理和 reaper 都必须通过根 fd 校验 lease、owner、mode 和 inode 后才能删除。
- 所有可竞争状态转换匹配预期 status 与 `state_version`；`state_version` 同时配置为 EF 并发令牌，
  Abandon 不得通过跟踪实体的无条件更新覆盖并发 Complete。
- `BeginConsumeAsync` 在 READY CAS 前打开并校验 manifest 和 content 目录句柄，返回值不暴露物理
  或相对路径。CONSUMING 使用有限 watchdog lease；P1-04 接入长操作时必须续租，并在接入正式
  Game workspace 提交前增加持久对账，不能依赖无限期 CONSUMING。
- P1-03 不创建 Game 或最终内容目录。正式 HTTP adapter 和 workspace 绑定由 P1-04 完成。

## 备选方案

### 直接从 HTTP 流边读边解包

可以少写一份临时 ZIP，但中央目录位于文件尾部，无法在写出早期条目前完成全局碰撞、entry count
和 header 一致性检查；失败也更难保证零半成品，因此不采用。

### 完全信任 `ZipArchive`

代码较少，但安全策略需要审查 raw name bytes、header、extra 和 Unix file type。框架 API 没有
暴露所有所需证据，因此使用预检 offset 对 Stored/Deflate 字节范围直接做有界解码。

### 接受 CP437/CP932 ZIP 文件名

可能兼容更多旧压缩工具，但在不同生成器和 locale 下有歧义，容易制造规范化/碰撞差异。MVP
要求 ASCII 或明确 UTF-8；Shift-JIS 要求针对文件内容，不等同于归档文件名。

### 自动剥离唯一顶层目录或修复文件名

用户体验更宽容，但会改变内容身份并制造碰撞/引用歧义。摄取保持规范后的完整树，结构问题以
诊断交给草稿编辑修复。

### 仅用进程内并发限制

实现简单，但并发最坏情况预算和 API 重启不可恢复，也无法安全清理孤儿，因此采用 SQLite 持久
预留加文件系统安全余量。

### 把所有内容问题都当作硬拒绝

边界简单，但用户无法在草稿中修复编码/结构错误。归档安全问题硬拒绝；可安全保存的内容质量
问题形成 publish-blocking diagnostic。

## 后果

- ZIP 兼容范围有意小于通用桌面解压软件；被拒绝的包需要用户重新打成标准 UTF-8 classic ZIP。
- 需要维护一个小型 ZIP header inspector 和恶意 corpus，但安全判断可测试且不依赖 framework
  未公开行为。
- 原包与展开树会在摄取期间同时占空间；悲观预留会降低并发度，换取可预测的磁盘上界。
- 2 GiB classic ZIP 不需要 ZIP64，但接近格式上限的包仍受中央目录和条目数限制。
- 同内容不同压缩方式得到相同 content digest，便于 P1-04 核验 Game workspace/current content。
- NFC 规范化可能改变原始 entry name 的码点序列；manifest、物理路径和后续编辑统一使用规范名，
  并产生信息诊断。
- P1-04 必须实现 CONSUMING 与 Game workspace 提交对账，不能绕开 lease 直接按 staging 字符串寻址。

## 验证

- ArchiveSecurity corpus 覆盖路径、header、extra、链接、碰撞、ZIP64、加密和畸形 Deflate；
- ArchiveQuota 覆盖每项 exactly-limit/limit+1、声明/实际不一致、压缩炸弹和并发预留；
- Encoding 覆盖 UTF-8 BOM/无 BOM、ASCII、CP932、无效编码和 locale 独立性；
- IngestionRecovery 覆盖取消、超时、磁盘满、进程中断、READY/reaper 竞争和恶意 staging 条目；
- Manifest 测试证明 ZIP 顺序、timestamp、压缩级别不影响 content digest。
