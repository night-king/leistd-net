#if (RemoteTokenAuth)
using CompanyName.ProjectName.Domain.Users.ValueObjects;

namespace CompanyName.ProjectName.Api.Auth.Authorization;

/// <summary>本请求投影时读到的成员访问状态，供授权阶段复用，免去再查一次。</summary>
/// <remarks>
/// <para>作用域服务：只活在一个请求（或一次 Hub 调用）里，不跨请求缓存——停用要在下一个请求就生效。</para>
/// <para>按（租户，主体）记录，取用时两者都要对上：请求中途切换了租户或主体，就当没有快照，回到按主键读。</para>
/// </remarks>
public sealed class LocalMemberAccessSnapshot
{
    private (Guid? TenantId, Guid UserId, UserAccessStatus Status)? recorded;

    /// <summary>记下投影读到的状态（由 <c>ResourceUserProvisioningMiddleware</c> 在投影成功后调用）。</summary>
    public void Record(Guid? tenantId, Guid userId, UserAccessStatus status) =>
        recorded = (tenantId, userId, status);

    /// <summary>取同一（租户，主体）的状态；没有记录或对不上时返回 <see langword="false"/>。</summary>
    public bool TryGet(Guid? tenantId, Guid userId, out UserAccessStatus status)
    {
        if (recorded is { } value && value.TenantId == tenantId && value.UserId == userId)
        {
            status = value.Status;
            return true;
        }

        status = default;
        return false;
    }
}
#endif
