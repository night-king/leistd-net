#if (LocalIdentity)
#if (Email && IncludeLocalization)
using System.Globalization;
#endif
#if (Email)
using System.Net;
#endif
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
#if (Email && IncludeLocalization)
using CompanyName.ProjectName.Application.Settings.Provider;
#endif
#if (Email)
using Leistd.Email.Abstractions;
#endif
using Leistd.EventBus.EventHandlers;
#if (Email && IncludeLocalization)
using Leistd.Settings.Resolution;
#endif
#if (Email)
using Microsoft.Extensions.Logging;
#endif
#if (Email && IncludeLocalization)
using Microsoft.Extensions.Localization;
#endif

namespace CompanyName.ProjectName.Application.Auth.EventHandlers;

#if (Email)
/// <summary>邮箱修改提交之后告知本人：向被换掉的已验证地址直接发一封账号安全邮件，并发一条普通安全提醒。</summary>
/// <remarks>
/// <para>旧地址邮件直接经 <see cref="IEmailSender"/> 发送，不走通知组件：通知的邮件渠道只发账号<b>当前</b>的已验证地址，
/// 也受本人的通知偏好约束，而这封信的意义就在于让被换掉的地址的主人知道。所以它不看通知偏好，只发已验证的旧地址——
/// 未验证的地址（含外部登录建号时的占位地址）没有证明属于本人，不发账号安全信息。</para>
/// <para>投递尽力而为：发送失败只记日志，不让已经提交的修改报错；发送器正常返回也只表示投递设施已接受。</para>
/// <para>普通安全提醒经 <see cref="ISecurityAlertPublisher"/> 发出，按通知偏好送达当前收件人（含当前地址的邮件渠道），
/// 与旧地址邮件互不依赖；未启用通知时它什么也不做。</para>
/// </remarks>
internal sealed class EmailChangedEventHandler(
    IEmailSender emailSender,
#if (IncludeLocalization)
    ISettingProvider settingProvider,
    IStringLocalizer localizer,
#endif
    ISecurityAlertPublisher securityAlerts,
    ILogger<EmailChangedEventHandler> logger) : IEventHandler<EmailChangedEvent>
#else
/// <summary>邮箱修改提交之后发一条普通安全提醒（未启用通知时什么也不做）。</summary>
internal sealed class EmailChangedEventHandler(ISecurityAlertPublisher securityAlerts) : IEventHandler<EmailChangedEvent>
#endif
{
    /// <inheritdoc />
    public async Task HandleAsync(EmailChangedEvent @event, CancellationToken cancellationToken = default)
    {
#if (Email)
        if (@event.OldEmailConfirmed)
        {
            await NotifyOldAddressAsync(@event, cancellationToken);
        }

#endif
        // 投递失败由发布方的实现自己吞掉
        await securityAlerts.PublishAsync(@event.UserId, new SecurityAlert(SecurityAlertKind.EmailChanged), cancellationToken);
    }
#if (Email)

    private async Task NotifyOldAddressAsync(EmailChangedEvent @event, CancellationToken cancellationToken)
    {
        try
        {
            var (subject, content) = await RenderAsync(@event.UserId, cancellationToken);
            await emailSender.SendAsync(
                new EmailMessage
                {
                    To = @event.OldEmail,
                    Subject = subject,
                    Body = BuildBody(subject, content)
                },
                cancellationToken);
        }
        // 只有调用方真的取消了才让取消传出去；下游自己的超时、取消也是投递失败，按尽力而为吞掉
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 修改已经提交：发不出去只记日志，不让它报失败
            logger.LogWarning(
                ex,
                "Could not send the email-changed notice for user {UserId} (changed by {ChangedBy})",
                @event.UserId,
                @event.ChangedBy);
        }
    }

#if (IncludeLocalization)
    // 文案按收件人的界面语言渲染：本人设过语言就用它，否则用组织默认或部署基线，都没有才用本次请求的语言
    private async Task<(string Subject, string Content)> RenderAsync(Guid userId, CancellationToken cancellationToken)
    {
        var language = await settingProvider.GetOrNullForUserAsync(SettingConstant.Display.Language, userId.ToString(), cancellationToken);

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            if (language is not null)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            }

            return (
                localizer["SecurityAlert:EmailChanged:OldAddressTitle"].Value,
                localizer["SecurityAlert:EmailChanged:OldAddressContent"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
#else
    private static Task<(string Subject, string Content)> RenderAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult((
            "This address is no longer your account's email",
            "The email address of your account was changed to another address. If you didn't do this, contact your administrator right away."));
#endif

    private static string BuildBody(string heading, string content) => $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden;'>
    <div style='background-color: #0f172a; padding: 20px; text-align: center; color: white;'>
        <h2 style='margin: 0;'>{WebUtility.HtmlEncode(heading)}</h2>
    </div>
    <div style='padding: 30px; background-color: #f8fafc; color: #334155;'>
        <p>{WebUtility.HtmlEncode(content)}</p>
    </div>
</div>";
#endif
}
#endif
