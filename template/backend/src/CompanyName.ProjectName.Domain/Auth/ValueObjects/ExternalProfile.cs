#if (LocalIdentity)
namespace CompanyName.ProjectName.Domain.Auth.ValueObjects;

/// <summary>外部提供商上次同步来的账号资料快照，每次登录或重新绑定时整体替换。</summary>
/// <remarks>只用于展示"绑定的是哪个外部账号"；不参与身份匹配，匹配看提供商与提供商用户 Id。</remarks>
public sealed record ExternalProfile
{
    /// <summary>提供商上的账号标签（如 GitHub 的 login，Google 为邮箱）。</summary>
    public string? AccountLabel { get; private init; }

    /// <summary>提供商给的邮箱，可能未验证，只作展示。</summary>
    public string? Email { get; private init; }

    public string? AvatarUrl { get; private init; }

    /// <summary>同步时刻。</summary>
    public DateTime SyncedAt { get; private init; }

    private ExternalProfile()
    {
    }

    public ExternalProfile(DateTime syncedAt, string? accountLabel, string? email, string? avatarUrl)
    {
        SyncedAt = syncedAt;
        AccountLabel = accountLabel;
        Email = email;
        AvatarUrl = avatarUrl;
    }
}
#endif
