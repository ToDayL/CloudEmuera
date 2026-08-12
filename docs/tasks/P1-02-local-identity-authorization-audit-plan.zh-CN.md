# P1-02 本地身份、资源授权与审计详细实现计划

状态：DONE（2026-08-09 复验）

范围说明：本文保留 P1-02 的身份、授权和关键审计验收记录；其中通用审计查询、用户级公平配额等
历史产品面已由 [`ADR-0017`](../adr/0017-trusted-self-hosted-mvp-simplification.md) 取代。

计划日期：2026-08-08

对应开发步骤：`P1-02 — 本地身份、资源授权与审计`

前置条件：P1-01 SQLite 首版 schema 与迁移已完成；现有 Web 展示版包含登录、游戏、
Session、存档、管理和设置页面，但尚未连接真实 API

后续步骤：P1-03 安全游戏包摄取；P1-04 草稿编辑与不可变发布；P1-06 Session 创建与关闭；
P1-08 WebSocket 恢复与输入去重

需求映射：AUTH-001～006、OPS-004/005、SEC-009、NFR-011/015～018、AC-004。
其中 AUTH-005 在本步骤建立可复用的升级/恢复授权器和 Origin 策略，生产 WebSocket 协议接线
仍由 P1-08 完成。

## 1. 任务结论

P1-02 要把当前“任意用户名都能进入”的展示前端变为以邮箱认证的本地多用户登录系统，并建立后续
所有 Game、Session、Save 和管理员端点必须调用的服务端资源授权边界与追加式
审计能力。

本步骤完成后应能证明：

- 没有有效登录会话的浏览器只能访问公开端点、登录页和前端静态资源；
- 本地密码只以 ASP.NET Core Identity 的自适应 hash 保存，密码、Cookie、CSRF token 和
  session handle 不进入日志或审计 metadata；
- 浏览器持有的是加密、HttpOnly 的同源 Cookie；每个 Cookie 对应 SQLite 中可撤销的
  `auth_sessions` 记录，因此注销、禁用账户、修改角色、修改或重置密码后旧 Cookie 失效；
- API 进程重启后，只要 Data Protection key ring 和 SQLite 仍在，未过期会话继续有效；
- MVP 不开放匿名注册。全新实例首次启动时从 `.env` bootstrap 配置自动创建首个管理员，
  并要求该管理员首次登录后修改临时密码；之后只有管理员才能创建玩家账户；
- 普通玩家不能读取、控制或探测其他用户的私有 Game、Session 和 Save；共享 Game 只扩大
  读取权，不扩大编辑、发布或 Session/Save 权限；
- 管理员权限按操作显式授予，不因 `ADMIN` 角色自动获得玩家私有控制台、输入或存档内容；
- 管理员创建/禁用/启用用户、修改角色、重置密码等敏感操作与审计事件在同一数据库事务提交；
- 现有 React 登录页使用真实 API，受保护路由会等待 `/auth/me`，注销后不能通过浏览器返回
  重新进入；管理员入口只对管理员展示，但服务端仍独立强制权限。

P1-02 不实现游戏包、Game/Session/Save 正式业务 API，也不实现完整 WebSocket 实时协议。
资源授权器在本步骤用持久化 fixture 和测试 HTTP adapter 验证，随后由 P1-03～P1-09 的真实
端点复用；不得在这些后续任务中另写一套所有权判断。

## 2. 已冻结的身份方案

### 2.1 MVP 身份来源与首次启动

- MVP 只支持本地邮箱和密码登录；用户名仅作为账户显示名和管理标识，不是登录凭据；
- 不提供公开注册、邮件找回密码、OAuth/OIDC、API token 或“访客模式”；
- 部署者在项目根目录、不提交 Git 的 `.env` 中配置：

  ```dotenv
  CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=admin
  CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=admin@example.com
  CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password
  ```

- 全新数据库首次启动时，API 在单一 SQLite 写事务中创建默认 quota profile、首个 ACTIVE
  ADMIN、初始化完成标记和审计事件；
- bootstrap 密码是临时密码，创建的管理员固定 `must_change_password=true`。首次登录只允许
  访问 `/auth/me`、修改密码和注销，改密成功后才能进入应用；
- 初始化完成后，后续启动完全忽略三个 bootstrap 变量，不校验它们是否仍存在，也绝不据此
  覆盖用户名、邮箱、角色或密码；
- 即使数据库以后没有 ACTIVE ADMIN，也绝不能重新执行 bootstrap；
- 后续用户由已登录管理员通过管理 API 创建；管理员设置临时密码，用户首次登录后必须修改；
- P1-02 不提供管理员密码灾难恢复命令。恢复机制留给后续运维任务，且不得重新启用 bootstrap；
- `.env` 必须已被 `.gitignore` 排除。应用、脚本和测试不能打印 bootstrap password，也不能把它
  写入审计 metadata；用户已明确接受它作为个人部署的简化凭据传递方式；
- 不创建硬编码 demo 用户或镜像内默认凭据。自动化测试只使用隔离环境变量和临时数据库。

实现阶段必须新增 `ADR-0001-local-identity-and-oidc-trigger.md`，记录：

1. 当前采用本地身份、服务端可撤销 Cookie session、仅首次启动读取 `.env` bootstrap 且无
   公开注册；
2. bootstrap 完成后的永久关闭语义、首次登录强制改密，以及管理员恢复为何不得重跑 bootstrap；
3. 不在 MVP 提前引入 OIDC 的原因；
4. 触发重新评审 OIDC 的条件，例如组织统一身份、MFA/账户恢复要求、外部用户生命周期治理或
   多个 CloudEmuera 实例共享登录；
5. 引入 OIDC 时本地 `usr_` 身份与外部 subject 的绑定、迁移和回退要求。

ADR 编号使用仓库现有四位格式 `0001`；同时更新 `docs/adr/README.md`。若仓库在实施前已占用
该编号，则选择最小未占用编号并同步修正开发计划链接。

### 2.2 不使用长期 JWT

浏览器和 API 同源部署，采用 ASP.NET Core Cookie Authentication：

- Cookie 只承载经过 Data Protection 加密和签名的最小 claims 与随机 `authSessionId`；
- 每次认证都查询 `auth_sessions + users`，确认 session 未撤销/未过期、用户 ACTIVE、
  security stamp 一致；不能只相信 Cookie 内旧角色；
- Cookie 不放进 `localStorage`、`sessionStorage`、URL、React state 持久化或可读 JS token；
- 注销将数据库 session 标记 revoked 后再删除浏览器 Cookie；被复制的旧 Cookie 也会失败；
- 角色/状态/密码变化递增 `state_version`、轮换 `security_stamp` 并撤销该用户所有会话；
- API 重启不丢失会话；Data Protection key 丢失时 Cookie 失效，但用户和资源数据不受影响。

不使用纯 stateless JWT，因为它无法满足立即注销和管理员禁用后立即失效，又会引入 refresh
token 存储、轮换和浏览器暴露面。未来非浏览器客户端需要 token 时另立 ADR。

## 3. 范围

### 3.1 必须实现

1. 新增身份补充 migration：`auth_sessions`、首次改密状态和密码变更时间；
2. 实现适配 P1-01 自定义 `users` 表的 ASP.NET Core Identity user store；
3. 实现邮箱与用户名规范化、密码策略、hash/rehash、持久锁定和恒定形态的失败响应；
4. 实现 Cookie session 创建、逐请求验证、滑动/绝对过期、撤销和有界清理；
5. 持久化 Data Protection key ring，并安全配置 Cookie、CSRF、HTTPS 和安全响应头；
6. 实现 login、logout、me、change-password API；
7. 实现管理员列出/创建用户、启用/禁用、修改角色和重置密码 API；
8. 实现 bootstrap 配置验证、持久初始化状态和首次管理员原子创建服务；
9. 在 Application 建立 actor、角色策略、资源访问描述符和集中授权服务；
10. 在 Infrastructure 建立只返回授权所需最小字段的 owner/visibility lookup；
11. 建立追加式审计端口和 SQLite 实现，敏感状态更新与审计同事务；
12. 建立 WebSocket upgrade Origin 检查及每次 `session.resume` 可调用的重新授权接口；
13. 把现有 React 登录页接入真实 API，增加 AuthProvider、受保护路由、管理员导航和注销；
14. 新增 API 集成测试、Application 授权测试、Infrastructure 身份测试、Web 组件测试和登录 E2E；
15. 更新配置、ADR、HTTP 契约、开发计划和安全运维说明。

