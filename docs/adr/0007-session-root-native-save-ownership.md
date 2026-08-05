# ADR-0007：SessionRoot 直接持有 Emuera 原生存档

状态：Accepted

日期：2026-08-05

## 背景

早期设计把运行中的存档工作副本和独立 `SaveArtifact` generation 分开，计划在每次
原生保存后执行复制、摘要、原子发布和数据库索引。这会在 Emuera 已经拥有原生文件
读写语义之外再建立一套提交协议，并要求 Worker 判断何时一次游戏保存已经成功。

CloudEmuera 的单机 MVP 已经为每个 Session 分配持久、独占的本地目录。只要该目录
位于 `/data` 挂载中，Emuera 写入的原生文件本身就是持久状态，无需在运行链路中再
复制一份。

## 决定

- 每个 Session 从创建到删除始终拥有一个持久、独占的 `SessionRoot`；它是该
  Session 完整游戏副本、配置、临时状态和原生存档的唯一权威文件树。
- Session 管理方在首次启动前按发布 manifest 把固定 GameVersion 的完整合法普通
  文件树复制到 staging root。CSV、ERB、resources、配置和管理方不认识的游戏目录
  使用同一规则，不做 mount/copy/discard 分类。
- 复制完成后重新校验文件类型、数量、总大小和摘要，再把 staging 原子重命名为最终
  SessionRoot。复制失败不能留下可启动的半目录，也不能删除已有 SessionRoot。
- 普通字节复制是必需基线。支持时可以用 reflink 作为保持相同独立写语义的优化，
  不支持时自动回退普通复制；禁止硬链接以及任何形式的共享可写 inode。
- 游戏包中的软链接、硬链接、FIFO、设备和 socket 在上传阶段拒绝，复制阶段再次核对，
  不存在管理方链接例外。
- Emuera 以 `SessionRoot` 作为 `Program.ExeDir`，直接使用原生 reader/writer。
  `UseSaveFolder:NO` 写根级 `save*.sav`/`global.sav`，`YES` 写 `sav/`。
- `UseSaveFolder` 来自游戏版本的 `emuera.config`，是布局的唯一权威。宿主记录的布局
  只能用于校验，不能覆盖配置。
- Worker 只能看到自己的 SessionRoot，不需要看到原始 GameVersion。正常停止、崩溃或
  重启都不触发再次复制、存档物化、提交或 generation 发布。
  重启同一 Session 时复用原目录。
- MVP 不建立 `SaveArtifact` 领域实体或数据库表。停止态存档 API 直接授权访问
  SessionRoot 中允许的原生文件；活动 Worker 存在时禁止外部修改。
- CloudEmuera 不改变固定上游保存时使用的打开、覆盖、flush 或失败语义。备份与历史
  恢复针对静止的整个 SessionRoot 或 `/data` 执行，不拦截每次游戏保存。

## 备选方案

### 每次保存发布不可变 SaveArtifact

可提供逐次 generation 和更强的崩溃恢复点，但需要识别所有上游保存入口、处理提交
竞态、维护第二份权威数据和数据库索引。MVP 的 Session 私有持久目录已经满足保存与
隔离目标，因此不采用。

### 为每个游戏内容目录使用只读 bind mount

节省内容副本空间，但要求判断哪些路径可写；未知自定义目录可能因只读而破坏兼容性，
并引入 mount namespace、权限、失败回滚和卸载生命周期。MVP 不采用。

### 为已知目录建立软链接

实现容易但不能可靠判断未知目录是否会被游戏写入，且链接目标和路径逃逸增加安全
复杂度，因此不采用。

### OverlayFS lower/upper

可以同时提供完整目录语义和按写入付费的空间效率，但需要 Linux mount 能力、whiteout
语义、崩溃清理和专门测试。若完整复制的实测成本不可接受，可在不改变 Runtime 接口的
前提下另立 ADR 引入。

## 后果

- Runtime 无需定义存档内容格式、提交通知或 generation 协议，保存兼容性更接近原版。
- 同一 Session 的 Worker 重启天然看到原有存档；不同 Session 通过不同实际目录隔离。
- 未知合法文件和目录天然保留，游戏可以在自己的完整副本内修改任意内容。
- Session 创建时间、磁盘空间和 inode 数随 GameVersion 大小增长；配额必须在复制前
  预留并在复制过程中强制执行。reflink 只能优化成本，不能成为正确性前提。
- 进程在原生 writer 覆盖文件期间退出时可能留下上游本来会产生的现场；产品不得宣称
  每次保存都具有事务性历史恢复点。
- 存档管理操作必须与 Worker 租约互斥。上传、替换、重命名、复制和删除只允许在没有
  活动 Worker 时执行。
- 原始 GameVersion 摘要必须在 Session 运行前后保持不变，Worker 沙箱不得暴露源目录。

## 验证

- P0-05 的 `save-root` 与 `save-directory` 场景分别以真实 Emuera 保存、退出、复用同一
  SessionRoot 启动并加载原值。
- 两个用户及同一用户的两个 Session 绑定同一 GameVersion 时，存档路径、内容和
  `global.sav` 完全独立，GameVersion 摘要保持不变。
- layout builder 测试证明 manifest 中每个合法条目都被复制、未知目录未丢失、源和目标
  不共享可写 inode，并拒绝链接、特殊文件、摘要变化、配额超限和半成品恢复。
- 测试和运行报告证明不存在 `SaveArtifact`、generation 或关闭时复制步骤。
