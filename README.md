# CloudEmuera

CloudEmuera 将 Emuera.EM+EE 文字游戏运行时部署到远程服务器，使玩家能够通过桌面或移动浏览器管理游戏包、运行彼此隔离且可重连的 Session，并管理各自的原生 Emuera 存档。

项目目前处于 Phase 1 单机 MVP 阶段，不建议用于托管不受信任用户上传的游戏。

## 技术栈

- .NET 10 LTS、ASP.NET Core、EF Core 和 SQLite；
- React 19、TypeScript 7、Vite 8；
- 浏览器使用 WebSocket，容器内进程使用 gRPC over Unix Domain Socket；
- Web/API、API-owned Worker Manager 和每 Session 独立 Worker；
- Docker Compose 开发环境，生产目标为单容器部署。

详细决策见 [需求文档](docs/requirements.zh-CN.md)、[详细设计](docs/design.zh-CN.md) 和 [可验证开发计划](docs/development-plan.zh-CN.md)。自动化开发代理还应先阅读 [CLAUDE.md](CLAUDE.md)。

## 快速开始

推荐只安装 Docker 28+ 和 Docker Compose v2：

```bash
cp docker/.env.example docker/.env
# 只有使用 bind mount 时才需要在 docker/.env 设置 CLOUDEMUERA_UID/GID
./scripts/dev-up.sh
```

P1-02 身份功能已落地。首次使用前可在 `docker/.env` 修改管理员 username 和登录 email；临时密码固定示例为
`CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password`。全新数据库启动时只创建一次管理员，
登录仅使用 email，首次登录必须修改临时密码。初始化完成后后续启动忽略这三个 bootstrap 变量。
`docker/.env` 不得提交 Git。

启动脚本会先停止可能仍在运行的旧 API，在 API 容器内由 `dev-start.sh` 先执行 Migrator，再以 API 为唯一长驻进程启动服务；前端由一次性的 `web` frozen install/build 工具容器构建，API 直接服务 `dist` 中的 SPA。开发脚本仍读取宿主机的 `id -u` 和 `id -g`，保证 bind mount 产生的 `node_modules`、`bin` 和 `obj` 归当前开发用户所有。不要直接运行未构建 DLL/SPA 的裸 `docker compose up`；日常开发请使用 `./scripts/dev-up.sh`。需要浏览器 HMR 时，另开终端执行：

```bash
export CLOUDEMUERA_UID="$(id -u)"
export CLOUDEMUERA_GID="$(id -g)"
docker compose --env-file docker/.env -f docker/compose.dev.yml --profile hmr up --build api web-hmr
```

可独立验证两个开发镜像的运行身份和 bind mount 写入归属：

```bash
./scripts/verify-dev-user.sh
```

启动后 API 同时提供前端和后端：

- Web/API：<http://localhost:28648>
- API 存活检查：<http://localhost:28648/health/live>

默认开发拓扑只有 API 长驻运行。HMR 仅在显式启用 `hmr` profile 时提供：<http://localhost:5173>；HMR
终端退出不影响 API。需要重新生成前端静态文件时可单独运行 `docker compose --env-file docker/.env
-f docker/compose.dev.yml run --rm web`。

停止环境：

```bash
./scripts/dev-down.sh
```

执行完整检查：

```bash
./scripts/check.sh
```

身份 E2E/CI 必须使用测试脚本生成的临时 env、DataRoot、Compose project 和端口；它们不得读取或
修改当前 checkout 的人工 `docker/.env`、`./data`，也不得停止人工开发容器。

## 生产 Docker 部署

所有生产 Docker 文件集中在 [`docker/`](docker/) 目录。部署者只需复制配置、填写管理员信息和端口，
并按需设置 bind mount 的 UID/GID 和数据目录，然后从该目录执行一次 `docker compose up -d`：

```bash
cd docker
cp .env.example .env
# named volume 默认以 root 运行；只有 bind mount 才设置 CLOUDEMUERA_UID/GID
docker compose up -d
```

`CLOUDEMUERA_DATA_PATH` 未设置时使用 Docker named volume `cloudemuera-data`；设置后使用指定宿主机目录
作为 `/data` bind mount。使用 bind mount 时请先创建并赋予启动账号权限。容器入口 `/app/start.sh` 会在
同一个容器内先运行独占 Migrator，成功后以 `exec` 启动 API；后续 schema 变更再次执行 `docker compose up -d`
即可。生产镜像已经包含构建后的 SPA，不需要独立 web 容器。默认情况下宿主机只在
`127.0.0.1:28647` 监听；部署者可以让上级 Nginx/Caddy 选择是否使用 HTTPS，也可以直接使用 HTTP。
应用不推断外部协议、不执行 HTTPS 跳转或 HSTS；只有在公开入口确实使用 HTTPS 时才设置
`CLOUDEMUERA_SECURITY_SECURE_COOKIES=true`。只有明确需要直接公网暴露时才设置
`CLOUDEMUERA_HTTP_BIND_ADDRESS=0.0.0.0`。SPA 和 API 仍然是同一应用端口：浏览器加载 `/` 的静态文件后，
使用同源 `/api/v1/*` 和 `/api/v1/realtime`。

