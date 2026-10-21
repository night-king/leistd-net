using Leistd.Settings.Definitions;
using Leistd.Settings.Resolution;
using Leistd.Settings.Stores;
using Leistd.Settings.Hosting.Configuration;
using Leistd.Settings.Hosting.Options;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.Settings.Hosting.Runtime;

// 把宿主级设置按绑定推进配置源。只推存储里有行的项；值经 ISettingProvider 取，机密设置由它解密，明文只在进程内。
// 新值让某个 Options 校验不过时整组不生效、沿用上一组，只记错误不抛（值已落库）。
internal sealed class HostSettingApplier(
    HostSettingsConfigurationProvider configuration,
    ISettingStore settingStore,
    ISettingProvider settingProvider,
    IOptions<HostSettingBindingCollection> bindings,
    IServiceProvider services,
    ILogger<HostSettingApplier> logger)
{
    private static readonly MethodInfo ValidateMethod =
        typeof(HostSettingApplier).GetMethod(nameof(Validate), BindingFlags.NonPublic | BindingFlags.Static)!;

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        // 租户上下文下读不到宿主行，不能当成“都没设”推进配置
        if (!settingStore.CanAccessHostScope)
        {
            logger.LogWarning("Host settings were not applied: the host scope is not reachable in the current context.");
            return;
        }

        var stored = await settingStore.GetAllAsync(SettingScopes.Host, userId: null, cancellationToken);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings.Value.Bindings.Where(binding => stored.ContainsKey(binding.SettingName)))
        {
            var value = await settingProvider.GetOrNullAsync(binding.SettingName, cancellationToken);
            foreach (var key in binding.ConfigurationKeys)
            {
                values[key] = value;
            }
        }

        var failures = configuration.Apply(values, ValidateBoundOptions);
        if (failures.Count > 0)
        {
            logger.LogError(
                "Host settings were not applied because some options would fail validation; keeping the previous values: {Failures}",
                string.Join("; ", failures));
        }
    }

    // 按绑定涉及的选项类型各建一次实例：工厂每次新建并执行全部校验，不经缓存，也不依赖有没有人订阅变更
    private IReadOnlyList<string> ValidateBoundOptions()
    {
        List<string> failures = [];
        foreach (var type in bindings.Value.Bindings.Select(binding => binding.OptionsType).OfType<Type>().Distinct())
        {
            var message = (string?)ValidateMethod.MakeGenericMethod(type).Invoke(null, [services]);
            if (message is not null)
            {
                failures.Add(message);
            }
        }

        return failures;
    }

    private static string? Validate<TOptions>(IServiceProvider services)
        where TOptions : class
    {
        try
        {
            _ = services.GetRequiredService<IOptionsFactory<TOptions>>().Create(Microsoft.Extensions.Options.Options.DefaultName);
            return null;
        }
        catch (OptionsValidationException exception)
        {
            return exception.Message;
        }
    }
}
