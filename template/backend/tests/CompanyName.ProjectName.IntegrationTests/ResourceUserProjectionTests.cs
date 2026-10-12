#if (!LocalIdentity)
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Api.Middlewares;
using CompanyName.ProjectName.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>资源服务形态：本地用户行由令牌投影而来，不由人手工创建。</summary>
/// <remarks>
/// 用户主键取签发方的 <c>sub</c>，角色授予使用同一主键，避免主体不匹配的无效授权。
/// </remarks>
public sealed class ResourceUserProjectionTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    /// <summary>首次持令牌访问就建行，且主键等于令牌里的 sub。</summary>
    [Fact]
    public async Task A_first_authenticated_request_projects_the_subject_into_a_local_row()
    {
        var subjectId = Guid.CreateVersion7();
        using var session = factory.CreateResourceSession(subjectId, ProjectWebApplicationFactory.NewTenantId());

        var response = await session.Client.GetAsync("/api/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await FindUserAsync(subjectId);
        Assert.NotNull(user);
        // 主键就是 sub：这正是"抄错一位就永远不生效"所在的那个等式，机器来填就不可能错
        Assert.Equal(subjectId, user.Id);
    }

    /// <summary>重复访问不重复建行。</summary>
    [Fact]
    public async Task Repeated_requests_do_not_create_a_second_row()
    {
        var subjectId = Guid.CreateVersion7();
        using var session = factory.CreateResourceSession(subjectId, ProjectWebApplicationFactory.NewTenantId());

        await session.Client.GetAsync("/api/health/live");
        await session.Client.GetAsync("/api/health/live");

        Assert.Equal(1, await CountUsersAsync(subjectId));
    }

    /// <summary>令牌没带显示名时，重复访问不再写库：新建与比较用同一个回落值（用户名）。</summary>
    /// <remarks>投影在每个已认证请求上都会走到，口径不一致会让每个请求都多一次 UPDATE。</remarks>
    [Fact]
    public async Task Repeated_requests_without_a_display_name_do_not_rewrite_the_row()
    {
        var subjectId = Guid.CreateVersion7();
        var updates = new UserUpdateCounter(subjectId);
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(updates))));
        using var session = ProjectWebApplicationFactory.CreateResourceSession(host, subjectId, ProjectWebApplicationFactory.NewTenantId());

        await session.Client.GetAsync("/api/health/live");
        await session.Client.GetAsync("/api/health/live");
        await session.Client.GetAsync("/api/health/live");

        Assert.Equal(0, updates.Count);
        await using var scope = host.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Users
            .IgnoreQueryFilters().SingleAsync(u => u.Id == subjectId);
        Assert.Equal(user.Username, user.DisplayName);
    }

    /// <summary>未认证的请求不建行：没有主体就没什么可投影的。</summary>
    [Fact]
    public async Task An_anonymous_request_projects_nothing()
    {
        var before = await CountAllUsersAsync();

        using var client = ProjectWebApplicationFactory.CreateProjectClient(factory);
        await client.GetAsync("/api/health/live");

        Assert.Equal(before, await CountAllUsersAsync());
    }

    /// <summary>当前主体的响应契约：字段集合与缺省值固定；超管标记与角色只取本服务的授权数据。</summary>
    /// <remarks>令牌里没有用户名与邮箱时为空串，显示名与租户为空时不输出（JSON 忽略 null）。</remarks>
    [Fact]
    public async Task Current_user_reports_local_super_admin_and_roles_with_a_stable_shape()
    {
        var subjectId = Guid.CreateVersion7();
        using var session = factory.CreateResourceSession(subjectId, tenantId: null);

        var before = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(
            ["email", "id", "isEmailVerified", "isSuperAdmin", "roles", "username"],
            before.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(subjectId, before.GetProperty("id").GetGuid());
        Assert.Equal("", before.GetProperty("username").GetString());
        Assert.Equal("", before.GetProperty("email").GetString());
        Assert.False(before.GetProperty("isEmailVerified").GetBoolean());
        Assert.False(before.GetProperty("isSuperAdmin").GetBoolean());
        Assert.Empty(before.GetProperty("roles").EnumerateArray());

        string roleName;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var user = await db.Set<User>().SingleAsync(user => user.Id == subjectId);
            user.MarkAsSuperAdmin();
            var role = await db.Set<Role>().OrderBy(role => role.Name).FirstAsync();
            roleName = role.Name;
            user.AssignRoles([role.Id]);
            await db.SaveChangesAsync();
        }

        var after = await session.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.True(after.GetProperty("isSuperAdmin").GetBoolean());
        Assert.Equal([roleName], after.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    /// <summary>同一个 sub 的并发首访：输的一方重试后投影成功，只建一行，两个请求都成功。</summary>
    /// <remarks>
    /// <para>工作单元在提交时才报告撞键，重试须覆盖提交边界。</para>
    /// <para>屏障让两个请求同时提交新行；响应码和行数无法单独证明重试，
    /// 还须断言没有投影失败警告，因为投影失败也会放行请求。</para>
    /// </remarks>
    [Fact]
    public async Task Concurrent_first_requests_for_one_subject_all_succeed_and_create_one_row()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        var race = new FirstInsertRace(subjectId, participants: 2);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(race));
            // 宿主用 Serilog 接管了日志工厂，不换回标准工厂，"没有记下警告"就恒成立、证伪不了
            services.RemoveAll<ILoggerFactory>();
            services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Warning)
                // 操作记录写日志时，启动期检查要求该类别在 Information 可用
                .AddFilter("Leistd.OperationRecords", LogLevel.Information));
        }));
        using var first = ProjectWebApplicationFactory.CreateResourceSession(host, subjectId, tenantId);
        using var second = ProjectWebApplicationFactory.CreateResourceSession(host, subjectId, tenantId);

        var responses = await Task.WhenAll(
            first.Client.GetAsync("/api/health/live"),
            second.Client.GetAsync("/api/health/live"));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        foreach (var response in responses) response.Dispose();
        Assert.True(race.Released, "The two requests did not reach commit together; no race occurred, so the test proves nothing");
        // 输的一方第一次提交撞键时 EF 与工作单元会各记一条错误，那是竞争本身；要看的是投影最终有没有失败
        Assert.DoesNotContain(
            host.Services.GetFakeLogCollector().GetSnapshot(),
            record => record.Category?.EndsWith(nameof(ResourceUserProvisioningMiddleware), StringComparison.Ordinal) == true
                && record.Message.Contains(subjectId.ToString(), StringComparison.Ordinal));
        Assert.Equal(1, await CountUsersAsync(subjectId, host.Services));
    }

    /// <summary>投影持续失败：只重试一次，记下警告后放行请求，不留下用户行。</summary>
    /// <remarks>投影不是安全闸门：拦下请求只会让一个可预见的写入故障把该用户的每个请求都变成 500。</remarks>
    [Fact]
    public async Task A_projection_that_keeps_failing_is_retried_once_then_logged_and_the_request_proceeds()
    {
        var subjectId = Guid.CreateVersion7();
        var failing = new FailingUserInsert(subjectId);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.ConfigureDbContext<MyProjectDbContext>(options => options.AddInterceptors(failing));
            services.RemoveAll<ILoggerFactory>();
            services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Warning)
                // 操作记录写日志时，启动期检查要求该类别在 Information 可用
                .AddFilter("Leistd.OperationRecords", LogLevel.Information));
        }));
        using var session = ProjectWebApplicationFactory.CreateResourceSession(host, subjectId, ProjectWebApplicationFactory.NewTenantId());

        using var response = await session.Client.GetAsync("/api/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // 两次：首次加一次重试；不重试是 1，重试循环会大于 2
        Assert.Equal(2, failing.Attempts);
        Assert.Contains(
            host.Services.GetFakeLogCollector().GetSnapshot(),
            record => record.Category?.EndsWith(nameof(ResourceUserProvisioningMiddleware), StringComparison.Ordinal) == true
                && record.Message.Contains(subjectId.ToString(), StringComparison.Ordinal));
        Assert.Equal(0, await CountUsersAsync(subjectId, host.Services));
    }

    // 数这个主体的用户行被改写了几次
    private sealed class UserUpdateCounter(Guid subjectId) : SaveChangesInterceptor
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<User>()
                    .Any(entry => entry.State == EntityState.Modified && entry.Entity.Id == subjectId) == true)
            {
                Interlocked.Increment(ref count);
            }

            return ValueTask.FromResult(result);
        }
    }

    // 只拦插入这个主体的提交，其余写入照常
    private sealed class FailingUserInsert(Guid subjectId) : SaveChangesInterceptor
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var inserting = eventData.Context?.ChangeTracker.Entries<User>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.Id == subjectId) == true;
            if (inserting)
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("Simulated projection failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    // 两个上下文都要提交同一个新用户行时才一起放行：两边都已读到"不存在"，提交时必有一方撞键
    private sealed class FirstInsertRace(Guid subjectId, int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;

        public bool Released => released.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var inserting = eventData.Context?.ChangeTracker.Entries<User>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.Id == subjectId) == true;
            if (inserting && !released.Task.IsCompleted)
            {
                if (Interlocked.Increment(ref arrived) >= participants)
                    released.TrySetResult();
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private async Task<User?> FindUserAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return await db.Set<User>().IgnoreQueryFilters().FirstOrDefaultAsync(user => user.Id == id);
    }

    private Task<int> CountUsersAsync(Guid id) => CountUsersAsync(id, factory.Services);

    private static async Task<int> CountUsersAsync(Guid id, IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return await db.Set<User>().IgnoreQueryFilters().CountAsync(user => user.Id == id);
    }

    private async Task<int> CountAllUsersAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        return await db.Set<User>().IgnoreQueryFilters().CountAsync();
    }
}
#endif
