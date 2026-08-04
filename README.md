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
- API：<http://localhost:8080>
- API 存活检查：<http://localhost:8080/health/live>

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
src/                  产品代码
tests/                .NET 单元与集成测试
e2e/                  Playwright 端到端测试
docs/                 需求、设计和 ADR
deploy/               容器与部署配置
scripts/              开发、检查和三方源码维护脚本
third_party/          固定版本的上游源码 submodule
patches/              CloudEmuera 针对上游的可审查补丁
data/                 本地运行数据，不提交 Git
```

## 三方源码

Emuera.EM+EE 以 Git submodule 检出到 `third_party/emuera-em`，固定提交和更新流程见 [third_party/README.md](third_party/README.md)。首次克隆后运行：

```bash
git submodule update --init --recursive
```

不得直接把未记录来源的上游文件复制进 `src/`。CloudEmuera 修改应保存在适配层或 `patches/emuera/`，并在运行时清单中记录上游提交。

## 项目状态

当前骨架只提供健康检查、版本端点、进程入口和前端占位页。开发环境和工程基线 P0-00 已通过验证；下一步是 P0-01 兼容测试资产与运行时基线。Session、运行时适配、身份、存档和实时协议按 [开发计划](docs/development-plan.zh-CN.md) 分阶段实现。

## 贡献与安全

提交前请运行 `./scripts/check.sh`。安全问题请按 [SECURITY.md](SECURITY.md) 中的方式私下报告，不要在公开 Issue 中附带游戏包、存档、令牌或用户输入。

## 许可证

CloudEmuera 自研代码使用 [Apache License 2.0](LICENSE)。三方源码不因根许可证而重新授权，继续保留各自的版权和许可证声明，详见 [NOTICE](NOTICE) 与 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
