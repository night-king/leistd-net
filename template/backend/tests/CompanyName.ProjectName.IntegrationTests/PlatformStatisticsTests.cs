using System.Globalization;
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
#if (LocalIdentity && (IncludeMultiTenancy || IncludeOperationRecords))
using System.Net.Http.Json;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
using Leistd.MultiTenancy.Stores;
#endif
#if (!LocalIdentity)
using Leistd.MultiTenancy.Context;
#endif
#if (LocalIdentity)
using Microsoft.AspNetCore.Mvc.Testing;
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
/// <summary>平台统计：权限、区间校验、按形态裁剪的字段、按请求时区切分的区间与桶，以及当前上下文的范围。</summary>
public sealed class PlatformStatisticsTests(ProjectWebApplicationFactory factory)
    : AuthorizationTestBase(factory), IClassFixture<ProjectWebApplicationFactory>
{
    private const string Url = "/api/v1/platform/statistics";

    /// <summary>没有仪表盘权限答 403；授予后可读，内置管理员角色在播种时已带上它。</summary>
    [Fact]
    public async Task Statistics_require_the_dashboard_permission_which_the_admin_role_is_seeded_with()
    {
        var url = TodayUrl();
        using var anonymous = Factory.CreateProjectClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);

        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var member = await CreateUserAsync(superAdmin.Client);
        using var memberSession = await Factory.LoginAsync(member.Username, TestPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberSession.Client.GetAsync(url)).StatusCode);

        await GrantAsync(PermissionGrantProviderNames.User, member.Id, PermissionConstant.Dashboard.Default);
        Assert.Equal(HttpStatusCode.OK, (await memberSession.Client.GetAsync(url)).StatusCode);

        var administrator = await CreateUserAsync(superAdmin.Client, [await GetAdminRoleIdAsync(Factory.Services)]);
        using var adminSession = await Factory.LoginAsync(administrator.Username, TestPassword);
        Assert.Equal(HttpStatusCode.OK, (await adminSession.Client.GetAsync(url)).StatusCode);
    }

    /// <summary>
    /// 区间参数缺失、日期写坏、时区不可用（含 .NET 认、浏览器不认的 Windows 时区 ID）、起止颠倒、跨度超过 366 天、
    /// 结束日晚于该时区的今天，都是对应字段的 400，与模型校验同一种响应形状。
    /// </summary>
    [Theory]
    [InlineData("", "startDate")]
    [InlineData("startDate=2026-01-01&timeZone=UTC", "endDate")]
    [InlineData("startDate=2026-01-01&endDate=2026-01-02", "timeZone")]
    [InlineData("startDate=2026-13-01&endDate=2026-01-02&timeZone=UTC", "startDate")]
    [InlineData("startDate=2026-01-01&endDate=2026-01-02&timeZone=Mars/Olympus", "timeZone")]
    [InlineData("startDate=2026-01-01&endDate=2026-01-02&timeZone=China%20Standard%20Time", "timeZone")]
    [InlineData("startDate=2026-01-02&endDate=2026-01-01&timeZone=UTC", "endDate")]
    [InlineData("startDate=2025-01-01&endDate=2026-01-02&timeZone=UTC", "endDate")]
    [InlineData("startDate=2026-01-01&endDate={future}&timeZone=UTC", "endDate")]
    [InlineData("startDate=0001-01-01&endDate=0001-01-01&timeZone=UTC", "startDate")]
    public async Task Invalid_ranges_are_field_errors(string query, string field)
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var response = await superAdmin.Client.GetAsync($"{Url}?{query.Replace("{future}", future)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("urn:leistd:problem:validation-error", problem.RootElement.GetProperty("type").GetString());
        Assert.Contains(problem.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == field);
    }

    /// <summary>最早的可用区间：上一区间从 0001-01-02 起，换算到 +14 时区的 UTC 仍可表示。</summary>
    [Fact]
    public async Task The_earliest_representable_range_is_accepted()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);

        using var response = await superAdmin.Client.GetAsync(
            $"{Url}?startDate=0001-01-03&endDate=0001-01-03&timeZone=Pacific/Kiritimati");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>366 天恰好是上限，照常作答。</summary>
    [Fact]
    public async Task A_range_of_exactly_366_days_is_accepted()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var response = await superAdmin.Client.GetAsync(RangeUrl(today.AddDays(-365), today, "UTC"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>字段按形态裁剪：不存在的指标不出现在响应里，而不是给 0；各序列与桶等长，回显区间与时区。</summary>
    [Fact]
    public async Task Response_carries_the_fields_this_service_can_answer()
    {
        using var superAdmin = await Factory.LoginAsync("admin", ProjectWebApplicationFactory.TestAdminPassword);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var body = JsonDocument.Parse(await superAdmin.Client.GetStringAsync(RangeUrl(today.AddDays(-6), today, "UTC")));
        var root = body.RootElement;

        var range = root.GetProperty("range");
        Assert.Equal("UTC", range.GetProperty("timeZone").GetString());
        Assert.Equal("day", range.GetProperty("granularity").GetString());
        var buckets = root.GetProperty("buckets").GetArrayLength();
        Assert.Equal(7, buckets);
        Assert.True(root.GetProperty("totalUsers").GetProperty("value").GetInt32() >= 1);
        Assert.Equal(buckets, root.GetProperty("totalUsers").GetProperty("series").GetArrayLength());
        Assert.Equal(buckets, root.GetProperty("newUsers").GetProperty("series").GetArrayLength());
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
        Assert.Equal(buckets, root.GetProperty("activeUsers").GetProperty("series").GetArrayLength());
        Assert.Equal(buckets, root.GetProperty("succeededLogins").GetProperty("series").GetArrayLength());
        Assert.Equal(buckets, root.GetProperty("failedLoginAlerts").GetProperty("series").GetArrayLength());
#else
        // 没有登录记录：活跃用户退回最近登录时间，只有本期值；登录次数不存在
        var activeUsers = root.GetProperty("activeUsers");
        Assert.True(activeUsers.TryGetProperty("value", out _));
        // 宿主的 JSON 设置省略 null：没有上期值与序列就是没有这两个字段
        Assert.False(activeUsers.TryGetProperty("previous", out _));
        Assert.False(activeUsers.TryGetProperty("series", out _));
        Assert.False(root.TryGetProperty("succeededLogins", out _));
        Assert.False(root.TryGetProperty("failedLoginAlerts", out _));
#endif
    }

    /// <summary>
    /// 区间含今天时截到此刻，上一区间前移 7 天、比较相同的本地进度；边界上的记录只归后一个区间或桶。
    /// 用户总数是期末存量，按桶序列由起点存量逐桶推出；注册数与登录次数按事件计。
    /// </summary>
    /// <remarks>独立宿主与库：断言的是精确计数，同类其他用例写入的用户与登录不能混进来。</remarks>
    [Fact]
    public async Task Counts_follow_the_calendar_range_and_compare_the_same_local_progress()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Utc(2026, 5, 1)));
        using var isolated = new ProjectWebApplicationFactory();
        await using var host = WithClock(isolated, clock);
        // 宿主在首次取 Services 时启动，内置管理员建在那一刻：先启动、再拨钟读基数，
        // 否则它恰好落在零长度区间的终点上、不计入
        _ = host.Services;
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 5, 1, 1)));
        var existingUsers = (await GetStatisticsAsync(host.Services, D(2026, 5, 1), D(2026, 5, 1), "UTC")).TotalUsers.Value;

        // 本区间 06-09 至 06-15，此刻 06-15 12:00；上一区间 [06-02 00:00, 06-08 12:00)
        var previousEnd = Utc(2026, 6, 8, 12);
        var createdAt = new[] { Utc(2026, 6, 2), previousEnd.AddMinutes(-1), previousEnd, Utc(2026, 6, 9), Utc(2026, 6, 15, 11) };
        foreach (var time in createdAt)
        {
            clock.SetUtcNow(new DateTimeOffset(time));
#if (IncludeOperationRecords)
            await InsertUsersAsync(host.Services, NewUser());
#else
            // 没有登录记录时活跃用户看最近登录时间：只有落在本区间里的才算
            var user = NewUser();
            user.RecordLoginSuccess(time);
            await InsertUsersAsync(host.Services, user);
#endif
        }

        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 6, 15, 12)));
