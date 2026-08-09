# ADR-0001：本地身份与 OIDC 重新评审触发条件

状态：Accepted

日期：2026-08-09

## 背景

MVP 是同源、自托管的单实例应用，需要立即撤销登录态、首次管理员初始化及后续资源级授权；没有组织身份提供商或账户恢复服务。

## 决定

- 使用本地邮箱和密码登录；用户名仅用于显示和管理，不作为凭据。
- 浏览器使用受 Data Protection 保护的 HttpOnly Cookie。Cookie 只携带最小 claims 和随机 `auths_` 会话 ID；每个请求回查 SQLite，因此注销、禁用、改密和角色变化立即失效。
- 仅在持久 `instance_state` 为 `BOOTSTRAP_REQUIRED` 时读取三个 bootstrap 环境变量，原子创建首个临时密码 ADMIN 并永久标记完成。不会因为管理员缺失或禁用重新 bootstrap。
- 临时密码账户只能完成改密闭环；不开放注册、找回密码、令牌登录或访客模式。

## 备选方案

纯 JWT 无法提供即时撤销，并会增加 refresh token 生命周期和浏览器暴露面。现在引入 OIDC 会增加外部部署和故障边界，而没有当前产品需求支撑。

## 后果与重新评审

`/data/keys` 与 SQLite 必须一起备份；丢失 key ring 只会让 Cookie 失效。出现组织统一身份、MFA/账户恢复、外部用户生命周期治理，或多个实例共享登录任一需求时，必须重新评审 OIDC。届时保留 `usr_` 本地身份，建立外部 subject 绑定、迁移、断开与回退方案，不以外部 subject 替换资源 owner ID。