### 3.2 明确非目标

- 不实现公开注册、邮箱验证、短信、忘记密码邮件、MFA 或用户恢复码；email 是登录标识，但
  P1-02 不证明用户控制该邮箱，也不发送邮件；
- 不实现 OIDC/OAuth/SAML、外部登录绑定、个人 access token 或服务 API key；
- 不创建 `AspNetUsers`、roles、claims、logins、tokens 等默认 Identity 表；单角色继续存于
  `users.role`；
- 不把 auth session 存在 API 内存、Redis、浏览器 storage 或进程内 singleton 中；
- 不实现 Game、Session、Save 正式 CRUD；只实现可供它们复用的授权器；
- 不允许管理员通过通用“超级用户”规则读取私有存档、提交玩家输入或修改玩家私有 Game；
- 不实现 P1-08 的 WebSocket envelope、snapshot/resume 数据流或输入协议；
- 不把 CSRF token 当作身份凭据，也不以 CORS 代替 CSRF；
- 不记录原始密码、password hash、Cookie、Authorization、CSRF token、session ID、输入全文或
  完整邮箱或用户名到日志/审计 metadata；
- 不在 API 启动时自动运行 migration；schema 已兼容且 instance 未初始化时，API 才按 `.env`
  自动创建一次管理员；
- 不在 P1-02 实现管理员遗失密码后的离线恢复；该能力不能通过检测“无管理员”自动开启；
- 不在本步骤重构展示页的游戏、Session、存档假数据；只替换身份相关假行为和用户显示。

## 4. 建议工程布局和依赖

```text
src/CloudEmuera.Domain/
└── Identity/
    ├── UserRole.cs
    ├── UserStatus.cs
    └── ResourceAction.cs
src/CloudEmuera.Application/
├── Identity/
│   ├── CurrentActor.cs
│   ├── ILocalAuthenticationService.cs
│   ├── ILocalUserStore.cs
│   ├── IAuthSessionStore.cs
│   ├── LoginService.cs
│   └── UserAdministrationService.cs
├── Authorization/
│   ├── ResourceDescriptor.cs
│   ├── IResourceAccessReader.cs
│   ├── IResourceAuthorizer.cs
│   └── ResourceAuthorizer.cs
└── Auditing/
    ├── AuditEvent.cs
    ├── IAuditWriter.cs
    └── AuditActionCatalog.cs
src/CloudEmuera.Contracts/
└── Identity/
    ├── AuthRequests.cs
    ├── AuthResponses.cs
    ├── AdminUserRequests.cs
    └── ApiError.cs
src/CloudEmuera.Infrastructure/
├── Identity/
│   ├── CloudEmueraUserStore.cs
│   ├── AuthSessionRepository.cs
│   └── PasswordPolicy.cs
├── Authorization/
│   └── SqliteResourceAccessReader.cs
├── Auditing/
│   └── SqliteAuditWriter.cs
└── Persistence/
    ├── Entities/AuthSessionRow.cs
    ├── Configurations/AuthSessionConfiguration.cs
    └── Migrations/<timestamp>_AddLocalIdentitySessions.*
src/CloudEmuera.Api/
├── Identity/
│   ├── IdentityServiceCollectionExtensions.cs
│   ├── AuthEndpoints.cs
│   ├── AdminUserEndpoints.cs
│   ├── AuthSessionValidator.cs
│   └── RealtimeOriginValidator.cs
├── Bootstrap/
│   ├── BootstrapAdminOptions.cs
│   ├── BootstrapAdminInitializer.cs
│   └── BootstrapReadinessCheck.cs
└── Security/
    ├── ApiPolicies.cs
    ├── AntiforgeryEndpoints.cs
    └── SecurityHeadersMiddleware.cs
tests/CloudEmuera.Application.Tests/
tests/CloudEmuera.Api.IntegrationTests/
tests/CloudEmuera.Infrastructure.Tests/Identity/
```

文件名可调整，但依赖保持：

```text
Domain ← Application ← Infrastructure
                    ↖ Api
Contracts ← Api
```

- Domain 不引用 Identity、EF、HTTP、Cookie 或 claims 类型；
- Application 不引用 `HttpContext`、`ClaimsPrincipal`、EF entity 或 SQLite；
- Api 将 `ClaimsPrincipal` 映射为不可变 `CurrentActor` 后调用 Application；
- Infrastructure 的 Identity store 适配 `CloudEmueraUser` 和 DbContext，不向 Application 泄露
  `IdentityResult` 或 tracked entity；
- Bootstrap initializer 调用同一个 Application 首次初始化用例、密码策略和 audit port，不复制 hash
  或审计逻辑；
- React 不根据角色推断资源所有权；服务端响应才是权威结果。

如果实现发现 ASP.NET Core `UserManager<TUser>` 无法在不生成默认 Identity 表的情况下满足当前
表结构，应实现所需最小接口：`IUserStore`、`IUserPasswordStore`、`IUserSecurityStampStore`、
`IUserLockoutStore`、`IUserEmailStore` 和单角色适配器。不得改回 `IdentityDbContext` 或添加闲置
Identity 表来绕过。

## 5. 数据库变更

新增不可修改的第二个 migration，例如 `AddLocalIdentitySessions`。不得编辑 P1-01 的
`InitialMetadata` migration 或 snapshot 历史。

### 5.1 `instance_state`

新增单行实例状态表；它是“是否曾完成初始化”的权威依据，不能用当前 ADMIN 数量代替：

```text
id INTEGER PRIMARY KEY                  -- 固定为 1
bootstrap_status TEXT NOT NULL          -- BOOTSTRAP_REQUIRED | COMPLETED
initialized_at INTEGER NULL
initial_admin_user_id TEXT NULL REFERENCES users(id) ON DELETE RESTRICT
state_version INTEGER NOT NULL DEFAULT 0
```

约束：

- `id = 1` 且表最多一行；P1-02 migration 确定性插入单例：不存在同时具有 normalized email 与
  password hash 的 ACTIVE ADMIN 时为 BOOTSTRAP_REQUIRED；已有可登录 ACTIVE ADMIN 时为
  COMPLETED，并以
  `(created_at, id)` 最早者回填 initial admin/time；
- BOOTSTRAP_REQUIRED 时初始化字段必须为空；COMPLETED 时 initialized time 和 initial admin ID
  必须非空；
- 状态只允许 `BOOTSTRAP_REQUIRED -> COMPLETED`，产品代码没有反向方法；
- 初始化事务使用 `state_version` 条件更新；并发请求最多一个能把状态改为 COMPLETED；
- 即使所有用户被禁用、角色被修改或未来发生数据修复，也不根据用户行重新推导 bootstrap 状态；
- 普通管理员 API 无权修改此表。未来恢复工具也不得把它改回 REQUIRED。

API 发现 BOOTSTRAP_REQUIRED 时严格读取并验证三个 bootstrap 环境变量。缺失或非法则不创建
部分数据，ready 返回 `BOOTSTRAP_CONFIGURATION_INVALID`；变量只作为首次事务输入，不写入
`instance_state`。COMPLETED 时不再读取或验证变量内容。

### 5.2 `users` 新列

```text
email TEXT NULL
normalized_email TEXT NULL
must_change_password INTEGER NOT NULL DEFAULT 0
password_changed_at INTEGER NULL
```

约束：

- email 与 normalized_email 必须同时 NULL 或同时非 NULL；非 NULL 时满足长度/NUL/格式约束；
- 对 `normalized_email IS NOT NULL` 建唯一索引；登录只查询 normalized_email，不查询
  login_name/normalized_login_name；