#if (IncludeOperationRecords)
        string userA = Guid.NewGuid().ToString(), userB = Guid.NewGuid().ToString(), userC = Guid.NewGuid().ToString();
        await InsertLoginRecordsAsync(host.Services,
            (OperationRecordActions.AuthLoginSucceeded, userA, Utc(2026, 6, 2)),
            (OperationRecordActions.AuthLoginSucceeded, userB, previousEnd.AddMinutes(-1)),
            (OperationRecordActions.AuthLoginSucceeded, userC, previousEnd),
            (OperationRecordActions.AuthLoginSucceeded, userA, Utc(2026, 6, 9)),
            (OperationRecordActions.AuthLoginSucceeded, userA, Utc(2026, 6, 12, 8)),
            (OperationRecordActions.AuthLoginSucceeded, userB, Utc(2026, 6, 15, 11, 59)),
            (OperationRecordActions.AuthLoginSucceeded, userC, Utc(2026, 6, 15, 12)),
            (OperationRecordActions.AuthLoginFailed, "someone", Utc(2026, 6, 8, 11)),
            (OperationRecordActions.AuthLoginFailed, "someone", Utc(2026, 6, 10, 1)),
            (OperationRecordActions.AuthLoginFailed, "someone", Utc(2026, 6, 10, 2)));
