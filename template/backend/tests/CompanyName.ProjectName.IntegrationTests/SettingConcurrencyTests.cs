#if (LocalIdentity)
using System.Data;
using System.Net;
using System.Net.Http.Json;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Extensions;
using Leistd.Settings.Definitions;
using Leistd.Settings.EntityFrameworkCore.Entities;
using Leistd.Settings.Stores;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Leistd.UnitOfWork.Options;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>设置首次写入撞上并发同键插入时，在真实 PostgreSQL 上的恢复。</summary>
/// <remarks>
/// 两边都先读到"没有这一行"再插入，后提交的一方撞唯一索引。存储在同一上下文里撤下本次插入、
/// 回读到赢家行后改写，因此不会把并发写入报成 500。
/// </remarks>
public sealed class SettingConcurrencyTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    /// <summary>同一用户的两个会话同时首次保存同一项设置：都成功，库里只留一行。</summary>
    [Fact]
    public async Task Concurrent_first_writes_of_one_user_setting_both_succeed_and_leave_one_row()
    {
        var race = new FirstSettingInsertRace(SettingConstant.Display.TimeZone, participants: 2);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(race))));
        using var first = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var second = await ProjectWebApplicationFactory.LoginAsync(host, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        var responses = await Task.WhenAll(
            first.Client.PutAsJsonAsync("/api/v1/settings/current-user",
                new { Name = SettingConstant.Display.TimeZone, Value = "Asia/Shanghai" }),
            second.Client.PutAsJsonAsync("/api/v1/settings/current-user",
                new { Name = SettingConstant.Display.TimeZone, Value = "Asia/Tokyo" }));

        Assert.True(race.Released, "The two writes did not reach commit together; no race occurred, so the test proves nothing");
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        foreach (var response in responses) response.Dispose();

        await using var scope = host.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Set<SettingRecord>()
            .IgnoreQueryFilters()
            .Where(record => record.Name == SettingConstant.Display.TimeZone)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Contains(row.Value, new[] { "Asia/Shanghai", "Asia/Tokyo" });
    }

    /// <summary>
    /// 外层 READ COMMITTED 事务里首插撞上另一连接已提交的赢家：恢复后本次值落库，宿主同批待写的实体随外层事务一起提交。
    /// </summary>
    /// <remarks>
    /// 交错是确定的：本次插入提交前，另一连接在自己的工作单元里写入同键赢家并提交。首次保存因此必然撞唯一索引，
    /// EF 回滚到自动保存点后事务仍可用，存储在同一事务里读到赢家并改写成本次值。
    /// 按写入顺序本次在后，值必须是本次的，"发现赢家就直接返回"的回归会在这里失败。
    /// </remarks>
    [Fact]
    public async Task Interleaved_first_write_inside_an_outer_transaction_recovers_and_commits_host_entities()
    {
        // 存储不校验设置定义；用本用例独有的名称与用户，避免与同类用例的行相互干扰
        const string name = "IntegrationTests.InterleavedWrite";
        const string pendingName = "IntegrationTests.HostPending";
        var userId = Guid.NewGuid().ToString("N");
        var winner = new CommittedWinnerBeforeInsert(name, userId, "winner");
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(winner))));
        winner.Services = host.Services;

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var provider = scope.ServiceProvider;
            using var unitOfWork = provider.GetRequiredService<IUnitOfWorkManager>()
                .Begin(new UnitOfWorkOptions { IsTransactional = true, IsolationLevel = IsolationLevel.ReadCommitted }, requiresNew: true);

            // 宿主在同一上下文里先登记一个待写实体，随存储的保存一起进入首次（失败的）保存
            var dbContext = await provider.GetRequiredService<IDbContextProvider<MyProjectDbContext>>().GetDbContextAsync();
            dbContext.Set<SettingRecord>().Add(new SettingRecord
            {
                UserId = userId,
                ScopeKey = provider.GetRequiredService<ICurrentTenant>().ScopeKey($"u:{userId}"),
                Name = pendingName,
                Value = "pending"
            });

            await provider.GetRequiredService<ISettingStore>().SetAsync(name, "mine", SettingScopes.User, userId);
            await unitOfWork.CompleteAsync();
        }

        Assert.True(winner.Committed, "The winner did not commit before this insert; no interleaving occurred, so the test proves nothing");
        Assert.Equal(1, winner.LoserFailures);

        await using var verify = host.Services.CreateAsyncScope();
        var rows = await verify.ServiceProvider.GetRequiredService<MyProjectDbContext>().Set<SettingRecord>()
            .IgnoreQueryFilters()
            .Where(record => record.UserId == userId)
            .ToListAsync();
        Assert.Equal("mine", Assert.Single(rows, record => record.Name == name).Value);
        Assert.Equal("pending", Assert.Single(rows, record => record.Name == pendingName).Value);
    }

    // 本次插入提交前，在另一连接（新作用域、新工作单元）写入同键赢家并提交；只触发一次，并记下本次保存失败的次数
    private sealed class CommittedWinnerBeforeInsert(string name, string userId, string value) : SaveChangesInterceptor
    {
        private DbContext? loser;
        private int failures;

        public IServiceProvider? Services { get; set; }

        public bool Committed { get; private set; }

        public int LoserFailures => Volatile.Read(ref failures);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var inserting = eventData.Context?.ChangeTracker.Entries<SettingRecord>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.Name == name) == true;
            if (inserting && Interlocked.CompareExchange(ref loser, eventData.Context, null) is null)
            {
                // 不带出外层的环境工作单元：赢家走自己的连接与事务，提交后才放行本次保存
                Task commit;
                using (ExecutionContext.SuppressFlow())
                {
                    commit = Task.Run(CommitWinnerAsync, cancellationToken);
                }

                await commit;
                Committed = true;
            }

            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(eventData.Context, loser))
            {
                Interlocked.Increment(ref failures);
            }

            return Task.CompletedTask;
        }

        private async Task CommitWinnerAsync()
        {
            await using var scope = Services!.CreateAsyncScope();
            using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
            await scope.ServiceProvider.GetRequiredService<ISettingStore>().SetAsync(name, value, SettingScopes.User, userId);
            await unitOfWork.CompleteAsync();
        }
    }

    // 两个上下文都要插入这项设置时才一起放行：两边都已读到"不存在"，提交时必有一方撞键
    private sealed class FirstSettingInsertRace(string name, int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;

        public bool Released => released.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var inserting = eventData.Context?.ChangeTracker.Entries<SettingRecord>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.Name == name) == true;
            if (inserting && !released.Task.IsCompleted)
            {
                if (Interlocked.Increment(ref arrived) >= participants)
                    released.TrySetResult();
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }
}
#endif