- `must_change_password IN (0,1)`；
- `password_hash IS NULL` 时 `password_changed_at` 必须为 NULL；有 hash 时变更时间必须存在；
- 新 migration 面对 P1-01 已有的无密码用户时保持 NULL/false；如果已有非 NULL hash，则把
  `password_changed_at` 保守回填为原 `updated_at` 并设置 `must_change_password=1`，不能因新增
  CHECK 使带数据升级失败，也不能把来源未验证的旧密码视为已完成首次改密；
- 管理员创建/重置的临时密码设置 `must_change_password=1`；用户成功自行改密后设为 0；
- bootstrap 管理员使用 `.env` 临时密码，设置为 1。

P1-01 已有 User 行回填 email=NULL，数据保持但不能登录；管理员以后为其设置唯一 email 后才能
使用。migration 不根据 login_name 猜测邮箱。新的 bootstrap/admin-created 用户必须同时提供
email 与 username。

SQLite 增加跨列 CHECK 可能触发表重建。migration 必须有 P1-01 → P1-02 的带数据升级测试、
迁移前 Online Backup 测试和失败回滚测试。

### 5.3 `auth_sessions`

```text
id TEXT PRIMARY KEY                    -- auths_<base64url CSPRNG>
user_id TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT
security_stamp TEXT NOT NULL
created_at INTEGER NOT NULL
last_seen_at INTEGER NOT NULL
idle_expires_at INTEGER NOT NULL
absolute_expires_at INTEGER NOT NULL
revoked_at INTEGER NULL
revoke_reason TEXT NULL
is_persistent INTEGER NOT NULL
```

约束和索引：

- ID、user ID、security stamp、reason 长度与 NUL 检查；
- `is_persistent IN (0,1)`；
- `created_at <= last_seen_at <= idle_expires_at <= absolute_expires_at`；
- `revoked_at` 为空或不早于 created；reason 与 revoked 必须同时为空或同时非空；
- `(user_id, revoked_at, absolute_expires_at)` 与 `(idle_expires_at)` 索引；
- session ID 使用 256 bit CSPRNG 并以无 padding base64url 编码，不按用户顺序生成，不在日志中
  输出；它不是 UUIDv7，也不携带创建时间；
- 不保存 Cookie、CSRF token、密码或完整 User-Agent。若需要关联风险事件，仅保存有版本前缀、
  服务端加盐的短期 UA/IP 摘要，默认 P1-02 不保存。

删除过期 session 是可重试维护动作，不属于审计事件；必须分批、有界执行。用户不得物理删除，
因此 FK 使用 RESTRICT。

### 5.4 并发和事务

- 用户更新使用 `state_version` CAS；角色、状态、密码、安全戳在一个事务中更新；
- bootstrap 使用 SQLite `BEGIN IMMEDIATE` 或等价的单写者事务，并以
  `instance_state(id=1, bootstrap_status=BOOTSTRAP_REQUIRED, state_version=expected)` 条件更新线性化；
- 首次 quota profile、ADMIN、COMPLETED 状态和 `SYSTEM_ADMIN_BOOTSTRAPPED` 审计必须同事务；
- 角色/状态/密码改变时，同事务撤销全部未撤销 auth session；
- 管理员敏感操作必须在同一 DbContext transaction 插入 `audit_events`；审计插入失败则用户
  变更整体回滚；
- 登录成功的 session insert 和成功审计同事务；已知用户登录失败的计数/锁定与失败审计同事务；
- 未知邮箱没有 User 行可锁，仍执行 dummy password hash verify 并写不含输入邮箱的 SYSTEM
  失败审计；
- 并发错误密码达到阈值时不能丢失计数；使用 CAS 重试或参数化条件 SQL，不用 read-modify-save；
- 注销提交之前已经完成认证的在途请求允许完成；revocation commit 后开始的新请求必须失败。

## 6. 邮箱、用户名、密码和账户状态

### 6.1 登录邮箱

- 登录 API 只接受 `email + password`，绝不把 username 当作登录凭据，也不做“邮箱或用户名”
  的模糊分支；
- 输入先移除首尾空白，拒绝 NUL、控制字符、长度超过 254 或不符合集中式 email validator 的值；
- `normalized_email` 由唯一的 `ILookupNormalizer`/normalizer 产生，至少将域名经 IDNA 处理并将
  比较形式统一为 invariant case-folded 值；应用、migration 和测试不得各自实现不同规则；
- 唯一性由 `normalized_email` 的 SQLite partial unique index 强制，不能依赖 SQLite `NOCASE`；
- P1-02 不做邮箱验证或邮件投递，因此 UI 应称为“登录邮箱”，不能显示“已验证”；
- 未知邮箱、错误密码、DISABLED、LOCKED 均返回相同失败结构；日志和审计不得保存完整输入邮箱；
- 管理员修改用户邮箱必须验证唯一性，使用 `state_version` CAS，轮换 security stamp、撤销旧会话
  并与审计同事务提交。

### 6.2 用户名

username 是稳定的账户显示名/管理标识，不是登录凭据。继续沿用 P1-01 的约束：

- 3～64 个 ASCII 字符；
- 首字符为字母或数字；其余只允许字母、数字、`.`、`_`、`-`；
- 不允许空白、控制字符、路径字符、NUL、前后点或连续点；
- `normalized_login_name = login_name.ToUpperInvariant()`，仍保持唯一，避免后台列表和审计主体
  出现歧义；
- 登录页面只展示邮箱输入框，不提供 username fallback。

### 6.3 密码

- 允许完整 Unicode，不 trim、不改变大小写、不执行 Unicode normalization；
- 长度按 Unicode scalar value 计 12～128，拒绝 NUL 和无效 surrogate；
- 不强制大小写/数字/符号组合，允许长 passphrase；
- 使用 ASP.NET Core Identity `IPasswordHasher<CloudEmueraUser>`，显式使用当前 Identity V3
  兼容格式和经测试的最低 work factor；不自制 hash、salt 或可逆加密；
- `SuccessRehashNeeded` 时在成功登录事务中更新 hash、password_changed_at、security stamp，
  但不因 rehash 撤销刚创建的新 session；已有其他 session 应按安全戳策略失效；
- 日志、validation details 和审计只记录稳定结果码，不记录密码长度以外的信息；默认连长度也
  不记录；
- 测试不得断言 hash 固定字节，因为 salt 必须随机；应断言 verify 成功、两个相同密码 hash
  不同、错误密码失败和旧 work factor 会触发 rehash。

### 6.4 锁定与防枚举

建议产品默认值集中配置并设合理上下限：连续 5 次失败后锁定 15 分钟；成功登录清零失败计数。
锁定时间使用注入 `TimeProvider` 的 UTC instant。无论账户未知、密码错误、禁用或锁定，登录 API
统一返回 `401 INVALID_CREDENTIALS` 和相同结构；客户端不显示具体原因。

对登录端点额外使用 ASP.NET Core rate limiter：

- 每来源地址与规范化邮箱摘要组合限制突发速率；
- key 使用进程级带上限缓存且只作为防御层，权威锁定仍在 SQLite；
- `429 TOO_MANY_ATTEMPTS` 可带标准 `Retry-After`，不透露账户是否存在；
- 代理来源只在配置了受信任代理时读取 forwarded headers；不能信任任意 X-Forwarded-For；
- rate limiter 状态在 API 重启后丢失不影响账户锁定正确性。

### 6.5 管理员不变量

- 系统必须始终至少保留一个 ACTIVE ADMIN；
- 禁止管理员禁用或降级唯一的 active admin；该约束必须经 SQLite 短事务和并发测试，不只在
  前端计数；
- 管理员可以修改自己的密码，但不能通过普通管理端点重置自己的密码来绕过旧密码校验；
- 管理员不能物理删除用户；P1-02 只支持 ACTIVE/DISABLED；
- DISABLED 用户不能新登录，现有 session 在禁用事务提交时全部撤销；
- ADMIN 角色只授予明确 admin policy，不自动覆盖资源 owner policy。

## 7. Cookie、CSRF 与 Data Protection

### 7.1 Cookie

