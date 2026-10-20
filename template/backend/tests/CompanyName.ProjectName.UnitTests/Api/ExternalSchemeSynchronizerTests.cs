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

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        schemes.OnAdd = name =>
        {
            if (name != GithubScheme) return;
            schemes.OnAdd = null;
            entered.SetResult();
            // 有界等待只为回归时不挂死；正常由下面放行
            release.Task.Wait(Timeout);
        };
        var earlier = Task.Run(() => monitor.Publish(Options(github: true)));
        var later = new PublishingThread(() => monitor.Publish(Options(github: false)));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            later.Start();
            // 屏障：较新的那次要么已经跑完（没有串行保护），要么挡在串行入口上（线程进入阻塞等待）。
            // 发布到进入同步器之间没有别的等待，所以线程一阻塞就是停在锁外；等待有上限，只防挂死
            await later.FinishedOrBlockedAsync(Timeout);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(earlier, later.Completion).WaitAsync(Timeout);
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

    /// <summary>在专用线程上发布一次变更，便于观察它是否挡在同步器的串行入口上。</summary>
    private sealed class PublishingThread
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private bool _started;

        public PublishingThread(Action publish)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    publish();
                    _completion.SetResult();
                }
                catch (Exception exception)
                {
                    _completion.SetException(exception);
                }
            }) { IsBackground = true };
        }

        /// <summary>未启动时视为已完成：用例在启动前失败也不在清理里等满时限。</summary>
        public Task Completion => _started ? _completion.Task : Task.CompletedTask;

        public void Start()
        {
            _started = true;
            _thread.Start();
        }

        public async Task FinishedOrBlockedAsync(TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            while (!_completion.Task.IsCompleted && (_thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
                await Task.Delay(TimeSpan.FromMilliseconds(1), deadline.Token);
        }
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
