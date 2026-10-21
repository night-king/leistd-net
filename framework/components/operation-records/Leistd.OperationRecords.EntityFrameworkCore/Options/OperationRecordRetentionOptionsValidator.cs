using Microsoft.Extensions.Options;

namespace Leistd.OperationRecords.EntityFrameworkCore.Options;

// 区间校验放在选项上：设置面板写进来的越界值在这里被拒，整组不生效，归档照旧按上一组合规值运行
internal sealed class OperationRecordRetentionOptionsValidator(string configSectionPath) : IValidateOptions<OperationRecordRetentionOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath { get; } = configSectionPath;

    public ValidateOptionsResult Validate(string? name, OperationRecordRetentionOptions options)
    {
        var failures = new List<string>();
        if (options.Enabled && options.RetentionDays is null)
        {
            failures.Add($"{ConfigSectionPath}:RetentionDays is required when Enabled is true.");
        }
        else if (options.RetentionDays is < OperationRecordRetentionOptions.MinimumRetentionDays or > OperationRecordRetentionOptions.MaximumRetentionDays)
        {
            failures.Add($"{ConfigSectionPath}:RetentionDays must be between {OperationRecordRetentionOptions.MinimumRetentionDays} and {OperationRecordRetentionOptions.MaximumRetentionDays}.");
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
