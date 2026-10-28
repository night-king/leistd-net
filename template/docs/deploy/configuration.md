# 配置参考

## 配置键的查找与覆盖

框架键与代码默认值看组件随包文档的 `## 配置项（<配置节路径>）`，定位方法见 [后端说明](../../backend/README.md#leistd-框架-api)。
模板自身 Options 的可调键见本文按主题列出的用途与默认值，实际环境覆盖以 `appsettings*.json` 为准。
部署变量与必填项看 `deploy/.env.example`，未列出的调优项沿用推荐默认值。

Compose 清单外的键：非机密写 `backend/src/CompanyName.ProjectName.Api/appsettings.Production.json` 并重新构建镜像；机密或按环境变化的值在 `deploy/docker-compose.yml` 的目标服务 `environment` 加标准键（如 `Section__Key=${VARIABLE}`），使用新变量时同步 `.env.example`。清单内的键已由 Compose 覆盖，改用 Production 档前先删对应 environment 行。
Kubernetes：非机密放 ConfigMap，机密放 Secret，以 `Section__Key` 环境变量注入；证书文件通过卷挂载。

## 模板可调键

默认值就是推荐值，需要调整时才覆盖；管理界面已有的设置优先在界面调整。

| 配置节与键 | 用途与默认值 |
| --- | --- |
| `RequestLogging:Level` | 正常请求的日志级别，默认 `Information`；可在系统设置的「运维」面板覆盖 |
<!--#if (SpaFrontend)-->
| `SessionCookie:SameSite`、`ExpireDays` | 浏览器会话策略默认 `Lax`，滑动过期默认 7 天；安全边界见 [浏览器认证](../standards/auth.md#浏览器认证) |
<!--#endif-->
<!--#if (IncludeLocalization)-->
| `DisplayDefaults:Language`、`TimeZone` | 界面语言与展示时区（IANA 名）的部署基线；默认空，浏览器跟随设备，服务端回落请求文化与 UTC；组织与个人可覆盖，非法值启动失败，修改基线需重启各实例 |
<!--#else-->
| `DisplayDefaults:TimeZone` | 展示时区（IANA 名）的部署基线；默认空，浏览器跟随设备，服务端回落 UTC；组织与个人可覆盖，非法值启动失败，修改基线需重启各实例 |
<!--#endif-->
<!--#if (LocalIdentity)-->
| `UserRegistration:Enabled`、`CaptchaExpiryMinutes` | 自助注册默认开放，图形验证码默认有效 5 分钟；注册开关可在「注册设置」覆盖 |
<!--#if (Email)-->
| `UserRegistration:EnableEmailVerification`、`EmailCodeExpiryMinutes`、`EmailCodeSendIntervalSeconds`、`EmailCodeMaxAttempts` | 邮箱验证默认关闭；验证码有效期 5 分钟，发送间隔 60 秒，最多错误 5 次；验证开关在「注册设置」覆盖，开启前先提供摘要密钥（见下文） |
<!--#endif-->
| `Passkeys:RelyingPartyName` | 通行密钥显示的站点名称，默认 `MyProject`；域名与来源见下文 |
<!--#endif-->
<!--#if (RemoteTokenAuth)-->
| `Leistd:ServiceClients:Identity:AccountPreferences:FreshFor`、`RetainFor` | 收件人偏好缓存默认新鲜 30 秒、保留 24 小时；保留期须长于新鲜期，当前用户 `/me` 每次条件重验证 |
<!--#if (ResourceBrowserSession)-->
| `Authentication:Scope` | 浏览器会话申请的 API scope，未配置时取 `Authentication:Audience` |
| `SessionCookie:RefreshGracePeriod` | Identity 不可达时沿用本地会话主体的宽限期，默认 1 小时；行为见 [浏览器认证](../standards/auth.md#浏览器认证) |
<!--#endif-->
<!--#if (IncludeMultiTenancy)-->
| `Leistd:MultiTenancy:Routing:MaxStaleness` | Identity 不可达时沿用路由与库清单的陈旧上限，默认 1 小时；行为见 [服务间调用](../standards/service-invocation.md#认证中心暂时不可用) |
<!--#endif-->
<!--#endif-->
<!--#if (OpenIddictServer)-->
| `OAuth:Issuer` | 对外签发方地址，默认从请求 host 取得；跨服务验证时显式配置 |
| `OAuth:ClientCredentialsAccessTokenLifetime` | 机器客户端访问令牌寿命，默认 2 小时；用户访问令牌另由 `OAuth:AccessTokenLifetime` 控制 |
<!--#endif-->

## 配置与机密的分层

<!--#if (SpaFrontend)-->
各环境共用同一套配置键，只换值的来源。浏览器页面与所属 API 必须同源（同镜像托管，或分进程经部署代理统一外部源），前端构建不注入 API 地址；同源、Cookie 与 SameSite 规则见 [浏览器认证](../standards/auth.md#浏览器认证)。
<!--#else-->
各环境共用同一套配置键，只换值的来源。纯 API 通过 Bearer 认证，不配置浏览器 Cookie、OIDC 回调或前端网关。
<!--#endif-->
<!--#if (LocalIdentity)-->

口令哈希使用 Identity 原生 IdentityV3 格式（PBKDF2-HMAC-SHA512），模板默认迭代 220,000 次。通过 `PasswordHash__IterationCount` 按部署硬件调整成本；提高成本后，存量口令在正确且允许登录时自动重新哈希，保留安全版本与已有会话。算法与格式由原生实现维护，参数政策见 [OWASP](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#pbkdf2)。
<!--#endif-->
<!--#if (OpenIddictServer)-->

多系统退出不会即时通知其他依赖方：模板没有 OIDC back-channel logout。登记为会话绑定的依赖方在 Identity 会话结束后，于访问令牌到期时续期失败而收敛；未绑定的依赖方持有的刷新令牌不受影响。

授权与退出请求缓存在控制库的令牌表里（request token），重新认证证明、退出确认凭据与 Cookie 由 Data Protection 保护；重新认证证明比较签发时刻与会话开始时间，各副本的时钟须同步。多实例 Identity 之间一次登录或退出可能落在不同副本，因此除共享缓存外，还要共享同一个控制库、同一套令牌证书、同一个 Data Protection 密钥环与应用名，以及分布式锁。修改开放应用登记后滚动重启 Identity：OpenIddict 应用缓存只在本进程失效。
<!--#endif-->

| 环境 | `ASPNETCORE_ENVIRONMENT` | 非机密配置 | 机密 |
| --- | --- | --- | --- |
| 本机开发 | `Development` | `appsettings.Development.json`（随仓库提交，全队共用） | `dotnet user-secrets`（每人一份，见 [后端说明](../../backend/README.md#开发配置)） |
| 集成测试 | `Testing` | 测试夹具显式给出 | 测试夹具给出确定性的假值 |
| 测试 / 预发 | `Staging` | 环境变量，需要时加 `appsettings.Staging.json` | CI/CD 的 secret 注入为环境变量 |
| 生产 | `Production` | 环境变量与 `appsettings.Production.json`（只放非机密） | 密钥管理系统，经环境变量或挂载文件注入 |

## 部署环境的必填项

开发环境以外，下列只在单机上成立的回落缺配即启动失败：

<!--#if (SpaFrontend)-->
- Data Protection 密钥必须落在 Redis 或共享持久目录（`DataProtection:KeysPath`），并随数据一同备份。`KeysPath` 只替代密钥的存储位置，不替代 Redis 承载的分布式缓存与锁。存储位置应只允许本服务访问：Redis 不对外发布端口，跨主机或使用托管 Redis 时设口令并开启 TLS；目录用文件系统权限限制到运行身份。显式指定存储位置后框架不再自动加密密钥，需要静态加密时按官方 `ProtectKeysWith*` 在 `AddMyProjectDataProtection` 里追加。
<!--#endif-->
<!--#if (LocalIdentity && IncludeMultiTenancy)-->
- 控制库用 Data Protection 加密租户连接串，API 与 `DbMigrator` 必须使用同一个密钥环与应用名。
<!--#endif-->
<!--#if (SpaFrontend)-->
- 未配置 `ConnectionStrings:Redis` 不阻止启动：单实例配 `KeysPath` 是合法部署。此时分布式缓存与分布式锁回落到进程内存，启动日志各有一条 Warning；多副本必须配置 Redis，并共享 Data Protection 密钥。
<!--#else-->
- 未配置 `ConnectionStrings:Redis` 不阻止单实例启动，分布式锁回落到进程内并输出 Warning；纯 API 不注册浏览器会话、Data Protection 或其 `KeysPath` 配置。多副本必须配置 Redis。
<!--#endif-->
- TLS 在网关或 ingress 终结时，所有形态（含 Standalone）都须配置 `ForwardedHeaders:KnownProxies` / `KnownNetworks` 还原原始协议与主机。
<!--#if (SpaFrontend)-->
  浏览器写请求的 Origin 校验同样依赖这些转发头；来源拒绝返回 Problem Details，Warning 日志给出收到的 Origin 与计算出的本源。
<!--#endif-->
<!--#if (OpenIddictServer)-->
- 令牌签名与加密证书必须显式提供（`OAuth:SigningCertificates`、`OAuth:EncryptionCertificates` 各至少一项，每项 `Path`、`Password`），与 HTTPS 证书分开；开发证书只用于本机开发。任何一项缺路径、无法加载或没有 RSA 私钥，启动失败并指出带下标的键名。
- TLS 在网关或 ingress 终结时不要打开 `OAuth:DisableHttpsRequirement`：OpenIddict 要求生产环境即使在反向代理后也不关闭传输安全检查，按上一条配置转发头即可。
<!--#endif-->
<!--#if (Email)-->
- 发信基线指向本机邮件捕获器（`localhost:1025`）。部署时覆盖 `Leistd:Email:Smtp` 的 `Host`、`Port`、`DefaultFromAddress`；`EnableSsl` 默认 `true`，仅 Development 为本机 Mailpit 关闭；`Username` 与 `Password` 成对提供或都不提供；compose 以 `SMTP_HOST`、`SMTP_PORT`、`SMTP_FROM_ADDRESS` 为必填，账号口令只填一项时启动失败。
- 在「系统设置 → 注册设置」开启邮箱验证前必须提供 `Leistd:Security:VerificationCodes:Key`（compose 的 `VERIFICATION_CODES_KEY`）：Base64、至少 32 字节，所有实例相同且跨重启不变，可用 `openssl rand -base64 32` 生成。缺失时界面开启被拒绝；非空无效密钥始终在启动时失败。若直接配置 `UserRegistration:EnableEmailVerification=true`，缺失密钥也会启动失败并报出键名。
<!--#endif-->
<!--#if (ExternalLogin)-->
- 外部登录提供商（`ExternalAuth:Github`、`ExternalAuth:Google`）默认关闭，`Enabled`（compose 的 `GITHUB_LOGIN_ENABLED`、`GOOGLE_LOGIN_ENABLED`）打开且 `ClientId`、`ClientSecret` 都提供时才生效；缺一项不阻止启动，只是不提供该入口并记警告日志。宿主管理员也可在「系统设置 → 外部登录」运行期覆盖开关与凭据，清除覆盖值即回到这里的部署配置。从只填凭据的旧版本升级时，要显式打开开关才会继续提供入口。提供商后台登记的回调地址是 `https://<站点对外地址>/api/v1/external-auth/{github,google}/signin`，设置页的状态部件按当前请求地址显示。
<!--#endif-->
<!--#if (LocalIdentity)-->
- 通行密钥的依赖方在 `Passkeys`：`RelyingPartyId` 是站点的可注册域名（不含协议与端口），`Origins` 列出浏览器页面的来源（协议 + 域名 + 端口，须落在 RP ID 之下，除环回地址外只收 https）；compose 的 `PASSKEY_RP_ID`、`PASSKEY_ORIGIN`。两项都不填时通行密钥不可用、照常启动；填了却不合法即启动失败并指出键名。登记过的通行密钥绑定 RP ID，部署后改它会让已登记的全部失效；多租户按子域名部署时 RP ID 取共同的父域。
<!--#endif-->

## 生产边界

- 密钥和生产凭据由环境变量或密钥管理系统提供，不写入仓库。
<!--#if (OpenIddictServer)-->
- 浏览器整页进入授权端点，发现文档、JWKS、令牌与 UserInfo 由 Resource 后端访问，不要求为这些服务端协议通信开放浏览器 CORS。本机 Angular 开发代理将 `/api/**` 转到 API。
  生产 API 的 `Cors:AllowedOrigins` 默认为空；仅供显式需要的其他 API 集成使用，不负责浏览器认证导航。
- 下游资源服务与各类客户端的登记（含 `OAuth:ApiResources`）见 [服务间调用](../standards/service-invocation.md#identity-与资源服务对接)。
- 各类撤销在下游资源服务上生效的时间不同。资源服务只验签，不回本服务查状态；Access Token 有效期由 `OAuth:AccessTokenLifetime` 决定（默认 10 分钟）。
  调短它让下面的窗口收敛更快，代价是依赖方续期更频繁；它必须是整秒且长于 1 分钟——资源服务的浏览器会话在令牌剩余 1 分钟时续期，
  不长于这个窗口时每个请求都要续期，启动时拒绝并指出该键。测试与 CI 要观察真实到期时，用环境变量把它缩短（如 `OAuth__AccessTokenLifetime=00:01:30`），
  并从实际签发的令牌（`exp - iat`）确认已生效，不要靠等满默认寿命。
  - **停用或删除账号**：同时撤销该用户已签发的令牌，本服务立即拒绝；资源服务要等 Access Token 过期，之后也刷新不到新令牌。
  - **停用或删除租户**：本服务每个请求都查注册表，立即拒绝，刷新令牌也换不到新令牌；资源服务同样要等 Access Token 过期。
  - **撤销会话（含"退出其他设备"、退出登录与空闲到期）**：作废本服务的登录会话，不撤销已签发的访问令牌。之后是否还能刷新取决于开放应用的"会话绑定"：绑定的客户端（授权时记下了会话）刷新随即被拒，下游访问在访问令牌到期时结束；未绑定的客户端不受影响，刷新令牌默认 14 天有效且滑动续期，持续刷新可以一直访问下去，调短 Access Token 有效期也改变不了这一点。

  停用或删除账号、租户之后无法再刷新，所以这两类的窗口可以靠调短 Access Token 有效期来缩短。资源服务改用令牌内省（`UseIntrospection()`）能让**已撤销的令牌**即时失效，所以对停用或删除账号有效；租户停用还要先在停用时撤销该租户的令牌才行。
<!--#endif-->
<!--#if (RemoteTokenAuth)-->
- Compose 只要求 `IDENTITY_ISSUER`；`IDENTITY_AUTHORITY`、`IDENTITY_BASE_ADDRESS` 留空时沿用它，仅内网地址不同时填写，API 与多租户 DbMigrator 一致。跨服务客户端、Audience 与机器身份配置见 [服务间调用](../standards/service-invocation.md#identity-与资源服务对接)。
- readiness 是启动门禁：首次成功解析发现文档、精确比对 issuer 并取得非空 JWKS 后锁存成功。
  冷实例在 Identity 不可达时 `/api/health/ready` 返回 503，`/api/health/live` 仍为 200；
  日志指出发现文档或签名密钥依赖（探针请求时健康检查与请求日志各记一条），Identity 恢复后自动重试并进入就绪。
  热实例保持就绪；只有 OpenIddict 验证器已缓存所需密钥时，才能在 Identity 停机期间继续本地验签。
  该探针不表示 Identity 持续可达，也不检查租户路由。
  采用 [ASP.NET Core 分离 readiness/liveness 的启动任务示例](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0#separate-readiness-and-liveness-probes)的门禁语义，
  不周期探测共享 Identity，避免其短暂故障使所有资源实例同时摘流。
  代价是就绪不保证新密钥获取、新令牌获取或租户回源成功，这些依赖失败在对应请求上暴露。
<!--#if (ResourceBrowserSession)-->
- 浏览器使用后端 OIDC 机密客户端与 Cookie 会话，服务端保存并刷新令牌；配置、回调、缓存/密钥与 CSRF 边界见 [浏览器认证](../standards/auth.md#浏览器认证)。
<!--#endif-->
- 本服务只验证身份服务签发的令牌，不回去查账号与租户状态。身份服务那边停用账号、撤销会话、停用或删除租户，已签发的 Access Token 在本服务仍然有效，直到过期（有效期由身份服务决定）。各类撤销的完整边界见身份服务的部署文档。改用令牌内省（OpenIddict 验证端的 `UseIntrospection()`）只能让身份服务**已撤销的令牌**即时失效，代价是每个请求多一次往返。
- 本服务自己禁用成员（用户管理里的禁用）不依赖身份服务，下一个请求即答 403，边界见 [资源服务的成员启停](../standards/auth.md#资源服务的成员启停)。
<!--#endif-->
<!--#if (IncludeNotifications && Email)-->
- 通知邮件要附可点开的站内链接时配置 `Leistd:Notifications:Email:PublicBaseUrl`（站点对外地址；哈希路由以 `/#` 结尾），不配则不附。
<!--#endif-->
<!--#if (IncludeOperationRecords)-->
- 操作记录默认**只增不减**。需要保留期时打开 `Leistd:OperationRecords:Retention:Enabled`（或由宿主管理员在系统设置的「审计」面板打开），
  到期记录会被搬进 `OperationRecordArchives` 表而不是删除；归档表不参与日常查询，但数据仍在库里，容量规划要把它算进去。
  配置里的开关与保留天数是基线，界面上的设置优先，归档任务每轮读取；执行时刻与批大小只在配置里。
  归档逐个进入宿主库与每个独立库，某个库失败只记错误、最迟在下一个调度时段重做（按截止时间扫描，积压会一并搬走）；
  多副本部署经分布式锁每轮只跑一份，要求配置 Redis，否则每个副本各跑一遍。
- **多个服务共用一个 Redis 时，每个服务的 `Leistd:Lock:Redis:KeyPrefix` 必须互不相同**（默认取应用名，由代码给出；追加环境时由部署侧覆盖，同一服务的 API 与所有迁移、恢复作业必须同值）。
  周期任务的锁键是固定的作业名（如 `operation-records.archive`），前缀相同的两个服务会互相抢同一把锁——
  抢不到的那个当轮直接跳过自己的库，而两边日志各自都正常。
- 「只增不减」由应用契约保证。需要数据库层也保证时，把 Runtime Secret 对 `OperationRecords` 表的权限收敛到 INSERT／SELECT；
  代价是归档要从原表删除，**收敛权限与启用保留期不能同时成立**，二选一并在部署记录里写明。
- 操作记录表增长到按时间范围查询变慢时，再按时间分区（数据库层 DDL，由 `DbMigrator` 的 DDL 身份执行），不预先做。
<!--#else-->
- 安全行为写入 `Leistd.OperationRecords` 结构化日志。成功事件在真实事务提交后输出，失败事件立即输出；必须保留 Information 级别并配置生产日志采集与保留策略。提交后、输出前进程退出仍可能丢失记录，日志模式不提供数据库模式的同事务持久化保证。
- 多个服务共用 Redis 时，各服务的 `Leistd:Lock:Redis:KeyPrefix` 必须互不相同，避免周期任务相互抢锁；默认取应用名，覆盖时同一服务的 API 与所有迁移、恢复作业必须同值。
<!--#endif-->
<!--#if (LocalIdentity)-->
- 原始 IP 保存在三处，各有各的期限，合规评估按这三处分别写明：
  - **会话表**：会话过期后不再使用，由登录时的本人清理或每天的 `auth.sessions.cleanup` 作业删除（逐库执行，含停用租户的库），作业正常运行时最长比有效期多留约一天；
<!--#if (IncludeOperationRecords)-->
  - **操作记录**（会话撤销等记录的目标名里带 IP）：跟随操作记录的保留期，见上；
<!--#else-->
  - **安全行为日志**（会话撤销等记录的目标名里带 IP）：由日志采集系统的保留与脱敏策略管理；
<!--#endif-->
  - **用户的最近登录 IP**：是当前状态字段而非历史，随用户记录保留、不单设保留期。删除用户是软删除，**不会**清掉它；合规要求删除时须物理删除用户记录或显式清空该字段。
<!--#endif-->
- 模板不内置限流。对外提供登录的服务应在网关或 ASP.NET Core 限流中间件上按来源 IP 限流：
  应用内的失败登录计数按账号聚合，同一 IP 轮换账号的撞库不会触发它。
