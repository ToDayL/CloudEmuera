# CloudEmuera 项目上下文

本文档供参与本仓库开发的 Claude Code 和其他自动化开发代理使用。开始修改前，先阅读本文件、`docs/requirements.zh-CN.md`、`docs/design.zh-CN.md` 和与当前任务相关的 ADR。发生冲突时，以用户当前指令和需求文档为准，并同步修正文档。

## 项目目标与当前阶段

CloudEmuera 将 Emuera.EM+EE 文字游戏运行时部署到远程服务器，让玩家通过桌面或移动浏览器管理游戏包、运行相互隔离且可重连的 Session，并管理 Emuera 原生存档。

Phase 0 运行时切分与兼容性证明已经完成，当前进入 Phase 1 单机 MVP 实施阶段。已有内容包括：

- Git 仓库、解决方案和前后端工程骨架；
- API 健康检查和版本端点；
- API、Worker、Migrator 进程入口，以及待按 ADR-0015 移除的历史 Supervisor 入口；
- Session 状态机的最小领域实现和测试；
- React 占位应用、单元测试和 Playwright 骨架；
- Docker Compose 开发环境及宿主 UID/GID 映射；
- 固定版本、直接纳入仓库的 Emuera.EM+EE 源码；
- Runtime fixture、平台端口和结构化 Console/Input；
- 原生双布局存档与持久私有 SessionRoot；
- 历史单 Session Worker/Supervisor UDS IPC 冒烟链路；
- SQLite 首版 schema、独占 Migrator、迁移备份与约束测试；
- React 游戏库、Session、控制台、存档和管理页面展示版；
- Apache-2.0 项目许可证和第三方许可声明。

Game 库后端纵切、真实 parser-only Validator、dirfd/fsync 存储和恢复加固已经完成；持久 Worker
管理、浏览器实时协议、正式存档管理和正式 Session UI 仍待实现。P0-06 已提供最小的单 Session
Worker/Supervisor UDS IPC 历史冒烟链路，P1-01 已完成首版 SQLite 持久化基线，P1-02 已完成本地
身份、资源授权与审计，P1-03 已完成安全游戏包摄取，P1-04 已完成单一 Game
workspace/current content 模型。当前任务是 P1-05：依据 ADR-0015 把 Worker 管理迁入 API、移除
独立 Supervisor，并依据 ADR-0016 建立可反复 open/close/reopen 的持久 SessionRoot 生命周期。

## 已确认技术方案

- 后端：.NET 10 LTS、ASP.NET Core、EF Core 10、SQLite；
- 前端：React 19、TypeScript 7、Vite 8、TanStack Query、React Router；
- 浏览器实时通信：原生 WebSocket，HTTP 负责资源和管理操作；
- 容器内通信：API 与 Worker 使用 gRPC over Unix Domain Socket；
- 进程模型：一个 Web/API 控制面直接管理每个活动 Session 的独立 Worker；无独立 Supervisor；
- 数据库所有权：运行期间只有 API 业务进程访问 SQLite，Migrator 仅在 API 启动前独占执行；
- 持久化：SQLite 保存元数据，挂载的数据目录保存 Game workspace/current content、SessionRoot 和存档；
- 部署：MVP 为单容器，开发环境使用 Docker Compose；
- 沙箱方向：Linux namespace、cgroup、seccomp 和只读/私有文件系统边界；
- 许可证：CloudEmuera 自研代码使用 Apache-2.0；Emuera.EM+EE 保留 zlib/libpng 许可证。

不得在没有 ADR 和验证证据的情况下，把已确认方案替换为独立 Supervisor、SignalR、PostgreSQL、
Redis、消息队列、Kubernetes、同进程多 Session 或多主机调度。

## 核心架构约束

1. 每个活动 Session 只能有一个有效 Worker，并用递增 epoch fencing 拒绝旧 Worker 的心跳、输出和输入结果；同一时刻只能把 SessionRoot 写权限交给该 Worker。
2. 浏览器断开不会改变 Session 的持久运行状态或关闭 Worker；连接数由 Realtime Gateway 瞬时维护，
   不建立 `DETACHED` Session 状态，也不能把无浏览器连接偷换为挂起状态。
3. Session 是持久 SessionRoot，不是一次性 Worker。`CLOSED` 和完成旧 Worker 回收的 `CRASHED`
   都可用同一 Session ID/目录重新开启；open/close 只获取或释放 Worker，创建后不再复制 Game
   current content，关闭或崩溃不删除目录。
4. 产品不建立 GameVersion。每个 Game 最多一个可编辑 workspace 和一个当前只读 content；新内容
   验证后原子替换 current，不提供版本列表、版本标签或历史回滚。
