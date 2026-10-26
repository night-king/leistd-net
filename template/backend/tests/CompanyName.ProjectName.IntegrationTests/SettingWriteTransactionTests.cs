using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.EventBus.EventHandlers;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Extensions;
using Leistd.Settings.Definitions;
using Leistd.Settings.EntityFrameworkCore.Entities;
using Leistd.Settings.Events;
using Leistd.Settings.Management;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Leistd.UnitOfWork.Events;
using Leistd.UnitOfWork.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>设置写入在自己的事务里提交：调用方外层回滚不撤销它，提交前失败则整体回滚。</summary>
/// <remarks>
/// 在真实 PostgreSQL 上验证默认存储确实用了独立的连接与事务，替身证明不了这一点。
/// 每条用例用自己的用户，结果从全新的作用域（新连接）读回。
/// </remarks>
public sealed class SettingWriteTransactionTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    // 外层工作单元自己写的标记行；存储不校验定义，用本类独有的名称
    private const string MarkerName = "IntegrationTests.OuterMarker";

    /// <summary>外层已保存未提交时写入设置，随后外层回滚：设置仍在，外层的标记不在，提交后处理器恰好执行一次。</summary>
    [Fact]
    public async Task A_setting_written_inside_an_outer_transaction_survives_its_rollback()
    {
        var userId = Guid.NewGuid().ToString("N");
        var afterCommit = new AfterCommitCounter(userId);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ISettingDefinitionProvider, SampleUserSetting>().AddSingleton<IEventHandler<SettingChangedEvent>>(afterCommit)));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            using var outer = services.GetRequiredService<IUnitOfWorkManager>()
                .Begin(new UnitOfWorkOptions { IsTransactional = true }, requiresNew: true);
            var dbContext = await services.GetRequiredService<IDbContextProvider<MyProjectDbContext>>().GetDbContextAsync();
            dbContext.Set<SettingRecord>().Add(new SettingRecord
            {
                UserId = userId,
                ScopeKey = services.GetRequiredService<ICurrentTenant>().ScopeKey($"u:{userId}"),
                Name = MarkerName,
                Value = "outer"
            });
            await outer.SaveChangesAsync();

            await services.GetRequiredService<ISettingManager>()
                .SetAsync(SampleUserSetting.Name, "Asia/Tokyo", SettingScopes.User, userId);
            Assert.Equal(1, afterCommit.Count);

            await outer.RollbackAsync();
        }

        var rows = await RowsAsync(host, userId);
        Assert.Equal("Asia/Tokyo", Assert.Single(rows, row => row.Name == SampleUserSetting.Name).Value);
        Assert.DoesNotContain(rows, row => row.Name == MarkerName);
        Assert.Equal(1, afterCommit.Count);
    }

    /// <summary>存储已保存、事务尚未提交时失败（提交前处理器抛出）：设置被回滚，提交后处理器不执行。</summary>
    [Fact]
    public async Task A_failure_after_the_store_saved_but_before_commit_rolls_the_setting_back()
    {
        var userId = Guid.NewGuid().ToString("N");
        var journal = new List<string>();
        var afterCommit = new AfterCommitCounter(userId);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISettingDefinitionProvider, SampleUserSetting>();
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(new SavedSettingRecorder(userId, journal)));
            services.AddSingleton<IEventHandler<SettingChangedEvent>>(new FailingBeforeCommit(userId, journal));
            services.AddSingleton<IEventHandler<SettingChangedEvent>>(afterCommit);
        }));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                .GetRequiredService<ISettingManager>()
                .SetAsync(SampleUserSetting.Name, "Asia/Tokyo", SettingScopes.User, userId));
            Assert.Equal(FailingBeforeCommit.Message, error.Message);
        }

        // 失败点在存储的保存之后：不是校验失败那种什么都没写
        Assert.Equal(["saved", "before-commit"], journal);
        Assert.Empty(await RowsAsync(host, userId));
        Assert.Equal(0, afterCommit.Count);
    }

    /// <summary>提交后处理器抛错：值已提交，写入方收到这个异常（不是回滚），处理器只执行一次，锁随之释放。</summary>
    /// <remarks>
    /// 带外层工作单元时同样如此：事件属于设置自己的提交，在写入返回前就已分发，不会推迟到外层提交（外层随后回滚也不影响）。
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_after_commit_handler_surfaces_after_the_setting_committed(bool insideOuterUnitOfWork)
    {
        var userId = Guid.NewGuid().ToString("N");
        var failing = new FailingAfterCommit(userId);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ISettingDefinitionProvider, SampleUserSetting>().AddSingleton<IEventHandler<SettingChangedEvent>>(failing)));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            using var outer = insideOuterUnitOfWork
                ? services.GetRequiredService<IUnitOfWorkManager>().Begin(new UnitOfWorkOptions { IsTransactional = true }, requiresNew: true)
                : null;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<ISettingManager>()
                .SetAsync(SampleUserSetting.Name, "Asia/Tokyo", SettingScopes.User, userId));
            Assert.Equal(FailingAfterCommit.Message, error.Message);
            Assert.Equal(1, failing.Calls);

            if (outer is not null)
            {
                await outer.RollbackAsync();
            }
        }

        Assert.Equal("Asia/Tokyo", Assert.Single(await RowsAsync(host, userId)).Value);
        Assert.Equal(1, failing.Calls);

        // 宿主上下文的设置锁已释放：下一次写入不会被这次失败挂住
        await using var handle = await host.Services.GetRequiredService<IDistributedLock>()
            .TryLockAsync("settings:host", TimeSpan.Zero);
        Assert.NotNull(handle);
    }

    private static async Task<List<SettingRecord>> RowsAsync(WebApplicationFactory<Program> host, string userId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Set<SettingRecord>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(record => record.UserId == userId)
            .ToListAsync();
    }

    // 未标注阶段即提交后处理器
    // 只验证设置组件的事务语义，与具体业务设置无关：各形态都有这一项用户层设置（资源服务没有用户层的业务设置）
    private sealed class SampleUserSetting : ISettingDefinitionProvider
    {
        public const string Name = "IntegrationTests.UserSample";

        public void Define(ISettingDefinitionContext context)
            => context.Add(Name, defaultValue: null, scopes: SettingScopes.User, displayName: "Sample", group: "IntegrationTests");
    }

    private sealed class AfterCommitCounter(string userId) : IEventHandler<SettingChangedEvent>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task HandleAsync(SettingChangedEvent @event, CancellationToken cancellationToken = default)
        {
            if (@event.UserId == userId)
            {
                Interlocked.Increment(ref _count);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingAfterCommit(string userId) : IEventHandler<SettingChangedEvent>
    {
        public const string Message = "Injected failure after commit";

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task HandleAsync(SettingChangedEvent @event, CancellationToken cancellationToken = default)
        {
            if (@event.UserId != userId)
            {
                return Task.CompletedTask;
            }

            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException(Message);
        }
    }

    [UnitOfWorkEventHandler(UnitOfWorkPhase.BeforeCommit)]
    private sealed class FailingBeforeCommit(string userId, List<string> journal) : IEventHandler<SettingChangedEvent>
    {
        public const string Message = "Injected failure before commit";

        public Task HandleAsync(SettingChangedEvent @event, CancellationToken cancellationToken = default)
        {
            if (@event.UserId != userId)
            {
                return Task.CompletedTask;
            }

            lock (journal)
            {
                journal.Add("before-commit");
            }

            throw new InvalidOperationException(Message);
        }
    }

    // 记下本用户的设置行已成功保存（进入事务、尚未提交）
    private sealed class SavedSettingRecorder(string userId, List<string> journal) : SaveChangesInterceptor
    {
        // 同一上下文的保存前后两次回调成对出现；本用例只有一个写入方
        private volatile bool _pending;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _pending = eventData.Context?.ChangeTracker.Entries<SettingRecord>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.UserId == userId) == true;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (_pending)
            {
                lock (journal)
                {
                    journal.Add("saved");
                }
            }

            return ValueTask.FromResult(result);
        }
    }
}
