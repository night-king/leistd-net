using System.Security.Cryptography;
using System.Text;
using CompanyName.ProjectName.Domain.Users.Policies;
using CompanyName.ProjectName.Domain.Users.Entities;

namespace CompanyName.ProjectName.Application.Users.Avatars;

/// <summary>头像对外的地址。</summary>
/// <remarks>
/// <para>上传的图片不内联进 DTO：内联的话 <c>/auth/me</c> 与每一行用户列表都要背一整张图。
/// 对外只给 <c>/api/v1/users/{id}/avatar?v=…</c>，版本号取自内容摘要——换了头像地址就变，
/// 因此图片可以长期缓存而不会看到旧图。外部地址（外部登录提供方给的）原样给出。</para>
/// <para>写回时同理：客户端把拿到的地址原样提交回来，表示"没改头像"，
/// 见 <see cref="ResolveSubmitted"/>。</para>
/// </remarks>
public static class AvatarUrls
{
    /// <summary>对外的头像地址；没有头像时为 <see langword="null"/>。</summary>
    public static string? For(User user) => For(user.Id, user.Avatar);

    /// <inheritdoc cref="For(User)"/>
    public static string? For(Guid userId, string? avatar)
    {
        if (string.IsNullOrEmpty(avatar))
            return null;

        if (AvatarPolicy.IsExternalUrl(avatar))
            return avatar;

        // 写入时已按 AvatarPolicy 校验，这里不再解码；版本号是内容摘要的前 12 位。
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(avatar)));
        return $"/api/v1/users/{userId}/avatar?v={digest[..12].ToLowerInvariant()}";
    }

    /// <summary>客户端提交的头像值：原样送回的对外地址表示"不变"，换回存储的原值；其余照收（仍须另行校验）。</summary>
    public static string? ResolveSubmitted(User user, string? submitted)
    {
        var trimmed = submitted?.Trim();
        return !string.IsNullOrEmpty(trimmed) && string.Equals(trimmed, For(user), StringComparison.Ordinal)
            ? user.Avatar
            : trimmed;
    }
}
