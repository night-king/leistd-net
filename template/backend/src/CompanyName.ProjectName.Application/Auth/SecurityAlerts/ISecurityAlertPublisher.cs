#if (LocalIdentity)
namespace CompanyName.ProjectName.Application.Auth.SecurityAlerts;

/// <summary>把账号安全相关的事件告知本人。</summary>
/// <remarks>
/// <para>提醒是尽力而为的：投递失败只记日志，不让触发它的操作（登录、改密码）失败——
/// 实现必须自己吞掉异常。</para>
/// <para>默认实现什么也不做；启用通知时由宿主换成经通知组件发布的实现（站内 + 邮件，按本人偏好）。</para>
/// </remarks>
public interface ISecurityAlertPublisher
{
    /// <summary>发出一条安全提醒。</summary>
    /// <param name="userId">账号本人。</param>
    /// <param name="alert">提醒内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task PublishAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken = default);
}

// 没有通知组件时的默认实现
#endif
