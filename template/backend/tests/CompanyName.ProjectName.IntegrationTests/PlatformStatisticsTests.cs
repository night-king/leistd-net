using System.Net;
using System.Text.Json;
using CompanyName.ProjectName.Application.Permissions.Provider;
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Statistics.AppServices;
using CompanyName.ProjectName.Application.Statistics.Dtos;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
#endif
using Leistd.Authorization.Constants;
#if (!LocalIdentity)
using Leistd.Authorization.Grants;
#endif
#if (LocalIdentity && IncludeOperationRecords)
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.EntityFrameworkCore.Retention;
using Leistd.OperationRecords.Models;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
using System.Net.Http.Json;
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.MultiTenancy.Stores;
#endif
#if (!LocalIdentity)
using Leistd.MultiTenancy.Context;
#endif
#if (LocalIdentity)
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
#endif
using Microsoft.Extensions.DependencyInjection;
#if (LocalIdentity)
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
#endif

namespace CompanyName.ProjectName.IntegrationTests;

#if (LocalIdentity)
/// <summary>平台统计：权限、输入验证、按形态裁剪的字段、UTC 日区间与当前上下文的范围。</summary>
public sealed class PlatformStatisticsTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    private const string Url = "/api/v1/platform/statistics";

    /// <summary>没有仪表盘权限答 403；授予后可读，内置管理员角色在播种时已带上它。</summary>
    [Fact]
    public async Task Statistics_require_the_dashboard_permission_which_the_admin_role_is_seeded_with()
    {
        using var anonymous = Factory.CreateProjectClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url)).StatusCode);

        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var member = await CreateUserAsync(superAdmin.Client);
        using var memberSession = await Factory.LoginAsync(member.Username, TestPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberSession.Client.GetAsync(Url)).StatusCode);

        await GrantAsync(PermissionGrantProviderNames.User, member.Id, PermissionConstant.Dashboard.Default);
        Assert.Equal(HttpStatusCode.OK, (await memberSession.Client.GetAsync(Url)).StatusCode);

        var administrator = await CreateUserAsync(superAdmin.Client, [await GetAdminRoleIdAsync(Factory.Services)]);
        using var adminSession = await Factory.LoginAsync(administrator.Username, TestPassword);
        Assert.Equal(HttpStatusCode.OK, (await adminSession.Client.GetAsync(Url)).StatusCode);
    }

    /// <summary>区间只收 7 或 30：其他值与无法解析的值都是字段 <c>days</c> 的 400。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("14")]
    [InlineData("-7")]
    [InlineData("abc")]
    public async Task Days_other_than_7_or_30_are_a_field_error(string days)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        using var response = await superAdmin.Client.GetAsync($"{Url}?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == "days");
    }

    /// <summary>字段按形态裁剪：不存在的指标不出现在响应里，而不是给 0。</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    public async Task Response_carries_the_fields_this_service_can_answer(int days)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        using var body = JsonDocument.Parse(await superAdmin.Client.GetStringAsync($"{Url}?days={days}"));
        var root = body.RootElement;

        Assert.Equal(days, root.GetProperty("days").GetInt32());
        Assert.True(root.GetProperty("totalUsers").GetProperty("value").GetInt32() >= 1);
        Assert.True(root.GetProperty("newUsers").TryGetProperty("previous", out _));
        var attention = root.GetProperty("attention");
        Assert.True(attention.TryGetProperty("lockedUsers", out _));
        Assert.True(attention.TryGetProperty("adminsWithoutTwoFactor", out _));
        Assert.True(attention.TryGetProperty("unconfirmedEmailUsers", out _));
#if (IncludeMultiTenancy)
        // 宿主上下文才统计停用的租户
        Assert.True(attention.TryGetProperty("disabledTenants", out _));
#else
        Assert.False(attention.TryGetProperty("disabledTenants", out _));
#endif
#if (IncludeOperationRecords)
        Assert.True(root.GetProperty("activeUsers").TryGetProperty("previous", out _));
        Assert.True(root.TryGetProperty("failedLogins", out _));
        Assert.Equal(days, root.GetProperty("loginTrend").GetArrayLength());
#else
        // 没有登录记录：活跃用户退回最近登录时间，只有当前值；登录趋势与失败数不存在
        Assert.True(root.GetProperty("activeUsers").TryGetProperty("value", out _));
        Assert.False(root.GetProperty("activeUsers").TryGetProperty("previous", out _));
        Assert.False(root.TryGetProperty("failedLogins", out _));
        Assert.False(root.TryGetProperty("loginTrend", out _));
