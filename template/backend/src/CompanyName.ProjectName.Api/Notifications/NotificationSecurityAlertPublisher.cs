#if (LocalIdentity)
#if (IncludeNotifications)
using System.Globalization;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Notifications.Constants;
using CompanyName.ProjectName.Application.Settings.Timing;
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
/// 文案按<b>收件人</b>的界面语言渲染：本人设过语言就用它，否则用组织默认或部署基线，都没有才用本次请求的语言——
/// 触发提醒的请求往往就是本人发出的（改密码、启用两步验证）。
/// 正文里的时刻（锁定截止）按收件人的展示时区写出并注明时区：锁定发生在登录前，当前用户为空，按目标用户解析。
/// </remarks>
public sealed class NotificationSecurityAlertPublisher(
    INotificationPublisher publisher,
    IUserTimeZoneProvider userTimeZoneProvider,
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
            var until = alert.Until is { } utc
                ? FormatUntil(utc, await userTimeZoneProvider.GetForUserAsync(userId, cancellationToken))
                : "-";
            var (title, content) = await RenderAsync(userId, alert, until, cancellationToken);
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
        // 只有调用方真的取消了才让取消传出去；下游自己的超时、取消也是投递失败，按尽力而为吞掉
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 提醒是尽力而为的：发不出去不能让登录、改密码失败
            logger.LogWarning(ex, "Could not publish security alert {Kind} for user {UserId}", alert.Kind, userId);
        }
    }

#if (IncludeLocalization)
    private async Task<(string Title, string? Content)> RenderAsync(Guid userId, SecurityAlert alert, string until, CancellationToken cancellationToken)
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
            var content = Fill(localizer[$"SecurityAlert:{alert.Kind}:Content"].Value, alert, until);
            return (title, content);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
#else
    private static Task<(string Title, string? Content)> RenderAsync(Guid userId, SecurityAlert alert, string until, CancellationToken cancellationToken)
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
            SecurityAlertKind.LockedOut => ("Your account was locked", "There were too many failed sign-in attempts. It unlocks at {Until}."),
            SecurityAlertKind.EmailChanged => ("Your account email address was changed", "If you didn't do this, contact your administrator right away."),
#if (Email)
            SecurityAlertKind.PasswordRecovered => ("Your password was reset with an email code", "All devices were signed out. If you didn't do this, contact your administrator right away."),
            SecurityAlertKind.TwoFactorEmailEnabled => ("Email verification codes turned on", "Signing in can now use a code sent to your email. If you didn't do this, change your password right away."),
            SecurityAlertKind.TwoFactorEmailDisabled => ("Email verification codes turned off", "Signing in no longer offers codes sent to your email. If you didn't do this, change your password."),
#endif
            _ => (alert.Kind.ToString(), null)
        };
        return Task.FromResult((title, content is null ? null : Fill(content, alert, until)));
    }
#endif

    private static string Fill(string text, SecurityAlert alert, string until) => text
        .Replace("{Ip}", alert.IpAddress ?? "-", StringComparison.Ordinal)
        .Replace("{Device}", DescribeDevice(alert.UserAgent), StringComparison.Ordinal)
        .Replace("{Until}", until, StringComparison.Ordinal);

    // 时刻后面带上时区名：读的人才知道它是哪里的时间，回落到 UTC 时也写明
    private static string FormatUntil(DateTime utc, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);
        return $"{local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({timeZone.Id})";
    }

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