#endif

        var week = await GetStatisticsAsync(host.Services, D(2026, 6, 9), D(2026, 6, 15), "UTC");

        Assert.Equal(StatisticsGranularity.Day, week.Range.Granularity);
        Assert.Equal(Utc(2026, 6, 9), week.Range.Start);
        Assert.Equal(Utc(2026, 6, 15, 12), week.Range.End);
        Assert.Equal(Utc(2026, 6, 2), week.Range.PreviousStart);
        Assert.Equal(previousEnd, week.Range.PreviousEnd);
        Assert.Equal(Enumerable.Range(0, 7).Select(day => Utc(2026, 6, 9).AddDays(day)), week.Buckets);

        Assert.Equal(existingUsers + 5, week.TotalUsers.Value);
        Assert.Equal(existingUsers + 2, week.TotalUsers.Previous);
        Assert.Equal([.. Enumerable.Repeat(existingUsers + 4, 6), existingUsers + 5], week.TotalUsers.Series);
        Assert.Equal(new StatisticValueOutputDto { Value = 2, Previous = 2, Series = week.NewUsers.Series }, week.NewUsers);
        Assert.Equal([1, 0, 0, 0, 0, 0, 1], week.NewUsers.Series);
#if (IncludeOperationRecords)
        Assert.Equal((3, 2), (week.SucceededLogins.Value, week.SucceededLogins.Previous));
        Assert.Equal([1, 0, 0, 1, 0, 0, 1], week.SucceededLogins.Series);
        Assert.Equal((2, 2), (week.ActiveUsers.Value, week.ActiveUsers.Previous));
        Assert.Equal([1, 0, 0, 1, 0, 0, 1], week.ActiveUsers.Series);
        Assert.Equal((2, 1), (week.FailedLoginAlerts.Value, week.FailedLoginAlerts.Previous));
        Assert.Equal([0, 2, 0, 0, 0, 0, 0], week.FailedLoginAlerts.Series);
#else
        Assert.Equal(2, week.ActiveUsers.Value);
        Assert.Null(week.ActiveUsers.Previous);
        Assert.Null(week.ActiveUsers.Series);

        // 历史区间只看最近一次登录：06-02 登录过、之后又在 06-08 登录的人，在 06-01 至 06-03 里数不到
        var history = await GetStatisticsAsync(host.Services, D(2026, 6, 1), D(2026, 6, 3), "UTC");
        Assert.Equal(1, history.ActiveUsers.Value);