#endif
    }

    /// <summary>
    /// 区间按 UTC 日划分：本区间是含今天在内往前 7 个整日，上一区间紧邻其前；两端恰好落在边界上的时刻归后一个区间。
    /// 上一区间没有数据时上一区间值为 0。
    /// </summary>
    /// <remarks>独立宿主与库：断言的是精确计数，同类其他用例写入的用户与登录不能混进来。</remarks>
    [Fact]
    public async Task Intervals_are_utc_days_and_the_previous_interval_is_the_one_right_before()
    {
        var today = DateTime.UtcNow.Date;
        var now = today.AddSeconds(30);
        var currentStart = today.AddDays(-6);
        var previousStart = today.AddDays(-13);
        // 时钟只能往前拨：宿主在两个区间之前启动（内置管理员建在此刻），再依次拨到各个边界
        var clock = new FakeTimeProvider(new DateTimeOffset(previousStart.AddMinutes(-1)));
        using var isolated = new ProjectWebApplicationFactory();
        await using var host = isolated.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));

        var existingUsers = (await GetStatisticsAsync(host.Services, 7)).TotalUsers.Value;

        // 用户的创建时间取自时钟：拨到各个边界上建
        foreach (var createdAt in new[] { previousStart.AddSeconds(-1), previousStart, currentStart.AddSeconds(-1), currentStart, now })
        {
            clock.SetUtcNow(new DateTimeOffset(createdAt));
#if (IncludeOperationRecords)
            await InsertUsersAsync(host.Services, NewUser());
#else
            // 没有登录记录时活跃用户看最近登录时间：只有落在本区间里的才算
            var user = NewUser();
            user.RecordLoginSuccess(createdAt);
            await InsertUsersAsync(host.Services, user);
#endif
        }

        clock.SetUtcNow(new DateTimeOffset(now));

#if (IncludeOperationRecords)
        string userA = Guid.NewGuid().ToString(), userB = Guid.NewGuid().ToString(), userC = Guid.NewGuid().ToString();
        await InsertLoginRecordsAsync(host.Services,
            (OperationRecordActions.AuthLoginSucceeded, userC, previousStart.AddSeconds(-1)),
            (OperationRecordActions.AuthLoginSucceeded, userB, previousStart),
            (OperationRecordActions.AuthLoginSucceeded, userA, currentStart.AddSeconds(-1)),
            (OperationRecordActions.AuthLoginSucceeded, userA, currentStart),
            (OperationRecordActions.AuthLoginSucceeded, userC, today.AddDays(-3).AddHours(12)),
            (OperationRecordActions.AuthLoginSucceeded, userB, today.AddSeconds(-1)),
            (OperationRecordActions.AuthLoginSucceeded, userA, now),
            (OperationRecordActions.AuthLoginFailed, "someone", previousStart.AddSeconds(-1)),
            (OperationRecordActions.AuthLoginFailed, "someone", currentStart.AddSeconds(-1)),
            (OperationRecordActions.AuthLoginFailed, "someone", today.AddDays(-2).AddHours(8)),
            (OperationRecordActions.AuthLoginFailed, "someone", today.AddDays(-2).AddHours(9)));
#endif

        var week = await GetStatisticsAsync(host.Services, 7);
        Assert.Equal(new StatisticValueOutputDto { Value = existingUsers + 5, Previous = existingUsers + 3 }, week.TotalUsers);
        Assert.Equal(2, week.NewUsers.Value);
        Assert.Equal(2, week.NewUsers.Previous);
#if (IncludeOperationRecords)
        Assert.Equal(new StatisticValueOutputDto { Value = 3, Previous = 2 }, week.ActiveUsers);
        Assert.Equal(new StatisticValueOutputDto { Value = 2, Previous = 1 }, week.FailedLogins);
        Assert.Equal(
            Enumerable.Range(0, 7).Select(offset => DateOnly.FromDateTime(currentStart.AddDays(offset))),
            week.LoginTrend.Select(point => point.Date));
        Assert.Equal(new[] { 1, 0, 0, 1, 0, 1, 1 }, week.LoginTrend.Select(point => point.Succeeded));
        Assert.Equal(new[] { 0, 0, 0, 0, 2, 0, 0 }, week.LoginTrend.Select(point => point.Failed));

        // 30 天：上述数据全落在本区间，上一区间没有任何数据
        var month = await GetStatisticsAsync(host.Services, 30);
        Assert.Equal(30, month.LoginTrend.Count);
        Assert.Equal(new StatisticValueOutputDto { Value = 3, Previous = 0 }, month.ActiveUsers);
        Assert.Equal(new StatisticValueOutputDto { Value = 4, Previous = 0 }, month.FailedLogins);
