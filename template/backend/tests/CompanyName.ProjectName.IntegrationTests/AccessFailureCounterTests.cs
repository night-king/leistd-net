#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>认证失败计数器自身的契约：提交独立于调用方的事务。</summary>
/// <remarks>
/// <para>口令登录、两步验证登录、再认证三条路径都靠它累计失败。三处此前各写一份
/// "累计、写入、然后紧接着抛出"，正确性都挂在同一个隐含前提上——调用方不在工作单元内，
/// 写入才会即时落库。谁给其中任何一处加了 <c>[UnitOfWork]</c>，计数就随那次抛出一并回滚，
/// 而接口返回一模一样、界面毫无异常，只有真被爆破时才发现锁定从未生效。</para>
/// <para><b>这组用例只钉住计数器这一个组件</b>：它的提交与调用方的事务无关。
/// 至于那三条路径确实走了计数器，靠的是"全仓只有这一份实现"这个结构，
/// <b>没有测试覆盖</b>——从 HTTP 端点做不到"自己开一个工作单元再丢弃"，无法在那一层验证。
/// 再认证那条经守卫调用（见 <c>ReauthenticationLockoutTests</c>），能额外守住守卫的用法。</para>
/// </remarks>
public sealed class AccessFailureCounterTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "CounterTests!Passw0rd";

    [Fact]
    public async Task The_count_is_committed_independently_of_the_calling_transaction()
    {
        var userId = await CreateUserAsync("counter_uow");

        using (var scope = factory.Services.CreateScope())
        {
            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            var counter = scope.ServiceProvider.GetRequiredService<IAccessFailureCounter>();

            using var unitOfWork = unitOfWorkManager.Begin();
            var outcome = await counter.CountAsync(userId);
            Assert.NotNull(outcome.User);

            // 不 CompleteAsync：调用方这一侧的写入在这里全部丢弃
        }

        Assert.Equal(1, await AccessFailedCountAsync(userId));
    }

    /// <summary>行已不存在时不计数。</summary>
    [Fact]
    public async Task A_deleted_row_is_not_counted()
    {
        using var scope = factory.Services.CreateScope();
        var counter = scope.ServiceProvider.GetRequiredService<IAccessFailureCounter>();

        var outcome = await counter.CountAsync(Guid.CreateVersion7());

        Assert.Null(outcome.User);
        Assert.False(outcome.LockoutTriggered);
    }

    private async Task<Guid> CreateUserAsync(string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var admin = await ProjectWebApplicationFactory.LoginAsync(
            factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return (await db.Set<User>().IgnoreQueryFilters()
            .SingleAsync(user => user.Username == username)).Id;
    }

    private async Task<int> AccessFailedCountAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return (await db.Set<User>().IgnoreQueryFilters()
            .SingleAsync(user => user.Id == userId)).Lockout.AccessFailedCount;
    }
}
#endif
