using System.Data;
using Microsoft.Extensions.Options;

namespace Leistd.UnitOfWork.Options;

// 默认选项与单次选项共用同一判据：启动期经配置节拒绝默认选项，Begin(options) 在创建工作单元前拒绝单次选项。
internal sealed class UnitOfWorkOptionsValidator(string configSectionPath) : IValidateOptions<UnitOfWorkOptions>
{
    // ADO.NET 命令超时以整秒计，0 表示不限时：不足 1 秒的值会被换算成"不限时"，而不是更短的超时。
    internal static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);

    // 超出 int 整秒无法换算成命令超时。
    internal static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(int.MaxValue);

    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath { get; } = configSectionPath;

    public ValidateOptionsResult Validate(string? name, UnitOfWorkOptions options)
    {
        var failures = GetFailures(options, ConfigSectionPath + ":");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // 启动期与 Begin(options) 共用的判据；keyPrefix 为配置节路径加冒号，或方法参数时的类型名加点。
    // 返回每条失败一项，为空表示通过。
    internal static List<string> GetFailures(IUnitOfWorkOptions options, string keyPrefix)
    {
        var failures = new List<string>();
        if (options.Timeout is { } timeout && (timeout < MinimumTimeout || timeout > MaximumTimeout))
        {
            failures.Add($"{keyPrefix}Timeout must be null or between 1 second and {int.MaxValue} seconds.");
        }

        if (options.IsolationLevel is { } isolationLevel && !Enum.IsDefined(isolationLevel))
        {
            failures.Add($"{keyPrefix}IsolationLevel must be a defined {nameof(IsolationLevel)} value.");
        }

        return failures;
    }
}
