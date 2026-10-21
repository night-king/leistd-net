using Leistd.Data.Connections;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary>共享的远端解析任务不能用发起者的作用域：发起者的请求结束、作用域释放后，搭车者照样拿到结果。</summary>
/// <remarks>
/// 共享任务被刻意设计成比发起者活得更久（发起者取消不取消它），它就不能持有发起者作用域里的实例——
/// 表现是负载一上来偶发 <see cref="ObjectDisposedException"/>，几乎无法定位。
/// 远端存储注册为 Scoped 且"释放后即不可用"，把这条从注释变成断言。
/// </remarks>
public sealed class RemoteSharedResolutionScopeTests
{
    [Fact]
    public async Task Shared_resolution_survives_disposal_of_the_initiating_scope()
    {
        var tenant = new SettableCurrentTenant { Id = Guid.NewGuid() };
        var source = new ScriptedRemoteSource { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        source.Lookup = (id, name) => ScriptedRemoteSource.Hit(id, name, "Host=tenant");

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=shared",
            ["Leistd:MultiTenancy:Routing:CacheLifetime"] = "00:05:00"
        }).Build());
        services.AddSingleton<ICurrentTenant>(tenant);
        services.AddScoped<ITenantConnectionConfigurationStore>(_ => new ScopeBoundStore(source));
        services.AddRemoteTenantConnectionResolution();
        await using var provider = services.BuildServiceProvider();

        Task<string> initiator;
        Task<string> joiner;
        var initiatorScope = provider.CreateScope();
        try
        {
            initiator = initiatorScope.ServiceProvider.GetRequiredService<IConnectionStringResolver>().ResolveAsync("Default");
            await WaitForCallAsync(source);

            initiatorScope.Dispose();

            using var joinerScope = provider.CreateScope();
            joiner = joinerScope.ServiceProvider.GetRequiredService<IConnectionStringResolver>().ResolveAsync("Default");
        }
        finally
        {
            source.Gate.TrySetResult();
        }

        Assert.Equal("Host=tenant", await initiator.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Host=tenant", await joiner.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, source.RuntimeCalls);
    }

    private static async Task WaitForCallAsync(ScriptedRemoteSource source)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref source.RuntimeCalls) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Yield();
        }
    }

    private sealed class ScopeBoundStore(ScriptedRemoteSource inner) : ITenantConnectionConfigurationStore, IDisposable
    {
        private bool _disposed;

        public void Dispose() => _disposed = true;

        public Task<TenantConnectionLookupResult?> FindAsync(Guid tenantId, string name, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return inner.FindAsync(tenantId, name, cancellationToken);
        }

        public Task<TenantMigrationConnectionListResult> GetListAsync(string name, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return inner.GetListAsync(name, cancellationToken);
        }
    }
}