5. 每个 Session 使用独立、持久的实际 SessionRoot。创建时把 Game 当时 current content 的完整
   合法普通文件树复制进去，并记录源摘要/manifest 快照；Worker 只读写该副本，Game 后续编辑不
   改变既有 SessionRoot，库内容与 Session 不共享可写 inode。
6. 必须兼容根目录存档与 `sav/` 两种 Emuera 原生布局，不定义替代存档格式。
7. Worker 只输出结构化、可校验的 Console 事件，不把任意游戏 HTML 直接交给浏览器。
8. 输出使用单调递增 sequence；输入使用 `promptId + clientMessageId`，重复输入不得执行两次。
9. 授权检查必须位于服务端的每个资源操作边界，不能依赖前端隐藏入口。
10. 上传和文件操作必须防御路径穿越、绝对路径、符号链接逃逸、Unicode/大小写碰撞、压缩炸弹和 TOCTOU。
11. Session、WorkerLease、epoch 和配额正确性不能只依赖 API 进程内临时状态；API 退出时 Worker
    必须有界退出，新 API 确认旧写权限释放后把活动 Session 对账为 `CRASHED`。

## 仓库结构

```text
src/                  产品代码及固定版本的 Emuera 内置源码
tests/                .NET 单元与集成测试
e2e/                  Playwright 端到端测试
docs/                 需求、设计、ADR 和开发计划
deploy/               容器与部署配置
scripts/              开发、验证和内置源码维护脚本
data/                 本地运行数据，不提交 Git
```

解决方案中的职责：

- `CloudEmuera.Domain`：领域实体、值对象和纯业务约束；
- `CloudEmuera.Application`：用例、端口、授权和事务编排；
- `CloudEmuera.Contracts`：HTTP、WebSocket 和共享版本契约；
- `CloudEmuera.Infrastructure`：EF Core、文件系统和外部实现；
- `CloudEmuera.Ipc`：API/Worker 的 protobuf/gRPC 契约；
- `CloudEmuera.RuntimeAdapter`：平台无关的 Console/Input/File/Clock/Media 契约；
- `CloudEmuera.EmueraRuntime`：内置 Emuera 源码、headless host 与平台接线（P0-04 建立可构建项目）；
- `CloudEmuera.Api`：HTTP/WebSocket、Worker IPC 与 Worker Manager 宿主；
- `CloudEmuera.Supervisor`：P0-06 历史实现；P1-05 迁移可复用代码后删除；
- `CloudEmuera.Worker`：单 Session 运行时宿主；
- `CloudEmuera.Migrator`：数据库和数据布局迁移；
- `CloudEmuera.Web`：浏览器客户端。

依赖方向应保持 `Domain ← Application ← 外部实现` 的整洁边界，以及 `RuntimeAdapter ← EmueraRuntime ← Worker` 的单向依赖关系。Domain 不得引用 EF Core、ASP.NET Core、文件系统或上游 UI 类型。

## 开发环境与强制命令

推荐通过脚本运行开发环境：

```bash
./scripts/dev-up.sh
./scripts/dev-down.sh
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
```

构建与测试必须通过仓库的 dev Docker 运行，不要依赖宿主机上的 .NET SDK、Node.js
或 pnpm。完整验证使用：

```bash
./scripts/dev-up.sh
./scripts/check.sh
```

`dev-up.sh` 会通过 `scripts/lib/dev-env.sh` 注入宿主 UID/GID 并构建、启动开发容器；
`check.sh` 会在容器中依次执行 locked NuGet restore、runtime fixture 校验、Release
构建、.NET 测试，以及 Web 的 frozen install、typecheck、测试和 production build。
完成后可运行 `./scripts/dev-down.sh` 停止环境。需要定向验证时，也必须使用 dev
Docker，例如：

```bash
docker compose -f compose.dev.yaml run --rm api \
  dotnet test CloudEmuera.slnx --no-restore --configuration Release
docker compose -f compose.dev.yaml run --rm web \
  sh -c 'pnpm install --frozen-lockfile && pnpm typecheck:web && pnpm test:web && pnpm build:web'
```

常用子测试集也必须在 dev Docker 中运行。`--filter` 使用 xUnit trait 的
`Category` 名称；修改后端代码时可按范围选择：

```bash
# Domain 全部测试
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.Domain.Tests --no-restore --configuration Release

# RuntimeAdapter：路径/架构/Console 契约
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore \
  --configuration Release --filter 'Category=RuntimePaths|Category=Architecture'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore \
  --configuration Release --filter 'Category=ConsoleContract'

# 真实 headless Emuera：RuntimeBridge 或全部兼容性测试
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore \
  --configuration Release --filter 'Category=RuntimeBridge'
docker compose -f compose.dev.yaml run --rm api \
  dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore \
  --configuration Release

# 运行 P0-04 input-roundtrip 兼容性场景（不是 xUnit filter）
docker compose -f compose.dev.yaml run --rm api \
  bash -lc './scripts/test-runtime-compat.sh --scenario input-roundtrip'
```