#else
        Assert.Equal(new StatisticValueOutputDto { Value = 2 }, week.ActiveUsers);

        var month = await GetStatisticsAsync(host.Services, 30);
        Assert.Equal(new StatisticValueOutputDto { Value = 5 }, month.ActiveUsers);
#endif
        // 内置管理员建在宿主启动时，也落在 30 天的本区间里
        Assert.Equal(existingUsers + 5, month.NewUsers.Value);
        Assert.Equal(0, month.NewUsers.Previous);
    }

    /// <summary>
    /// 用户被删除后，在它曾存在过的时刻仍计入：本区间开始前的 10 人里本区间删了 3 人，总数的上一区间值仍是 10；
    /// 注册数按创建事件计，注册后又被删除的照样计入。
    /// </summary>
    /// <remarks>独立宿主与库：断言的是精确计数。删除时间取自同一个可拨的时钟。</remarks>
    [Fact]
    public async Task Deleted_users_still_count_where_they_existed_and_as_registrations()
    {
        var today = DateTime.UtcNow.Date;
        var currentStart = today.AddDays(-6);
        var previousStart = today.AddDays(-13);
        var clock = new FakeTimeProvider(new DateTimeOffset(previousStart.AddDays(-1)));
        using var isolated = new ProjectWebApplicationFactory();
        await using var host = isolated.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));

        var existingUsers = (await GetStatisticsAsync(host.Services, 7)).TotalUsers.Value;

        // 上一区间：注册 11 人，其中 1 人当场删掉——它不在本区间开始时的总数里，但算上一区间的注册
        clock.SetUtcNow(new DateTimeOffset(previousStart.AddHours(1)));
        var earlier = Enumerable.Range(0, 10).Select(_ => NewUser()).ToArray();
        var deletedEarlier = NewUser();
        await InsertUsersAsync(host.Services, [.. earlier, deletedEarlier]);
        clock.SetUtcNow(new DateTimeOffset(previousStart.AddHours(2)));
        await DeleteUsersAsync(host.Services, deletedEarlier.Id);

        // 本区间：删掉之前的 3 人；注册 2 人，其中 1 人随后删掉
        clock.SetUtcNow(new DateTimeOffset(currentStart.AddHours(1)));
        await DeleteUsersAsync(host.Services, earlier[0].Id, earlier[1].Id, earlier[2].Id);
        var registeredThenDeleted = NewUser();
        await InsertUsersAsync(host.Services, registeredThenDeleted, NewUser());
        clock.SetUtcNow(new DateTimeOffset(currentStart.AddHours(2)));
        await DeleteUsersAsync(host.Services, registeredThenDeleted.Id);

        clock.SetUtcNow(new DateTimeOffset(today.AddSeconds(30)));
        var week = await GetStatisticsAsync(host.Services, 7);

        Assert.Equal(new StatisticValueOutputDto { Value = existingUsers + 8, Previous = existingUsers + 10 }, week.TotalUsers);
        Assert.Equal(new StatisticValueOutputDto { Value = 2, Previous = 11 }, week.NewUsers);
    }