固定一个产品 Cookie 名，例如 `__Host-CloudEmuera.Session`，生产配置：

```text
Path=/
Secure=true
HttpOnly=true
SameSite=Lax
Domain=<unset>
```

`__Host-` 前缀要求 Secure、Path=/ 且无 Domain。开发 HTTP 环境无法发送 Secure Cookie，应使用
独立开发 Cookie 名并明确 `SecurePolicy=SameAsRequest`；生产环境若不是 HTTPS 必须拒绝 ready，
不能静默降级生产 Cookie。

会话期限建议：

- 未勾选“保持登录”：浏览器 session Cookie，服务端 idle 12 小时、absolute 24 小时；
- 勾选“保持登录”：持久 Cookie，idle 7 天、absolute 30 天；
- 每次请求检查数据库期限；只有超过刷新间隔（例如 5 分钟）才 CAS 更新 last_seen/idle expiry，
  避免每请求写 SQLite；
- sliding 不能越过 absolute expiry；过期或 revoked session 统一拒绝并删除客户端 Cookie；
- claims 只含 user ID、role、auth session ID、security stamp 和 authentication instant；不含
  password hash、quota、私有路径或完整偏好 JSON。

### 7.2 Data Protection

- key ring 固定到 DataRoot 下的 `keys/`，不能留在容器临时 home；
- 父目录 mode `0700`、key 文件 `0600`，拒绝 symlink、特殊文件和错误 owner；
- 设置稳定 application discriminator，避免不同应用误解密彼此 Cookie；
- key material 不写数据库、日志、镜像或 Git；
- 备份 `/data` 时 key ring 与 SQLite 必须一起备份，否则恢复后所有 Cookie 会失效；
- 本步骤不要求外部 KMS；面向多用户生产环境仅用文件权限是否足够需在部署安全文档中明确。

### 7.3 CSRF

所有使用 Cookie 身份的非安全方法（POST/PUT/PATCH/DELETE）必须验证 antiforgery token，包括
login、logout、change-password 和管理员操作：

1. `GET /api/v1/auth/csrf` 获取 request token；响应同时设置框架保护的 antiforgery Cookie；
2. React 在请求头 `X-CSRF-TOKEN` 发送 request token；
3. token 只保存在当前页面内存，不进 localStorage 或 URL；
4. token 缺失、错误或与 Cookie 不匹配统一返回版本化 API error；
5. GET/HEAD 不产生业务状态变化；
6. 不配置宽泛 CORS。开发环境由 Vite 同源 proxy `/api` 到 API；
7. 登录 CSRF 防止 login-CSRF，不能因用户尚未认证而豁免。

### 7.4 WebSocket Origin 和恢复授权

建立两个独立检查：

- upgrade gate：要求有效 Cookie session，并将 `Origin` 与显式允许的 HTTPS origin 精确匹配；
- resume authorizer：每次 `session.resume` 都根据当前 actor 和目标 Session 重新查询数据库授权，
  不能沿用连接建立时缓存的 owner 判断。

P1-02 对这两个组件做单元/集成测试，并提供 P1-08 可调用接口；不建立假的生产 WebSocket 消息
流。P1-08 必须在真实 `/api/v1/realtime` adapter 上再次验证 AUTH-005。

## 8. HTTP API 契约

所有端点位于 `/api/v1`。JSON camelCase、枚举大写下划线。错误统一为：

```json
{
  "code": "INVALID_CREDENTIALS",
  "message": "邮箱或密码不正确。",
  "requestId": "req_...",
  "details": null
}
```

生产错误不包含异常、SQL、路径或内部用户状态。认证端点设置 `Cache-Control: no-store`；涉及
Cookie 的响应不被共享缓存。

### 8.1 启动 bootstrap 与就绪语义

不提供 `/setup` 页面、公开注册端点或 bootstrap HTTP API。schema 兼容且
`bootstrap_status=BOOTSTRAP_REQUIRED` 时，API 启动初始化器读取三个部署变量并在对外 ready 前
原子创建首个管理员。配置缺失或非法时不得创建部分数据：

- `/health/live` 在进程本身正常时仍返回 200；
- `/health/ready` 返回 503 和稳定原因 `BOOTSTRAP_CONFIGURATION_INVALID`；
- login 与其他业务/admin endpoint 在 bootstrap 完成前不可用；
- HTTP 响应、日志和审计只报告变量名/稳定错误码，不回显变量值；
- 状态为 COMPLETED 时完全忽略 bootstrap 变量，变量缺失或变化都不影响 ready，也不修改账户。

### 8.2 匿名可访问

| 方法与路径 | 请求/响应 | 说明 |
| --- | --- | --- |
| `GET /api/v1/auth/csrf` | `{ token }` | 设置 antiforgery Cookie，不创建登录态 |
| `POST /api/v1/auth/login` | `{ email, password, rememberMe }` → `CurrentUser` | CSRF、rate limit；username 不可用于登录 |
| `GET /health/live` | 现有响应 | 公开，不泄露 DB/用户信息 |
| `GET /api/v1/version` | 现有响应 | 公开 |

`/health/ready` 是否公开沿用运维设计，但响应不得泄露路径或异常；它不是身份探测端点。

### 8.3 已认证用户

| 方法与路径 | 请求/响应 | 说明 |
| --- | --- | --- |
| `GET /api/v1/auth/me` | `CurrentUser` | ACTIVE session；返回 id/username/email/role/mustChangePassword |
| `POST /api/v1/auth/logout` | `204` | 撤销当前 session，幂等，要求 CSRF |
| `POST /api/v1/auth/change-password` | `{ currentPassword, newPassword }` → `204` | 成功后撤销其他 session，轮换当前 Cookie |

`CurrentUser` 不返回 password hash、security stamp、锁定计数、quota profile 内部路径或 Cookie
期限。`mustChangePassword=true` 的用户只允许 `csrf`、`me`、`logout` 和 `change-password`；
访问其他受保护端点返回 `403 PASSWORD_CHANGE_REQUIRED`。

### 8.4 管理员用户管理

| 方法与路径 | 请求/响应 | 说明 |
| --- | --- | --- |
| `GET /api/v1/admin/users?cursor=&limit=` | 稳定游标页 | ADMIN；不返回 hash/security stamp |
| `POST /api/v1/admin/users` | username、email、temporaryPassword、role、quotaProfileId | 创建，返回 201；必须审计 |
| `PATCH /api/v1/admin/users/{id}` | username/email/role/status + `If-Match` | CAS；身份字段变化撤销会话；必须审计 |
| `POST /api/v1/admin/users/{id}:reset-password` | temporaryPassword + `If-Match` | mustChange=true；必须审计 |

- 列表按 `(created_at, id)` 稳定游标分页，limit 有上限；
- 重复规范化邮箱/用户名分别返回 `409 EMAIL_ALREADY_EXISTS` / `USERNAME_ALREADY_EXISTS`；
- 不存在用户和不可定位资源统一 404；非管理员调用 admin route 返回 403；
- `If-Match` 缺失返回 428，版本冲突返回 412；
- 创建用户时验证 quota profile 存在；本步骤由 bootstrap 创建 default profile，但不开放通用
  配额编辑 UI；
- reset/change password 不把临时密码放在响应；管理员通过原请求中的安全通道传给用户；
- 管理动作 request body、响应和日志必须按敏感字段策略过滤。

### 8.5 前端路由行为

- `/login`：匿名可见；表单只接受邮箱和密码；已登录且无需改密时跳转到安全的内部
  `returnTo` 或 `/games`；
- `/change-password`：登录且 mustChange=true 时强制进入；成功后进入原安全目标；
- 其他展示路由由 `RequireAuthenticated` 包裹；认证加载期间显示稳定 skeleton，不短暂泄露页面；
- `/admin` 及未来 `/admin/users` 由 `RequireAdmin` 包裹；普通用户导航中不显示管理员入口；
- 任何 `returnTo` 只接受以单 `/` 开头的站内相对路径，拒绝 `//`、scheme 和反斜杠；
- 401 清理客户端 query cache 并跳登录；403 显示无权或必须改密，不循环刷新；
- logout 成功清空 TanStack Query cache、身份内存和敏感表单，使用 replace navigation；
- 删除原型的默认密码、任意账户提示和无实现的“忘记密码”按钮；
- sidebar 用户名/角色来自 `CurrentUser`，不能再使用硬编码“林间/玩家账户”。

