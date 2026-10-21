using Leistd.Data.Connections;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.EntityFrameworkCore.ConnectionStrings;

// 这组选项由宿主在 AddXxx 里用代码配置，没有配置节，因此消息用类型名定位而不是配置键。
internal sealed class LocalTenantConnectionOptionsValidator : IValidateOptions<LocalTenantConnectionOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalTenantConnectionOptions options) =>
        !string.IsNullOrWhiteSpace(options.ControlPlaneConnectionStringName)
        && !string.Equals(options.ControlPlaneConnectionStringName, ConnectionStringNames.Default, StringComparison.Ordinal)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "LocalTenantConnectionOptions.ControlPlaneConnectionStringName is required and must not be " +
                $"'{ConnectionStringNames.Default}' (was '{options.ControlPlaneConnectionStringName}'). " +
                "The control-plane context must be pinned to its own connection name so it is never tenant-routed.");
}