#endif

        // 已经结束的历史区间：终点是结束日次日零点，上一区间紧邻其前、终点就是本区间起点
        var past = await GetStatisticsAsync(host.Services, D(2026, 6, 1), D(2026, 6, 7), "UTC");
        Assert.Equal(Utc(2026, 6, 8), past.Range.End);
        Assert.Equal(Utc(2026, 5, 25), past.Range.PreviousStart);
        Assert.Equal(past.Range.Start, past.Range.PreviousEnd);
        Assert.Equal(new StatisticValueOutputDto { Value = 1, Previous = 0, Series = past.NewUsers.Series }, past.NewUsers);
    }

    /// <summary>
    /// 桶按请求的时区切：纽约回拨日 25 个小时桶（两个本地 01 点各成一桶）、春令日 23 个；上一区间终点落在跳时缺口里时
    /// 取缺口后的第一个时刻、落在重复钟点时取较早那次（Auckland 这类 UTC+13 时区同样成立）；Lord Howe 半小时夏令时当天末桶只有半小时；Kolkata 的日桶起点在 UTC 的 :30；
    /// 本地零点上的记录归新的一天；零点请求今天是零长度区间，没有桶。
    /// </summary>
    [Fact]
    public async Task Buckets_follow_local_days_and_hours_of_the_requested_time_zone()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Utc(2025, 10, 1)));
        using var isolated = new ProjectWebApplicationFactory();
        await using var host = WithClock(isolated, clock);

        clock.SetUtcNow(new DateTimeOffset(Utc(2025, 11, 3, 12)));
        var fallBack = await GetStatisticsAsync(host.Services, D(2025, 11, 2), D(2025, 11, 2), "America/New_York");
        Assert.Equal(StatisticsGranularity.Hour, fallBack.Range.Granularity);
        Assert.Equal(25, fallBack.Buckets.Count);
        Assert.Equal(Utc(2025, 11, 2, 4), fallBack.Range.Start);
        Assert.Equal(Utc(2025, 11, 3, 5), fallBack.Range.End);
        Assert.Contains(Utc(2025, 11, 2, 5), fallBack.Buckets);
        Assert.Contains(Utc(2025, 11, 2, 6), fallBack.Buckets);

        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 3, 9, 12)));
        var springForward = await GetStatisticsAsync(host.Services, D(2026, 3, 8), D(2026, 3, 8), "America/New_York");
        Assert.Equal(23, springForward.Buckets.Count);
        Assert.Equal(Utc(2026, 3, 8, 5), springForward.Range.Start);
        Assert.Equal(Utc(2026, 3, 9, 4), springForward.Range.End);

        // 此刻是本地 03-15 02:30；前移 7 天的 03-08 02:30 不存在，取跳时后的 03:00（UTC 07:00）
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 3, 15, 6, 30)));
        var acrossTheGap = await GetStatisticsAsync(host.Services, D(2026, 3, 9), D(2026, 3, 15), "America/New_York");
        Assert.Equal(Utc(2026, 3, 15, 6, 30), acrossTheGap.Range.End);
        Assert.Equal(Utc(2026, 3, 2, 5), acrossTheGap.Range.PreviousStart);
        Assert.Equal(Utc(2026, 3, 8, 7), acrossTheGap.Range.PreviousEnd);

        // Auckland 04-05 03:00 回拨到 02:00（+13 → +12）：此刻本地 04-06 02:30，前一天的 02:30 出现两次，取较早的 +13 那次
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 4, 5, 14, 30)));
        var aucklandFallBack = await GetStatisticsAsync(host.Services, D(2026, 4, 6), D(2026, 4, 6), "Pacific/Auckland");
        Assert.Equal(Utc(2026, 4, 4, 13, 30), aucklandFallBack.Range.PreviousEnd);

        // Auckland 09-27 02:00 跳到 03:00（+12 → +13）：此刻本地 09-28 02:15，前一天的 02:15 不存在，取跳时后的 03:00
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 9, 27, 13, 15)));
        var aucklandGap = await GetStatisticsAsync(host.Services, D(2026, 9, 28), D(2026, 9, 28), "Pacific/Auckland");
        Assert.Equal(Utc(2026, 9, 27, 11), aucklandGap.Range.Start);
        Assert.Equal(Utc(2026, 9, 26, 12), aucklandGap.Range.PreviousStart);
        Assert.Equal(Utc(2026, 9, 26, 14), aucklandGap.Range.PreviousEnd);

        // Lord Howe 10-04 02:00 起 +10:30 → +11:00，当天 23.5 小时：24 个桶，末桶半小时
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 10, 5)));
        var halfHourShift = await GetStatisticsAsync(host.Services, D(2026, 10, 4), D(2026, 10, 4), "Australia/Lord_Howe");
        Assert.Equal(24, halfHourShift.Buckets.Count);
        Assert.Equal(Utc(2026, 10, 3, 13, 30), halfHourShift.Range.Start);
        Assert.Equal(Utc(2026, 10, 4, 13), halfHourShift.Range.End);
        Assert.Equal(Utc(2026, 10, 4, 12, 30), halfHourShift.Buckets[^1]);

        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 10, 6)));