## 9. 资源授权模型

### 9.1 两层授权

每个受保护操作依次执行：

1. 身份/角色策略：session 有效、用户 ACTIVE、已完成强制改密，必要时 ADMIN；
2. 资源授权：根据路由 ID 从数据库读取最小 descriptor，再判断 owner、visibility 和 action。

前端隐藏按钮、请求中的 `ownerUserId`、Cookie 内旧 role、资源 ID 前缀和“管理员通常能访问”
都不是授权证据。

建议稳定 action：

```text
GAME_READ, GAME_MUTATE, GAME_VALIDATE, GAME_ACTIVATE, GAME_BLOCK
SESSION_READ, SESSION_CONTROL, SESSION_FORCE_STOP, SESSION_RESUME
SAVE_LIST, SAVE_DOWNLOAD, SAVE_MUTATE
USER_ADMINISTER, AUDIT_READ
```

### 9.2 权限矩阵

| 资源/动作 | Owner PLAYER | 其他 PLAYER | ADMIN |
| --- | --- | --- | --- |
| 私有 Game/current/workspace 读取 | owner 按 scope 允许 | 隐藏 | 默认隐藏；除非也是 owner |
| SERVER_SHARED Game current 读取 | 允许 | 允许 | 允许 |
| Game workspace 编辑、验证、启用 | owner 允许 | 隐藏 | 默认不允许；安全 BLOCK 使用独立动作 |
| Session 元数据/连接/输入/关闭 | owner 允许 | 隐藏 | 默认隐藏；force-stop 使用独立 admin 动作 |
| Save 列表/下载/修改 | owner 按状态允许 | 隐藏 | 默认隐藏，不授予内容访问 |
| Worker/Session 运维摘要 | 不允许 | 不允许 | 独立 admin endpoint 允许 |
| 用户管理、审计查询 | 不允许 | 不允许 | 允许 |

管理员强停只能作用于运维标识和状态，不因此获得 console snapshot、玩家输入或存档内容。
未来若确需支持性访问，必须有显式临时授权、原因和审计设计，不能扩大通用 ADMIN policy。

### 9.3 不可枚举响应

- owner-scoped 资源读取、修改和删除中，“不存在”和“存在但无权”都映射为 404；
- 列表查询必须在 SQL 中按 actor 过滤，不能先加载全表再在内存过滤；
- 管理员专属路由对已认证非管理员返回 403，因为路由能力本身不是用户资源枚举；
- 登录失败统一 401；未登录访问受保护 API 返回 401 JSON，不重定向 HTML；
- API Cookie handler 的 `OnRedirectToLogin/AccessDenied` 必须改写为 401/403，不能返回 302；
- 浏览器页面路由重定向由 React 处理。

### 9.4 授权接口

Application authorizer 输入只能是：

```text
CurrentActor(userId, role, authSessionId)
ResourceKind
resourceId
ResourceAction
```

它通过 `IResourceAccessReader` 获取最小 descriptor：owner ID、Game visibility、父资源 ID、
Session 状态和是否存在 lease 等当前动作所需字段。返回稳定 decision：`Allowed`、
`NotFoundOrHidden`、`Forbidden`、`PasswordChangeRequired`。不要返回 EF row 或区分“隐藏的资源存在”。

每次 WebSocket resume、存档操作的开始和文件替换前、Session 控制命令发送前都必须重新调用。
长操作的授权检查不能跨越状态竞争；后续任务需要在提交副作用前重验当前状态/lease。

## 10. 审计设计

### 10.1 Application 端口

定义不可变 `AuditEvent` 和 `IAuditWriter.AppendAsync`。事件至少包括 P1-01 已有字段，时间由
`TimeProvider` 注入；ID 由安全 UUIDv7 generator 产生。

稳定 action catalog 在代码中集中定义，P1-02 至少包含：

```text
IDENTITY_LOGIN_SUCCEEDED
IDENTITY_LOGIN_FAILED
IDENTITY_LOGOUT
IDENTITY_PASSWORD_CHANGED
ADMIN_USER_CREATED
ADMIN_USER_PROFILE_CHANGED
ADMIN_USER_STATUS_CHANGED
ADMIN_USER_ROLE_CHANGED
ADMIN_USER_PASSWORD_RESET
SYSTEM_ADMIN_BOOTSTRAP_FAILED
SYSTEM_ADMIN_BOOTSTRAPPED
```

后续任务把 `SESSION_CREATED/CONNECTED/CLOSED/CRASHED/FORCE_STOPPED`、
`GAME_ACTIVATED`、`SAVE_DELETED` 等追加到同一 catalog。

### 10.2 内容最小化

- `actor_user_id` 只记录已知 actor；未知登录失败为 NULL/SYSTEM；
- `resource_type/id` 使用稳定内部 ID，不使用游戏名、Session 名、username 或 email；
- request ID 可以记录，session cookie ID 不记录；
- metadata 使用每个 action 的强类型 allowlist DTO，序列化后受 P1-01 JSON/长度约束；
- 登录失败 metadata 只允许原因大类（例如 `INVALID_CREDENTIALS`）和 rate-limit outcome，不记录
  输入邮箱、IP、密码信息或 user existence；
- 管理员变更记录允许的 before/after role/status；email/username 变化只记录对应字段发生变化和
  reason code，不记录原值、新值、password/hash；
- 默认应用日志只使用 `userIdHash`，审计表按权限保存真实 actor ID。

### 10.3 原子性与失败

- 改变用户/资源状态的敏感用例把业务写入和 audit insert 放在同一个 SQLite transaction；
- 不能在事务提交后通过 fire-and-forget 队列补审计；
- 审计失败返回操作失败，原状态保持；
- 纯失败尝试若没有业务变更，也应尽力写独立失败审计；审计库不可写时登录可以 fail closed，
  避免产生无法追踪的新会话；
- 读取 `/auth/me` 不逐次审计；否则会产生高噪声和写放大；
- P1-01 的 append-only trigger 必须继续存在，普通 DbContext 不暴露 update/delete audit 方法。

## 11. API 安全配置

### 11.1 中间件顺序

建议明确测试以下顺序：

```text
forwarded headers（仅受信任代理）
request ID / exception mapping
HTTPS/HSTS（非 Development）
security headers
static files
routing
rate limiting
authentication
authorization
antiforgery
endpoints
```

实际 .NET 10 minimal API 的 antiforgery middleware/endpoint metadata 顺序以官方 API 要求为准，
但认证必须先于依赖 actor 的授权，错误响应必须保持 JSON。

### 11.2 安全响应头

至少设置：

```text
Content-Security-Policy: default-src 'self'; object-src 'none'; base-uri 'none';
  frame-ancestors 'none'; form-action 'self'
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
Permissions-Policy: camera=(), microphone=(), geolocation=()
```

根据 Vite production bundle 的实际资源调整 `script-src/style-src/img-src`，不使用 `'unsafe-eval'`
或任意 `*`。当前展示页的内联 `style` 属性应改成有限 CSS class/data attribute 方案，使生产
`style-src 'self'` 不必加入宽泛的 `'unsafe-inline'`；确有无法移除的需求必须单独评审。开发 HMR
可使用单独 Development policy，不能污染生产 policy。框架不再依赖已废弃的
`X-XSS-Protection`。

### 11.3 错误与日志过滤

- 建立统一 API error mapper，按第 8 节固定 envelope 返回 `application/json`；不要混用 HTML、
  裸字符串或另一套 Problem Details shape；
- request body logging 默认关闭；若未来启用，只允许字段白名单并永远排除 password/token；
- query string 不允许承载密码、session 或 CSRF；访问日志过滤 Cookie/Authorization；
- 启动配置验证不能把 key ring path、数据库 connection string 或 secret 内容写入响应；
- 测试使用 canary password/session/token 搜索捕获的结构化日志和 HTTP error body。

