using Microsoft.Extensions.Options;

namespace Leistd.Notifications.EntityFrameworkCore.Options;

internal sealed class NotificationRetentionOptionsValidator(string configSectionPath) : IValidateOptions<NotificationRetentionOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath { get; } = configSectionPath;

    public ValidateOptionsResult Validate(string? name, NotificationRetentionOptions options)
    {
        var failures = new List<string>();
        if (options.ReadRetentionDays is < 1 or > 3650)
        {
            failures.Add($"{ConfigSectionPath}:ReadRetentionDays must be between 1 and 3650.");
        }

        if (options.UnreadRetentionDays is < 1 or > 3650)
        {
            failures.Add($"{ConfigSectionPath}:UnreadRetentionDays must be between 1 and 3650.");
        }

        // 未读不得比已读先删
        if (options.UnreadRetentionDays < options.ReadRetentionDays)
        {
            failures.Add($"{ConfigSectionPath}:UnreadRetentionDays must not be shorter than ReadRetentionDays.");
        }

        if (options.DailyRunHourUtc is < 0 or > 23)
        {
            failures.Add($"{ConfigSectionPath}:DailyRunHourUtc must be between 0 and 23.");
        }

        if (options.BatchSize is < 100 or > 10000)
        {
            failures.Add($"{ConfigSectionPath}:BatchSize must be between 100 and 10000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
