# ADR-0028：生产 bind mount 使用部署者 UID/GID

- 状态：Accepted
- 日期：2026-08-21
- 关联：ADR-0017、ADR-0027、P1-13

## 背景

P1-13 的生产镜像需要以非 root 身份运行，但单机自托管部署的持久数据通常属于启动
`docker compose` 的宿主机用户。若生产 Compose 固定使用镜像内 UID（例如 `10001`），
bind mount 到 `/data` 后会导致新建的 SQLite、游戏内容、SessionRoot、存档和日志归固定
容器 UID 所有，部署者需要额外执行 `chown`，也不符合宿主机权限管理习惯。

同时，单机部署也需要一个不依赖宿主机路径的默认模式，方便用户直接使用 Docker volume。

本 ADR 只决定生产容器与宿主机数据目录的身份映射，不改变应用登录用户、资源授权或
SessionRoot 的逻辑隔离。当前 MVP 仍是可信自托管实例；API 与 Worker 使用同一进程身份，
不构成恶意游戏代码的内核级沙箱。

## 决定

1. `docker/Dockerfile` 保留一个固定的非 root 默认用户，保证直接运行镜像时不会以 root 启动。
2. 生产 Compose 必须要求部署者提供 `CLOUDEMUERA_UID` 和 `CLOUDEMUERA_GID`，并用
   `user: "${CLOUDEMUERA_UID}:${CLOUDEMUERA_GID}"` 启动 API。API 创建的 Worker、Validator
   和 Migrator 也继承该身份。
3. 生产 Compose 根据 `CLOUDEMUERA_DATA_PATH` 选择 `/data`：变量未设置时使用声明的
   Docker named volume `cloudemuera-data`；变量设置后使用该宿主机路径作为 bind mount。
   bind mount 的相对路径相对于 `docker/` 目录解析，部署者应预先创建并设置目录权限。
4. 镜像中的 `/data` 默认目录属于 `10001:10001`，使用 `0770`；生产 Compose 为动态部署者
   UID/GID 添加固定辅助组 `10001`，因此新建 named volume 也能由部署者身份写入。bind mount
   时则由部署者的主 UID/GID 直接取得目录写权限。
5. 生产启动前使用 bind mount 的部署者以自己的账号创建并设置数据目录权限，例如：

   ```bash
   export CLOUDEMUERA_UID="$(id -u)"
   export CLOUDEMUERA_GID="$(id -g)"
   export CLOUDEMUERA_DATA_PATH="$PWD/data"
   install -d -m 700 "$CLOUDEMUERA_DATA_PATH"
   ```

6. 生产镜像不挂 Docker socket、宿主 home、源码、密钥或 Worker UDS；容器仍保持非 root、
   `cap_drop: ALL`、`no-new-privileges` 和整体资源限制。

## 备选方案

1. 固定镜像 UID 配合 named volume 或 bind mount：部署简单，但 bind mount 数据会归镜像 UID
   所有，部署者需要额外调整宿主权限。
2. 容器以 root 启动后在 entrypoint 中动态降权：可以匹配宿主目录，但扩大 root 权限窗口，
   且会让 PID1、迁移和 Worker 身份语义复杂化。
3. 为每个应用登录用户切换 Unix UID：与单容器 API 的请求模型不匹配，也不是当前应用级
   授权需求。

## 后果

生产部署需要在 `docker/.env` 中填写 UID/GID；只有选择 bind mount 时必须先创建数据目录。好处是 bind mount
持久化文件直接归部署者所有，named volume 也能使用同一部署者身份写入；备份、迁移和宿主机清理不需要
固定 UID 特殊处理；API、Worker、Validator 和 Migrator 仍共享
同一非 root 身份，既保留 P1-13 的可信自托管边界，也不会把它误标为恶意租户沙箱。

从旧 named volume 迁移到 bind mount 时，部署者必须先停服务并将旧卷内容复制到目标目录，
再按目标宿主机 UID/GID 调整目录所有权；应用不会在启动时递归修改数据权限。

## 验证

- `scripts/test-production-image.sh` 使用临时宿主机目录、当前测试 UID/GID 和 bind mount，
  检查 Compose 实际进程 UID/GID、`/data` 挂载类型与路径，并完成真实 Worker smoke；
- Compose 配置检查同时覆盖未设置 `CLOUDEMUERA_DATA_PATH` 时的 named volume 分支；
- `scripts/test-instance-limits.sh` 和完整 `scripts/check.sh` 继续在 dev Docker 中运行；
- `docker compose config` 在未提供 UID/GID 或数据目录不存在时拒绝生产配置。
