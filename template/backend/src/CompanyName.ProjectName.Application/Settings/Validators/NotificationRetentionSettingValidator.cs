#if (IncludeNotifications)
using System.Globalization;
using CompanyName.ProjectName.Application.Settings.Errors;
using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.ExceptionHandling;
using Leistd.Settings.Resolution;
using Leistd.Settings.Validation;

namespace CompanyName.ProjectName.Application.Settings.Validators;

/// <summary>未读通知的保留天数不得短于已读的，否则未读通知先于已读被删。</summary>
/// <remarks>
/// <para>写入与清除都经过这里（清除时候选值是回落后的部署基线），两项分开保存也不会留下违序的一组。</para>
/// <para>另一项读设置链（宿主层覆盖，没有就是部署基线），不读 <c>IOptionsMonitor</c>：其它实例尚未刷新、或上一组被整组退回时，
/// Options 的当前值会落后于库里的值。</para>
/// </remarks>
internal sealed class NotificationRetentionSettingValidator(ISettingProvider settingProvider) : ISettingValueValidator
{
    public async Task ValidateAsync(SettingValueValidationContext context, CancellationToken cancellationToken = default)
    {
        var name = context.Definition.Name;
        if (name is not (SettingConstant.NotificationRetention.ReadDays or SettingConstant.NotificationRetention.UnreadDays))
        {
            return;
        }

        var candidate = int.Parse(context.Value, CultureInfo.InvariantCulture);
        var (read, unread) = name == SettingConstant.NotificationRetention.ReadDays
            ? (candidate, await settingProvider.GetAsync<int>(SettingConstant.NotificationRetention.UnreadDays, cancellationToken))
            : (await settingProvider.GetAsync<int>(SettingConstant.NotificationRetention.ReadDays, cancellationToken), candidate);

        if (unread < read)
        {
            throw new BusinessException(
                    AppSettingErrorCodes.NotificationRetentionOrder,
                    $"Unread notifications must be kept at least as long as read ones ({unread} < {read} days).")
                .WithData("Read", read)
                .WithData("Unread", unread);
        }
    }
}
#endif