API 使用 Docker `init: true` 作为轻量 PID 1，并把 SIGTERM 传给 API；镜像默认 `STOPSIGNAL` 也是
SIGTERM。API 收到停机信号后先停止接入，给所有 Worker 共用 5 秒优雅停止预算，再给仍存活的 Worker
共用 5 秒强制终止预算；Host 总预算为 15 秒，Compose 停止宽限期为 20 秒。控制面停机不会伪装成用户
关闭，活动 Session 会在可确认时记为 `CRASHED`，SessionRoot 和原生存档保留。

生产升级或维护前应做冷备份：先停止 API，再复制整个 `/data`（必须同时包含 SQLite、WAL/SHM、keys、
games、sessions、logs 和 backups），完成复制后再启动 API。bind mount 可直接复制宿主目录；named volume
可使用临时工具容器，例如：

```bash
docker compose stop api
mkdir -p backups/cloudemuera-data
docker run --rm --user "$(id -u):$(id -g)" \
  -v cloudemuera-data:/source:ro \
  -v "$PWD/backups/cloudemuera-data:/backup" \
  busybox:1.36 sh -c 'cp -a /source/. /backup/'
docker compose start api
```

恢复时同样先停止 API，把备份目录完整恢复到 `/data` 后执行
`docker compose run --rm --no-deps api rebind-session-roots`，再启动 API；该命令只在离线恢复时
重新校验数据库 marker，并更新恢复后 Game 目录和 SessionRoot 的文件系统 identity，不会改变正常启动语义。
不要只恢复 SQLite 或只恢复某个 SessionRoot。

镜像默认不声明 USER；named volume 使用 root 以避免固定镜像 UID 的目录所有权问题。使用 bind mount 时可在
生产 Compose 通过 `CLOUDEMUERA_UID/GID` 让 API、Worker、Validator 和 Migrator 继承宿主部署账号。生产
Compose 不挂 Docker socket、宿主 home、密钥、源码或 Worker UDS；同 UID Worker 是可信自托管边界，不构成
恶意游戏代码的内核级沙箱。默认值与交叉关系见
[ADR-0027](docs/adr/0027-instance-capacity-and-production-boundary.md)，数据目录身份见
[ADR-0028](docs/adr/0028-production-bind-mount-ownership.md)。

验证脚本：

```bash
./scripts/test-production-image.sh
./scripts/test-instance-limits.sh
./scripts/test-process-recovery.sh
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
docker/               Dockerfile、Compose 与生产环境示例
scripts/              开发、检查和内置源码维护脚本
data/                 本地运行数据，不提交 Git
```

## 三方源码

Emuera.EM+EE 以普通 Git 文件形式固定在 `src/CloudEmuera.EmueraRuntime/Upstream`，不使用 submodule。来源仓库、固定提交、原始 tree ID、许可证和更新流程见 [UPSTREAM.md](src/CloudEmuera.EmueraRuntime/UPSTREAM.md)。

首次克隆无需额外初始化。对内置源码的修改必须保留上游声明，并登记在 `MODIFICATIONS.md`；运行时清单同时记录上游提交和 CloudEmuera integration version。上游升级必须以独立导入提交进行，不能跟踪浮动的“最新版”。

## 项目状态

开发环境、Phase 0 运行时切分、P1-01～P1-14 已完成；其中包括安全游戏包摄取、持久 SessionRoot、
API-owned Worker Manager、浏览器实时协议、正式存档与实例级 Worker/文件/并发边界。固定上游
Emuera loader/interpreter 已在 Linux 无 UI Runtime 中跑通两套 INPUT 往返，真实独立 Worker 也完成
注册、结构化输出、重复输入、短断重连、优雅停止和双进程隔离。P1-14 已补齐单容器进程恢复、信号
转发、冷备份说明和可重复故障验证；后续功能按 [开发计划](docs/development-plan.zh-CN.md) 分阶段实现。

## 贡献与安全

提交前请运行 `./scripts/check.sh`。安全问题请按 [SECURITY.md](SECURITY.md) 中的方式私下报告，不要在公开 Issue 中附带游戏包、存档、令牌或用户输入。

## 许可证

CloudEmuera 自研代码使用 [Apache License 2.0](LICENSE)。三方源码不因根许可证而重新授权，继续保留各自的版权和许可证声明，详见 [NOTICE](NOTICE) 与 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
