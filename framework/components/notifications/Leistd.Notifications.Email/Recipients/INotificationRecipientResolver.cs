namespace Leistd.Notifications.Email.Recipients;

/// <summary>把通知收件人解析成可投递的邮件地址，由宿主按自己的用户模型实现。</summary>
public interface INotificationRecipientResolver
{
    /// <summary>返回收件人已验证的邮件地址；没有地址或未验证时返回 <see langword="null"/>，这一次不发邮件。</summary>
    /// <param name="userId">收件人。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<string?> ResolveEmailAsync(string userId, CancellationToken cancellationToken = default);
}