#if (IncludeOperationRecords)

    /// <summary>
    /// 保留期把上一区间的登录记录搬进归档表之后，活跃用户与登录失败的上一区间值不变。
    /// </summary>
    /// <remarks>
    /// 30 天区间配最短保留期（30 天）时，上一区间几乎整段都在归档表里；只数原表的话上一区间值会塌成 0。
    /// 独立宿主与库：归档按整库搬运，断言的是精确条数。
    /// </remarks>
    [Fact]
    public async Task The_previous_interval_still_counts_logins_moved_to_the_archive()
    {
        var today = DateTime.UtcNow.Date;
        var currentStart = today.AddDays(-29);
        var previousStart = today.AddDays(-59);
        using var host = new ProjectWebApplicationFactory();

        string userA = Guid.NewGuid().ToString(), userB = Guid.NewGuid().ToString();
        await InsertLoginRecordsAsync(host.Services,
            (OperationRecordActions.AuthLoginSucceeded, userA, previousStart.AddDays(1)),
            (OperationRecordActions.AuthLoginSucceeded, userB, previousStart.AddDays(10)),
            (OperationRecordActions.AuthLoginSucceeded, userA, previousStart.AddDays(20)),
            (OperationRecordActions.AuthLoginFailed, "someone", previousStart.AddDays(2)),
            (OperationRecordActions.AuthLoginFailed, "someone", previousStart.AddDays(3)),
            (OperationRecordActions.AuthLoginFailed, "someone", previousStart.AddDays(4)),
            (OperationRecordActions.AuthLoginSucceeded, userA, currentStart.AddDays(1)),
            (OperationRecordActions.AuthLoginFailed, "someone", currentStart.AddDays(2)));

        var expectedActiveUsers = new StatisticValueOutputDto { Value = 1, Previous = 2 };
        var expectedFailedLogins = new StatisticValueOutputDto { Value = 1, Previous = 3 };
        var beforeArchiving = await GetStatisticsAsync(host.Services, 30);
        Assert.Equal(expectedActiveUsers, beforeArchiving.ActiveUsers);
        Assert.Equal(expectedFailedLogins, beforeArchiving.FailedLogins);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IOperationRecordArchiveService>()
                .ArchiveOlderThanAsync(DateTime.UtcNow.AddDays(-30), batchSize: 100);
            // 上一区间的 6 条全部搬走，本区间的 2 条留在原表
            Assert.Equal(6, result.Archived);
        }

        var afterArchiving = await GetStatisticsAsync(host.Services, 30);
        Assert.Equal(expectedActiveUsers, afterArchiving.ActiveUsers);
        Assert.Equal(expectedFailedLogins, afterArchiving.FailedLogins);
        Assert.Equal(beforeArchiving.LoginTrend, afterArchiving.LoginTrend);
    }
#endif

    /// <summary>关注项统计当前状态：锁定看此刻是否仍在生效，管理员按内置管理员角色判定。</summary>
    [Fact]
    public async Task Attention_counts_current_lockouts_admins_without_two_factor_and_unconfirmed_emails()
    {
        var now = DateTime.UtcNow;
        var before = (await GetStatisticsAsync(Factory.Services, 7)).Attention;
        var adminRoleId = await GetAdminRoleIdAsync(Factory.Services);

        var administratorLocked = NewUser();
        administratorLocked.Lock();
        var lockExpired = NewUser(confirmed: true);
        lockExpired.Lock(now.AddMinutes(-1));
        var temporarilyLocked = NewUser(confirmed: true);
        temporarilyLocked.Lock(now.AddMinutes(10));
        var adminWithoutTwoFactor = NewUser(confirmed: true);
        adminWithoutTwoFactor.AssignRoles([adminRoleId]);
        var adminWithTwoFactor = NewUser(confirmed: true);
        adminWithTwoFactor.AssignRoles([adminRoleId]);
        adminWithTwoFactor.EnableTwoFactor("protected-secret", [], usedStep: 0);
        await InsertUsersAsync(Factory.Services,
            administratorLocked, lockExpired, temporarilyLocked, adminWithoutTwoFactor, adminWithTwoFactor);

        var after = (await GetStatisticsAsync(Factory.Services, 7)).Attention;
        Assert.Equal(before.LockedUsers + 2, after.LockedUsers);
        Assert.Equal(before.AdminsWithoutTwoFactor + 1, after.AdminsWithoutTwoFactor);
        Assert.Equal(before.UnconfirmedEmailUsers + 1, after.UnconfirmedEmailUsers);
    }
