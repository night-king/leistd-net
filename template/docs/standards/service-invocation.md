# 服务间调用

本项目使用 Leistd.ServiceClient 的强类型客户端、关联标识、官方 OpenIddict.Client 机器认证及 Token Exchange。身份来自签发方令牌，业务代码面向接口，不直接用 `IHttpClientFactory` 绕开标准管道；随包文档 docs/service-client.md 说明框架入口与错误契约。

## 调用其他服务

引用目标服务的 Client 包，注册工作负载身份，再为返回的 IHttpClientBuilder 明确选择认证模式：

```csharp
builder.Services.AddServiceAuthentication();
builder.Services.AddOrderClient().AddClientCredentials();   // 目标服务 Client 包的注册入口
builder.Services.AddBillingClient().AddTokenExchange();
```

机器调用只代表客户端，范围绑定在 Leistd:ServiceClients:{Name}:Scope。用户调用从 Resource 请求读取已验证的 Bearer；带浏览器会话时也可读取服务端保存的访问令牌并交换，不能通过设置 ICurrentUser、ICurrentTenant 或请求头制造用户凭据。Identity/Standalone 的 Cookie 和后台用户上下文不提供交换证明；用户委托需要已验证的用户访问令牌。

用户委托默认用服务级 scope，表示允许代表用户访问该服务，默认与目标资源同名；实际以签发方登记的契约为准，Scope 可单独配置。被调服务仍校验用户本地权限（见[被其他服务调用](#被其他服务调用)）；需要更窄的应用委托能力时，显式设计对应的签发与校验契约。机器调用按能力分组细分 scope（如 invoices.read），保持最小权限，不按单个端点机械拆分，也不收敛成服务级。机器令牌没有用户权限兜底，scope 是主要的能力授权边界，资源与租户校验仍须保留。

远端失败以 RemoteServiceException 携带业务码、远端 traceId 和错误字段；协议取令牌失败为 ServiceClientException。ServiceClientException 按本地观测的失败来源映射：本地配置或未分类故障默认 500，远端明确失败、响应无效或提前中断默认 502，连接失败 503、等待超时 504；不从远端状态推断本地状态，原始 URL 与响应片段只留服务端诊断，已知上游契约可由宿主覆盖。可预期业务失败由本服务明确翻译。

## 被其他服务调用

接收端验证令牌签名、issuer、audience 和有效期，再从同一个主体读取用户与租户。旧的用户/租户委托头不参与认证与租户判定；机器令牌即使附带伪造头，也不能通过要求自然人的默认策略。纯工作负载端点单独声明机器策略。命名策略不叠加默认策略，带权限策略的控制器仍在类上标 `[Authorize]`。机器调用不会自动转发环境租户，租户须作为路由或请求参数显式传递；机器端点校验调用权限及租户有效性。

Token Exchange 令牌保留用户、租户与身份库的用户名/邮箱/显示名，带 act 说明调用方；不继承来源角色和超管标记。下游使用本地授权主体和权限决定业务操作。非法租户 claim 失败关闭，不回退宿主。诊断入口 GET /api/v1/service-info/whoami 返回当前用户和 client 身份。

## Identity 与资源服务对接

Identity 的 OAuth:ApiResources 登记由它签发令牌的下游资源对象，例如 { "Name": "orders-api", "OwnerClientId": "orders-worker" }，Scope 与 OwnerClientId 可单独配置、默认资源名；同一资源仅一个归属，客户端可拥有多个资源。Resource 的 Authentication:Audience 使用资源 Name，Authentication:Issuer 与 Identity 的 OAuth:Issuer 精确一致，含路径与尾斜杠。
<!--#if (OpenIddictServer)-->
Identity 自身 API 的标识是 OAuth:Resource，同名登记为 scope，本服务只接受受众是它的令牌。访问令牌的受众由授予的 scope 推出，能签发哪些 scope 只由 `backend/src/CompanyName.ProjectName.Application/Auth/OAuth/OAuthScopes.cs` 定义（服务端登记、scope 表、开放应用的权限校验都读它）。
<!--#endif-->

开放应用仅使用 implicit consent，不提供同意类型输入和同意页。按调用场景登记权限：

<!--#if (OpenIddictServer || ResourceBrowserSession)-->
- 为带浏览器会话的资源服务登记依赖方：web/confidential、开启会话绑定、authorization code、refresh token、退出端点（ept:end_session）、PKCE；只授予 openid/profile/email/roles/offline_access 与自己的 API scope，登记本服务对外源下完整的 /api/v1/auth/signin、/api/v1/auth/signout 回调，不申请下游 scope。第三方 public client 仍可单独登记。
<!--#endif-->
- 调用方为 service/confidential，client ID 是来源资源的 OwnerClientId（默认资源名，例如 orders-api），启用 Token Exchange grant、ept:token、aud:billing-api、scp:billing-api。subject 令牌必须面向 orders-api，但无需带 billing-api scope；目标权限取调用方应用的登记值。只接受单跳访问令牌，不支持 actor_token 和请求覆盖身份。
- 机器调用启用 client credentials 与目标 scope，工作负载不混用用户授权流。
<!--#if (IncludeMultiTenancy)-->
- 租户回源授予 tenant-routing.read，迁移身份单独授予 tenant-migration.read。需要机器回源的交换客户端可同时启用 client credentials。
<!--#endif-->

配置示例（Secret 由 Leistd__ServiceAuth__ClientSecret 注入）：

```json
{
<!--#if (ResourceBrowserSession)-->
  "Authentication": {
    "Issuer": "https://login.example.com/",
    "Audience": "orders-api",
    "ClientId": "orders-browser",
    "ClientSecret": "<from-secret-store>"
  },
<!--#elseif (RemoteTokenAuth)-->
  "Authentication": {
    "Issuer": "https://login.example.com/",
    "Audience": "orders-api"
  },
<!--#endif-->
  "Leistd": {
    "ServiceAuth": {
      "Authority": "https://login.example.com/",
      "ClientId": "orders-api"
    },
    "ServiceClients": {
<!--#if (RemoteTokenAuth && IncludeMultiTenancy)-->
      "Identity": {
        "BaseAddress": "https://login.example.com/",
        "Scope": "tenant-routing.read"
      },
<!--#endif-->
      "Billing": {
        "BaseAddress": "https://billing.example.com/",
        "TokenExchange": { "Audience": "billing-api", "Scope": "billing-api" }
      }
    }
  }
}
```

工作负载身份全局注册一次，客户端配置名与注册名一致；示例中的 Billing 经 AddTokenExchange 注册。同一下游有多个客户端时用 `configSectionPath` 共用地址节，认证节按客户端名各自配置。
<!--#if (RemoteTokenAuth)-->
Resource 已注册用户访问令牌适配器。
<!--#if (IncludeMultiTenancy)-->
租户回源的 Identity 客户端经 AddClientCredentials 注册。
<!--#endif-->
<!--#endif-->
<!--#if (ResourceBrowserSession)-->
浏览器会话要求前端与本服务 API 同源，见 [浏览器认证](auth.md#浏览器认证)。
<!--#endif-->

交换 JWT 有效期 120 秒且不超过 subject exp；进程内 HybridCache 按官方客户端返回的到期时间提前 10 秒失效，键含完整 subject 摘要，不把 Bearer 写入 Redis。没有真实用户令牌时拒绝委托，不回退为机器身份。401 清除缓存，重试由业务层按幂等性决定。
<!--#if (IncludeMultiTenancy)-->

独立库先由各服务 DbMigrator 预迁移，再登记租户命名连接；只登记 default 时，各服务在租户库内使用自己的 schema。机器令牌和路由均有缓存，撤销权限不会主动清空热缓存；冷实例取被撤销范围时返回 502，日志保留官方 invalid_request 与 ID2051 诊断。
<!--#endif-->

## 发布本服务的 Client 包

src/CompanyName.ProjectName.Client 只依赖 Refit 与服务客户端框架，不引用服务内部程序集；DTO 独立演进。新增接口经 Refit 特性声明，并由 AddRefitServiceClient 注册，以统一还原远端异常。Client 注册返回 IHttpClientBuilder，由消费宿主选择机器认证或用户交换，不自行猜测身份模式。以 NuGet 发布并按服务版本升级。

服务同时提供机器端点与用户端点时，按认证方式拆分 Refit 接口与命名客户端，各给一个注册入口；框架要求每个命名客户端只挂一种认证，单模式服务不预建两套接口。

认证变更需运行本项目已配置的认证与服务调用集成测试，并在部署环境验证跨服务 Token Exchange 正反例、租户成员关系与用户资料一致性。
