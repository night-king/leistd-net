using Leistd.MultiTenancy.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.AspNetCore.Options;

// 在所有注册完成后校验 ITenantStore，避免组合顺序造成误报。
// 只查注册表，不从根容器解析 Scoped Store。报错键名按实际绑定的配置节给出。
internal sealed class TenantStoreRegistrationValidator(
    IServiceProvider serviceProvider,
    string sectionPath = MultiTenancyOptions.SectionName)
    : IValidateOptions<MultiTenancyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MultiTenancyOptions options)
    {
        if (!options.ValidateResolvedTenant)
        {
            return ValidateOptionsResult.Success;
        }

        // 自定义容器未提供注册探测时跳过。
        var probe = serviceProvider.GetService<IServiceProviderIsService>();
        if (probe is null || probe.IsService(typeof(ITenantStore)))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"{sectionPath}:ValidateResolvedTenant is true but no ITenantStore is registered. " +
            "Hosts that own the tenant registry should call AddMultiTenancyEfCore<TDbContext>(); " +
            "resource services that only " +
            "consume the tenant claim should set ValidateResolvedTenant to false — the resolve chain then " +
            "narrows to the principal contributor alone and no store is needed.");
    }
}
