namespace CompanyName.ProjectName.Domain.Auth.Abstractions;

/// <summary>外部用户信息。</summary>
public record ExternalUserInfo
{
    public required string ProviderId { get; init; }
    public string? Email { get; init; }

    /// <summary>提供商确认 <see cref="Email"/> 属于该外部账号。只有为真时才可能按邮箱关联已有用户。</summary>
    public bool EmailVerified { get; init; }

    /// <summary>提供商侧的账号标签，用于在"已绑定哪些登录方式"里显示是哪个账号。</summary>
    /// <remarks>
    /// 它是展示用的，<b>不能</b>直接当本地用户名：Google 这类提供商没有句柄，这里放的是邮箱，
    /// 拿它当用户名等于把邮箱本地部变成公开标识符。本地用户名由
    /// <see cref="SuggestedUsername"/> 与显示名派生，见 <c>ExternalAuthDomainService</c> 的生成方法。
    /// </remarks>
    public required string ProviderAccountLabel { get; init; }

    /// <summary>提供商的公开句柄，可以直接作为本地用户名的基底；没有句柄的提供商留 <c>null</c>。</summary>
    /// <remarks>
    /// 由提供商<b>显式</b>给出，不从别的字段推断：GitHub 的 <c>login</c> 设计上就是公开句柄，
    /// 放进来是安全的；Google 只有邮箱与姓名，没有句柄，必须留 <c>null</c>——
    /// 不要拿邮箱本地部顶替，那正是要避免的事。
    /// </remarks>
    public string? SuggestedUsername { get; init; }

    public string? DisplayName { get; init; }
    public string? AvatarUrl { get; init; }

    /// <summary>
    /// 提供商的显示名（官方远程 scheme 的 DisplayName，如 <c>GitHub</c>），只用于面向用户的提示；
    /// 为空时提示退回提供商标识。
    /// </summary>
    public string? ProviderDisplayName { get; init; }
}
