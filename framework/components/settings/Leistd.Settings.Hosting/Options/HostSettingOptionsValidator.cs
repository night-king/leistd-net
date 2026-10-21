using Microsoft.Extensions.Options;

namespace Leistd.Settings.Hosting.Options;

internal sealed class HostSettingOptionsValidator(string configSectionPath) : IValidateOptions<HostSettingOptions>
{
    // 选项绑定的配置节；重复注册时据此拒绝另一路径。
    public string ConfigSectionPath { get; } = configSectionPath;

    public ValidateOptionsResult Validate(string? name, HostSettingOptions options)
        => options.RefreshInterval >= TimeSpan.FromSeconds(1)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ConfigSectionPath}:{nameof(HostSettingOptions.RefreshInterval)} must be at least one second (was '{options.RefreshInterval}').");
}