## 12. 前端接入计划

保留现有视觉设计，但把身份从 `App.tsx` 的展示逻辑中拆出：

```text
src/CloudEmuera.Web/src/
├── api/
│   ├── client.ts
│   └── auth.ts
├── auth/
│   ├── AuthProvider.tsx
│   ├── RequireAuthenticated.tsx
│   ├── RequireAdmin.tsx
│   └── useCurrentUser.ts
├── pages/
│   ├── LoginPage.tsx
│   ├── ChangePasswordPage.tsx
│   └── AdminUsersPage.tsx
└── App.tsx
```

实现要求：

- 使用 TanStack Query 管理 `/auth/me`；fetch 始终 `credentials: "same-origin"`；
- API client 在 unsafe request 前获取/刷新 CSRF token，并只保存在内存；
- 登录按钮具有 pending、防重复提交、可聚焦错误摘要和 `aria-live`；
- 登录错误统一显示，不根据服务端内部原因改变文案；
- 密码字段默认空，不提供展示凭据；支持密码管理器的标准 autocomplete；
- `rememberMe` 只控制 Cookie/session 期限，不保存邮箱或密码；
- 强制改密页面校验新密码确认，服务端仍做权威验证；
- 管理员用户页至少支持列表、创建、修改 username/email、禁用/启用、角色变更和临时密码重置确认；
- 危险动作使用现有 modal 视觉组件，等待服务端成功后才更新 UI；
- 保留现有展示页，但所有入口处于 auth guard 后；管理员导航按真实角色显示；
- 手机 viewport 下登录、改密和用户管理可完成，焦点、label、错误提示和触摸目标满足现有
  可访问性基线。

开发 Vite 增加 `/api` proxy 到 `http://api:28647`（容器内）或配置化等价地址，使浏览器保持
同源 Cookie/CSRF 语义；不得通过允许任意 origin 的 CORS 解决开发连接。

## 13. 自动化测试设计

### 13.1 Domain/Application 单元测试

新增 `tests/CloudEmuera.Application.Tests`：

- 权限矩阵逐格测试，包括 shared Game read 与 owner-only mutate；
- ADMIN 只能执行明确 admin action，不能读取其他用户私有 Save/Console；
- 不存在与隐藏资源返回相同 decision；
- must-change-password gate；
- 安全 returnTo validator；
- audit metadata allowlist/大小限制；
- 最后 active admin 禁用/降级规则；
- role/status/password 更新需要审计，审计失败时 use case 返回失败。

Category 使用 `Authorization`、`Audit`，测试注释映射 AUTH/OPS 编号。

### 13.2 Infrastructure 测试

扩展 `CloudEmuera.Infrastructure.Tests`：

- P1-01 数据库带代表数据升级到 `AddLocalIdentitySessions` 且不丢数据；
- migration failure 完整回滚，Online Backup 在变更前可独立恢复；
- instance singleton 的单行、状态组合、不可逆转换和 initial admin FK 约束；
- auth session FK、时间、revocation/reason、布尔和 ID CHECK；
- 自定义 user store 不创建或查询任何 `AspNet*` 表；
- email/username normalization、邮箱登录查询和两种唯一冲突；
- password hash 随机 salt、verify、rehash、Unicode/最大长度；
- session create/validate/revoke/expire/cleanup；
- role/status/password 变更与 revoke/audit 的事务原子性；
- 并发登录失败计数不丢失，阈值只产生合法 lockout；
- 并发禁用/降级最后管理员最多一个成功；
- audit trigger 仍拒绝 update/delete。

### 13.3 API 集成测试

新增 `tests/CloudEmuera.Api.IntegrationTests`，使用 `WebApplicationFactory<Program>`、真实临时
SQLite、真实 migration、测试 Data Protection key ring 和可控 `TimeProvider`。不得用 EF
InMemory provider 替代 SQLite 约束。

`Category=Bootstrap` 至少覆盖：

1. 全新数据库 + 三个合法变量原子创建唯一 ADMIN、默认 quota、COMPLETED 标记和审计，且
   `mustChangePassword=true`；
2. 任一变量缺失、空白、非法或邮箱/username 冲突时 ready 返回稳定错误，不产生部分用户、quota
   或审计；原始 password 不出现在日志、响应、审计和数据库明文列；
3. 两个 API host 并发启动最多一个 bootstrap 成功，最终只有一套管理员/quota/completion 审计；
4. hasher、quota、audit 或状态 CAS 任一步故障时整个事务回滚，状态仍 REQUIRED，可修正配置重试；
5. COMPLETED 后删除或改变 bootstrap 变量再重启，不修改 username、email、password、role 或
   ready 状态；删除/禁用/降级所有管理员也不重跑 bootstrap；
6. P1-01 已有 PLAYER/无密码用户时迁移仍 REQUIRED 且数据保留；已有可登录 ACTIVE ADMIN 时
   确定性迁移为 COMPLETED；
7. schema 落后/未知、instance singleton 缺失或非法时 ready fail closed；
8. bootstrap 密码仅产生 Identity hash，首次用 bootstrap email 登录后只能改密闭环；
9. 测试宿主显式注入配置，不能从仓库根 `.env` 或调用进程的同名变量意外取值；
10. 并行测试各用独立临时 SQLite/DataRoot，清理只触及本测试生成的绝对路径。

`Category=Authentication` 至少覆盖：

1. 匿名访问受保护 API 返回 JSON 401，不是 302；
2. GET csrf + 正确 token，以规范化邮箱登录成功；缺失/错误 token 失败；
3. 未知邮箱、错误密码、DISABLED、LOCKED 返回相同 401 shape；username 即使匹配也不能登录；
4. 并发错误密码触发持久 lockout，API 重启后仍有效；
5. session/nonpersistent 与 remember-me Cookie 属性、idle/absolute expiry；
6. logout 撤销服务端 session；复制旧 Cookie 重放失败；重复 logout 安全；
7. API host 重建后，保留 DB/key ring 的会话仍有效；key ring 不同则失败；
8. change-password 校验旧密码、撤销其他 session、轮换当前 session；
9. must-change 用户不能访问普通受保护端点，改密后可访问；
10. password/session/CSRF canary 不出现在响应、日志或 audit metadata；
11. login rate limit 有界，伪造 forwarded header 不能绕过默认来源判断；
12. Cookie 认证失败/过期会清 Cookie 且不缓存敏感响应。

`Category=Authorization` 至少覆盖：

1. PLAYER 调用 admin user API 返回 403；ADMIN 成功；
2. 两用户资源 descriptor fixture：owner 允许，另一用户得到与不存在相同 404 映射；
3. SERVER_SHARED Game 允许读取但拒绝非 owner 修改；
4. ADMIN force-stop descriptor 允许，但私有 console/save read 仍隐藏；
5. 管理员禁用用户后，目标用户下一请求失败；角色改变后旧 Cookie 权限不残留；
6. 每次模拟 `session.resume` 都重新读取 owner/status，而不缓存首次结果；
7. WebSocket upgrade gate 拒绝匿名、无 Origin、错误 Origin 和已撤销 Cookie；
8. `returnTo` 不产生站外重定向；
9. 用户列表不返回 password hash/security stamp/session ID；
10. admin mutation 缺 If-Match、版本冲突、最后管理员不变量返回稳定错误。
11. 管理员可为 P1-01 遗留用户设置唯一 email；email 变化后旧会话撤销，旧 email 不再登录，新
    email 可登录且审计不含邮箱原文。

`Category=Audit` 至少覆盖：

- bootstrap 失败/完成、成功/失败登录、logout、改密、用户创建、状态/角色改变和重置的
  action/result；
- actor/resource/request correlation 正确；
- metadata 不含密码、hash、Cookie、session、CSRF、完整邮箱或 username；
- 注入 audit insert 失败时敏感变更没有提交；
- 普通路径不能 update/delete audit row。

资源授权 API 测试可在测试宿主映射最小 adapter 来驱动同一个 production authorizer；不得向
生产 API 添加 `/authorization-probe` 之类测试后门。

