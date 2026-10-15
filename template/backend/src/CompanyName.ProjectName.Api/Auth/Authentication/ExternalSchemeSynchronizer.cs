#if (ExternalLogin)
using AspNet.Security.OAuth.GitHub;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Infrastructure.Auth.OAuth.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Api.Auth.Authentication;

/// <summary>按外部登录的开关与凭据增删认证方案，让系统设置里的改动在运行期生效。</summary>
/// <remarks>
/// <para>不可用的提供方不能"登记着但凭据为空"：认证中间件每个请求都会初始化全部请求处理型方案，
/// 初始化即取命名选项并触发官方的必填校验，全站请求都会失败。因此按官方 DynamicSchemes 的做法，
/// 用 <see cref="IAuthenticationSchemeProvider"/> 增删方案，并按方案名作废命名选项缓存，下次取用时由官方工厂按新凭据重建。</para>
/// <para>先订阅变更、再做首次同步：反过来时两者之间发生的重载会被漏掉，而配置源对相同数据不再触发重载，
/// 周期刷新也补不回来。首次同步与变更回调走同一把锁、进来后读当前值，较旧的结果不会覆盖较新的。</para>
/// <para>各实例经宿主级设置的周期刷新各自收敛；停用只关闭新的入口，已签发的外部票据在有效期内仍可完成。</para>
/// </remarks>
internal sealed class ExternalSchemeSynchronizer(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitor<ExternalAuthOptions> options,
    IOptionsMonitorCache<GitHubAuthenticationOptions> githubOptions,
    IOptionsMonitorCache<GoogleOptions> googleOptions,
    ILogger<ExternalSchemeSynchronizer> logger) : IHostedService, IDisposable
{
    public const string Github = "github";
    public const string Google = "google";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AuthenticationScheme> _registered = [];
    private IDisposable? _subscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 启动时两个方案都已由官方扩展登记，先记下来，移除后才能原样加回
        foreach (var provider in new[] { Github, Google })
        {
            var name = AuthenticationSchemeNames.ExternalProviderPrefix + provider;
            _registered[name] = await schemes.GetSchemeAsync(name)
                ?? throw new InvalidOperationException($"External authentication scheme '{name}' is not registered.");
        }

        _subscription = options.OnChange((_, _) => Synchronize());
        Synchronize();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void Synchronize()
    {
        lock (_gate)
        {
            var current = options.CurrentValue;
            Apply(Github, "Github", current.Github, githubOptions);
            Apply(Google, "Google", current.Google, googleOptions);
        }
    }

    private void Apply<TOptions>(
        string provider,
        string section,
        ExternalAuthOptions.ProviderOptions settings,
        IOptionsMonitorCache<TOptions> cache)
        where TOptions : class
    {
        var name = AuthenticationSchemeNames.ExternalProviderPrefix + provider;
        if (settings.IsAvailable)
        {
            // 凭据可能换过：先作废缓存再确保登记，新请求取到的是按当前值重建的选项
            cache.TryRemove(name);
            schemes.TryAddScheme(_registered[name]);
            return;
        }

        // 先移出目录再作废缓存（同官方 DynamicSchemes）：反过来时，两步之间的新请求会按缺凭据的当前值重建选项、触发必填校验
        schemes.RemoveScheme(name);
        cache.TryRemove(name);
        if (settings.Enabled)
        {
            var missing = new List<string>(2);
            if (!settings.HasClientId) missing.Add($"{ExternalAuthOptions.SectionName}:{section}:{nameof(settings.ClientId)}");
            if (!settings.HasClientSecret) missing.Add($"{ExternalAuthOptions.SectionName}:{section}:{nameof(settings.ClientSecret)}");
            logger.LogWarning(
                "External login provider {Provider} is enabled but unavailable: missing {MissingKeys}.",
                provider, string.Join(", ", missing));
        }
    }
}
#endif