#if (IncludeMultiTenancy)

    /// <summary>
    /// 租户管理员只看到本租户：新租户里只有开通时建的管理员，看不到宿主的用户，也看不到租户的状态；
    /// 宿主看不到租户里的用户，停用的租户只在宿主侧计数。
    /// </summary>
    [Fact]
    public async Task Tenant_administrators_see_their_own_tenant_and_only_the_host_counts_disabled_tenants()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var hostBefore = await ReadAsync(superAdmin.Client);

        var created = await superAdmin.Client.PostAsJsonAsync("/api/v1/tenants", new
        {
            Name = "statsscope",
            DisplayName = "Stats scope",
            AdminEmail = "admin@statsscope.example.com",
            AdminPassword = "Tenant@123456"
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var tenantId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using (var tenantAdmin = Factory.CreateProjectClient())
        {
            tenantAdmin.DefaultRequestHeaders.Add(MultiTenancyOptions.DefaultHeaderName, tenantId.ToString());
            var login = await tenantAdmin.PostAsJsonAsync(
                "/api/v1/auth/session-login", new { UsernameOrEmail = "admin", Password = "Tenant@123456" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            tenantAdmin.DefaultRequestHeaders.Add("Cookie", string.Join("; ",
                login.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0])));

            // 开通时 Admin 角色带上了租户侧全部权限，仪表盘在其中
            var tenant = await ReadAsync(tenantAdmin);
            Assert.Equal(1, tenant.GetProperty("totalUsers").GetProperty("value").GetInt32());
            Assert.False(tenant.GetProperty("attention").TryGetProperty("disabledTenants", out _));
#if (IncludeOperationRecords)
            // 刚才那次登录记在租户层
            Assert.Equal(1, tenant.GetProperty("activeUsers").GetProperty("value").GetInt32());
#endif
        }

        var hostAfter = await ReadAsync(superAdmin.Client);
        Assert.Equal(
            hostBefore.GetProperty("totalUsers").GetProperty("value").GetInt32(),
            hostAfter.GetProperty("totalUsers").GetProperty("value").GetInt32());

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetActiveAsync(tenantId, false);
        }

        var hostDisabled = await ReadAsync(superAdmin.Client);
        Assert.Equal(
            hostAfter.GetProperty("attention").GetProperty("disabledTenants").GetInt32() + 1,
            hostDisabled.GetProperty("attention").GetProperty("disabledTenants").GetInt32());
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
#endif

    private static async Task<PlatformStatisticsOutputDto> GetStatisticsAsync(IServiceProvider services, int days)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPlatformStatisticsAppService>()
            .GetAsync(new GetPlatformStatisticsInputDto { Days = days });
    }

    private static async Task<Guid> GetAdminRoleIdAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MyProjectDbContext>().Roles
            .Where(role => role.Name == AdminConstant.RoleName)
            .Select(role => role.Id)
            .SingleAsync();
    }

    private static User NewUser(bool confirmed = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var user = new User($"stats_{suffix}", $"stats_{suffix}@example.test");
        if (confirmed)
        {
            user.ConfirmEmail();
        }

        return user;
    }

    private static async Task InsertUsersAsync(IServiceProvider services, params User[] users)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
    }

    // 经上下文删除：软删除与删除时间由保存拦截器按时钟落定，与业务删除同一条路径
    private static async Task DeleteUsersAsync(IServiceProvider services, params Guid[] userIds)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        db.Users.RemoveRange(await db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync());
        await db.SaveChangesAsync();
    }
#if (IncludeOperationRecords)

    // 登录记录直接写表：要的是落在指定时刻的记录，经登录接口写只能落在"此刻"
    private static async Task InsertLoginRecordsAsync(
        IServiceProvider services,
        params (string Action, string TargetId, DateTime Time)[] records)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyProjectDbContext>();
        db.Set<OperationRecord>().AddRange(records.Select(record => new OperationRecord
        {
            Action = record.Action,
            TargetId = record.TargetId,
            AuthorizationBasis = "CredentialsPresented",
            Outcome = record.Action == OperationRecordActions.AuthLoginSucceeded
                ? OperationRecordOutcome.Succeeded
                : OperationRecordOutcome.Failed,
            Visibility = OperationVisibility.Tenant,
            CreationTime = record.Time
        }));
        await db.SaveChangesAsync();
    }
#endif
}
#else
/// <summary>资源服务形态的平台统计：只有用户数；锁定、两步验证与登录归签发方，相应指标不出现。</summary>
public sealed class PlatformStatisticsTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Url = "/api/v1/platform/statistics";

    [Fact]
    public async Task Statistics_require_the_dashboard_permission_and_carry_only_user_counts()
    {
        var subjectId = Guid.CreateVersion7();
        var tenantId = ProjectWebApplicationFactory.NewTenantId();
        using var session = factory.CreateResourceSession(subjectId, tenantId);
        using (var me = await session.Client.GetAsync("/api/v1/auth/me"))
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.Client.GetAsync(Url)).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            await scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>()
                .GrantAsync(PermissionConstant.Dashboard.Default, PermissionGrantProviderNames.User, subjectId.ToString());
        }

        using var body = JsonDocument.Parse(await session.Client.GetStringAsync(Url));
        var root = body.RootElement;
        Assert.True(root.GetProperty("totalUsers").GetProperty("value").GetInt32() >= 1);
        Assert.True(root.TryGetProperty("newUsers", out _));
        Assert.False(root.TryGetProperty("activeUsers", out _));
        Assert.False(root.TryGetProperty("loginTrend", out _));
        Assert.Empty(root.GetProperty("attention").EnumerateObject());

        using var invalid = await session.Client.GetAsync($"{Url}?days=14");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
#endif