### 13.4 Frontend 组件测试

使用 MSW 或等价 fetch mock；若新增依赖，固定版本并更新 `pnpm-lock.yaml`：

- auth 初始化 loading 时不渲染受保护内容；
- 401 跳到登录并保留安全 returnTo；
- 登录表单只有 email/password，email autocomplete 与校验正确；username 不作为 fallback；
- 登录成功、统一错误、pending 防双击、remember-me 请求；
- mustChange 跳改密，成功后恢复目标页；
- PLAYER 不显示 admin nav，ADMIN 显示；直接访问 admin route 仍有前端 fallback；
- logout 清 query cache 并 replace 到登录；浏览器 back 不恢复受保护 DOM；
- CSRF token 获取/复用/失效刷新；
- 管理员用户列表和敏感操作成功、409/412/失败回滚显示；
- 可访问 label、焦点错误摘要、键盘提交和移动布局。

现有展示测试继续通过；如果因为 auth guard 需要 fixture，应提供统一测试 AuthProvider，不能在
产品代码中加入“prototype bypass”。

### 13.5 Playwright 冒烟

新增最小真实身份 E2E：

1. 测试以专属临时 env、DataRoot 和 SQLite 运行 Migrator/API，启动时自动创建首个管理员；
2. 管理员用 bootstrap email 与 `temporary-password` 登录，被强制改密后进入应用；
3. 重启 API 并改变 bootstrap 变量，确认现有管理员未被覆盖且旧临时密码已失效；
4. 管理员创建玩家；玩家用 email 和临时密码登录 → 强制改密 → 进入展示游戏库；
5. 管理员登录后显示真实 username/email/角色 → 注销 → 受保护页返回登录；
6. PLAYER 无管理员入口且访问 admin route 被拒绝；ADMIN 能打开用户管理；
7. 在一个移动 viewport 完成邮箱登录、改密错误显示和注销；
8. 测试结束不在仓库 `.env`/`data/` 留修改、用户、Cookie、key 或临时数据库。

P1-02 不要求五浏览器执行完整业务旅程；完整浏览器矩阵仍在 P1-10/P1-14。

## 14. `.env` 首次管理员 bootstrap

### 14.1 配置契约

项目根 `.env` 为个人部署的人工配置入口，示例固定为：

```dotenv
CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=admin
CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=admin@example.com
CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password
```

三个变量仅在持久状态为 BOOTSTRAP_REQUIRED 时全部必需。username 使用 6.2 的显示名规则，email
使用 6.1 的登录邮箱规则，password 使用 6.3 的密码规则；示例值
`CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password` 是本项目固定的简化初始值。`.env`
不得提交 Git，应用不得打印其内容。首次改密完成后部署者可以删除这三个变量，也可以保留固定的
`temporary-password` 示例值；COMPLETED 实例无论变量仍在、缺失或变化都必须忽略它们。

`compose.dev.yaml` 的 API service 必须把三个值显式映射进容器，例如
`CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL: ${CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL}`；只向 Compose 传
`--env-file` 不会自动形成容器环境。生产入口也必须显式传入，应用本身不能读取仓库路径或
`.env.example`。缺值必须保持缺值并触发 readiness 错误，Compose 不能用隐式默认管理员补齐。

### 14.2 启动与原子创建

API 在接受登录/业务流量前执行：

1. 检查 migration compatibility；schema 落后或未知时不自动 migrate，ready 失败；
2. 读取 `instance_state`；缺失、非法或状态字段矛盾时 fail closed；
3. COMPLETED 时立即结束，不读取/验证 bootstrap 变量，不根据当前管理员数量反推状态；
4. BOOTSTRAP_REQUIRED 时读取并完整验证三个变量；失败则 ready 返回
   `BOOTSTRAP_CONFIGURATION_INVALID`，数据库保持不变；
5. 在 SQLite `BEGIN IMMEDIATE` 或等价短事务中以 `state_version` CAS，创建默认 quota profile，
   用正常 Identity hasher 创建 ACTIVE ADMIN（同时保存 username/email，
   `mustChangePassword=true`），插入 `SYSTEM_ADMIN_BOOTSTRAPPED`，最后更新为 COMPLETED；
6. 任一步失败整体回滚；多个 API 进程并发时只有 CAS 胜者创建，失败者重新读取 COMPLETED；
7. bootstrap 不自动签发 Cookie。部署者必须在 `/login` 用 email 与临时密码登录并完成强制改密。

默认 quota profile 的具体数值由集中常量/配置冻结，migration 只插入 instance singleton，不 seed
用户或 quota。P1-05/P1-12 正式执行配额前，UI 不得把这些数值描述为 Worker 沙箱保护。

### 14.3 同一仓库的人工与自动化环境隔离

人工开发与自动化校验可以共用同一 checkout，但不能共用配置解析上下文、Compose project、端口
或持久数据：

- 人工运行 `./scripts/dev-up.sh` 时使用仓库根 `.env`、正常 `./data`、固定开发 project/端口；
- 自动化脚本绝不能读取、source、修改、覆盖或删除仓库根 `.env`，也不能触碰人工 `./data`；
- 每次自动化在 `mktemp -d` 生成专属 env 文件与 DataRoot，并显式传递
  `docker compose --env-file <absolute-temp-env> -p <unique-project>`；不得依赖 Compose 自动加载
  当前目录 `.env`；
- 临时 env 必须显式写入测试专属 username/email、
  `CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=temporary-password`、UID/GID、DataRoot 和端口，并由
  Compose service 显式映射进对应容器。调用脚本应
  清除继承的同名配置后再加载该文件，防止 shell 环境优先级让人工值渗入测试；
- 并行任务使用唯一 project name、独立 SQLite/key ring/DataRoot；能用 `compose run` 而无需发布
  端口的测试不发布端口，E2E 使用动态空闲端口并写入专属 env；
- cleanup/trap 只允许对本次已记录且位于临时根下的绝对路径和唯一 Compose project 操作，不能
  删除根 `.env`、`./data` 或停止人工 project；
- `scripts/lib/dev-env.sh` 应扩展/配套一个 test-env helper，集中生成和验证上述参数。所有身份 E2E
  和 CI 只能调用 helper，不各自复制容易漂移的 Compose 命令；
- 静态测试必须证明自动化脚本不引用仓库根 `.env`/`./data`；集成测试在专属 fixture project
  directory 中放置 sentinel `.env`，再显式传临时 env 并断言 sentinel 未被读取。若真实 checkout
  已存在人工 `.env`/SQLite，只做运行前后 fingerprint 比较，测试不得创建或改写它们；另有并行
  启动测试证明两个自动化 project 互不覆盖。

`.env.example` 可以提交上述示例值，但它不是运行时 secret，也不能被 API 自行读取为默认配置。
若使用者没有复制为 `.env`，全新实例应明确 ready 失败，而不是用示例账户静默启动。

### 14.4 管理员遗失访问权

初始化完成后出现“无 ACTIVE ADMIN”、管理员忘记密码或 Data Protection key 丢失，都不能重跑
bootstrap：

- key ring 丢失只使 Cookie 失效，管理员仍可用邮箱和密码重新登录；
- 忘记唯一管理员密码需要未来独立的离线恢复工具/受控数据库恢复流程；
- 后续恢复方案必须验证 DataRoot 主机权限、要求停机/互斥、轮换 security stamp、撤销 session
  并写 SYSTEM audit；
- 在恢复方案实现前，不得提供隐藏 endpoint、环境变量持续重置密码或“删管理员后重新初始化”捷径。

## 15. 实施顺序

### 阶段 A：ADR、契约和测试骨架

1. 编写 ADR-0001 并更新索引；
2. 新建 Application 与 API integration 测试项目并加入 solution；
3. 冻结 API error、CurrentUser、管理员请求/响应、action catalog 和权限矩阵；
4. 为 email/username/password、last-admin、resource authorization 和 audit allowlist 写单元测试；
5. 为现有展示前端提供测试用 Auth fixture，但不加入产品 bypass。

