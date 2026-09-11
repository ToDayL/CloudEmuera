# CloudEmuera 生产部署指南

[English](deployment.md) | 中文

本指南介绍 `docker/compose.yml` 中的生产 Compose 部署方式。如果只想从代码检出快速启动实例，请先阅读仓库 README 中的[快速开始](../README.zh-CN.md)。

CloudEmuera 面向自托管实例和可信游戏包。当前 MVP 不是面向不可信用户或不可信游戏代码的公网沙箱。

## 前置条件

- Docker 28 或更高版本
- Docker Compose v2
- 有足够空间存放游戏库、SessionRoot、存档和 SQLite 数据的宿主机目录或 Docker 命名卷

生产 Compose 会在同一个容器内先运行 Migrator，再启动 API。不要针对同一个数据目录单独启动 Migrator 或 API 服务。

## 准备 `docker/.env`

在仓库根目录执行：

```bash
cd docker
cp .env.example .env
```

生产 Compose 读取 `docker/.env`。请不要将此文件提交到版本控制，并在首次登录后修改临时管理员密码。

### 必需的管理员初始化配置

每次启动生产服务时，Compose 都要求以下三个值存在。应用只会在全新的数据目录处于 `BOOTSTRAP_REQUIRED` 状态时使用它们。首次登录使用配置的邮箱地址；初始化密码可以是任意非空值，登录后设置的新密码必须至少包含 8 个字符。

```dotenv
CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=admin
CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=you@example.com
CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=change-this-password
```

### 常用生产配置

下表列出生产部署通常需要的配置。未列出的值使用应用或 Compose 默认值；仓库模板见 [`docker/.env.example`](../docker/.env.example)。

| 变量 | 默认值 | 用途 |
| --- | --- | --- |
| `CLOUDEMUERA_DATA_PATH` | Docker 命名卷 `cloudemuera-data` | 绑定到持久化 `/data` 目录的宿主机目录。 |
| `CLOUDEMUERA_UID` / `CLOUDEMUERA_GID` | `0` / `0` | 容器用户和组。使用 bind mount 时设置，使文件归属于指定的宿主机账户。 |
| `CLOUDEMUERA_HTTP_BIND_ADDRESS` | `127.0.0.1` | HTTP 端口绑定的宿主机地址。只有明确需要直接暴露到局域网或公网时才使用 `0.0.0.0`。 |
| `CLOUDEMUERA_HTTP_PORT` | `28647` | Docker 暴露到宿主机的端口。如果该端口已被占用，可以修改。 |
| `CLOUDEMUERA_CONTAINER_PORT` | `28647` | 容器内应用使用的端口，通常保持不变。 |
| `CLOUDEMUERA_SECURITY_SECURE_COOKIES` | `false` | 使用 HTTPS 反向代理作为公网入口时设置为 `true`。 |
| `CLOUDEMUERA_PRODUCTION_IMAGE` | `cloudemuera:local` | 可选的镜像名称，用于使用预构建镜像代替本地构建。 |
| `CLOUDEMUERA_MEMORY_LIMIT` | `2g` | 可选的整个容器内存限制。 |
| `CLOUDEMUERA_PIDS_LIMIT` | `512` | 可选的整个容器进程数限制。 |

`CLOUDEMUERA_*` 容量、实时通信和 Worker 配置属于部署级调优选项，首次部署通常不需要修改。生产 Compose 会使用应用默认值。

## 持久化数据与挂载

`/data` 是应用的持久化目录，其中包含 SQLite 数据库、游戏内容、SessionRoot、存档、身份认证密钥和其他持久化状态。备份时应完整备份整个数据目录，尤其不能遗漏数据库和 `/data/keys`。

### Docker 命名卷（默认）

不设置 `CLOUDEMUERA_DATA_PATH`。Compose 会创建并管理 `cloudemuera-data` 命名卷，生产容器默认以 root 身份运行。这是最简单的部署方式，不需要设置 `CLOUDEMUERA_UID` 或 `CLOUDEMUERA_GID`。

### 宿主机 bind mount

如果需要将数据放在固定路径，或使用宿主机备份工具管理数据，可以使用专用宿主机目录。相对路径以 `docker/` 目录为基准解析，建议使用绝对路径。

