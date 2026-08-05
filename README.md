# CloudEmuera

CloudEmuera 将 Emuera.EM+EE 文字游戏运行时部署到远程服务器，使玩家能够通过桌面或移动浏览器管理游戏包、运行彼此隔离且可重连的 Session，并管理各自的原生 Emuera 存档。

项目目前处于架构验证和开发环境初始化阶段，不建议用于托管不受信任用户上传的游戏。

## 技术栈

- .NET 10 LTS、ASP.NET Core、EF Core 和 SQLite；
- React 19、TypeScript 7、Vite 8；
- 浏览器使用 WebSocket，容器内进程使用 gRPC over Unix Domain Socket；
- Web/API、Worker Supervisor 和每 Session 独立 Worker；
- Docker Compose 开发环境，生产目标为单容器部署。

详细决策见 [需求文档](docs/requirements.zh-CN.md)、[详细设计](docs/design.zh-CN.md) 和 [可验证开发计划](docs/development-plan.zh-CN.md)。自动化开发代理还应先阅读 [CLAUDE.md](CLAUDE.md)。

## 快速开始

推荐只安装 Docker 28+ 和 Docker Compose v2：

```bash
cp .env.example .env
./scripts/dev-up.sh
```

启动脚本会读取宿主机的 `id -u` 和 `id -g`，以此构建 API 与 Web 开发镜像，并让所有容器进程使用相同 UID/GID。容器写入的 `node_modules`、`bin`、`obj` 和 lockfile 因而仍归当前开发用户所有。不要直接运行未注入 UID/GID 的裸 `docker compose up`；如需手工运行，请先执行：

```bash
export CLOUDEMUERA_UID="$(id -u)"
export CLOUDEMUERA_GID="$(id -g)"
docker compose -f compose.dev.yaml up --build
```

可独立验证两个开发镜像的运行身份和 bind mount 写入归属：

```bash
./scripts/verify-dev-user.sh
```

启动后：

- 前端开发服务器：<http://localhost:5173>
- API：<http://localhost:18080>
- API 存活检查：<http://localhost:18080/health/live>

停止环境：

```bash
./scripts/dev-down.sh
```

执行完整检查：

```bash
./scripts/check.sh
```

如果选择原生开发，需要 .NET 10 SDK、Node.js 24 LTS 和 pnpm 11：

```bash
dotnet restore CloudEmuera.slnx --locked-mode
dotnet test CloudEmuera.slnx --no-restore
corepack pnpm install --frozen-lockfile
corepack pnpm --dir src/CloudEmuera.Web test
corepack pnpm --dir src/CloudEmuera.Web build
```

## 仓库结构

```text
src/                  产品代码及固定版本的 Emuera 内置源码
tests/                .NET 单元与集成测试
e2e/                  Playwright 端到端测试
docs/                 需求、设计和 ADR
deploy/               容器与部署配置
scripts/              开发、检查和内置源码维护脚本
data/                 本地运行数据，不提交 Git
```

## 三方源码

Emuera.EM+EE 以普通 Git 文件形式固定在 `src/CloudEmuera.EmueraRuntime/Upstream`，不使用 submodule。来源仓库、固定提交、原始 tree ID、许可证和更新流程见 [UPSTREAM.md](src/CloudEmuera.EmueraRuntime/UPSTREAM.md)。

首次克隆无需额外初始化。对内置源码的修改必须保留上游声明，并登记在 `MODIFICATIONS.md`；运行时清单同时记录上游提交和 CloudEmuera integration version。上游升级必须以独立导入提交进行，不能跟踪浮动的“最新版”。

## 项目状态

开发环境、运行时 fixture、平台端口、结构化 Console、P0-05 持久 SessionRoot 和 P0-06 单 Session Worker/UDS IPC 已通过验证；固定上游 Emuera loader/interpreter 已在 Linux 无 UI Runtime 中跑通两套 INPUT 往返，真实独立 Worker 也完成注册、结构化输出、重复输入、短断重连、优雅停止和双进程隔离。下一步是 P1-01 SQLite 首版 schema 与迁移；租约、epoch 持久化、浏览器实时 API、身份和正式 UI 按 [开发计划](docs/development-plan.zh-CN.md) 分阶段实现。

## 贡献与安全

提交前请运行 `./scripts/check.sh`。安全问题请按 [SECURITY.md](SECURITY.md) 中的方式私下报告，不要在公开 Issue 中附带游戏包、存档、令牌或用户输入。

## 许可证

CloudEmuera 自研代码使用 [Apache License 2.0](LICENSE)。三方源码不因根许可证而重新授权，继续保留各自的版权和许可证声明，详见 [NOTICE](NOTICE) 与 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