### 阶段 B：身份 migration 和 store

1. 增加 instance singleton、users 改密字段、AuthSession row/configuration 和 DbSet；
2. 生成新的 migration，审查 SQLite rebuild、FK/CHECK/index/Down；
3. 完成 P1-01 → P1-02 带数据升级、backup 和失败回滚测试；
4. 实现最小 Identity user store、password policy、normalizer 和 lockout CAS；
5. 实现 auth session repository、过期判断、撤销和批量清理。

### 阶段 C：Application 身份、授权和审计

1. 实现 bootstrap initializer、email login/logout/change-password/user administration 用例；
2. 实现 CurrentActor、资源 descriptor reader 端口和权限矩阵；
3. 实现 audit writer、强类型 metadata 和事务编排；
4. 完成管理员最小权限、最后 active admin 和审计失败回滚测试；
5. 实现 WebSocket Origin/resume 授权组件接口。

### 阶段 D：API 安全接线

1. 注册 DbContext、Identity store、Cookie、Data Protection、antiforgery 和 rate limiter；
2. 配置 JSON 401/403、统一 error 和安全头；
3. 实现 bootstrap options、initializer、instance coordinator 和 readiness check；
4. 映射 auth/admin endpoints 和 policy；不映射 setup/register endpoint；
5. 验证中间件顺序、production HTTPS/cookie 配置和 trusted proxy；
6. 完成 bootstrap 竞争与配置隔离、真实 Cookie/CSRF/重启/过期/日志脱敏 API 集成测试。
7. 实现 `scripts/test-identity.sh` 隔离 helper，并让 `scripts/check.sh` 调用它；自动化身份测试不得
   通过默认 Compose project 执行。

### 阶段 E：身份前端

1. 拆分 React AuthProvider、API client、路由 guards 和身份页面；
2. 将登录表单固定为 email/password，并实现 bootstrap 管理员首次登录的强制改密流；
3. 把展示版 sidebar/admin route 接到真实 CurrentUser；
4. 增加管理员用户页及敏感动作确认；
5. 完成组件测试和桌面/移动 Playwright 邮箱登录/身份冒烟。

### 阶段 F：文档与质量门

1. 更新 requirements/design 中实际 Cookie/session、`.env` bootstrap、邮箱登录和 AUTH-005 分工；
2. 更新部署说明，说明 HTTPS、Data Protection key 备份、人工/自动化隔离、首次管理员和恢复边界；
3. 更新开发计划的实际 migration id、测试数量和命令；
4. 扫描代码/日志/测试快照，确认没有 demo 密码和 secret；
5. 执行全部验证后把 P1-02 标记 DONE，并把 P1-03 标记 NEXT。

## 16. 验证命令

所有构建和测试通过 dev Docker。实现后的身份定向测试统一经隔离 helper 调用，helper 内部按
14.3 显式执行 Docker Compose；以下 CLI 是待实现契约，不能退回默认 project 的裸 Compose 命令：

```bash
./scripts/dev-up.sh

./scripts/test-identity.sh --suite application
./scripts/test-identity.sh --suite infrastructure
./scripts/test-identity.sh --suite api

source scripts/lib/dev-env.sh
docker compose -f compose.dev.yaml run --rm web \
  sh -c 'pnpm install --frozen-lockfile && pnpm typecheck:web && pnpm test:web && pnpm build:web'
```

同一 helper family 提供身份 E2E：

```bash
./scripts/test-identity-e2e.sh
```

脚本必须按 14.3 创建隔离 env/DataRoot/project/端口，运行 Migrator、启动 API/Web，以测试专属
bootstrap 邮箱和固定临时密码完成登录/强制改密，再执行 Playwright、停止专属 project 并只清理
专属临时目录。脚本不得读取或改变仓库根 `.env` 与人工 `./data`；密码不能出现在 argv 或输出。
`scripts/check.sh` 必须调用 `test-identity.sh`，且不得用默认人工 project 重复执行这些测试。

完整质量门：

```bash
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
git diff --check
./scripts/dev-down.sh
```

## 17. 完成定义

P1-02 只有同时满足以下条件才可标记 DONE：

1. ADR-0001 记录本地身份、邮箱登录、可撤销 Cookie session、`.env` bootstrap 和 OIDC 触发条件；
2. 新 migration 从 P1-01 带数据升级成功，失败回滚和备份恢复测试通过；
3. 没有默认 `AspNet*` 表；自定义 instance/users/auth_sessions schema 与模型一致；
4. 没有公开注册或硬编码运行时用户；首次启动只从三个 bootstrap 变量原子创建一个管理员，
   两个并发启动最多一个成功；COMPLETED 后永久忽略变量且不会因管理员缺失重开；
5. 密码按明确策略校验并使用 ASP.NET Core Identity 自适应 hash；需要时自动 rehash；
6. 未知邮箱、错误密码、禁用、锁定均返回相同 401，username 不能登录，持久 lockout 和 rate
   limit 生效；
7. Cookie 在生产使用 Secure/HttpOnly/SameSite/`__Host-`，Data Protection key 持久且权限正确；
8. 所有 Cookie 写请求验证 CSRF，开发环境使用同源 proxy 而非宽泛 CORS；
9. auth session 可逐条/全用户撤销；logout、禁用、角色和密码变化后旧 Cookie 无效；
10. API 重启保留 DB/key ring 后会话仍有效，key ring 不同时安全失效；
11. must-change-password 用户只能访问改密闭环，成功后获得正常访问；
12. 服务端资源授权器实现完整权限矩阵、SQL 列表过滤和不可枚举 404；
13. ADMIN 不自动拥有其他用户私有 Console、Session 控制或 Save 内容权限；
14. WebSocket upgrade Origin gate 和 resume reauthorization 接口通过测试，真实协议留给 P1-08；
15. 首次初始化、管理员敏感修改和身份事件写入追加式审计；业务与审计同事务；
16. 审计/日志/错误中没有密码、hash、Cookie、CSRF、session ID、完整邮箱/username 或输入全文；
17. 现有 React 展示已接真实身份 API；登录只接受邮箱，路由 guard、改密、管理员用户管理和注销可用；
18. PLAYER/ADMIN 导航和页面不同，但服务端测试证明不依赖前端隐藏；
19. 桌面和至少一个移动 viewport 的真实 bootstrap 后邮箱登录/改密/注销 E2E 通过；
20. Application、Infrastructure、API、Web 定向测试以及全量质量门全部通过；P0/P1-01 无回归；
21. 配置、ADR、部署说明、HTTP 契约、锁文件和开发计划与实现一致；
22. 自动化使用独立 temp env/DataRoot/Compose project/端口，静态和动态测试证明不会读取或修改
    同一 checkout 的人工 `.env`、`./data` 或开发容器。

## 18. 实现交接清单

- [x] 阅读本计划、requirements AUTH/SEC/OPS 和 design Identity/HTTP/Web Security/Audit 章节；
- [x] 检查 P1-01 migration 不被修改，当前展示前端修改不被覆盖；
- [x] 完成 ADR-0001、API/error 契约和权限矩阵测试；
- [x] 新增 Application/API test projects，保持架构依赖方向；
- [x] 添加 P1-02 migration 和真实 P1-01 → P1-02 升级测试；
- [x] 实现 instance singleton、bootstrap options/initializer/readiness 和并发一次性创建；
- [x] 实现 custom user store、password、lockout、auth session 和事务审计；
- [x] 实现 email-only auth/admin endpoints、Cookie/CSRF/Data Protection/rate limit；
- [x] 实现资源 authorizer 与 WebSocket 授权接口，不添加生产测试后门；
- [x] 接通 React 邮箱登录、强制改密、管理员用户管理、guards 和 logout；
- [x] 实现并验证人工 `.env` 与自动化临时环境的隔离 helper；
- [x] 完成组件/API/SQLite/并发/进程/E2E 测试；
- [x] 执行全量质量门并记录实际测试数量（262 .NET、6 Web，另含桌面/移动 E2E）；
- [x] 全部通过后标记 P1-02 DONE、P1-03 NEXT。
