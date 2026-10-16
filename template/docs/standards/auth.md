# 认证与授权

接口的认证方式、授权与权限侧别、浏览器会话。响应与错误契约见 [API 规范](api.md)，密钥、令牌与个人信息不进日志和响应的边界见 [通用约定](coding-common.md#1-语言与敏感信息)。

## 认证方式与授权要求

### 用户认证

<!--#if (OpenIddictServer)-->
支持本地 Cookie Session 与 OpenIddict Bearer 校验。
<!--#elseif (ResourceBrowserSession)-->
支持远端 Bearer 校验与服务端 Cookie Session；有 Authorization 头时只选 Bearer，失败不回退 Cookie。
<!--#elseif (RemoteTokenAuth)-->
仅使用 OpenIddict 校验远端 Bearer，不提供 Cookie Session。
<!--#else-->
使用本地 Cookie Session，不提供 OIDC 授权服务器或 Bearer 验证入口。
<!--#endif-->
<!--#if (LocalIdentity)-->
- Cookie 会话在服务端登记（`UserSessions`，即个人设置里的「登录设备」）：每个请求都确认会话仍然有效，所以撤销——退出某台设备、退出其他所有设备、修改密码、管理员重置密码、退出登录——对已发出的 Cookie 立即生效。确认结果缓存 1 分钟；多实例部署未配 Redis 时，其他实例上的撤销至多滞后这么久。
<!--#if (OpenIddictServer)-->
- 自签发的访问令牌不在会话撤销的范围内，按有效期自然失效。
- 访问令牌是只签名、不加密的 JWT：资源服务经 discovery/JWKS 本地验签，不需要分发解密密钥。令牌中的 claim 对每个持有者可读（包括获准的 public/native 客户端），不要放入业务机密；授权码与 refresh token 不受此影响，仍然加密。要改为加密令牌，须删除 `DisableAccessTokenEncryption()` 并为每个资源服务配置解密凭据；要改用 introspection，须启用签发端点，并让资源服务以客户端身份调用。两者都要同时改动签发端与资源端。
<!--#endif-->
<!--#endif-->
<!--#if (LocalIdentity && (IncludeNotifications || IncludeRealTime))-->
- 实时连接（SignalR）不在撤销的即时范围内：握手时认证的主体沿用到连接结束，Hub 方法调用只按策略复评、不重新认证。会话被撤销或账号被停用后，已建立的连接仍收到服务端推送（通知、业务事件），直到断线重连（重连时认证失败）或页面关闭；`CloseOnAuthenticationExpiration` 只在认证票据到期时断开，不感知撤销。
<!--#endif-->
- 所有需要用户身份的接口必须校验认证状态。
- 认证失败统一返回 401，不暴露内部认证细节。
- 不指定认证方案的策略（只写权限名的端点、组件端点）匿名被拒时由宿主默认挑战方案回应；调整认证方案时须保证它对无凭据请求仍回 401。
<!--#if (OpenIddictServer || ResourceBrowserSession)-->
- 默认挑战与认证同一分流：带 Authorization 头由 Bearer 验证回应（401 带 `WWW-Authenticate: Bearer`），否则由会话 Cookie 回应，401/403 不带 `WWW-Authenticate`。这是沿用 Cookie 会话 API 不重定向的约定，不满足 RFC 9110 对 401 携带 challenge 的要求：客户端不能靠它发现 Bearer。需要标准 Bearer challenge 的端点在策略里显式指定 Bearer 方案。
<!--#endif-->

### 授权

- 写操作必须校验资源归属或角色权限。
- 批量操作必须逐项校验权限或明确全局权限。
- 管理接口必须与普通用户接口隔离权限。
<!--#if (RemoteTokenAuth)-->

### 资源服务的成员启停

成员的启用与禁用归本服务所有，签发方的令牌不知道它，认证阶段拦不住；由授权阶段的成员要求（`LocalMemberAccessRequirement`）把守：

- 默认策略与 `App.CurrentUser`（读设置、读自己的权限、通知中心等组件自用端点）都要求本服务有这名成员的行且未被禁用，禁用在下一个请求生效，不等令牌过期。被拒答 403 ProblemDetails：`code` 为 `User:LocalAccessDisabled`（已禁用）或 `User:LocalMemberMissing`（没有成员行，通常是首次访问时投影失败，日志里有对应 Warning）。认证失败仍是 401；带权限点的端点同时由 RBAC 拒绝。
- 只约束自然人；机器主体（`client:<client_id>`）照旧由自然人断言拒绝，不查成员行。
- 状态取自本请求投影时读到的那一行（`ResourceUserProvisioningMiddleware`），不另查库；投影失败或 Hub 方法调用（每次调用是新的作用域）时按主键只读一次。读库失败答 500，不当作禁用；不跨请求缓存。
<!--#if (IncludeRealTime)-->
- 实时连接：Hub 握手与每次 Hub 方法调用都按默认策略判定，禁用后下一次方法调用（如 `Subscribe`）中止连接，重连被拒。在此之前已建立的连接仍收到服务端推送，直到断线重连或页面关闭；`CloseOnAuthenticationExpiration` 只在令牌到期时断开，不感知禁用。
<!--#elseif (IncludeNotifications)-->
- 实时连接：通知 Hub 的握手按默认策略判定，禁用后重连被拒；它没有客户端可调用的方法，连接中途不复评，已建立的连接仍收到通知推送，直到断线重连或页面关闭。`CloseOnAuthenticationExpiration` 只在令牌到期时断开，不感知禁用。
<!--#endif-->
<!--#endif-->

## 权限侧别与租户维度

新增权限时先回答：**这条权限背后的数据带不带 `TenantId`？**

| 数据形态 | 侧别 |
| --- | --- |
| 实体实现 `IMultiTenant`，受全局租户过滤器分区（用户、角色、设置、权限目录） | `Both` |
| 宿主全局，无 `TenantId`，过滤器不生效（租户注册表、OpenIddict 开放应用） | `Host` |
| 只在租户内成立 | `Tenant` |

- 侧别是 `AddPermission` 的必填参数，必须按上表显式选择。`Both` 表示租户管理员也拿得到：把宿主全局资源标成 `Both` 就是跨租户越权——租户初始化会把它授予每个租户的管理员，而宿主全局的表不受租户过滤器约束。这只在建了租户之后才暴露，单租户开发与测试发现不了。
- 侧别为 `Host` 时，权限检查本身就是边界：检查按当前侧别判定且与是否授予无关，不在应用服务里再拦一次。
- 只有权限必须保持 `Both`、而它管辖的内容里有一部分是宿主专属时，才在服务内补校验（如 `App.Settings` 中只有宿主能写的进程级设置，由设置用例拒绝，错误码 `Setting:HostOnly`）。判断口径：先问侧别能不能表达；能，就只写侧别。
- 收紧侧别不会撤销已授出的记录：管理员权限只在授权版本为 0 时播种，既不补齐也不撤销。把权限从 `Both` 改成 `Host` 时，必须同时为既有部署提供撤销 SQL。

实体的租户维度用同一条判据，问法换成：**这个实体会不会被独立查询？**

| 情形 | 要求 |
| --- | --- |
| 会被独立查询（有自己的仓储 / `DbSet`，或被 `Where` 直接命中） | 必须实现 `IMultiTenant` |
| 只经聚合根访问，没有任何独立查询入口 | 不需要；但必须真的没有入口 |

"当前调用路径恰好都先经过了受过滤的表"不成立：新增一条直查路径就失去隔离，且多租户过滤检查只覆盖 `IMultiTenant` 实体。宿主全局数据（控制面表、OpenIddict 表）不映射进租户上下文，能力边界由权限侧别把守。

<!--#if (SpaFrontend)-->
## 浏览器认证

浏览器与所属 API 必须同源；开发期 Angular 代理转发 `/api/**`。页面由前端渲染，认证协议由后端处理。浏览器只持有 HttpOnly 会话引用，OAuth access/refresh/id token 留在服务端 `ITicketStore`，不写 URL、前端存储、JSON 响应或 SignalR 参数。唯一的例外是退出：依赖方以自动提交的表单把 id_token 作为 `id_token_hint` 发往 Identity，它只出现在那张表单的正文里，不进地址栏、历史记录与 Referer。SignalR 浏览器连接使用同源 Cookie；机器令牌仅走 Authorization 头，Hub 仅接受请求头中的 Bearer。

### 会话与部署

`DistributedTicketStore` 使用已有分布式缓存与 Data Protection 保护完整票据。多实例必须共享缓存与 Data Protection 密钥；没有 Redis 的本地开发使用内存缓存，重启后需重新登录。缓存票据删除后，复制的旧 Cookie 失效。显式登录更换引用版本，滑动续期保留版本；旧请求不能把已撤销票据重新写回，也不能删除再次登录的新版本。

会话 Cookie 在部署环境名为 `__Host-Http-CompanyName.ProjectName.Auth`，Secure、HttpOnly、`Path=/`、不带 Domain；浏览器只接受经 HTTPS 写入的这个名字，因此对外源必须是 HTTPS，站点不能挂在子路径下。Development 环境为了支持 HTTP 同源调试，名字不带前缀，Secure 跟随请求协议。

`SessionCookie:ExpireDays` 是会话时长（天，至少 1，启动期校验），会话 Cookie 的滑动过期取它。
<!--#if (LocalIdentity)-->
服务端会话（登录设备）的空闲时限从同一份选项派生，两者不会不一致。
<!--#endif-->

`SessionCookie:SameSite` 只控制应用会话 Cookie；默认 Lax，放宽到 None 也不会让跨源浏览器认证导航成立。OAuth correlation 与 OIDC nonce Cookie 保持官方 SameSite=None、Secure=Always，HTTPS 回调不可省略。开发回调在 `/api/**` 下，由开发代理转发。

浏览器写 API 请求与实时 Hub（`/hubs/**`，含 WebSocket 握手）检查 Origin，接受本源及显式 `Cors:AllowedOrigins`。使用 `Leistd.Security.AspNetCore` 的显式来源防护，宿主选择 `/api` 写路径与 `/hubs` 全方法路径。Authorization 或成功 Bearer 均不豁免；跨源浏览器 Bearer 写请求也须来自允许携带凭据的 CORS 源，否则403。CORS 不约束 WebSocket，Hub 独立检查来源。没有 Origin 时看 `Sec-Fetch-Site`：单值 `same-origin`、`none` 放行，其余值或多值拒绝；两个头都没有的非浏览器调用保持支持。API 写请求不使用 ASP.NET Core antiforgery，也不把 Angular 默认 XSRF 拦截器当成完整防护；唯一用到官方 antiforgery 的是 Identity 的退出确认表单（见下文）。浏览器认证不支持独立跨源 API 地址；进程分离须由部署代理将页面、认证导航、协议回调与 API 暴露在同一个外部源。`environment.api.gateway` 保持空值，以相对路径访问同源 API；其他服务通过同源微服务路由前缀访问。整页认证导航不经过 HTTP 拦截器；不要将任意源加入允许列表。OIDC form_post 回调由官方处理器消费，依靠 state、correlation 与 nonce 校验。

<!--#if (LocalIdentity)-->
### 本地账号

账号密码与第二步验证走 `/api/v1/auth/session-login` 等现有会话接口。`GET /api/v1/auth/me` 读取已验证用户，退出撤销服务端会话。第二步完成前不签发最终会话，第二步凭据由 JSON 与前端导航状态传递。

#### 敏感变更的近期认证

改变“谁能登进这个账号”的操作假设会话可能被盗用：会话的认证时间（`auth_time`）要在 15 分钟内，否则当场出示操作者自己的当前口令（`Auth:ReauthenticationRequired`），口令错误与登录共用失败计数和锁定；没有本地口令的账号重新登录（`Auth:SignInAgainRequired`）。模拟登录的会话不算刚认证（那是发起人的认证时间）。覆盖的入口：

- 本人换邮箱（`PUT /api/v1/auth/me` 的 `currentPassword`，只在邮箱变化时要求；用户名、显示名、电话不受此限）；
<!--#if (ExternalLogin)-->
- 本人绑定外部账号（`link/complete` 的 `password`）；
<!--#endif-->
- 绑定、换绑两步验证（见后端 README）；
- 管理员改他人邮箱或把邮箱标为已验证（`PUT /api/v1/users/{id}` 的 `password`）、重置他人口令（`reset-password` 的 `currentPassword`）、重置他人两步验证。经用户管理改自己同样要求。

受限会话（组织要求两步验证而本人未设置）是端点限制，与认证时间无关：它调不到资料与绑定接口，服务端以 `Auth:TwoFactorSetupRequired` 拒绝；它自己的首次设置照常按自己的认证时间判定。

#### 凭据并发

口令、安全版本、邮箱与两步验证凭据的改写，以及凭它们登记会话的路径（口令登录、登录第二步、已有账号的外部登录），都在同一把用户级凭据锁（`UserCredentialLock`）内读取最新的用户行、决定并提交。撤销会话只撤得掉当时已有的会话；串行之后，改口令、重置口令、启用两步验证、解绑之后不会再冒出凭旧状态登记的会话，邮箱确认也只确认得了此刻账号上的地址。受限会话完成两步验证设置后在原服务端会话上换发 Cookie，不登记新会话。
<!--#endif-->
<!--#if (OpenIddictServer)-->
### 依赖方的登录与退出

授权（`/connect/authorize`）与退出（`/connect/logout`）启用了 OpenIddict 请求缓存：首个请求校验后存为 request token（控制库的令牌表），再重定向回同一端点、只带 `client_id` 与 `request_uri`，此后才进入控制器。依赖方因此可以跨站 POST 发起，重入是顶层 GET，Lax 会话 Cookie 随之送达；附加在重入地址上的参数不能改写缓存的请求。request token 一次性：授权完成或退出完成时即被标记为已兑现，同一 `request_uri` 再次进入返回 400；过期记录由已有的令牌清理任务删除。

请求要求重新认证（`prompt=login`，或 `max_age` 已超过）时，跳转登录页的回跳地址带一份受保护的证明：绑定这一个 `request_uri`，只有证明签发之后才开始的会话（即一次新的登录）能兑现它，签发前就已存在的会话都不行。证明只抵消 `prompt=login` 与 `max_age=0` 的"每次都要认证"；正数 `max_age` 在回跳时仍按当前认证年龄判断。登录、第二步验证与外部登录都回到同一地址；没有新认证就回跳会再次要求登录，证明挪给别的请求无效。

退出时，`id_token_hint` 中的会话标识（`sid`）与当前 Identity 会话一致才直接退出并回到登记的退出回调；没有 hint、hint 属于别的会话时转到确认页 `/auth/logout-confirm`（RP-Initiated Logout 1.0 §2）。确认页的地址只有 `request_uri` 与受保护的确认凭据，后者绑定该退出请求与当前会话的用户、租户、会话标识，有效 10 分钟。页面经 `GET /api/v1/auth/logout-confirmation` 核对凭据并取得官方 antiforgery 令牌，用户确认时整页 POST 回 `/connect/logout`；防伪令牌或凭据不符时再次显示确认页，会话保持。重新登录后，旧确认页不能结束新会话；取消不退出，发起退出的应用已清掉的本地会话不会因此恢复。当前没有会话时直接回到退出回调。

开放应用的"会话绑定"（`sessionBound`，创建与更新都必须显式给值，缺失或 null 返回 400）决定授权是否跟随签发时的 Identity 会话：

- 开启时，授权码、刷新令牌与 id_token 带上会话标识（访问令牌不带）；换取或刷新令牌时会话已退出、被撤销或空闲到期即返回 `invalid_grant`。判定不记活跃，后台续期不会延长 Identity 会话。服务端会话类客户端（BFF，如 Resource 的浏览器登录）应开启：用户在 Identity 退出后，它在访问令牌到期时随之收敛。
- 关闭时授权与会话无关，适合需要持续离线续期的客户端（桌面端、原生应用）。管理界面新建 Web 应用默认开启，桌面端与服务模板默认关闭。
- 授权按签发时的事实处理：开启前签发的刷新令牌在开启后被拒，客户端须重新授权；签发时已绑定的授权在关闭后仍受约束。早于该设置的登记读作未设置（按关闭处理），编辑时须明确选择。
- 修改登记只在当前实例立即生效，多实例须滚动重启 Identity（见部署文档）。

依赖方的登记见 [服务间调用](service-invocation.md#identity-与资源服务对接)。退出确认、重新认证证明与 Cookie 都依赖 Data Protection，多实例 Identity 须共享的资源见 [部署说明](../deploy/README.md#配置与机密的分层)。
<!--#endif-->
<!--#if (ExternalLogin)-->
### 外部账号

Google 使用微软官方 AddGoogle（UserInfo v3）；GitHub 使用 aspnet-contrib 的 AddGitHub（`AspNet.Security.OAuth.GitHub`），显式启用 S256 PKCE。处理器自带的邮箱补取已关闭，因为它只给地址、不给 verified，且失败即中断登录；主邮箱及其 verified 由 `OnCreatingTicket` 查询 `/user/emails` 取得，查询失败时只是不按邮箱关联。定制 github scheme 时用 `Configure<GitHubAuthenticationOptions>`，不是 `OAuthOptions`。添加提供商时在组合根注册官方远程处理器：scheme 名为 `AuthenticationSchemeNames.ExternalProviderPrefix + provider`（provider 使用小写），`SignInScheme` 指向 `ExternalCookie`，`CallbackPath` 在 `/api/**` 下，并在 `OnCreatingTicket` 把规范化 `ExternalUserInfo` 序列化到 `context.Properties.Items[ExternalAuthenticationExtensions.UserInfoKey]`。目录从该专用前缀的远程 scheme 得出，Cookie/Bearer/策略 scheme 均不开放。注册时明确协议失败响应、所需 PKCE 与资料验证；账号关联、锁定、用户名生成与第二步验证仍由领域/应用层决定。

**启用与配置**：每个提供商有启用开关和 `ClientId`、`ClientSecret`（配置节 `ExternalAuth:{Github|Google}`），部署配置给基线（开关默认关闭），宿主管理员可在「系统设置 → 外部登录」运行期覆盖，密钥是机密设置、只写不回显。开关打开且两项凭据齐全时提供商才**可用**；凭据不全不拒绝写入、也不阻止启动，只是不提供该入口并记警告日志（列出缺失的键）。设置页的状态部件显示开关、凭据是否已配置（含部署配置提供的密钥）、本实例是否生效与回调地址（`GET /api/v1/settings/external-login/status`，仅宿主）。

启动时始终调用 `AddGoogle`、`AddGitHub` 装配处理器与官方选项链；运行期的 scheme 目录由 `ExternalSchemeSynchronizer` 按可用性增删，并作废命名选项缓存，凭据每次重建选项时读 `IOptionsMonitor<ExternalAuthOptions>`；不能把不可用的 scheme 留在目录里，认证中间件每个请求都会初始化它并触发官方的必填校验。多实例经宿主级设置的周期刷新各自收敛。停用只关闭新的入口：再发起 challenge 返回 503（`ExternalAuth:ProviderNotConfigured`），已跳到提供商、停用后才回调的请求落到 `/api/**` 兜底返回 404，已签发的外部票据在五分钟有效期内仍可完成。停用前注意：只靠该提供商登录、又没有密码的用户将无法登录。模板没有自助找回密码，这类用户只能靠别的仍可用的外部绑定，或由管理员在用户管理里重置口令。

新增提供商除了上面的 scheme 登记，还要补设置定义、`HostSettingBindings` 绑定行、`ExternalAuthOptions` 节、同步器与状态端点里的提供商、前端入口与 Mock。

0. 登录页匿名读取 `GET /api/v1/external-auth/providers`，只为可用的提供商显示入口；列表为空时登录页、注册页不显示入口与分隔文案，账户与安全不显示外部账号区块；读取失败时单独提示并可重试（5xx 附追踪 ID），不当作"未配置"。登录页只内置 GitHub、Google 两个入口，新增提供商时要同时补前端入口和 `getExternalLoginUrl` 的提供商类型。
1. 浏览器导航至 `GET /api/v1/external-auth/{provider}/challenge`，可带站内 `returnUrl`（外站地址返回 400）。绑定使用 `GET /api/v1/external-auth/{provider}/link/challenge`，要求通过自然人策略的非受限会话。
2. 提供商回调至 `/api/v1/external-auth/{provider}/signin`，官方处理器完成 code/state/correlation/PKCE 与 UserInfo，签发五分钟外部票据引用，然后重定向前端 `/auth/external-callback/{provider}?intent=...`。用户在提供商处取消或协议校验失败（state、correlation 等）时，不签发外部票据，重定向前端 `/auth/external-callback/{provider}?intent=...&error=cancelled|failed`：登录意图显示原因并提供返回登录入口（会话仍有效时直接回到应用，例如后退键重放旧回调），绑定意图回到安全设置页并提示。业务提示中的提供商名使用官方 scheme 的显示名（`ExternalUserInfo.ProviderDisplayName`）。
3. 前端 `POST /api/v1/external-auth/{provider}/complete` 或受保护的 `POST /api/v1/external-auth/{provider}/link/complete`。登录的请求体为空对象；绑定在会话不够新时带上本人当前口令（`{ "password": ... }`），见“敏感变更的近期认证”。后端匹配受保护的提供商、意图、绑定发起者与租户，先一次消费外部票据，再执行账号政策；登录返回最终会话结果或第二步凭据及受保护的 `returnUrl`，前端在登录或第二步成功后接续该地址；绑定成功为空响应（HTTP 200），结果以绑定列表为准。登录与第二步的会话 Cookie 统一由 `backend/src/CompanyName.ProjectName.Api/Auth/Sessions/SessionCookieIssuer.cs` 签发：先结束当前会话再签发，要求第二步时只返回凭据、不签发最终会话。

完成端点失败也不能重用票据，须重新 challenge；唯一的例外是绑定要求出示口令（`Auth:ReauthenticationRequired`、`Security:CurrentPasswordIncorrect`）：近期认证只在消费票据之前检查一次，带上口令重试同一个请求即可；票据消费之后不再因认证时间拒绝。查询参数不能改变保护过的登录/绑定意图。提供商后台需分别登记上述完整 HTTPS signin 地址。Google v3 使用 `sub/email_verified`。邮箱接口失败或未验证邮箱不允许按邮箱关联账号。

按邮箱自动关联是模板的产品政策：提供商确认邮箱已验证、且本地账号的邮箱也已确认时，首次用该外部账号登录直接关联到这个本地账号。它信任的是“受信提供商 + 双方已验证的同一邮箱”，不是通用的账号同一性证明；因此本地邮箱的变更要求近期认证，确认要求发到该地址的验证码，两者与自动关联的判定和写入共用同一把凭据锁（见“本地账号”下的“敏感变更的近期认证”“凭据并发”）。不接受这一政策的项目，改为邮箱命中已有账号时返回 `ExternalAuth:AccountExistsSignInToLink`，由本人登录后在账号设置里绑定。
<!--#endif-->
<!--#if (RemoteTokenAuth)-->
### Resource 依赖方

后端是 OIDC 机密客户端，使用 code、PKCE、SaveTokens 与服务端票据。配置 `Authentication:Issuer`、`Audience`、`ClientId`、`ClientSecret`，缺键启动失败；`Scope` 可省略，默认与 Audience 同名。密钥只放后端机密配置。

本服务须在 Identity 登记为开启会话绑定的浏览器依赖方，登记项见 [服务间调用](service-invocation.md#identity-与资源服务对接)；会话绑定使 Identity 退出后本服务的会话在访问令牌到期时收敛，而不是靠刷新令牌继续存活。

前端导航至 `GET /api/v1/auth/login?returnUrl=...`，仅接受站内 returnUrl；回调后 `GET /api/v1/auth/me` 还原用户、角色与租户。授权与退出请求都以官方 FormPost（`AuthenticationMethod = FormPost`）发往 Identity：响应是一张自动提交的表单，参数不进地址栏。`POST /api/v1/auth/logout` 先删除本服务端票据（旧 Cookie 立即失效），再以表单携带 `id_token_hint` 发起退出，Identity 据其中的会话标识免确认退出。表单依赖一段内联脚本自动提交（禁用脚本时显示提交按钮）；宿主若加内容安全策略，要放行这段脚本或接受手动提交。

有 Authorization 头的请求只选官方 Bearer 验证，失败不回退到 Cookie；无头时选 Cookie。角色与租户取已验证访问令牌的声明，不能假定 ID token 具有资源声明：会话主体在登录（登录回调的 `OnTokenValidated`）与续期时都由同一处按访问令牌的验证结果构建：保留验证结果中的声明（签发方放进访问令牌的 `acr`、`amr`、`auth_time` 也在其中），只出现在 id_token 里的声明不进会话。这个 OIDC 处理器不做声明映射，默认 `ClaimActions` 已清空；不要往里加映射，需要额外声明时由签发方放进访问令牌。需要重新认证时，用官方 `OpenIdConnectChallengeProperties` 的 `MaxAge` 与 `Prompt` 发起挑战；它们只要求 Identity 重新验证身份，不是多因素或升级认证——那需要 Identity 把 `amr`/`acr` 签发进访问令牌，模板没有内置。`OnValidatePrincipal` 在过期前一分钟于服务端刷新，同一会话由分布式锁串行化，采用最新 refresh token；失败注销会话。访问令牌保存在服务器，框架提供的 `IUserAccessTokenAccessor` 为下游 Token Exchange 提供经过验证的请求令牌。

访问令牌按只签名的 JWT 本地验签（issuer、audience、签名、有效期），本服务不持有解密凭据；签发方若改为加密令牌或 introspection，这里要同步配置。

签发方轮换签名证书后，遇到不认识的 kid 时先向配置的签发方刷新一次公钥再验（`Leistd.Security.OpenIddict.Validation` 的 `AddSigningKeyRefresh()`，覆盖 Bearer、登录回调与服务端续期；id_token 由 OIDC 处理器自身刷新重试）。只处理可读的 JWS，公钥只来自配置的发现文档，验签规则不放宽。同一时刻的刷新合并成一次抓取，抓取超时 10 秒，请求刷新每分钟至多转交一次（签发方不可用时，伪造 kid 的请求不会逐个触发抓取）；抓取失败时沿用已有公钥；抓取成功则本次只用返回的公钥集，签发方撤掉的公钥不再参与验签。逐请求结果只记 Debug，真实的刷新请求每次记一条 Information。这依赖进程级开关 `Switch.Microsoft.IdentityModel.UpdateConfigAsBlocking`（Api 与集成测试项目以 `RuntimeHostConfigurationOption` 设置）：它也让定期自动刷新改为由到点的请求等待完成。签发方刚刷新过（IdentityModel 的 5 分钟间隔、本服务的 1 分钟限频）或不可达时，新 kid 的请求仍会失败，所以轮换仍按签发方部署文档的顺序先发布、后切换。

退出 Resource 会话不会撤销签发方所有既有令牌；注销 Identity Cookie 与撤销 OAuth 授权/令牌也是不同边界。在 Identity 停用账号或租户后，本地验签窗口由访问令牌有效期（Identity 的 `OAuth:AccessTokenLifetime`，默认 10 分钟）决定，后续刷新失败收敛会话；本服务自己禁用成员则在下一个请求生效，见[资源服务的成员启停](#资源服务的成员启停)。
<!--#endif-->
<!--#endif-->
