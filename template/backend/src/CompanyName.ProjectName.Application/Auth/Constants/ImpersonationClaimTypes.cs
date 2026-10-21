#if (LocalIdentity)
using Leistd.Security.Claims;

namespace CompanyName.ProjectName.Application.Auth.Constants;

/// <summary>模拟登录期间写入会话主体的声明类型。</summary>
/// <remarks>
/// <para>放在应用层而不是框架：模拟登录是本模板的产品能力，不是 Leistd 的通用原语。
/// 与 <see cref="Shared.AuthenticationSchemeNames"/> 同型——应用层构造主体时需要它，而应用层不能引用 Api。</para>
/// <para>发起人是宿主用户时 <see cref="ImpersonatorTenantId"/> <b>缺席</b>，
/// 与 <c>CustomClaimTypes.TenantId</c> "宿主用户无此声明"是同一口径；
/// 若改成写入空串，结束模拟时就分不清"发起人在宿主"和"声明写坏了"。</para>
/// </remarks>
public static class ImpersonationClaimTypes
{
    /// <summary>发起模拟的用户 Id。<b>存在即表示当前会话处于模拟态</b>。</summary>
    /// <remarks>
    /// 取框架的声明名而不是自起一个：操作记录按 <see cref="CustomClaimTypes.ImpersonatorUserId"/>
    /// 还原真实操作人。两边名字一旦不同，模拟期间的每条记录都只剩被模拟者，
    /// "由谁模拟操作"永远是空的——而且不报错，要到租户翻记录时才发现。
    /// </remarks>
    public const string ImpersonatorUserId = CustomClaimTypes.ImpersonatorUserId;

    /// <summary>发起模拟的用户显示名快照；操作记录据它写"由谁模拟操作"。</summary>
    public const string ImpersonatorName = CustomClaimTypes.ImpersonatorUserName;

    /// <summary>发起模拟的用户所属租户 Id；缺席表示发起人是宿主用户。</summary>
    public const string ImpersonatorTenantId = "impersonator_tenant_id";
}
#endif