在 `docker/.env` 中设置路径和宿主机账户 ID：

```dotenv
CLOUDEMUERA_DATA_PATH=/srv/cloudemuera-data
CLOUDEMUERA_UID=1000
CLOUDEMUERA_GID=1000
```

将 `1000` 替换为目标宿主机账户通过 `id -u` 和 `id -g` 返回的值，然后在启动 Compose 前创建并准备目录：

```bash
sudo mkdir -p /srv/cloudemuera-data
sudo chown 1000:1000 /srv/cloudemuera-data
```

Compose 不会递归修改 bind mount 的所有权。目录必须已经对配置的 UID/GID 可写。请使用专用数据目录；不要挂载宿主机 home 目录、代码仓库、Docker socket 或包含其他无关密钥的目录。

## 网络暴露

宿主机端口默认只绑定到 loopback：

```dotenv
CLOUDEMUERA_HTTP_BIND_ADDRESS=127.0.0.1
CLOUDEMUERA_HTTP_PORT=28647
```

如果需要从可信局域网中的其他设备直接访问，请显式设置绑定地址：

```dotenv
CLOUDEMUERA_HTTP_BIND_ADDRESS=0.0.0.0
```

然后使用 `docker compose up -d` 重新创建服务，并打开 `http://<server-address>:28647`。请使用防火墙限制可以访问该端口的来源。

如果部署到公网，请让应用继续绑定 loopback，并在前面配置 HTTPS 反向代理：

```dotenv
CLOUDEMUERA_HTTP_BIND_ADDRESS=127.0.0.1
CLOUDEMUERA_SECURITY_SECURE_COOKIES=true
```

反向代理必须同时转发 HTTP 请求和 WebSocket 升级请求。应用不会执行 HTTPS 重定向；TLS 终止和公网协议策略由反向代理负责。宿主机端口被占用时只需修改 `CLOUDEMUERA_HTTP_PORT`。只有在明确要修改容器内应用端口时，才修改 `CLOUDEMUERA_CONTAINER_PORT`。

## 启动、停止和更新

在 `docker/` 目录中执行以下命令：

```bash
docker compose up -d --build
docker compose ps
docker compose logs -f api
```

修改环境变量后，再次运行 `docker compose up -d` 使配置生效。要在保留全部数据的情况下停止服务：

```bash
docker compose stop
```

`docker compose down` 会删除容器和网络，但会保留命名卷。除非明确要删除托管数据卷，否则不要使用 `docker compose down -v`。

要更新代码检出版本，请拉取目标版本并重新构建：

```bash
git pull
docker compose up -d --build
```

Migrator 会在 API 启动前自动运行。升级前请先对完整的 `/data` 目录进行一致性备份。

## 备份与恢复

复制持久化数据前请先停止 CloudEmuera，以确保 SQLite 数据库和文件系统数据一致。使用 bind mount 时，备份配置的 `CLOUDEMUERA_DATA_PATH`；使用命名卷时，针对 `cloudemuera-data` 使用 Docker 卷备份流程。服务停止期间恢复完整目录或命名卷，包括 `/data/keys`，然后重新启动服务。

不要将删除命名卷作为日常清理步骤。丢失 `/data/keys` 会使已有登录 Cookie 失效；账户和应用数据仍保留在数据库中，但用户需要重新登录。

## 故障排查

可以在不打印解析后完整配置的情况下验证 Compose 插值：

```bash
docker compose config --quiet
```

- 如果 Compose 报告缺少管理员初始化变量，请在 `docker/.env` 中设置三个管理员初始化值。
- 如果 bind mount 报告 permission denied，请检查目录所有者、`CLOUDEMUERA_UID` 和 `CLOUDEMUERA_GID`；Compose 不会自动修复所有权。
- 如果宿主机端口已被占用，请修改 `CLOUDEMUERA_HTTP_PORT`，然后运行 `docker compose up -d`。
- 如果服务位于 HTTPS 后面，请设置 `CLOUDEMUERA_SECURITY_SECURE_COOKIES=true`；使用普通 HTTP 时保持为 `false`。
