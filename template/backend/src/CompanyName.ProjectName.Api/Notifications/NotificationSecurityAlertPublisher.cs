#if (LocalIdentity)
#if (IncludeNotifications)
using System.Globalization;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Notifications.Constants;
#if (IncludeLocalization)
using CompanyName.ProjectName.Api.Localization;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Settings.Resolution;
using Microsoft.Extensions.Localization;
#endif
using Leistd.Notifications.Publishing;
using Leistd.Notifications.Dtos;

namespace CompanyName.ProjectName.Api.Notifications;

/// <summary>经通知组件发出安全提醒：站内通知总会送达（必达组合），邮件按本人偏好（通知偏好组件读收件人的设置）。</summary>
/// <remarks>
/// 文案按<b>收件人</b>的界面语言渲染：本人设过语言就用它，没设过用本次请求的语言——
/// 触发提醒的请求往往就是本人发出的（改密码、启用两步验证）。
/// </remarks>
public sealed class NotificationSecurityAlertPublisher(
    INotificationPublisher publisher,
#if (IncludeLocalization)
    ISettingProvider settingProvider,
    IStringLocalizerFactory localizerFactory,
#endif
    ILogger<NotificationSecurityAlertPublisher> logger) : ISecurityAlertPublisher
{
    /// <inheritdoc />
    public async Task PublishAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken = default)
    {
        try
        {
            var (title, content) = await RenderAsync(userId, alert, cancellationToken);
            await publisher.PublishToUserAsync(
                userId.ToString(),
                new NotificationInputDto
                {
                    Title = title,
                    Content = content,
                    Type = AppNotificationTypes.Security,
                    Icon = "shield-alert",
                    Link = "/workspace/settings/security",
                    Metadata = new Dictionary<string, object> { ["kind"] = alert.Kind.ToString() }
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 提醒是尽力而为的：发不出去不能让登录、改密码失败
            logger.LogWarning(ex, "Could not publish security alert {Kind} for user {UserId}", alert.Kind, userId);
        }
    }

#if (IncludeLocalization)
    private async Task<(string Title, string? Content)> RenderAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken)
    {
        var language = await settingProvider.GetOrNullForUserAsync(SettingConstant.Display.Language, userId.ToString(), cancellationToken);

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            if (language is not null)
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            }

            // 必须用已登记到 JsonResourceTypes 的类型，未登记的类型会落到 RESX，取回的就是键本身
            var localizer = localizerFactory.Create(typeof(ApiResource));
            var title = localizer[$"SecurityAlert:{alert.Kind}:Title"].Value;
            var content = Fill(localizer[$"SecurityAlert:{alert.Kind}:Content"].Value, alert);
            return (title, content);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
#else
    private static Task<(string Title, string? Content)> RenderAsync(Guid userId, SecurityAlert alert, CancellationToken cancellationToken)
    {
        var (title, content) = alert.Kind switch
        {
            SecurityAlertKind.NewDeviceSignIn => ("New sign-in to your account", "Your account was signed in from a new device ({Device}, IP {Ip}). If this wasn't you, sign it out and change your password."),
            SecurityAlertKind.PasswordChanged => ("Your password was changed", "If you didn't do this, contact your administrator right away."),
            SecurityAlertKind.PasswordReset => ("Your password was reset by an administrator", "Your other devices were signed out."),
            SecurityAlertKind.TwoFactorEnabled => ("Two-factor authentication turned on", "Signing in now also needs a code from your authenticator app."),
            SecurityAlertKind.TwoFactorAuthenticatorReplaced => ("Authenticator app replaced", "Codes from your previous authenticator and your old recovery codes no longer work. If you didn't do this, contact your administrator right away."),
            SecurityAlertKind.TwoFactorDisabled => ("Two-factor authentication turned off", "If you didn't do this, turn it back on and change your password."),
            SecurityAlertKind.TwoFactorReset => ("Two-factor authentication was reset by an administrator", "Set it up again from your security settings."),
            SecurityAlertKind.LockedOut => ("Your account was locked", "There were too many failed sign-in attempts. It unlocks at {Until} (UTC)."),
            _ => (alert.Kind.ToString(), null)
        };
        return Task.FromResult((title, content is null ? null : Fill(content, alert)));
    }
#endif

    private static string Fill(string text, SecurityAlert alert) => text
        .Replace("{Ip}", alert.IpAddress ?? "-", StringComparison.Ordinal)
        .Replace("{Device}", DescribeDevice(alert.UserAgent), StringComparison.Ordinal)
        .Replace("{Until}", alert.Until?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-", StringComparison.Ordinal);

    // 只取 User-Agent 里最能认出设备的一截，完整原文放进提醒里没人读
    private static string DescribeDevice(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "-";

        var start = userAgent.IndexOf('(');
        var end = start < 0 ? -1 : userAgent.IndexOf(')', start);
        return start >= 0 && end > start ? userAgent[(start + 1)..end] : userAgent[..Math.Min(userAgent.Length, 60)];
    }
}
#endif
#endif
