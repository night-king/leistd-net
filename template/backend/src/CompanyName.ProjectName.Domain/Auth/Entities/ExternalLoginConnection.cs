#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.ValueObjects;
using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.MultiTenancy.Tenancy;

namespace CompanyName.ProjectName.Domain.Auth.Entities;

/// <summary>外部登录连接实体（GitHub、Google 等第三方身份提供商）。</summary>
/// <remarks>
/// 实现 <see cref="IMultiTenant"/>：外部身份的 (Provider, ProviderUserId) 由第三方决定，
/// 只在租户内唯一。不分区的话，同一个 GitHub 账号在租户 A 绑定后，租户 B 的登录会命中 A 的连接，
/// 既泄漏该外部身份已被占用，又让同一账号无法在多个租户各自绑定——那是 SaaS 的正常需求。
/// </remarks>
public class ExternalLoginConnection : DeletionAuditedEntity<Guid>, IAggregateRoot<Guid>, IMultiTenant
{
    /// <summary>所属租户（null 为宿主），由多租户落值拦截器在创建时填充。</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>用户 ID。</summary>
    public Guid UserId { get; private set; }

    /// <summary>外部身份提供商（GitHub, Google）。</summary>
    public string Provider { get; private set; }

    /// <summary>外部身份提供商的用户 ID。</summary>
    public string ProviderUserId { get; private set; }

    /// <summary>提供商侧的账号资料快照。</summary>
    /// <remarks>
    /// 只用于在"已绑定哪些登录方式"里显示是哪个账号，不是本地用户名，也不是身份标识：
    /// 身份按 <see cref="Provider"/> + <see cref="ProviderUserId"/> 认，本地用户名由用户领域服务生成。
    /// </remarks>
    public ExternalProfile Profile { get; private set; }

    /// <summary>连接是按已验证邮箱自动关联到已有账号建立的，此后还没有经它用独立因子完成过第二步。</summary>
    /// <remarks>
    /// 自动关联凭的是邮箱：控制了邮箱的人可以在提供商那里建一个同邮箱的号连进来。为真时经这条连接登录，
    /// 第二步只认独立因子；用独立因子完成一次后清除。本人在设置里主动绑定的连接不带这个标记。
    /// </remarks>
    public bool LinkedByEmail { get; private set; }

    private ExternalLoginConnection()
    {
        Provider = null!;
        ProviderUserId = null!;
        Profile = null!;
    }

    public ExternalLoginConnection(Guid userId, string provider, string providerUserId, ExternalProfile profile, bool linkedByEmail = false)
    {
        Id = Guid.CreateVersion7();
        UserId = userId;
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ProviderUserId = providerUserId ?? throw new ArgumentNullException(nameof(providerUserId));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        LinkedByEmail = linkedByEmail;
    }

    /// <summary>本人经这条连接用独立因子完成了第二步：连接不再视为只凭邮箱建立。</summary>
    public void ConfirmByIndependentFactor()
    {
        LinkedByEmail = false;
    }

    /// <summary>用提供商最新的资料替换快照。</summary>
    public void Sync(ExternalProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }
}
#endif
