#if (LocalIdentity)
using System.Security.Claims;
using CompanyName.ProjectName.Application.Tenants.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Tenants.AppServices;

/// <summary>租户模拟登录：宿主管理员以目标租户管理员的身份获得一个会话主体。</summary>
/// <remarks>
/// <para><b>为什么是模拟登录而不是"宿主跨租户管理"。</b>全局查询过滤器按
/// <c>TenantId == CurrentTenantId</c> 分区，宿主只看得见宿主自己的行——这是默认安全。
/// 若改成在宿主界面上跨租户 CRUD，就要到处关掉过滤器；而租户一旦分库
/// （DedicatedDatabase），跨库查询根本不成立；审计也会分不清操作是宿主做的还是租户自己做的。
/// 模拟登录把这三件事一次解决：进到租户上下文里操作，过滤器照常生效，
/// 而发起人身份留在主体声明上可供审计。</para>
/// <para>返回主体而不是直接登录：<c>SignInAsync</c> 属于 HTTP 层，应用层不能引用 Api。
/// 与 <c>IAuthAppService.AuthenticateSessionAsync</c> 同型。</para>
/// </remarks>
public interface ITenantImpersonationAppService : IAppService
{
    /// <summary>以目标租户的管理员身份构造会话主体。</summary>
    Task<ClaimsPrincipal> ImpersonateAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>结束模拟，构造回发起人的会话主体。</summary>
    Task<ClaimsPrincipal> EndImpersonationAsync(CancellationToken cancellationToken = default);

    /// <summary>读取当前会话的模拟状态。</summary>
    Task<ImpersonationStatusOutputDto> GetStatusAsync(CancellationToken cancellationToken = default);
}
#endif