#if (IncludeOperationRecords)
        await InsertLoginRecordsAsync(host.Services,
            (OperationRecordActions.AuthLoginSucceeded, Guid.NewGuid().ToString(), Utc(2026, 10, 4, 18, 30)));
#endif
        var kolkata = await GetStatisticsAsync(host.Services, D(2026, 10, 4), D(2026, 10, 5), "Asia/Kolkata");
        Assert.Equal([Utc(2026, 10, 3, 18, 30), Utc(2026, 10, 4, 18, 30)], kolkata.Buckets);
#if (IncludeOperationRecords)
        // 本地 10-05 00:00 上的那次登录归 10-05
        Assert.Equal([0, 1], kolkata.SucceededLogins.Series);
#endif

        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 10, 7)));
        var midnight = await GetStatisticsAsync(host.Services, D(2026, 10, 7), D(2026, 10, 7), "UTC");
        Assert.Empty(midnight.Buckets);
        Assert.Equal(midnight.Range.Start, midnight.Range.End);
        Assert.Empty(midnight.NewUsers.Series!);
        Assert.Equal(0, midnight.NewUsers.Value);
        Assert.True(midnight.TotalUsers.Value >= 1);
    }

    /// <summary>
    /// 用户被删除后，在它曾存在过的时刻仍计入：上一区间终点前的 10 人里本区间删了 3 人，总数的上期值仍是 10；
    /// 注册数按创建事件计，注册后又被删除的照样计入；之后的注册与删除不改变历史区间的期末存量。
    /// </summary>
    /// <remarks>独立宿主与库：断言的是精确计数。删除时间取自同一个可拨的时钟。</remarks>
    [Fact]
    public async Task Deleted_users_still_count_where_they_existed_and_as_registrations()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Utc(2026, 5, 1)));
        using var isolated = new ProjectWebApplicationFactory();
        await using var host = WithClock(isolated, clock);
        // 宿主在首次取 Services 时启动，内置管理员建在那一刻：先启动、再拨钟读基数，
        // 否则它恰好落在零长度区间的终点上、不计入
        _ = host.Services;
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 5, 1, 1)));
        var existingUsers = (await GetStatisticsAsync(host.Services, D(2026, 5, 1), D(2026, 5, 1), "UTC")).TotalUsers.Value;

        // 上一区间（06-02 至 06-08）：注册 11 人，其中 1 人当场删掉
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 6, 2, 1)));
        var earlier = Enumerable.Range(0, 10).Select(_ => NewUser()).ToArray();
        var deletedEarlier = NewUser();
        await InsertUsersAsync(host.Services, [.. earlier, deletedEarlier]);
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 6, 2, 2)));
        await DeleteUsersAsync(host.Services, deletedEarlier.Id);

        // 本区间（06-09 至 06-15）：删掉之前的 3 人；注册 2 人，其中 1 人随后删掉
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 6, 9, 1)));
        await DeleteUsersAsync(host.Services, earlier[0].Id, earlier[1].Id, earlier[2].Id);
        var registeredThenDeleted = NewUser();
        await InsertUsersAsync(host.Services, registeredThenDeleted, NewUser());
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 6, 9, 2)));
        await DeleteUsersAsync(host.Services, registeredThenDeleted.Id);

        // 历史区间之后：再注册 1 人、删 1 人，不该影响 06-09 至 06-15 的期末存量
        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 6, 20)));
        await InsertUsersAsync(host.Services, NewUser());
        await DeleteUsersAsync(host.Services, earlier[3].Id);

        clock.SetUtcNow(new DateTimeOffset(Utc(2026, 6, 21)));
        var week = await GetStatisticsAsync(host.Services, D(2026, 6, 9), D(2026, 6, 15), "UTC");

        Assert.Equal(existingUsers + 8, week.TotalUsers.Value);
        Assert.Equal(existingUsers + 10, week.TotalUsers.Previous);
        Assert.Equal(Enumerable.Repeat(existingUsers + 8, 7), week.TotalUsers.Series);
        Assert.Equal((2, 11), (week.NewUsers.Value, week.NewUsers.Previous));
    }
