# 模板浏览器认证维护规则

本页约束生成前端及浏览器会话的形态：Identity、Standalone，以及 `IncludeFrontend=true` 的 Resource。纯 Resource API 不生成前端、浏览器 OIDC 或 Cookie 会话，使用 Bearer 验证；参数边界见[模板开发规范](./development-guide.md#32-有效能力集中派生)。

模板组合根按有效能力直接组合 ASP.NET Core 官方 Cookie、AddOpenIdConnect、AddGoogle 与 aspnet-contrib 的 AddGitHub；外部处理器还要求开启外部登录。框架不新增 Web/BFF 抽象，不引入反向代理。OpenIddict.Client 继续承担框架的机器认证。带前端的形态保留 Angular 页面、开发代理与同源 Cookie 会话认证。

令牌载体、服务端票据与撤销行为的运行契约见生成项目 [认证与授权](../../template/docs/standards/auth.md#浏览器认证)。维护时：SaveTokens 与 ITicketStore 必须一起配置；显式再登录与冒用切换都签发新会话、更换引用版本。组织要求两步验证时，受限会话完成设置不换发：受限是每个请求按策略与账号当前状态动态判定的，完成设置后从下一个请求起解除，sid、auth_time 与 Cookie 都不变。

会话 Cookie 的命名与属性见同一契约；LocalIdentity 与带浏览器会话的 Resource 两处注册要同时修改，测试与 e2e 的期望名各自集中维护，不能从实现读取。

应用 SameSite 与协议 correlation/nonce Cookie 分开。官方默认协议 Cookie 的 None/Secure Always 保持不变，OIDC code 显式指定，GitHub 的 UsePkce 显式 true。GitHub 处理器的邮箱补取保持关闭（UserEmailsEndpoint 为空）：它不给 verified 且失败即中断登录，verified 由模板查询并在失败时降级。外部 OAuth 的 OpenIddict web providers 不引入：Standalone 的 Api 宿主不含 OpenIddict，只在 Identity 采用会形成两套外部登录实现。回调须在 /api/** 下以覆盖开发代理。Items 提供完整性，业务 complete 的一次消费由服务端外部票据、锁与先删除保证。

带浏览器会话的 Resource 的 Smart policy scheme 以 Authorization 头是否存在确定 Bearer/Cookie，DefaultPolicy 与当前用户策略都使用 Smart；验证失败不尝试其他方案。模板的 IUserAccessTokenAccessor 读经过验证的 Bearer 或 Cookie 服务端票据，不改框架现有 Bearer-only 实现。刷新使用公开 OnValidatePrincipal、官方访问令牌验证服务与已有分布式锁。

OAuth:ApiResources 是对象目录，资源名、scope、OwnerClientId 单一来源，scope/owner 默认资源名。Scope 与 audience 可不同、同一客户端可拥有多个资源、同一资源只有一个归属；启动期拒绝重复资源/Scope。用户访问令牌的 Token Exchange 只允许单跳，目标为一个资源与 scope，发起方必须拥有来源资源，目标 audience/scope 与登记权限对应。OpenIddict 7.7 ValidateAuthorizedParty 直接看主体 aud，ValidAudiences 不改其逻辑；模板只对 access-token exchange 替换这一处理器，其余分支委托官方实现，不能全局关闭验证。

协议测试仅替换通信依赖，运行生产注册的官方处理器与失败事件；业务测试可直接输入规范化 ExternalUserInfo。提供商以 `AuthenticationSchemeNames.ExternalProviderPrefix` 登记官方远程 scheme，目录过滤此专用前缀与远程处理器类型；绑定入口执行自然人策略与受限会话规则。站内回跳地址由 challenge 校验后随受保护票据保存，并贯穿外部登录和第二步验证。

浏览器认证仅支持页面与所属 API 同源，服务可分进程、由部署代理统一外部源。TLS 终结后的来源检查依赖受信任转发头；来源拒绝输出诊断日志并由 API 状态码管道补写问题详情。无 Origin 的写请求按 Sec-Fetch-Site 判定：cross-site、same-site 拒绝，Fetch Metadata 不能推翻已失败的 Origin 检查。升级到内置跨源防护的 .NET 版本后，要先核对它对 JSON 端点的判定范围，并注入原缺陷做变异验证，之后才能删除自写判定。访问令牌只签名、不加密，这是宿主显式选择，契约写在生成项目 auth.md 的认证小节。有效票据远离刷新窗口时不获取刷新锁；临近过期才加锁、重读并复查。

Identity 的授权与退出端点启用 OpenIddict 请求缓存并保留 passthrough；不另建 ASP.NET Core 层的平行参数缓存。缓存后协议参数只在 request token 里，控制器不能再靠改写 URL 兑现 `prompt=login`/`max_age`：重新认证用绑定 `request_uri` 的受保护证明：兑现要求当前会话在证明签发之后才开始（每次登录都新建会话，会话开始时间是完整精度；auth_time 只到秒，同一秒里已有的会话靠它分辨不出）。证明只抵消 `prompt=login` 与 `max_age=0`，正数 `max_age` 始终按当前认证年龄判断。一次性由 request token 的兑现保证。Resource 使用官方 `AuthenticationMethod = FormPost` 并保留 hint，本地 Cookie 单独先退出且不带回跳地址（Cookie 处理器会写 302 盖过表单）。退出时 hint 的 `sid` 与当前会话一致才免确认；确认页只拿 `request_uri` 与受保护凭据，凭据绑定请求与 sub/tenant/sid 并限时，确认是本源整页 POST，显式调用官方 `IAntiforgery` 校验（端点本身对初始跨源协议请求保持 `IgnoreAntiforgeryToken`）；不得用 `confirm=true` 一类查询参数代替确认。

开放应用的会话绑定是模板自有的 Settings 键 `leistd:session_bound`，读写只经 `OpenApplicationSettings`；不要把它等同于 `backchannel_logout_session_required`。API 区分缺值/null（400）与显式 false；签发时把经验证的 Cookie `sid` 写入 code/RT/id_token，换取与刷新只判定 Identity 会话（不 Touch、不走 `UserSessionValidator` 的缓存），失败一律 `invalid_grant`。是否受约束按签发时 grant 里有无 `sid` 判定，当前登记只用于拒绝缺 `sid` 的旧 grant；token exchange 与 client_credentials 不进入会话检查。后通道退出与 OpenIddict 8 的会话能力到位前不另建事件通道。

仓库验收日志和过程放 .tmp，不分发进 template。

## 测试分工

仓库不另建浏览器闭环。真实界面只验两条主路径，其余行为按层放在能稳定复现的测试里：

| 行为 | 由谁证明 |
| --- | --- |
| 组织要求两步验证时在页面上完成设置并登录 | 生成项目 E2E `two-factor-required.e2e.ts` |
| 会话不够新时确认身份后保存 | E2E `reauthentication.e2e.ts`；15 分钟窗口与验证码重放：`SensitiveChangeReauthenticationTests`、`TwoFactorReauthenticationTests`；取消不保存、方式切换：`reauthenticate-dialog.spec.ts`、`profile-panel.spec.ts` |
| 引用 Cookie 不带令牌、HttpOnly/Secure/`__Host-Http-` 前缀，票据删除即失效 | `ResourceBrowserSessionTests.Official_code_flow_keeps_tokens_on_the_server_and_rejects_a_deleted_ticket`、`UserSessionTests.Deployed_session_cookie_carries_the_host_http_prefix_and_its_attributes` |
| 到期续期轮换令牌；续期被拒时删票据、清 Cookie | `ResourceBrowserSessionTests.Concurrent_requests_refresh_once_and_use_the_rotated_refresh_token`、`Refresh_lock_failure_rejects_the_session_instead_of_returning_500`；跨进程的真实到期见 OIDC 端到端 `-IncludeExpiryWait` |
| 退出：hint 指向当前会话时免确认，没有 hint 走本源确认页，复制的 Cookie 失效 | `OidcLogoutTests`、`UserSessionTests.Copied_cookie_is_rejected_after_sign_out`；跨进程链路见 OIDC 端到端 S4 |
| 受限会话的授权请求转去设置页，完成后接续原缓存请求 | `TwoFactorRequirementTests.A_restricted_session_gets_no_authorization_code_until_setup_is_done`、前端 `two-factor-required.spec.ts` |
| 外部登录：PKCE、令牌不进浏览器、接续原授权请求、第二步与恢复码、纯外部账号重新登录 | `ExternalAuthenticationTests`（`The_official_handler_sends_pkce_and_keeps_tokens_out_of_browser_cookies`、`An_authorization_request_continues_through_external_login_and_optional_two_factor`）、`ExternalLoginLinkTests`、`PasswordlessAccountTests`、`TwoFactorTests.Recovery_code_replaces_a_code_only_once` |
| 角色列表实时刷新、断线重连后补查 | 前端 `roles.spec.ts`、`signalr-service.spec.ts`；提交后推送：`RealTimeSubscriptionTests` |

## 外部登录的手工验证

Google、GitHub 的真实登录有机器人检测，不做自动化；协议细节由上表的集成测试承担。改动外部登录（处理器配置、回调地址、资料映射、绑定规则）后，在可访问的部署上手工走一遍：

1. 提供商控制台登记的回调与 `https://<站点>/api/v1/external-auth/<provider>/signin` 逐字一致；部署配置打开 `ExternalAuth:Google:Enabled`（或 `ExternalAuth:Github:Enabled`），并给出 ClientId 与 ClientSecret。
2. 从依赖方发起登录，在登录页点提供商按钮，授权后回到依赖方原页面，顶栏显示的是本地投影的用户。
3. 已开启两步验证的账号再走一次：外部登录后进入第二步，用验证码或恢复码完成后接续原授权请求。
4. 在浏览器开发者工具里确认：Cookie 只有会话引用，本地存储、会话存储与地址栏里都没有令牌。