不要直接运行宿主机的 `dotnet build/test` 或 `pnpm`，也不要绕过
`scripts/lib/dev-env.sh` 使用未注入 UID/GID 的裸 `docker compose` 命令。

所有开发容器和开发镜像必须使用当前宿主用户的 UID/GID。不得直接以 root 运行会写入 bind mount 的开发容器，也不得绕过 `scripts/lib/dev-env.sh`。新增 Compose 服务时必须同时做到：

- 构建参数接收 `CLOUDEMUERA_UID` 和 `CLOUDEMUERA_GID`；
- Compose `user` 显式使用相同 UID/GID；
- HOME、工具缓存和 bind mount 写入目录对该用户可写；
- 扩展 `scripts/verify-dev-user.sh`，实际创建文件并检查宿主所有权。

NuGet 和 pnpm 依赖必须锁定。修改依赖后更新并提交 `packages.lock.json` 或 `pnpm-lock.yaml`，然后验证 locked/frozen restore。不要提交 `bin/`、`obj/`、`node_modules/`、`dist/`、运行数据或密钥。

## 上游源码规则

Emuera.EM+EE 以普通 Git 文件位于 `src/CloudEmuera.EmueraRuntime/Upstream`，来源提交为：

```text
2175f8a629257efb08214e093704b3a3d3d06d05
```

- 首次导入和每次上游升级使用独立提交，禁止跟踪浮动分支；
- 优先在 `CloudEmuera.RuntimeAdapter` 建立稳定平台契约，在 `CloudEmuera.EmueraRuntime` 完成真实解释器接线；
- 修改 `Upstream/` 时必须在 `MODIFICATIONS.md` 记录原因、范围和验证结果，并在修改文件保留显著变更说明；
- 更新上游时同步修改 `UPSTREAM.md`、`RuntimeBaseline`、验证脚本、第三方声明和兼容性报告；
- 保留上游 zlib/libpng 及其捆绑组件的原始版权和许可证声明。

## 测试与完成定义

实现任务不得只以“能够编译”作为完成标准。每个开发步骤必须在 `docs/development-plan.zh-CN.md` 中具有可执行验证，并至少满足：

1. 新行为有对应的自动化测试或可重复验证脚本；
2. 测试名称或注释映射相关需求编号；
3. 正常路径、边界条件和一个主要失败路径得到覆盖；
4. 涉及并发、文件系统、授权或恢复时，必须包含竞争/故障/恶意输入测试；
5. `./scripts/check.sh` 通过；
6. 文档、协议版本、迁移和许可证声明按变更同步更新。

优先测试层级为领域单元测试、Runtime 兼容测试、协议契约测试、文件系统安全测试、组件集成测试、浏览器测试、故障注入、性能和视觉回归。禁止为了让检查通过而全局关闭警告、漏洞审计或分析器。

## 开发顺序

按 `docs/development-plan.zh-CN.md` 的编号顺序推进。Phase 0、P1-01～P1-04 已完成；当前首要工作
是 P1-05：迁移独立 Supervisor 到 API Worker Manager，实现运行期 SQLite 单进程所有权、API
生命周期绑定和可重开的持久 Session 状态机。P1-02 自动化身份校验必须继续与人工 `.env`、
`./data` 和 Compose project 隔离。

涉及待决事项时，在实现前创建 ADR，至少记录背景、选项、决策、后果和验证方案。不要以临时代码默默固化产品或安全决策。

## Git 提交规范

所有提交必须同时遵守以下规则：

1. 必须包含 DCO signoff。创建提交时使用 `git commit -s`；提交消息末尾必须包含 `Signed-off-by: <姓名> <邮箱>`，并与 Git 配置中的提交者身份一致。
2. 提交标题必须使用固定的 Conventional Commits 格式：

   ```text
   <type>(<scope>): <summary>
   ```

   `scope` 必须填写。`type` 使用 `feat`、`fix`、`docs`、`refactor`、`test`、`build`、`ci`、`chore`、`perf`、`style` 或 `revert`；`summary` 使用祈使式、简洁描述，不以句号结尾，长度不超过 72 个字符。
3. 标题与正文之间空一行；需要补充实现背景、行为变化或验证信息时，在正文说明。提交脚注使用 Git trailer 格式。
4. 一个提交只包含一个逻辑变更；提交前必须确认暂存区内容和 signoff 均正确。