#if (IncludeOperationRecords)

    /// <summary>保留期把上一区间的登录记录搬进归档表之后，登录次数、活跃用户与序列都不变。</summary>
    /// <remarks>
    /// 30 天区间配最短保留期（30 天）时，上一区间几乎整段都在归档表里；只数原表的话上一区间值会塌成 0。
    /// 独立宿主与库：归档按整库搬运，断言的是精确条数。
    /// </remarks>
    [Fact]
    public async Task The_previous_interval_still_counts_logins_moved_to_the_archive()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentStart = today.AddDays(-29);
        var previousStart = currentStart.AddDays(-30).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var current = currentStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        using var host = new ProjectWebApplicationFactory();

        string userA = Guid.NewGuid().ToString(), userB = Guid.NewGuid().ToString();
        await InsertLoginRecordsAsync(host.Services,
            (OperationRecordActions.AuthLoginSucceeded, userA, previousStart.AddDays(1)),
            (OperationRecordActions.AuthLoginSucceeded, userB, previousStart.AddDays(10)),
            (OperationRecordActions.AuthLoginSucceeded, userA, previousStart.AddDays(20)),
            (OperationRecordActions.AuthLoginFailed, "someone", previousStart.AddDays(2)),
            (OperationRecordActions.AuthLoginFailed, "someone", previousStart.AddDays(3)),
            (OperationRecordActions.AuthLoginFailed, "someone", previousStart.AddDays(4)),
            (OperationRecordActions.AuthLoginSucceeded, userA, current.AddDays(1)),
            (OperationRecordActions.AuthLoginFailed, "someone", current.AddDays(2)));

        var beforeArchiving = await GetStatisticsAsync(host.Services, currentStart, today, "UTC");
        Assert.Equal((1, 2), (beforeArchiving.ActiveUsers.Value, beforeArchiving.ActiveUsers.Previous));
        Assert.Equal((1, 3), (beforeArchiving.FailedLoginAlerts.Value, beforeArchiving.FailedLoginAlerts.Previous));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IOperationRecordArchiveService>()
                .ArchiveOlderThanAsync(DateTime.UtcNow.AddDays(-30), batchSize: 100);
            // 上一区间的 6 条全部搬走，本区间的 2 条留在原表
            Assert.Equal(6, result.Archived);
        }

        var afterArchiving = await GetStatisticsAsync(host.Services, currentStart, today, "UTC");
        Assert.Equal(beforeArchiving.ActiveUsers.Previous, afterArchiving.ActiveUsers.Previous);
        Assert.Equal(beforeArchiving.FailedLoginAlerts.Previous, afterArchiving.FailedLoginAlerts.Previous);
        Assert.Equal(beforeArchiving.SucceededLogins.Previous, afterArchiving.SucceededLogins.Previous);
        Assert.Equal(beforeArchiving.SucceededLogins.Series, afterArchiving.SucceededLogins.Series);
        Assert.Equal(beforeArchiving.FailedLoginAlerts.Series, afterArchiving.FailedLoginAlerts.Series);
    }

    /// <summary>失败登录告警数的是节流后的记录，不是失败次数：同一标识连续失败 6 次只在第 1、5 次各记一条。</summary>
    /// <remarks>独立宿主与库：断言的是精确条数。用不存在的登录名，避免账号锁定改写第 5 次的结果。</remarks>
    [Fact]
    public async Task Failed_sign_in_alerts_count_throttled_records_rather_than_attempts()
    {
        using var host = new ProjectWebApplicationFactory();
        using var anonymous = host.CreateProjectClient();
        var identifier = "nobody_" + Guid.NewGuid().ToString("N")[..10];
        for (var i = 0; i < 6; i++)
        {
            using var failed = await anonymous.PostAsJsonAsync(
                "/api/v1/auth/session-login", new { UsernameOrEmail = identifier, Password = "Wrong!Passw0rd" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var statistics = await GetStatisticsAsync(host.Services, today, today, "UTC");

        Assert.Equal(2, statistics.FailedLoginAlerts.Value);
    }
#endif

    /// <summary>关注项统计当前状态：锁定看此刻是否仍在生效，管理员按内置管理员角色判定。</summary>
    [Fact]
    public async Task Attention_counts_current_lockouts_admins_without_two_factor_and_unconfirmed_emails()
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var before = (await GetStatisticsAsync(Factory.Services, today, today, "UTC")).Attention;
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

        var after = (await GetStatisticsAsync(Factory.Services, today, today, "UTC")).Attention;
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
        using var response = await client.GetAsync(TodayUrl());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
#endif

    // 今天（UTC）单日区间：只关心能不能读、读到什么形状的用例用它
    private static string TodayUrl()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return RangeUrl(today, today, "UTC");
    }

    private static string RangeUrl(DateOnly startDate, DateOnly endDate, string timeZone)
        => string.Create(CultureInfo.InvariantCulture,
            $"{Url}?startDate={startDate:yyyy-MM-dd}&endDate={endDate:yyyy-MM-dd}&timeZone={Uri.EscapeDataString(timeZone)}");

    private static async Task<PlatformStatisticsOutputDto> GetStatisticsAsync(
        IServiceProvider services,
        DateOnly startDate,
        DateOnly endDate,
        string timeZone)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPlatformStatisticsAppService>()
            .GetAsync(new GetPlatformStatisticsInputDto { StartDate = startDate, EndDate = endDate, TimeZone = timeZone });
    }

    // 时钟只能往前拨：宿主在拨钟之前启动（内置管理员建在此刻）
    private static WebApplicationFactory<Program> WithClock(ProjectWebApplicationFactory isolated, FakeTimeProvider clock)
        => isolated.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

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
        Assert.Equal(HttpStatusCode.Forbidden, (await session.Client.GetAsync($"{Url}?startDate=2026-01-01&endDate=2026-01-01&timeZone=UTC")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        using (scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            await scope.ServiceProvider.GetRequiredService<IPermissionGrantManager>()
                .GrantAsync(PermissionConstant.Dashboard.Default, PermissionGrantProviderNames.User, subjectId.ToString());
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var body = JsonDocument.Parse(
            await session.Client.GetStringAsync($"{Url}?startDate={today}&endDate={today}&timeZone=UTC"));
        var root = body.RootElement;
        Assert.True(root.GetProperty("totalUsers").GetProperty("value").GetInt32() >= 1);
        Assert.True(root.TryGetProperty("newUsers", out _));
        Assert.False(root.TryGetProperty("activeUsers", out _));
        Assert.False(root.TryGetProperty("succeededLogins", out _));
        Assert.Equal(root.GetProperty("buckets").GetArrayLength(), root.GetProperty("totalUsers").GetProperty("series").GetArrayLength());
        Assert.Empty(root.GetProperty("attention").EnumerateObject());

        using var invalid = await session.Client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
#endif
