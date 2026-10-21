#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Tenants.Dtos;

/// <summary>当前会话的模拟登录状态。</summary>
/// <remarks>
/// 独立于 <c>UserOutputDto</c>：后者同时是注册接口的输出，把模拟态塞进去会让一个
/// 与注册无关的字段出现在注册响应里。代价是前端启动时多一次请求。
/// </remarks>
public record ImpersonationStatusOutputDto
{
    /// <summary>当前会话是否处于模拟态。</summary>
    public required bool IsImpersonating { get; init; }

    /// <summary>发起模拟的用户的显示名（未设置时为用户名）；非模拟态为 <see langword="null"/>。</summary>
    /// <remarks>与操作记录的操作人列同一取法，否则同一个人在顶栏是 admin、在记录里是 System Administrator。</remarks>
    public string? ImpersonatorName { get; init; }

    /// <summary>当前所在租户的显示名（未设置时为租户名）；非模拟态为 <see langword="null"/>。</summary>
    public string? TenantName { get; init; }
}
#endif
