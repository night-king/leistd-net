#if (ExternalLogin)
using CompanyName.ProjectName.Api.Auth.Authentication;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Infrastructure.Auth.OAuth.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Google;

namespace CompanyName.ProjectName.UnitTests.Api;

/// <summary>方案同步器按开关与凭据增删方案，不漏掉订阅交界处发生的配置重载。</summary>
public sealed class ExternalSchemeSynchronizerTests
{
    private static readonly string GithubScheme = AuthenticationSchemeNames.ExternalProviderPrefix + ExternalSchemeSynchronizer.Github;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string GoogleScheme = AuthenticationSchemeNames.ExternalProviderPrefix + ExternalSchemeSynchronizer.Google;

    [Fact]
    public async Task A_reload_landing_while_subscribing_is_not_missed()
    {
        // 模拟重载恰好发生在注册监听器的那一刻：监视器已拿到新值，但不会回放给刚注册的监听器。
        // 先同步再订阅时，首次同步看到的是旧值，这次变化就再也没人处理
        var monitor = new ScriptedMonitor(Options(github: false))
        {
            OnSubscribe = monitor => monitor.CurrentValue = Options(github: true)
        };
        using var services = Services();
        using var synchronizer = Create(services, monitor);

        await synchronizer.StartAsync(CancellationToken.None);

        Assert.NotNull(await services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(GithubScheme));
    }

    [Fact]
    public async Task Changes_after_start_add_and_remove_schemes_and_drop_cached_options()
    {
        var monitor = new ScriptedMonitor(Options(github: false));
        using var services = Services();
        using var synchronizer = Create(services, monitor);
        var schemes = services.GetRequiredService<IAuthenticationSchemeProvider>();
        var cache = services.GetRequiredService<IOptionsMonitorCache<GitHubAuthenticationOptions>>();

        await synchronizer.StartAsync(CancellationToken.None);
        Assert.Null(await schemes.GetSchemeAsync(GithubScheme));
        Assert.Null(await schemes.GetSchemeAsync(GoogleScheme));

        monitor.Publish(Options(github: true));
        Assert.NotNull(await schemes.GetSchemeAsync(GithubScheme));
        Assert.Null(await schemes.GetSchemeAsync(GoogleScheme));

        // 换凭据：缓存里的旧选项被作废，下次取用按新值重建
        cache.TryAdd(GithubScheme, new GitHubAuthenticationOptions { ClientId = "stale" });
        monitor.Publish(Options(github: true));
        Assert.True(cache.TryAdd(GithubScheme, new GitHubAuthenticationOptions()));

        monitor.Publish(Options(github: false));
        Assert.Null(await schemes.GetSchemeAsync(GithubScheme));

        await synchronizer.StopAsync(CancellationToken.None);
        monitor.Publish(Options(github: true));
        Assert.Null(await schemes.GetSchemeAsync(GithubScheme));
    }

    [Fact]
    public async Task Disabling_leaves_the_directory_before_the_cached_options_are_dropped()
    {
        // 反过来时，两步之间的新请求会按缺凭据的当前值重建选项、触发官方必填校验
        var monitor = new ScriptedMonitor(Options(github: true));
        using var services = Services();
        var cache = services.GetRequiredService<IOptionsMonitorCache<GitHubAuthenticationOptions>>();
        bool? cachedWhenRemoved = null;
        var schemes = new ScriptedSchemes(services.GetRequiredService<IAuthenticationSchemeProvider>())
        {
            OnRemove = name =>
            {
                if (name == GithubScheme) cachedWhenRemoved = !cache.TryAdd(GithubScheme, new GitHubAuthenticationOptions());
            }
        };
        using var synchronizer = Create(services, monitor, schemes);
        await synchronizer.StartAsync(CancellationToken.None);
        cache.TryAdd(GithubScheme, new GitHubAuthenticationOptions { ClientId = "client" });

        monitor.Publish(Options(github: false));

        Assert.True(cachedWhenRemoved);
        Assert.True(cache.TryAdd(GithubScheme, new GitHubAuthenticationOptions()));
    }

