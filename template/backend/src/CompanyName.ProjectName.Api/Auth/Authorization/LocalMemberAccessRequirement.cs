#if (RemoteTokenAuth)
using Microsoft.AspNetCore.Authorization;

namespace CompanyName.ProjectName.Api.Auth.Authorization;

/// <summary>资源服务形态：自然人主体在本服务有成员行，且本服务没有停用它。</summary>
/// <remarks>
/// <para>启停归本服务所有，签发方的令牌不知道它，所以认证阶段拦不住；放进默认策略与
/// <see cref="ApiPolicies.CurrentUser"/>，才能让停用对 <c>/me</c>、读设置、通知中心这些
/// 不走权限点的端点也立即生效。RBAC 路径另有同样的判定（见 <c>PermissionSubjectProvider</c>）。</para>
/// <para>只约束自然人：机器主体（<c>client:&lt;client_id&gt;</c>）没有成员行，本要求对它不作判定。</para>
/// <para>判定见 <see cref="LocalMemberAccessHandler"/>，拒绝的响应见 <see cref="ApiAuthorizationResultHandler"/>。</para>
/// </remarks>
public sealed class LocalMemberAccessRequirement : IAuthorizationRequirement;
#endif
