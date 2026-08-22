# ADR-0028：生产数据目录身份与单容器入口

- 状态：Accepted
- 日期：2026-08-21
- 关联：ADR-0017、ADR-0027、P1-13、P1-14

## 背景

生产 MVP 使用一个容器，同时包含 API、Worker、Validator、Migrator 和已经构建好的 SPA。Docker
named volume 不需要与宿主机用户做 UID 映射；固定镜像 UID 只会增加目录所有权和 Compose 配置复杂度。
bind mount 则可能需要让容器进程匹配预先创建数据目录的宿主账号。

## 决定

1. 生产 `docker/Dockerfile` 不声明固定 `USER`，也不创建或使用 `10001` 用户/辅助组。直接运行镜像和
   默认 named volume Compose 部署使用 root；`/app` 只读，`/data` 可写。
2. 生产 `docker/compose.yml` 只保留一个 `api` 服务。`user` 默认展开为 `0:0`；使用 bind mount 时，
   部署者可以设置 `CLOUDEMUERA_UID` 和 `CLOUDEMUERA_GID`，API 创建的 Worker、Validator 和容器内
   Migrator 都继承该身份。
3. `CLOUDEMUERA_DATA_PATH` 未设置时使用 named volume `cloudemuera-data`；设置后使用该宿主机路径
   作为 `/data` bind mount。bind mount 的目录由部署者预先创建并授予选择的 UID/GID，应用不会在入口
   脚本中递归修改目录所有权。
4. `/app/start.sh` 是唯一生产入口：先在同一容器内独占运行 Migrator，成功后 `exec` API。SPA 已进入
   API 的静态 web root，不启动独立 web 服务。
5. 生产 Compose 不主动映射 `CloudEmuera__Capacity:*` 或其他细粒度容量环境变量；应用使用代码默认值。
   CPU 不设置限制；内存/PID 约束仍可由部署者显式覆盖。
6. 容器不挂 Docker socket、宿主 home、源码、密钥或 Worker UDS，继续使用 `init: true`、`cap_drop: ALL`
   和 `no-new-privileges`。
7. 生产 Compose 的宿主 HTTP 端口默认绑定 `127.0.0.1`；部署者可以让上级 Nginx/Caddy 选择 HTTP 或 HTTPS，
   应用不执行协议跳转。只有明确设置 `CLOUDEMUERA_HTTP_BIND_ADDRESS` 时才直接绑定其他地址。容器内 API
   仍监听 `0.0.0.0:28647`，SPA、HTTP API 和 WebSocket 共用该应用端口；`CLOUDEMUERA_SECURITY_SECURE_COOKIES`
   显式控制是否使用 Secure Cookie。

## 备选方案

1. 固定镜像 UID（例如 `10001`）：named volume 需要辅助组，bind mount 还会制造宿主机所有权问题。
2. root 入口再动态降权：会让迁移、API、Worker 和 PID 1 的身份语义变复杂，并增加入口脚本权限窗口。
3. 生产拆成 Migrator、API、Web 多个服务：增加 Compose 依赖、生命周期和数据卷拓扑，不符合单容器 MVP。

## 后果

默认部署最小且可直接使用 named volume；选择 bind mount 的部署者需要自行创建目录并按需填写 UID/GID。
默认 loopback 绑定避免把应用端口意外暴露到公网；公网部署是否使用外部 HTTPS 终结点由部署者选择，直接绑定
公网地址也是显式部署选择。root 默认不代表恶意游戏隔离，当前 MVP 仍是假设可信自托管参与者；路径、SessionRoot、授权和 Worker
生命周期边界仍由应用负责。迁移和 API 共用一个容器，但 Migrator 只在 API 进程启动前运行，API 仍是
运行期间唯一访问 SQLite 的业务进程。

## 验证

- `scripts/test-production-image.sh` 检查 Compose 只有 `api` 服务、没有 `migrator`、没有 CPU 限制、没有
  `CloudEmuera__Capacity:*` 映射，并覆盖 named volume root 默认与 bind mount 动态 UID/GID；
- `scripts/test-process-recovery.sh` 通过单容器入口验证迁移、SIGTERM/SIGKILL、Worker 对账和冷恢复；
- `scripts/verify-dev-user.sh` 继续验证开发 bind mount 使用宿主 UID/GID；
- `git diff --check`、`./scripts/check.sh` 和第三方声明检查通过。