    [Fact]
    public async Task A_change_arriving_mid_synchronization_waits_and_wins()
    {
        // 较早的同步停在登记方案那一步时，较新的停用到达：串行入口让它排在后面、读到的是最新值，目录最终没有该方案。
        // 没有串行保护时，停用先跑完，较早的同步随后把方案加回去
        var monitor = new ScriptedMonitor(Options(github: true));
        using var services = Services();
        var schemes = new ScriptedSchemes(services.GetRequiredService<IAuthenticationSchemeProvider>());
        using var synchronizer = Create(services, monitor, schemes);
        await synchronizer.StartAsync(CancellationToken.None);

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var laterStarted = new TaskCompletionSource();
        schemes.OnAdd = name =>
        {
            if (name != GithubScheme) return;
            schemes.OnAdd = null;
            entered.SetResult();
            // 有界等待只为回归时不挂死；正常由下面放行
            release.Task.Wait(Timeout);
        };
        var earlier = Task.Run(() => monitor.Publish(Options(github: true)));
        Task? later = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            later = Task.Run(() =>
            {
                laterStarted.SetResult();
                monitor.Publish(Options(github: false));
            });
            // 先确认较新的那次已经在执行（发布紧接着就进入同步器），再给它跑完的机会：
            // 有串行保护时它停在锁外，这里等满时限；没有保护时它会在时限内跑完并移除方案
            await laterStarted.Task.WaitAsync(Timeout);
            await Task.WhenAny(later, Task.Delay(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(earlier, later ?? Task.CompletedTask).WaitAsync(Timeout);
        }

        Assert.Null(await services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(GithubScheme));
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddGitHub(GithubScheme, _ => { })
            .AddGoogle(GoogleScheme, _ => { });
        return services.BuildServiceProvider();
    }

    private static ExternalSchemeSynchronizer Create(
        IServiceProvider services, IOptionsMonitor<ExternalAuthOptions> monitor, IAuthenticationSchemeProvider? schemes = null) => new(
        schemes ?? services.GetRequiredService<IAuthenticationSchemeProvider>(),
        monitor,
        services.GetRequiredService<IOptionsMonitorCache<GitHubAuthenticationOptions>>(),
        services.GetRequiredService<IOptionsMonitorCache<GoogleOptions>>(),
        NullLogger<ExternalSchemeSynchronizer>.Instance);

    private static ExternalAuthOptions Options(bool github)
    {
        var options = new ExternalAuthOptions();
        options.Github.Enabled = github;
        options.Github.ClientId = "client";
        options.Github.ClientSecret = "secret";
        return options;
    }

    /// <summary>转发给真实的方案提供器，在增删时插入观察或暂停。</summary>
    private sealed class ScriptedSchemes(IAuthenticationSchemeProvider inner) : IAuthenticationSchemeProvider
    {
        public Action<string>? OnAdd { get; set; }

        public Action<string>? OnRemove { get; init; }

        public bool TryAddScheme(AuthenticationScheme scheme)
        {
            OnAdd?.Invoke(scheme.Name);
            return inner.TryAddScheme(scheme);
        }

        public void AddScheme(AuthenticationScheme scheme) => inner.AddScheme(scheme);

        public void RemoveScheme(string name)
        {
            OnRemove?.Invoke(name);
            inner.RemoveScheme(name);
        }

        public Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => inner.GetAllSchemesAsync();
        public Task<AuthenticationScheme?> GetSchemeAsync(string name) => inner.GetSchemeAsync(name);
        public Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => inner.GetDefaultAuthenticateSchemeAsync();
        public Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => inner.GetDefaultChallengeSchemeAsync();
        public Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync() => inner.GetDefaultForbidSchemeAsync();
        public Task<AuthenticationScheme?> GetDefaultSignInSchemeAsync() => inner.GetDefaultSignInSchemeAsync();
        public Task<AuthenticationScheme?> GetDefaultSignOutSchemeAsync() => inner.GetDefaultSignOutSchemeAsync();
        public Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() => inner.GetRequestHandlerSchemesAsync();
    }

    private sealed class ScriptedMonitor(ExternalAuthOptions initial) : IOptionsMonitor<ExternalAuthOptions>
    {
        private readonly List<Action<ExternalAuthOptions, string?>> _listeners = [];

        public ExternalAuthOptions CurrentValue { get; set; } = initial;

        public Action<ScriptedMonitor>? OnSubscribe { get; init; }

        public ExternalAuthOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ExternalAuthOptions, string?> listener)
        {
            _listeners.Add(listener);
            OnSubscribe?.Invoke(this);
            return new Subscription(() => _listeners.Remove(listener));
        }

        public void Publish(ExternalAuthOptions value)
        {
            CurrentValue = value;
            foreach (var listener in _listeners.ToList())
                listener(value, Microsoft.Extensions.Options.Options.DefaultName);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
#endif
