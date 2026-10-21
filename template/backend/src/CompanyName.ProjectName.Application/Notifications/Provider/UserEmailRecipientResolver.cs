#if (LocalIdentity)
#if (IncludeNotifications)
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Notifications.Email.Recipients;

namespace CompanyName.ProjectName.Application.Notifications.Provider;

/// <summary>邮件通知的收件地址：只给<b>已验证</b>的邮箱。</summary>
/// <remarks>
/// 没验证过的邮箱不发：那个地址可能根本不是本人的，发过去就是把账号动态告诉了别人。
/// 发送本身（HTML 编码、经后台队列异步发出、队列满时放弃这一封）由邮件通知组件负责。
/// 无状态，按 Transient 登记（见 <c>AddMyProjectNotifications</c>）。
/// </remarks>
public sealed class UserEmailRecipientResolver(IRepository<User, Guid> userRepository) : INotificationRecipientResolver
{
    /// <inheritdoc />
    public async Task<string?> ResolveEmailAsync(string userId, CancellationToken cancellationToken = default)
        => Guid.TryParse(userId, out var id) && await userRepository.GetByIdAsync(id, cancellationToken) is { EmailConfirmed: true } user
            ? user.Email
            : null;
}
#endif
#endif
